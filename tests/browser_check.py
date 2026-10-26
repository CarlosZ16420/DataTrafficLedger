"""Exercise shipped HTML against --serve-fixture in isolated Chrome; no third-party dependencies."""
import base64
import hashlib
import json
import os
from pathlib import Path
import socket
import struct
import subprocess
import sys
import time
import urllib.request

ROOT = Path(__file__).resolve().parent.parent
DATA = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / 'tests' / 'live'
state = json.loads((DATA / 'runtime.json').read_text(encoding='utf-8-sig'))
profile = ROOT / 'tests' / ('chrome-' + str(os.getpid()))
profile.mkdir(parents=True, exist_ok=True)
chrome = Path(os.environ['ProgramFiles']) / 'Google' / 'Chrome' / 'Application' / 'chrome.exe'
process = subprocess.Popen([str(chrome), '--headless=new', '--no-sandbox', '--disable-gpu', '--disable-background-networking', '--no-first-run',
                            '--no-default-browser-check', '--remote-debugging-port=0', '--remote-debugging-address=127.0.0.1',
                            '--user-data-dir=' + str(profile), 'about:blank'],
                           creationflags=subprocess.CREATE_NO_WINDOW, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


class CDP:
    def __init__(self, url):
        from urllib.parse import urlsplit
        parts = urlsplit(url)
        self.socket = socket.create_connection((parts.hostname, parts.port), timeout=8)
        key = base64.b64encode(os.urandom(16)).decode()
        request = f'GET {parts.path} HTTP/1.1\r\nHost: {parts.hostname}:{parts.port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n'
        self.socket.sendall(request.encode())
        header = b''
        while not header.endswith(b'\r\n\r\n'):
            header += self.socket.recv(1)
        assert b' 101 ' in header, header
        expected = base64.b64encode(hashlib.sha1((key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode()).digest())
        assert expected in header
        self.next_id = 0
        self.errors = []

    def read(self, n):
        data = b''
        while len(data) < n:
            part = self.socket.recv(n - len(data))
            if not part:
                raise EOFError('Chrome closed the connection')
            data += part
        return data

    def send(self, opcode, payload):
        mask = os.urandom(4)
        length = len(payload)
        prefix = bytes([0x80 | opcode])
        if length < 126:
            prefix += bytes([0x80 | length])
        elif length < 65536:
            prefix += bytes([0xfe]) + struct.pack('!H', length)
        else:
            prefix += bytes([0xff]) + struct.pack('!Q', length)
        self.socket.sendall(prefix + mask + bytes(v ^ mask[i % 4] for i, v in enumerate(payload)))

    def call(self, method, params=None):
        self.next_id += 1
        target = self.next_id
        self.send(1, json.dumps({'id': target, 'method': method, 'params': params or {}}).encode())
        chunks = b''
        while True:
            a, b = self.read(2)
            size = b & 127
            if size == 126:
                size = struct.unpack('!H', self.read(2))[0]
            elif size == 127:
                size = struct.unpack('!Q', self.read(8))[0]
            mask = self.read(4) if b & 128 else None
            payload = self.read(size)
            if mask:
                payload = bytes(v ^ mask[i % 4] for i, v in enumerate(payload))
            if a & 15 == 9:
                self.send(10, payload)
                continue
            if a & 15 == 8:
                raise EOFError('WebSocket closed')
            chunks += payload
            if not a & 128:
                continue
            message = json.loads(chunks)
            chunks = b''
            if message.get('method') == 'Runtime.exceptionThrown':
                self.errors.append(message['params']['exceptionDetails'].get('text'))
            if message.get('method') == 'Log.entryAdded' and message['params']['entry']['level'] == 'error':
                self.errors.append(message['params']['entry']['text'])
            if message.get('id') == target:
                if 'error' in message:
                    raise RuntimeError(message['error'])
                return message.get('result', {})

    def evaluate(self, expression):
        response = self.call('Runtime.evaluate', {'expression': expression, 'awaitPromise': True, 'returnByValue': True})
        if 'exceptionDetails' in response:
            raise RuntimeError(response['exceptionDetails'])
        return response['result'].get('value')


try:
    deadline = time.time() + 20
    while True:
        try:
            debug_lines = (profile / 'DevToolsActivePort').read_text().splitlines()
            if len(debug_lines) >= 2:
                port = int(debug_lines[0])
                break
        except (OSError, ValueError):
            pass
        if time.time() > deadline:
            raise TimeoutError('Isolated Chrome did not start')
        time.sleep(.2)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    pages = json.loads(opener.open(f'http://127.0.0.1:{port}/json/list', timeout=5).read())
    cdp = CDP(next(p['webSocketDebuggerUrl'] for p in pages if p['type'] == 'page'))
    cdp.call('Runtime.enable')
    cdp.call('Log.enable')
    cdp.call('Page.enable')
    cdp.call('Emulation.setFocusEmulationEnabled', {'enabled': True})
    cdp.call('Emulation.setTimezoneOverride', {'timezoneId': 'America/Los_Angeles'})
    cdp.call('Emulation.setDeviceMetricsOverride', {'width': 1440, 'height': 1080, 'deviceScaleFactor': 1, 'mobile': False})
    if '--paging' in sys.argv:
        cdp.call('Emulation.setCPUThrottlingRate', {'rate': 4})
        cdp.call('Page.addScriptToEvaluateOnNewDocument', {'source': "window.ledgerLongTasks=[];new PerformanceObserver(list=>ledgerLongTasks.push(...list.getEntries().map(e=>({duration:e.duration,start:e.startTime,action:window.checkAction||'initial',rows:document.querySelectorAll('.traffic-row').length})))).observe({type:'longtask',buffered:true})"})
    cdp.call('Page.navigate', {'url': f"http://127.0.0.1:{state['port']}/open/{state['token']}"})
    def settled():
        for _ in range(600):
            if cdp.evaluate("typeof state!=='undefined'&&state.data&&!state.loading&&!state.renderPending&&activePages===0&&!pageQueue.length&&state.pages.has('[]')&&[...state.pages.values()].every(p=>!p.loading)"):
                return
            time.sleep(.05)
        raise TimeoutError('Statistics or page loading did not finish')

    def act(expression):
        cdp.evaluate('window.checkAction='+json.dumps(expression))
        result = cdp.evaluate(expression)
        settled()
        return result

    for _ in range(40):
        if cdp.evaluate("Boolean(typeof state !== 'undefined' && state.data && state.status)"):
            break
        time.sleep(.2)
    assert cdp.evaluate("Boolean(state.data && state.status)"), 'Page failed to load data'
    settled()
    assert cdp.evaluate("document.title") == '流量账本'
    assert cdp.evaluate("document.querySelectorAll('.traffic-row').length") > 0, 'No live records shown'
    if '--paging' in sys.argv:
        assert cdp.evaluate('state.data.summary.groups>100000'), 'Large fixture must exceed the old limit'
        assert cdp.evaluate("!('rows' in state.data)&&!('applicationRoutes' in state.data)"), 'Summary must not transfer all dimensions'
        keys, up, down = set(), 0, 0
        while True:
            page = cdp.evaluate("state.pages.get('[]')")
            assert len(page['rows']) <= 30
            for row in page['rows']:
                assert row['key'] not in keys, 'Root pagination repeated a group'
                keys.add(row['key'])
                up += row['upload']
                down += row['download']
            if not page['hasMore']:
                break
            act("document.getElementById('show-more').click()")
        assert (up, down) == tuple(cdp.evaluate('[state.data.summary.upload,state.data.summary.download]')), 'All root pages must reconcile exactly to full totals'
        while cdp.evaluate("state.pages.get('[]').offset>0"):
            act("document.getElementById('previous-page').click()")
        act("[...document.querySelectorAll('[data-expand]')].find(r=>r.querySelector('.row-name').textContent.startsWith('extra-')).click()")
        branch_key = cdp.evaluate("[...state.expanded][0]")
        first_children = cdp.evaluate('state.pages.get('+json.dumps(branch_key)+').rows.map(r=>r.key)')
        assert len(first_children) == 30
        assert cdp.evaluate('state.pages.get('+json.dumps(branch_key)+').total>1500')
        act("document.querySelector('.branch-pagination button:last-child').click()")
        assert cdp.evaluate('state.pages.get('+json.dumps(branch_key)+').offset') == 30
        assert cdp.evaluate('state.pages.get('+json.dumps(branch_key)+').rows[0].key') not in first_children
        act("document.querySelector('.branch-pagination button').click()")
        assert cdp.evaluate('state.pages.get('+json.dumps(branch_key)+').rows.map(r=>r.key)') == first_children
        act("document.querySelector('.traffic-row.child[data-expand]').click()")
        assert cdp.evaluate("document.querySelectorAll('.traffic-row.grandchild').length") > 0
        act("document.querySelector('[data-view=host]').click()")
        assert cdp.evaluate("state.pages.get('[]').total>100000&&state.pages.get('[]').rows.length===30")
        # Delayed responses exercise visible progress, stale-result protection and recovery.
        cdp.evaluate("window.savedFetch=window.fetch;window.fetch=async(...args)=>{const response=await savedFetch(...args);if(String(args[0]).startsWith('/api/groups'))await new Promise(r=>setTimeout(r,1500));return response};document.querySelector('[data-view=app]').click()")
        for _ in range(10):
            if cdp.evaluate("!document.getElementById('loading-status').hidden"):
                break
            time.sleep(.1)
        assert cdp.evaluate("!document.getElementById('loading-status').hidden&&document.querySelector('[role=progressbar]')!==null"), cdp.evaluate("({hidden:document.getElementById('loading-status').hidden,activePages,pages:[...state.pages.values()],loading:state.loading})")
        cdp.evaluate("document.querySelector('[data-view=host]').click();document.querySelector('[data-view=node]').click()")
        settled()
        assert cdp.evaluate("state.view==='node'&&state.pages.get('[]').rows.every(r=>r.icon==='node')"), 'Superseded group responses must never overwrite the current view'
        cdp.evaluate("window.fetch=savedFetch;window.failOnce=true;window.fetch=(...args)=>{if(failOnce&&String(args[0]).startsWith('/api/groups')){failOnce=false;return Promise.reject(new Error('模拟读取失败'))}return savedFetch(...args)}")
        act("document.querySelector('[data-view=app]').click()")
        assert cdp.evaluate("document.getElementById('traffic-table').textContent.includes('模拟读取失败')"), 'Page errors should offer an inline retry'
        act("document.querySelector('#traffic-table [data-page-path]').click()")
        cdp.evaluate("window.fetch=savedFetch")
        # Repeated expansion keeps the rendered row count bounded.
        for index in range(10):
            act("[...document.querySelectorAll('.traffic-row:not(.child):not(.grandchild)[data-expand]')][%d].click()" % index)
        assert cdp.evaluate("state.expanded.size<=8&&document.querySelectorAll('.traffic-row').length<=270"), 'Many expansions must not create an unbounded DOM'
        metrics = cdp.evaluate("(()=>{const boot=performance.getEntriesByType('navigation')[0].domContentLoadedEventEnd;return {maxDataTaskMs:Math.max(0,...ledgerLongTasks.filter(t=>t.start>=boot).map(t=>t.duration)),coldStartTaskMs:Math.max(0,...ledgerLongTasks.filter(t=>t.start<boot).map(t=>t.duration)),longTasks:ledgerLongTasks.length,renderedRows:document.querySelectorAll('.traffic-row').length,summaryMs:state.data.elapsedMs}})()")
        assert metrics['maxDataTaskMs'] < 300, metrics
        print('PASS: 100,000+ groups, exact totals across all root pages, nested pages, lazy leaves, delayed loading, stale results, retry and bounded rendering at 4x CPU slowdown. '+json.dumps(metrics))
        act("document.querySelector('[data-view=app]').click()")
        cdp.call('Emulation.setCPUThrottlingRate', {'rate': 1})
    cdp.evaluate("document.querySelector('.traffic-row').scrollIntoView({block:'center'})")
    for _ in range(50):
        if cdp.evaluate("document.querySelectorAll('.app-icon.has-icon img').length > 0"):
            break
        time.sleep(.2)
    assert cdp.evaluate("document.querySelectorAll('.app-icon.has-icon img').length > 0"), 'Native application icons did not load'
    assert cdp.evaluate("Array.from(document.querySelectorAll('.app-icon.has-icon img')).every(i=>i.naturalWidth>0&&new URL(i.src).origin===location.origin)"), 'Icons must be local, decodable images'
    cdp.evaluate("document.getElementById('traffic-table').insertAdjacentHTML('beforeend','<div id=icon-fallback-test><span class=app-icon><span class=app-icon-fallback>X</span><img data-icon-id=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA= alt=\"\"></span></div>');observeApplicationIcons();document.getElementById('icon-fallback-test').scrollIntoView()")
    for _ in range(30):
        if cdp.evaluate("!document.querySelector('#icon-fallback-test img')"):
            break
        time.sleep(.1)
    assert cdp.evaluate("!document.querySelector('#icon-fallback-test img')&&!document.querySelector('#icon-fallback-test .has-icon')"), 'Unavailable icon must retain its letter fallback'
    cdp.evaluate("document.getElementById('icon-fallback-test').remove()")
    act("document.querySelector('[data-expand]').click()")
    assert cdp.evaluate("document.querySelectorAll('.traffic-row.child').length") > 0
    act("document.querySelector('.traffic-row.child[data-expand]').click()")
    assert cdp.evaluate("document.querySelectorAll('.traffic-row.grandchild').length") > 0
    act("document.querySelector('[data-view=node]').click()")
    assert cdp.evaluate("state.view") == 'node'
    act("document.querySelector('[data-view=app]').click();document.getElementById('settings-button').click()")
    assert cdp.evaluate("document.getElementById('settings-panel').hidden") is False
    assert cdp.evaluate("document.getElementById('startup-toggle').getAttribute('role')") == 'switch'
    assert cdp.evaluate("document.getElementById('port-mode').value") == 'auto'
    cdp.evaluate("document.getElementById('port-mode').value='fixed';document.getElementById('port-mode').dispatchEvent(new Event('change'))")
    assert cdp.evaluate("document.getElementById('port-number').hidden") is False
    cdp.evaluate("document.getElementById('port-mode').value='auto';document.getElementById('port-mode').dispatchEvent(new Event('change'))")
    assert cdp.evaluate("document.getElementById('port-number').hidden") is True
    cdp.evaluate("document.getElementById('settings-button').click();document.getElementById('accuracy-toggle').click()")
    assert cdp.evaluate("document.getElementById('accuracy-detail').hidden") is False
    cdp.evaluate("document.getElementById('accuracy-toggle').click()")
    cdp.evaluate("document.querySelector('[data-point]').focus()")
    assert cdp.evaluate("document.getElementById('chart-tooltip').hasAttribute('hidden')") is False, cdp.evaluate("({active:document.activeElement.outerHTML,tip:document.getElementById('chart-tooltip').outerHTML,errors:state.data.trend.length})")
    cdp.call('Input.dispatchKeyEvent', {'type': 'keyDown', 'key': 'ArrowRight', 'code': 'ArrowRight'})
    assert cdp.evaluate("document.activeElement.dataset.point") == '1'
    cdp.evaluate("document.activeElement.blur()")
    def header_order_ok(field, ascending):
        # Check ordering and that the DOM uses the server's complete aggregate page.
        return cdp.evaluate("""(()=>{
          const field=%s, ascending=%s, rows=state.pages.get('[]').rows;
          const dom=[...document.querySelectorAll('.traffic-row:not(.child):not(.grandchild)')];
          if(dom.length!==rows.length||dom.some((el,i)=>el.querySelector('.row-name').textContent!==rows[i].title))return false;
          const actual=rows.map(g=>field==='upload'?g.upload:field==='download'?g.download:g.upload+g.download);
          return actual.length>1&&actual.every((value,i)=>i===0||(ascending?actual[i-1]<=value:actual[i-1]>=value));
        })()""" % (json.dumps(field), str(ascending).lower()))

    # Initial total sorting still starts with descending on the user's first header click.
    for field in ('total', 'upload', 'download', 'share'):
        for ascending in (False, True, False):
            act("document.querySelector('[data-sort-field=\"%s\"]').click()" % field)
            assert header_order_ok(field, ascending), f'{field}: wrong header sort direction ({ascending=})'
            expected = field + ('-asc' if ascending else '')
            assert cdp.evaluate("document.getElementById('sort-order').value") == expected
            assert cdp.evaluate("document.querySelector('[data-sort-field=\"%s\"]').getAttribute('aria-pressed')" % field) == 'true'
            assert cdp.evaluate("document.querySelector('[data-sort-field=\"%s\"] .sort-arrow').textContent" % field) == ('↑' if ascending else '↓')
    for view in ('node', 'host'):
        act("document.querySelector('[data-view=\"%s\"]').click();document.querySelector('[data-sort-field=\"upload\"]').click()" % view)
        assert header_order_ok('upload', False), f'{view}: header sorting must apply to this view'
        act("document.querySelector('[data-sort-field=\"upload\"]').click()")
        assert header_order_ok('upload', True)
    act("document.querySelector('[data-view=app]').click();document.getElementById('sort-order').value='total';document.getElementById('sort-order').dispatchEvent(new Event('change'))")
    act("document.querySelector('[data-expand]').click()")
    expanded_key = cdp.evaluate("document.querySelector('[data-expand]').dataset.expand")
    act("document.querySelector('[data-sort-field=\"total\"]').click()")
    assert cdp.evaluate('state.expanded.has('+json.dumps(expanded_key)+')'), 'Sorting must preserve expanded branches'
    assert cdp.evaluate("(()=>{const page=state.pages.get("+json.dumps(expanded_key)+");return !page||page.rows.every((r,i)=>i===0||page.rows[i-1].upload+page.rows[i-1].download<=r.upload+r.download)})()"), 'Visible expanded branches must reload with the selected sort order'
    print('PASS: Clickable total/upload/download/share headers, descending/ascending cycles, synchronized menu, all three views and expanded branches.')
    act("document.getElementById('sort-order').value='upload';document.getElementById('sort-order').dispatchEvent(new Event('change'))")
    assert header_order_ok('upload', False), 'Upload sort did not order applications correctly'
    act("document.getElementById('sort-order').value='total';document.getElementById('sort-order').dispatchEvent(new Event('change'))")
    export_ok = cdp.evaluate("(async()=>{const q=params();q.set('search','chrome.exe');const r=await fetch('/api/export?'+q);return r.ok&&(await r.text()).includes('上传字节')})()")
    assert export_ok, 'CSV export failed'
    invalid = cdp.evaluate("(async()=>{const r=await fetch('/api/stats?from=wrong&to=wrong');return r.status})()")
    assert invalid == 400

    def chart_click(minute_of_day):
        cdp.evaluate("document.getElementById('trend-chart').scrollIntoView({block:'center',behavior:'instant'})")
        coordinates = cdp.evaluate(f"(()=>{{const g=state.chartGeometry,s=document.querySelector('#trend-chart svg');const x=g.left+g.plotW*{minute_of_day}/1440;const p=new DOMPoint(x,g.top+g.plotH*.75).matrixTransform(s.getScreenCTM());return {{x:p.x,y:p.y}}}})()")
        cdp.call('Input.dispatchMouseEvent', {'type': 'mouseMoved', **coordinates})
        cdp.call('Input.dispatchMouseEvent', {'type': 'mousePressed', 'button': 'left', 'clickCount': 1, **coordinates})
        cdp.call('Input.dispatchMouseEvent', {'type': 'mouseReleased', 'button': 'left', 'clickCount': 1, **coordinates})

    assert cdp.evaluate("document.querySelectorAll('.traffic-row:not(.child):not(.grandchild) .row-tags').length") > 0, 'Application route badges missing'
    assert cdp.evaluate("(()=>{const chrome=[...document.querySelectorAll('.traffic-row')].find(r=>r.querySelector('.row-name').textContent==='chrome.exe');return chrome&&chrome.querySelector('.route-tag.proxy')&&chrome.querySelector('.route-tag.direct')&&chrome.querySelectorAll('.node-tag').length===2})()"), 'Chrome must expose mixed routes and both actual nodes before expansion'
    for view in ('app', 'host', 'node'):
        act("document.querySelector('[data-view=\"%s\"]').click()" % view)
        act("document.querySelector('#traffic-table [data-expand]').click()")
        assert cdp.evaluate("""(()=>{
          function check(row,group){
            const labels=[...new Set(group.routes.map(r=>r.source==='windows'?'Clash 外':r.route==='DIRECT'?'直连':r.route==='PROXY'?'代理':r.route==='REJECT'?'拒绝':'其他路线'))];
            const badges=[...row.querySelectorAll('.route-tag')].map(t=>t.textContent);
            if(labels.length!==badges.length||!labels.every(label=>badges.includes(label)))return false;
            const expected=group.nodes;
            const actual=[...row.querySelectorAll('.node-tag')].map(t=>t.title);
            return expected.length===actual.length&&expected.every(node=>actual.includes(node));
          }
          return [...state.pages.values()].every(page=>page.rows.every(group=>{const key=JSON.stringify([...page.path,group.key]),row=[...document.querySelectorAll('[data-expand]')].find(r=>r.dataset.expand===key);return row&&check(row,group)}));
        })()"""), f'{view}: every parent and child must show all route types and actual proxy nodes before leaf expansion'
    act("document.querySelector('[data-view=host]').click();document.getElementById('service-toggle').click()")
    assert cdp.evaluate("[...document.querySelectorAll('.traffic-row:not(.child):not(.grandchild)')].filter(r=>r.querySelector('.row-name').textContent==='www.youtube.com').every(r=>r.querySelector('.route-tag.proxy')&&r.querySelector('.route-tag.direct')&&r.querySelector('.node-tag'))"), 'Unmerged websites should expose mixed routes and proxy nodes too'
    act("document.getElementById('service-toggle').click();document.querySelector('[data-view=app]').click()")
    print('PASS: Route badges and actual proxy nodes at website/application roots and all child branches, including mixed paths and multiple nodes.')

    cdp.evaluate("document.getElementById('range-picker-button').click()")
    assert cdp.evaluate("!document.getElementById('range-picker').hidden&&document.getElementById('range-whole-days').checked")
    cdp.evaluate("document.getElementById('range-whole-days').click();document.getElementById('time-from').value='15:45';document.getElementById('time-to').value='16:30';document.getElementById('apply-range').click()")
    settled()
    assert cdp.evaluate("state.data.from===state.range.start&&state.data.to===state.range.end&&state.data.to-state.data.from===46*60"), 'Calendar minute range must include its last minute'
    assert cdp.evaluate("new URLSearchParams(params()).get('fromTime')") == '15:45'
    assert cdp.evaluate("state.data.trendTo-state.data.trendFrom") == 86400
    assert cdp.evaluate("document.getElementById('range-from-label').textContent.endsWith('15:45')")
    cdp.evaluate("document.getElementById('range-picker-button').click();document.getElementById('time-from').value='18:00';document.getElementById('time-to').value='17:00';document.getElementById('apply-range').click()")
    assert cdp.evaluate("document.getElementById('range-error').hidden") is False, 'Reversed calendar times should report an inline error'
    assert cdp.evaluate("state.range.fromTime") == '15:45', 'Invalid draft must leave the applied filter unchanged'
    cdp.evaluate("document.getElementById('close-range-picker').click();document.getElementById('range-picker-button').click()")
    assert cdp.evaluate("document.getElementById('time-from').value") == '15:45', 'Reopening the calendar must discard an unapplied draft'
    cdp.evaluate("document.getElementById('range-whole-days').click();document.getElementById('apply-range').click()")
    settled()
    assert cdp.evaluate("state.data.to-state.data.from") == 86400, 'Whole-day selection must restore all minutes'

    chart_click(15*60+45.35)
    assert cdp.evaluate('state.pendingStart') == cdp.evaluate('state.chartGeometry.from+15*3600+45*60'), cdp.evaluate('({pending:state.pendingStart,range:state.range,geometry:state.chartGeometry})')
    before = cdp.evaluate('state.data.from')
    assert cdp.evaluate('state.range.start') == before, 'The first chart click should only choose a pending start'
    chart_click(17*60+30.35)
    settled()
    assert cdp.evaluate('state.pendingStart===null&&state.range.end-state.range.start===106*60'), 'Two chart clicks must apply the inclusive minute range'
    assert cdp.evaluate('state.data.from===state.range.start&&state.data.to===state.range.end'), 'Graph selection did not reach the server query'
    assert cdp.evaluate("document.getElementById('chart-range-fill').hasAttribute('hidden')") is False
    assert cdp.evaluate('state.data.trendTo-state.data.trendFrom') == 86400, 'Chart context must stay full-day after selection'
    selected_start = cdp.evaluate('state.data.from')
    chart_click(9*60+10.35)
    assert cdp.evaluate('state.pendingStart') == cdp.evaluate('state.chartGeometry.from+9*3600+10*60'), 'The third click must start a new selection outside the prior range'
    assert cdp.evaluate('state.data.from') == selected_start, 'Third click should leave the last completed filter active'
    chart_click(8*60+15.35)
    settled()
    assert cdp.evaluate("state.range.fromTime==='08:15'&&state.range.toTime==='09:10'"), 'Reverse chart selection must sort its bounds'
    chart_click(6*60+5.35)
    cdp.call('Input.dispatchKeyEvent', {'type': 'keyDown', 'key': 'Escape', 'code': 'Escape'})
    assert cdp.evaluate('state.pendingStart') is None, 'Escape must cancel the pending start'
    chart_click(10*60+7.35)
    chart_click(10*60+7.35)
    settled()
    assert cdp.evaluate('state.range.end-state.range.start') == 60, 'Clicking the same minute twice must select one minute'
    cdp.evaluate("document.getElementById('reset-filters').click()")
    settled()
    assert cdp.evaluate('!timeFiltered()&&state.pendingStart===null&&state.data.to-state.data.from===86400'), 'Clear filters must also clear a custom time range'
    assert cdp.evaluate("document.getElementById('range-whole-days').checked")
    cdp.evaluate("document.getElementById('range-picker-button').click();document.getElementById('date-from').value='2020-01-01';document.getElementById('date-to').value='2020-01-01';document.getElementById('apply-range').click()")
    settled()
    assert cdp.evaluate('state.data.trend.length===0&&Boolean(state.chartGeometry)'), 'An empty date must retain a selectable time axis'
    chart_click(8*60+15.35)
    assert cdp.evaluate('state.pendingStart') is not None, 'Empty chart regions must accept a start point'
    chart_click(9*60+10.35)
    settled()
    assert cdp.evaluate("timeFiltered()&&state.pages.get('[]').total===0&&state.data.from===state.range.start"), 'Selection on an empty date should still apply its time filter'
    cdp.evaluate("document.querySelector('#presets [data-days=\"1\"]').click()")
    settled()

    cdp.errors = [e for e in cdp.errors if 'status of 400' not in e]
    external = cdp.evaluate("performance.getEntriesByType('resource').filter(x=>new URL(x.name).origin!==location.origin).map(x=>x.name)")
    assert external == [], external
    icon_health = cdp.evaluate("(async()=>{const s=await (await fetch('/api/status')).json();return s.icons})()")
    assert icon_health['initialized'] and icon_health['extractions'] + icon_health['diskHits'] > 0, icon_health
    cdp.evaluate("window.scrollTo(0,Math.max(0,document.querySelector('.details-panel').offsetTop-100))")
    time.sleep(.5)
    desktop = ROOT / 'tests' / 'dashboard-desktop.png'
    desktop.write_bytes(base64.b64decode(cdp.call('Page.captureScreenshot', {'captureBeyondViewport': True})['data']))
    cdp.call('Emulation.setDeviceMetricsOverride', {'width': 390, 'height': 844, 'deviceScaleFactor': 1, 'mobile': True})
    time.sleep(.3)
    assert cdp.evaluate('document.documentElement.scrollWidth <= window.innerWidth'), 'Mobile horizontal overflow'
    cdp.evaluate("document.getElementById('range-picker-button').click();document.getElementById('range-whole-days').click()")
    assert cdp.evaluate('document.documentElement.scrollWidth <= window.innerWidth'), 'Mobile calendar/time picker overflow'
    assert cdp.evaluate("(()=>{const r=document.getElementById('range-picker').getBoundingClientRect();return r.left>=0&&r.right<=innerWidth})()"), 'Time picker must fit a narrow window'
    cdp.evaluate("document.getElementById('close-range-picker').click()")
    mobile = ROOT / 'tests' / 'dashboard-mobile.png'
    mobile.write_bytes(base64.b64decode(cdp.call('Page.captureScreenshot', {'captureBeyondViewport': True})['data']))
    assert not cdp.errors, cdp.errors
    if '--showcase' in sys.argv:
        source = Path(sys.argv[sys.argv.index('--showcase-output') + 1]) if '--showcase-output' in sys.argv else ROOT / 'tools' / 'showcase' / 'sources'
        source.mkdir(parents=True, exist_ok=True)

        def screenshot(name, width=1440, height=1080, expression=None):
            cdp.call('Emulation.setDeviceMetricsOverride', {'width': width, 'height': height, 'deviceScaleFactor': 1, 'mobile': width < 500})
            cdp.evaluate("window.scrollTo({top:0,behavior:'instant'});document.querySelector('.eyebrow').textContent='DEMO DATA · 演示数据，非真实访问记录';document.getElementById('data-directory').textContent='数据位置：D:/DataTrafficLedger/data（演示目录）'")
            time.sleep(.3)
            options = {'captureBeyondViewport': True}
            if expression:
                clip = cdp.evaluate(expression)
                options['clip'] = {**clip, 'scale': 1}
            else:
                options['clip'] = {'x': 0, 'y': 0, 'width': width, 'height': height, 'scale': 1}
            (source / (name + '_zh.png')).write_bytes(base64.b64decode(cdp.call('Page.captureScreenshot', options)['data']))

        cdp.evaluate("(async()=>{await savePreferences('light',false);document.getElementById('toast').hidden=true})()")
        overview_clip = "({x:0,y:0,width:window.innerWidth,height:document.getElementById('details').offsetTop-20})"
        screenshot('dashboard-light', expression=overview_clip)
        cdp.evaluate("document.getElementById('theme-button').click()")
        for _ in range(30):
            if cdp.evaluate("document.documentElement.dataset.theme") == 'dark':
                break
            time.sleep(.1)
        assert cdp.evaluate("document.documentElement.dataset.theme") == 'dark', 'Theme control did not apply dark mode'
        saved = cdp.evaluate("(async()=>await (await fetch('/api/status')).json())()")
        assert saved['preferences']['theme'] == 'dark', 'Theme must survive a new browser origin'
        cdp.evaluate("document.getElementById('toast').hidden=true")
        screenshot('dashboard-dark', expression=overview_clip)
        cdp.evaluate("document.getElementById('settings-button').click()")
        screenshot('settings', expression="(()=>{const r=document.getElementById('settings-panel').getBoundingClientRect();return {x:r.x-12,y:r.y-12,width:r.width+24,height:r.height+24}})()")
        cdp.evaluate("document.getElementById('close-settings').click()")
        cdp.evaluate("(async()=>{await savePreferences('light',false);document.getElementById('toast').hidden=true})()")
        act("document.querySelector('[data-view=app]').click()")
        act("document.querySelector('[data-expand]').click()")
        act("document.querySelector('.traffic-row.child[data-expand]').click()")
        cdp.evaluate("document.getElementById('details').scrollIntoView({block:'start',behavior:'instant'})")
        for _ in range(30):
            if cdp.evaluate("document.querySelectorAll('.app-icon.has-icon img').length > 0"):
                break
            time.sleep(.1)
        screenshot('details', height=900, expression="(()=>{const r=document.getElementById('details').getBoundingClientRect();return {x:r.x-12,y:r.y-12,width:r.width+24,height:r.height+24}})()")
        act("document.querySelector('[data-view=host]').click()")
        act("const youtube=[...document.querySelectorAll('[data-expand]')].find(r=>r.querySelector('.row-name').textContent==='YouTube');if(youtube)youtube.click()")
        screenshot('website-routes', height=900, expression="(()=>{const r=document.getElementById('details').getBoundingClientRect();return {x:r.x-12,y:r.y-12,width:r.width+24,height:r.height+24}})()")
        act("document.querySelector('[data-view=app]').click()")
        act("document.querySelector('[data-expand]').click()")
        screenshot('mobile', width=390, height=1440)
        assert cdp.evaluate('document.documentElement.scrollWidth <= window.innerWidth')
        cdp.evaluate("settingsVisible(true)")
        assert cdp.evaluate('document.documentElement.scrollWidth <= window.innerWidth'), 'Mobile settings overflow'
        cdp.evaluate("settingsVisible(false)")
        cdp.call('Emulation.setDeviceMetricsOverride', {'width': 1440, 'height': 1080, 'deviceScaleFactor': 1, 'mobile': False})
        time.sleep(.2)
        chart_click(15*60+45.35)
        chart_click(17*60+30.35)
        settled()
        screenshot('trend-selection', expression="(()=>{const r=document.querySelector('.trend-panel').getBoundingClientRect();return {x:r.x-12,y:r.y-12,width:r.width+24,height:r.height+24}})()")
        cdp.evaluate("document.getElementById('range-picker-button').click()")
        cdp.evaluate('document.activeElement.blur()')
        screenshot('time-range', expression="(()=>{const r=document.getElementById('range-picker').getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height}})()")
        cdp.evaluate("document.getElementById('close-range-picker').click();document.getElementById('clear-time-range').click()")
        settled()
        assert not cdp.errors, cdp.errors
        print('Showcase captures saved (synthetic data only).')
    print(json.dumps({'browser': 'Chrome (isolated)', 'checks': ['live data', 'native application icons', 'missing-icon letter fallback', 'local image cache', 'application route badges', 'application expansion', 'original domain and node', 'node view', 'settings', 'measurement notes', 'CSV', 'calendar minutes and whole days', 'invalid time ranges', 'first/second/third chart clicks', 'reverse and single-minute selections', 'Escape cancellation', 'time-filter reset', 'no external resources', 'mobile calendar and table layout', 'no JS/CSP errors'], 'iconCache': icon_health, 'screenshots': [str(desktop), str(mobile)]}, ensure_ascii=False))
finally:
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=8)
        except subprocess.TimeoutExpired:
            process.kill()

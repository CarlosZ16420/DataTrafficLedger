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
    cdp.call('Page.navigate', {'url': f"http://127.0.0.1:{state['port']}/open/{state['token']}"})
    for _ in range(40):
        if cdp.evaluate("Boolean(typeof state !== 'undefined' && state.data && state.status)"):
            break
        time.sleep(.2)
    assert cdp.evaluate("Boolean(state.data && state.status)"), 'Page failed to load data'
    assert cdp.evaluate("document.title") == '流量账本'
    assert cdp.evaluate("document.querySelectorAll('.traffic-row').length") > 0, 'No live records shown'
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
    cdp.evaluate("document.querySelector('[data-expand]').click()")
    assert cdp.evaluate("document.querySelectorAll('.traffic-row.child').length") > 0
    cdp.evaluate("document.querySelector('.traffic-row.child[data-expand]').click()")
    assert cdp.evaluate("document.querySelectorAll('.traffic-row.grandchild').length") > 0
    cdp.evaluate("document.querySelector('[data-view=node]').click()")
    assert cdp.evaluate("state.view") == 'node'
    cdp.evaluate("document.querySelector('[data-view=app]').click();document.getElementById('settings-button').click()")
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
        # Compare visible DOM rows with independently aggregated raw records, using unrounded bytes.
        return cdp.evaluate("""(()=>{
          const field=%s, ascending=%s, view=state.view, sums=new Map();
          state.data.rows.forEach(r=>{const key=view==='app'?JSON.stringify([r.app,r.path]):view==='node'?r.node:document.getElementById('service-toggle').checked?service(r.host):r.host;
            if(!sums.has(key))sums.set(key,{up:0,down:0});const g=sums.get(key);g.up+=Number(r.upload);g.down+=Number(r.download);});
          const actual=[...document.querySelectorAll('.traffic-row:not(.child):not(.grandchild)')].map(row=>{const g=sums.get(row.dataset.expand.slice(view.length+1));return field==='upload'?g.up:field==='download'?g.down:g.up+g.down;});
          return actual.length>1&&actual.every((value,i)=>i===0||(ascending?actual[i-1]<=value:actual[i-1]>=value));
        })()""" % (json.dumps(field), str(ascending).lower()))

    # Initial total sorting still starts with descending on the user's first header click.
    for field in ('total', 'upload', 'download', 'share'):
        for ascending in (False, True, False):
            cdp.evaluate("document.querySelector('[data-sort-field=\"%s\"]').click()" % field)
            assert header_order_ok(field, ascending), f'{field}: wrong header sort direction ({ascending=})'
            expected = field + ('-asc' if ascending else '')
            assert cdp.evaluate("document.getElementById('sort-order').value") == expected
            assert cdp.evaluate("document.querySelector('[data-sort-field=\"%s\"]').getAttribute('aria-pressed')" % field) == 'true'
            assert cdp.evaluate("document.querySelector('[data-sort-field=\"%s\"] .sort-arrow').textContent" % field) == ('↑' if ascending else '↓')
    for view in ('node', 'host'):
        cdp.evaluate("document.querySelector('[data-view=\"%s\"]').click();document.querySelector('[data-sort-field=\"upload\"]').click()" % view)
        assert header_order_ok('upload', False), f'{view}: header sorting must apply to this view'
        cdp.evaluate("document.querySelector('[data-sort-field=\"upload\"]').click()")
        assert header_order_ok('upload', True)
    cdp.evaluate("document.querySelector('[data-view=app]').click();document.getElementById('sort-order').value='total';document.getElementById('sort-order').dispatchEvent(new Event('change'));document.querySelector('[data-expand]').click()")
    expanded_key = cdp.evaluate("document.querySelector('[data-expand]').dataset.expand")
    cdp.evaluate("document.querySelector('[data-sort-field=\"total\"]').click()")
    assert cdp.evaluate('state.expanded.has('+json.dumps(expanded_key)+')'), 'Sorting must preserve expanded branches'
    assert cdp.evaluate("(()=>{const key="+json.dumps(expanded_key)+",identity=JSON.parse(key.slice(4)),totals=new Map();state.data.rows.filter(r=>r.app===identity[0]&&r.path===identity[1]).forEach(r=>{const host=document.getElementById('service-toggle').checked?service(r.host):r.host;totals.set(host,(totals.get(host)||0)+Number(r.upload)+Number(r.download))});const row=[...document.querySelectorAll('[data-expand]')].find(r=>r.dataset.expand===key),values=[];for(let next=row.nextElementSibling;next&&next.classList.contains('child');next=next.nextElementSibling)values.push(totals.get(next.dataset.expand.slice(key.length+2)));return values.length===totals.size&&values.every((v,i)=>i===0||values[i-1]<=v)})()"), 'Expanded children should follow ascending sorting while staying under their parent'
    print('PASS: Clickable total/upload/download/share headers, descending/ascending cycles, synchronized menu, all three views and expanded branches.')
    cdp.evaluate("document.getElementById('sort-order').value='upload';document.getElementById('sort-order').dispatchEvent(new Event('change'))")
    assert cdp.evaluate("(()=>{const groups=groupRows(state.data.rows,r=>JSON.stringify([r.app,r.path]));const top=groups.sort((a,b)=>b.upload-a.upload)[0];return document.querySelector('.traffic-row .row-name').textContent===top.rows[0].app})()"), 'Upload sort did not order applications correctly'
    cdp.evaluate("document.getElementById('sort-order').value='total';document.getElementById('sort-order').dispatchEvent(new Event('change'))")
    export_ok = cdp.evaluate("(async()=>{const r=await fetch('/api/export?'+params());return r.ok&&(await r.text()).includes('上传字节')})()")
    assert export_ok, 'CSV export failed'
    invalid = cdp.evaluate("(async()=>{const r=await fetch('/api/stats?from=wrong&to=wrong');return r.status})()")
    assert invalid == 400

    def settled():
        for _ in range(80):
            if cdp.evaluate('!state.loading'):
                return
            time.sleep(.05)
        raise TimeoutError('Filtering did not finish')

    def chart_click(minute_of_day):
        cdp.evaluate("document.getElementById('trend-chart').scrollIntoView({block:'center',behavior:'instant'})")
        coordinates = cdp.evaluate(f"(()=>{{const g=state.chartGeometry,s=document.querySelector('#trend-chart svg');const x=g.left+g.plotW*{minute_of_day}/1440;const p=new DOMPoint(x,g.top+g.plotH*.75).matrixTransform(s.getScreenCTM());return {{x:p.x,y:p.y}}}})()")
        cdp.call('Input.dispatchMouseEvent', {'type': 'mouseMoved', **coordinates})
        cdp.call('Input.dispatchMouseEvent', {'type': 'mousePressed', 'button': 'left', 'clickCount': 1, **coordinates})
        cdp.call('Input.dispatchMouseEvent', {'type': 'mouseReleased', 'button': 'left', 'clickCount': 1, **coordinates})

    assert cdp.evaluate("document.querySelectorAll('.traffic-row:not(.child):not(.grandchild) .row-tags').length") > 0, 'Application route badges missing'
    assert cdp.evaluate("(()=>{const rows=[...document.querySelectorAll('.traffic-row:not(.child):not(.grandchild)')];return rows.every(row=>{const app=row.querySelector('.row-name').textContent;const namePath=JSON.parse(row.dataset.expand.slice(4));const routes=state.data.applicationRoutes.filter(r=>r.app===app&&r.path===namePath[1]);const labels=[...new Set(routes.map(r=>r.source==='windows'?'Clash 外':r.route==='DIRECT'?'直连':r.route==='PROXY'?'代理':r.route==='REJECT'?'拒绝':'其他路线'))];return labels.length==row.querySelectorAll('.route-tag').length&&labels.every(label=>[...row.querySelectorAll('.route-tag')].some(t=>t.textContent===label))})})()"), 'Application badges must reflect all routes within active filters'
    for view in ('app', 'host', 'node'):
        cdp.evaluate("document.querySelector('[data-view=\"%s\"]').click();[...document.querySelectorAll('#traffic-table [data-expand]')].forEach(r=>{if(r.getAttribute('aria-expanded')==='false')state.expanded.add(r.dataset.expand)});renderTable()" % view)
        assert cdp.evaluate("""(()=>{
          const view=state.view, merge=document.getElementById('service-toggle').checked;
          const roots=groupRows(state.data.rows,r=>view==='app'?JSON.stringify([r.app,r.path]):view==='node'?r.node:merge?service(r.host):r.host);
          function check(row,records,showNodes){
            const labels=[...new Set(records.map(r=>r.source==='windows'?'Clash 外':r.route==='DIRECT'?'直连':r.route==='PROXY'?'代理':r.route==='REJECT'?'拒绝':'其他路线'))];
            const badges=[...row.querySelectorAll('.route-tag')].map(t=>t.textContent);
            if(labels.length!==badges.length||!labels.every(label=>badges.includes(label)))return false;
            const expected=showNodes?[...new Set(records.filter(r=>r.source==='mihomo'&&r.route==='PROXY').map(r=>r.node).filter(Boolean))]:[];
            const actual=[...row.querySelectorAll('.node-tag')].map(t=>t.title);
            return expected.length===actual.length&&expected.every(node=>actual.includes(node));
          }
          return roots.every(root=>{
            const key=view+':'+root.key,row=[...document.querySelectorAll('[data-expand]')].find(r=>r.dataset.expand===key);
            if(!row||!check(row,root.rows,view!=='node'))return false;
            const children=groupRows(root.rows,r=>view==='app'?(merge?service(r.host):r.host):JSON.stringify([r.app,r.path]));
            return children.every(child=>{const childRow=[...document.querySelectorAll('[data-expand]')].find(r=>r.dataset.expand===key+'::'+child.key);return childRow&&check(childRow,child.rows,true)});
          });
        })()"""), f'{view}: every parent and child must show all route types and actual proxy nodes before leaf expansion'
    cdp.evaluate("document.querySelector('[data-view=host]').click();document.getElementById('service-toggle').click()")
    assert cdp.evaluate("[...document.querySelectorAll('.traffic-row:not(.child):not(.grandchild)')].filter(r=>r.querySelector('.row-name').textContent==='www.youtube.com').every(r=>r.querySelector('.route-tag.proxy')&&r.querySelector('.route-tag.direct')&&r.querySelector('.node-tag'))"), 'Unmerged websites should expose mixed routes and proxy nodes too'
    cdp.evaluate("document.getElementById('service-toggle').click();document.querySelector('[data-view=app]').click()")
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
    assert cdp.evaluate('timeFiltered()&&state.data.rows.length===0&&state.data.from===state.range.start'), 'Selection on an empty date should still apply its time filter'
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
        source = ROOT / 'tools' / 'showcase' / 'sources'
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
        cdp.evaluate("document.querySelector('[data-view=app]').click();document.querySelector('[data-expand]').click();document.querySelector('.traffic-row.child[data-expand]').click()")
        cdp.evaluate("document.getElementById('details').scrollIntoView({block:'start',behavior:'instant'})")
        for _ in range(30):
            if cdp.evaluate("document.querySelectorAll('.app-icon.has-icon img').length > 0"):
                break
            time.sleep(.1)
        screenshot('details', height=900, expression="(()=>{const r=document.getElementById('details').getBoundingClientRect();return {x:r.x-12,y:r.y-12,width:r.width+24,height:r.height+24}})()")
        cdp.evaluate("document.querySelector('[data-view=host]').click();const youtube=[...document.querySelectorAll('[data-expand]')].find(r=>r.querySelector('.row-name').textContent==='YouTube');if(youtube)youtube.click()")
        screenshot('website-routes', height=900, expression="(()=>{const r=document.getElementById('details').getBoundingClientRect();return {x:r.x-12,y:r.y-12,width:r.width+24,height:r.height+24}})()")
        cdp.evaluate("document.querySelector('[data-view=app]').click();document.querySelector('[data-expand]').click()")
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

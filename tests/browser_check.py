"""Exercise the shipped HTML in an isolated Chrome profile; no third-party dependencies."""
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
    cdp.call('Emulation.setDeviceMetricsOverride', {'width': 1440, 'height': 1080, 'deviceScaleFactor': 1, 'mobile': False})
    cdp.call('Page.navigate', {'url': f"http://127.0.0.1:{state['port']}/open/{state['token']}"})
    for _ in range(40):
        if cdp.evaluate("Boolean(typeof state !== 'undefined' && state.data && state.status)"):
            break
        time.sleep(.2)
    assert cdp.evaluate("Boolean(state.data && state.status)"), 'Page failed to load data'
    assert cdp.evaluate("document.title") == '本机流量账本'
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
    cdp.evaluate("document.getElementById('sort-order').value='upload';document.getElementById('sort-order').dispatchEvent(new Event('change'))")
    assert cdp.evaluate("(()=>{const groups=groupRows(state.data.rows,r=>JSON.stringify([r.app,r.path]));const top=groups.sort((a,b)=>b.upload-a.upload)[0];return document.querySelector('.traffic-row .row-title').textContent===top.rows[0].app})()"), 'Upload sort did not order applications correctly'
    cdp.evaluate("document.getElementById('sort-order').value='total';document.getElementById('sort-order').dispatchEvent(new Event('change'))")
    export_ok = cdp.evaluate("(async()=>{const r=await fetch('/api/export?'+params());return r.ok&&(await r.text()).includes('上传字节')})()")
    assert export_ok, 'CSV export failed'
    invalid = cdp.evaluate("(async()=>{const r=await fetch('/api/stats?from=wrong&to=wrong');return r.status})()")
    assert invalid == 400
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
    mobile = ROOT / 'tests' / 'dashboard-mobile.png'
    mobile.write_bytes(base64.b64decode(cdp.call('Page.captureScreenshot', {'captureBeyondViewport': True})['data']))
    assert not cdp.errors, cdp.errors
    if '--showcase' in sys.argv:
        source = ROOT / 'tools' / 'showcase' / 'sources'
        source.mkdir(parents=True, exist_ok=True)

        def screenshot(name, width=1440, height=1080, expression=None):
            cdp.call('Emulation.setDeviceMetricsOverride', {'width': width, 'height': height, 'deviceScaleFactor': 1, 'mobile': width < 500})
            cdp.evaluate("window.scrollTo({top:0,behavior:'instant'});document.querySelector('.eyebrow').textContent='DEMO DATA · 演示数据，非真实访问记录';document.getElementById('data-directory').textContent='数据位置：D:/LocalTrafficLedger/data（演示目录）'")
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
        screenshot('mobile', width=390, height=1440)
        assert cdp.evaluate('document.documentElement.scrollWidth <= window.innerWidth')
        cdp.evaluate("settingsVisible(true)")
        assert cdp.evaluate('document.documentElement.scrollWidth <= window.innerWidth'), 'Mobile settings overflow'
        assert not cdp.errors, cdp.errors
        print('Showcase captures saved (synthetic data only).')
    print(json.dumps({'browser': 'Chrome (isolated)', 'checks': ['live data', 'native application icons', 'missing-icon letter fallback', 'local image cache', 'application expansion', 'original domain and node', 'node view', 'settings', 'measurement notes', 'CSV', 'invalid dates', 'no external resources', 'mobile layout', 'no JS/CSP errors'], 'iconCache': icon_health, 'screenshots': [str(desktop), str(mobile)]}, ensure_ascii=False))
finally:
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=8)
        except subprocess.TimeoutExpired:
            process.kill()

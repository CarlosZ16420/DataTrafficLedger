"""Round-trip the installed app's startup switch, restoring its original state."""
import json
from pathlib import Path
import sys
import urllib.request

data = Path(sys.argv[1])
state = json.loads((data / 'runtime.json').read_text(encoding='utf-8-sig'))
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def request(path, post=False):
    headers = {'Cookie': 'ledger_session=' + state['token']}
    if post:
        headers['X-Ledger-Token'] = state['token']
    req = urllib.request.Request(f"http://127.0.0.1:{state['port']}{path}", headers=headers,
                                 data=b'' if post else None)
    return json.loads(opener.open(req, timeout=10).read())


original = request('/api/status')['startup']
try:
    assert request('/api/startup?enabled=false', True)['enabled'] is False
    assert request('/api/status')['startup'] is False
    assert request('/api/startup?enabled=true', True)['enabled'] is True
    status = request('/api/status')
    assert status['startup'] and not status['startupNeedsRepair'], 'Scheduled action must point to this executable and data directory'
    print('PASS: Startup off/on, scheduler state, current executable/data path; original preference restored.')
finally:
    request('/api/startup?enabled=' + str(original).lower(), True)

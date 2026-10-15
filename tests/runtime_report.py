"""Read only the installed application's local health; never print its session credential."""
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import urllib.request

root = Path(__file__).resolve().parent.parent
state = json.loads((root / 'data' / 'runtime.json').read_text(encoding='utf-8-sig'))
client = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def get(path):
    request = urllib.request.Request(f"http://127.0.0.1:{state['port']}{path}",
                                     headers={'Cookie': 'ledger_session=' + state['token']})
    return json.loads(client.open(request, timeout=8).read())


status = get('/api/status')
today = datetime.now(timezone(timedelta(hours=8))).date().isoformat()
stats = get(f'/api/stats?from={today}&to={today}&scope=all')
report = {'checkedAt': datetime.now(timezone(timedelta(hours=8))).isoformat(),
          'connected': status['collector']['connected'], 'startup': status['startup'],
          'storageHealthy': status['collector']['storageHealthy'],
          'coreVersion': status['collector']['version'], 'connections': status['collector']['connections'],
          'historyRows': len(stats['rows']), 'retentionDays': status['retentionDays'],
          'windowsCaptureRunning': status['etw'].get('running', False),
          'databaseBytes': status['databaseBytes'], 'workingSetBytes': status['memoryBytes']}
assert report['connected'], 'Mihomo is not connected'
assert report['startup'], 'The native application cannot confirm its Windows startup task'
assert report['storageHealthy'], 'The historical ledger is not writable'
assert report['historyRows'] > 0, 'History has not been preserved'
(root / 'tests' / 'verification.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False))

#!/usr/bin/env bash
# Run on the existing API host after the Ubuntu workers are ready.
set -euo pipefail
root=/opt/snowshot-v2
active=$root/runtime/appsettings.Production.json
overlay=${1:-$root/runtime/policy-revision-13.json}
backup=$root/backups/ubuntu-cpu-policy-$(date -u +%Y%m%dT%H%M%SZ)
install -d -m 700 "$backup"
cp -a "$active" "$backup/appsettings.before.json"
python3 - "$active" "$overlay" <<'PY'
import json, pathlib, sys
path=pathlib.Path(sys.argv[1])
current=json.loads(path.read_text())
overlay=json.loads(pathlib.Path(sys.argv[2]).read_text())
assert overlay['Policy']['Revision']==13
assert current['Policy']['Revision'] in (12,13)
def merge(target, source):
    for key, value in source.items():
        if isinstance(value, dict):
            merge(target.setdefault(key, {}), value)
        else:
            target[key]=value
merge(current,overlay)
for name in ('table-extraction','latex-extraction'):
    assert current['Policy']['Resources'][name]['GlobalConcurrency']==1
    assert current['Policy']['Resources'][name]['PerPrincipalConcurrency']==1
temporary=path.with_suffix('.cpu-staged.json')
temporary.write_text(json.dumps(current,indent=2)+'\n')
temporary.chmod(path.stat().st_mode & 0o777)
temporary.replace(path)
PY
recreate() {
    docker compose --env-file "$root/runtime/api.env" -f "$root/compose.yaml" up -d --force-recreate api
}
ready() {
    curl --fail --silent --show-error --max-time 5 --output /dev/null \
        -H 'Host: snowshot.top' -H 'X-Forwarded-For: 127.0.0.1' -H 'X-Forwarded-Proto: https' \
        http://127.0.0.1:5000/health/ready
}
if recreate; then
    for attempt in $(seq 1 45); do
        if ready; then printf 'Policy 13 active; API ready. Backup: %s\n' "$backup"; exit 0; fi
        sleep 2
    done
fi
# Policy activation is monotonic. A rollback after activation needs a higher
# revision, rather than restoring revision 12 and leaving the API unable to start.
python3 - "$backup/appsettings.before.json" "$active" <<'PY'
import json,pathlib,sys
config=json.loads(pathlib.Path(sys.argv[1]).read_text())
config['Policy']['Revision']=14
path=pathlib.Path(sys.argv[2])
path.write_text(json.dumps(config,indent=2)+'\n')
PY
recreate
printf 'Policy deployment failed; restored prior settings at recovery revision 14. Backup: %s\n' "$backup" >&2
exit 1

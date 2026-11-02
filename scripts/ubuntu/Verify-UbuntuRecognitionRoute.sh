#!/usr/bin/env bash
# Run on the API host with two public benchmark WebP fixtures in the argument directory.
set -euo pipefail
fixtures=${1:?Provide the fixture directory}
umask 077
temporary=$(mktemp -d)
trap 'rm -rf -- "$temporary"' EXIT
for kind in table latex; do
    port=18080
    pki=/root/publish/SnowShotApi/table-communication
    if [[ $kind == latex ]]; then
        port=18081
        pki=/opt/snowshot-v2/secrets/latex-communication
    fi
    openssl pkcs12 -in "$pki/api-client.pfx" -passin "file:$pki/api-client-pfx-password.txt" \
        -clcerts -nokeys -out "$temporary/client.pem" 2>/dev/null
    openssl pkcs12 -in "$pki/api-client.pfx" -passin "file:$pki/api-client-pfx-password.txt" \
        -nocerts -nodes -out "$temporary/client-key.pem" 2>/dev/null
    curl --fail --silent --show-error --max-time 60 \
        --resolve "snowshot-$kind-rec:$port:127.0.0.1" \
        --cacert "$pki/worker-server-ca.pem" --cert "$temporary/client.pem" --key "$temporary/client-key.pem" \
        --header 'Content-Type: image/webp' --header "X-Request-ID: ubuntu-cutover-$kind" \
        --data-binary "@$fixtures/$kind.webp" \
        "https://snowshot-$kind-rec:$port/v2/$kind/extract" > "$temporary/$kind.json"
done
python3 - "$temporary" <<'PY'
import hashlib, json, pathlib, sys
root = pathlib.Path(sys.argv[1])
report = {}
for kind, field in (("table", "html"), ("latex", "latex")):
    response = json.loads((root / f"{kind}.json").read_text())
    output = response[field]
    if kind == "table":
        assert "<table>" in output and "Notebook" in output, output
    else:
        assert output == r"x^{2}+y^{2}=1", output
    report[kind] = {"status": 200, "known_output_verified": True,
                    "output_sha256": hashlib.sha256(output.encode()).hexdigest()}
print(json.dumps(report, indent=2))
PY

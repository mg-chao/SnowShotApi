#!/usr/bin/env bash
# name table_workers table_threads table_affinity latex_workers latex_threads latex_affinity [seconds]
set -euo pipefail
name=$1; tw=$2; tt=$3; ta=$4; lw=$5; lt=$6; la=$7; seconds=${8:-90}
[[ $lw == 1 ]] || { echo 'Formula release benchmarks require one process.' >&2; exit 1; }
root=$(cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
directory="$root/reports/$name"
barrier="$directory/barrier"
mkdir -p "$directory"
mkdir "$barrier"
setsid "$root/table-venv/bin/python" scripts/benchmarks/recognition_cpu.py --service table --root "$root/services/TableStructureRec" \
    --workers "$tw" --threads "$tt" --affinity "$ta" --cycles 0 --seconds "$seconds" --barrier-dir "$barrier" \
    --output "$directory/table.json" > "$directory/table.log" 2>&1 &
table_pid=$!
setsid "$root/latex-venv/bin/python" scripts/benchmarks/recognition_cpu.py --service latex --root "$root/services/FormulaRecognition" \
    --workers "$lw" --threads "$lt" --affinity "$la" --cycles 0 --seconds "$seconds" --barrier-dir "$barrier" \
    --output "$directory/latex.json" > "$directory/latex.log" 2>&1 &
latex_pid=$!
cleanup() {
    kill -TERM -- "-$table_pid" "-$latex_pid" 2>/dev/null || true
    sleep .2
    kill -KILL -- "-$table_pid" "-$latex_pid" 2>/dev/null || true
    wait "$table_pid" "$latex_pid" 2>/dev/null || true
}
trap cleanup EXIT
trap 'exit 130' INT TERM
deadline=$(( $(date +%s) + 300 ))
while [[ ! -f "$barrier/table.ready.json" || ! -f "$barrier/latex.ready.json" ]]; do
    kill -0 "$table_pid" "$latex_pid" 2>/dev/null || { echo "Warmup failed; inspect $directory" >&2; exit 1; }
    (( $(date +%s) < deadline )) || { echo 'Readiness barrier timed out.' >&2; exit 1; }
    sleep .1
done
"$root/latex-venv/bin/python" -c 'import json,pathlib,sys,time; p=pathlib.Path(sys.argv[1]); t=p.with_suffix(".tmp"); t.write_text(json.dumps({"start_monotonic":time.monotonic()+.3})); t.replace(p)' "$barrier/start.json"
wait "$table_pid"
wait "$latex_pid"
tail -1 "$directory/table.log"
tail -1 "$directory/latex.log"
trap - INT TERM EXIT

#!/usr/bin/env bash
# Arguments: name table_workers table_threads table_affinity
#            latex_workers latex_threads latex_affinity [seconds]
set -euo pipefail
name=$1; tw=$2; tt=$3; ta=$4; lw=$5; lt=$6; la=$7; seconds=${8:-60}
root=$(cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$root"
start_at=$(( $(date +%s) + 45 ))
mkdir -p "reports/$name"
"$root/table-venv/bin/python" scripts/benchmarks/recognition_cpu.py --service table --root "$root/services/TableStructureRec" \
    --workers "$tw" --threads "$tt" --affinity "$ta" --cycles 0 --seconds "$seconds" --start-at "$start_at" \
    --output "reports/$name/table.json" > "reports/$name/table.log" 2>&1 &
table_pid=$!
"$root/latex-venv/bin/python" scripts/benchmarks/recognition_cpu.py --service latex --root "$root/services/RapidLaTeXOCR" \
    --workers "$lw" --threads "$lt" --affinity "$la" --cycles 0 --seconds "$seconds" --start-at "$start_at" \
    --output "reports/$name/latex.json" > "reports/$name/latex.log" 2>&1 &
latex_pid=$!
trap 'kill "$table_pid" "$latex_pid" 2>/dev/null || true' INT TERM EXIT
wait "$table_pid"
wait "$latex_pid"
tail -1 "reports/$name/table.log"
tail -1 "reports/$name/latex.log"
trap - INT TERM EXIT

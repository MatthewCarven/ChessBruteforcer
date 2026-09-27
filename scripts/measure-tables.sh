#!/usr/bin/env bash
# Solve all 36 tables up to 4 pieces from nothing, one per line, with the time
# and the solver's memory for each.  The regression check for solver changes:
#   scripts/measure-tables.sh <cli bin dir> <empty tables dir> <log file>
# then `cmp` every .cbt against a reference folder (e.g. tables-baseline/).
# Takes ~8 min.  Dependencies are solved first, so each line is one table.
set -uo pipefail
BIN="$1"; OUT="$2"; LOG="$3"
mkdir -p "$OUT"; : > "$LOG"
export CHESS_TABLES="$OUT"
all="KvK KQvK KRvK KBvK KNvK KPvK KQvKQ KQvKR KQvKB KQvKN KQvKP KRvKR KRvKB KRvKN KRvKP KBvKB KBvKN KBvKP KNvKN KNvKP KPvKP KQQvK KQRvK KQBvK KQNvK KQPvK KRRvK KRBvK KRNvK KRPvK KBBvK KBNvK KBPvK KNNvK KNPvK KPPvK"
begin=$(date +%s)
for m in $all; do
  start=$(date +%s)
  out=$(dotnet "$BIN/ChessBruteforcer.Cli.dll" solve "$m" 2>/dev/null)
  echo "$m | $(( $(date +%s) - start ))s | $(echo "$out" | sed -n 2p)" >> "$LOG"
done
echo "TOTAL $(( $(date +%s) - begin ))s, $(ls "$OUT"/*.cbt | wc -l) tables" >> "$LOG"

#!/usr/bin/env bash
# The depth ladder, end to end: solve all 36 tables up to 4 pieces capped at
# 10 plies, extend them to 20, 40, 80, 160, then to the end, one table per
# line with its time and the share of legal positions settled so far.
#   scripts/ladder-tables.sh <cli bin dir> <empty tables dir> <log file> [caps...]
# then `cmp` every .cbt against a reference folder (e.g. tables-baseline/):
# the laddered tables must come out byte for byte the same.
set -uo pipefail
BIN="$1"; OUT="$2"; LOG="$3"; shift 3
caps="${*:-10 20 40 80 160}"
mkdir -p "$OUT"; : > "$LOG"
export CHESS_TABLES="$OUT"
all="KvK KQvK KRvK KBvK KNvK KPvK KQvKQ KQvKR KQvKB KQvKN KQvKP KRvKR KRvKB KRvKN KRvKP KBvKB KBvKN KBvKP KNvKN KNvKP KPvKP KQQvK KQRvK KQBvK KQNvK KQPvK KRRvK KRBvK KRNvK KRPvK KBBvK KBNvK KBPvK KNNvK KNPvK KPPvK"
begin=$(date +%s)
for cap in $caps end; do
  step=$(date +%s)
  for m in $all; do
    start=$(date +%s)
    if [ "$cap" = end ]; then
      out=$(dotnet "$BIN/ChessBruteforcer.Cli.dll" solve "$m" 2>/dev/null)
    else
      out=$(dotnet "$BIN/ChessBruteforcer.Cli.dll" solve "$m" --cap "$cap" 2>/dev/null)
    fi
    # Legal positions and those beyond the cap, both sides to move together.
    settled=$(echo "$out" | awk '/legal positions/ { gsub(",", "", $4); legal += $4 }
      $1 == "beyond" { gsub(",", "", $2); beyond += $2 }
      END { if (legal > 0) printf "%.1f", 100 * (legal - beyond) / legal; else print "-" }')
    echo "$cap | $m | $(( $(date +%s) - start ))s | settled $settled%" >> "$LOG"
  done
  echo "STEP $cap $(( $(date +%s) - step ))s" >> "$LOG"
done
echo "TOTAL $(( $(date +%s) - begin ))s, $(ls "$OUT"/*.cbt | wc -l) tables, $(ls "$OUT"/*.cbf 2>/dev/null | wc -l) frontiers left" >> "$LOG"

#!/usr/bin/env bash
# DTZ (the 50-move rule) for all 36 tables up to 4 pieces, one line each:
# time, then per side to move: wins / draws / losses under the rule, cursed
# wins and blessed losses (decided with best play, drawn by the rule), and
# the longest distance to a capture, pawn move or mate.  Then `dtz verify`
# on every n-th position of each table.
#   scripts/dtz-tables.sh <cli bin dir> <tables dir> <log file> [verify stride, default 5]
# The DTM tables (.cbt) are used from the folder, or solved if missing.
set -uo pipefail
BIN="$1"; OUT="$2"; LOG="$3"; STRIDE="${4:-5}"
mkdir -p "$OUT"; : > "$LOG"
export CHESS_TABLES="$OUT"
all="KvK KQvK KRvK KBvK KNvK KPvK KQvKQ KQvKR KQvKB KQvKN KQvKP KRvKR KRvKB KRvKN KRvKP KBvKB KBvKN KBvKP KNvKN KNvKP KPvKP KQQvK KQRvK KQBvK KQNvK KQPvK KRRvK KRBvK KRNvK KRPvK KBBvK KBNvK KBPvK KNNvK KNPvK KPPvK"
begin=$(date +%s)
for m in $all; do
  start=$(date +%s)
  out=$(dotnet "$BIN/ChessBruteforcer.Cli.dll" dtz "$m" 2>/dev/null)
  took=$(( $(date +%s) - start ))
  summary=$(echo "$out" | awk '
    /to move:/ { side = $1 }
    $1 == "wins" || $1 == "draws" || $1 == "losses" { gsub(",", "", $2); v[side, $1] = $2 }
    $1 == "cursed" { gsub(",", "", $3); gsub(",", "", $6); c[side] = $3; b[side] = $6 }
    $1 == "longest:" { match($0, /in [0-9]+ plies/); l[side] = substr($0, RSTART + 3, RLENGTH - 9) }
    END { for (s in c) printf "%s W/D/L %s/%s/%s cursed %s blessed %s longest %s; ", s,
            v[s, "wins"], v[s, "draws"], v[s, "losses"], c[s], b[s], l[s] }')
  check=$(dotnet "$BIN/ChessBruteforcer.Cli.dll" dtz "$m" verify "$STRIDE" 2>/dev/null | tail -1)
  echo "$m | ${took}s | $summary| $check" >> "$LOG"
done
echo "TOTAL $(( $(date +%s) - begin ))s" >> "$LOG"

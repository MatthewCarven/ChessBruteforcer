#!/usr/bin/env bash
# Solve every 4-piece endgame table into ./tables at the repository root.  Smaller tables
# a solve needs (3-piece, and 4-piece ones reached by promotion) are solved
# along the way.  Tables already on disk are skipped, so it can be stopped and
# restarted.  Expect roughly 1-3 hours and ~2 GB of disk for all 30.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build -c Release -v q src/ChessBruteforcer.Cli
# Relative to the repository root (cd above): $PWD may hold a space, and $CLI is word-split.
CLI="dotnet src/ChessBruteforcer.Cli/bin/Release/net8.0/ChessBruteforcer.Cli.dll"

one_v_one="KQvKQ KQvKR KQvKB KQvKN KQvKP KRvKR KRvKB KRvKN KRvKP KBvKB KBvKN KBvKP KNvKN KNvKP KPvKP"
two_v_zero="KQQvK KQRvK KQBvK KQNvK KQPvK KRRvK KRBvK KRNvK KRPvK KBBvK KBNvK KBPvK KNNvK KNPvK KPPvK"

mkdir -p tables
for material in $one_v_one $two_v_zero; do
  if [ -f "tables/$material.cbt" ]; then
    echo "$material: already solved"
    continue
  fi
  start=$(date +%s)
  summary=$($CLI solve "$material")
  echo "${summary%%$'\n'*}"
  echo "  ($(( $(date +%s) - start ))s)"
done
echo "done: $(ls tables/*.cbt | wc -l) tables in ./tables"

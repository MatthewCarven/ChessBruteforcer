#!/usr/bin/env bash
# Build a snapshot of the engine and play it against an opponent.
#
#   scripts/match.sh snapshot v0.3            # copy the current engine build to engines/v0.3
#   scripts/match.sh vs-stockfish v0.3 1800   # 20 games v Stockfish limited to 1800 Elo, 10+0.1
#   scripts/match.sh vs-engine v0.3 v0.2      # 40 games between two snapshots, stopping early by SPRT
#
# Snapshots live in engines/ (ignored by git). Results go to the terminal and to
# engines/<name>-vs-<opponent>.pgn; record the summary line in MATCHES.md.
set -euo pipefail
cd "$(dirname "$0")/.."
TABLES=${TABLES:-tables}
MATCH="dotnet src/ChessBruteforcer.Match/bin/Release/net8.0/ChessBruteforcer.Match.dll"
engine_cmd() { echo "dotnet engines/$1/ChessBruteforcer.Engine.dll --tables $TABLES"; }

case "${1:-}" in
  snapshot)
    dotnet build -c Release -v q src/ChessBruteforcer.Engine src/ChessBruteforcer.Match
    rm -rf "engines/$2" && mkdir -p engines && cp -r src/ChessBruteforcer.Engine/bin/Release/net8.0 "engines/$2"
    echo "engines/$2 ready" ;;
  vs-stockfish)
    $MATCH --engine name="cb-$2" cmd="$(engine_cmd "$2")" \
           --engine name="sf$3" cmd="${STOCKFISH:-/usr/games/stockfish}" option.UCI_LimitStrength=true option.UCI_Elo="$3" \
           --games "${GAMES:-20}" --tc "${TC:-10+0.1}" --concurrency "${CONCURRENCY:-2}" \
           --tables "$TABLES" --pgn "engines/$2-vs-sf$3.pgn" ;;
  vs-engine)
    $MATCH --engine name="cb-$2" cmd="$(engine_cmd "$2")" --engine name="cb-$3" cmd="$(engine_cmd "$3")" \
           --games "${GAMES:-40}" --tc "${TC:-10+0.1}" --concurrency "${CONCURRENCY:-2}" \
           --tables "$TABLES" --sprt 0 10 --pgn "engines/$2-vs-$3.pgn" ;;
  *)
    sed -n '2,10p' "$0"; exit 1 ;;
esac

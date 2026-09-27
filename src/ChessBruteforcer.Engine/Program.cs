using System.Diagnostics;
using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Engine;
using ChessBruteforcer.Core.Game;

// ChessBruteforcer as a UCI engine: point a GUI or match runner at this
// executable.  "bench [depth]" searches a fixed set of positions and prints
// the node count and speed, which is how changes are compared for speed.
// An optional second argument (or --tables <dir>) enables endgame tables.

string? tables = null;
int tablesAt = Array.IndexOf(args, "--tables");
if (tablesAt >= 0 && tablesAt + 1 < args.Length)
    tables = args[tablesAt + 1];

if (args.Length > 0 && args[0] == "bench")
{
    int depth = args.Length > 1 && int.TryParse(args[1], out int d) ? d : 8;
    return Bench(depth);
}

if (args.Length > 0 && args[0] == "selfplay")
{
    int games = args.Length > 1 && int.TryParse(args[1], out int g) ? g : 4;
    int moveTime = args.Length > 2 && int.TryParse(args[2], out int t) ? t : 100;
    return SelfPlay.Run(games, moveTime, tables);
}

new UciEngine(Console.Out, tables).Run(Console.In);
return 0;

static int Bench(int depth)
{
    string[] positions =
    {
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
        "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
        "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
        "r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
    };
    long total = 0;
    var clock = Stopwatch.StartNew();
    foreach (string fen in positions)
    {
        var search = new Search(new TranspositionTable(16));
        var result = search.Run(Position.FromFen(fen), new SearchLimits { Depth = depth });
        Console.WriteLine($"{result.BestMove?.ToUci(),-6} score {result.Score,6}  nodes {result.Nodes,12:N0}  {fen}");
        total += result.Nodes;
    }
    double seconds = clock.Elapsed.TotalSeconds;
    Console.WriteLine($"bench depth {depth}: {total:N0} nodes in {seconds:0.00}s = {total / seconds / 1000:0} knps");
    return 0;
}

/// <summary>
/// The engine against itself from a handful of openings, applying every way
/// a game can end.  A robustness check (no crashes, no illegal moves, games
/// finish) and the seed of the local match runner.
/// </summary>
static class SelfPlay
{
    private static readonly string[] Openings =
    {
        "e2e4 e7e5", "d2d4 d7d5", "e2e4 c7c5", "c2c4 e7e5", "d2d4 g8f6 c2c4 e7e6", "e2e4 e7e6",
        "g1f3 d7d5", "e2e4 c7c6",
    };

    public static int Run(int games, int moveTimeMs, string? tables)
    {
        var tally = new Dictionary<string, int>();
        for (int game = 0; game < games; game++)
        {
            string opening = Openings[game % Openings.Length];
            var (result, reason, moves) = Play(opening, moveTimeMs, tables);
            tally[result] = tally.GetValueOrDefault(result) + 1;
            Console.WriteLine($"game {game + 1}: {result} ({reason}), {moves.Count} plies");
            Console.WriteLine($"  {string.Join(' ', moves)}");
        }
        Console.WriteLine(string.Join(", ", tally.Select(kv => $"{kv.Key}: {kv.Value}")));
        return 0;
    }

    private static (string Result, string Reason, List<string> Moves) Play(string opening, int moveTimeMs, string? tables)
    {
        var position = Position.Start();
        var hashes = new List<ulong>();
        var moves = new List<string>();
        var tablebase = tables is null ? null : new Tablebase(tables) { SolveMissing = false, LoadOnDemand = false };
        tablebase?.Preload();
        var search = new Search(new TranspositionTable(32), tablebase);

        void Play(Move move)
        {
            hashes.Add(position.Hash);
            moves.Add(move.ToUci());
            position.MakeMove(move);
        }

        foreach (string uci in opening.Split(' '))
            Play(position.ParseUciMove(uci)!.Value);

        while (true)
        {
            switch (position.Status())
            {
                case GameStatus.Checkmate:
                    return (position.SideToMove == Colour.White ? "0-1" : "1-0", "checkmate", moves);
                case GameStatus.Stalemate:
                    return ("1/2-1/2", "stalemate", moves);
            }
            if (position.HalfmoveClock >= 100)
                return ("1/2-1/2", "50-move rule", moves);
            if (hashes.Count(h => h == position.Hash) >= 2)
                return ("1/2-1/2", "threefold repetition", moves);
            if (InsufficientMaterial(position))
                return ("1/2-1/2", "insufficient material", moves);
            if (moves.Count >= 400)
                return ("1/2-1/2", "move limit", moves);

            var result = search.Run(position, new SearchLimits { MoveTimeMs = moveTimeMs }, hashes);
            var move = result.BestMove ?? throw new InvalidOperationException("No move in a live position.");
            if (position.ParseUciMove(move.ToUci()) is null)
                throw new InvalidOperationException($"Illegal move {move} in {position.ToFen()}");
            Play(move);
        }
    }

    /// <summary>Only kings, or king and one minor piece against a bare king.</summary>
    private static bool InsufficientMaterial(Position position)
    {
        int minors = 0;
        for (int square = 0; square < 64; square++)
        {
            switch (position[square].Type)
            {
                case PieceType.None or PieceType.King: break;
                case PieceType.Knight or PieceType.Bishop: minors++; break;
                default: return false;
            }
        }
        return minors <= 1;
    }
}

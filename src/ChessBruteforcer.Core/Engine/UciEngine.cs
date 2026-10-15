using System.Globalization;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Engine;

/// <summary>
/// The Universal Chess Interface: the text protocol every chess GUI and
/// match runner (cutechess-cli, fastchess, lichess-bot) uses to drive an
/// engine.  Commands arrive one per line; the search runs on a background
/// thread so "stop", "ponderhit" and "isready" are answered while it thinks.
/// </summary>
public sealed class UciEngine
{
    public const string Name = "ChessBruteforcer 0.1";
    public const string Author = "Matthew Carven";

    private readonly TextWriter _output;
    private readonly object _outputLock = new();
    private readonly TranspositionTable _table = new(64);
    private Tablebase? _tablebase;
    private Search _search;
    private Thread? _searchThread;

    private Position _position = Position.Start();
    private int _moveOverheadMs = 50;
    private int _threads = 1;
    private ParallelMode _split = ParallelMode.LazySmp;
    private bool _tablesLoaded;
    private List<ulong> _previousHashes = new();

    public UciEngine(TextWriter output, string? tablePath = null)
    {
        _output = output;
        SetTablePath(tablePath);
        _search = new Search(_table, _tablebase);
    }

    public Position Position => _position;

    /// <summary>Read commands until "quit" or end of input.</summary>
    public void Run(TextReader input)
    {
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (!Handle(line))
                break;
        }
        StopSearch();
    }

    /// <summary>Handle one command; false means quit.</summary>
    public bool Handle(string line)
    {
        string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return true;
        try
        {
            switch (words[0])
            {
                case "uci":
                    Send($"id name {Name}");
                    Send($"id author {Author}");
                    Send("option name Hash type spin default 64 min 1 max 4096");
                    Send("option name EndgameTables type string default <empty>");
                    Send("option name Move Overhead type spin default 50 min 0 max 5000");
                    Send("option name EvalFile type string default <empty>");
                    Send("option name Threads type spin default 1 min 1 max 64");
                    Send("option name ThreadSplit type combo default LazySMP var LazySMP var Ranges");
                    Send("option name Ponder type check default false");
                    Send("uciok");
                    break;
                case "isready":
                    EnsureTablesLoaded();
                    Send("readyok");
                    break;
                case "ucinewgame":
                    StopSearch();
                    _table.Clear();
                    break;
                case "setoption":
                    SetOption(words);
                    break;
                case "position":
                    StopSearch();
                    SetPosition(words);
                    break;
                case "go":
                    StopSearch();
                    EnsureTablesLoaded();
                    StartSearch(ParseGo(words) with { MoveOverheadMs = _moveOverheadMs });
                    break;
                case "ponderhit":
                    // The opponent played the move we were pondering on: the search goes on, on our clock.
                    _search.PonderHit();
                    break;
                case "stop":
                    StopSearch();
                    break;
                case "quit":
                    StopSearch();
                    return false;
                case "d":
                    Send(_position.ToPackedBoard().ToDiagram());
                    Send($"Fen: {_position.ToFen()}");
                    Send($"Key: {_position.Hash:X16}");
                    break;
                case "eval":
                    Send($"eval {Evaluation.Evaluate(_position)} cp (side to move)");
                    break;
                default:
                    Send($"info string unknown command: {words[0]}");
                    break;
            }
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException)
        {
            Send($"info string error: {e.Message}");
        }
        return true;
    }

    /// <summary>Wait for a running search to finish on its own (for tests and scripts; a "go ponder" search never does).</summary>
    public void WaitForSearch() => _searchThread?.Join();

    private void SetOption(string[] words)
    {
        // setoption name <name words...> [value <value words...>]
        int nameAt = Array.IndexOf(words, "name");
        int valueAt = Array.IndexOf(words, "value");
        if (nameAt < 0)
            return;
        string name = string.Join(' ', words[(nameAt + 1)..(valueAt < 0 ? words.Length : valueAt)]);
        string value = valueAt < 0 ? "" : string.Join(' ', words[(valueAt + 1)..]);
        StopSearch();
        switch (name.ToLowerInvariant())
        {
            case "hash":
                _table.Resize(int.Parse(value, CultureInfo.InvariantCulture));
                break;
            case "move overhead":
                _moveOverheadMs = int.Parse(value, CultureInfo.InvariantCulture);
                break;
            case "threads":
                _threads = Math.Clamp(int.Parse(value, CultureInfo.InvariantCulture), 1, 64);
                break;
            case "threadsplit":
                // LazySMP: every thread searches the whole tree, sharing the table.  Ranges (Matthew's
                // "ranges of possibility"): each thread searches with its own slice of the score range.
                _split = value.ToLowerInvariant() switch
                {
                    "lazysmp" => ParallelMode.LazySmp,
                    "ranges" => ParallelMode.Ranges,
                    _ => throw new ArgumentException($"ThreadSplit is LazySMP or Ranges, not {value}."),
                };
                break;
            case "ponder":
                // Only tells us the GUI may send "go ponder", which works either way.  (UCI lets an
                // engine plan its time differently when it may ponder; this one doesn't, yet.)
                break;
            case "evalfile":
                // Tuned evaluation numbers (EvalParams text); empty for the built-in ones. Process-wide.
                Evaluation.Parameters = value is "" or "<empty>" ? EvalParams.Original() : EvalParams.Load(value);
                _table.Clear();
                Send(value is "" or "<empty>" ? "info string evaluation: built in" : $"info string evaluation from {value}");
                break;
            case "endgametables":
                SetTablePath(value is "" or "<empty>" ? null : value);
                _search = new Search(_table, _tablebase);
                Send(_tablebase is null
                    ? "info string endgame tables off"
                    : $"info string endgame tables from {_tablebase.Directory}");
                break;
            default:
                Send($"info string unknown option: {name}");
                break;
        }
    }

    private void SetTablePath(string? path)
    {
        _tablebase = path is not null && System.IO.Directory.Exists(path)
            ? new Tablebase(path) { SolveMissing = false, LoadOnDemand = false }
            : null;
        _tablesLoaded = false;
    }

    /// <summary>
    /// Endgame tables are read into memory once, on "isready" (the UCI
    /// moment for slow setup), so the search never waits for the disk.
    /// </summary>
    private void EnsureTablesLoaded()
    {
        if (_tablesLoaded || _tablebase is null)
            return;
        var (tables, bytes, onDisk) = _tablebase.Preload();
        _tablesLoaded = true;
        Send($"info string loaded {tables} endgame tables ({bytes / (1024 * 1024)} MB in memory, " +
             $"{onDisk} read from disk as probed)");
    }

    /// <summary>position [startpos | fen &lt;fen&gt;] [moves &lt;move&gt;...]</summary>
    private void SetPosition(string[] words)
    {
        int movesAt = Array.IndexOf(words, "moves");
        int end = movesAt < 0 ? words.Length : movesAt;
        Position position;
        if (words.Length > 1 && words[1] == "startpos")
            position = Position.Start();
        else if (words.Length > 2 && words[1] == "fen")
            position = Position.FromFen(string.Join(' ', words[2..end]));
        else
            throw new FormatException("position needs 'startpos' or 'fen <fen>'.");

        var hashes = new List<ulong>();
        if (movesAt >= 0)
        {
            foreach (string text in words[(movesAt + 1)..])
            {
                var move = position.ParseUciMove(text)
                           ?? throw new FormatException($"Illegal move {text} in {position.ToFen()}.");
                hashes.Add(position.Hash);
                position.MakeMove(move);
            }
        }
        _position = position;
        _previousHashes = hashes;
    }

    private static SearchLimits ParseGo(string[] words)
    {
        var limits = new SearchLimits();
        for (int i = 1; i < words.Length; i++)
        {
            string next = i + 1 < words.Length ? words[i + 1] : "0";
            switch (words[i])
            {
                case "infinite": limits = limits with { Infinite = true }; break;
                case "ponder": limits = limits with { Ponder = true }; break;
                case "depth": limits = limits with { Depth = int.Parse(next) }; i++; break;
                case "nodes": limits = limits with { Nodes = long.Parse(next) }; i++; break;
                case "movetime": limits = limits with { MoveTimeMs = int.Parse(next) }; i++; break;
                case "wtime": limits = limits with { WhiteTimeMs = int.Parse(next) }; i++; break;
                case "btime": limits = limits with { BlackTimeMs = int.Parse(next) }; i++; break;
                case "winc": limits = limits with { WhiteIncrementMs = int.Parse(next) }; i++; break;
                case "binc": limits = limits with { BlackIncrementMs = int.Parse(next) }; i++; break;
                case "movestogo": limits = limits with { MovesToGo = int.Parse(next) }; i++; break;
            }
        }
        return limits;
    }

    private void StartSearch(SearchLimits limits)
    {
        // The search works on its own copy so "position" can't change the board under it.
        var root = Position.FromFen(_position.ToFen());
        var history = _previousHashes.ToList();
        var search = _search;
        search.Threads = _threads;
        search.Split = _split;
        // "go" returns once the search is ready, so a "stop" or "ponderhit" straight after it isn't lost.
        var started = new ManualResetEventSlim();
        _searchThread = new Thread(() =>
        {
            try
            {
                var result = search.Run(root, limits, history, ReportInfo, started.Set);
                if (result.BestMove is not Move move)
                    Send("bestmove 0000");
                else if (PonderMove(root, result) is Move reply)
                    Send($"bestmove {move.ToUci()} ponder {reply.ToUci()}");
                else
                    Send($"bestmove {move.ToUci()}");
            }
            finally
            {
                started.Set();
            }
        })
        {
            IsBackground = true,
            Name = "search",
        };
        _searchThread.Start();
        started.Wait();
    }

    /// <summary>
    /// The reply we expect to our best move, for "bestmove ... ponder": the
    /// principal variation's second move, or where the line stops at our move
    /// (cut short by the table, or an endgame-table move) the table's move in
    /// the position after it.  Null when there's no legal one.
    /// </summary>
    private Move? PonderMove(Position root, SearchResult result)
    {
        var pv = result.PrincipalVariation;
        var after = Position.FromFen(root.ToFen());
        after.MakeMove(result.BestMove!.Value);
        Move? reply = pv.Count > 1 && pv[0] == result.BestMove ? pv[1]
                    : _table.TryGet(after.Hash, out var entry) ? entry.Move
                    : null;
        return reply is Move candidate ? after.ParseUciMove(candidate.ToUci()) : null;
    }

    private void StopSearch()
    {
        if (_searchThread is null)
            return;
        _search.Stop();
        _searchThread.Join();
        _searchThread = null;
    }

    private void ReportInfo(SearchInfo info)
    {
        long ms = Math.Max(1, (long)info.Elapsed.TotalMilliseconds);
        string score = info.MateIn is int mate ? $"mate {mate}" : $"cp {info.Score}";
        string pv = string.Join(' ', info.PrincipalVariation.Select(m => m.ToUci()));
        Send($"info depth {info.Depth} score {score} nodes {info.Nodes} nps {info.Nodes * 1000 / ms} " +
             $"time {ms} hashfull {_table.Usage()} pv {pv}");
    }

    private void Send(string text)
    {
        lock (_outputLock)
        {
            _output.WriteLine(text);
            _output.Flush();
        }
    }
}

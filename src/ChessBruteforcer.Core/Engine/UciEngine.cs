using System.Globalization;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Engine;

/// <summary>
/// The Universal Chess Interface: the text protocol every chess GUI and
/// match runner (cutechess-cli, fastchess, lichess-bot) uses to drive an
/// engine.  Commands arrive one per line; the search runs on a background
/// thread so "stop" and "isready" are answered while it thinks.
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
                    Send("uciok");
                    break;
                case "isready":
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
                    StartSearch(ParseGo(words));
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

    /// <summary>Wait for a running search to finish on its own (for tests and scripts).</summary>
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
            ? new Tablebase(path) { SolveMissing = false }
            : null;
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
        _searchThread = new Thread(() =>
        {
            var result = search.Run(root, limits, history, ReportInfo);
            Send($"bestmove {(result.BestMove is Move move ? move.ToUci() : "0000")}");
        })
        {
            IsBackground = true,
            Name = "search",
        };
        _searchThread.Start();
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

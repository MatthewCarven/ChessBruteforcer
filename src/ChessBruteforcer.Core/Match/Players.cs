using System.Collections.Concurrent;
using System.Diagnostics;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Match;

/// <summary>What a player is told before choosing a move.</summary>
public sealed record MoveRequest(
    string StartFen, IReadOnlyList<string> Moves, Position Position,
    int WhiteTimeMs, int BlackTimeMs, int WhiteIncrementMs, int BlackIncrementMs, int? MoveTimeMs);

/// <summary>Anything that can play: an engine process, or a scripted player in tests.</summary>
public interface IPlayer : IDisposable
{
    string Name { get; }

    void NewGame();

    /// <summary>The chosen move in UCI notation (null if the player failed to answer).</summary>
    string? ChooseMove(MoveRequest request, TimeSpan timeout);
}

/// <summary>How to start one engine: a command line plus UCI options to set.</summary>
public sealed record EngineSpec(string Name, string Command, IReadOnlyDictionary<string, string> Options)
{
    /// <summary>Parse "name=X cmd=... option.Hash=64" style settings.</summary>
    public static EngineSpec Parse(IEnumerable<string> settings)
    {
        string? name = null, command = null;
        var options = new Dictionary<string, string>();
        foreach (string setting in settings)
        {
            int eq = setting.IndexOf('=');
            if (eq < 0)
                throw new FormatException($"Engine setting '{setting}' should be key=value.");
            string key = setting[..eq], value = setting[(eq + 1)..];
            if (key == "name") name = value;
            else if (key == "cmd") command = value;
            else if (key.StartsWith("option.")) options[key["option.".Length..]] = value;
            else throw new FormatException($"Unknown engine setting '{key}'.");
        }
        if (command is null)
            throw new FormatException("Each engine needs cmd=<command>.");
        return new EngineSpec(name ?? Path.GetFileNameWithoutExtension(command.Split(' ')[0]), command, options);
    }
}

/// <summary>An engine running as a separate process, spoken to over UCI.</summary>
public sealed class UciPlayer : IPlayer
{
    private readonly Process _process;
    private readonly BlockingCollection<string> _lines = new();

    public string Name { get; }

    public UciPlayer(EngineSpec spec)
    {
        Name = spec.Name;
        string[] parts = spec.Command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var start = new ProcessStartInfo(parts[0], parts.Length > 1 ? parts[1] : "")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        _process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {spec.Command}.");
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) _lines.CompleteAdding();
            else if (!_lines.IsAddingCompleted) _lines.Add(e.Data);
        };
        _process.BeginOutputReadLine();

        Send("uci");
        WaitFor("uciok", TimeSpan.FromSeconds(20));
        foreach (var (option, value) in spec.Options)
            Send($"setoption name {option} value {value}");
        Ready();
    }

    public void NewGame()
    {
        Send("ucinewgame");
        Ready();
    }

    public string? ChooseMove(MoveRequest request, TimeSpan timeout)
    {
        string position = request.StartFen == Fen.StartPosition ? "startpos" : $"fen {request.StartFen}";
        string moves = request.Moves.Count > 0 ? " moves " + string.Join(' ', request.Moves) : "";
        Send($"position {position}{moves}");
        Send(request.MoveTimeMs is int moveTime
            ? $"go movetime {moveTime}"
            : $"go wtime {request.WhiteTimeMs} btime {request.BlackTimeMs} " +
              $"winc {request.WhiteIncrementMs} binc {request.BlackIncrementMs}");
        string? line = WaitFor("bestmove", timeout);
        if (line is null)
            return null;
        string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1 ? words[1] : null;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                Send("quit");
                if (!_process.WaitForExit(2000))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        _process.Dispose();
    }

    private void Ready()
    {
        Send("isready");
        // Generous: an engine may load endgame tables here (a slow disk can take a while).
        WaitFor("readyok", TimeSpan.FromSeconds(180));
    }

    private void Send(string command)
    {
        _process.StandardInput.WriteLine(command);
        _process.StandardInput.Flush();
    }

    /// <summary>The first line starting with <paramref name="prefix"/>, or null on timeout or exit.</summary>
    private string? WaitFor(string prefix, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            var remaining = timeout - deadline.Elapsed;
            if (!_lines.TryTake(out string? line, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero))
                return null;
            if (line.StartsWith(prefix))
                return line;
        }
        return null;
    }
}

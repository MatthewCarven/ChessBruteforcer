namespace ChessBruteforcer.Core.Match;

/// <summary>
/// How far an engine's searches went in a match: the last "info depth ...
/// nodes ... time ..." line before each bestmove, summed.  Depth is the mean
/// per move; speed is all nodes over all search time, so long searches weigh
/// more, as they do on the clock.
/// </summary>
public sealed record SearchTally(int Moves, long Depths, long Nodes, long Milliseconds)
{
    public static readonly SearchTally Empty = new(0, 0, 0, 0);

    public double MeanDepth => Moves == 0 ? 0 : (double)Depths / Moves;

    /// <summary>Thousands of nodes a second (nodes per millisecond).</summary>
    public double Knps => Milliseconds == 0 ? 0 : (double)Nodes / Milliseconds;

    /// <summary>This tally plus one search, read from its last info line; unchanged if the line lacks depth, nodes or time.</summary>
    public SearchTally Add(string infoLine)
    {
        long? depth = null, nodes = null, time = null;
        string[] words = infoLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i + 1 < words.Length && words[i] != "pv"; i++)
        {
            if (!long.TryParse(words[i + 1], out long value))
                continue;
            switch (words[i])
            {
                case "depth": depth = value; break;
                case "nodes": nodes = value; break;
                case "time": time = value; break;
            }
        }
        return depth is long d && nodes is long n && time is long t
            ? new SearchTally(Moves + 1, Depths + d, Nodes + n, Milliseconds + t)
            : this;
    }

    public SearchTally Add(SearchTally other) =>
        new(Moves + other.Moves, Depths + other.Depths, Nodes + other.Nodes, Milliseconds + other.Milliseconds);

    public override string ToString() => $"depth {MeanDepth:0.0}, {Knps:N0} knps ({Moves} moves)";
}

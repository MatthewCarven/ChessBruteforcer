namespace ChessBruteforcer.Core.Endgame;

/// <summary>
/// Every read of a compressed table, recorded for a bench (bench-threads
/// --trace), so that other caches can be tried on the same reads offline
/// (scripts/block-cache-trace.py): the table, the value's index, the thread,
/// and for a DTZ read whether the mate distance read just before had already
/// settled what the 50-move rule makes of the position (see
/// <see cref="Tablebase.TryProbeWithClock"/>).  Off unless started; while
/// off, a read costs one test of a static field.
///
/// A read is one long: the index in bits 0-39, the table in 40-51, the
/// thread (from 1, in the order threads first read) in 52-57, and bit 58
/// set for a DTZ read the mate distance had settled.
/// </summary>
public static class TableReadTrace
{
    /// <summary>A compressed table as a trace numbers it.</summary>
    public readonly record struct TableInfo(int Id, string Material, bool Dtz, long Size, int ValuesPerBlock);

    private static volatile bool s_on;
    private static readonly object s_lock = new();
    private static readonly List<TableInfo> s_tables = new();
    private static readonly List<List<long>> s_lists = new();
    [ThreadStatic] private static List<long>? t_reads;
    [ThreadStatic] private static long t_thread;
    [ThreadStatic] private static bool t_settled;

    public static bool On => s_on;

    /// <summary>Every compressed table opened so far.</summary>
    public static IReadOnlyList<TableInfo> Tables
    {
        get { lock (s_lock) return s_tables.ToList(); }
    }

    public static void Start() => s_on = true;

    public static void Stop() => s_on = false;

    internal static int Register(Material material, bool dtz, long size, int valuesPerBlock)
    {
        lock (s_lock)
        {
            int id = s_tables.Count;
            s_tables.Add(new TableInfo(id, material.ToString(), dtz, size, valuesPerBlock));
            return id;
        }
    }

    /// <summary>Whether the DTZ reads this thread makes next were settled by the mate distance (set around them).</summary>
    internal static bool Settled
    {
        set => t_settled = value;
    }

    private static long s_contradicted;

    /// <summary>Probes the mate distance settled whose DTZ said otherwise: there should be none.</summary>
    public static long Contradictions => Interlocked.Read(ref s_contradicted);

    internal static void Contradicted() => Interlocked.Increment(ref s_contradicted);

    internal static void Read(int table, long index, bool dtz)
    {
        var reads = t_reads;
        if (reads is null)
        {
            reads = t_reads = new List<long>(1 << 16);
            lock (s_lock)
            {
                s_lists.Add(reads);
                t_thread = s_lists.Count;
            }
        }
        reads.Add(index | (long)table << 40 | t_thread << 52 | (dtz && t_settled ? 1L << 58 : 0));
    }

    /// <summary>The reads recorded since the last call, thread by thread (each thread's in order), and forget them.  Call it with no search running.</summary>
    public static long[] Take()
    {
        lock (s_lock)
        {
            var all = new long[s_lists.Sum(list => list.Count)];
            int at = 0;
            foreach (var list in s_lists)
            {
                list.CopyTo(all, at);
                at += list.Count;
                list.Clear();
            }
            return all;
        }
    }
}

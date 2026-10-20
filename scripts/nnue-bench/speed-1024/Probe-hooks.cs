namespace ChessBruteforcer.Core.Engine.Nnue;

/// <summary>
/// Measurement hooks for the scratchpad probe (never committed). Mode: 0 normal; 1 record every network
/// evaluation's value; 2 replay the recorded values without the forward pass; 3 the forward pass, checked
/// against the replayed value. SkipUpdates leaves the accumulators alone (only with replay). Counting
/// counts pushes, evaluations, illegal makes and pushes no evaluation ever needed.
/// </summary>
public static class Probe
{
    public static int Mode;
    public static bool SkipUpdates;
    public static bool Counting;

    public static int[] Values = new int[1 << 20];
    public static int Count;
    public static int Next;
    public static long Mismatches;

    // The ideal lazy update: the counting run notes, push by push, whether any evaluation needed it;
    // with IdealLazy a later run of the identical tree skips the work of each push that wasn't needed.
    public static bool IdealLazy;
    public static bool[] UsedBits = new bool[1 << 23];
    public static int PushSeq;

    public static void NoteUsed(int seq, bool used)
    {
        if (seq >= UsedBits.Length)
            Array.Resize(ref UsedBits, Math.Max(UsedBits.Length * 2, seq + 1));
        UsedBits[seq] = used;
    }

    public static long Quiet, Capture, Castle, Pops, BottomRefreshes, Evals, Unused, UnusedLeaf, Illegal,
                       NullEvals, QsEvals, NegamaxNodes, QsNodes, ChainLength, ChainEvals;

    public static void Record(int value)
    {
        if (Count == Values.Length)
            Array.Resize(ref Values, Values.Length * 2);
        Values[Count++] = value;
    }

    // The update log, for the kernel lab: records of 10 ints, [parent slot, kind (0 quiet, 1 capture,
    // 2 castle, 3 evaluation), white's add1 add2 sub1 sub2, black's add1 add2 sub1 sub2] (-1 unused; an
    // evaluation's record holds its slot and the side to move in the first add).
    public static bool Logging;
    public static int[] Log = Array.Empty<int>();
    public static int LogCount;
    public static long LogSkip;   // records to pass over first (so the log comes from the deeper iterations)

    public static void LogRecord(int slot, int kind, int a1w, int a2w, int s1w, int s2w, int a1b, int a2b, int s1b, int s2b)
    {
        if (LogSkip > 0 && kind != 5)
        {
            LogSkip--;
            return;
        }
        if (LogCount + 10 > Log.Length)
        {
            Logging = false;
            return;
        }
        var log = Log.AsSpan(LogCount, 10);
        log[0] = slot; log[1] = kind;
        log[2] = a1w; log[3] = a2w; log[4] = s1w; log[5] = s2w;
        log[6] = a1b; log[7] = a2b; log[8] = s1b; log[9] = s2b;
        LogCount += 10;
    }

    public static void ResetCounters() =>
        Quiet = Capture = Castle = Pops = BottomRefreshes = Evals = Unused = UnusedLeaf = Illegal =
            NullEvals = QsEvals = NegamaxNodes = QsNodes = ChainLength = ChainEvals = 0;
}

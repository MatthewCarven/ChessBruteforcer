#:property TargetFramework=net8.0
#:property PublishAot=false
#:property AllowUnsafeBlocks=true
// The kernel lab: the accumulator updates and forward passes of a real search (probelog.cs's log),
// replayed with each kernel variant on the net's own weights, apart from the rest of the search.
// Variants: 256-bit (as the engine's Kernels.cs and Network.cs) or 512-bit vectors, on weights and a
// stack placed at a chosen offset from a 64-byte boundary (0 = aligned), or in plain .NET arrays as the
// engine allocates them. Every variant must give the same checksum. Arms rotate round by round.
//   dotnet run -c Release lab.cs <net .nnue> <log .bin> <rounds>
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
byte[] file = File.ReadAllBytes(args[0]);
int H = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16));
int QA = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(20));
short[] ReadShorts(int at, int count) => MemoryMarshal.Cast<byte, short>(file.AsSpan(at, 2 * count)).ToArray();
var weights = ReadShorts(32, 768 * H);
var biases = ReadShorts(32 + 2 * 768 * H, H);
var outputs = ReadShorts(32 + 2 * 768 * H + 2 * H, 2 * H);
int[] log = MemoryMarshal.Cast<byte, int>(File.ReadAllBytes(args[1])).ToArray();
int rounds = int.Parse(args[2]);
int records = log.Length / 10, pushes = 0, evals = 0, maxSlot = 0;
for (int r = 0; r < records; r++)
{
    int kind = log[r * 10 + 1];
    if (kind <= 2) pushes++;
    if (kind == 3) evals++;
    maxSlot = Math.Max(maxSlot, log[r * 10] + 1);
}
Console.WriteLine($"H {H}, QA {QA}; {records:N0} records: {pushes:N0} pushes, {evals:N0} evaluations; deepest slot {maxSlot}; " +
                  $"Avx2 {Avx2.IsSupported}, Avx512BW {Avx512BW.IsSupported}, Vector512 accelerated {Vector512.IsHardwareAccelerated}");
int slots = maxSlot + 2;

// ---- memory: native at an offset from a 64-byte boundary, or plain arrays (pinned while in use) ----
unsafe
{
    var placed = new Dictionary<string, (nint W, nint S, nint O)>();
    foreach (int offset in new[] { 0, 16, 32 })
    {
        nint Alloc(int shorts, short[]? init)
        {
            nint p = (nint)NativeMemory.AlignedAlloc((nuint)(2 * shorts + 128), 64) + offset;
            if (init is not null) init.AsSpan().CopyTo(new Span<short>((void*)p, shorts));
            else new Span<short>((void*)p, shorts).Clear();
            return p;
        }
        placed[$"@{offset}"] = (Alloc(768 * H, weights), Alloc(slots * 2 * H, null), Alloc(2 * H, outputs));
    }
    var plainStack = new short[slots * 2 * H];
    var hW = GCHandle.Alloc(weights, GCHandleType.Pinned);
    var hS = GCHandle.Alloc(plainStack, GCHandleType.Pinned);
    var hO = GCHandle.Alloc(outputs, GCHandleType.Pinned);
    placed["plain"] = (hW.AddrOfPinnedObject(), hS.AddrOfPinnedObject(), hO.AddrOfPinnedObject());
    Console.WriteLine($"plain .NET arrays as the engine has them: weights at {(long)placed["plain"].W % 64}, stack at {(long)placed["plain"].S % 64}, " +
                      $"output weights at {(long)placed["plain"].O % 64} bytes past a 64-byte boundary");

    var arms = new List<(string Name, int Bits, string Place)>();
    foreach (int bits in new[] { 256, 512 })
        foreach (string place in new[] { "plain", "@0", "@16", "@32" })
            arms.Add(($"{bits}{place}", bits, place));

    var pushNs = arms.ToDictionary(a => a.Name, _ => new List<double>());
    var evalNs = arms.ToDictionary(a => a.Name, _ => new List<double>());
    var sums = new Dictionary<string, long>();
    for (int round = 0; round <= rounds; round++)
    {
        for (int i = 0; i < arms.Count; i++)
        {
            var arm = arms[(i + round) % arms.Count];
            var (w, s, o) = placed[arm.Place];
            long sum = 0;
            double t0 = ThreadNs();
            Replay((short*)w, (short*)s, (short*)o, arm.Bits, doPushes: true, doEvals: false, ref sum);
            double t1 = ThreadNs();
            Replay((short*)w, (short*)s, (short*)o, arm.Bits, doPushes: false, doEvals: true, ref sum);
            double tPush = t1 - t0, tEval = ThreadNs() - t1;
            long check = 0;
            Replay((short*)w, (short*)s, (short*)o, arm.Bits, doPushes: true, doEvals: true, ref check);
            if (sums.TryGetValue(arm.Name, out long before) && before != check) throw new Exception($"{arm.Name}: checksum moved");
            sums[arm.Name] = check;
            if (round > 0)
            {
                pushNs[arm.Name].Add(tPush / pushes);
                evalNs[arm.Name].Add(tEval / evals);
            }
        }
        Console.Error.WriteLine($"round {round} done {DateTime.Now:HH:mm:ss}");
    }
    if (sums.Values.Distinct().Count() != 1)
        throw new Exception("variants disagree: " + string.Join(", ", sums.Select(kv => $"{kv.Key} {kv.Value}")));

    double Median(List<double> v) { var t = v.OrderBy(x => x).ToList(); return t.Count % 2 == 1 ? t[t.Count / 2] : (t[t.Count / 2 - 1] + t[t.Count / 2]) / 2; }
    Console.WriteLine($"all variants agree (checksum {sums.Values.First()}); medians of {rounds} rounds");
    Console.WriteLine("variant      ns a push   vs 256plain   ns an evaluation   vs 256plain");
    double basePush = Median(pushNs["256plain"]), baseEval = Median(evalNs["256plain"]);
    foreach (var arm in arms)
        Console.WriteLine($"{arm.Name,-11} {Median(pushNs[arm.Name]),10:0.0} {100 * Median(pushNs[arm.Name]) / basePush - 100,12:+0.0;-0.0}% {Median(evalNs[arm.Name]),18:0.0} {100 * Median(evalNs[arm.Name]) / baseEval - 100,12:+0.0;-0.0}%");
}

// This thread's own CPU time (TSC ticks at 2.496 GHz): time spent waiting for the CPU doesn't count.
double ThreadNs()
{
    Native.QueryThreadCycleTime(Native.GetCurrentThread(), out ulong cycles);
    return cycles / 2.496;
}

// ---- the replay ----
unsafe void Replay(short* w, short* stack, short* ow, int bits, bool doPushes, bool doEvals, ref long sum)
{
    int stride = 2 * H;
    fixed (int* entries = log)
    fixed (short* b = biases)
    for (int r = 0; r < records; r++)
    {
        int* rec = entries + r * 10;
        int kind = rec[1];
        if (kind == 5)
        {
            for (int p = 0; p < 2; p++)
                Buffer.MemoryCopy(b, stack + p * H, 2 * H, 2 * H);
            continue;
        }
        short* parent = stack + rec[0] * stride;
        if (kind == 3)
        {
            if (doEvals)
            {
                short* us = rec[2] == 0 ? parent : parent + H, them = rec[2] == 0 ? parent + H : parent;
                sum += bits == 512 ? Half512(us, ow) + Half512(them, ow + H) : Half256(us, ow) + Half256(them, ow + H);
            }
            continue;
        }
        if (!doPushes)
            continue;
        short* child = parent + stride;
        for (int p = 0; p < 2; p++)
        {
            int* f = rec + 2 + 4 * p;   // add1 add2 sub1 sub2
            short* src = parent + p * H, dst = child + p * H;
            short* a1 = w + f[0] * H, s1 = w + f[2] * H;
            if (kind == 0)
            {
                if (bits == 512) AddSub512(dst, src, a1, s1); else AddSub256(dst, src, a1, s1);
            }
            else if (kind == 1)
            {
                short* s2 = w + f[3] * H;
                if (bits == 512) AddSubSub512(dst, src, a1, s1, s2); else AddSubSub256(dst, src, a1, s1, s2);
            }
            else
            {
                short* a2 = w + f[1] * H, s2 = w + f[3] * H;
                if (bits == 512) AddAddSubSub512(dst, src, a1, a2, s1, s2); else AddAddSubSub256(dst, src, a1, a2, s1, s2);
            }
        }
    }
}

unsafe void AddSub256(short* d, short* p, short* a, short* s)
{
    for (int i = 0; i < H; i += 16)
        (Vector256.Load(p + i) + Vector256.Load(a + i) - Vector256.Load(s + i)).Store(d + i);
}
unsafe void AddSubSub256(short* d, short* p, short* a, short* s1, short* s2)
{
    for (int i = 0; i < H; i += 16)
        (Vector256.Load(p + i) + Vector256.Load(a + i) - Vector256.Load(s1 + i) - Vector256.Load(s2 + i)).Store(d + i);
}
unsafe void AddAddSubSub256(short* d, short* p, short* a1, short* a2, short* s1, short* s2)
{
    for (int i = 0; i < H; i += 16)
        (Vector256.Load(p + i) + Vector256.Load(a1 + i) + Vector256.Load(a2 + i) - Vector256.Load(s1 + i) - Vector256.Load(s2 + i)).Store(d + i);
}
unsafe void AddSub512(short* d, short* p, short* a, short* s)
{
    for (int i = 0; i < H; i += 32)
        (Vector512.Load(p + i) + Vector512.Load(a + i) - Vector512.Load(s + i)).Store(d + i);
}
unsafe void AddSubSub512(short* d, short* p, short* a, short* s1, short* s2)
{
    for (int i = 0; i < H; i += 32)
        (Vector512.Load(p + i) + Vector512.Load(a + i) - Vector512.Load(s1 + i) - Vector512.Load(s2 + i)).Store(d + i);
}
unsafe void AddAddSubSub512(short* d, short* p, short* a1, short* a2, short* s1, short* s2)
{
    for (int i = 0; i < H; i += 32)
        (Vector512.Load(p + i) + Vector512.Load(a1 + i) + Vector512.Load(a2 + i) - Vector512.Load(s1 + i) - Vector512.Load(s2 + i)).Store(d + i);
}

unsafe long Half256(short* acc, short* ow)
{
    var zero = Vector256<short>.Zero;
    var qa = Vector256.Create((short)QA);
    var lanes = Vector256<int>.Zero;
    for (int i = 0; i < H; i += 16)
    {
        var v = Vector256.Min(Vector256.Max(Vector256.Load(acc + i), zero), qa);
        lanes += Avx2.MultiplyAddAdjacent(v * Vector256.Load(ow + i), v);
    }
    var (low, high) = Vector256.Widen(lanes);
    return Vector256.Sum(low + high);
}
unsafe long Half512(short* acc, short* ow)
{
    var zero = Vector512<short>.Zero;
    var qa = Vector512.Create((short)QA);
    var lanes = Vector512<int>.Zero;
    for (int i = 0; i < H; i += 32)
    {
        var v = Vector512.Min(Vector512.Max(Vector512.Load(acc + i), zero), qa);
        lanes += Avx512BW.MultiplyAddAdjacent(v * Vector512.Load(ow + i), v);
    }
    var (low, high) = Vector512.Widen(lanes);
    return Vector512.Sum(low + high);
}

static class Native
{
    [DllImport("kernel32.dll")]
    public static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryThreadCycleTime(nint thread, out ulong cycles);
}

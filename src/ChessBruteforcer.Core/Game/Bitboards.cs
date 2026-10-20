using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace ChessBruteforcer.Core.Game;

/// <summary>How <see cref="Position.IsAttacked(int, Colour)"/> finds a square's attackers.</summary>
public enum AttackPath : byte
{
    /// <summary>Walking rays out from the square, one square at a time, as before the sets (the tests' reference).</summary>
    Rays,

    /// <summary>From the piece sets by table, a rook's and a bishop's attacks found by magic multiplication (any 64-bit CPU).</summary>
    Magic,

    /// <summary>From the piece sets by table, a rook's and a bishop's attacks found by PEXT (BMI2).</summary>
    Pext,
}

/// <summary>
/// Squares as sets, a bit a square (a1 = bit 0, h8 = bit 63), and attacks by table: what a knight, king or pawn on a
/// square attacks, and what a rook or bishop there reaches through a given occupancy. A slider's table holds, for each
/// square, an entry per subset of the squares that can block it (its rays less the board's edge): 102,400 rook entries
/// and 5,248 bishop ones, 64 bits each (~840 KB). PEXT gathers the blockers into the entry's number where the CPU does
/// it in hardware; elsewhere a magic multiplication does (a number per square that sends every subset to a slot of its
/// own, found by scripts/bitboards/magics.cs): AMD before Zen 3 microcodes PEXT, tens to hundreds of cycles by the mask.
/// Only the table the chosen path reads is built.
/// </summary>
public static class Bitboards
{
    /// <summary>
    /// The path <see cref="Position.IsAttacked(int, Colour)"/> takes: <see cref="Best"/>, unless the environment variable
    /// CB_ATTACKS asks for another (pext, magic or rays), for an A/B; pext only where the CPU has it.
    /// </summary>
    public static readonly AttackPath Path = Choose(Environment.GetEnvironmentVariable("CB_ATTACKS"));

    /// <summary>The table <see cref="Rook(int, ulong)"/> and <see cref="Bishop(int, ulong)"/> read: <see cref="Path"/>'s, or with rays the CPU's best.</summary>
    internal static readonly AttackPath Tables = Path == AttackPath.Rays ? Best : Path;

    /// <summary>The path this CPU does best: PEXT where it has it in hardware, magic multiplication elsewhere.</summary>
    public static AttackPath Best => Bmi2.X64.IsSupported && !PextIsSlowHere() ? AttackPath.Pext : AttackPath.Magic;

    internal static AttackPath Choose(string? asked) => asked?.Trim().ToLowerInvariant() switch
    {
        "rays" => AttackPath.Rays,
        "magic" => AttackPath.Magic,
        "pext" when Bmi2.X64.IsSupported => AttackPath.Pext,
        _ => Best,
    };

    /// <summary>
    /// Is PEXT microcoded on this CPU? AMD's Zen, Zen+ and Zen 2 (family 17h) and Hygon's Dhyana (18h) take tens to
    /// hundreds of cycles for it, by the mask; Zen 3 on (19h) do it in hardware, as Intel's CPUs do.
    /// </summary>
    internal static bool PextIsSlow(string vendor, int family) => vendor is "AuthenticAMD" or "HygonGenuine" && family < 0x19;

    private static bool PextIsSlowHere()
    {
        if (!X86Base.IsSupported)
            return false;
        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        Span<int> words = stackalloc int[] { ebx, edx, ecx };
        string vendor = Encoding.ASCII.GetString(MemoryMarshal.AsBytes(words));
        int eax = X86Base.CpuId(1, 0).Eax;
        int family = eax >> 8 & 0xF;
        if (family == 0xF)
            family += eax >> 20 & 0xFF;
        return PextIsSlow(vendor, family);
    }

    private static readonly (int File, int Rank)[] RookSteps = { (0, 1), (0, -1), (1, 0), (-1, 0) };
    private static readonly (int File, int Rank)[] BishopSteps = { (1, 1), (1, -1), (-1, 1), (-1, -1) };

    private static readonly ulong[] KnightSets = Steps(new[] { (1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2) });
    private static readonly ulong[] KingSets = Steps(RookSteps.Concat(BishopSteps).ToArray());
    private static readonly ulong[] PawnSets = Steps(new[] { (-1, 1), (1, 1) }).Concat(Steps(new[] { (-1, -1), (1, -1) })).ToArray();
    private static readonly ulong[] RookLineSets = Rays(RookSteps, edge: true);
    private static readonly ulong[] BishopLineSets = Rays(BishopSteps, edge: true);
    private static readonly ulong[] RookMasks = Rays(RookSteps, edge: false);
    private static readonly ulong[] BishopMasks = Rays(BishopSteps, edge: false);

    /// <summary>The squares a knight on <paramref name="square"/> attacks.</summary>
    public static ulong Knight(int square) => KnightSets[square];

    /// <summary>The squares a king on <paramref name="square"/> attacks.</summary>
    public static ulong King(int square) => KingSets[square];

    /// <summary>The squares a pawn of <paramref name="colour"/> on <paramref name="square"/> attacks.</summary>
    public static ulong Pawn(Colour colour, int square) => PawnSets[(int)colour * 64 + square];

    /// <summary>The squares a rook on <paramref name="square"/> reaches on an empty board: its rank and file.</summary>
    public static ulong RookLines(int square) => RookLineSets[square];

    /// <summary>The squares a bishop on <paramref name="square"/> reaches on an empty board: its diagonals.</summary>
    public static ulong BishopLines(int square) => BishopLineSets[square];

    /// <summary>The squares a rook on <paramref name="square"/> reaches through <paramref name="occupied"/>: each way up to and including the first piece.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Rook(int square, ulong occupied) => Rook(Tables, square, occupied);

    /// <summary>The squares a bishop on <paramref name="square"/> reaches through <paramref name="occupied"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Bishop(int square, ulong occupied) => Bishop(Tables, square, occupied);

    /// <summary><see cref="Rook(int, ulong)"/> from the PEXT table or the magic one (the tests read both).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Rook(AttackPath path, int square, ulong occupied) =>
        path == AttackPath.Pext ? PextTables.Rook(square, occupied) : MagicTables.Rook(square, occupied);

    /// <summary><see cref="Bishop(int, ulong)"/> from the PEXT table or the magic one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Bishop(AttackPath path, int square, ulong occupied) =>
        path == AttackPath.Pext ? PextTables.Bishop(square, occupied) : MagicTables.Bishop(square, occupied);

    /// <summary>The squares a rook (or bishop) on <paramref name="square"/> could be blocked on, its rays less the board's edge: the mask its table is built and read with.</summary>
    internal static ulong RookMask(int square) => RookMasks[square];

    internal static ulong BishopMask(int square) => BishopMasks[square];

    private static ulong[] Rays((int File, int Rank)[] steps, bool edge) =>
        Enumerable.Range(0, 64).Select(square => Slide(square, 0, steps, edge)).ToArray();

    private static ulong[] Steps((int File, int Rank)[] steps)
    {
        var sets = new ulong[64];
        for (int square = 0; square < 64; square++)
        {
            foreach (var (f, r) in steps)
            {
                int file = square % 8 + f, rank = square / 8 + r;
                if (file is >= 0 and < 8 && rank is >= 0 and < 8)
                    sets[square] |= 1UL << (rank * 8 + file);
            }
        }
        return sets;
    }

    /// <summary>
    /// The squares reached from <paramref name="square"/> along <paramref name="steps"/>, each way up to and including the
    /// first square in <paramref name="occupied"/>; without <paramref name="edge"/> a ray's last square is left out (it
    /// blocks nothing beyond it), which gives a slider's mask.
    /// </summary>
    private static ulong Slide(int square, ulong occupied, (int File, int Rank)[] steps, bool edge)
    {
        ulong set = 0;
        foreach (var (f, r) in steps)
        {
            int file = square % 8 + f, rank = square / 8 + r;
            while (file is >= 0 and < 8 && rank is >= 0 and < 8)
            {
                if (!edge && !(file + f is >= 0 and < 8 && rank + r is >= 0 and < 8))
                    break;
                ulong bit = 1UL << (rank * 8 + file);
                set |= bit;
                if ((occupied & bit) != 0)
                    break;
                file += f;
                rank += r;
            }
        }
        return set;
    }

    /// <summary>A square's slider entry: the squares that can block, where its slots start, and its magic number and shift.</summary>
    private readonly record struct Slider(ulong Mask, ulong Magic, int Shift, int Base);

    /// <summary>
    /// One slider kind's table: <paramref name="slot"/> gives a subset's slot among its square's 2^bits. Every subset of
    /// every mask is walked (the carry-rippler: (s - mask) &amp; mask counts through them), and a slot two subsets with
    /// different attacks share is an error: a wrong magic number throws here rather than answer wrongly.
    /// </summary>
    private static (Slider[] Squares, ulong[] Table) Build((int File, int Rank)[] steps, ulong[] masks, ulong[]? magics,
                                                           Func<ulong, Slider, int> slot)
    {
        var squares = new Slider[64];
        int size = 0;
        for (int square = 0; square < 64; square++)
        {
            ulong mask = masks[square];
            int bits = System.Numerics.BitOperations.PopCount(mask);
            squares[square] = new Slider(mask, magics?[square] ?? 0, 64 - bits, size);
            size += 1 << bits;
        }
        var table = new ulong[size];
        for (int square = 0; square < 64; square++)
        {
            var slider = squares[square];
            ulong subset = 0;
            do
            {
                ulong attacks = Slide(square, subset, steps, edge: true);
                ref ulong entry = ref table[slider.Base + slot(subset, slider)];
                if (entry != 0 && entry != attacks)
                    throw new InvalidOperationException($"Bitboards: square {square}'s slider table has a collision (a wrong magic number?).");
                entry = attacks;   // never 0: a slider always reaches at least one square
                subset = (subset - slider.Mask) & slider.Mask;
            } while (subset != 0);
        }
        return (squares, table);
    }

    /// <summary>
    /// The slider tables indexed by PEXT: a subset's slot is its blockers gathered into a number. Built on first use (a
    /// static constructor of its own), so a CPU on the magic path never builds them.
    /// </summary>
    private static class PextTables
    {
        private static readonly Slider[] Rooks, Bishops;
        private static readonly ulong[] RookTable, BishopTable;

        static PextTables()
        {
            (Rooks, RookTable) = Build(RookSteps, RookMasks, null, (subset, slider) => (int)Bmi2.X64.ParallelBitExtract(subset, slider.Mask));
            (Bishops, BishopTable) = Build(BishopSteps, BishopMasks, null, (subset, slider) => (int)Bmi2.X64.ParallelBitExtract(subset, slider.Mask));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Rook(int square, ulong occupied)
        {
            ref readonly var slider = ref Rooks[square];
            return RookTable[slider.Base + (int)Bmi2.X64.ParallelBitExtract(occupied, slider.Mask)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Bishop(int square, ulong occupied)
        {
            ref readonly var slider = ref Bishops[square];
            return BishopTable[slider.Base + (int)Bmi2.X64.ParallelBitExtract(occupied, slider.Mask)];
        }
    }

    /// <summary>
    /// The slider tables indexed by magic multiplication: a subset's slot is the top bits of subset x magic. Built on first
    /// use, so a CPU on the PEXT path never builds them.
    /// </summary>
    private static class MagicTables
    {
        private static readonly Slider[] Rooks, Bishops;
        private static readonly ulong[] RookTable, BishopTable;

        static MagicTables()
        {
            (Rooks, RookTable) = Build(RookSteps, RookMasks, RookMagics, Slot);
            (Bishops, BishopTable) = Build(BishopSteps, BishopMasks, BishopMagics, Slot);
        }

        private static int Slot(ulong subset, Slider slider) => (int)((subset * slider.Magic) >> slider.Shift);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Rook(int square, ulong occupied)
        {
            ref readonly var slider = ref Rooks[square];
            return RookTable[slider.Base + (int)(((occupied & slider.Mask) * slider.Magic) >> slider.Shift)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Bishop(int square, ulong occupied)
        {
            ref readonly var slider = ref Bishops[square];
            return BishopTable[slider.Base + (int)(((occupied & slider.Mask) * slider.Magic) >> slider.Shift)];
        }
    }

    // The magic numbers, from scripts/bitboards/magics.cs (a fixed seed; it prints these arrays).
    private static readonly ulong[] RookMagics =
    {
        0x1080004008801020, 0x0840092002C03000, 0x1900200010400900, 0x0880100008000480,
        0x4200100420080200, 0x8100020100080400, 0x0200040110886200, 0x0200008040220411,
        0x0404800084400220, 0x0000401000402000, 0x0086001081220440, 0x0408800800100280,
        0x000A001201040820, 0x8848800200840080, 0x4001000100040200, 0x0442000102105084,
        0x9080010020804100, 0x0040404000201009, 0x0000808010002009, 0x2200090021D00100,
        0x0008008008040080, 0x0004004002010040, 0x0011040008015042, 0x00000A0001768104,
        0x0000800080204009, 0x2010004140002001, 0x9800200280100080, 0x1000100080080080,
        0x0442000A00049020, 0x2100040080020080, 0x0800120400900148, 0x0010040A00128541,
        0x2800804000800030, 0x1010002000400041, 0x4000200011004100, 0x0610008410800800,
        0x0400802402800800, 0xC100020080800400, 0x0002000802000401, 0x0182085882000401,
        0x0220204000808000, 0x2860100040024022, 0x0001002004110040, 0x99101042000A0020,
        0x0004080004008080, 0x0010040002008080, 0x2012004881020004, 0x8300842444820011,
        0x0088403882010200, 0x0820400080210100, 0x0110910040A00300, 0x0801100280080480,
        0x0242009008200600, 0x1002000489500200, 0x0040800200010080, 0x0091800041000080,
        0x0000209300488001, 0x04C1002414824001, 0x020020000B001041, 0x7000100004200901,
        0x8002002004100802, 0x30010002084C0007, 0x0888221800813004, 0x4000002840840112,
    };

    private static readonly ulong[] BishopMagics =
    {
        0xA010041108003100, 0x006082020A002900, 0x6810010619200000, 0x08281A0520000408,
        0x0001104001000400, 0x0018901008048400, 0x00040A0210245280, 0x000200210808A402,
        0x9140048410821200, 0x0800091010820041, 0x20504804832202C0, 0x0100091401081000,
        0x8021011140000012, 0x0810020804450400, 0x208B0542109008A2, 0x0080084A08040204,
        0x0040E2A80811244C, 0x2505022008008108, 0x0430220100420040, 0x010A040420220040,
        0x1105000290400000, 0x0093001200822120, 0x4000A62048043004, 0x280120048A015004,
        0x006090002A020814, 0x44042000240800D0, 0x01102800040A4400, 0x1004080080220040,
        0x0001001011004024, 0x0010044000805040, 0x0914041200820100, 0x0004821012821480,
        0x0024040500C05021, 0x0088611002080200, 0x0116080A00040020, 0x4000020080080080,
        0x2450450140840040, 0x0000880201484100, 0x0222020404020092, 0x8081110600002E00,
        0x2842101105000801, 0x1100809008001025, 0x00020202221C0400, 0x0422014022009020,
        0x0210046102100C00, 0xC004008082029102, 0x00AA461801101200, 0x0404080080201108,
        0x020542108C205002, 0x0410544804100100, 0x0040910841100000, 0x0400200042021100,
        0x00004204850400C0, 0x0200100410A42102, 0x1040020801210102, 0x0805040410420000,
        0x2884804130100200, 0x800C262201242000, 0x1058000194108800, 0x0014221054420204,
        0x0104000012A02200, 0x0200881003300100, 0x0140400202840100, 0x0402020801010201,
    };
}

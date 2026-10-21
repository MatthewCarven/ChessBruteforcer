// Finds the magic multipliers for Bitboards' magic path (rook and bishop attacks by multiplication where PEXT is slow
// or missing): for every square, a number that maps every occupancy of the square's mask to a slot of a table of
// 2^bits entries (bits = the mask's population) with no two occupancies of different attacks in one slot. A fixed seed,
// so a run always prints the same numbers: RookMagics and BishopMagics in src/ChessBruteforcer.Core/Game/Bitboards.cs,
// as C# arrays (~0.3 s). Bitboards checks every number again as it builds its table, and BitboardTests every slot.
//
//     dotnet run -c Release --disable-build-servers -p:UseSharedCompilation=false magics.cs
using System.Diagnostics;
using System.Numerics;

var clock = Stopwatch.StartNew();
ulong state = 0x9E3779B97F4A7C15UL;
ulong Next()
{
    // xorshift64*
    state ^= state >> 12;
    state ^= state << 25;
    state ^= state >> 27;
    return state * 0x2545F4914F6CDD1DUL;
}

(int, int)[] rook = { (0, 1), (0, -1), (1, 0), (-1, 0) };
(int, int)[] bishop = { (1, 1), (1, -1), (-1, 1), (-1, -1) };

ulong Slide(int sq, ulong occ, (int F, int R)[] dirs, bool inner)
{
    ulong set = 0;
    foreach (var (f, r) in dirs)
    {
        int file = sq % 8 + f, rank = sq / 8 + r;
        while (file is >= 0 and < 8 && rank is >= 0 and < 8)
        {
            if (inner && !(file + f is >= 0 and < 8 && rank + r is >= 0 and < 8))
                break;
            ulong bit = 1UL << (rank * 8 + file);
            set |= bit;
            if ((occ & bit) != 0)
                break;
            file += f;
            rank += r;
        }
    }
    return set;
}

ulong[] Find((int, int)[] dirs, string name)
{
    var magics = new ulong[64];
    long tries = 0;
    var occupancies = new ulong[4096];
    var attacks = new ulong[4096];
    var used = new ulong[4096];
    var epoch = new int[4096];
    int stamp = 0;
    for (int sq = 0; sq < 64; sq++)
    {
        ulong mask = Slide(sq, 0, dirs, inner: true);
        int bits = BitOperations.PopCount(mask);
        int n = 0;
        ulong sub = 0;
        do
        {
            occupancies[n] = sub;
            attacks[n] = Slide(sq, sub, dirs, inner: false);
            n++;
            sub = (sub - mask) & mask;
        } while (sub != 0);
        Debug.Assert(n == 1 << bits);
        while (true)
        {
            tries++;
            ulong magic = Next() & Next() & Next();
            if (BitOperations.PopCount((mask * magic) & 0xFF00000000000000UL) < 6)
                continue;
            stamp++;
            bool ok = true;
            for (int i = 0; i < n && ok; i++)
            {
                int slot = (int)((occupancies[i] * magic) >> (64 - bits));
                if (epoch[slot] != stamp)
                {
                    epoch[slot] = stamp;
                    used[slot] = attacks[i];
                }
                else if (used[slot] != attacks[i])
                {
                    ok = false;
                }
            }
            if (ok)
            {
                magics[sq] = magic;
                break;
            }
        }
    }
    Console.Error.WriteLine($"{name}: {tries:N0} tries, {clock.ElapsedMilliseconds} ms so far");
    return magics;
}

void Print(string name, ulong[] magics)
{
    Console.WriteLine($"    private static readonly ulong[] {name} =");
    Console.WriteLine("    {");
    for (int i = 0; i < 64; i += 4)
        Console.WriteLine("        " + string.Join(" ", Enumerable.Range(i, 4).Select(s => $"0x{magics[s]:X16},")));
    Console.WriteLine("    };");
}

var rookMagics = Find(rook, "rook");
var bishopMagics = Find(bishop, "bishop");
Print("RookMagics", rookMagics);
Console.WriteLine();
Print("BishopMagics", bishopMagics);

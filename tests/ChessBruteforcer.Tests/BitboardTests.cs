using System.Runtime.Intrinsics.X86;
using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Tests;

/// <summary>
/// The sets beside the squares and attacks by table (NNUE.md "Bitboards, measured", session 1). The sets must equal
/// the squares after every make, unmake and null move, and IsAttacked from the sets must equal IsAttacked by walking
/// rays, for every square and both colours, at every node of perft walks (illegal positions included: the search
/// makes a move before it looks). Generation from the sets (session 2) must give the walk's lists move for move.
/// Every path runs under the same tests: the table paths (PEXT where the CPU has it, magic multiplication always)
/// against the ray walk here, and the whole suite under CB_ATTACKS=magic or rays.
/// </summary>
public class BitboardTests
{
    private static readonly Colour[] Colours = { Colour.White, Colour.Black };

    /// <summary>The table paths this CPU runs: magic multiplication always, PEXT where it has BMI2.</summary>
    private static readonly AttackPath[] TablePathsHere =
        Bmi2.X64.IsSupported ? new[] { AttackPath.Magic, AttackPath.Pext } : new[] { AttackPath.Magic };

    public static IEnumerable<object[]> TablePaths() => TablePathsHere.Select(path => new object[] { path });

    public static IEnumerable<object[]> AllPaths() => TablePathsHere.Prepend(AttackPath.Rays).Select(path => new object[] { path });

    /// <summary>
    /// At every node of a perft walk (and after every illegal move, null move and unmake): the sets equal the squares,
    /// and every table path finds the attackers of every square as the ray walk does.
    /// </summary>
    [Theory]
    [InlineData(Fen.StartPosition, 3)]
    [InlineData(MoveGeneratorTests.Kiwipete, 3)]
    [InlineData(MoveGeneratorTests.EnPassantPins, 4)]
    [InlineData(MoveGeneratorTests.Promotions, 3)]
    [InlineData(MoveGeneratorTests.CastlingDiscovery, 2)]
    [InlineData(MoveGeneratorTests.Middlegame, 2)]
    [InlineData("r1n1k3/1P4P1/8/8/8/8/1p4p1/R1N1K2N w - - 0 1", 3)]      // both sides promote, pushing and capturing
    [InlineData("n1n5/PPPk4/8/8/8/8/4Kppp/5N1N b - - 0 1", 3)]           // capture-promotions, black first
    [InlineData("8/8/8/KPp4r/8/8/8/7k w - c6 0 2", 4)]                   // en passant, and a pinned one
    [InlineData("r3k2r/8/8/8/3pP3/8/8/R3K2R b KQkq e3 0 1", 3)]          // en passant and castling, both sides
    public void SetsEqualTheSquaresAndTablesEqualRaysAtEveryNode(string fen, int depth)
    {
        long nodes = Walk(Position.FromFen(fen), depth);
        Assert.True(nodes > 100, $"{nodes} nodes");
    }

    private static long Walk(Position position, int plies)
    {
        CheckSets(position);
        CheckAttacks(position);
        if (plies == 0)
            return 1;
        long nodes = 1;

        var nullUndo = position.MakeNullMove();
        CheckSets(position);
        position.UnmakeNullMove(nullUndo);
        CheckSets(position);

        var moves = new List<Move>(64);
        MoveGenerator.Pseudo(position, moves);
        Colour us = position.SideToMove;
        foreach (var move in moves)
        {
            var undo = position.MakeMove(move);
            if (position.InCheck(us))
            {
                // Illegal: the search makes it, looks, and takes it back.
                CheckSets(position);
                CheckAttacks(position);
                nodes++;
            }
            else
            {
                nodes += Walk(position, plies - 1);
            }
            position.UnmakeMove(move, undo);
            CheckSets(position);
        }
        return nodes;
    }

    /// <summary>Every set against the squares: each colour's and type's, each colour's in all, and the occupied squares.</summary>
    private static void CheckSets(Position position)
    {
        Span<ulong> expected = stackalloc ulong[16];
        for (int square = 0; square < 64; square++)
        {
            var piece = position[square];
            if (piece.IsEmpty)
                continue;
            expected[(int)piece.Colour * 8] |= 1UL << square;
            expected[(int)piece.Colour * 8 + (int)piece.Type] |= 1UL << square;
        }
        foreach (var colour in Colours)
        {
            if (position.Pieces(colour) != expected[(int)colour * 8])
                Assert.True(false, $"{position.ToFen()}: {colour}'s set {position.Pieces(colour):X16}, squares {expected[(int)colour * 8]:X16}");
            for (var type = PieceType.Pawn; type <= PieceType.King; type++)
                if (position.Pieces(colour, type) != expected[(int)colour * 8 + (int)type])
                    Assert.True(false, $"{position.ToFen()}: {colour} {type} set {position.Pieces(colour, type):X16}, squares {expected[(int)colour * 8 + (int)type]:X16}");
        }
        if (position.Occupied != (expected[0] | expected[8]))
            Assert.True(false, $"{position.ToFen()}: occupied {position.Occupied:X16}");
    }

    private static void CheckAttacks(Position position)
    {
        for (int square = 0; square < 64; square++)
        {
            foreach (var by in Colours)
            {
                bool expected = position.IsAttacked(AttackPath.Rays, square, by);
                foreach (var path in TablePathsHere)
                    if (position.IsAttacked(path, square, by) != expected)
                        Assert.True(false, $"{position.ToFen()}: {PackedBoard.SquareName(square)} attacked by {by}: rays say {expected}, {path} not");
            }
        }
    }

    /// <summary>
    /// Generation from the sets gives the walk's lists move for move, in the walk's order (the order is part of the
    /// search: PickNext breaks ties by place in the list, and LMR reduces by it), every move and quiescence's captures
    /// alike, on each table path, at every node of perft walks and after a null move at each.
    /// </summary>
    [Theory]
    [InlineData(Fen.StartPosition, 3)]
    [InlineData(MoveGeneratorTests.Kiwipete, 3)]
    [InlineData(MoveGeneratorTests.EnPassantPins, 4)]
    [InlineData(MoveGeneratorTests.Promotions, 3)]
    [InlineData(MoveGeneratorTests.CastlingDiscovery, 3)]
    [InlineData(MoveGeneratorTests.Middlegame, 3)]
    [InlineData("r1n1k3/1P4P1/8/8/8/8/1p4p1/R1N1K2N w - - 0 1", 3)]      // both sides promote, pushing and capturing
    [InlineData("n1n5/PPPk4/8/8/8/8/4Kppp/5N1N b - - 0 1", 3)]           // capture-promotions, black first
    [InlineData("8/8/8/KPp4r/8/8/8/7k w - c6 0 2", 4)]                   // en passant, and a pinned one
    [InlineData("r3k2r/8/8/8/3pP3/8/8/R3K2R b KQkq e3 0 1", 3)]          // en passant and castling, both sides
    public void GenerationFromTheSetsGivesTheWalksListsMoveForMove(string fen, int depth)
    {
        var expected = new List<Move>(256);
        var actual = new List<Move>(256);
        long lists = 0;

        void Check(Position position)
        {
            foreach (bool quiet in new[] { true, false })
            {
                Generate(AttackPath.Rays, position, expected, quiet);
                foreach (var path in TablePathsHere)
                {
                    Generate(path, position, actual, quiet);
                    if (!actual.SequenceEqual(expected))
                        Assert.True(false, $"{position.ToFen()}: {path}'s {(quiet ? "moves" : "captures")} {string.Join(' ', actual)}, the walk's {string.Join(' ', expected)}");
                    lists++;
                }
            }
        }

        void Walk(Position position, int plies)
        {
            Check(position);
            if (plies == 0)
                return;
            var nullUndo = position.MakeNullMove();
            Check(position);
            position.UnmakeNullMove(nullUndo);
            foreach (var move in MoveGenerator.Legal(position))
            {
                var undo = position.MakeMove(move);
                Walk(position, plies - 1);
                position.UnmakeMove(move, undo);
            }
        }

        Walk(Position.FromFen(fen), depth);
        Assert.True(lists > 100, $"{lists} lists");
    }

    private static void Generate(AttackPath path, Position position, List<Move> moves, bool quiet)
    {
        if (quiet)
            MoveGenerator.Pseudo(path, position, moves);
        else
            MoveGenerator.Captures(path, position, moves);
    }

    /// <summary>
    /// Perft with generation and the legality test on each path in turn: the sets by PEXT or by magic, or the walk
    /// throughout (castling's own tests take the engine's path; the walk above checks every path against the rays there).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllPaths))]
    public void PerftHoldsOnEveryPath(AttackPath path)
    {
        var cases = new (string Fen, int Depth, long Nodes)[]
        {
            (Fen.StartPosition, 3, 8_902),
            (MoveGeneratorTests.Kiwipete, 3, 97_862),
            (MoveGeneratorTests.EnPassantPins, 4, 43_238),
            (MoveGeneratorTests.Promotions, 3, 9_467),
            (MoveGeneratorTests.CastlingDiscovery, 3, 62_379),
            (MoveGeneratorTests.Middlegame, 3, 89_890),
            ("n1n5/PPPk4/8/8/8/8/4Kppp/5N1N b - - 0 1", 3, 9_483),
        };
        foreach (var (fen, depth, nodes) in cases)
            Assert.Equal(nodes, Perft(Position.FromFen(fen), depth, path));
    }

    private static long Perft(Position position, int depth, AttackPath path)
    {
        var moves = new List<Move>(64);
        MoveGenerator.Pseudo(path, position, moves);
        Colour us = position.SideToMove, them = Position.Opponent(us);
        long nodes = 0;
        foreach (var move in moves)
        {
            var undo = position.MakeMove(move);
            if (!position.IsAttacked(path, position.KingSquare(us), them))
                nodes += depth == 1 ? 1 : Perft(position, depth - 1, path);
            position.UnmakeMove(move, undo);
        }
        return nodes;
    }

    /// <summary>
    /// Each slider table against a walk along the engine's own rays (Attacks.Rays), through every subset of the squares
    /// that can block (with pieces scattered beyond them, which must change nothing).
    /// </summary>
    [Theory]
    [MemberData(nameof(TablePaths))]
    public void SliderTablesEqualTheRaysThroughEveryBlockerSubset(AttackPath path)
    {
        var random = new Random(20261020);
        long checkedSubsets = 0;
        for (int square = 0; square < 64; square++)
        {
            foreach (bool rook in new[] { true, false })
            {
                int first = rook ? Attacks.FirstRookDirection : Attacks.FirstBishopDirection;
                int end = rook ? Attacks.FirstBishopDirection : Attacks.DirectionCount;
                ulong mask = 0;   // every ray but its last square: the squares that can block
                for (int d = first; d < end; d++)
                    foreach (int blocker in Attacks.Rays[square][d].SkipLast(1))
                        mask |= 1UL << blocker;
                Assert.Equal(mask, rook ? Bitboards.RookMask(square) : Bitboards.BishopMask(square));
                ulong subset = 0;
                do
                {
                    ulong occupied = subset | (Bits(random) & ~mask);
                    ulong expected = ByRays(square, occupied, first, end);
                    ulong actual = rook ? Bitboards.Rook(path, square, occupied) : Bitboards.Bishop(path, square, occupied);
                    if (actual != expected)
                        Assert.True(false, $"{path} {(rook ? "rook" : "bishop")} on {PackedBoard.SquareName(square)} through {occupied:X16}: {actual:X16}, rays {expected:X16}");
                    checkedSubsets++;
                    subset = (subset - mask) & mask;
                } while (subset != 0);
            }
        }
        Assert.Equal(102_400 + 5_248, checkedSubsets);
    }

    private static ulong Bits(Random random)
    {
        Span<byte> bytes = stackalloc byte[8];
        random.NextBytes(bytes);
        return BitConverter.ToUInt64(bytes);
    }

    private static ulong ByRays(int square, ulong occupied, int firstDirection, int endDirection)
    {
        ulong set = 0;
        for (int d = firstDirection; d < endDirection; d++)
        {
            foreach (int to in Attacks.Rays[square][d])
            {
                set |= 1UL << to;
                if ((occupied >> to & 1) != 0)
                    break;
            }
        }
        return set;
    }

    [Fact]
    public void KnightKingAndPawnSetsMatchTheBoard()
    {
        for (int square = 0; square < 64; square++)
        {
            Assert.Equal(Attacks.Knight[square].Aggregate(0UL, (set, to) => set | 1UL << to), Bitboards.Knight(square));
            Assert.Equal(Attacks.King[square].Aggregate(0UL, (set, to) => set | 1UL << to), Bitboards.King(square));
            int file = square % 8, rank = square / 8;
            ulong white = 0, black = 0;
            foreach (int side in new[] { -1, 1 })
            {
                if (file + side is < 0 or > 7)
                    continue;
                if (rank < 7) white |= 1UL << (square + 8 + side);
                if (rank > 0) black |= 1UL << (square - 8 + side);
            }
            Assert.Equal(white, Bitboards.Pawn(Colour.White, square));
            Assert.Equal(black, Bitboards.Pawn(Colour.Black, square));
        }
    }

    /// <summary>SetPiece keeps the sets whatever it puts where, over a piece of either colour or none (the solver's way of building positions).</summary>
    [Fact]
    public void SetPieceKeepsTheSetsWhateverItReplaces()
    {
        var random = new Random(7);
        var pieces = Piece.All.Append(Piece.Empty).ToArray();
        var position = Position.FromFen(MoveGeneratorTests.Kiwipete);
        for (int i = 0; i < 5_000; i++)
        {
            position.SetPiece(random.Next(64), pieces[random.Next(pieces.Length)]);
            CheckSets(position);
            Assert.Equal(position.ComputeHash(), position.Hash);
        }
        CheckSets(Tablebase.SwapColours(Position.FromFen(MoveGeneratorTests.Middlegame)));
    }

    [Theory]
    [InlineData(Fen.StartPosition, "KQRRBBNNPPPPPPPPvKQRRBBNNPPPPPPPP")]
    [InlineData(MoveGeneratorTests.Kiwipete, "KQRRBBNNPPPPPPPPvKQRRBBNNPPPPPPPP")]
    [InlineData("8/8/8/4k3/8/8/8/4K2Q b - - 12 40", "KQvK")]
    [InlineData("8/8/2n5/4k3/8/1P6/8/4K2R w - - 0 1", "KRPvKN")]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", "KRPPPvKRPPP")]
    [InlineData("4k3/8/8/8/8/8/8/4K3 w - - 0 1", "KvK")]
    public void MaterialIsCountedFromTheSets(string fen, string expected)
    {
        var material = Material.FromPosition(Position.FromFen(fen));
        Assert.Equal(expected, material.ToString());
        Assert.Equal(Material.Parse(expected), material);
    }

    /// <summary>Material.FromPosition makes the material and its two arrays, and nothing else (it used to fill two lists and sort them).</summary>
    [Fact]
    public void MaterialFromPositionAllocatesOnlyItsArrays()
    {
        var position = Position.FromFen("8/8/2n5/4k3/8/1P6/8/4K2R w - - 0 1");
        Material.FromPosition(position);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            Material.FromPosition(position);
        long perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
        Assert.True(perCall <= 120, $"{perCall} bytes a call");
    }

    /// <summary>
    /// The tablebase counts a position's pieces from the sets, and six pieces never reach a table: here a junk file sits
    /// where the six-piece table would be, and reading it would throw.
    /// </summary>
    [Fact]
    public void TryProbeTurnsAwaySixPiecesBeforeLookingForATable()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cb-six-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var tablebase = new Tablebase(dir) { SolveMissing = false };
            File.WriteAllText(Path.Combine(dir, "KRRvKRR.cbt"), "not a table");
            var six = Position.FromFen("1r2k2r/8/8/8/8/8/8/1R2K2R w - - 0 1");
            Assert.Equal(6, System.Numerics.BitOperations.PopCount(six.Occupied));
            Assert.False(tablebase.TryProbe(six, out _));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The CPU's own best path: PEXT where it has BMI2 and does PEXT in hardware, else magic multiplication.</summary>
    [Fact]
    public void BestIsPextWhereTheCpuDoesItInHardware()
    {
        if (!Bmi2.X64.IsSupported)
        {
            Assert.Equal(AttackPath.Magic, Bitboards.Best);
            return;
        }
        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        string vendor = string.Concat(new[] { ebx, edx, ecx }.Select(word => System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(word))));
        int eax = X86Base.CpuId(1, 0).Eax;
        int family = (eax >> 8 & 0xF) == 0xF ? 0xF + (eax >> 20 & 0xFF) : eax >> 8 & 0xF;
        Assert.Equal(Bitboards.PextIsSlow(vendor, family) ? AttackPath.Magic : AttackPath.Pext, Bitboards.Best);
    }

    [Fact]
    public void TheEnvironmentPicksThePath()
    {
        Assert.Equal(AttackPath.Rays, Bitboards.Choose("rays"));
        Assert.Equal(AttackPath.Rays, Bitboards.Choose(" Rays "));
        Assert.Equal(AttackPath.Magic, Bitboards.Choose("magic"));
        Assert.Equal(Bmi2.X64.IsSupported ? AttackPath.Pext : Bitboards.Best, Bitboards.Choose("pext"));
        Assert.Equal(Bitboards.Best, Bitboards.Choose(null));
        Assert.Equal(Bitboards.Best, Bitboards.Choose(""));
        Assert.Equal(Bitboards.Best, Bitboards.Choose("fast"));
        Assert.Equal(Bitboards.Choose(Environment.GetEnvironmentVariable("CB_ATTACKS")), Bitboards.Path);
        Assert.NotEqual(AttackPath.Rays, Bitboards.Best);
    }

    [Theory]
    [InlineData("AuthenticAMD", 0x15, true)]    // Excavator
    [InlineData("AuthenticAMD", 0x17, true)]    // Zen, Zen+, Zen 2
    [InlineData("HygonGenuine", 0x18, true)]    // Dhyana (Zen)
    [InlineData("AuthenticAMD", 0x19, false)]   // Zen 3, Zen 4
    [InlineData("AuthenticAMD", 0x1A, false)]   // Zen 5
    [InlineData("GenuineIntel", 0x06, false)]
    public void PextIsSlowOnlyOnAmdBeforeZen3(string vendor, int family, bool slow)
    {
        Assert.Equal(slow, Bitboards.PextIsSlow(vendor, family));
    }
}

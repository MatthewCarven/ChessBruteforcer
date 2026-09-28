using ChessBruteforcer.Core;
using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Tests;

/// <summary>Solving the small tables once, shared by every test in the class.</summary>
public sealed class SolvedTables
{
    public Tablebase Tablebase { get; } = new();

    public SolvedTables()
    {
        Tablebase.Get(Material.Parse("KQvK"));
        Tablebase.Get(Material.Parse("KRvK"));
        Tablebase.Get(Material.Parse("KPvK"));
    }
}

public class EndgameTests : IClassFixture<SolvedTables>
{
    private readonly Tablebase _tablebase;

    public EndgameTests(SolvedTables tables) => _tablebase = tables.Tablebase;

    [Theory]
    [InlineData("KQvK", 19)]   // mate in 10
    [InlineData("KRvK", 31)]   // mate in 16
    public void LongestMatesMatchTheKnownValues(string material, int plies)
    {
        var stats = _tablebase.Get(Material.Parse(material)).Statistics();
        Assert.Equal(Outcome.Win(plies), stats.Longest[(int)Colour.White]!.Value.Outcome);
        Assert.Equal(Outcome.Loss(plies + 1), stats.Longest[(int)Colour.Black]!.Value.Outcome);
        Assert.Equal(0, stats.Draws[(int)Colour.White]);   // with the extra piece to move, always a win
        Assert.Equal(0, stats.Wins[(int)Colour.Black]);
    }

    [Fact]
    public void KingVersusKingIsAlwaysADraw()
    {
        var stats = _tablebase.Get(Material.Parse("KvK")).Statistics();
        foreach (var side in new[] { Colour.White, Colour.Black })
        {
            Assert.Equal(3612, stats.Legal(side));   // 64 x 63 minus the 420 adjacent placements
            Assert.Equal(3612, stats.Draws[(int)side]);
        }
    }

    /// <summary>
    /// The defining equation of a solved table, checked at every legal
    /// position: no moves means mate (loss in 0) or stalemate (draw),
    /// otherwise the value is exactly the best outcome among the moves.
    /// Together with the mates this pins every value down uniquely.
    /// </summary>
    [Theory]
    [InlineData("KQvK")]
    [InlineData("KRvK")]
    [InlineData("KPvK")]
    public void EveryPositionEqualsTheBestOfItsMoves(string material)
    {
        var (checkedPositions, mismatches) = _tablebase.Verify(Material.Parse(material));
        Assert.Empty(mismatches);
        // Each position once, up to symmetry: ~46k for K+Q v K (it was ~400k before symmetry).
        Assert.True(checkedPositions > 40_000);
    }

    [Theory]
    [InlineData("7k/5Q2/6K1/8/8/8/8/8 b - - 0 1", OutcomeKind.Draw, 0)]      // stalemate
    [InlineData("7k/6Q1/6K1/8/8/8/8/8 b - - 0 1", OutcomeKind.Loss, 0)]      // checkmate, queen defended
    [InlineData("6Qk/8/6K1/8/8/8/8/8 b - - 0 1", OutcomeKind.Draw, 0)]       // Kxg8: the queen was loose
    [InlineData("7k/8/6K1/8/8/8/8/1Q6 w - - 0 1", OutcomeKind.Win, 1)]       // Qb8 mates
    [InlineData("8/8/8/8/8/8/1q6/K4k2 w - - 0 1", OutcomeKind.Draw, 0)]      // Kxb2
    [InlineData("8/8/8/8/8/8/1q6/K1k5 w - - 0 1", OutcomeKind.Loss, 0)]      // queen defended: mate
    public void KnownPositionsProbeCorrectly(string fen, OutcomeKind kind, int plies)
    {
        Assert.Equal(new Outcome(kind, plies), _tablebase.Probe(Position.FromFen(fen)));
    }

    /// <summary>Textbook king-and-pawn results (only the verdict; the distances come from the solver).</summary>
    [Theory]
    [InlineData("7k/8/8/8/P7/8/8/7K w - - 0 1", OutcomeKind.Win)]     // black king outside the pawn's square
    [InlineData("8/8/8/2k5/P7/8/8/7K b - - 0 1", OutcomeKind.Draw)]   // inside it: Kb4 wins the pawn
    [InlineData("k7/8/1K6/P7/8/8/8/8 w - - 0 1", OutcomeKind.Draw)]   // rook pawn, defender in the corner
    [InlineData("k7/8/1K6/P7/8/8/8/8 b - - 0 1", OutcomeKind.Draw)]
    [InlineData("4k3/8/4K3/4P3/8/8/8/8 w - - 0 1", OutcomeKind.Win)]  // king on the 6th in front of its pawn
    [InlineData("4k3/8/4K3/4P3/8/8/8/8 b - - 0 1", OutcomeKind.Loss)] //   wins whoever is to move
    [InlineData("8/4P3/8/8/8/8/k7/4K3 w - - 0 1", OutcomeKind.Win)]   // promotes, then K+Q v K
    [InlineData("8/8/8/8/4k3/8/4p3/K7 w - - 0 1", OutcomeKind.Loss)]  // black's pawn: found by swapping colours
    public void KingAndPawnTheory(string fen, OutcomeKind kind)
    {
        Assert.Equal(kind, _tablebase.Probe(Position.FromFen(fen)).Kind);
    }

    [Fact]
    public void TheLoneKingNeverWins()
    {
        var stats = _tablebase.Get(Material.Parse("KPvK")).Statistics();
        Assert.Equal(0, stats.Wins[(int)Colour.Black]);
        Assert.Equal(0, stats.Losses[(int)Colour.White]);
        Assert.True(stats.Draws[(int)Colour.White] > 0);   // unlike K+Q v K, plenty of draws
    }

    /// <summary>
    /// Pawns on both sides bring in en passant.  Solving K+P v K+P needs a
    /// dozen 4-piece tables for its promotions (~20 minutes), so this only
    /// runs when CHESS_SLOW_TESTS names a directory of solved tables
    /// (e.g. after `solve KPvKP` there).
    /// </summary>
    [SlowFact]
    public void PawnsOnBothSidesIncludingEnPassantAreConsistent()
    {
        var tablebase = new Tablebase(Environment.GetEnvironmentVariable(SlowFactAttribute.Variable));
        var (checkedPositions, mismatches) = tablebase.Verify(Material.Parse("KPvKP"), stride: 101);
        Assert.Empty(mismatches);
        Assert.True(checkedPositions > 100_000);

        // An en passant right only ever adds an option for the side that has it.
        var withRight = Position.FromFen("4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1");
        var without = Position.FromFen("4k3/8/8/3pP3/8/8/8/4K3 w - - 0 1");
        Assert.True(tablebase.Probe(withRight).Score >= tablebase.Probe(without).Score);
    }

    [Theory]
    [InlineData("4k3/8/4K3/4P3/8/8/8/8 w - - 0 1")]
    [InlineData("8/8/8/5k2/8/8/1q6/7K b - - 0 1")]
    public void SwappingColoursKeepsTheResult(string fen)
    {
        var position = Position.FromFen(fen);
        Assert.Equal(_tablebase.Probe(position), _tablebase.Probe(Tablebase.SwapColours(position)));
    }

    [Fact]
    public void SwappingColoursSwapsNothingButWhoWins()
    {
        // Black with the queen: looked up in the KQvK table with colours swapped.
        var position = Position.FromFen("8/8/8/5k2/8/8/1q6/7K b - - 0 1");
        var swapped = Tablebase.SwapColours(position);
        Assert.Equal(_tablebase.Probe(swapped), _tablebase.Probe(position));
        Assert.Equal(OutcomeKind.Win, _tablebase.Probe(position).Kind);
    }

    [Fact]
    public void MovesAreRankedBestFirst()
    {
        var ranked = _tablebase.RankMoves(Position.FromFen("8/8/8/4k3/8/8/8/4K2Q w - - 0 1"));
        Assert.Equal(Outcome.Win(13), ranked[0].Outcome);
        for (int i = 1; i < ranked.Count; i++)
            Assert.True(ranked[i - 1].Outcome.Score >= ranked[i].Outcome.Score);
    }

    [Fact]
    public void ThePrincipalLineMatesInExactlyTheStoredDistance()
    {
        var position = Position.FromFen("8/8/8/8/8/2k5/1R6/K7 w - - 0 1");
        var outcome = _tablebase.Probe(position);
        var line = _tablebase.PrincipalLine(position);
        Assert.Equal(outcome.Plies, line.Count);
        foreach (var move in line)
            position.MakeMove(move);
        Assert.Equal(GameStatus.Checkmate, position.Status());
    }

    [Fact]
    public void TablesSurviveASaveAndLoad()
    {
        string path = Path.Combine(Path.GetTempPath(), $"krvk-{Guid.NewGuid():N}.cbt");
        try
        {
            var table = _tablebase.Get(Material.Parse("KRvK"));
            table.Save(path);
            using var loaded = EndgameTable.Load(path);
            Assert.Equal(table.Material, loaded.Material);
            Assert.Equal(table.Size, loaded.Size);
            for (long index = 0; index < table.Size; index += 97)
                Assert.Equal(table[index], loaded[index]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TablesTooBigToSolveAreRefusedBeforeAnyWork()
    {
        // Six pieces, either way; five with a pawn only slice by slice, so not capped.
        Assert.Throws<NotSupportedException>(() => EndgameTable.Solve(Material.Parse("KQRRvKR"), _ => Outcome.Draw));
        Assert.Throws<NotSupportedException>(() => EndgameTable.SolveDtz(Material.Parse("KQRRvKR"), _ => Outcome.Draw));
        Assert.Throws<NotSupportedException>(() => EndgameTable.Solve(Material.Parse("KQRPvK"), _ => Outcome.Draw, cap: 10));
    }

    [Theory]
    [InlineData("KPvK")]   // (4-piece pawn tables promote into 4-piece tables: minutes each here, so the
                           // byte-for-byte check of all 36 covers them, scripts/measure-tables.sh)
    public void SolvingByPawnSlicesGivesTheSameTableAsSolvingItWhole(string text)
    {
        // Slices (the default for pawns) against the whole table at once (a cap past every mate).
        var material = Material.Parse(text);
        var sliced = EndgameTable.Solve(material, _tablebase.Probe);
        var whole = EndgameTable.Solve(material, _tablebase.Probe, cap: 1000);
        Assert.Null(whole.Cap);
        Assert.Equal(whole.Size, sliced.Size);
        for (long i = 0; i < whole.Size; i++)
            Assert.Equal(whole[i], sliced[i]);
    }

    [Fact]
    public void ASlicedTableIsWrittenStraightToItsFile()
    {
        string dir = Directory.CreateTempSubdirectory("cbt-sliced").FullName;
        try
        {
            string path = Path.Combine(dir, "KPvK.cbt");
            using (var table = EndgameTable.Solve(Material.Parse("KPvK"), _tablebase.Probe, path: path))
            {
                Assert.Equal(path, table.FilePath);   // loaded from the file it was solved into
                table.Save(path);                     // already there: nothing to do (and no clash with the mapping)
            }
            string reference = Path.Combine(dir, "reference.cbt");
            _tablebase.Get(Material.Parse("KPvK")).Save(reference);
            Assert.Equal(File.ReadAllBytes(reference), File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ASolvedTableReportsItsSolverMemory()
    {
        var table = EndgameTable.Solve(Material.Parse("KRvK"), _tablebase.Probe);
        var memory = table.SolverMemory!.Value;
        // Values, move counts and longest losses take 5 bytes a slot; the three flags are bits.
        Assert.Equal(table.Size * 5 + 3 * ((table.Size + 63) / 64 * 8), memory.ArrayBytes);
        Assert.True(memory.QueueEntries > 0);
        Assert.True(memory.QueueBytes >= memory.QueueEntries * sizeof(uint));
    }

    /// <summary>A capped table agrees with the complete one on everything within its cap, and says "beyond" for the rest.</summary>
    private static void AssertSettledWithin(EndgameTable complete, EndgameTable capped, int cap)
    {
        Assert.Equal(cap, capped.Cap);
        for (long i = 0; i < complete.Size; i++)
        {
            var full = complete[i];
            var part = capped[i];
            if (full is null)
                Assert.Null(part);
            else if (full.Value.Kind != OutcomeKind.Draw && full.Value.Plies <= cap)
                Assert.Equal(full, part);
            else if (part!.Value.Kind != OutcomeKind.Beyond)
                Assert.Equal(full, part);   // only proven draws (stalemates) may show before the end
            else
                Assert.Equal(Outcome.Beyond(cap), part);
        }
    }

    [Fact]
    public void ACappedTableExtendedStepByStepEndsAsTheCompleteTable()
    {
        // K+P v K promotes into K+Q v K and the rest, so its captures lean on capped tables too.
        var complete = _tablebase.Get(Material.Parse("KPvK"));
        var tablebase = new Tablebase { Cap = 10 };
        AssertSettledWithin(complete, tablebase.Get(Material.Parse("KPvK")), 10);
        Assert.Equal(10, tablebase.Get(Material.Parse("KQvK")).Cap);
        Assert.Empty(tablebase.Verify(Material.Parse("KPvK"), stride: 7).Mismatches);

        tablebase.Cap = 25;
        AssertSettledWithin(complete, tablebase.Get(Material.Parse("KPvK")), 25);

        tablebase.Cap = null;
        var extended = tablebase.Get(Material.Parse("KPvK"));
        Assert.Null(extended.Cap);
        for (long i = 0; i < complete.Size; i++)
            Assert.Equal(complete[i], extended[i]);
    }

    [Fact]
    public void ACappedTableOnDiskCarriesOnFromItsFrontierFile()
    {
        string dir = Directory.CreateTempSubdirectory("cbt-ladder").FullName;
        try
        {
            string path = Path.Combine(dir, "KPvK.cbt");
            using (var first = new Tablebase(dir) { Cap = 8 })
                first.Get(Material.Parse("KPvK"));
            Assert.True(File.Exists(Path.ChangeExtension(path, ".cbf")));
            using (var capped = EndgameTable.Load(path))
                Assert.Equal(8, capped.Cap);

            // New tablebases, as a later run would open: each picks up where the files left off.
            using (var deeper = new Tablebase(dir) { Cap = 20 })
                deeper.Get(Material.Parse("KPvK"));
            using (var last = new Tablebase(dir))
                last.Get(Material.Parse("KPvK"));
            Assert.False(File.Exists(Path.ChangeExtension(path, ".cbf")));

            string reference = Path.Combine(dir, "reference.cbt");
            _tablebase.Get(Material.Parse("KPvK")).Save(reference);
            Assert.Equal(File.ReadAllBytes(reference), File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BeyondTheCapIsNotADrawToTheEngine()
    {
        var complete = _tablebase.Get(Material.Parse("KRvK"));
        var (outcome, index) = complete.Statistics().Longest[(int)Colour.White]!.Value;
        var position = complete.PositionAt(index);
        var tablebase = new Tablebase { Cap = 4 };

        Assert.Equal(Outcome.Beyond(4), tablebase.Probe(position));
        Assert.False(tablebase.TryProbe(position, out _));
        Assert.Equal(OutcomeKind.Win, outcome.Kind);
    }

    [Fact]
    public void CapturingIntoATableCappedTooShallowIsRefused()
    {
        var krvk = Material.Parse("KRvK");
        // The capture leads to a table that says "beyond 2 plies": not enough for a cap of 10, nor for no cap.
        Assert.Throws<InvalidOperationException>(() => EndgameTable.Solve(krvk, _ => Outcome.Beyond(2), cap: 10));
        Assert.Throws<InvalidOperationException>(() => EndgameTable.Solve(krvk, _ => Outcome.Beyond(2)));
        Assert.Equal(10, EndgameTable.Solve(krvk, _ => Outcome.Beyond(9), cap: 10).Cap);
    }

    [Theory]
    [InlineData("KRvK")]
    [InlineData("KPvK")]   // pawn moves reset the count: solved in slices, most advanced pawn first
    public void UnderTheFiftyMoveRuleShortEndingsKeepTheirResults(string text)
    {
        var material = Material.Parse(text);
        var full = _tablebase.Get(material);
        var dtz = _tablebase.GetDtz(material);
        Assert.True(dtz.IsDtz);
        for (long i = 0; i < full.Size; i++)
        {
            var mate = full[i];
            var zero = dtz[i];
            Assert.Equal(mate?.Kind, zero?.Kind);   // every mate here is well inside 100 plies
            // Mate is itself the end of the count, so the next capture, pawn move or mate can't be further off.
            if (mate is { Kind: not OutcomeKind.Draw })
                Assert.True(zero!.Value.Plies <= mate.Value.Plies);
        }
        var (cursed, blessed) = full.RuleDraws(dtz);
        Assert.Equal(new long[2], cursed);
        Assert.Equal(new long[2], blessed);
        Assert.Empty(_tablebase.VerifyDtz(material).Mismatches);
    }

    [Fact]
    public void AZeroingMoveCountsOnePlyWhateverLiesBeyondIt()
    {
        // K+P v K, the king behind its pawn: mate is a long way off, but a pawn push is
        // a zeroing move, so under the rule the win is one ply from its next reset.
        var position = Position.FromFen("8/8/8/8/8/8/4P3/4K2k w - - 0 1");
        var mate = _tablebase.Probe(position);
        var rule = _tablebase.ProbeDtz(position);
        Assert.Equal(OutcomeKind.Win, mate.Kind);
        Assert.Equal(Outcome.Win(1), rule);
        Assert.True(mate.Plies > 20);
        foreach (var (move, outcome) in _tablebase.RankMovesUnderRule(position))
        {
            if (position[move.From].Type == PieceType.Pawn)
                Assert.True(outcome.Kind == OutcomeKind.Draw || outcome.Plies == 1, move.ToUci());
        }
    }

    [Fact]
    public void ADtzTableSavesAndLoadsAsOne()
    {
        var dtz = _tablebase.GetDtz(Material.Parse("KPvK"));
        string path = Path.Combine(Path.GetTempPath(), $"kpvk-{Guid.NewGuid():N}.cbz");
        try
        {
            dtz.Save(path);
            using var loaded = EndgameTable.Load(path);
            Assert.True(loaded.IsDtz);
            Assert.Null(loaded.Cap);
            for (long i = 0; i < dtz.Size; i++)
                Assert.Equal(dtz[i], loaded[i]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(OutcomeKind.Win, 3, OutcomeKind.Beyond, 10, OutcomeKind.Win, 3)]       // a win within N beats anything beyond N
    [InlineData(OutcomeKind.Win, 12, OutcomeKind.Beyond, 10, OutcomeKind.Beyond, 10)]  // beyond might be a quicker win
    [InlineData(OutcomeKind.Draw, 0, OutcomeKind.Beyond, 10, OutcomeKind.Beyond, 10)]  // ... or a win at all
    [InlineData(OutcomeKind.Loss, 4, OutcomeKind.Beyond, 10, OutcomeKind.Beyond, 10)]  // anything beyond is a slower loss or better
    [InlineData(OutcomeKind.Beyond, 12, OutcomeKind.Beyond, 10, OutcomeKind.Beyond, 10)]
    [InlineData(OutcomeKind.Loss, 4, OutcomeKind.Loss, 8, OutcomeKind.Loss, 8)]
    public void TheBetterOfTwoOutcomes(OutcomeKind a, int aPlies, OutcomeKind b, int bPlies, OutcomeKind kind, int plies)
    {
        var expected = new Outcome(kind, plies);
        Assert.Equal(expected, Outcome.Better(new Outcome(a, aPlies), new Outcome(b, bPlies)));
        Assert.Equal(expected, Outcome.Better(new Outcome(b, bPlies), new Outcome(a, aPlies)));
    }

    [Theory]
    [InlineData("KQvK", "KQvK", true)]
    [InlineData("kvkq", "KvKQ", false)]
    [InlineData("KRvKQ", "KRvKQ", false)]
    [InlineData("KNBvK", "KBNvK", true)]
    [InlineData("KBvKN", "KBvKN", true)]
    public void MaterialParsesAndKnowsWhichSideIsStronger(string text, string normalised, bool canonical)
    {
        var material = Material.Parse(text);
        Assert.Equal(normalised, material.ToString());
        Assert.Equal(canonical, material.IsCanonical);
        Assert.True(material.Canonical.IsCanonical);
    }

    [Fact]
    public void OutcomesFlipBetweenMovers()
    {
        Assert.Equal(Outcome.Win(1), Outcome.Loss(0).ForPreviousMover());
        Assert.Equal(Outcome.Loss(2), Outcome.Win(1).ForPreviousMover());
        Assert.Equal(Outcome.Draw, Outcome.Draw.ForPreviousMover());
        Assert.Equal(1, Outcome.Win(1).MovesToMate);
        Assert.Equal(10, Outcome.Win(19).MovesToMate);
        Assert.True(Outcome.Win(3).Score > Outcome.Win(5).Score);
        Assert.True(Outcome.Draw.Score > Outcome.Loss(40).Score);
        Assert.True(Outcome.Loss(40).Score > Outcome.Loss(2).Score);
    }
}

/// <summary>A test that only runs when CHESS_SLOW_TESTS is set (to a directory of solved tables).</summary>
public sealed class SlowFactAttribute : FactAttribute
{
    public const string Variable = "CHESS_SLOW_TESTS";

    public SlowFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Slow: set {Variable} to a directory of solved tables to run.";
    }
}

using ChessBruteforcer.Core.Endgame;
using ChessBruteforcer.Core.Records;

namespace ChessBruteforcer.Tests;

public class EndingStatsTests
{
    private static StoredGame FromFen(string fen, string moves) =>
        Pgn.Parse($"[SetUp \"1\"]\n[FEN \"{fen}\"]\n\n{moves} *")[0];

    [Fact]
    public void AGameIsCountedOnceInEachMaterialItPassesThrough()
    {
        var stats = new EndingStats();
        stats.Add(Pgn.Parse("1. e4 e5 *")[0]);   // never gets near 5 pieces
        // K+R+P v K+R: the pawn steps up and is taken, then the rooks come off: K+R v K+R, then K+R v K.
        stats.Add(FromFen("4k3/8/8/8/3r4/8/3P4/3RK3 w - - 0 1", "1. d3 Rxd3 2. Rxd3"));
        // Black's side stronger: K+R v K+R+P is K+R+P v K+R in the tables.
        stats.Add(FromFen("3rk3/3p4/8/3R4/8/8/8/4K3 b - - 0 1", "1... d6 2. Kf1"));

        Assert.Equal(3, stats.Games);
        Assert.Equal(2, stats.Reaching);
        Assert.Equal(0, stats.WithCastling);
        var names = stats.Materials.Select(m => m.ToString()).ToList();
        Assert.Equal(new[] { "KRPvKR", "KRvKR", "KRvK" }, names);
        var krpvkr = stats.Counts[0];
        Assert.Equal(2, krpvkr.Games);
        Assert.Equal(2, krpvkr.FirstEntries);
        Assert.Equal(1 + 2, krpvkr.Plies);   // d3; then d6, Kf1
        Assert.Equal(1, stats.Counts[1].Games);
        Assert.Equal(0, stats.Counts[1].FirstEntries);
        Assert.Equal(new[] { 0, 1, 2 }, stats.GameMaterials[0]);
        Assert.Equal(new[] { 0 }, stats.GameMaterials[1]);
    }

    [Fact]
    public void CastlingRightsAtFivePiecesAreNoticed()
    {
        var stats = new EndingStats();
        stats.Add(FromFen("4k3/8/8/8/8/8/4P3/R3K2R w KQ - 0 1", "1. O-O Kd7"));
        Assert.Equal(1, stats.WithCastling);
        Assert.Equal("KRRPvK", stats.Materials.Single().ToString());
    }

    [Fact]
    public void TheBuildOrderPutsCheapGainsFirstAndPromotionsBeforeTheirTables()
    {
        // K+R+P v K+P promotes into eight one-pawn tables; K+R+R v K+P (half the size) into none of them.
        var krpvkp = Material.Parse("KRPvKP");
        var missing = EndingStats.PromotionTargets(krpvkp).Append(krpvkp).ToList();
        var krrvkp = Material.Parse("KRRvKP");
        Assert.Contains(krrvkp, missing);
        var needs = Enumerable.Repeat(new[] { krpvkp }, 10).Append(new[] { krrvkp }).ToList();

        var order = EndingStats.BuildOrder(missing, needs);
        Assert.Equal(missing.Count, order.Distinct().Count());
        // One game for 466 M positions beats eleven for all nine tables (8.1 G).
        Assert.Equal(krrvkp, order[0]);
        Assert.Equal(krpvkp, order[^1]);   // after everything it promotes into
    }

    [Theory]
    [InlineData("KRPvKR", "KQRvKR KRRvKR KRBvKR KRNvKR")]
    [InlineData("KRPvKP", "KQRvKP KRRvKP KRBvKP KRNvKP KRPvKQ KRPvKR KRPvKB KRPvKN")]
    [InlineData("KPPvKP", "KQPvKP KRPvKP KBPvKP KNPvKP KPPvKQ KPPvKR KPPvKB KPPvKN")]
    [InlineData("KQRvK", "")]
    public void PromotionsLeadIntoTheTablesWithOnePawnFewer(string material, string targets)
    {
        var expected = targets.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order();
        var actual = EndingStats.PromotionTargets(Material.Parse(material)).Select(m => m.ToString()).Order();
        Assert.Equal(expected, actual);
    }
}

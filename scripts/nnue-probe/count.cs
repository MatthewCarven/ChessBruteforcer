#:project tree/src/ChessBruteforcer.Core/ChessBruteforcer.Core.csproj
#:property TargetFramework=net8.0
#:property PublishAot=false
// Scratch: how many labelled positions an analysed game gives, under the tuner's quiet rule and looser ones.
// Reads the first N games of an eval-games .cbg (games kept by scripts/eval-games.py, an Evals tag each).
using System.Globalization;
using ChessBruteforcer.Core.Records;

string file = args[0];
int limit = int.Parse(args[1]);
long games = 0, blitzUp = 0, plies = 0, numeric = 0, mates = 0, quietTuner = 0, quietTunerBlitzUp = 0, notInCheckAfter8 = 0, pieces5OrFewer = 0;
foreach (var game in GameFile.Read(file))
{
    if (games >= limit) break;
    games++;
    string[]? evals = game.Tag("Evals")?.Split(' ');
    if (evals is null) continue;
    bool slow = Speed(game) is "blitz" or "rapid" or "classical";
    if (slow) blitzUp++;
    var position = game.StartPosition();
    int pieces = 32;
    for (int ply = 0; ply < game.Moves.Count; ply++)
    {
        var move = game.Moves[ply];
        plies++;
        bool hasNumeric = ply >= 1 && ply - 1 < evals.Length
                          && int.TryParse(evals[ply - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        if (hasNumeric) numeric++;
        else if (ply >= 1 && ply - 1 < evals.Length && evals[ply - 1].StartsWith('M')) mates++;
        bool before = ply > 0 && (game.Moves[ply - 1].IsCapture || game.Moves[ply - 1].IsPromotion);
        bool inCheck = position.InCheck();
        if (hasNumeric && ply >= 16 && !inCheck) notInCheckAfter8++;
        if (hasNumeric && pieces <= 5) pieces5OrFewer++;
        if (hasNumeric && ply >= 16 && pieces > 5 && !before && !move.IsCapture && !move.IsPromotion && !inCheck)
        {
            quietTuner++;
            if (slow) quietTunerBlitzUp++;
        }
        if (move.IsCapture) pieces--;
        position.MakeMove(move);
    }
}
Console.WriteLine($"{games:N0} analysed games ({blitzUp:N0} blitz or slower), {plies:N0} plies = {(double)plies / games:0.0} a game");
Console.WriteLine($"positions with a numeric eval     {numeric,12:N0}  {(double)numeric / games,6:0.0} a game");
Console.WriteLine($"  with a mate score instead       {mates,12:N0}  {(double)mates / games,6:0.0} a game");
Console.WriteLine($"  after move 8, not in check      {notInCheckAfter8,12:N0}  {(double)notInCheckAfter8 / games,6:0.0} a game");
Console.WriteLine($"  the tuner's quiet rule          {quietTuner,12:N0}  {(double)quietTuner / games,6:0.0} a game");
Console.WriteLine($"  quiet rule, blitz or slower     {quietTunerBlitzUp,12:N0}  {(double)quietTunerBlitzUp / Math.Max(1, blitzUp),6:0.0} a slow game");
Console.WriteLine($"  5 pieces or fewer (tables)      {pieces5OrFewer,12:N0}");

static string Speed(StoredGame game)
{
    string[] parts = (game.Tag("TimeControl") ?? "-").Split('+');
    if (parts.Length != 2 || !int.TryParse(parts[0], out int seconds) || !int.TryParse(parts[1], out int increment))
        return "other";
    int estimate = seconds + 40 * increment;
    return estimate < 29 ? "ultrabullet" : estimate < 180 ? "bullet" : estimate < 480 ? "blitz"
         : estimate < 1500 ? "rapid" : "classical";
}

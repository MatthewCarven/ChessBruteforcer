#:project ../src/ChessBruteforcer.Core/ChessBruteforcer.Core.csproj
// Every position of real tables: a mate in d <= 100 plies must have the same result in the DTZ
// table at d plies or fewer (why TryProbeWithClock skips DTZ then).  Reads both files through
// small caches, 4 threads.  Columns: positions, mates within 100 plies (and of them 90-100),
// mates past 100, the longest, rule draws among wins and losses (and their shortest mate), exceptions.
//   dotnet run -c Release dtz-skip-check.cs -- <tables dir> KBBvKN KQPvKQ ...
using System.Diagnostics;
using ChessBruteforcer.Core.Endgame;

string dir = args[0];
foreach (string name in args.Skip(1))
{
    var clock = Stopwatch.StartNew();
    using var mate = EndgameTable.Load(Path.Combine(dir, name + ".cbt"), cache: new BlockCache(128));
    using var rule = EndgameTable.Load(Path.Combine(dir, name + ".cbz"), cache: new BlockCache(64));
    long size = mate.Size;
    const long Chunk = 1 << 22;
    long chunks = (size + Chunk - 1) / Chunk;
    long inTime = 0, late = 0, bad = 0, cursed = 0, nearEdge = 0, maxMate = 0, cursedShortest = long.MaxValue;
    string? first = null;
    Parallel.For(0L, chunks, new ParallelOptions { MaxDegreeOfParallelism = 4 }, c =>
    {
        long myIn = 0, myLate = 0, myBad = 0, myCursed = 0, myNear = 0, myMax = 0, myCursedShortest = long.MaxValue;
        for (long i = c * Chunk; i < Math.Min(size, (c + 1) * Chunk); i++)
        {
            if (mate[i] is not { } m || m.Kind == OutcomeKind.Draw)
                continue;
            myMax = Math.Max(myMax, m.Plies);
            var z = rule[i]!.Value;
            if (z.Kind == OutcomeKind.Draw)
            {
                myCursed++;
                myCursedShortest = Math.Min(myCursedShortest, m.Plies);
            }
            if (m.Plies > EndgameTable.RulePlies)
            {
                myLate++;
                continue;
            }
            myIn++;
            if (m.Plies >= 90)
                myNear++;
            if (z.Kind != m.Kind || z.Plies > m.Plies)
            {
                myBad++;
                if (first is null)
                    Interlocked.CompareExchange(ref first, $"{mate.PositionAt(i).ToFen()}: mate {m}, rule {z}", null);
            }
        }
        Interlocked.Add(ref inTime, myIn);
        Interlocked.Add(ref late, myLate);
        Interlocked.Add(ref bad, myBad);
        Interlocked.Add(ref cursed, myCursed);
        Interlocked.Add(ref nearEdge, myNear);
        long seen;
        while ((seen = Interlocked.Read(ref maxMate)) < myMax && Interlocked.CompareExchange(ref maxMate, myMax, seen) != seen) { }
        while ((seen = Interlocked.Read(ref cursedShortest)) > myCursedShortest
               && Interlocked.CompareExchange(ref cursedShortest, myCursedShortest, seen) != seen) { }
    });
    Console.WriteLine($"| {name} | {size:N0} | {inTime:N0} | {nearEdge:N0} | {late:N0} | {maxMate} | {cursed:N0} | " +
                      $"{(cursed > 0 ? cursedShortest.ToString() : "")} | {bad} | {clock.Elapsed.TotalSeconds:0} s |" +
                      (first is null ? "" : $" first: {first}"));
}

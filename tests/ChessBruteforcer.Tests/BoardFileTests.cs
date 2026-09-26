using ChessBruteforcer.Core;

namespace ChessBruteforcer.Tests;

public sealed class BoardFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cbb-tests").FullName;

    private string PathFor(string name) => Path.Combine(_dir, name);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly PackedBoard Start = Fen.Parse(Fen.StartPosition);
    private static readonly PackedBoard Kings = Fen.Parse("4k3/8/8/8/8/8/8/4K3");
    private static readonly PackedBoard Queen = Fen.Parse("4k3/8/8/8/8/8/8/3QK3");

    [Fact]
    public void WriteThenReadGivesTheSameBoards()
    {
        string file = PathFor("a.cbb");
        BoardFile.Write(file, new[] { Start, Kings, Queen });
        Assert.Equal(3 * 48, new FileInfo(file).Length);
        Assert.Equal(new[] { Start, Kings, Queen }, BoardFile.ReadAll(file));
        Assert.Equal(3, BoardFile.Count(file));
    }

    [Fact]
    public void AppendAddsToTheEnd()
    {
        string file = PathFor("b.cbb");
        BoardFile.Append(file, new[] { Start });
        BoardFile.Append(file, new[] { Kings });
        Assert.Equal(new[] { Start, Kings }, BoardFile.ReadAll(file));
    }

    [Fact]
    public void DedupeSortsAndRemovesDuplicates()
    {
        string file = PathFor("c.cbb");
        BoardFile.Write(file, new[] { Queen, Start, Kings, Start, Queen, Queen });
        var (before, after) = BoardFile.Deduplicate(file);
        Assert.Equal(6, before);
        Assert.Equal(3, after);

        var boards = BoardFile.ReadAll(file);
        Assert.Equal(3, boards.Count);
        Assert.Equal(boards.OrderBy(b => b).ToList(), boards);
        Assert.Equal(new[] { Kings, Queen, Start }.OrderBy(b => b), boards);
    }

    [Fact]
    public void DedupeCanWriteElsewhere()
    {
        string file = PathFor("d.cbb");
        string output = PathFor("d-unique.cbb");
        BoardFile.Write(file, new[] { Start, Start });
        BoardFile.Deduplicate(file, output);
        Assert.Equal(2, BoardFile.Count(file));
        Assert.Equal(1, BoardFile.Count(output));
    }

    [Fact]
    public void MeaninglessBoardsSurviveTheRoundTrip()
    {
        string file = PathFor("e.cbb");
        var junk = new PackedBoard(Enumerable.Repeat((byte)0xFF, 48).ToArray());
        BoardFile.Write(file, new[] { junk });
        Assert.Equal(junk, BoardFile.ReadAll(file).Single());
    }

    [Fact]
    public void TruncatedFilesAreRejected()
    {
        string file = PathFor("f.cbb");
        File.WriteAllBytes(file, new byte[50]);
        Assert.Throws<InvalidDataException>(() => BoardFile.ReadAll(file));
        Assert.Throws<InvalidDataException>(() => BoardFile.Append(file, new[] { Start }));
    }
}

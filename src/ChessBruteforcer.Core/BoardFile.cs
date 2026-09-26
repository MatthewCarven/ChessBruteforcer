namespace ChessBruteforcer.Core;

/// <summary>
/// Reads and writes board files: nothing but 48-byte packed boards, back to
/// back.  No header, no separators, so:
///
/// * record n lives at byte offset n * 48, and the record count is length / 48;
/// * two files can be joined with plain concatenation;
/// * identical positions are identical records, so deduplication is sort + unique;
/// * sorted files put near-identical boards next to each other, which is what
///   folder- or file-level compression feeds on.
///
/// The conventional extension is .cbb (chess board binary).
/// </summary>
public static class BoardFile
{
    public const string Extension = ".cbb";

    public static long Count(string path)
    {
        long length = new FileInfo(path).Length;
        CheckLength(path, length);
        return length / PackedBoard.ByteLength;
    }

    /// <summary>Stream every board in the file without loading it all.</summary>
    public static IEnumerable<PackedBoard> Read(string path)
    {
        using var stream = File.OpenRead(path);
        CheckLength(path, stream.Length);
        var buffer = new byte[PackedBoard.ByteLength];
        while (true)
        {
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read == 0)
                yield break;
            if (read != buffer.Length)
                throw new InvalidDataException($"{path} ends part way through a board.");
            yield return new PackedBoard(buffer);
        }
    }

    public static List<PackedBoard> ReadAll(string path) => Read(path).ToList();

    /// <summary>Replace the file with exactly these boards.</summary>
    public static long Write(string path, IEnumerable<PackedBoard> boards) =>
        WriteTo(path, boards, FileMode.Create);

    /// <summary>Add boards to the end of the file, creating it if needed.</summary>
    public static long Append(string path, IEnumerable<PackedBoard> boards)
    {
        if (File.Exists(path))
            CheckLength(path, new FileInfo(path).Length);
        return WriteTo(path, boards, FileMode.Append);
    }

    /// <summary>
    /// Sort the boards and drop duplicates, writing the result to
    /// <paramref name="outputPath"/> (the input file itself when null).
    /// Loads the whole file; fine for millions of boards, see TODO.md for
    /// the external-sort version.
    /// </summary>
    public static (long Before, long After) Deduplicate(string path, string? outputPath = null)
    {
        var boards = ReadAll(path);
        long before = boards.Count;
        boards.Sort();
        var unique = new List<PackedBoard>(boards.Count);
        foreach (var board in boards)
        {
            if (unique.Count == 0 || !unique[^1].Equals(board))
                unique.Add(board);
        }

        string target = outputPath ?? path;
        string temp = target + ".tmp";
        Write(temp, unique);
        File.Move(temp, target, overwrite: true);
        return (before, unique.Count);
    }

    private static long WriteTo(string path, IEnumerable<PackedBoard> boards, FileMode mode)
    {
        long written = 0;
        using var stream = new FileStream(path, mode, FileAccess.Write);
        foreach (var board in boards)
        {
            stream.Write(board.Bytes);
            written++;
        }
        return written;
    }

    private static void CheckLength(string path, long length)
    {
        if (length % PackedBoard.ByteLength != 0)
            throw new InvalidDataException(
                $"{path} is {length} bytes, not a whole number of {PackedBoard.ByteLength}-byte boards.");
    }
}

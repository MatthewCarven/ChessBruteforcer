using System.Text;
using ChessBruteforcer.Core.Game;

namespace ChessBruteforcer.Core.Records;

/// <summary>
/// Reads and writes game files: the 4-byte magic "CBG1", then games back to
/// back, each one
///
/// <code>
///   tag count                      varint
///   per tag: name, value           length-prefixed UTF-8
///   result                         1 byte: 0 = *, 1 = 1-0, 2 = 0-1, 3 = 1/2-1/2
///   move count                     varint
///   moves                          1 byte each, see MoveCode
/// </code>
///
/// The start position isn't stored separately: it's the standard one unless
/// the tags hold a FEN.  Games vary in length, so finding game n means
/// reading the n - 1 before it.
///
/// The conventional extension is .cbg (chess board games).
/// </summary>
public static class GameFile
{
    public const string Extension = ".cbg";
    private static readonly byte[] Magic = "CBG1"u8.ToArray();

    /// <summary>Stream every game in the file without loading it all.</summary>
    public static IEnumerable<StoredGame> Read(string path)
    {
        using var stream = new BufferedStream(File.OpenRead(path));
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        CheckMagic(path, reader);
        for (int number = 1; stream.Position < stream.Length; number++)
        {
            StoredGame game;
            try
            {
                game = ReadGame(reader);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException($"{path} ends part way through game {number}.");
            }
            catch (InvalidDataException e)
            {
                throw new InvalidDataException($"{path}, game {number}: {e.Message}");
            }
            yield return game;
        }
    }

    /// <summary>Game <paramref name="number"/> (the first is 1).  The games before it are skipped over, not decoded.</summary>
    public static StoredGame Read(string path, long number)
    {
        if (number < 1)
            throw new ArgumentException("Games are numbered from 1.");
        using var stream = new BufferedStream(File.OpenRead(path));
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        CheckMagic(path, reader);
        try
        {
            for (long skipped = 1; skipped < number; skipped++)
            {
                if (stream.Position >= stream.Length)
                    throw new ArgumentException($"{path} has only {skipped - 1:N0} games.");
                SkipGame(reader);
                if (stream.Position > stream.Length)
                    throw new EndOfStreamException();
            }
            if (stream.Position >= stream.Length)
                throw new ArgumentException($"{path} has only {number - 1:N0} games.");
            return ReadGame(reader);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException($"{path} ends part way through a game.");
        }
    }

    public static long Count(string path)
    {
        using var stream = new BufferedStream(File.OpenRead(path));
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        CheckMagic(path, reader);
        long count = 0;
        try
        {
            for (; stream.Position < stream.Length; count++)
                SkipGame(reader);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException($"{path} ends part way through game {count + 1}.");
        }
        if (stream.Position > stream.Length)
            throw new InvalidDataException($"{path} ends part way through game {count}.");
        return count;
    }

    /// <summary>Replace the file with exactly these games.  Written to a temporary file and renamed, so an interrupted write leaves the old file alone.</summary>
    public static (long Games, long Moves) Write(string path, IEnumerable<StoredGame> games)
    {
        string temp = path + ".tmp";
        (long, long) counts;
        using (var stream = File.Create(temp))
        {
            stream.Write(Magic);
            counts = WriteGames(stream, games);
        }
        File.Move(temp, path, overwrite: true);
        return counts;
    }

    /// <summary>Add games to the end of the file, creating it if needed.</summary>
    public static (long Games, long Moves) Append(string path, IEnumerable<StoredGame> games)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            return Write(path, games);
        using (var reader = new BinaryReader(File.OpenRead(path)))
            CheckMagic(path, reader);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write);
        return WriteGames(stream, games);
    }

    private static (long Games, long Moves) WriteGames(Stream stream, IEnumerable<StoredGame> games)
    {
        long gameCount = 0, moveCount = 0;
        using var writer = new BinaryWriter(new BufferedStream(stream), Encoding.UTF8, leaveOpen: false);
        foreach (var game in games)
        {
            WriteGame(writer, game);
            gameCount++;
            moveCount += game.Moves.Count;
        }
        return (gameCount, moveCount);
    }

    private static void WriteGame(BinaryWriter writer, StoredGame game)
    {
        int result = IndexOfResult(game.Result);
        if (result < 0)
            throw new ArgumentException($"'{game.Result}' is not a PGN result.");

        writer.Write7BitEncodedInt(game.Tags.Count);
        foreach (var (name, value) in game.Tags)
        {
            writer.Write(name);
            writer.Write(value);
        }
        writer.Write((byte)result);
        writer.Write7BitEncodedInt(game.Moves.Count);
        var position = game.StartPosition();
        foreach (var move in game.Moves)
        {
            writer.Write(MoveCode.Encode(position, move));
            position.MakeMove(move);
        }
    }

    private static StoredGame ReadGame(BinaryReader reader)
    {
        int tagCount = reader.Read7BitEncodedInt();
        var tags = new List<(string, string)>(tagCount);
        for (int i = 0; i < tagCount; i++)
            tags.Add((reader.ReadString(), reader.ReadString()));
        int result = reader.ReadByte();
        if (result >= StoredGame.Results.Count)
            throw new InvalidDataException($"result code {result} means nothing.");
        int moveCount = reader.Read7BitEncodedInt();

        var moves = new List<Move>(moveCount);
        var game = new StoredGame(tags, moves, StoredGame.Results[result]);
        Position position;
        try
        {
            position = game.StartPosition();
        }
        catch (FormatException e)
        {
            throw new InvalidDataException($"bad FEN tag: {e.Message}");
        }
        for (int i = 0; i < moveCount; i++)
        {
            var move = MoveCode.Decode(position, reader.ReadByte());
            position.MakeMove(move);
            moves.Add(move);
        }
        return game;
    }

    /// <summary>Step over one game: read its lengths, jump its move bytes.</summary>
    private static void SkipGame(BinaryReader reader)
    {
        int tagCount = reader.Read7BitEncodedInt();
        for (int i = 0; i < tagCount * 2; i++)
            reader.ReadString();
        reader.ReadByte();
        int moveCount = reader.Read7BitEncodedInt();
        reader.BaseStream.Seek(moveCount, SeekOrigin.Current);
    }

    private static int IndexOfResult(string result)
    {
        for (int i = 0; i < StoredGame.Results.Count; i++)
            if (StoredGame.Results[i] == result)
                return i;
        return -1;
    }

    private static void CheckMagic(string path, BinaryReader reader)
    {
        var magic = reader.ReadBytes(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException($"{path} is not a game file (no CBG1 header).");
    }
}

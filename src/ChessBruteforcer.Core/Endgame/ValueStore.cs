using System.IO.MemoryMappedFiles;
using System.Text;

namespace ChessBruteforcer.Core.Endgame;

/// <summary>
/// A whole table's values while it is solved slice by slice: in memory for a
/// small table, or straight in its file for a big one (1.9 GB for five pieces
/// with a pawn), so the table never has to fit in memory at once.
/// </summary>
internal interface IValueStore : IDisposable
{
    short this[long index] { get; set; }
}

internal sealed class ArrayStore(short[] values) : IValueStore
{
    public short[] Values { get; } = values;

    public short this[long index]
    {
        get => Values[index];
        set => Values[index] = value;
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// A table file being written in place: its header first, then the values,
/// every one set to <paramref name="fill"/> to begin with.  Written to a
/// temporary name; <see cref="Finish"/> renames it once the solve is done.
/// </summary>
internal sealed class FileStore : IValueStore
{
    private readonly string _path;
    private readonly string _temp;
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly long _dataOffset;
    private readonly int _width;   // bytes a value: 2, or 1 for DTZ (see EndgameTable.ToByte)

    public FileStore(string path, string magic, Material material, long size, short fill, int width = 2)
    {
        _width = width;
        _path = path;
        _temp = path + ".tmp";
        using (var stream = File.Create(_temp))
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes(magic));
            writer.Write(material.ToString());
            writer.Write(size);
            writer.Flush();
            _dataOffset = stream.Position;
            var chunk = new short[1 << 20];
            Array.Fill(chunk, fill);
            var narrow = new sbyte[chunk.Length];
            Array.Fill(narrow, EndgameTable.ToByte(fill));
            var bytes = width == 1 ? System.Runtime.InteropServices.MemoryMarshal.AsBytes(narrow.AsSpan())
                                   : System.Runtime.InteropServices.MemoryMarshal.AsBytes(chunk.AsSpan());
            for (long written = 0; written < size; written += chunk.Length)
                stream.Write(bytes[..(int)(Math.Min(chunk.Length, size - written) * width)]);
        }
        _file = MemoryMappedFile.CreateFromFile(_temp, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
        _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
    }

    public short this[long index]
    {
        get => _width == 1 ? EndgameTable.FromByte(_view.ReadSByte(_dataOffset + index))
                           : _view.ReadInt16(_dataOffset + index * sizeof(short));
        set
        {
            if (_width == 1)
                _view.Write(_dataOffset + index, EndgameTable.ToByte(value));
            else
                _view.Write(_dataOffset + index * sizeof(short), value);
        }
    }

    /// <summary>Flush, close, and give the file its real name.</summary>
    public void Finish()
    {
        _view.Flush();
        Dispose();
        File.Move(_temp, _path, overwrite: true);
    }

    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }
}

using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace ChessBruteforcer.Core.Endgame;

/// <summary>
/// A table file compressed in blocks ("CBC1"), for disk: the values cut into
/// blocks of 64 KB, each compressed on its own with Brotli, so a probe
/// decompresses only the block it needs.  Measured on the 5-piece tables,
/// ~10x smaller than the plain file (draws, holes and runs of one value
/// squeeze well).
///
///   "CBC1", kind (0 = mate distances, 2 bytes a value; 1 = DTZ, 1 byte),
///   material, value count, values per block, block count,
///   block offsets (block count + 1 longs, from the start of the data),
///   then the blocks.
///
/// Only complete tables are compressed (not capped ones with a frontier).
/// </summary>
internal sealed class CompressedTable : IDisposable
{
    public const string Magic = "CBC1";
    private const int BlockBytes = 1 << 16;
    private const int CacheSlots = 1024;   // decompressed blocks kept: 64 MB at most per table

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly long[] _offsets;
    private readonly long _dataStart;
    private readonly int _valuesPerBlock;
    private readonly byte[]?[] _cache = new byte[CacheSlots][];
    private readonly int[] _cachedBlock = new int[CacheSlots];
    private readonly object _lock = new();

    public Material Material { get; }
    public bool IsDtz { get; }
    public long Size { get; }
    public int Width => IsDtz ? 1 : 2;

    private CompressedTable(string path, Material material, bool dtz, long size, int valuesPerBlock,
                            long[] offsets, long dataStart)
    {
        Material = material;
        IsDtz = dtz;
        Size = size;
        _valuesPerBlock = valuesPerBlock;
        _offsets = offsets;
        _dataStart = dataStart;
        Array.Fill(_cachedBlock, -1);
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    public static bool IsCompressedFile(string path)
    {
        using var stream = File.OpenRead(path);
        var magic = new byte[Magic.Length];
        return stream.Read(magic) == magic.Length && Encoding.ASCII.GetString(magic) == Magic;
    }

    public static CompressedTable Open(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.ASCII);
        if (Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length)) != Magic)
            throw new InvalidDataException($"{path} is not a compressed table.");
        bool dtz = reader.ReadByte() == 1;
        var material = Material.Parse(reader.ReadString());
        long size = reader.ReadInt64();
        int valuesPerBlock = reader.ReadInt32();
        int blocks = reader.ReadInt32();
        if (size != EndgameTable.TableSize(material))
            throw new InvalidDataException($"{path} has {size} values; {material} needs {EndgameTable.TableSize(material)}.");
        var offsets = new long[blocks + 1];
        for (int i = 0; i <= blocks; i++)
            offsets[i] = reader.ReadInt64();
        long dataStart = reader.BaseStream.Position;
        if (reader.BaseStream.Length < dataStart + offsets[blocks])
            throw new InvalidDataException($"{path} is truncated.");
        return new CompressedTable(path, material, dtz, size, valuesPerBlock, offsets, dataStart);
    }

    /// <summary>The stored value at an index (as the plain table would hold it).</summary>
    public short this[long index]
    {
        get
        {
            int block = (int)(index / _valuesPerBlock);
            int within = (int)(index % _valuesPerBlock);
            lock (_lock)
            {
                var bytes = Block(block);
                return IsDtz ? EndgameTable.FromByte((sbyte)bytes[within])
                             : BitConverter.ToInt16(bytes, within * sizeof(short));
            }
        }
    }

    /// <summary>A block, decompressed: from the cache, or read and put in its slot.</summary>
    private byte[] Block(int block)
    {
        int slot = block % CacheSlots;
        if (_cachedBlock[slot] == block)
            return _cache[slot]!;
        long start = _offsets[block], length = _offsets[block + 1] - start;
        var packed = new byte[length];
        _view.ReadArray(_dataStart + start, packed, 0, packed.Length);
        var bytes = _cache[slot] ??= new byte[BlockBytes];
        if (!BrotliDecoder.TryDecompress(packed, bytes, out int written) || written != BlockLength(block))
            throw new InvalidDataException($"{Material}: block {block} doesn't decompress.");
        _cachedBlock[slot] = block;
        return bytes;
    }

    private int BlockLength(int block) =>
        (int)(Math.Min(_valuesPerBlock, Size - (long)block * _valuesPerBlock) * Width);

    /// <summary>
    /// Write a table compressed into <paramref name="file"/>.  Every block is
    /// decompressed again and checked against the original before it is
    /// written.  (Replacing the plain file is the caller's job, once the
    /// table it was read from is closed.)  Returns the compressed size.
    /// </summary>
    public static long Write(EndgameTable table, string file, Func<long, short> raw, int quality)
    {
        int width = table.IsDtz ? 1 : 2;
        int valuesPerBlock = BlockBytes / width;
        int blocks = (int)((table.Size + valuesPerBlock - 1) / valuesPerBlock);
        var offsets = new long[blocks + 1];
        using (var stream = File.Create(file))
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes(Magic));
            writer.Write((byte)(table.IsDtz ? 1 : 0));
            writer.Write(table.Material.ToString());
            writer.Write(table.Size);
            writer.Write(valuesPerBlock);
            writer.Write(blocks);
            long offsetsAt = stream.Position;
            writer.Write(new byte[(blocks + 1) * sizeof(long)]);   // filled in at the end
            writer.Flush();

            // Blocks in batches, compressed in parallel, written in order.
            const int Batch = 256;
            var plain = new byte[Batch][];
            var packed = new byte[Batch][];
            var packedLength = new int[Batch];
            for (int i = 0; i < Batch; i++)
            {
                plain[i] = new byte[BlockBytes];
                packed[i] = new byte[BrotliEncoder.GetMaxCompressedLength(BlockBytes)];
            }
            long position = 0;
            for (int first = 0; first < blocks; first += Batch)
            {
                int count = Math.Min(Batch, blocks - first);
                for (int b = 0; b < count; b++)
                {
                    long start = (long)(first + b) * valuesPerBlock;
                    int n = (int)Math.Min(valuesPerBlock, table.Size - start);
                    var bytes = plain[b];
                    for (int i = 0; i < n; i++)
                    {
                        short value = raw(start + i);
                        if (width == 1)
                            bytes[i] = (byte)EndgameTable.ToByte(value);
                        else
                            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), value);
                    }
                }
                Parallel.For(0, count, b =>
                {
                    long start = (long)(first + b) * valuesPerBlock;
                    int length = (int)(Math.Min(valuesPerBlock, table.Size - start) * width);
                    if (!BrotliEncoder.TryCompress(plain[b].AsSpan(0, length), packed[b], out packedLength[b], quality, 22))
                        throw new InvalidOperationException("Brotli could not compress a block.");
                    // Check it comes back exactly before trusting it.
                    var check = new byte[length];
                    if (!BrotliDecoder.TryDecompress(packed[b].AsSpan(0, packedLength[b]), check, out int back)
                        || back != length || !check.AsSpan().SequenceEqual(plain[b].AsSpan(0, length)))
                        throw new InvalidOperationException($"{table.Material}: block {first + b} did not survive compression.");
                });
                for (int b = 0; b < count; b++)
                {
                    offsets[first + b] = position;
                    stream.Write(packed[b], 0, packedLength[b]);
                    position += packedLength[b];
                }
            }
            offsets[blocks] = position;
            stream.Position = offsetsAt;
            foreach (long offset in offsets)
                writer.Write(offset);
            writer.Flush();
        }
        return new FileInfo(file).Length;
    }

    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }
}

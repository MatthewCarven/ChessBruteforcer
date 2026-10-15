// What smaller blocks would cost and save, without rewriting a table: sample 64 KB blocks of
// compressed tables (CBC1, see CompressedTable.cs), cut each into blocks of 32, 16, 8 and 4 KB,
// compress those as the tables are (Brotli quality 10, window 22), and time decompressing a
// block of each size.  Nothing is written.  One thread (run it alone for the times).
//   dotnet run -c Release block-size-probe.cs -- <table file>... [--blocks 48]
using System.Diagnostics;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text;

int sample = 48;
var files = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--blocks") sample = int.Parse(args[++i]);
    else files.Add(args[i]);
}
int[] sizes = { 65536, 32768, 16384, 8192, 4096 };
Console.WriteLine("| table | block | compressed, of plain | x smaller | us to decompress a block | us a KB |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|");
var totalPlain = new long[sizes.Length];
var totalPacked = new long[sizes.Length];
var totalUs = new double[sizes.Length];
var totalBlocks = new long[sizes.Length];
foreach (string path in files)
{
    using var reader = new BinaryReader(File.OpenRead(path), Encoding.ASCII);
    if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "CBC1")
        throw new InvalidDataException($"{path} is not compressed");
    bool dtz = reader.ReadByte() == 1;
    string material = reader.ReadString();
    long size = reader.ReadInt64();
    int valuesPerBlock = reader.ReadInt32();
    int blocks = reader.ReadInt32();
    var offsets = new long[blocks + 1];
    for (int i = 0; i <= blocks; i++)
        offsets[i] = reader.ReadInt64();
    long dataStart = reader.BaseStream.Position;
    using var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
    using var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    var random = new Random(1);
    var picks = Enumerable.Range(0, Math.Min(sample, blocks - 1)).Select(_ => random.Next(blocks - 1)).Distinct().ToArray();
    var plain = new byte[65536];
    var back = new byte[65536];
    var packed = new byte[BrotliEncoder.GetMaxCompressedLength(65536)];
    var plainBytes = new long[sizes.Length];
    var packedBytes = new long[sizes.Length];
    var ticks = new long[sizes.Length];
    var decodes = new long[sizes.Length];
    foreach (int block in picks)
    {
        int length = (int)(offsets[block + 1] - offsets[block]);
        var stored = new byte[length];
        view.ReadArray(dataStart + offsets[block], stored, 0, length);
        if (!BrotliDecoder.TryDecompress(stored, plain, out int written) || written != 65536)
            throw new InvalidDataException($"{path}: block {block}");
        for (int s = 0; s < sizes.Length; s++)
        {
            int bs = sizes[s];
            for (int at = 0; at < 65536; at += bs)
            {
                var part = plain.AsSpan(at, bs);
                byte[] mine;
                if (bs == 65536)
                    mine = stored;   // as the file has it
                else
                {
                    if (!BrotliEncoder.TryCompress(part, packed, out int n, 10, 22))
                        throw new InvalidOperationException("Brotli could not compress");
                    mine = packed[..n];
                }
                plainBytes[s] += bs;
                packedBytes[s] += mine.Length;
                // Time the decompression: the median of 5 tries, each long enough to measure.
                int reps = Math.Max(1, 65536 / bs);
                var tries = new long[5];
                for (int t = 0; t < tries.Length; t++)
                {
                    long began = Stopwatch.GetTimestamp();
                    for (int r = 0; r < reps; r++)
                        if (!BrotliDecoder.TryDecompress(mine, back, out int got) || got != bs)
                            throw new InvalidDataException("round trip");
                    tries[t] = (Stopwatch.GetTimestamp() - began) / reps;
                }
                Array.Sort(tries);
                ticks[s] += tries[2];
                decodes[s]++;
                if (!back.AsSpan(0, bs).SequenceEqual(part))
                    throw new InvalidDataException("round trip");
            }
        }
    }
    for (int s = 0; s < sizes.Length; s++)
    {
        double us = ticks[s] * 1e6 / Stopwatch.Frequency / decodes[s];
        Console.WriteLine($"| {material}.{(dtz ? "cbz" : "cbt")} | {sizes[s] / 1024} KB | {(double)packedBytes[s] / plainBytes[s]:P1} | " +
                          $"{(double)plainBytes[s] / packedBytes[s]:0.0} | {us:0.0} | {us / (sizes[s] / 1024.0):0.00} |");
        totalPlain[s] += plainBytes[s];
        totalPacked[s] += packedBytes[s];
        totalUs[s] += ticks[s] * 1e6 / Stopwatch.Frequency;
        totalBlocks[s] += decodes[s];
    }
}
Console.WriteLine();
Console.WriteLine("| all sampled | block | x smaller | size against 64 KB blocks | us a block | us against 64 KB |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|");
for (int s = 0; s < sizes.Length; s++)
    Console.WriteLine($"| | {sizes[s] / 1024} KB | {(double)totalPlain[s] / totalPacked[s]:0.0} | " +
                      $"{(double)totalPacked[s] / totalPacked[0]:0.00}x | {totalUs[s] / totalBlocks[s]:0.0} | " +
                      $"{totalUs[s] / totalBlocks[s] / (totalUs[0] / totalBlocks[0]):0.00} |");

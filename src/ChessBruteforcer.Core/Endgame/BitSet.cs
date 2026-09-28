namespace ChessBruteforcer.Core.Endgame;

/// <summary>
/// One bit per index, where a <c>bool[]</c> spends a byte: the solver keeps
/// two flags per table slot, and a 5-piece table has hundreds of millions.
/// </summary>
internal sealed class BitSet
{
    private readonly ulong[] _words;

    public BitSet(long length) => _words = new ulong[(length + 63) >> 6];

    public long ByteCount => _words.LongLength * sizeof(ulong);

    public void Clear() => Array.Clear(_words);

    /// <summary>The first set bit at or after <paramref name="from"/>, or -1.</summary>
    public long NextSetBit(long from)
    {
        long word = from >> 6;
        if (word >= _words.LongLength)
            return -1;
        ulong bits = _words[word] & (~0UL << (int)(from & 63));
        while (bits == 0)
        {
            if (++word >= _words.LongLength)
                return -1;
            bits = _words[word];
        }
        return (word << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits);
    }

    public void Write(BinaryWriter writer)
    {
        foreach (ulong word in _words)
            writer.Write(word);
    }

    public void Read(BinaryReader reader)
    {
        for (long i = 0; i < _words.LongLength; i++)
            _words[i] = reader.ReadUInt64();
    }

    public bool this[long index]
    {
        get => (_words[index >> 6] & (1UL << (int)(index & 63))) != 0;
        set
        {
            ulong bit = 1UL << (int)(index & 63);
            if (value)
                _words[index >> 6] |= bit;
            else
                _words[index >> 6] &= ~bit;
        }
    }
}

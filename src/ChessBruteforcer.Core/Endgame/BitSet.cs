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

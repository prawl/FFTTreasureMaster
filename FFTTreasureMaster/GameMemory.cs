namespace FFTTreasureMaster;

/// <summary>
/// The slice of memory access the treasure logic needs, behind an interface so the arm gate
/// and the tile-hold path are unit-testable with a fake memory -- no live game. LiveMemory is
/// the production adapter over the RPM/WPM-backed <see cref="Mem"/>.
/// </summary>
internal interface IGameMemory
{
    byte U8(long addr);
    ushort U16(long addr);
    /// <summary>Read an 8-byte little-endian value (e.g. a heap pointer dereferenced from a
    /// static slot). Default 0 (test fakes override; LiveMemory delegates to Mem.U64).</summary>
    ulong U64(long addr) => 0UL;
    /// <summary>Read len bytes; false on a failed/partial read (callers that scan handle it).</summary>
    bool TryReadBytes(long addr, int len, out byte[] buf)
    {
        buf = new byte[len];
        return false;
    }
    /// <summary>Write a single byte to <paramref name="addr"/>. Default no-op (test fakes
    /// override; LiveMemory delegates to Mem.W8).</summary>
    void W8(long addr, byte value) { }
    /// <summary>Write a 4-byte little-endian value (EnhancedMarker int fields). Default no-op
    /// (test fakes override; LiveMemory delegates to Mem.W32).</summary>
    void W32(long addr, uint value) { }
    bool Readable(long addr, int len) => false;
    bool Writable(long addr, int len) => false;
}

internal sealed class LiveMemory : IGameMemory
{
    public byte U8(long addr) => Mem.U8(addr);
    public ushort U16(long addr) => Mem.U16(addr);
    public ulong U64(long addr) => Mem.U64(addr);
    public bool TryReadBytes(long addr, int len, out byte[] buf) => Mem.TryReadBytes(addr, len, out buf);
    public void W8(long addr, byte value) => Mem.W8(addr, value);
    public void W32(long addr, uint value) => Mem.W32(addr, value);
    public bool Readable(long addr, int len) => Mem.Readable(addr, len);
    public bool Writable(long addr, int len) => Mem.Writable(addr, len);
}

using System;

namespace FFTTreasureMaster;

/// <summary>
/// The pure decisions behind the Treasure Master module, tested directly since they touch no
/// memory. The stateful tick loop lives in TreasureMaster.cs.
///
/// MapIdValid accepts 1 to 127, the only live battle map ids. Fnv1a64 is the standard 64 bit
/// FNV-1a, shared verbatim with the Python capture tool so the two cannot drift. ClassifyAddr
/// is the per byte safety contract over the only legitimate values, 0x00, 0x01 and MarkValue,
/// and anything else is Foreign and never written. WantWrite is a flat write of MarkValue, with
/// no clear path, since the engine clears marks itself. DecideArm arms once enough addresses
/// are ok and otherwise retries forever. Foreign bytes are not a veto, because they are off
/// screen render bytes that return to Resting when the tile scrolls back, and TileHolder never
/// writes them. Disarm is reserved for fingerprint mismatches in TreasureMaster.TickArming.
/// BuildKeyMatches is exact equality on both PE header fields, and a mismatch is a hard global
/// disarm until re-capture.
/// </summary>
internal sealed partial class TreasureMaster
{
    // ---- #3 per-byte classification ----

    /// <summary>
    /// The runtime-visible states of a tile's render-flag byte.
    /// Only <see cref="Resting"/> bytes are candidates for a <see cref="WantWrite"/>; a
    /// <see cref="Held"/> byte is already marked; a <see cref="Foreign"/> byte is never written.
    /// </summary>
    internal enum AddrState { Resting, Held, Foreign }

    // ---- #5 arm verdict ----

    /// <summary>Outcome of a per-tick address audit.
    /// <see cref="Disarm"/> is no longer returned by <see cref="DecideArm"/> (foreign bytes
    /// at arm time are off-screen render bytes, not a signal to disarm). It remains in the enum
    /// for the fingerprint-mismatch path in <see cref="TickArming"/>, which bypasses DecideArm.</summary>
    internal enum ArmVerdict { Arm, Retry, Disarm }

    // ---- #1 MapIdValid ----

    /// <summary>
    /// True for map ids 1 to 127, the valid LiveBattleMapId range. 0 is the uninitialized value
    /// and 128 and up are not assigned. Never read outside <see cref="BattleState.InLiveBattle"/>.
    /// </summary>
    internal static bool MapIdValid(byte id) => id >= 1 && id <= 127;

    // ---- #2 Fnv1a64 ----

    // Pinned constants that must stay bit for bit identical to the Python capture tool.
    private const ulong FnvBasis = 0xcbf29ce484222325UL;
    private const ulong FnvPrime = 0x00000100000001b3UL;

    /// <summary>
    /// Standard FNV-1a 64-bit hash. Pinned test vectors (also in the Python self-test):
    ///   empty   -> 0xcbf29ce484222325
    ///   "a"     -> 0xaf63dc4c8601ec8c
    ///   "foobar" -> 0x85944171f73967e8
    /// </summary>
    internal static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        ulong h = FnvBasis;
        foreach (byte b in data)
        {
            h ^= b;
            h *= FnvPrime;
        }
        return h;
    }

    // ---- #3 ClassifyAddr ----

    /// <summary>
    /// Maps a raw byte from a tile's render flag address to its <see cref="AddrState"/>.
    /// <see cref="MarkValue"/> is Held, 0x00 or 0x01 is Resting (the low bit is engine driven),
    /// and anything else is Foreign. The mark is a flat value, not an OR of 0x80, because the
    /// whole byte selects the highlight colour, so a held byte is exactly MarkValue.
    /// </summary>
    internal static AddrState ClassifyAddr(byte cur)
    {
        if (cur == MarkValue) return AddrState.Held;
        if (cur == 0x00 || cur == 0x01) return AddrState.Resting;
        return AddrState.Foreign;
    }

    // ---- #4 MarkValue / WantWrite ----

    /// <summary>
    /// The render flag byte that paints a treasure tile, a cyan border with a yellow fill. The
    /// byte value is the colour, tuned live in June 2026 by cycling values on map 74.
    /// </summary>
    internal const byte MarkValue = 0xCC;

    /// <summary>
    /// The value to write for this byte, always <see cref="MarkValue"/> regardless of
    /// <paramref name="cur"/>. Written flat rather than ORed, because the colour is the whole
    /// byte and ORing a resting 0x01 would give 0xCD, a different colour. Still set only, since
    /// the engine clears marks itself.
    /// </summary>
    internal static byte WantWrite(byte cur) => MarkValue;

    // ---- #5 DecideArm ----

    /// <summary>
    /// The per tick arm decision from an address audit summary. Arm once
    /// <paramref name="okCount"/> reaches <paramref name="minPlausible"/>, else Retry, and the
    /// module polls until enough tiles are on screen. Foreign bytes are not a veto, because they
    /// are off screen render bytes that return to Resting when the tile scrolls back, and
    /// <see cref="TileHolder.Hold"/> never writes them. Never returns
    /// <see cref="ArmVerdict.Disarm"/>, which belongs to <see cref="TickArming"/> for fingerprint
    /// mismatches only.
    /// </summary>
    internal static ArmVerdict DecideArm(
        int okCount, int foreignCount, int unreadableCount,
        int minPlausible)
    {
        return okCount >= minPlausible ? ArmVerdict.Arm : ArmVerdict.Retry;
    }

    // ---- #6 BuildKeyMatches ----

    /// <summary>
    /// True when the dataset's baked PE header fields exactly match the live header.
    /// A single-field mismatch means the game was patched since capture; the module
    /// globally disarms and logs once until the dataset is rebuilt.
    /// </summary>
    internal static bool BuildKeyMatches(
        uint dsTimeDateStamp, uint dsSizeOfImage,
        uint liveTimeDateStamp, uint liveSizeOfImage) =>
        dsTimeDateStamp == liveTimeDateStamp && dsSizeOfImage == liveSizeOfImage;

    // ---- #7 MaskedTerrainHash (fingerprint v2) and MaskedTerrainHashV3 ----

    // The grid is 208 records of 7 bytes. Fields 1 and 6 change during play and field 0 is tile
    // height. The v1 fingerprint hashed all 1456 raw bytes and saw changes whenever field 1 or 6
    // moved mid battle. V2 hashes only field 0 of every record. On water and lava maps fields 0,
    // 1 and 6 all animate, seen live on Zeirchele Falls, so v2 cycled with the animation and
    // disarmed and re-armed. V3 hashes fields 2 to 5 only, which are static on every map type.
    private const int TerrainRecordLen = 7;   // bytes per terrain record

    /// <summary>
    /// Fingerprint v2, FNV-1a64 over field 0 of each record, the byte at offset i times 7.
    /// Records beyond the buffer are skipped, and the caller already returns false on a partial
    /// read. The pinned test vector, shared with the Python self test, is two records of
    /// 01 02 03 04 05 06 07 and 11 12 13 14 15 16 17, whose field 0 bytes 01 and 11 hash to
    /// 0x082f3307b4e8a9a7. Kept because ten dry land maps were captured with fpVer 2, while water
    /// and lava maps use v3.
    /// </summary>
    internal static ulong MaskedTerrainHash(ReadOnlySpan<byte> raw)
    {
        int numRecords = raw.Length / TerrainRecordLen;
        ulong h = FnvBasis;
        for (int i = 0; i < numRecords; i++)
        {
            byte b = raw[i * TerrainRecordLen];  // field 0 only
            h ^= b;
            h *= FnvPrime;
        }
        return h;
    }

    /// <summary>
    /// Fingerprint v3, FNV-1a64 over fields 2 to 5 of each record, the bytes whose position mod 7
    /// is 2, 3, 4 or 5. Those are the static geometry fields. Fields 0, 1 and 6 animate on water
    /// and lava maps and are excluded. The pinned test vector, shared with the Python self test,
    /// is two records of 01 02 03 04 05 06 07 and 11 12 13 14 15 16 17, whose selected bytes
    /// 03 04 05 06 13 14 15 16 hash to 0x05708f90b5f5fac5.
    /// </summary>
    internal static ulong MaskedTerrainHashV3(ReadOnlySpan<byte> raw)
    {
        ulong h = FnvBasis;
        for (int i = 0; i < raw.Length; i++)
        {
            int fieldOff = i % TerrainRecordLen;
            if (fieldOff >= 2 && fieldOff <= 5)
            {
                h ^= raw[i];
                h *= FnvPrime;
            }
        }
        return h;
    }
}

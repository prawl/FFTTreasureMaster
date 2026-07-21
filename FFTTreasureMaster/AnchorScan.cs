using System.Collections.Generic;

namespace FFTTreasureMaster;

/// <summary>
/// Stateless masked byte-pattern scanner over caller-supplied memory ranges (sections), plus the
/// RIP-relative resolve formula. Knows nothing about regions/singletons/deltas -- that policy
/// lives in AnchorResolver. Every <see cref="AnchorSig"/> pattern arrives already parsed (see
/// AnchorDb.ParsePattern); this class only searches for it.
///
/// Scan mechanics (see the update-hardening design doc for the full rationale):
///   - Each section is scanned independently in fixed <see cref="Tuning.AnchorScanChunkBytes"/>
///     steps; a pattern never matches across a section boundary.
///   - Each chunk read is <c>chunkBytes + overlap</c> long (overlap = longest pattern - 1) so a
///     match that starts near the end of the chunk's "primary" range can still be verified in
///     full; a candidate match start is only ever accepted when it falls inside the CURRENT
///     chunk's primary range (<c>[0, chunkBytes)</c> relative to that chunk, clamped to the
///     section's remaining length on the last chunk). Because consecutive chunks' primary
///     ranges exactly partition the section, every absolute match-start position is considered
///     by exactly one chunk -- a hit in the overlap tail of chunk N is rejected there and picked
///     up by chunk N+1, so it is counted exactly once with no separate de-dup pass needed.
///   - Hits aggregate across chunks AND across sections before any uniqueness judgement is made
///     by the caller; a per-pattern hit list is capped at <see cref="Tuning.AnchorScanHitCap"/>.
///   - ANY failed chunk read, or exceeding <see cref="Tuning.AnchorScanMaxTotalBytes"/> total
///     scanned across every section, aborts the WHOLE scan (null) -- a missed duplicate can
///     never be ruled out otherwise.
/// </summary>
internal sealed class AnchorScan
{
    private readonly IGameMemory _mem;

    public AnchorScan(IGameMemory mem) => _mem = mem;

    /// <summary>
    /// Scans every section for every sig in one pass, returning a hit list per sig name (by
    /// <see cref="AnchorSig.Name"/>), or null on any unreadable chunk or budget overrun.
    /// An empty list for a sig means zero hits (a legitimate "this sig didn't resolve" outcome,
    /// not a failure).
    /// </summary>
    public Dictionary<string, List<long>>? FindAll(
        IReadOnlyList<(long va, long size)> sections, IReadOnlyList<AnchorSig> sigs)
    {
        var hits = new Dictionary<string, List<long>>();
        foreach (var sig in sigs) hits[sig.Name] = new List<long>();
        if (sigs.Count == 0) return hits;

        int maxPatLen = 0;
        foreach (var sig in sigs)
            if (sig.Pattern.Length > maxPatLen) maxPatLen = sig.Pattern.Length;
        int overlap = maxPatLen > 0 ? maxPatLen - 1 : 0;

        long totalScanned = 0;
        foreach (var (sectionVa, sectionSize) in sections)
        {
            long offset = 0;
            while (offset < sectionSize)
            {
                long remaining = sectionSize - offset;
                long primaryLen = System.Math.Min(Tuning.AnchorScanChunkBytes, remaining);
                long windowLen = System.Math.Min(Tuning.AnchorScanChunkBytes + overlap, remaining);

                totalScanned += windowLen;
                if (totalScanned > Tuning.AnchorScanMaxTotalBytes) return null;

                if (!_mem.TryReadBytes(sectionVa + offset, (int)windowLen, out var buf))
                    return null;

                foreach (var sig in sigs)
                {
                    var list = hits[sig.Name];
                    if (list.Count >= Tuning.AnchorScanHitCap) continue;
                    SearchWindow(buf, sig.Pattern, sectionVa + offset, primaryLen, list);
                }

                offset += Tuning.AnchorScanChunkBytes;
            }
        }
        return hits;
    }

    /// <summary>
    /// Reads the 4-byte displacement at <c>siteVa + sig.DispOff</c>, sign-extends it, and applies
    /// the RIP-resolve formula: <c>siteVa + dispOff + 4 + endAdjust + signExtend(disp32)</c>.
    /// Returns null on a failed (guarded) read.
    /// </summary>
    public long? ResolveTarget(long siteVa, AnchorSig sig)
    {
        long dispAddr = siteVa + sig.DispOff;
        if (!_mem.TryReadBytes(dispAddr, 4, out var buf)) return null;
        uint u32 = Mem.U32(buf, 0);
        long disp = (int)u32;   // sign-extend
        return siteVa + sig.DispOff + 4 + sig.EndAdjust + disp;
    }

    private static void SearchWindow(
        byte[] buf, int?[] pattern, long windowVa, long primaryLen, List<long> hitList)
    {
        var (runOff, runLen) = LongestLiteralRun(pattern);
        if (runLen == 0) return;   // an all-wildcard pattern can never anchor -- nothing to find
        byte first = (byte)pattern[runOff]!;
        int patLen = pattern.Length;
        int maxStart = (int)System.Math.Min(primaryLen, buf.Length - patLen + 1);

        for (int start = 0; start < maxStart; start++)
        {
            if (hitList.Count >= Tuning.AnchorScanHitCap) return;

            int p = start + runOff;
            if (buf[p] != first) continue;

            bool runOk = true;
            for (int k = 1; k < runLen; k++)
            {
                if (buf[p + k] != (byte)pattern[runOff + k]!) { runOk = false; break; }
            }
            if (!runOk) continue;

            bool ok = true;
            for (int i = 0; i < patLen; i++)
            {
                if (pattern[i] is int want && buf[start + i] != (byte)want) { ok = false; break; }
            }
            if (ok) hitList.Add(windowVa + start);
        }
    }

    /// <summary>Longest run of non-wildcard entries in <paramref name="pattern"/>, as
    /// (offset, length). Used to anchor the search on the cheapest possible literal check
    /// before verifying the whole (possibly wildcard-sparse) pattern.</summary>
    private static (int offset, int length) LongestLiteralRun(int?[] pattern)
    {
        int bestOff = 0, bestLen = 0, curOff = 0, curLen = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] is not null)
            {
                if (curLen == 0) curOff = i;
                curLen++;
                if (curLen > bestLen) { bestLen = curLen; bestOff = curOff; }
            }
            else curLen = 0;
        }
        return (bestOff, bestLen);
    }
}

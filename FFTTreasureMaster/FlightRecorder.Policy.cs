using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace FFTTreasureMaster;

/// <summary>
/// The flight recorder's pure half, which FlightRecorder.cs calls into.
/// Filename sanitizing, JSONL serialization and the retention selection, all plain functions of
/// their inputs with no lock, no clock and no IO.
/// </summary>
internal sealed partial class FlightRecorder
{
    /// <summary>Ring contents in insertion (oldest-first) order. Caller holds <c>_lock</c>;
    /// this just linearizes the circular buffer into a plain array.</summary>
    private FlightRecord[] Linearize()
    {
        if (_count < Capacity)
        {
            var outArr = new FlightRecord[_count];
            Array.Copy(_ring, 0, outArr, 0, _count);
            return outArr;
        }
        var full = new FlightRecord[Capacity];
        int firstPart = Capacity - _head;
        Array.Copy(_ring, _head, full, 0, firstPart);
        Array.Copy(_ring, 0, full, firstPart, _head);
        return full;
    }

    private static string SafeTrigger(string trigger) => string.IsNullOrEmpty(trigger) ? "flush" : trigger;

    /// <summary>The JSONL body, a header line with the wall clock and elapsed ms, then one compact
    /// JSON object per record. Newtonsoft does the escaping, so hostile payloads round trip
    /// exactly.</summary>
    private static string Serialize(FlightRecord[] records, DateTime wall, long flushElapsed)
    {
        var sb = new StringBuilder();
        sb.Append(JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            ["hdr"] = true,
            ["wall"] = wall.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            ["t"] = flushElapsed,
        })).Append('\n');
        foreach (var r in records)
        {
            sb.Append(JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["t"] = r.ElapsedMs,
                ["e"] = r.Type,
                ["d"] = r.Payload,
            })).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Which of the <paramref name="existing"/> archives to delete so at most
    /// <paramref name="retentionCount"/> remain, oldest first. The file names sort
    /// chronologically, so a plain ordinal sort is a valid oldest first order.</summary>
    private static List<string> SelectForDeletion(IEnumerable<string> existing, int retentionCount)
    {
        var files = existing.ToList();
        files.Sort(StringComparer.Ordinal);
        int excess = files.Count - retentionCount;
        return excess > 0 ? files.Take(excess).ToList() : new List<string>();
    }
}

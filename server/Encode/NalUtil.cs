namespace DeskStreamer.Server.Encode;

/// <summary>
/// Minimal Annex-B NAL scanning: enough to detect SPS presence and to lift the
/// SPS/PPS parameter sets out of an IDR access unit for caching.
/// </summary>
internal static class NalUtil
{
    private const int NalSps = 7;
    private const int NalPps = 8;

    /// <summary>True if the buffer contains at least one SPS (nal_unit_type 7).</summary>
    public static bool ContainsSps(byte[] data, int length)
    {
        foreach (var (start, _) in EnumerateNals(data, length))
        {
            if ((data[start] & 0x1F) == NalSps)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns SPS+PPS NAL units (each with a 4-byte start code) concatenated, or null if
    /// none are present. Used to build a reusable parameter-set prefix for later IDRs.
    /// </summary>
    public static byte[]? ExtractParameterSets(byte[] data, int length)
    {
        var chunks = new List<(int start, int end)>();
        int total = 0;
        foreach (var (start, end) in EnumerateNals(data, length))
        {
            int t = data[start] & 0x1F;
            if (t == NalSps || t == NalPps)
            {
                chunks.Add((start, end));
                total += 4 + (end - start); // 4-byte start code + payload
            }
        }
        if (chunks.Count == 0)
            return null;

        var result = new byte[total];
        int o = 0;
        foreach (var (start, end) in chunks)
        {
            result[o] = 0; result[o + 1] = 0; result[o + 2] = 0; result[o + 3] = 1;
            o += 4;
            int len = end - start;
            Buffer.BlockCopy(data, start, result, o, len);
            o += len;
        }
        return result;
    }

    /// <summary>
    /// Yields (payloadStart, payloadEnd) for each NAL, where payloadStart is the byte after
    /// the start code (i.e. the NAL header byte) and payloadEnd is exclusive.
    /// Handles both 3-byte (00 00 01) and 4-byte (00 00 00 01) start codes.
    /// </summary>
    private static IEnumerable<(int start, int end)> EnumerateNals(byte[] d, int length)
    {
        int i = 0;
        int prevStart = -1;

        while (i + 2 < length)
        {
            bool sc3 = d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1;
            bool sc4 = i + 3 < length && d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 0 && d[i + 3] == 1;

            if (sc3 || sc4)
            {
                int payloadStart = i + (sc4 ? 4 : 3);
                if (prevStart >= 0)
                    yield return (prevStart, i);
                prevStart = payloadStart;
                i = payloadStart;
            }
            else
            {
                i++;
            }
        }

        if (prevStart >= 0 && prevStart < length)
            yield return (prevStart, length);
    }
}

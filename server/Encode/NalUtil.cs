namespace DeskStreamer.Server.Encode;

/// <summary>
/// Minimal Annex-B NAL scanning: enough to detect SPS presence and to lift the
/// SPS/PPS parameter sets out of an IDR access unit for caching.
/// </summary>
internal static class NalUtil
{
    private const int NalSps = 7;
    private const int NalPps = 8;

    public static bool IsAnnexB(byte[] data, int length)
    {
        for (int i = 0; i + 2 < length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 &&
                (data[i + 2] == 1 || (i + 3 < length && data[i + 2] == 0 && data[i + 3] == 1)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Converts the common 4-byte-length-prefixed AVC access-unit representation into Annex-B.
    /// Returns false when the input is not a complete sequence of valid length-prefixed NALs.
    /// </summary>
    public static bool TryConvertLengthPrefixedToAnnexB(
        byte[] source, int length, ref byte[] destination, out int convertedLength)
    {
        convertedLength = 0;
        if (length < 5)
            return false;

        int input = 0;
        int total = 0;
        while (input + 4 <= length)
        {
            int nalLength = (source[input] << 24) | (source[input + 1] << 16) |
                            (source[input + 2] << 8) | source[input + 3];
            input += 4;
            if (nalLength <= 0 || nalLength > length - input)
                return false;
            if (total > int.MaxValue - 4 - nalLength)
                return false;
            total += 4 + nalLength;
            input += nalLength;
        }

        if (input != length || total == 0)
            return false;

        if (destination.Length < total)
            Array.Resize(ref destination, Math.Max(total, destination.Length * 2));

        input = 0;
        int output = 0;
        while (input < length)
        {
            int nalLength = (source[input] << 24) | (source[input + 1] << 16) |
                            (source[input + 2] << 8) | source[input + 3];
            input += 4;
            destination[output++] = 0;
            destination[output++] = 0;
            destination[output++] = 0;
            destination[output++] = 1;
            Buffer.BlockCopy(source, input, destination, output, nalLength);
            input += nalLength;
            output += nalLength;
        }
        convertedLength = output;
        return true;
    }

    /// <summary>Converts an AVCDecoderConfigurationRecord (avcC) into Annex-B SPS/PPS NALs.</summary>
    public static byte[]? ExtractParameterSetsFromAvcConfiguration(byte[] data)
    {
        // ISO/IEC 14496-15 AVCDecoderConfigurationRecord. The first byte is always 1.
        if (data.Length < 7 || data[0] != 1)
            return null;

        int input = 5;
        int spsCount = data[input++] & 0x1F;
        if (spsCount == 0)
            return null;

        var nals = new List<(int Offset, int Length)>();
        for (int i = 0; i < spsCount; i++)
        {
            if (!TryReadAvcConfigNal(data, ref input, out var nal)) return null;
            nals.Add(nal);
        }

        if (input >= data.Length)
            return null;
        int ppsCount = data[input++];
        for (int i = 0; i < ppsCount; i++)
        {
            if (!TryReadAvcConfigNal(data, ref input, out var nal)) return null;
            nals.Add(nal);
        }

        if (nals.Count == 0)
            return null;

        int total = nals.Sum(nal => 4 + nal.Length);
        var result = new byte[total];
        int output = 0;
        foreach (var nal in nals)
        {
            result[output++] = 0;
            result[output++] = 0;
            result[output++] = 0;
            result[output++] = 1;
            Buffer.BlockCopy(data, nal.Offset, result, output, nal.Length);
            output += nal.Length;
        }
        return result;
    }

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

    private static bool TryReadAvcConfigNal(byte[] data, ref int input, out (int Offset, int Length) nal)
    {
        nal = default;
        if (input + 2 > data.Length)
            return false;
        int length = (data[input] << 8) | data[input + 1];
        input += 2;
        if (length <= 0 || input + length > data.Length)
            return false;
        nal = (input, length);
        input += length;
        return true;
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

using Vortice.Direct3D11;

namespace DeskStreamer.Server.Encode;

public static class EncoderFactory
{
    public static IVideoEncoder Create(
        ID3D11Device device,
        int width,
        int height,
        int fps,
        int initialBitrateKbps)
    {
        try
        {
            var nvenc = new NvencH264Encoder(
                device, width, height, fps, initialBitrateKbps);
            Console.WriteLine("[encoder] using native NVIDIA NVENC ultra-low-latency backend.");
            return nvenc;
        }
        catch (Exception ex)
        {
            // Loading nvEncodeAPI, opening a DirectX session, and initialization are all
            // capability probes. Any failure here is safe to fall back from because no frame
            // has entered the stream yet.
            Console.WriteLine($"[encoder] native NVENC unavailable: {ShortMessage(ex)}");
            Console.WriteLine("[encoder] trying Media Foundation hardware H.264 fallback.");
        }

        return new H264Encoder(device, width, height, fps, initialBitrateKbps);
    }

    private static string ShortMessage(Exception ex)
    {
        string message = ex.Message;
        int newline = message.IndexOfAny(new[] { '\r', '\n' });
        return newline >= 0 ? message[..newline] : message;
    }
}

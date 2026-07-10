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
        string requested = Environment.GetEnvironmentVariable("DESKSTREAM_ENCODER")?
            .Trim().ToLowerInvariant() ?? "";

        if (requested == "nvenc")
        {
            Console.WriteLine("[encoder] native NVIDIA NVENC explicitly requested.");
            return new NvencH264Encoder(device, width, height, fps, initialBitrateKbps);
        }

        // The Media Foundation MFT is the Windows hardware path that was exercised by the
        // original releases. Direct NVENC initialization is not sufficient proof that its
        // first D3D11 texture can be registered/mapped on every NVIDIA driver, so it must be
        // explicitly opted into instead of silently tearing down an otherwise healthy client.
        Console.WriteLine("[encoder] using Media Foundation hardware H.264 backend.");
        return new H264Encoder(device, width, height, fps, initialBitrateKbps);
    }
}

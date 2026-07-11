using Vortice.Direct3D11;

namespace DeskStreamer.Server.Encode;

/// <summary>Common surface-to-H.264 contract for native and Media Foundation encoders.</summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>Called with a reused buffer. Consumers must finish reading before returning.</summary>
    Action<byte[], int, bool, uint>? OnEncodedFrame { get; set; }

    string BackendName { get; }

    void Submit(ID3D11Texture2D nv12, uint ptsMs);

    void RequestIdr();

    /// <summary>Attempts a live bitrate change. Returns true only when the encoder accepts it.</summary>
    bool SetBitrate(int kbps);
}

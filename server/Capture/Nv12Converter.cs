using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskStreamer.Server.Capture;

/// <summary>
/// GPU BGRA -> NV12 conversion via the D3D11 VideoProcessor. Runs entirely on the shared
/// device so the encoder can consume the NV12 texture on-GPU (no CPU round-trip).
///
/// A small pool of NV12 textures is rotated so the encoder can still be reading texture N
/// while we write texture N+1. The BGRA source is the duplicator's single stable copy, so
/// its input view is created once.
/// </summary>
public sealed class Nv12Converter : IDisposable
{
    // Media Foundation commonly exposes 4-6 concurrent NeedInput credits. Each submitted sample
    // retains its D3D surface until the hardware consumes it, so three rotating textures can be
    // overwritten while still in flight once proper credit accounting is enabled. Eight surfaces
    // cost only ~11 MiB at 720p and keep capture/convert from racing the encoder.
    private const int PoolSize = 8;

    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;

    private readonly ID3D11Texture2D[] _nv12 = new ID3D11Texture2D[PoolSize];
    private readonly ID3D11VideoProcessorOutputView[] _outputViews =
        new ID3D11VideoProcessorOutputView[PoolSize];

    private ID3D11VideoProcessorInputView? _inputView;
    private ID3D11Texture2D? _boundBgra;
    private int _next;

    /// <summary>Width of the NV12 output textures produced by <see cref="Convert"/>.</summary>
    public int OutWidth { get; }

    /// <summary>Height of the NV12 output textures produced by <see cref="Convert"/>.</summary>
    public int OutHeight { get; }

    /// <summary>Output dimensions equal the input dimensions (no scaling).</summary>
    public Nv12Converter(ID3D11Device device, int width, int height, int fps)
        : this(device, width, height, width, height, fps)
    {
    }

    /// <summary>
    /// Converts a BGRA source of (<paramref name="inWidth"/>, <paramref name="inHeight"/>)
    /// into NV12 output textures of (<paramref name="outWidth"/>, <paramref name="outHeight"/>).
    /// The VideoProcessor scales input to output during the blit, so quality downscaling is free
    /// on the GPU. All four dimensions must be even (NV12 requirement).
    /// </summary>
    public Nv12Converter(
        ID3D11Device device, int inWidth, int inHeight, int outWidth, int outHeight, int fps)
    {
        OutWidth = outWidth;
        OutHeight = outHeight;

        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = device.ImmediateContext.QueryInterface<ID3D11VideoContext>();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)fps, 1),
            InputWidth = (uint)inWidth,
            InputHeight = (uint)inHeight,
            OutputFrameRate = new Rational((uint)fps, 1),
            OutputWidth = (uint)outWidth,
            OutputHeight = (uint)outHeight,
            Usage = VideoUsage.PlaybackNormal,
        };

        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        // Full-range/BT.709-ish is fine for desktop content; leaving color space at the
        // driver default keeps the API surface small. If colors look washed out, set the
        // stream/output color space here.

        var outDesc = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        };

        // The NV12 pool is created at OUTPUT dimensions; VideoProcessorBlt scales into it.
        for (int i = 0; i < PoolSize; i++)
        {
            _nv12[i] = CreateNv12Texture(device, outWidth, outHeight);
            _outputViews[i] = _videoDevice.CreateVideoProcessorOutputView(_nv12[i], _enumerator, outDesc);
        }
    }

    private static ID3D11Texture2D CreateNv12Texture(ID3D11Device device, int w, int h)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            // RenderTarget: required as a VideoProcessor output. ShaderResource: some encoder
            // MFTs sample the surface.
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        return device.CreateTexture2D(desc);
    }

    /// <summary>Blits the BGRA source into the next pooled NV12 texture and returns it.</summary>
    public ID3D11Texture2D Convert(ID3D11Texture2D bgra)
    {
        // The duplicator reuses one BGRA texture, so the input view is created just once.
        if (_inputView == null || !ReferenceEquals(_boundBgra, bgra))
        {
            _inputView?.Dispose();
            var inDesc = new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            };
            _inputView = _videoDevice.CreateVideoProcessorInputView(bgra, _enumerator, inDesc);
            _boundBgra = bgra;
        }

        int idx = _next;
        _next = (_next + 1) % PoolSize;

        var stream = new VideoProcessorStream
        {
            Enable = true,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            InputSurface = _inputView,
        };

        _videoContext.VideoProcessorBlt(_processor, _outputViews[idx], 0, 1, new[] { stream });
        return _nv12[idx];
    }

    public void Dispose()
    {
        _inputView?.Dispose();
        foreach (var v in _outputViews) v?.Dispose();
        foreach (var t in _nv12) t?.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}

using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskStreamer.Server.Capture;

/// <summary>
/// DXGI Desktop Duplication on the primary output (ARCHITECTURE.md latency pipeline).
///
/// Owns the shared D3D11 device used by the whole GPU pipeline (converter + encoder).
/// AcquireNextFrame runs with a short timeout; on timeout the desktop is simply idle and
/// we produce no new frame (do NOT re-encode). On DXGI_ERROR_ACCESS_LOST (mode change /
/// UAC secure desktop) we transparently recreate the duplication.
/// </summary>
public sealed class DesktopDuplicator : IDisposable
{
    private readonly IDXGIFactory1 _factory;
    private readonly IDXGIAdapter1 _adapter;
    private readonly IDXGIOutput1 _output1;

    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _bgraCopy;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public int Width { get; }
    public int Height { get; }

    public DesktopDuplicator()
    {
        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        // Primary adapter / primary output.
        Result r = _factory.EnumAdapters1(0, out _adapter);
        if (r.Failure || _adapter == null)
            throw new InvalidOperationException("No DXGI adapter found.");

        r = _adapter.EnumOutputs(0, out IDXGIOutput? output);
        if (r.Failure || output == null)
            throw new InvalidOperationException("No display output found on primary adapter.");

        _output1 = output.QueryInterface<IDXGIOutput1>();
        output.Dispose();

        // BGRA support is required for the desktop image; VideoSupport lets us reuse this
        // device for the D3D11 VideoProcessor (BGRA->NV12) stage.
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        D3D11.D3D11CreateDevice(
            _adapter,
            DriverType.Unknown, // must be Unknown when an adapter is supplied
            flags,
            new[]
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
            },
            out ID3D11Device device,
            out ID3D11DeviceContext context).CheckError();

        Device = device;
        Context = context;

        // Request high GPU thread priority to avoid capture/encode starvation under 100% GPU game load.
        using (var dxgiDevice = Device.QueryInterface<IDXGIDevice>())
        {
            try
            {
                dxgiDevice.GPUThreadPriority = 5;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[capture] failed to set DXGI GPU priority: {ex.Message}");
            }
        }

        // Media Foundation and the video pipeline touch this device from multiple threads.
        using (var mt = Device.QueryInterface<ID3D11Multithread>())
            mt.SetMultithreadProtected(true);

        var desc = _output1.Description;
        var bounds = desc.DesktopCoordinates;
        Width = bounds.Right - bounds.Left;
        Height = bounds.Bottom - bounds.Top;

        CreateDuplication();
        _bgraCopy = CreateBgraTexture(Width, Height);
    }

    private void CreateDuplication()
    {
        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        _duplication?.Dispose();
        _duplication = _output1.DuplicateOutput(dxgiDevice);
    }

    private ID3D11Texture2D CreateBgraTexture(int w, int h)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        return Device.CreateTexture2D(desc);
    }

    /// <summary>
    /// Tries to acquire the next desktop frame. Returns true and yields a stable private BGRA
    /// texture (owned by this class, reused every call) when a NEW frame is available.
    /// Returns false on timeout (idle desktop) or after recovering from an access-lost event.
    /// </summary>
    public bool TryAcquire(int timeoutMs, out ID3D11Texture2D bgra)
    {
        bgra = _bgraCopy!;

        if (_duplication == null)
        {
            CreateDuplication();
            return false;
        }

        Result r = _duplication.AcquireNextFrame(
            (uint)timeoutMs, out OutduplFrameInfo frameInfo, out IDXGIResource? desktopResource);

        if (r == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            desktopResource?.Dispose();
            return false; // no new frame; correct to skip re-encoding
        }

        if (r == Vortice.DXGI.ResultCode.AccessLost)
        {
            desktopResource?.Dispose();
            RecreateAfterAccessLost();
            return false;
        }

        if (r.Failure || desktopResource == null)
        {
            desktopResource?.Dispose();
            return false;
        }

        try
        {
            // LastPresentTime == 0 means only the mouse pointer moved — no new screen content.
            if (frameInfo.LastPresentTime == 0)
                return false;

            using var frameTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
            Context.CopyResource(_bgraCopy!, frameTexture);
            return true;
        }
        finally
        {
            desktopResource.Dispose();
            // Release immediately — we already own a private copy.
            try { _duplication.ReleaseFrame(); } catch { }
        }
    }

    private void RecreateAfterAccessLost()
    {
        try
        {
            _duplication?.ReleaseFrame();
        }
        catch { }
        try
        {
            CreateDuplication();
        }
        catch (SharpGenException ex)
        {
            // Duplication may be briefly unavailable (e.g. during the UAC secure desktop).
            // Leave _duplication null; the next TryAcquire will retry.
            Console.Error.WriteLine($"[capture] duplication recreate deferred: {ex.Message}");
            _duplication = null;
        }
    }

    /// <summary>Queries the primary display size without starting a capture (for HELLO_OK).</summary>
    public static (int width, int height) GetPrimaryDisplaySize()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (factory.EnumAdapters1(0, out var adapter).Failure || adapter == null)
            throw new InvalidOperationException("No DXGI adapter found.");
        using (adapter)
        {
            if (adapter.EnumOutputs(0, out var output).Failure || output == null)
                throw new InvalidOperationException("No display output found.");
            using (output)
            {
                var b = output.Description.DesktopCoordinates;
                return (b.Right - b.Left, b.Bottom - b.Top);
            }
        }
    }

    public void Dispose()
    {
        _duplication?.Dispose();
        _bgraCopy?.Dispose();
        _output1.Dispose();
        Context.Dispose();
        Device.Dispose();
        _adapter.Dispose();
        _factory.Dispose();
    }
}

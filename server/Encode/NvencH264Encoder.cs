using System.Runtime.InteropServices;
using Lennox.NvEncSharp;
using Vortice.Direct3D11;
using static Lennox.NvEncSharp.LibNvEnc;

namespace DeskStreamer.Server.Encode;

/// <summary>
/// Native NVIDIA NVENC H.264 backend. Encoding is synchronous and has exactly one output
/// buffer, which deliberately prevents the application from building a hidden frame queue.
/// Raw NV12 textures stay on the shared D3D11 device for the entire path.
/// </summary>
public sealed class NvencH264Encoder : IVideoEncoder
{
    private sealed class RegisteredTexture : IDisposable
    {
        public readonly NvEncRegisterResource Registration;
        private readonly NvEncoder.NvEncRegisteredResource _lease;

        public RegisteredTexture(
            NvEncRegisterResource registration,
            NvEncoder.NvEncRegisteredResource lease)
        {
            Registration = registration;
            _lease = lease;
        }

        public void Dispose() => _lease.Dispose();
    }

    private readonly object _gate = new();
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly Dictionary<IntPtr, RegisteredTexture> _textures = new();

    private NvEncoder _encoder;
    private NvEncConfig _config;
    private NvEncCreateBitstreamBuffer _bitstream;
    private byte[] _output = new byte[1 << 20];
    private uint _frameIndex;
    private bool _forceIdr = true;
    private bool _initialized;
    private bool _disposed;

    public Action<byte[], int, bool, uint>? OnEncodedFrame { get; set; }
    public string BackendName => "nvenc-ultra-low-latency";

    public NvencH264Encoder(
        ID3D11Device device,
        int width,
        int height,
        int fps,
        int initialBitrateKbps)
    {
        _width = width;
        _height = height;
        _fps = Math.Max(1, fps);

        var status = TryInitialize(out string? reason);
        if (status != LibNcEncInitializeStatus.Success)
            throw new EncoderUnavailableException(
                "NVIDIA NVENC API could not be initialized: " + (reason ?? status.ToString()));

        try
        {
            _encoder = OpenEncoderForDirectX(device.NativePointer);
            _config = _encoder.GetEncodePresetConfigEx(
                NvEncCodecGuids.H264,
                NvEncPresetGuids.P3,
                NvEncTuningInfo.UltraLowLatency).PresetCfg;

            Configure(initialBitrateKbps);
            InitializeEncoder();
            _bitstream = _encoder.CreateBitstreamBuffer();
            _initialized = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Configure(int bitrateKbps)
    {
        uint bitrate = checked((uint)Math.Max(500, bitrateKbps) * 1000u);
        uint oneFrameVbv = Math.Max(32_000u, bitrate / (uint)_fps);

        _config.Version = NV_ENC_CONFIG_VER;
        _config.ProfileGuid = NvEncProfileGuids.H264High;
        _config.GopLength = uint.MaxValue;
        _config.FrameIntervalP = 1;
        _config.FrameFieldMode = NvEncParamsFrameFieldMode.Frame;
        _config.MvPrecision = NvEncMvPrecision.QuarterPel;

        var rc = _config.RcParams;
        rc.Version = NV_ENC_RC_PARAMS_VER;
        rc.RateControlMode = NvEncParamsRcMode.Cbr;
        rc.AverageBitRate = bitrate;
        rc.MaxBitRate = bitrate;
        rc.VbvBufferSize = oneFrameVbv;
        rc.VbvInitialDelay = oneFrameVbv;
        rc.EnableLookahead = false;
        rc.EnableTemporalAQ = false;
        rc.ZeroReorderDelay = true;
        rc.MultiPass = NvEncMultiPass.Disabled;
        rc.LowDelayKeyFrameScale = 1;
        _config.RcParams = rc;

        var h264 = _config.EncodeCodecConfig.H264Config;
        h264.DisableSPSPPS = false;
        h264.RepeatSPSPPS = true;
        h264.IdrPeriod = uint.MaxValue;
        h264.MaxNumRefFrames = 1;
        h264.ChromaFormatIDC = 1;
        h264.SliceMode = 0;
        h264.SliceModeData = 0;
        _config.EncodeCodecConfig.H264Config = h264;
    }

    private unsafe void InitializeEncoder()
    {
        fixed (NvEncConfig* config = &_config)
        {
            var init = CreateInitializeParams(config);
            _encoder.InitializeEncoder(ref init);
        }
    }

    private unsafe NvEncInitializeParams CreateInitializeParams(NvEncConfig* config) => new()
    {
        Version = NV_ENC_INITIALIZE_PARAMS_VER,
        EncodeGuid = NvEncCodecGuids.H264,
        PresetGuid = NvEncPresetGuids.P3,
        EncodeWidth = (uint)_width,
        EncodeHeight = (uint)_height,
        MaxEncodeWidth = (uint)_width,
        MaxEncodeHeight = (uint)_height,
        DarWidth = (uint)_width,
        DarHeight = (uint)_height,
        FrameRateNum = (uint)_fps,
        FrameRateDen = 1,
        EnableEncodeAsync = 0,
        EnablePTD = 1,
        ReportSliceOffsets = false,
        EnableSubFrameWrite = false,
        EnableWeightedPrediction = false,
        EncodeConfig = config,
        TuningInfo = NvEncTuningInfo.UltraLowLatency,
    };

    public void Submit(ID3D11Texture2D nv12, uint ptsMs)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RegisteredTexture registered = GetOrRegister(nv12);
            var mapped = new NvEncMapInputResource
            {
                Version = NV_ENC_MAP_INPUT_RESOURCE_VER,
                RegisteredResource = registered.Registration.RegisteredResource,
            };

            bool isMapped = false;
            try
            {
                _encoder.MapInputResource(ref mapped);
                isMapped = true;

                bool forceIdr = _forceIdr;
                _forceIdr = false;
                var picture = new NvEncPicParams
                {
                    Version = NV_ENC_PIC_PARAMS_VER,
                    InputWidth = (uint)_width,
                    InputHeight = (uint)_height,
                    FrameIdx = _frameIndex++,
                    InputTimeStamp = ptsMs,
                    InputDuration = (ulong)Math.Max(1, 1000 / _fps),
                    InputBuffer = mapped.MappedResource,
                    OutputBitstream = _bitstream.BitstreamBuffer,
                    BufferFmt = mapped.MappedBufferFmt,
                    PictureStruct = NvEncPicStruct.Frame,
                    EncodePicFlags = forceIdr
                        ? (uint)(NvEncPicFlags.FlagForceidr | NvEncPicFlags.FlagOutputSpspps)
                        : 0,
                };

                _encoder.EncodePicture(ref picture);
                var locked = _encoder.LockBitstream(ref _bitstream);
                try
                {
                    int length = checked((int)locked.BitstreamSizeInBytes);
                    EnsureCapacity(length);
                    Marshal.Copy(locked.BitstreamBufferPtr, _output, 0, length);
                    bool keyframe = locked.PictureType == NvEncPicType.Idr;
                    OnEncodedFrame?.Invoke(_output, length, keyframe, ptsMs);
                }
                finally
                {
                    _encoder.UnlockBitstream(_bitstream.BitstreamBuffer);
                }
            }
            finally
            {
                if (isMapped)
                    _encoder.UnmapInputResource(mapped.MappedResource);
            }
        }
    }

    private RegisteredTexture GetOrRegister(ID3D11Texture2D texture)
    {
        IntPtr key = texture.NativePointer;
        if (_textures.TryGetValue(key, out RegisteredTexture? existing))
            return existing;

        var registration = new NvEncRegisterResource
        {
            Version = NV_ENC_REGISTER_RESOURCE_VER,
            ResourceType = NvEncInputResourceType.Directx,
            Width = (uint)_width,
            Height = (uint)_height,
            Pitch = 0,
            ResourceToRegister = key,
            BufferFormat = NvEncBufferFormat.Nv12,
            BufferUsage = NvEncBufferUsage.NvEncInputImage,
        };
        NvEncoder.NvEncRegisteredResource lease = _encoder.RegisterResource(ref registration);
        var created = new RegisteredTexture(registration, lease);
        _textures.Add(key, created);
        return created;
    }

    public void RequestIdr()
    {
        lock (_gate)
        {
            if (!_disposed)
                _forceIdr = true;
        }
    }

    public unsafe bool SetBitrate(int kbps)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            Configure(kbps);
            fixed (NvEncConfig* config = &_config)
            {
                var reconfigure = new NvEncReconfigureParams
                {
                    Version = NV_ENC_RECONFIGURE_PARAMS_VER,
                    ReInitEncodeParams = CreateInitializeParams(config),
                    ResetEncoder = false,
                    ForceIDR = false,
                };
                _encoder.ReconfigureEncoder(ref reconfigure);
            }
            return true;
        }
    }

    private void EnsureCapacity(int length)
    {
        if (_output.Length < length)
            Array.Resize(ref _output, Math.Max(length, _output.Length * 2));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (RegisteredTexture texture in _textures.Values)
            {
                try { texture.Dispose(); } catch { }
            }
            _textures.Clear();

            if (_initialized)
            {
                try { _encoder.DestroyBitstreamBuffer(_bitstream.BitstreamBuffer); } catch { }
            }
            try { _encoder.DestroyEncoder(); } catch { }
            _initialized = false;
        }
    }
}

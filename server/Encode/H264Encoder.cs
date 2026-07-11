using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace DeskStreamer.Server.Encode;

/// <summary>
/// Hardware H.264 encoder over a Media Foundation async MFT (ARCHITECTURE.md pipeline).
///
/// Design notes / Windows subtleties:
///  * Hardware encoder MFTs are ASYNC: you must set MF_TRANSFORM_ASYNC_UNLOCK, then drive
///    them off METransformNeedInput / METransformHaveOutput events. ProcessInput is legal
///    ONLY after a NeedInput event; ProcessOutput ONLY after a HaveOutput event.
///  * The NV12 surface is consumed on-GPU via an IMFDXGIDeviceManager (SET_D3D_MANAGER) and
///    an MFCreateDXGISurfaceBuffer-backed input sample — no CPU copy of raw frames.
///  * Single-frame pipeline: only the newest captured frame is kept pending; older pending
///    input is dropped (PROTOCOL.md §3.3 / ARCHITECTURE.md "encoder busy -> skip stale").
///  * SPS/PPS: we cache the parameter sets seen on the first IDR and prepend them to any IDR
///    access unit that lacks in-band SPS, guaranteeing every keyframe is self-contained.
/// </summary>
public sealed class H264Encoder : IVideoEncoder
{
    private static readonly Guid Iid_ID3D11Texture2D = typeof(ID3D11Texture2D).GUID;

    private readonly int _fps;
    private readonly long _frameDurationTicks; // 100ns units

    private IMFTransform _transform = null!;
    private IMFMediaEventGenerator _events = null!;
    private IMFDXGIDeviceManager _deviceManager = null!;
    private CodecApi? _codecApi;
    private bool _outputProvidesSamples;

    private Thread? _eventThread;
    private volatile bool _running;

    // Feed state (guarded by _feedGate): newest-wins single-slot input.
    private readonly object _feedGate = new();
    private ID3D11Texture2D? _pendingTexture;
    private uint _pendingPts;
    private bool _hasPending;
    private bool _needInput;
    private readonly Queue<uint> _ptsQueue = new();
    private uint _lastPts;

    // Output assembly buffers (reused; event-thread only).
    private byte[] _rawBuf = new byte[1 << 20];
    private byte[] _annexBBuf = new byte[1 << 20];
    private byte[] _emitBuf = new byte[1 << 20];
    private byte[]? _cachedParamSets; // SPS+PPS NALs incl. start codes
    private bool _loggedProcessOutputError;
    private bool _loggedUnsupportedOutputFraming;

    /// <summary>Called on the encoder event thread with a reused buffer. Consume synchronously.</summary>
    public Action<byte[], int, bool, uint>? OnEncodedFrame { get; set; }
    public string BackendName => "media-foundation";

    public H264Encoder(ID3D11Device device, int width, int height, int fps, int initialBitrateKbps)
    {
        _fps = Math.Max(1, fps);
        _frameDurationTicks = 10_000_000L / _fps;

        // Light startup: the MF platform without networking features.
        MediaFactory.MFStartup(true).CheckError();

        _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        _deviceManager.ResetDevice(device).CheckError();

        if (!TrySelectAndConfigure(width, height, initialBitrateKbps))
        {
            throw new EncoderUnavailableException(
                "No hardware H.264 encoder MFT could be initialized. A GPU with a Media " +
                "Foundation H.264 encoder (NVENC / AMF / QuickSync) is required.");
        }

        _events = _transform.QueryInterface<IMFMediaEventGenerator>();

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        _running = true;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "h264-encoder" };
        _eventThread.Start();
    }

    // ---- Setup ----------------------------------------------------------------------------

    private bool TrySelectAndConfigure(int width, int height, int bitrateKbps)
    {
        // Enumerate hardware video-encoder MFTs. We filter to H.264 by trying to configure
        // each candidate rather than by passing register-type structs, which keeps the
        // enumeration call simple and robust.
        MediaFactory.MFTEnumEx(
            MfGuids.MFT_CATEGORY_VIDEO_ENCODER,
            (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter),
            null,
            null,
            out IntPtr activatePtrs,
            out uint activateCount);

        if (activatePtrs == IntPtr.Zero || activateCount == 0)
            return false;

        var activates = new IMFActivate[activateCount];
        for (int i = 0; i < activateCount; i++)
            activates[i] = new IMFActivate(Marshal.ReadIntPtr(activatePtrs, i * IntPtr.Size));
        Marshal.FreeCoTaskMem(activatePtrs);

        bool configured = false;
        foreach (var activate in activates)
        {
            if (configured)
                continue;

            IMFTransform? candidate = null;
            try
            {
                candidate = activate.ActivateObject<IMFTransform>();
                Configure(candidate, width, height, bitrateKbps);
                _transform = candidate;
                candidate = null; // ownership transferred to _transform
                configured = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[encoder] candidate rejected: {ex.Message}");
                _codecApi?.Dispose();
                _codecApi = null;
                candidate?.Dispose();
            }
        }

        // The activate objects are independent of the transform they created, so releasing
        // every activate once we have picked one is always safe.
        foreach (var a in activates)
        {
            try { a.Dispose(); } catch { }
        }

        return configured;
    }

    private void Configure(IMFTransform transform, int width, int height, int bitrateKbps)
    {
        uint bitrate = checked((uint)((long)bitrateKbps * 1000));

        // 1) Unlock async use.
        using (var attrs = transform.Attributes)
        {
            attrs.Set(MfGuids.MF_TRANSFORM_ASYNC_UNLOCK, 1u);
            attrs.Set(MfGuids.MF_LOW_LATENCY, 1u);
        }

        // 2) Bind the D3D device manager so NV12 GPU surfaces are consumed directly.
        UIntPtr managerPtr = unchecked((UIntPtr)(ulong)_deviceManager.NativePointer.ToInt64());
        transform.ProcessMessage(TMessageType.MessageSetD3DManager, managerPtr);

        // 3) Output type (compressed H.264) MUST be set before the input type on encoders.
        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MfGuids.MF_MT_MAJOR_TYPE, MfGuids.MFMediaType_Video);
            outType.Set(MfGuids.MF_MT_SUBTYPE, MfGuids.MFVideoFormat_H264);
            outType.Set(MfGuids.MF_MT_AVG_BITRATE, bitrate);
            outType.Set(MfGuids.MF_MT_INTERLACE_MODE, MfGuids.Interlace_Progressive);
            outType.Set(MfGuids.MF_MT_MPEG2_PROFILE, MfGuids.H264Profile_High);
            SetFrameSize(outType, width, height);
            SetRatio(outType, MfGuids.MF_MT_FRAME_RATE, _fps, 1);
            SetRatio(outType, MfGuids.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            transform.SetOutputType(0, outType, 0);
        }

        // 4) Input type (raw NV12).
        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MfGuids.MF_MT_MAJOR_TYPE, MfGuids.MFMediaType_Video);
            inType.Set(MfGuids.MF_MT_SUBTYPE, MfGuids.MFVideoFormat_NV12);
            inType.Set(MfGuids.MF_MT_INTERLACE_MODE, MfGuids.Interlace_Progressive);
            SetFrameSize(inType, width, height);
            SetRatio(inType, MfGuids.MF_MT_FRAME_RATE, _fps, 1);
            SetRatio(inType, MfGuids.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            transform.SetInputType(0, inType, 0);
        }

        // 5) Low-latency CBR, no B-frames, effectively-infinite GOP (IDR on demand only).
        _codecApi = CodecApi.TryCreate(transform.NativePointer);
        if (_codecApi == null)
            Console.Error.WriteLine("[encoder] ICodecAPI unavailable; using defaults");
        // The effective low-latency switch is the MF_LOW_LATENCY attribute set on the transform
        // above; CODECAPI_AVLowLatencyMode is redundant and many MFTs reject SetValue on it with
        // E_INVALIDARG. Only set it when the encoder advertises support, and treat a miss as
        // informational rather than an error.
        if (_codecApi != null && _codecApi.IsSupported(MfGuids.CODECAPI_AVLowLatencyMode))
            TrySetCodecBool(MfGuids.CODECAPI_AVLowLatencyMode, true);
        else
            Console.WriteLine("[encoder] CODECAPI_AVLowLatencyMode not supported; MF_LOW_LATENCY attribute is the effective low-latency switch.");
        TrySetCodecU32(MfGuids.CODECAPI_AVEncCommonRateControlMode, MfGuids.RateControlMode_CBR);
        TrySetCodecU32(MfGuids.CODECAPI_AVEncCommonMeanBitRate, bitrate);
        // H.264 expresses HRD/VBV size in bytes. One frame prevents the rate controller from
        // hiding a deep leaky-bucket queue while still allowing normal frame-size variation.
        TrySetCodecU32(MfGuids.CODECAPI_AVEncCommonBufferSize,
            (uint)Math.Max(4096, (long)bitrate / 8 / _fps));
        // 33 is the fastest edge of Microsoft's documented low-complexity range (0..33).
        TrySetCodecU32(MfGuids.CODECAPI_AVEncCommonQualityVsSpeed, 33);
        TrySetCodecU32(MfGuids.CODECAPI_AVEncMPVDefaultBPictureCount, 0);
        TrySetCodecU32(MfGuids.CODECAPI_AVEncMPVGOPSize, 0xFFFF);

        // 6) Cache the negotiated sequence header (SPS/PPS) if the encoder exposes it.
        using (var negotiated = transform.GetOutputCurrentType(0))
        {
            _cachedParamSets = NormalizeParameterSets(
                TryGetBlob(negotiated, MfGuids.MF_MT_MPEG_SEQUENCE_HEADER));
        }

        // 7) Learn whether the MFT allocates output samples for us.
        OutputStreamInfo info = transform.GetOutputStreamInfo(0);
        _outputProvidesSamples =
            (info.Flags & (MfGuids.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES |
                           MfGuids.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
    }

    private static void SetFrameSize(IMFMediaType type, int width, int height) =>
        type.Set(MfGuids.MF_MT_FRAME_SIZE, ((ulong)(uint)width << 32) | (uint)height);

    private static void SetRatio(IMFMediaType type, Guid key, int num, int den) =>
        type.Set(key, ((ulong)(uint)num << 32) | (uint)den);

    private static byte[]? TryGetBlob(IMFMediaType type, Guid key)
    {
        try { return type.GetBlob(key); }
        catch { return null; }
    }

    private static byte[]? NormalizeParameterSets(byte[]? data)
    {
        if (data is not { Length: > 0 }) return null;
        if (NalUtil.IsAnnexB(data, data.Length))
            return NalUtil.ExtractParameterSets(data, data.Length);
        return NalUtil.ExtractParameterSetsFromAvcConfiguration(data);
    }

    // ---- Dynamic controls -----------------------------------------------------------------

    /// <summary>Forces the next encoded frame to be an IDR. Caller rate-limits per PROTOCOL.md.</summary>
    public void RequestIdr() => TrySetCodecU32(MfGuids.CODECAPI_AVEncVideoForceKeyFrame, 1);

    /// <summary>Adjusts the CBR target mid-stream (adaptation controller, PROTOCOL.md §4).</summary>
    public bool SetBitrate(int kbps)
    {
        uint bitrate = checked((uint)((long)kbps * 1000));
        if (!TrySetCodecU32(MfGuids.CODECAPI_AVEncCommonMeanBitRate, bitrate))
            return false;

        // Some MFTs accept the target bitrate but expose a fixed VBV. That does not invalidate
        // the bitrate change, so log a buffer-size rejection without reporting total failure.
        TrySetCodecU32(MfGuids.CODECAPI_AVEncCommonBufferSize,
            (uint)Math.Max(4096, (long)bitrate / 8 / _fps));
        return true;
    }

    private bool TrySetCodecU32(Guid key, uint value)
    {
        if (_codecApi == null)
            return false;

        int hr = _codecApi.SetUInt32(key, value);
        if (hr < 0)
        {
            Console.Error.WriteLine($"[encoder] CODECAPI set {key} failed: 0x{hr:X8}");
            return false;
        }

        return true;
    }

    private void TrySetCodecBool(Guid key, bool value)
    {
        int hr = _codecApi?.SetBool(key, value) ?? 0;
        if (hr < 0)
            Console.Error.WriteLine($"[encoder] CODECAPI set {key} failed: 0x{hr:X8}");
    }

    // ---- Input (capture thread) -----------------------------------------------------------

    /// <summary>
    /// Submits the newest NV12 frame. If the encoder already asked for input we feed it now;
    /// otherwise it is stashed and any previously-stashed frame is dropped (newest wins).
    /// </summary>
    public void Submit(ID3D11Texture2D nv12, uint ptsMs)
    {
        lock (_feedGate)
        {
            _pendingTexture = nv12;
            _pendingPts = ptsMs;
            _hasPending = true;
            if (_needInput)
                FeedLocked();
        }
    }

    private void FeedLocked()
    {
        var texture = _pendingTexture!;
        uint pts = _pendingPts;
        _hasPending = false;
        _needInput = false;

        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(Iid_ID3D11Texture2D, texture, 0, false);
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = pts * 10_000L;
        sample.SampleDuration = _frameDurationTicks;

        _ptsQueue.Enqueue(pts);
        _transform.ProcessInput(0, sample, 0);
    }

    // ---- Event loop (encoder thread) ------------------------------------------------------

    private void EventLoop()
    {
        // Register with the MMCSS "Games" scheduling class so ProcessOutput drains promptly under
        // a fullscreen game's load. Guarded + reverted on exit; failure is tolerated silently.
        IntPtr mmcss = NativeMethods.JoinGamesMmcss();
        try
        {
            while (_running)
            {
                IMFMediaEvent mediaEvent;
                try
                {
                    mediaEvent = _events.GetEvent(0); // blocking
                }
                catch (SharpGenException)
                {
                    break; // MFT shut down
                }

                try
                {
                    var type = mediaEvent.EventType;
                    if (!_running) break;

                    if (type == MediaEventTypes.TransformNeedInput)
                    {
                        lock (_feedGate)
                        {
                            if (_hasPending) FeedLocked();
                            else _needInput = true;
                        }
                    }
                    else if (type == MediaEventTypes.TransformHaveOutput)
                    {
                        DrainOutput();
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[encoder] event handling error: {ex.Message}");
                }
                finally
                {
                    mediaEvent.Dispose();
                }
            }
        }
        finally
        {
            NativeMethods.RevertGamesMmcss(mmcss);
        }
    }

    private void DrainOutput()
    {
        var buffer = new OutputDataBuffer
        {
            StreamID = 0,
            Sample = _outputProvidesSamples ? null! : AllocateOutputSample(),
        };

        Result r = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);

        buffer.Events?.Dispose();

        if (r.Code == MfGuids.MF_E_TRANSFORM_STREAM_CHANGE)
        {
            // Renegotiate output type and refresh the cached sequence header.
            try
            {
                using var t = _transform.GetOutputCurrentType(0);
                _cachedParamSets = NormalizeParameterSets(
                    TryGetBlob(t, MfGuids.MF_MT_MPEG_SEQUENCE_HEADER)) ?? _cachedParamSets;
            }
            catch { }
            buffer.Sample?.Dispose();
            return;
        }

        if (r.Code == MfGuids.MF_E_TRANSFORM_NEED_MORE_INPUT)
        {
            buffer.Sample?.Dispose();
            return;
        }

        if (r.Failure)
        {
            if (!_loggedProcessOutputError)
            {
                _loggedProcessOutputError = true;
                Console.Error.WriteLine($"[encoder] ProcessOutput failed: 0x{r.Code:X8}");
            }
            buffer.Sample?.Dispose();
            return;
        }

        IMFSample? outSample = buffer.Sample;
        if (outSample == null)
            return;

        try
        {
            EmitSample(outSample);
        }
        finally
        {
            outSample.Dispose();
        }
    }

    private void EmitSample(IMFSample sample)
    {
        bool keyframe;
        try { keyframe = sample.GetUInt32(MfGuids.MFSampleExtension_CleanPoint) != 0; }
        catch { keyframe = false; }

        uint pts;
        lock (_feedGate)
            pts = _ptsQueue.TryDequeue(out var p) ? p : _lastPts;
        _lastPts = pts;

        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out IntPtr ptr, out _, out int currentLength);
        try
        {
            EnsureCapacity(ref _rawBuf, currentLength);
            Marshal.Copy(ptr, _rawBuf, 0, currentLength);
        }
        finally
        {
            contiguous.Unlock();
        }

        int rawLen = currentLength;
        byte[] accessUnit = _rawBuf;
        int accessUnitLength = rawLen;
        if (!NalUtil.IsAnnexB(_rawBuf, rawLen))
        {
            if (!NalUtil.TryConvertLengthPrefixedToAnnexB(
                    _rawBuf, rawLen, ref _annexBBuf, out accessUnitLength))
            {
                if (!_loggedUnsupportedOutputFraming)
                {
                    _loggedUnsupportedOutputFraming = true;
                    Console.Error.WriteLine("[encoder] unsupported H.264 output framing; dropping access units.");
                }
                return;
            }
            accessUnit = _annexBBuf;
        }

        byte[] emit;
        int emitLen;

        bool hasSps = NalUtil.ContainsSps(accessUnit, accessUnitLength);
        if (keyframe && hasSps)
        {
            // Refresh cache from the in-band parameter sets so future IDRs can be patched.
            _cachedParamSets = NalUtil.ExtractParameterSets(accessUnit, accessUnitLength);
        }

        if (keyframe && !hasSps && _cachedParamSets is { Length: > 0 })
        {
            emitLen = _cachedParamSets.Length + accessUnitLength;
            EnsureCapacity(ref _emitBuf, emitLen);
            Buffer.BlockCopy(_cachedParamSets, 0, _emitBuf, 0, _cachedParamSets.Length);
            Buffer.BlockCopy(accessUnit, 0, _emitBuf, _cachedParamSets.Length, accessUnitLength);
            emit = _emitBuf;
        }
        else
        {
            emit = accessUnit;
            emitLen = accessUnitLength;
        }

        OnEncodedFrame?.Invoke(emit, emitLen, keyframe, pts);
    }

    private static IMFSample AllocateOutputSample()
    {
        var sample = MediaFactory.MFCreateSample();
        using var buffer = MediaFactory.MFCreateMemoryBuffer(1 << 21);
        sample.AddBuffer(buffer);
        return sample;
    }

    private static void EnsureCapacity(ref byte[] buf, int needed)
    {
        if (buf.Length < needed)
            buf = new byte[Math.Max(needed, buf.Length * 2)];
    }

    public void Dispose()
    {
        _running = false;
        try { _transform?.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero); } catch { }
        try { _transform?.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }

        // Nudge the blocking GetEvent so the thread observes _running == false and exits.
        try { _transform?.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero); } catch { }
        try { _eventThread?.Join(2000); } catch { }

        try { _codecApi?.Dispose(); } catch { }
        try { _events?.Dispose(); } catch { }
        try { _transform?.Dispose(); } catch { }
        try { _deviceManager?.Dispose(); } catch { }
        try { MediaFactory.MFShutdown(); } catch { }
    }
}

/// <summary>Thrown when no usable hardware H.264 encoder MFT is available.</summary>
public sealed class EncoderUnavailableException : Exception
{
    public EncoderUnavailableException(string message) : base(message) { }
}

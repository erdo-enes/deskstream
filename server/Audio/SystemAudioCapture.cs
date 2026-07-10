using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DeskStreamer.Server.Audio;

/// <summary>
/// Captures the default Windows playback device through WASAPI loopback and asks the
/// shared-mode audio engine to normalize it to the wire format (48 kHz stereo PCM16).
/// </summary>
public sealed class SystemAudioCapture : IDisposable
{
    private readonly MMDevice _device;
    private readonly LowLatencyLoopbackCapture _capture;
    private readonly Func<uint> _getStreamPtsMs;
    private volatile bool _stopping;

    public event Action<byte[], int, uint>? DataAvailable;
    public event Action<string>? Failed;

    public SystemAudioCapture(Func<uint> getStreamPtsMs)
    {
        _getStreamPtsMs = getStreamPtsMs;
        _device = WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice();
        _capture = new LowLatencyLoopbackCapture(_device, bufferMilliseconds: 10)
        {
            WaveFormat = new WaveFormat(
                Protocol.AudioPacket.SampleRate,
                Protocol.AudioPacket.BytesPerSample * 8,
                Protocol.AudioPacket.Channels),
        };
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    public string DeviceName => _device.FriendlyName;

    public void Start() => _capture.StartRecording();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_stopping && e.BytesRecorded > 0)
            DataAvailable?.Invoke(e.Buffer, e.BytesRecorded, _getStreamPtsMs());
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (!_stopping && e.Exception != null)
            Failed?.Invoke(e.Exception.Message);
    }

    public void Dispose()
    {
        _stopping = true;
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
    }

    /// <summary>
    /// NAudio's stock loopback type uses a 100 ms capture buffer. Windows 10 1703+ supports
    /// event-driven loopback capture, so request a 10 ms event period while retaining
    /// shared-mode resampling. Each emitted network block is still only 5 ms.
    /// </summary>
    private sealed class LowLatencyLoopbackCapture : WasapiCapture
    {
        public LowLatencyLoopbackCapture(MMDevice device, int bufferMilliseconds)
            : base(device, useEventSync: true, audioBufferMillisecondsLength: bufferMilliseconds)
        {
        }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }
}

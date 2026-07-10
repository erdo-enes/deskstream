using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;

namespace DeskStreamer.Server.Protocol;

/// <summary>Fixed ports from PROTOCOL.md. Media port is negotiated (preferred 47802).</summary>
public static class Ports
{
    public const int Discovery = 47800;   // UDP
    public const int Control = 47801;      // TCP
    public const int PreferredMedia = 47802; // UDP (server picks; reported in STREAM_STARTED)
    public const int PreferredAudio = 47803; // UDP (server picks; reported in AUDIO_STARTED)
}

/// <summary>Protocol constant.</summary>
public static class ProtocolConstants
{
    public const int Version = 1;
    public const int MaxControlFrame = 65536;
}

public static class MonotonicClock
{
    private static readonly long Origin = Stopwatch.GetTimestamp();
    public static long NowUs
    {
        get
        {
            // Split quotient/remainder before scaling. Multiplying the full tick count by
            // 1,000,000 can overflow in days on a 10 MHz Stopwatch -- exactly the kind of
            // long-session failure this shared clock exists to avoid.
            long ticks = Stopwatch.GetTimestamp() - Origin;
            long seconds = ticks / Stopwatch.Frequency;
            long remainder = ticks % Stopwatch.Frequency;
            return seconds * 1_000_000 + remainder * 1_000_000 / Stopwatch.Frequency;
        }
    }
    public static long NowMs => NowUs / 1000;
}

/// <summary>
/// Shared JSON settings. We deliberately do NOT use a naming policy: outgoing messages are
/// emitted as anonymous objects whose member names already match the wire spec exactly, and
/// incoming DTOs use [JsonPropertyName] where their C# names differ. Case-insensitive matching
/// is enabled for robustness on the incoming side.
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Serialize against the runtime type so anonymous outgoing-message objects emit their
    // properties (a plain object-typed generic call would serialize as an empty object).
    public static byte[] SerializeToUtf8(object value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<T>(utf8, Options);
}

// ---- Incoming (client -> server) DTOs. Only fields we read are declared. ----

public sealed class HelloMessage
{
    [JsonPropertyName("ver")] public int Ver { get; set; }
    [JsonPropertyName("clientId")] public string ClientId { get; set; } = "";
    [JsonPropertyName("clientName")] public string ClientName { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
}

public sealed class PairCodeMessage
{
    [JsonPropertyName("pin")] public string Pin { get; set; } = "";
}

public sealed class StartStreamMessage
{
    [JsonPropertyName("maxBitrateKbps")] public int MaxBitrateKbps { get; set; }
    [JsonPropertyName("fps")] public int Fps { get; set; }
}

public sealed class GamepadStartMessage
{
    [JsonPropertyName("controllers")] public int Controllers { get; set; }
}

public sealed class StatsMessage
{
    [JsonPropertyName("framesOk")] public int FramesOk { get; set; }
    [JsonPropertyName("framesDropped")] public int FramesDropped { get; set; }
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("intervalMs")] public int IntervalMs { get; set; }
    [JsonPropertyName("captureToReceiveP95Ms")] public int CaptureToReceiveP95Ms { get; set; } = -1;
    [JsonPropertyName("decodeToSurfaceP95Ms")] public int DecodeToSurfaceP95Ms { get; set; } = -1;
}

public sealed class InputStartMessage
{
    [JsonPropertyName("mouse")] public bool Mouse { get; set; }
}

public sealed class MouseButtonMessage
{
    [JsonPropertyName("sequence")] public uint Sequence { get; set; }
    [JsonPropertyName("button")] public string Button { get; set; } = "";
    [JsonPropertyName("down")] public bool Down { get; set; }
}

/// <summary>
/// Outgoing messages are built as plain anonymous objects at the call site so their member
/// names map 1:1 to the wire spec. These small helpers keep the field names in one place.
/// </summary>
public static class OutgoingMessages
{
    public static object DiscoveryReply(string hostname) =>
        new { type = "DSREPLY", ver = ProtocolConstants.Version, name = hostname, controlPort = Ports.Control };

    public static object HelloOk(string serverName, int width, int height) =>
        new { type = "HELLO_OK", serverName, width, height };

    public static object PairRequired() => new { type = "PAIR_REQUIRED" };

    public static object PairOk(string token) => new { type = "PAIR_OK", token };

    public static object PairFail(int attemptsLeft) => new { type = "PAIR_FAIL", attemptsLeft };

    public static object Error(string code, string message) => new { type = "ERROR", code, message };

    public static object StreamStarted(
        int mediaPort, int width, int height, int fps, string encoderBackend, long clockBaseUs) =>
        new { type = "STREAM_STARTED", mediaPort, width, height, fps, codec = "h264", encoderBackend, clockBaseUs };

    public static object AudioStarted(int audioPort) =>
        new
        {
            type = "AUDIO_STARTED",
            audioPort,
            sampleRate = AudioPacket.SampleRate,
            channels = AudioPacket.Channels,
            format = "pcm_s16le",
            packetSamples = AudioPacket.SamplesPerPacket,
        };

    public static object AudioUnavailable(string message) =>
        new { type = "AUDIO_UNAVAILABLE", message };

    public static object GamepadStarted(int controllers) =>
        new { type = "GAMEPAD_STARTED", controllers, controllerType = "xbox360" };

    public static object GamepadUnavailable(string message) =>
        new
        {
            type = "GAMEPAD_UNAVAILABLE",
            message,
            driverUrl = "https://github.com/nefarius/ViGEmBus/releases/latest",
        };

    public static object GamepadRumble(int controllerId, byte largeMotor, byte smallMotor) =>
        new { type = "GAMEPAD_RUMBLE", controllerId, largeMotor, smallMotor };

    public static object InputStarted() => new { type = "INPUT_STARTED", mouse = true };

    public static object InputUnavailable(string message) =>
        new { type = "INPUT_UNAVAILABLE", message };

    public static object StreamStopped() => new { type = "STREAM_STOPPED" };

    public static object Bitrate(int kbps) => new { type = "BITRATE", kbps };

    public static object Pong(long? t0Us = null, long? t1Us = null, long? t2Us = null) =>
        new { type = "PONG", t0Us, t1Us, t2Us };
}

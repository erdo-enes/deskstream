using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DeskStreamer.Server.Net;
using DeskStreamer.Server.Protocol;
using DeskStreamer.Server.Session;

namespace DeskStreamer.Server.Web;

/// <summary>
/// A dependency-free, <see cref="TcpListener"/>-based local status dashboard. It serves a
/// single embedded HTML page that polls GET /api/status once per second, plus tiny JSON control
/// endpoints. It never touches the pipeline directly: Restart is marshalled onto the control
/// read loop through <see cref="StreamSession.RequestRestart"/>, keeping pipeline lifecycle
/// changes single-threaded. The deliberately small HTTP/1.1 implementation handles one bounded
/// request per connection and always replies with Connection: close.
/// </summary>
public sealed class WebDashboard : IDisposable
{
    private const int MaxRequestLineBytes = 2 * 1024;
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 4 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ControlServer _control;
    private readonly ServerOptions _options;
    private readonly string _hostname;
    private readonly IReadOnlyList<string> _listenIps;
    private readonly bool _lan;
    private readonly int _port;

    // Sent fps/kbps are derived from monotonic byte/frame counters as deltas between polls.
    private readonly object _rateGate = new();
    private long _prevFrames;
    private long _prevBytes;
    private long _prevTicks;

    private Task? _loop;
    private bool _started;

    public WebDashboard(
        ControlServer control,
        ServerOptions options,
        string hostname,
        IEnumerable<string> listenIps,
        bool lan,
        int port)
    {
        _control = control;
        _options = options;
        _hostname = hostname;
        _listenIps = listenIps.ToList();
        _lan = lan;
        _port = port;
        _listener = new TcpListener(lan ? IPAddress.Any : IPAddress.Loopback, port);
    }

    /// <summary>Human-readable URL for console output.</summary>
    public string Url => _lan ? $"http://<this-pc>:{_port}/" : $"http://127.0.0.1:{_port}/";

    public void Start()
    {
        if (_started)
            return;

        try
        {
            _listener.Start(backlog: 32);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            // A busy/unavailable port must never take down the streamer.
            Console.Error.WriteLine($"[web] dashboard disabled: could not bind {Url} ({ex.Message}).");
            return;
        }

        _started = true;
        _loop = Task.Run(AcceptLoopAsync);

        if (_lan)
        {
            Console.WriteLine();
            Console.WriteLine("  ****************************************************************");
            Console.WriteLine("  * WARNING: web dashboard is bound to ALL interfaces (--web-lan) *");
            Console.WriteLine("  * It is UNAUTHENTICATED and the pairing PIN is visible to      *");
            Console.WriteLine("  * anyone on the local network. Use only on trusted LANs.       *");
            Console.WriteLine("  ****************************************************************");
            Console.WriteLine();
        }
        Console.WriteLine($"Web dashboard: {Url}");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (_stop.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }

            // A slow/malformed dashboard client must not block the accept loop or other clients.
            _ = HandleClientAsync(client, _stop.Token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken shutdown)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown))
        {
            timeout.CancelAfter(RequestTimeout);
            client.NoDelay = true;

            try
            {
                NetworkStream stream = client.GetStream();
                RequestReadResult result = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
                if (result.Request == null)
                {
                    if (result.ErrorStatus != 0)
                    {
                        await WriteJsonAsync(
                            stream,
                            result.ErrorStatus,
                            new { error = result.Error ?? "bad request" },
                            timeout.Token).ConfigureAwait(false);
                    }
                    return;
                }

                await HandleAsync(stream, result.Request, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown or a slow/incomplete request: closing the connection is sufficient.
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // Client went away mid-request/response; nothing to do.
            }
            catch
            {
                // Never let one bad request surface as an unobserved task failure.
            }
        }
    }

    private async Task HandleAsync(NetworkStream stream, ParsedRequest req, CancellationToken cancellationToken)
    {
        string path = req.Path;
        string method = req.Method;

        if (method == "POST" && !IsMutationOriginAllowed(req))
        {
            await WriteJsonAsync(stream, 403, new { error = "cross-origin mutation rejected" }, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (method == "GET" && path == "/")
        {
            await WriteTextAsync(stream, 200, "text/html; charset=utf-8", DashboardHtml, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (method == "GET" && path == "/api/status")
        {
            await WriteJsonAsync(stream, 200, BuildStatus(), cancellationToken).ConfigureAwait(false);
        }
        else if (method == "POST" && path == "/api/restart")
        {
            _control.Current?.RequestRestart();
            await WriteJsonAsync(stream, 200, new { ok = true }, cancellationToken).ConfigureAwait(false);
        }
        else if (method == "POST" && path == "/api/quality")
        {
            _options.DefaultQuality = ReadQuality(req.Body);
            await WriteJsonAsync(stream, 200, new { ok = true }, cancellationToken).ConfigureAwait(false);
        }
        else if (method != "GET" && method != "POST")
        {
            await WriteJsonAsync(stream, 405, new { error = "method not allowed" }, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await WriteJsonAsync(stream, 404, new { error = "not found" }, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<RequestReadResult> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[MaxHeaderBytes];
        int buffered = 0;
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            if (buffered == MaxHeaderBytes)
                return RequestReadResult.Failure(431, "request headers too large");

            int count = Math.Min(2048, MaxHeaderBytes - buffered);
            int read = await stream.ReadAsync(buffer.AsMemory(buffered, count), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return buffered == 0
                    ? RequestReadResult.Closed()
                    : RequestReadResult.Failure(400, "incomplete request headers");
            }

            buffered += read;
            headerEnd = FindHeaderEnd(buffer, buffered);
        }

        string headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0 || Encoding.ASCII.GetByteCount(lines[0]) > MaxRequestLineBytes)
            return RequestReadResult.Failure(400, "invalid request line");

        string[] requestParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length != 3 ||
            !IsToken(requestParts[0]) ||
            (requestParts[2] != "HTTP/1.1" && requestParts[2] != "HTTP/1.0"))
        {
            return RequestReadResult.Failure(400, "invalid request line");
        }

        string target = requestParts[1];
        if (target.Length == 0 || target[0] != '/' || target.Any(char.IsControl))
            return RequestReadResult.Failure(400, "invalid request target");

        int? contentLength = null;
        bool hasTransferEncoding = false;
        string? host = null;
        string? origin = null;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            int colon = line.IndexOf(':');
            if (colon <= 0)
                return RequestReadResult.Failure(400, "malformed request header");

            string name = line[..colon];
            string value = line[(colon + 1)..].Trim();
            if (!IsToken(name) || value.Any(c => c < ' ' && c != '\t'))
                return RequestReadResult.Failure(400, "malformed request header");

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ||
                    parsed < 0)
                {
                    return RequestReadResult.Failure(400, "invalid content length");
                }

                if (contentLength.HasValue && contentLength.Value != parsed)
                    return RequestReadResult.Failure(400, "conflicting content length");
                contentLength = parsed;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && value.Length != 0)
            {
                hasTransferEncoding = true;
            }
            else if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                if (host != null && !host.Equals(value, StringComparison.OrdinalIgnoreCase))
                    return RequestReadResult.Failure(400, "conflicting host header");
                host = value;
            }
            else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
            {
                if (origin != null && !origin.Equals(value, StringComparison.Ordinal))
                    return RequestReadResult.Failure(400, "conflicting origin header");
                origin = value;
            }
        }

        if (hasTransferEncoding)
            return RequestReadResult.Failure(400, "transfer encoding is not supported");

        int bodyLength = contentLength ?? 0;
        if (bodyLength > MaxBodyBytes)
            return RequestReadResult.Failure(413, "request body too large");

        byte[] body = new byte[bodyLength];
        int bodyOffset = headerEnd + 4;
        int bodyBuffered = Math.Min(bodyLength, buffered - bodyOffset);
        if (bodyBuffered > 0)
            Buffer.BlockCopy(buffer, bodyOffset, body, 0, bodyBuffered);

        int bodyRead = bodyBuffered;
        while (bodyRead < bodyLength)
        {
            int read = await stream.ReadAsync(body.AsMemory(bodyRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return RequestReadResult.Failure(400, "incomplete request body");
            bodyRead += read;
        }

        int query = target.IndexOf('?');
        string path = query >= 0 ? target[..query] : target;
        return RequestReadResult.Success(new ParsedRequest(requestParts[0], path, body, host, origin));
    }

    private static int FindHeaderEnd(byte[] bytes, int count)
    {
        for (int i = 0; i <= count - 4; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' &&
                bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static bool IsToken(string value)
    {
        if (value.Length == 0)
            return false;

        const string separators = "()<>@,;:\\\"/[]?={} \t";
        return value.All(c => c > 0x20 && c < 0x7f && !separators.Contains(c));
    }

    private static bool IsMutationOriginAllowed(ParsedRequest request)
    {
        // Native clients and command-line tools generally omit Origin. When a browser supplies
        // it, require the authority to be exactly the authority it used for this HTTP request;
        // this prevents an arbitrary website from issuing simple POSTs to localhost/the LAN IP.
        if (request.Origin == null)
            return true;
        if (string.IsNullOrWhiteSpace(request.Host) ||
            !Uri.TryCreate(request.Origin, UriKind.Absolute, out Uri? origin) ||
            (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps) ||
            origin.UserInfo.Length != 0 ||
            origin.AbsolutePath != "/" ||
            origin.Query.Length != 0 ||
            origin.Fragment.Length != 0)
        {
            return false;
        }

        return origin.Authority.Equals(request.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadQuality(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("quality", out var q) && q.ValueKind == JsonValueKind.String)
                return ServerOptions.Normalize(q.GetString());
        }
        catch (JsonException) { }
        catch (IOException) { }
        return "native";
    }

    private object BuildStatus()
    {
        var session = _control.Current;
        bool connected = session != null;
        bool streaming = session is { Streaming: true };

        string state = session == null
            ? "idle"
            : streaming ? "streaming"
            : session.State == SessionState.Ready ? "ready"
            : "idle";

        long sentFps = 0;
        long sentKbps = 0;
        lock (_rateGate)
        {
            long frames = session?.MediaFramesSent ?? 0;
            long bytes = session?.MediaBytesSent ?? 0;
            long nowTicks = Stopwatch.GetTimestamp();

            if (streaming && _prevTicks != 0 && frames >= _prevFrames && bytes >= _prevBytes)
            {
                double elapsed = (nowTicks - _prevTicks) / (double)Stopwatch.Frequency;
                if (elapsed > 0.001)
                {
                    sentFps = (long)Math.Round((frames - _prevFrames) / elapsed);
                    sentKbps = (long)Math.Round((bytes - _prevBytes) * 8 / 1000.0 / elapsed);
                }
            }

            _prevFrames = frames;
            _prevBytes = bytes;
            _prevTicks = nowTicks;
        }

        return new
        {
            server = _hostname,
            protocol = ProtocolConstants.Version,
            clientConnected = connected,
            clientName = session?.ClientName,
            clientIp = session?.ClientIp,
            state,
            pin = session?.PendingPin,
            width = streaming ? session!.StreamWidth : 0,
            height = streaming ? session!.StreamHeight : 0,
            fps = streaming ? session!.Fps : 0,
            quality = streaming ? session!.Quality : _options.DefaultQuality,
            codec = "h264",
            encoderBackend = streaming ? session!.EncoderBackend : "",
            bitrateKbps = streaming ? session!.CurrentBitrateKbps : 0,
            sentFps,
            sentKbps,
            clientFramesDropped = session?.LastClientFramesDropped ?? 0,
            audio = session?.AudioStreaming ?? false,
            gamepads = session?.GamepadCount ?? 0,
            defaultQuality = _options.DefaultQuality,
            serverMaxBitrateKbps = _options.MaxBitrateKbps,
            listen = _listenIps,
            ports = new
            {
                control = Ports.Control,
                video = Ports.PreferredMedia,
                audio = Ports.PreferredAudio,
            },
        };
    }

    private static Task WriteJsonAsync(
        NetworkStream stream,
        int status,
        object payload,
        CancellationToken cancellationToken)
    {
        byte[] body = Json.SerializeToUtf8(payload);
        return WriteBytesAsync(stream, status, "application/json", body, cancellationToken);
    }

    private static Task WriteTextAsync(
        NetworkStream stream,
        int status,
        string contentType,
        string body,
        CancellationToken cancellationToken) =>
        WriteBytesAsync(stream, status, contentType, Encoding.UTF8.GetBytes(body), cancellationToken);

    private static async Task WriteBytesAsync(
        NetworkStream stream,
        int status,
        string contentType,
        byte[] body,
        CancellationToken cancellationToken)
    {
        string headers =
            $"HTTP/1.1 {status} {ReasonPhrase(status)}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Content-Security-Policy: default-src 'self'; style-src 'unsafe-inline'; " +
                "script-src 'unsafe-inline'; frame-ancestors 'none'\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "X-Frame-Options: DENY\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        413 => "Payload Too Large",
        431 => "Request Header Fields Too Large",
        _ => "Error",
    };

    private sealed record ParsedRequest(
        string Method,
        string Path,
        byte[] Body,
        string? Host,
        string? Origin);

    private sealed record RequestReadResult(ParsedRequest? Request, int ErrorStatus, string? Error)
    {
        public static RequestReadResult Success(ParsedRequest request) => new(request, 0, null);
        public static RequestReadResult Failure(int status, string error) => new(null, status, error);
        public static RequestReadResult Closed() => new(null, 0, null);
    }

    public void Dispose()
    {
        if (_started)
        {
            try { _stop.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
        }
        try { _loop?.Wait(1000); } catch { }
    }

    // ---- Embedded single-file dashboard ---------------------------------------------------

    private const string DashboardHtml = """
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>DeskStream</title>
<style>
  :root {
    --bg:#f4f5f7; --card:#ffffff; --fg:#1b1f24; --muted:#6b7480; --line:#e3e6ea;
    --accent:#3b82f6; --ok:#22c55e; --warn:#f59e0b; --idle:#9aa4b0; --danger:#ef4444;
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --bg:#0f1216; --card:#171b21; --fg:#e6e9ee; --muted:#8b95a2; --line:#252b33;
      --accent:#60a5fa; --ok:#4ade80; --warn:#fbbf24; --idle:#5b6675; --danger:#f87171;
    }
  }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--fg);
    font:14px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif; }
  .wrap { max-width:920px; margin:0 auto; padding:20px 16px 48px; }
  header { display:flex; flex-wrap:wrap; align-items:baseline; gap:8px 14px; margin-bottom:6px; }
  h1 { font-size:20px; margin:0; }
  .sub { color:var(--muted); font-size:13px; }
  .sub code { background:var(--line); padding:1px 6px; border-radius:5px; }
  .card { background:var(--card); border:1px solid var(--line); border-radius:12px;
    padding:16px; margin-top:16px; }
  .pin { text-align:center; border-color:var(--warn); }
  .pin .label { color:var(--muted); text-transform:uppercase; letter-spacing:.08em; font-size:12px; }
  .pin .code { font-size:44px; font-weight:700; letter-spacing:.18em; margin:6px 0 0; }
  .row { display:flex; align-items:center; justify-content:space-between; gap:12px; flex-wrap:wrap; }
  .chip { display:inline-flex; align-items:center; gap:8px; font-weight:600;
    padding:5px 12px; border-radius:999px; border:1px solid var(--line); }
  .dot { width:9px; height:9px; border-radius:50%; background:var(--idle); }
  .chip.streaming .dot { background:var(--ok); }
  .chip.ready .dot { background:var(--accent); }
  .chip.idle .dot { background:var(--idle); }
  .who { font-size:15px; }
  .who b { font-weight:600; }
  .who .ip { color:var(--muted); }
  .grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(140px,1fr));
    gap:12px; margin-top:4px; }
  .stat { background:var(--bg); border:1px solid var(--line); border-radius:10px; padding:10px 12px; }
  .stat .k { color:var(--muted); font-size:12px; }
  .stat .v { font-size:17px; font-weight:600; margin-top:2px; word-break:break-word; }
  .controls { display:flex; gap:10px; flex-wrap:wrap; align-items:center; margin-top:4px; }
  button, select { font:inherit; color:var(--fg); background:var(--card);
    border:1px solid var(--line); border-radius:9px; padding:8px 14px; cursor:pointer; }
  button.primary { background:var(--accent); border-color:var(--accent); color:#fff; }
  button:disabled { opacity:.45; cursor:not-allowed; }
  .qwrap { margin-left:auto; color:var(--muted); display:flex; align-items:center; gap:8px; }
  .muted { color:var(--muted); }
</style>
<div class="wrap">
  <header>
    <h1>DeskStream</h1>
    <span class="sub" id="hdr"></span>
  </header>
  <div class="sub" id="listen"></div>

  <div class="card pin" id="pinCard" style="display:none">
    <div class="label">Pairing PIN — enter on your device</div>
    <div class="code" id="pin">------</div>
  </div>

  <div class="card">
    <div class="row">
      <div class="who" id="who">No client connected</div>
      <span class="chip idle" id="chip"><span class="dot"></span><span id="chipText">idle</span></span>
    </div>
    <div class="grid" id="stats"></div>
  </div>

  <div class="card">
    <div class="controls">
      <button class="primary" id="restartBtn" onclick="cmd('restart')">Restart stream</button>
      <span class="qwrap">
        Default quality
        <select id="quality" onchange="setQuality(this.value)">
          <option value="native">Native</option>
          <option value="720p">720p</option>
        </select>
      </span>
    </div>
  </div>
  <p class="muted" id="err"></p>
</div>
<script>
  function stat(k, v) { return '<div class="stat"><div class="k">' + k + '</div><div class="v">' + v + '</div></div>'; }
  function esc(s) { return String(s == null ? '' : s).replace(/[&<>]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;'}[c])); }
  let qFocus = false;
  document.getElementById('quality').addEventListener('focus', () => qFocus = true);
  document.getElementById('quality').addEventListener('blur', () => qFocus = false);

  async function cmd(name) {
    try { await fetch('/api/' + name, { method:'POST' }); } catch (e) {}
    setTimeout(refresh, 250);
  }
  async function setQuality(v) {
    try { await fetch('/api/quality', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ quality:v }) }); } catch (e) {}
  }

  async function refresh() {
    let s;
    try {
      const r = await fetch('/api/status', { cache:'no-store' });
      s = await r.json();
      document.getElementById('err').textContent = '';
    } catch (e) {
      document.getElementById('err').textContent = 'Cannot reach the server.';
      return;
    }

    document.getElementById('hdr').innerHTML =
      esc(s.server) + ' &middot; protocol v' + esc(s.protocol);
    const ports = s.ports || {};
    document.getElementById('listen').innerHTML =
      'Listening on ' + (s.listen || []).map(ip => '<code>' + esc(ip) + '</code>').join(' ') +
      ' &middot; control ' + esc(ports.control) + ' &middot; video ' + esc(ports.video) +
      ' &middot; audio ' + esc(ports.audio);

    const pinCard = document.getElementById('pinCard');
    if (s.pin) { pinCard.style.display = ''; document.getElementById('pin').textContent = s.pin; }
    else { pinCard.style.display = 'none'; }

    const chip = document.getElementById('chip');
    chip.className = 'chip ' + s.state;
    document.getElementById('chipText').textContent = s.state;

    if (s.clientConnected) {
      document.getElementById('who').innerHTML =
        '<b>' + esc(s.clientName || 'Client') + '</b> <span class="ip">' + esc(s.clientIp || '') + '</span>';
    } else {
      document.getElementById('who').textContent = 'No client connected';
    }

    const streaming = s.state === 'streaming';
    document.getElementById('stats').innerHTML =
      stat('Resolution', streaming ? (s.width + '×' + s.height) : '—') +
      stat('FPS', streaming ? s.fps : '—') +
      stat('Quality', esc(s.quality)) +
      stat('Codec', esc(s.codec)) +
      stat('Encoder', streaming ? esc(s.encoderBackend || '—') : '—') +
      stat('Target bitrate', streaming ? (s.bitrateKbps + ' kbps') : '—') +
      stat('Server bitrate cap', s.serverMaxBitrateKbps + ' kbps') +
      stat('Sent', streaming ? (s.sentFps + ' fps / ' + s.sentKbps + ' kbps') : '—') +
      stat('Client drops', s.clientFramesDropped) +
      stat('Audio', s.audio ? 'on' : 'off') +
      stat('Gamepads', s.gamepads);

    document.getElementById('restartBtn').disabled = !streaming;
    if (!qFocus) document.getElementById('quality').value = s.defaultQuality || 'native';
  }

  refresh();
  setInterval(refresh, 1000);
</script>
""";
}

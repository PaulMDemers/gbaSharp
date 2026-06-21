using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Gba.Core.Input;

namespace Gba.Desktop;

internal sealed class DesktopControlServer : IDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly MainForm _form;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _acceptTask;
    private readonly string _discoveryPath;

    private DesktopControlServer(MainForm form, TcpListener listener)
    {
        _form = form;
        _listener = listener;
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{Port}";
        _discoveryPath = Path.Combine(Path.GetTempPath(), "gbaSharp-control.json");
        WriteDiscoveryFile();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
    }

    public int Port { get; }

    public string BaseUrl { get; }

    public static DesktopControlServer Start(MainForm form, int requestedPort)
    {
        var listener = StartListener(requestedPort);
        return new DesktopControlServer(form, listener);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _listener.Stop();
        try
        {
            _acceptTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException or SocketException or ObjectDisposedException))
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _cancellation.Dispose();
        DeleteDiscoveryFile();
    }

    private static TcpListener StartListener(int requestedPort)
    {
        if (requestedPort != DesktopStartupOptions.DefaultControlPort)
        {
            return StartListenerOnPort(requestedPort);
        }

        for (var port = requestedPort; port < requestedPort + 32; port++)
        {
            try
            {
                return StartListenerOnPort(port);
            }
            catch (SocketException)
            {
            }
        }

        throw new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    private static TcpListener StartListenerOnPort(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return listener;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            Request request;
            try
            {
                request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await WriteJsonAsync(stream, 400, new { error = ex.Message }, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            try
            {
                await DispatchAsync(stream, request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                await WriteJsonAsync(stream, 400, new { error = ex.Message }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DispatchAsync(NetworkStream stream, Request request, CancellationToken cancellationToken)
    {
        if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            await WriteBytesAsync(stream, 204, "text/plain", [], cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (request.Path)
        {
            case "/":
                await WriteJsonAsync(stream, 200, DescribeApi(), cancellationToken).ConfigureAwait(false);
                return;
            case "/status" when IsGet(request):
                await WriteJsonAsync(stream, 200, _form.GetControlStatus(), cancellationToken).ConfigureAwait(false);
                return;
            case "/game/ruby/state" when IsGet(request):
                await WriteJsonAsync(stream, 200, _form.GetRubyState(), cancellationToken).ConfigureAwait(false);
                return;
            case "/screenshot" when IsGet(request):
                await WriteScreenshotAsync(stream, request, cancellationToken).ConfigureAwait(false);
                return;
            case "/input/tap" when IsPost(request):
                await WriteJsonAsync(
                    stream,
                    200,
                    await SendTimedInputAsync(
                        "tap",
                        ParseKeys(request),
                        ParseInt(request, "duration", 90, 10, 2_000),
                        ParseInt(request, "delay", 0, 0, 10_000),
                        cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                return;
            case "/input/face" when IsPost(request):
                await WriteJsonAsync(
                    stream,
                    200,
                    await SendTimedInputAsync(
                        "face",
                        ParseDirection(request),
                        ParseInt(request, "duration", 45, 10, 500),
                        ParseInt(request, "delay", 120, 0, 10_000),
                        cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                return;
            case "/input/tile-step" when IsPost(request):
            case "/input/step-tile" when IsPost(request):
                await WriteJsonAsync(
                    stream,
                    200,
                    await SendTimedInputAsync(
                        "tile-step",
                        ParseDirection(request),
                        ParseInt(request, "duration", 170, 30, 2_000),
                        ParseInt(request, "delay", 250, 0, 10_000),
                        cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                return;
            case "/input/walk-tile" when IsPost(request):
            case "/input/verified-tile-step" when IsPost(request):
                await WriteJsonAsync(stream, 200, await SendWalkTileAsync(request, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                return;
            case "/input/sequence" when IsPost(request):
                await WriteJsonAsync(stream, 200, await SendSequenceAsync(request, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                return;
            case "/input/press" when IsPost(request):
                _form.ControlPress(ParseKeys(request));
                await WriteJsonAsync(stream, 200, _form.GetControlStatus(), cancellationToken).ConfigureAwait(false);
                return;
            case "/input/release" when IsPost(request):
                _form.ControlRelease(ParseKeys(request));
                await WriteJsonAsync(stream, 200, _form.GetControlStatus(), cancellationToken).ConfigureAwait(false);
                return;
            case "/input/set" when IsPost(request):
                _form.ControlSetKeys(ParseKeys(request));
                await WriteJsonAsync(stream, 200, _form.GetControlStatus(), cancellationToken).ConfigureAwait(false);
                return;
            case "/input/clear" when IsPost(request):
                _form.ControlSetKeys(GbaKey.None);
                await WriteJsonAsync(stream, 200, _form.GetControlStatus(), cancellationToken).ConfigureAwait(false);
                return;
            case "/emulation/run" when IsPost(request):
                await _form.ControlRunAsync().ConfigureAwait(false);
                await WriteJsonAsync(stream, 200, _form.GetControlStatus(), cancellationToken).ConfigureAwait(false);
                return;
            case "/emulation/pause" when IsPost(request):
                await _form.ControlPauseAsync().ConfigureAwait(false);
                await WriteJsonAsync(stream, 200, _form.GetControlStatus(), cancellationToken).ConfigureAwait(false);
                return;
            case "/emulation/toggle" when IsPost(request):
                await _form.ControlTogglePauseAsync().ConfigureAwait(false);
                await WriteJsonAsync(stream, 200, _form.GetControlStatus(), cancellationToken).ConfigureAwait(false);
                return;
            case "/emulation/reset" when IsPost(request):
                await _form.ControlResetAsync().ConfigureAwait(false);
                await WriteJsonAsync(stream, 200, _form.GetControlStatus(), cancellationToken).ConfigureAwait(false);
                return;
            case "/emulation/step" when IsPost(request):
                await _form.ControlStepFrameAsync().ConfigureAwait(false);
                await WriteJsonAsync(stream, 200, _form.GetControlStatus(), cancellationToken).ConfigureAwait(false);
                return;
            case "/app/close" when IsPost(request):
                await WriteJsonAsync(stream, 200, new { closing = true }, cancellationToken).ConfigureAwait(false);
                await _form.ControlCloseAsync().ConfigureAwait(false);
                return;
        }

        var methodAllowed = request.Path.StartsWith("/input/", StringComparison.OrdinalIgnoreCase)
            || request.Path.StartsWith("/emulation/", StringComparison.OrdinalIgnoreCase);
        await WriteJsonAsync(stream, methodAllowed ? 405 : 404, new { error = methodAllowed ? "Method not allowed." : "Unknown endpoint." }, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteScreenshotAsync(NetworkStream stream, Request request, CancellationToken cancellationToken)
    {
        byte[] png;
        try
        {
            png = _form.CaptureControlScreenshotPng(ParseScreenshotOptions(request));
        }
        catch (InvalidOperationException ex)
        {
            await WriteJsonAsync(stream, 409, new { error = ex.Message }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteBytesAsync(stream, 200, "image/png", png, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> SendSequenceAsync(Request request, CancellationToken cancellationToken)
    {
        if (!request.Query.TryGetValue("steps", out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Missing required 'steps' query parameter.");
        }

        var defaultDuration = ParseInt(request, "duration", 90, 10, 2_000);
        var defaultGap = ParseInt(request, "gap", 120, 0, 10_000);
        var results = new List<DesktopInputResult>();
        foreach (var rawStep in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = rawStep.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            if (parts[0].Equals("wait", StringComparison.OrdinalIgnoreCase))
            {
                var waitMs = parts.Length >= 2 ? ParseInt(parts[1], "wait", 0, 0, 10_000) : defaultGap;
                await Task.Delay(waitMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var keys = ParseKeys(parts[0]);
            var durationMs = parts.Length >= 2 ? ParseInt(parts[1], "duration", defaultDuration, 10, 2_000) : defaultDuration;
            var gapMs = parts.Length >= 3 ? ParseInt(parts[2], "gap", defaultGap, 0, 10_000) : defaultGap;
            results.Add(await SendTimedInputAsync("sequence-step", keys, durationMs, gapMs, cancellationToken).ConfigureAwait(false));
        }

        return new
        {
            command = "sequence",
            steps = results,
            status = _form.GetControlStatus()
        };
    }

    private async Task SendTapAsync(GbaKey keys, int durationMs, CancellationToken cancellationToken)
    {
        _form.ControlPress(keys);
        await Task.Delay(durationMs, cancellationToken).ConfigureAwait(false);
        _form.ControlRelease(keys);
    }

    private async Task<DesktopInputResult> SendTimedInputAsync(string command, GbaKey keys, int durationMs, int delayMs, CancellationToken cancellationToken)
    {
        var before = _form.GetControlStatus();
        await SendTapAsync(keys, durationMs, cancellationToken).ConfigureAwait(false);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        return new DesktopInputResult(command, keys.ToString(), durationMs, delayMs, before, _form.GetControlStatus());
    }

    private async Task<object> SendWalkTileAsync(Request request, CancellationToken cancellationToken)
    {
        var direction = ParseDirection(request);
        var timeoutMs = ParseInt(request, "timeout", 900, 100, 3_000);
        var delayMs = ParseInt(request, "delay", 180, 0, 10_000);
        var fallbackDurationMs = ParseInt(request, "duration", 170, 30, 2_000);
        var beforeStatus = _form.GetControlStatus();
        var beforeRuby = _form.GetRubyState();
        var beforePosition = beforeRuby.SaveBlockPlayer;
        if (beforePosition is null || !beforeRuby.IsRubyOrSapphire)
        {
            var fallback = await SendTimedInputAsync("walk-tile-fallback", direction, fallbackDurationMs, delayMs, cancellationToken).ConfigureAwait(false);
            return new
            {
                command = "walk-tile",
                direction = direction.ToString(),
                verified = false,
                reason = beforePosition is null ? "Ruby saveblock position unavailable." : "Loaded ROM is not Pokemon Ruby/Sapphire.",
                fallback
            };
        }

        var (targetX, targetY) = TargetPosition(beforePosition, direction);
        var stopwatch = Stopwatch.StartNew();
        DesktopRubyState afterRuby;
        _form.ControlPress(direction);
        try
        {
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                await Task.Delay(15, cancellationToken).ConfigureAwait(false);
                afterRuby = _form.GetRubyState();
                if (afterRuby.SaveBlockPlayer is { } current)
                {
                    var verificationType = PositionVerificationType(beforePosition, current, targetX, targetY, direction);
                    if (verificationType is not null)
                    {
                        _form.ControlRelease(direction);
                        if (delayMs > 0)
                        {
                            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                        }

                        return new
                        {
                            command = "walk-tile",
                            direction = direction.ToString(),
                            verified = true,
                            verificationType,
                            elapsedMs = stopwatch.ElapsedMilliseconds,
                            target = new { x = targetX, y = targetY, mapId = beforePosition.MapId },
                            beforeStatus,
                            afterStatus = _form.GetControlStatus(),
                            beforeRuby,
                            afterRuby = _form.GetRubyState()
                        };
                    }
                }
            }
        }
        finally
        {
            _form.ControlRelease(direction);
        }

        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        afterRuby = _form.GetRubyState();
        return new
        {
            command = "walk-tile",
            direction = direction.ToString(),
            verified = false,
            verificationType = "timeout",
            reason = "Timed out waiting for Ruby player coordinates to change.",
            elapsedMs = stopwatch.ElapsedMilliseconds,
            target = new { x = targetX, y = targetY, mapId = beforePosition.MapId },
            beforeStatus,
            afterStatus = _form.GetControlStatus(),
            beforeRuby,
            afterRuby
        };
    }

    private static string? PositionVerificationType(DesktopRubyPlayerPosition before, DesktopRubyPlayerPosition current, int targetX, int targetY, GbaKey direction)
    {
        if (current.MapId != before.MapId)
        {
            return "map-transition";
        }

        if (current.X == targetX && current.Y == targetY)
        {
            return "coordinate";
        }

        return HasMovedInDirection(before, current, direction) ? "directional-coordinate" : null;
    }

    private static (int X, int Y) TargetPosition(DesktopRubyPlayerPosition position, GbaKey direction)
        => direction switch
        {
            GbaKey.Up => (position.X, position.Y - 1),
            GbaKey.Down => (position.X, position.Y + 1),
            GbaKey.Left => (position.X - 1, position.Y),
            GbaKey.Right => (position.X + 1, position.Y),
            _ => (position.X, position.Y)
        };

    private static bool HasMovedInDirection(DesktopRubyPlayerPosition before, DesktopRubyPlayerPosition current, GbaKey direction)
        => direction switch
        {
            GbaKey.Up => current.Y < before.Y,
            GbaKey.Down => current.Y > before.Y,
            GbaKey.Left => current.X < before.X,
            GbaKey.Right => current.X > before.X,
            _ => false
        };

    private static async Task<Request> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(1024);
        var buffer = new byte[1];
        while (bytes.Count < MaxHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            bytes.Add(buffer[0]);
            if (HasHeaderTerminator(bytes))
            {
                break;
            }
        }

        if (bytes.Count == 0)
        {
            throw new InvalidOperationException("Empty request.");
        }

        var header = Encoding.ASCII.GetString(bytes.ToArray());
        var firstLineEnd = header.IndexOf("\r\n", StringComparison.Ordinal);
        if (firstLineEnd < 0)
        {
            throw new InvalidOperationException("Malformed HTTP request.");
        }

        var parts = header[..firstLineEnd].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("Malformed HTTP request line.");
        }

        var target = parts[1];
        var uri = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? new Uri(target)
            : new Uri($"http://127.0.0.1{target}");
        return new Request(parts[0], uri.AbsolutePath.TrimEnd('/').Length == 0 ? "/" : uri.AbsolutePath.TrimEnd('/'), ParseQuery(uri.Query));
    }

    private static bool HasHeaderTerminator(List<byte> bytes)
    {
        var count = bytes.Count;
        return count >= 4
            && bytes[count - 4] == '\r'
            && bytes[count - 3] == '\n'
            && bytes[count - 2] == '\r'
            && bytes[count - 1] == '\n';
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = query.TrimStart('?');
        if (trimmed.Length == 0)
        {
            return values;
        }

        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = WebUtility.UrlDecode(parts[0]);
            var value = parts.Length == 2 ? WebUtility.UrlDecode(parts[1]) : string.Empty;
            values[key] = value;
        }

        return values;
    }

    private static GbaKey ParseKeys(Request request)
    {
        if (!request.Query.TryGetValue("keys", out var value))
        {
            throw new ArgumentException("Missing required 'keys' query parameter.");
        }

        return ParseKeys(value);
    }

    private static DesktopScreenshotOptions ParseScreenshotOptions(Request request)
    {
        var overlay = request.Query.TryGetValue("overlay", out var overlayValue) ? overlayValue : string.Empty;
        return new DesktopScreenshotOptions(
            overlay,
            ParseInt(request, "scale", 4, 1, 8),
            ParseInt(request, "tiles", 9, 3, 13),
            request.Query.TryGetValue("atlas", out var atlasPath) ? atlasPath : string.Empty);
    }

    private static int ParseInt(Request request, string key, int defaultValue, int minValue, int maxValue)
    {
        if (!request.Query.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return ParseInt(value, key, defaultValue, minValue, maxValue);
    }

    private static int ParseInt(string value, string name, int defaultValue, int minValue, int maxValue)
    {
        if (!int.TryParse(value, out var result))
        {
            throw new ArgumentException($"Invalid {name} value '{value}'.");
        }

        if (result < minValue || result > maxValue)
        {
            throw new ArgumentException($"{name} must be between {minValue} and {maxValue}.");
        }

        return result;
    }

    private static GbaKey ParseKeys(string value)
    {
        var keys = GbaKey.None;
        foreach (var part in value.Split([',', '+', '|', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("none", StringComparison.OrdinalIgnoreCase) || part.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Enum.TryParse<GbaKey>(part, ignoreCase: true, out var key) || key == GbaKey.None)
            {
                throw new ArgumentException($"Unknown GBA key '{part}'.");
            }

            keys |= key;
        }

        return keys;
    }

    private static GbaKey ParseDirection(Request request)
    {
        var value = request.Query.TryGetValue("key", out var keyValue)
            ? keyValue
            : request.Query.TryGetValue("keys", out var keysValue)
                ? keysValue
                : throw new ArgumentException("Missing required 'key' query parameter.");
        var key = ParseKeys(value);
        if (key is not (GbaKey.Up or GbaKey.Down or GbaKey.Left or GbaKey.Right))
        {
            throw new ArgumentException("Direction must be exactly one of Up, Down, Left, or Right.");
        }

        return key;
    }

    private static bool IsGet(Request request) => request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase);

    private static bool IsPost(Request request) => request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteJsonAsync(NetworkStream stream, int statusCode, object body, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        await WriteBytesAsync(stream, statusCode, "application/json; charset=utf-8", bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteBytesAsync(NetworkStream stream, int statusCode, string contentType, byte[] body, CancellationToken cancellationToken)
    {
        var reason = statusCode switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            404 => "Not Found",
            405 => "Method Not Allowed",
            409 => "Conflict",
            _ => "OK"
        };
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Access-Control-Allow-Origin: http://127.0.0.1\r\n" +
            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
            "Access-Control-Allow-Headers: Content-Type\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }

    private object DescribeApi()
        => new
        {
            name = "gbaSharp desktop control server",
            baseUrl = BaseUrl,
            endpoints = new[]
            {
                "GET /status",
                "GET /game/ruby/state",
                "GET /screenshot",
                "GET /screenshot?overlay=movement-grid",
                "GET /screenshot?overlay=center-lens&scale=4&tiles=9",
                "GET /screenshot?overlay=coordinate-lens&scale=4&tiles=9",
                "GET /screenshot?overlay=atlas-lens&atlas=docs/live-atlas/pokemon-ruby.csv&scale=4&tiles=9",
                "GET /screenshot?overlay=atlas-coordinate-lens&atlas=docs/live-atlas/pokemon-ruby.csv&scale=4&tiles=9",
                "POST /input/tap?keys=A&duration=90&delay=120",
                "POST /input/face?key=Up&duration=45&delay=120",
                "POST /input/tile-step?key=Right&duration=170&delay=250",
                "POST /input/walk-tile?key=Right&timeout=900&delay=180",
                "POST /input/sequence?steps=Right:150:120,Up:150:120,A:80&gap=120",
                "POST /input/press?keys=A,Right",
                "POST /input/release?keys=A",
                "POST /input/set?keys=A,Right",
                "POST /input/clear",
                "POST /emulation/run",
                "POST /emulation/pause",
                "POST /emulation/toggle",
                "POST /emulation/reset",
                "POST /emulation/step",
                "POST /app/close"
            }
        };

    private void WriteDiscoveryFile()
    {
        var payload = JsonSerializer.Serialize(new
        {
            processId = Environment.ProcessId,
            port = Port,
            baseUrl = BaseUrl,
            startedUtc = DateTimeOffset.UtcNow
        }, JsonOptions);
        File.WriteAllText(_discoveryPath, payload);
    }

    private void DeleteDiscoveryFile()
    {
        try
        {
            if (File.Exists(_discoveryPath))
            {
                File.Delete(_discoveryPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Query);
}

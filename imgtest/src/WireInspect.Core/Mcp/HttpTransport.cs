using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WireInspect.Mcp;

// MCP Streamable HTTP transport on the loopback interface only. A plain TcpListener keeps it free of
// http.sys URL reservations (no admin rights) and works the same on every platform. Responses are always
// single JSON bodies; the server never opens an SSE stream (GET answers 405, as the spec allows).
public sealed class HttpTransport : IDisposable
{
    public const int DefaultPort = 3918;
    const int MaxHeaderBytes = 64 * 1024;
    const int MaxBodyBytes = 16 * 1024 * 1024;

    readonly McpServer _server;
    readonly List<TcpListener> _listeners = new();
    readonly CancellationTokenSource _cts = new();
    readonly ConcurrentDictionary<string, byte> _sessions = new();
    int _requests;

    public HttpTransport(McpServer server, int port = DefaultPort)
    {
        _server = server;
        Port = port;
    }

    public int Port { get; private set; }
    public string Url => $"http://127.0.0.1:{Port}/mcp";
    public bool Running { get; private set; }
    public int Requests => _requests;
    public int Sessions => _sessions.Count;

    public void Start()
    {
        var v4 = new TcpListener(IPAddress.Loopback, Port);
        try { v4.Start(); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new InvalidOperationException($"端口 {Port} 已被占用（可能已有另一个 WireInspect 实例在提供 MCP 服务）。", ex);
        }
        Port = ((IPEndPoint)v4.LocalEndpoint).Port;
        _listeners.Add(v4);
        if (Socket.OSSupportsIPv6)
        {
            var v6 = new TcpListener(IPAddress.IPv6Loopback, Port);
            try
            {
                v6.Start();
                _listeners.Add(v6);
            }
            catch (SocketException) { }
        }
        Running = true;
        foreach (var l in _listeners)
            _ = AcceptLoop(l);
    }

    public void Dispose()
    {
        Running = false;
        _cts.Cancel();
        foreach (var l in _listeners)
            l.Stop();
        _listeners.Clear();
    }

    async Task AcceptLoop(TcpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { break; }
            _ = Task.Run(() => Serve(client));
        }
    }

    async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var reader = new Reader(client.GetStream());
                while (!_cts.IsCancellationRequested)
                {
                    var req = await reader.ReadRequest(_cts.Token);
                    if (req == null)
                        break;
                    Interlocked.Increment(ref _requests);
                    var resp = Process(req);
                    bool keepAlive = req.KeepAlive && resp.Status < 400;
                    await Write(client.GetStream(), resp, keepAlive, _cts.Token);
                    if (!keepAlive)
                        break;
                }
            }
            catch (BadRequest ex)
            {
                try { await Write(client.GetStream(), new Response(ex.Status, ex.Message), false, CancellationToken.None); }
                catch (IOException) { }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body, bool KeepAlive);

    sealed record Response(int Status, string Body, string ContentType = "text/plain; charset=utf-8", Dictionary<string, string>? Headers = null);

    sealed class BadRequest(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    Response Process(Request req)
    {
        if (req.Headers.TryGetValue("Origin", out var origin) && !LocalOrigin(origin))
            return new Response(403, "Forbidden origin: " + origin);
        if (req.Headers.TryGetValue("Host", out var host) && !LocalOrigin("http://" + host))
            return new Response(403, "Forbidden host: " + host);
        string path = req.Path.Split('?')[0].TrimEnd('/');
        if (path is not ("/mcp" or ""))
            return new Response(404, "Not found. MCP endpoint: " + Url);
        req.Headers.TryGetValue("Mcp-Session-Id", out var session);
        switch (req.Method)
        {
            case "POST":
                break;
            case "DELETE":
                if (session != null)
                    _sessions.TryRemove(session, out _);
                return new Response(200, "");
            default:
                return new Response(405, "Method not allowed", Headers: new() { ["Allow"] = "POST, DELETE" });
        }
        string body = Encoding.UTF8.GetString(req.Body);
        bool initialize = false;
        try { initialize = JsonNode.Parse(body) is JsonObject o && o["method"]?.GetValue<string>() == "initialize"; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        string? reply = _server.Handle(body);
        var headers = new Dictionary<string, string>();
        if (initialize)
        {
            session = Guid.NewGuid().ToString("N");
            _sessions[session] = 0;
            headers["Mcp-Session-Id"] = session;
        }
        return reply == null
            ? new Response(202, "", Headers: headers)
            : new Response(200, reply, "application/json; charset=utf-8", headers);
    }

    static bool LocalOrigin(string origin)
    {
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback;
    }

    static async Task Write(Stream stream, Response r, bool keepAlive, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(r.Body);
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {r.Status} {Reason(r.Status)}\r\n");
        sb.Append($"Content-Type: {r.ContentType}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append(keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        if (r.Headers != null)
            foreach (var (k, v) in r.Headers)
                sb.Append($"{k}: {v}\r\n");
        sb.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    static string Reason(int status) => status switch
    {
        200 => "OK",
        202 => "Accepted",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        413 => "Payload Too Large",
        _ => "Error"
    };

    sealed class Reader(Stream stream)
    {
        byte[] _buf = new byte[8192];
        int _start, _end;

        async Task<bool> Fill(CancellationToken ct)
        {
            if (_start == _end)
                _start = _end = 0;
            if (_end == _buf.Length)
            {
                if (_start > 0)
                {
                    Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }
                else
                    Array.Resize(ref _buf, _buf.Length * 2);
            }
            int n = await stream.ReadAsync(_buf.AsMemory(_end), ct);
            if (n == 0)
                return false;
            _end += n;
            return true;
        }

        async Task<string?> ReadLine(CancellationToken ct)
        {
            while (true)
            {
                int i = Array.IndexOf(_buf, (byte)'\n', _start, _end - _start);
                if (i >= 0)
                {
                    string line = Encoding.ASCII.GetString(_buf, _start, i - _start).TrimEnd('\r');
                    _start = i + 1;
                    return line;
                }
                if (_end - _start > MaxHeaderBytes)
                    throw new BadRequest(400, "Header too large");
                if (!await Fill(ct))
                    return null;
            }
        }

        async Task<byte[]> ReadBytes(int count, CancellationToken ct)
        {
            var result = new byte[count];
            int got = 0;
            while (got < count)
            {
                if (_start == _end && !await Fill(ct))
                    throw new IOException("Connection closed inside the request body");
                int k = Math.Min(count - got, _end - _start);
                Buffer.BlockCopy(_buf, _start, result, got, k);
                _start += k;
                got += k;
            }
            return result;
        }

        public async Task<Request?> ReadRequest(CancellationToken ct)
        {
            string? first;
            do first = await ReadLine(ct);
            while (first is { Length: 0 });
            if (first == null)
                return null;
            var parts = first.Split(' ');
            if (parts.Length < 3)
                throw new BadRequest(400, "Bad request line");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await ReadLine(ct) is { Length: > 0 } line)
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            byte[] body;
            if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                using var ms = new MemoryStream();
                while (true)
                {
                    string size = (await ReadLine(ct) ?? throw new IOException("Connection closed")).Split(';')[0].Trim();
                    int n = Convert.ToInt32(size, 16);
                    if (n == 0)
                    {
                        while (await ReadLine(ct) is { Length: > 0 }) { }
                        break;
                    }
                    if (ms.Length + n > MaxBodyBytes)
                        throw new BadRequest(413, "Body too large");
                    ms.Write(await ReadBytes(n, ct));
                    await ReadLine(ct);
                }
                body = ms.ToArray();
            }
            else
            {
                int length = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var v) ? v : 0;
                if (length is < 0 or > MaxBodyBytes)
                    throw new BadRequest(413, "Body too large");
                body = await ReadBytes(length, ct);
            }
            bool http11 = parts[2].EndsWith("1.1", StringComparison.Ordinal);
            bool keepAlive = headers.TryGetValue("Connection", out var conn)
                ? !conn.Equals("close", StringComparison.OrdinalIgnoreCase)
                : http11;
            return new Request(parts[0].ToUpperInvariant(), parts[1], headers, body, keepAlive);
        }
    }
}

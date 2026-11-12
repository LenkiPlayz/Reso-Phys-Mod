using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Softbody;

/// <summary>
/// Loopback-only JSON stats, same pattern as ClaudeEyes: GET http://127.0.0.1:23801/status
/// (SOFTBODY_PORT overrides the port). Requests whose Host header is not a loopback name are refused
/// (DNS-rebinding guard). Read-only: there is no endpoint that changes anything.
/// </summary>
static class StatsServer
{
    const int DefaultPort = 23801;
    static TcpListener _listener;
    static Func<string> _status;

    public static void Start(Func<string> status)
    {
        _status = status;
        int port = int.TryParse(Environment.GetEnvironmentVariable("SOFTBODY_PORT"), out var p) && p > 0 && p < 65536 ? p : DefaultPort;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            SoftbodyRuntime.Warn($"stats server could not listen on 127.0.0.1:{port}: {ex.Message} (stats still go to the log)");
            _listener = null;
            return;
        }
        Task.Run(AcceptLoop);
        SoftbodyRuntime.Log($"stats on http://127.0.0.1:{port}/status");
    }

    public static void Stop() { try { _listener?.Stop(); } catch { } }

    static async Task AcceptLoop()
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }
            catch (InvalidOperationException) { return; }
            _ = Task.Run(() => Handle(client));
        }
    }

    static async Task Handle(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            stream.ReadTimeout = 5000;
            try
            {
                var buf = new byte[8192];
                int len = 0;
                while (len < buf.Length)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(len, buf.Length - len)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    if (n == 0) break;
                    len += n;
                    if (Encoding.ASCII.GetString(buf, 0, len).Contains("\r\n\r\n")) break;
                }
                var lines = Encoding.ASCII.GetString(buf, 0, len).Split("\r\n");
                var parts = lines[0].Split(' ');
                if (parts.Length < 2 || parts[0] != "GET") { await Respond(stream, 400, "text/plain", "GET only"); return; }
                string host = lines.Skip(1).FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?.Substring(5).Trim();
                if (host != null && !IsLoopbackHost(host)) { await Respond(stream, 403, "text/plain", "forbidden host"); return; }
                var path = parts[1].Split('?')[0];
                if (path == "/status" || path == "/") await Respond(stream, 200, "application/json", _status?.Invoke() ?? "{}");
                else await Respond(stream, 404, "text/plain", "try /status");
            }
            catch (Exception ex)
            {
                try { await Respond(stream, 500, "text/plain", ex.Message); } catch { }
            }
        }
    }

    static bool IsLoopbackHost(string host)
    {
        var h = host.Trim().ToLowerInvariant();
        int colon = h.LastIndexOf(':');
        if (colon > 0 && !h.EndsWith("]")) h = h.Substring(0, colon);
        return h == "127.0.0.1" || h == "localhost" || h == "[::1]";
    }

    static async Task Respond(NetworkStream stream, int code, string type, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        string reason = code switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
        var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
    }

    public static string Json(string s) => s == null ? "null"
        : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\t", " ") + "\"";
}

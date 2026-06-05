using Interstellar.Server.Services;
using Interstellar.Server.VoiceChat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
using WebSocketSharp.Server;

namespace Interstellar.Server;

internal class Server
{
    private readonly HttpServer http;
    private readonly string? turnUrl;
    private readonly string? turnUser;
    private readonly string? turnPass;

    private Server(string url, bool secure, string? certPath, string? password,
                   string? turnUrl, string? turnUser, string? turnPass)
    {
        this.turnUrl = turnUrl;
        this.turnUser = turnUser;
        this.turnPass = turnPass;

        http = new HttpServer(url);

        if (secure && certPath != null)
        {
            http.SslConfiguration.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            http.SslConfiguration.ServerCertificate =
                new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath, password ?? "");
        }

        // --- HTTP handlers ---
        http.OnGet += HandleHttpGet;

        // --- WebSocket /vc endpoint ---
        http.AddWebSocketService<VCClientService>("/vc");

        // Pass TURN config to the service factory so each connection gets it
        VCClientService.GlobalTurnUrl = turnUrl;
        VCClientService.GlobalTurnUser = turnUser;
        VCClientService.GlobalTurnPass = turnPass;

        http.KeepClean = true;
        http.WaitTime = TimeSpan.FromSeconds(30);
    }

    private void HandleHttpGet(object? sender, HttpRequestEventArgs e)
    {
        Console.WriteLine("HTTP request: " + e.Request.Url.AbsolutePath);
        var req = e.Request;
        var res = e.Response;
        var path = req.Url.AbsolutePath;

        string body;
        string contentType = "text/plain; charset=utf-8";
        int status = 200;

        if (path == "/" || path == "/index.html")
        {
            contentType = "text/html; charset=utf-8";
            body = BuildDashboardHtml();
        }
        else if (path == "/health")
        {
            contentType = "application/json; charset=utf-8";
            body = BuildHealthJson();
        }
        else if (path == "/stats")
        {
            contentType = "application/json; charset=utf-8";
            body = BuildStatsJson();
        }
        else if (path == "/vc")
        {
            status = 426; // Upgrade Required
            res.Headers.Add("Upgrade", "websocket");
            body = "This endpoint is WebSocket only. Use ws://.../vc or wss://.../vc";
        }
        else
        {
            status = 404;
            body = "Not Found";
        }

        res.StatusCode = status;
        res.ContentType = contentType;
        res.ContentEncoding = Encoding.UTF8;
        var buf = Encoding.UTF8.GetBytes(body);
        res.ContentLength64 = buf.LongLength;
        using (var os = res.OutputStream) { os.Write(buf, 0, buf.Length); }
    }

    private string BuildDashboardHtml()
    {
        int clientCount = RoomManager.TotalClientCount;
        int roomCount = RoomManager.RoomCount;
        bool coturnEnabled = !string.IsNullOrEmpty(turnUrl);
        string coturnDisplay = coturnEnabled
            ? $"<span style='color:#4f4'>Enabled — {System.Net.WebUtility.HtmlEncode(turnUrl)}</span>"
            : "<span style='color:#f44'>Disabled</span>";
        string secureDisplay = http.SslConfiguration.ServerCertificate != null
            ? "<span style='color:#4f4'>WSS Enabled</span>"
            : "<span style='color:#f84'>WS only (no TLS)</span>";

        return $@"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<title>Interstellar Voice Server</title>
<style>
  * {{ margin:0; padding:0; box-sizing:border-box; }}
  body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
         background: #0d1117; color: #c9d1d9; display:flex; justify-content:center;
         align-items:center; min-height:100vh; }}
  .card {{ background: #161b22; border: 1px solid #30363d; border-radius: 12px;
           padding: 40px; max-width: 520px; width: 90%; text-align: center; }}
  h1 {{ color: #58a6ff; margin-bottom: 8px; font-size: 1.8em; }}
  .sub {{ color: #8b949e; margin-bottom: 28px; font-size: 0.9em; }}
  .stat {{ display: flex; justify-content: space-between; padding: 10px 0;
           border-bottom: 1px solid #21262d; }}
  .stat:last-child {{ border-bottom: none; }}
  .label {{ color: #8b949e; }}
  .value {{ font-weight: 600; font-size: 1.1em; color: #e6edf3; }}
  .big {{ font-size: 2em; color: #58a6ff; }}
  .footer {{ margin-top: 24px; font-size: 0.75em; color: #484f58; }}
  a {{ color: #58a6ff; }}
</style>
</head>
<body>
<div class='card'>
  <h1>Interstellar Voice Server</h1>
  <p class='sub'>Voice Chat Relay — Running</p>

  <div class='stat'>
    <span class='label'>Connected Clients</span>
    <span class='value big'>{clientCount}</span>
  </div>
  <div class='stat'>
    <span class='label'>Active Rooms</span>
    <span class='value'>{roomCount}</span>
  </div>
  <div class='stat'>
    <span class='label'>Coturn / TURN</span>
    <span class='value'>{coturnDisplay}</span>
  </div>
  <div class='stat'>
    <span class='label'>Transport</span>
    <span class='value'>{secureDisplay}</span>
  </div>

  <p class='footer'>
    <a href='/health'>/health</a> &middot;
    <a href='/stats'>/stats</a> &middot;
    WebSocket at <code>/vc</code>
  </p>
</div>
</body>
</html>";
    }

    private string BuildHealthJson()
    {
        return "{\"status\":\"ok\"}";
    }

    private string BuildStatsJson()
    {
        int clientCount = RoomManager.TotalClientCount;
        int roomCount = RoomManager.RoomCount;
        bool coturnEnabled = !string.IsNullOrEmpty(turnUrl);
        bool wssEnabled = http.SslConfiguration.ServerCertificate != null;

        // Manual JSON building to avoid System.Text.Json dependency issues
        return "{"
            + $"\"status\":\"ok\","
            + $"\"clients\":{clientCount},"
            + $"\"rooms\":{roomCount},"
            + $"\"coturn\":{(coturnEnabled ? "true" : "false")},"
            + $"\"coturnUrl\":\"{EscapeJson(turnUrl ?? "")}\","
            + $"\"wss\":{(wssEnabled ? "true" : "false")}"
            + "}";
    }

    private static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // ── Static entry point ──────────────────────────────────────────────
    static public void StartServer(string url, bool secure, string? certPath, string? password,
                                    string? turnUrl = null, string? turnUser = null, string? turnPass = null)
    {
        var server = new Server(url, secure, certPath, password, turnUrl, turnUser, turnPass);
        server.http.Start();

        Console.WriteLine("Interstellar Voice Server is running.");
        Console.WriteLine("  Dashboard: " + url);
        Console.WriteLine("  WebSocket: " + url.Replace("http", "ws") + "/vc");
        if (secure)
            Console.WriteLine("  Secure:    " + url.Replace("http", "wss") + "/vc");

        ManualResetEvent exitEvent = new(false);
        Console.WriteLine("Press Ctrl-C to exit.");
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exitEvent.Set();
        };
        exitEvent.WaitOne();
    }
}

using Interstellar.Server.Services;
using Interstellar.Server.VoiceChat;
using System.Net;
using System.Security.Authentication;
using System.Text;
using WebSocketSharp.Server;

namespace Interstellar.Server;

internal class Server
{
    public static int OptimalPlayerCount { get; private set; }
    public static string ServerUrl { get; private set; } = "";

    static public void StartServer(string url, bool secure, string? certPath, string? password,
                                    string? turnUrl = null, string? turnUser = null, string? turnPass = null,
                                    int optimalPlayers = 0)
    {
        OptimalPlayerCount = optimalPlayers;
        ServerUrl = url;

        // Pass TURN config to the WebSocket service
        VCClientService.GlobalTurnUrl = turnUrl;
        VCClientService.GlobalTurnUser = turnUser;
        VCClientService.GlobalTurnPass = turnPass;

        var http = new HttpServer(url);
        if (secure && certPath != null)
        {
            http.SslConfiguration.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            http.SslConfiguration.ServerCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath, password ?? "");
        }

        http.OnGet += (sender, e) =>
        {
            // HTTP request log: very noisy (dashboard polls every 3s).
            // Uncomment for debugging only.
            // Console.WriteLine("HTTP request: " + e.Request.Url.AbsolutePath);
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
                body = "{\"status\":\"ok\"}";
            }
            else if (path == "/stats")
            {
                contentType = "application/json; charset=utf-8";
                body = BuildStatsJson(turnUrl);
            }
            else if (path == "/api/rooms")
            {
                contentType = "application/json; charset=utf-8";
                body = BuildRoomsJson(turnUrl);
            }
            else if (path == "/vc")
            {
                status = 426;
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
        };

        http.AddWebSocketService<VCClientService>("/vc");
        http.KeepClean = true;
        http.WaitTime = TimeSpan.FromSeconds(30);
        http.Start();

        Console.WriteLine("Interstellar Voice Server is running.");
        Console.WriteLine("  Dashboard: " + url);
        Console.WriteLine("  WebSocket: " + url.Replace("http", "ws") + "/vc");
        if (secure)
            Console.WriteLine("  Secure:    " + url.Replace("http", "wss") + "/vc");
        Console.WriteLine("Press Ctrl-C to exit.");

        ManualResetEvent exitEvent = new(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exitEvent.Set();
        };
        exitEvent.WaitOne();
    }

    private static string BuildDashboardHtml()
    {
        return @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<title>Interstellar Voice Server</title>
<style>
  *,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
  body{
    font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;
    background:#090c10;color:#c9d1d9;min-height:100vh;
    display:flex;flex-direction:column;align-items:center;padding:40px 16px;
  }
  canvas#bg{position:fixed;inset:0;z-index:0;opacity:.4}
  .main{position:relative;z-index:1;width:100%;max-width:600px}
  .hero{text-align:center;margin-bottom:28px}
  .hero .status{display:inline-flex;align-items:center;gap:6px;background:rgba(63,185,80,.12);
    border:1px solid rgba(63,185,80,.25);border-radius:20px;padding:4px 14px;
    font-size:.78em;color:#3fb950;margin-bottom:14px}
  .hero .status .dot{width:7px;height:7px;background:#3fb950;border-radius:50%;animation:pulse 2s infinite}
  @keyframes pulse{0%,100%{opacity:1;box-shadow:0 0 6px #3fb950}50%{opacity:.4;box-shadow:0 0 12px #3fb950}}
  .hero h1{font-size:1.7em;font-weight:700;color:#e6edf3}
  .hero .tagline{color:#7d8590;font-size:.85em;margin-top:4px}
  .big-num{text-align:center;margin-bottom:24px}
  .big-num .num{font-size:4.2em;font-weight:800;line-height:1;
    background:linear-gradient(180deg,#58a6ff 0%,#3fb950 100%);
    -webkit-background-clip:text;-webkit-text-fill-color:transparent;background-clip:text}
  .big-num .label{color:#7d8590;font-size:.82em;margin-top:4px;letter-spacing:.06em;text-transform:uppercase}
  .info{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-bottom:20px}
  .info-card{background:#0f131a;border:1px solid #21262d;border-radius:12px;padding:14px 16px;
    display:flex;justify-content:space-between;align-items:center}
  .info-card .k{color:#7d8590;font-size:.78em;text-transform:uppercase;letter-spacing:.05em}
  .info-card .v{font-weight:600;font-size:1em;font-family:'SF Mono','JetBrains Mono',monospace}
  .info-card .v.on{color:#3fb950}.info-card .v.off{color:#f85149}.info-card .v.warn{color:#d29922}
  .room{border:1px solid #21262d;border-radius:12px;margin-bottom:10px;overflow:hidden}
  .room-h{background:#0f131a;padding:12px 16px;display:flex;justify-content:space-between;align-items:center}
  .room-h .name{color:#c9d1d9;font-weight:500;font-size:.88em}
  .room-h .count{color:#58a6ff;font-size:.82em;font-weight:600}
  .pl{display:flex;justify-content:space-between;align-items:center;padding:7px 16px;font-size:.82em}
  .pl:nth-child(odd){background:rgba(48,54,61,.2)}
  .pl .pname{color:#c9d1d9}
  .pl .pname .pid{color:#484f58;margin-left:6px;font-size:.9em}
  .pl .ptag{color:#7d8590;font-size:.78em}
  .pl .ptag .mute{color:#d29922;font-weight:600}
  .empty{text-align:center;color:#484f58;padding:20px;font-size:.78em}
  .footer{margin-top:20px;text-align:center;font-size:.68em;color:#30363d}
  .footer span{margin:0 6px}.footer a{color:#484f58;text-decoration:none}.footer a:hover{color:#58a6ff}
  .err{color:#f85149;text-align:center;padding:8px;font-size:.78em;display:none}
</style>
</head>
<body>
<canvas id='bg'></canvas>
<div class='main'>
  <div class='hero'>
    <div class='status'><span class='dot'></span>Running</div>
    <h1>Interstellar Voice</h1>
    <p class='tagline'>Voice Chat Relay Server</p>
  </div>
  <div class='big-num'><div class='num' id='clients'>—</div><div class='label'>Online Players</div></div>
  <div class='info'>
    <div class='info-card'><span class='k'>Active Rooms</span><span class='v' id='rooms' style='color:#58a6ff'>—</span></div>
    <div class='info-card'><span class='k'>Optimal Players</span><span class='v' id='optimal'>—</span></div>
    <div class='info-card'><span class='k'>TURN Relay</span><span class='v' id='turn'>—</span></div>
    <div class='info-card'><span class='k'>Transport</span><span class='v' id='transport'>—</span></div>
    <div class='info-card'><span class='k'>WebSocket</span><span class='v' style='color:#58a6ff'>/vc</span></div>
    <div class='info-card'><span class='k'>VC Server URL</span><span class='v' id='vcUrl' style='color:#58a6ff;font-size:.75em'>—</span></div>
  </div>
  <div id='roomList'></div>
  <div class='err' id='err'></div>
  <div class='footer'>
    <a href='/health'>/health</a><span>·</span><a href='/stats'>/stats</a>
  </div>
</div>
<script>
var c=document.getElementById('bg'),C=c.getContext('2d');
function R(){c.width=innerWidth;c.height=innerHeight;var p=[];for(var i=0;i<40;i++)p.push({x:Math.random()*c.width,y:Math.random()*c.height,r:Math.random()*1.5+.3,vx:(Math.random()-.5)*.3,vy:(Math.random()-.5)*.3});function draw(){C.clearRect(0,0,c.width,c.height);for(var i=0;i<p.length;i++){var o=p[i];C.beginPath();C.arc(o.x,o.y,o.r,0,Math.PI*2);C.fillStyle='rgba(88,166,255,.25)';C.fill();for(var j=i+1;j<p.length;j++){var d=Math.hypot(o.x-p[j].x,o.y-p[j].y);if(d<100){C.beginPath();C.moveTo(o.x,o.y);C.lineTo(p[j].x,p[j].y);C.strokeStyle='rgba(88,166,255,'+(.08*(1-d/100))+')';C.lineWidth=.4;C.stroke()}}o.x+=o.vx;o.y+=o.vy;if(o.x<0||o.x>c.width)o.vx*=-1;if(o.y<0||o.y>c.height)o.vy*=-1}requestAnimationFrame(draw)}draw()}
R();addEventListener('resize',R);
async function load(){
  try{
    let r=await fetch('/api/rooms');
    if(!r.ok) throw new Error(r.status);
    let d=await r.json();
    document.getElementById('clients').textContent=d.clients;
    document.getElementById('rooms').textContent=d.rooms;
    document.getElementById('optimal').textContent=d.optimalPlayers>0?d.optimalPlayers+' (ideal)':'—';
    document.getElementById('vcUrl').textContent=d.serverUrl||'—';
    let t=document.getElementById('turn');
    t.textContent=d.coturn?'ON':'OFF';
    t.className='v '+(d.coturn?'on':'off');
    document.getElementById('transport').textContent=d.wss?'WSS':'HTTP';
    document.getElementById('transport').className='v '+(d.wss?'on':'warn');
    document.getElementById('err').style.display='none';
    let html='';
    if(d.roomList&&d.roomList.length){
      for(let rm of d.roomList){
        html+='<div class=room><div class=room-h><span class=name>'+esc(rm.region)+' / '+esc(rm.roomId)+'</span><span class=count>'+rm.clients+' clients</span></div><div class=room-b>';
        if(rm.players&&rm.players.length){
          for(let p of rm.players){
            let mt=p.muted?' <span class=mute>[MUTED]</span>':'';
            html+='<div class=pl><span class=pname>'+esc(p.name||'?')+'<span class=pid>#'+p.pid+'</span></span><span class=ptag>Voice #'+p.vid+mt+'</span></div>';
          }
        }else{html+='<div class=empty>Waiting for players…</div>'}
        html+='</div></div>';
      }
    }else{html='<div class=empty>No active rooms</div>'}
    document.getElementById('roomList').innerHTML=html;
  }catch(e){
    document.getElementById('err').style.display='block';
    document.getElementById('err').textContent='Load failed: '+e.message;
  }
}
function esc(s){var d={'&':'&amp;','<':'&lt;','>':'&gt;','\x22':'&quot;'};return s.replace(/[&<>\x22]/g,c=>d[c])}
load();setInterval(load,3000);
</script>
</body>
</html>";
    }

    private static string BuildStatsJson(string? turnUrl)
    {
        int clientCount = RoomManager.TotalClientCount;
        int roomCount = RoomManager.RoomCount;
        bool coturnEnabled = !string.IsNullOrEmpty(turnUrl);
        return "{"
            + $"\"status\":\"ok\","
            + $"\"clients\":{clientCount},"
            + $"\"rooms\":{roomCount},"
            + $"\"optimalPlayers\":{OptimalPlayerCount},"
            + $"\"serverUrl\":\"{EscJson(ServerUrl)}\","
            + $"\"coturn\":{(coturnEnabled ? "true" : "false")},"
            + $"\"coturnUrl\":\"{EscJson(turnUrl ?? "")}\","
            + $"\"wss\":false"
            + "}";
    }

    private static string BuildRoomsJson(string? turnUrl)
    {
        int clientCount = RoomManager.TotalClientCount;
        int roomCount = RoomManager.RoomCount;
        bool coturnEnabled = !string.IsNullOrEmpty(turnUrl);
        var snapshots = RoomManager.GetRoomSnapshots();

        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append($"\"clients\":{clientCount},");
        sb.Append($"\"rooms\":{roomCount},");
        sb.Append($"\"optimalPlayers\":{OptimalPlayerCount},");
        sb.Append($"\"serverUrl\":\"{EscJson(ServerUrl)}\",");
        sb.Append($"\"coturn\":{(coturnEnabled ? "true" : "false")},");
        sb.Append("\"wss\":false,");
        sb.Append("\"roomList\":[");
        for (int i = 0; i < snapshots.Count; i++)
        {
            var r = snapshots[i];
            if (i > 0) sb.Append(',');
            sb.Append('{');
            sb.Append($"\"region\":\"{EscJson(r.Region)}\",");
            sb.Append($"\"roomId\":\"{EscJson(r.RoomId)}\",");
            sb.Append($"\"clients\":{r.ClientCount},");
            sb.Append("\"players\":[");
            for (int j = 0; j < r.Clients.Length; j++)
            {
                var c = r.Clients[j];
                if (j > 0) sb.Append(',');
                sb.Append('{');
                sb.Append($"\"vid\":{c.VoiceId},");
                sb.Append($"\"pid\":{(c.PlayerId.HasValue ? c.PlayerId.Value.ToString() : "null")},");
                sb.Append($"\"name\":\"{EscJson(c.PlayerName ?? "?")}\",");
                sb.Append($"\"muted\":{(c.IsMuted ? "true" : "false")}");
                sb.Append('}');
            }
            sb.Append(']');
            sb.Append('}');
        }
        sb.Append(']');
        sb.Append('}');
        return sb.ToString();
    }

    private static string EscJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

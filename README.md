# Interstellar Voice Chat

Real-time proximity voice chat for Among Us. Single plugin DLL + standalone server.

## Project Structure

```
AmongUs-VoiceChat/
├── Interstellar.sln
├── Interstellar.Client/       # BepInEx plugin — audio engine + game integration
│   ├── Network/               #   WebRTC connection
│   ├── Routing/               #   Audio routing graph (mixer, filters, panner)
│   ├── VoiceChat/             #   Mic, Speaker, VCRoom
│   ├── Game/                  #   Among Us integration (HUD, config, patches)
│   ├── Android/               #   Android mic/speaker via Starlight
│   ├── NAudio/                #   Audio buffer providers
│   ├── Resources/             #   Sprites
│   └── InterstellarPlugin.cs  #   Entry point
├── Interstellar.Messages/     # Shared WebSocket / WebRTC message protocol
├── Interstellar.Server/       # Voice relay server (WebSocket + WebRTC)
├── docker-compose.yml         # Docker: Server + Coturn
├── Dockerfile
├── nuget.config
├── turnserver.conf
└── .github/workflows/build.yml
```

## Quick Start

### Prerequisites

- .NET 8 SDK (Server)
- .NET 6 SDK (Client plugin)
- 7-Zip (Windows — dependency packaging)

### Build

```bash
# Plugin (two-pass: first compiles, second embeds dependencies)
dotnet build Interstellar.Client/Interstellar.Client.csproj -c Release
dotnet build Interstellar.Client/Interstellar.Client.csproj -c Release

# Server
dotnet publish Interstellar.Server/Interstellar.Server.csproj -c Release -o ./server
```

### Install

Copy `Interstellar.Client.dll` and its dependencies from `bin/Release/net6.0/` into `BepInEx/plugins/`.

## Server

### Run

```bash
# Basic
dotnet run --project Interstellar.Server 0.0.0.0:22021

# With Coturn
dotnet run --project Interstellar.Server 0.0.0.0:22021 \
  --coturn turn:your-server.com:3478 \
  --coturn-user interstellar --coturn-pass yourpassword

# With WSS (TLS)
dotnet run --project Interstellar.Server 0.0.0.0:22021 \
  -s /path/to/cert.pfx -p password
```

### Dashboard

Visit `http://your-server:22021/` — shows connected clients, active rooms, Coturn status.

| Endpoint | Response |
|----------|----------|
| `GET /` | HTML dashboard |
| `GET /health` | `{"status":"ok"}` |
| `GET /stats` | `{"status":"ok","clients":5,"rooms":2,"coturn":true,"wss":false}` |

### CLI Reference

```
Interstellar.Server <bind_address> [options]

  -s, --secure <path>     WSS certificate (.pfx)
  -p, --password <pwd>    Certificate password
  -t, --coturn <url>      Coturn TURN URL
  --coturn-user <user>    TURN username
  --coturn-pass <pass>    TURN password
```

## Docker

```bash
# Edit credentials
export COTURN_URL=turn:your-server.com:3478
export COTURN_USER=interstellar
export COTURN_PASS=your_secure_password

# Edit turnserver.conf user= line, then:
docker compose up -d
```

| Port | Protocol | Service |
|------|----------|---------|
| 22021 | TCP | Interstellar WebSocket |
| 3478 | TCP+UDP | Coturn STUN/TURN |
| 5349 | TCP+UDP | Coturn TURN TLS |
| 49152-49252 | UDP | Coturn relay |

## Voice Server Matching

The plugin automatically maps the current Among Us game server to a voice server URL by fetching `https://api.amongusclub.cn/Interstellar/ServerList.json` at startup.

**API format:**
```json
{
  "servers": [
    { "name": "Modded Asia (MAS)", "address": "au-as.duikbo.at", "port": 443, "vc": "ws://47.122.116.50:22021" },
    { "name": "Modded NA (MNA)",  "address": "www.aumods.us",    "port": 443, "vc": "ws://vc-na.amongusclub.cn:22021" }
  ]
}
```

- `name` — Among Us server region name (exact match, case-insensitive)
- `vc` — WebSocket URL of the voice server for that region
- If no match, falls back to `ws://47.122.116.50:22021`
- Set `ServerAddress` in plugin config to override

## Plugin Features

| Key | Function |
|-----|----------|
| `M` | Cycle mic: Global → Impostor Radio → Muted |
| `N` | Toggle speaker on/off |

**Audio effects:** spatial panning, distance attenuation, wall occlusion, ghost reverb, impostor radio distortion.

## Plugin Config

`BepInEx/config/com.voicechatplugin.cn.cfg`:

```ini
[VoiceChat]
MicrophoneDevice =
SpeakerDevice =
ServerAddress =             # Override VC server URL (blank = auto-match)
MasterVolume = 1.0
MicVolume = 1.0
```

## CI

GitHub Actions builds on push:
- **Server** — single-file self-contained binaries for `linux-x64`, `linux-arm64`, `win-x64`, `osx-x64`
- **Client** — plugin DLL
- On tag push → draft GitHub Release with all artifacts

## Credits

- [Interstellar](https://github.com/Dolly1016/Interstellar) — voice server framework by Dolly
- [Nebula on the Ship](https://github.com/Dolly1016/Nebula) — architecture reference
- AOU Team — Starlight Android Audio API
- [NAudio](https://github.com/naudio/NAudio) — .NET audio library
- [SIPSorcery](https://github.com/sipsorcery/sipsorcery) — .NET WebRTC library
- [Coturn](https://github.com/coturn/coturn) — TURN/STUN server

## License

MIT

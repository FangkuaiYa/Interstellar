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
│   ├── Game/                  #   Among Us integration (HUD, config, settings UI, popups)
│   ├── Patches/               #   Harmony patches (splash screens, join overlay)
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
# Basic (with optimal player capacity warning)
dotnet run --project Interstellar.Server 0.0.0.0:22021 --optimal-players 12

# With Coturn
dotnet run --project Interstellar.Server 0.0.0.0:22021 \
  --optimal-players 12 \
  --coturn turn:your-server.com:3478 \
  --coturn-user interstellar --coturn-pass yourpassword

# With WSS (TLS)
dotnet run --project Interstellar.Server 0.0.0.0:22021 \
  -s /path/to/cert.pfx -p password
```

### Dashboard

Visit `http://your-server:22021/` — shows connected clients, active rooms, optimal player count, VC server URL, and Coturn status.

| Endpoint | Response |
|----------|----------|
| `GET /` | HTML dashboard (optimal players, rooms, clients, VC URL) |
| `GET /health` | `{"status":"ok"}` |
| `GET /stats` | `{"status":"ok","clients":5,"rooms":2,"optimalPlayers":12,"serverUrl":"http://...","coturn":true,"wss":false}` |
| `GET /api/rooms` | Full room list with player details + `optimalPlayers` / `serverUrl` |

### CLI Reference

```
Interstellar.Server <bind_address> [options]

  -s, --secure <path>         WSS certificate (.pfx)
  -p, --password <pwd>        Certificate password
  -t, --coturn <url>          Coturn TURN URL
  --coturn-user <user>        TURN username
  --coturn-pass <pass>        TURN password
  -op, --optimal-players <n>  Optimal player count (triggers capacity warning on clients)
```

### Player Capacity

When `--optimal-players` is set, the server sends player counts to all connected clients. If the total online count reaches the optimal value, a **full-screen popup** appears on each client:

- Shows current voice server address, optimal count, and current count
- Suggests switching servers or sponsoring a server upgrade
- The server dashboard also displays the optimal player count

## Voice Server Matching

The plugin maps the current Among Us game server to a voice server URL. Server discovery has three layers:

### 1. API Fetch (default on)

Fetches `https://api.amongusclub.cn/Interstellar/ServerList.json` at startup.

**API format:**
```json
{
  "servers": [
    { "name": "Modded Asia (MAS)", "address": "au-as.duikbo.at", "port": 443, "vc": "ws://47.122.116.50:22021", "vcLocation": "Hong Kong" },
    { "name": "Modded NA (MNA)",  "address": "www.aumods.us",    "port": 443, "vc": "ws://vc-na.amongusclub.cn:22021", "vcLocation": "Silicon Valley" }
  ]
}
```
- `vcLocation` — Human-readable server location, displayed in HUD instead of IP

### 2. Custom Server List (always active)

Define your own servers in the plugin config under `[VoiceChat.Server]` → `CustomServerListJson`. Same JSON format as the API. Custom servers **override** API servers with the same name. If API is disabled, only your custom list is used.

### 3. Force Voice Server

Set `ForceVoiceServerEnabled = true` and `ForceVoiceServerUrl` to make **all** Among Us servers use a single voice server — regardless of region.

### Priority

| Layer | Description |
|-------|-------------|
| `ForceVoiceServer` | Overrides everything — all regions share one VC server |
| `CustomServerListJson` | Overrides API entries with same name |
| API | Used for any server not in your custom list |
| Fallback | `ws://47.122.116.50:22021` if nothing matches |

- `name` — Among Us server region name (exact match, case-insensitive)
- `vc` — WebSocket URL of the voice server for that region (optional; falls back to default)
- If a server entry has no `vc` field, the default fallback is used

## Plugin Features

### Keyboard Shortcuts

| Key | Function |
|-----|----------|
| `F1` | Toggle VC settings window (anywhere) |
| `M` | Cycle mic: Global → Impostor Radio → Muted |
| `N` | Toggle speaker on/off |

### Settings UI

Press `F1` or click the **VC** button in the game's Options menu to open the settings window. Two sections:

- **Personal** — Microphone device, speaker device, mic volume, master volume (device selection hidden on Android)
- **Room** (host only) — Max chat distance, Walls Block Sound, Only Hear In Sight, Impostor Hear Ghosts, Only Ghosts Can Talk, Hear In Vent, Vent Private Chat, Comms Sabotage Mutes, Camera Can Hear, Impostor Private Radio, Only Meeting / Lobby

Non-host players see room settings grayed out. All changes are saved to the BepInEx config file.

### Join Splash Screen

When joining a game room, a fade-in overlay displays:
- Interstellar Voice Chat
- Current voice server WebSocket address
- Optimal player count and current online player count

### Capacity Popup

When the server's total online players reaches the optimal count, a full-screen popup warns players and suggests switching servers or sponsoring upgrades.

**Audio effects:** spatial panning, distance attenuation, wall occlusion, ghost reverb, impostor radio distortion.

## Plugin Config

`BepInEx/config/com.voicechatplugin.cn.cfg`:

```ini
[VoiceChat]
# Personal audio settings
MicrophoneDevice =
SpeakerDevice =
ServerAddress =             # Override VC server URL (blank = auto-match)
MasterVolume = 1.0
MicVolume = 1.0

[VoiceChat.Server]
# Fetch server list from API (disable to use only your custom list)
UseApiServerList = true

# Custom server list in JSON (same format as API). One-line JSON.
# Custom entries override API entries with the same name.
# Example: {"servers":[{"name":"My Server","address":"my.com","port":443,"vc":"ws://my.com:22021"}]}
# Server entries without "vc" use the default fallback VC server.
CustomServerListJson =

# Force all Among Us servers to use a single voice server
ForceVoiceServerEnabled = false
# Voice server WebSocket URL when force is enabled
ForceVoiceServerUrl =

[VoiceChat.Room]
# Host-only room settings (synced to all players via voice server)
MaxChatDistance = 6
WallsBlockSound = true
OnlyHearInSight = false
ImpostorHearGhosts = false
OnlyGhostsCanTalk = false
HearInVent = true
VentPrivateChat = false
CommsSabDisables = true
CameraCanHear = true
ImpostorPrivateRadio = false
OnlyMeetingOrLobby = false
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

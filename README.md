# Interstellar Voice Chat

Real-time proximity voice chat for Among Us. A single BepInEx plugin DLL plus a standalone relay server.

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

## Build

**Prerequisites:** .NET 8 SDK (server), .NET 6 SDK (plugin), 7-Zip (Windows — dependency packaging)

```bash
# Plugin (two-pass: first compile, second embeds dependencies)
dotnet build Interstellar.Client/Interstellar.Client.csproj -c Release
dotnet build Interstellar.Client/Interstellar.Client.csproj -c Release

# Server
dotnet publish Interstellar.Server/Interstellar.Server.csproj -c Release -o ./server
```

**Install:** Copy `Interstellar.Client.dll` and its dependencies from `bin/Release/net6.0/` into `BepInEx/plugins/`.

## Server

### Running

```bash
# Basic
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

### CLI Reference

```
Interstellar.Server <bind_address> [options]

  -s, --secure <path>          WSS certificate (.pfx)
  -p, --password <pwd>         Certificate password
  -t, --coturn <url>           Coturn TURN URL
  --coturn-user <user>         TURN username
  --coturn-pass <pass>         TURN password
  -op, --optimal-players <n>   Optimal player count (triggers capacity warning)
```

### Dashboard

Visit `http://your-server:22021/`.

| Endpoint | Response |
|----------|----------|
| `GET /` | HTML dashboard (optimal players, rooms, clients, VC URL, Coturn status) |
| `GET /health` | `{"status":"ok"}` |
| `GET /stats` | `{"status":"ok","clients":5,"rooms":2,"optimalPlayers":12,"serverUrl":"http://...","coturn":true,"wss":false}` |
| `GET /api/rooms` | Full room list with player details, `optimalPlayers`, and `serverUrl` |

### Player Capacity

When `--optimal-players` is set, the server broadcasts player counts to all connected clients. If the total online count reaches the optimal value, a full-screen popup warns players and suggests switching servers or sponsoring an upgrade.

## Voice Server Matching

The plugin resolves each Among Us region to a voice server URL through three layers, applied in priority order:

| Priority | Source | Behavior |
|----------|--------|----------|
| 1 | `ForceVoiceServer` | Overrides everything — all regions use a single VC server |
| 2 | `CustomServerListJson` | Overrides API entries with the same `name` (case-insensitive) |
| 3 | API | Fetched from `https://api.amongusclub.cn/Interstellar/ServerList.json` at startup |
| Fallback | `ws://47.122.116.50:22021` | Used when no match is found |

**API and custom server format:**

```json
{
  "servers": [
    {
      "name": "Modded Asia (MAS)",
      "address": "au-as.duikbo.at",
      "port": 443,
      "vc": "ws://47.122.116.50:22021",
      "vcLocation": "Hong Kong"
    }
  ]
}
```

- `name` — Among Us region name (exact, case-insensitive match)
- `vc` — WebSocket URL of the voice server for that region (optional; falls back to default)
- `vcLocation` — Human-readable label shown in the HUD instead of the IP
- Custom servers override API entries with the same `name`. If the API is disabled, only the custom list is used.

## Plugin Features

### Keyboard Shortcuts

| Key | Function |
|-----|----------|
| `F1` | Toggle VC settings window |
| `M` | Cycle mic mode: Global → Impostor Radio → Muted |
| `N` | Toggle speaker on/off |

### Settings UI

Press `F1` or click the **VC** button in the game's Options menu to open the settings window. It has two sections:

- **Personal** — Microphone device, speaker device, mic volume, master volume (device selection hidden on Android)
- **Room** (host only) — Max chat distance, Walls Block Sound, Only Hear In Sight, Impostor Hear Ghosts, Only Ghosts Can Talk, Hear In Vent, Vent Private Chat, Comms Sabotage Mutes, Camera Can Hear, Impostor Private Radio, Only Meeting / Lobby

Non-host players see room settings grayed out. All changes are persisted to the BepInEx config file.

### Join Overlay & Capacity Popup

On joining a game, a fade-in overlay shows the voice server address and current player count. When the server hits its optimal capacity, a full-screen popup warns players.

## Plugin Config

`BepInEx/config/com.voicechatplugin.cn.cfg`:

```ini
[VoiceChat]
MicrophoneDevice =
SpeakerDevice =
ServerAddress =             # Override VC server (blank = auto-match)
MasterVolume = 1.0
MicVolume = 1.0

[VoiceChat.Server]
UseApiServerList = true
CustomServerListJson =      # One-line JSON; overrides API entries with the same name
ForceVoiceServerEnabled = false
ForceVoiceServerUrl =       # VC WebSocket URL when force is enabled

[VoiceChat.Room]
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
- Tag pushes trigger a draft GitHub Release with all artifacts.

## Credits

- [Interstellar](https://github.com/Dolly1016/Interstellar) — voice server framework by Dolly
- [Nebula on the Ship](https://github.com/Dolly1016/Nebula) — architecture reference
- AOU Team — Starlight Android Audio API
- [NAudio](https://github.com/naudio/NAudio) — .NET audio library
- [SIPSorcery](https://github.com/sipsorcery/sipsorcery) — .NET WebRTC library
- [Coturn](https://github.com/coturn/coturn) — TURN/STUN server

## License

MIT

# Interstellar Voice Chat

A complete real-time proximity voice chat system for Among Us, consisting of:

- **Interstellar.Client** — Among Us BepInEx plugin (merged: Interstellar audio engine + VoiceChat plugin)
- **Interstellar.Messages** — Shared WebSocket message protocol
- **Interstellar.Server** — WebSocket + WebRTC voice relay server with Coturn TURN support

## Architecture

```
Game (Among Us)                    Server
┌──────────────────┐              ┌─────────────────────────┐
│ VoiceChatPlugin  │   WebSocket  │  Interstellar.Server    │
│ (BepInEx plugin) │◄────────────►│  ┌───────────────────┐  │
│                  │   + WebRTC   │  │ VCClientService   │  │
│  Interstellar    │   (Opus)     │  │ RoomManager       │  │
│  Audio Engine    │              │  │ VCRoom / VCClient │  │
│  (merged in)     │              │  └───────────────────┘  │
└──────────────────┘              │         │               │
                                  │    ┌────▼────┐          │
                                  │    │ Coturn  │ (optional)
                                  │    │ TURN    │          │
                                  │    └─────────┘          │
                                  └─────────────────────────┘
```

## Project Structure

```
Interstellar-master/
├── Interstellar.sln
├── Interstellar.Client/       # Among Us BepInEx plugin (merged)
│   ├── Interstellar/          #   Audio engine: routing, WebRTC, mic/speaker
│   ├── VoiceChatPlugin/       #   Plugin: HUD, options, RPC, patches
│   └── Resources/             #   Sprites + locale files
├── Interstellar.Messages/     # Shared WebSocket message protocol
├── Interstellar.Server/       # Voice relay server
├── docker-compose.yml         # Docker deployment (Server + Coturn)
├── Dockerfile                 # Server Docker image
├── turnserver.conf            # Coturn configuration
└── README.md
```

## Quick Start

### Prerequisites

- .NET 8 SDK (for Server)
- .NET 6 SDK (for Client plugin)
- 7-Zip (for dependency packaging on Windows)

### Build

```bash
# Build everything
dotnet build Interstellar.sln -c Release

# Build the plugin DLL only (for Among Us)
# First build — compiles code
dotnet build Interstellar.Client/Interstellar.Client.csproj -c Release
# Second build — packages dependencies into Libs.zip and embeds it
dotnet build Interstellar.Client/Interstellar.Client.csproj -c Release

# Build the server
dotnet publish Interstellar.Server/Interstellar.Server.csproj -c Release -o ./server
```

### Install the Plugin

1. Build `Interstellar.Client` twice (see above)
2. Copy `Interstellar.Client/bin/Release/net6.0/VoiceChatPlugin.dll` to `BepInEx/plugins/`
3. Launch Among Us — the plugin loads automatically

### Run the Server

```bash
# Basic (WebSocket only)
dotnet run --project Interstellar.Server 0.0.0.0:22021

# With Coturn TURN server
dotnet run --project Interstellar.Server 0.0.0.0:22021 \
  --coturn turn:your-server.com:3478 \
  --coturn-user interstellar \
  --coturn-pass yourpassword

# With WSS (TLS encryption)
dotnet run --project Interstellar.Server 0.0.0.0:22021 \
  -s /path/to/certificate.pfx \
  -p certificate_password

# Combined: WSS + Coturn
dotnet run --project Interstellar.Server 0.0.0.0:22021 \
  -s /path/to/certificate.pfx -p certpass \
  --coturn turn:your-server.com:3478 \
  --coturn-user interstellar \
  --coturn-pass yourpassword
```

## Server Dashboard

When the server is running, visit `http://your-server:22021/` to see:

- **Connected Clients** — Number of players currently in voice rooms
- **Active Rooms** — Number of rooms being served
- **Coturn Status** — Whether TURN relay is enabled
- **Transport** — WS or WSS

API endpoints:
- `GET /health` — `{"status":"ok"}`
- `GET /stats` — `{"status":"ok","clients":5,"rooms":2,"coturn":true,"coturnUrl":"turn:...","wss":false}`

## Docker Deployment

### 1. Configure credentials

Edit `.env` or set environment variables:

```bash
export COTURN_URL=turn:your-server.com:3478
export COTURN_USER=interstellar
export COTURN_PASS=your_secure_password
```

### 2. Edit turnserver.conf

Update the `user=` line in `turnserver.conf` to match your credentials.

### 3. Start services

```bash
docker compose up -d
```

This starts both Coturn and the Interstellar server. The server dashboard is available at `http://your-server:22021/`.

### Firewall / Ports

| Port | Protocol | Service |
|------|----------|---------|
| 22021 | TCP | Interstellar WebSocket |
| 3478 | TCP+UDP | Coturn STUN/TURN |
| 5349 | TCP+UDP | Coturn TURN TLS |
| 49152-49252 | UDP | Coturn relay |

## WSS (WebSocket Secure) Setup

### Option A: Self-signed certificate (testing)

```bash
# Generate a self-signed PFX certificate
openssl req -x509 -newkey rsa:4096 -keyout key.pem -out cert.pem -days 365 -nodes
openssl pkcs12 -export -out cert.pfx -inkey key.pem -in cert.pem

# Run server with the certificate
dotnet run --project Interstellar.Server 0.0.0.0:22021 -s cert.pfx
```

### Option B: Let's Encrypt (production)

```bash
# Get a certificate
certbot certonly --standalone -d your-server.com

# Convert to PFX
openssl pkcs12 -export -out cert.pfx \
  -inkey /etc/letsencrypt/live/your-server.com/privkey.pem \
  -in /etc/letsencrypt/live/your-server.com/fullchain.pem

# Run server with WSS
dotnet run --project Interstellar.Server 0.0.0.0:22021 -s cert.pfx
```

### Client Configuration

After enabling WSS on the server, set the plugin config to use `wss://`:

```ini
# BepInEx/config/com.voicechatplugin.cn.cfg
[VoiceChat]
ServerAddress = wss://your-server.com:22021
```

If you leave `ServerAddress` empty, the plugin defaults to the official server.

## Coturn TURN Server

Coturn provides NAT traversal for clients behind restrictive firewalls or symmetric NATs. Without Coturn, clients who cannot establish direct connections will be unable to use voice chat.

### How it works

1. The Interstellar server configures WebRTC with Coturn as a TURN server
2. When a client cannot connect directly, WebRTC falls back to relaying through Coturn
3. Coturn relays encrypted audio packets between the client and the Interstellar server

### Enable Coturn on the server

```bash
dotnet run --project Interstellar.Server 0.0.0.0:22021 \
  --coturn turn:coturn.your-server.com:3478 \
  --coturn-user interstellar \
  --coturn-pass yourpassword
```

Or in Docker Compose, set the environment variables in `.env`.

## Plugin Features

### In-Game Controls

| Key | Function |
|-----|----------|
| `M` | Cycle microphone (Global → Impostor Radio → Muted) |
| `N` | Toggle speaker on/off |

### Host Room Settings (11 rules)

Configured by the host in the lobby under "Voice Chat":

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| Max Chat Distance | Float | 6.0 | Maximum hearing distance (1.5–20.0) |
| Walls Block Sound | Bool | On | Walls attenuate or block voice |
| Only Hear In Sight | Bool | Off | Only hear players in line of sight |
| Impostor Hear Ghosts | Bool | Off | Impostors can hear dead players |
| Only Ghosts Can Talk | Bool | Off | Only dead players can speak |
| Hear In Vent | Bool | On | Players in vents can hear outside |
| Vent Private Chat | Bool | Off | Vent occupants only talk to each other |
| Comms Sabotage Disables | Bool | On | Comms sabotage disables voice |
| Camera Can Hear | Bool | On | Hear while using security cameras |
| Impostor Private Radio | Bool | Off | Impostors have a private radio channel |
| Only Meeting/Lobby | Bool | Off | Only chat during meetings or lobby |

### Proximity Audio Effects

- **Spatial panning** — Sound direction based on speaker position
- **Distance attenuation** — Volume decreases with distance
- **Wall occlusion** — Walls block/reduce sound smoothly
- **Ghost reverb** — Dead players hear spatial reverb effects
- **Radio distortion** — Impostor radio uses band-pass filter + distortion
- **Meeting indicators** — Speaking players glow on vote cards
- **Speaking bar** — HUD shows who is currently talking

## Plugin Configuration

After first run, a config file is generated at `BepInEx/config/com.voicechatplugin.cn.cfg`:

```ini
[VoiceChat]
MicrophoneDevice =          # Mic device name (blank = system default)
SpeakerDevice =             # Speaker device name (blank = system default)
ServerAddress =             # Custom server URL (blank = official server)
                            #   ws://your-server:22021
                            #   wss://your-server:22021 (with TLS)
MasterVolume = 1.0          # Master output volume (0.1–2.0)
MicVolume = 1.0             # Mic input volume (0.1–2.0)

[VoiceChat.Room]
MaxChatDistance = 6.0
WallsBlockSound = true
# ... (all 11 room settings)
```

## Server CLI Reference

```
Interstellar.Server <bind_address> [options]

Arguments:
  <bind_address>    Address to bind (e.g., 0.0.0.0:22021)

Options:
  -s, --secure <path>     Enable WSS with TLS certificate (.pfx)
  -p, --password <pwd>    Certificate password
  -t, --coturn <url>      Coturn TURN server URL
  --coturn-user <user>    TURN server username
  --coturn-pass <pass>    TURN server password
```

## Building from Source

### Build Order

```
1. Interstellar.Messages   (shared protocol library)
2. Interstellar.Client     (Among Us plugin — depends on Messages)
3. Interstellar.Server     (server binary — depends on Messages)
```

## License

MIT License.

## Credits

- **Interstellar** — Voice chat server framework by [Dolly1016](https://github.com/Dolly1016)
- **VoiceChatPlugin** — Among Us plugin by [FangkuaiYa](https://github.com/FangkuaiYa)
- **Nebula on the Ship** — Plugin architecture reference
- **AOU Team** — Starlight Android Audio API
- **NAudio** — .NET audio library by [Mark Heath](https://github.com/naudio/NAudio)
- **SIPSorcery** — .NET WebRTC/SIP library
- **Coturn** — TURN/STUN server

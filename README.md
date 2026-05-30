# Voice Chat Plugin

A BepInEx plugin providing real-time proximity voice chat for Among Us, powered by the Interstellar voice server.

## Features

- **Proximity Voice Chat** — Voice chat based on player distance in-game, with 3D spatial audio (pan + volume attenuation)
- **Dedicated Voice Server** — Audio goes through Interstellar WebSocket + WebRTC, not the Among Us game server, ensuring low latency and no impact on gameplay network
- **Self-Contained** — All dependencies (Interstellar, NAudio, SIPSorcery, etc.) are embedded as resources — no external plugin installation required
- **HUD Buttons** — Mic/speaker toggle buttons in the top-right corner with hover tooltips
- **Speaking Indicator Bar** — Shows currently speaking players' avatars and names at the top of the screen
- **Meeting Speaker Highlight** — Speaking players glow on the meeting vote cards
- **Multi-Channel Mic** — Cycle between Global, Impostor Private Radio, and Muted
- **Host Room Settings** — 11 voice rules configurable by the host, auto-synced to all clients via RPC
- **Chinese Localization** — Native Simplified/Traditional Chinese support, switches with game language

## Installation

Download from [Releases](https://github.com/FangkuaiYa/AmongUs-VoiceChat/releases).

### Option 1: ZIP (recommended)

1. Download the ZIP file for your platform (Steam or Epic)
2. Extract all files into the game root directory
3. Launch the game

### Option 2: DLL only

1. Install [BepInEx](https://github.com/BepInEx/BepInEx) for Among Us
2. Place `VoiceChatPlugin.dll` into `BepInEx/plugins/`
3. v1.0.0+ also requires [Reactor](https://github.com/NuclearPowered/Reactor/releases)

## In-Game Controls

| Key | Function |
|-----|----------|
| `M` | Cycle microphone (Global → Impostor Radio → Muted) |
| `N` | Toggle speaker (On / Off) |

### Microphone States

- **Global** — Everyone nearby can hear you
- **Impostor Private Radio** — Only fellow impostors can hear you (skipped if you are a crewmate)
- **Muted** — You are silenced

## Host Room Settings

Hosts can configure these in the lobby under the "Voice Chat" category:

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| Max Chat Distance | Float | 6.0 | Maximum hearing distance (1.5–20.0) |
| Walls Block Sound | Bool | On | Walls block voice propagation |
| Only Hear In Sight | Bool | Off | Only hear players in the same room or line of sight |
| Impostor Hear Ghosts | Bool | Off | Impostors can hear dead players |
| Only Ghosts Can Talk | Bool | Off | Only dead players can speak |
| Hear In Vent | Bool | On | Players in vents can hear outside |
| Vent Private Chat | Bool | Off | Players in vents can only talk to others also in vents |
| Comms Sabotage Disables | Bool | On | Comms sabotage disables voice chat |
| Camera Can Hear | Bool | On | Hear while using security cameras / door logs |
| Impostor Private Radio | Bool | Off | Impostors have a private radio channel |
| Only Meeting/Lobby | Bool | Off | Only chat during meetings or in lobby |

## Configuration

After first run, a config file is generated at `BepInEx/config/com.voicechatplugin.cn.cfg`:

```ini
[VoiceChat]
MicrophoneDevice =          # Mic device name (blank = system default)
SpeakerDevice =             # Speaker device name (blank = system default)
ServerAddress =             # Custom VC server URL (blank = official server)
MasterVolume = 1.0          # Master output volume (0.1–2.0)
MicVolume = 1.0             # Mic input volume (0.1–2.0)

[VoiceChat.Room]
MaxChatDistance = 6.0       # Default max hearing distance (1.5–20.0)
WallsBlockSound = true
OnlyHearInSight = false
# ... (all 11 room settings above)
```

## Running Your Own Voice Server

This plugin connects to an Interstellar voice server. If you want to host your own server, use the [Interstellar](https://github.com/Dolly1016/Interstellar) server framework.

After deploying your Interstellar server, set `ServerAddress` in the plugin config to your server URL (e.g., `ws://your-server.com:22021`).

## Build from Source

### Prerequisites

- .NET 6 SDK
- Access to BepInEx / AmongUs NuGet packages

### Steps

```bash
cd VoiceChatPlugin
dotnet build -c Release
```

Place `bin/Release/net6.0/VoiceChatPlugin.dll` into your `BepInEx/plugins/` folder.

## Technical Architecture

- **Interstellar** — WebSocket + WebRTC voice server transport
- **SIPSorcery** — .NET WebRTC library (peer connection, SDP, ICE)
- **NAudio** — Windows audio capture and playback
- **BepInEx IL2CPP** — Plugin framework for Unity IL2CPP backend
- **Opus** — Audio codec (handled by Interstellar internally)
- **Nebula Plugin** — Architectural reference for scene lifecycle and VC room management

## Contributors

- [AOU Team](https://github.com/All-Of-Us-Mods) — Starlight(Android) Audio Api Provider
- [Interstellar](https://github.com/Dolly1016/Interstellar) — Voice chat server framework by Dolly
- [Nebula on the Ship](https://github.com/Dolly1016/Nebula) — Plugin architecture reference by Dolly
- [ThreeXThreeTeam](https://github.com/ThreeXThreeTeam) — Among Us server support for mainland China by TAIKongguo
- [NAudio](https://github.com/naudio/NAudio) — .NET audio library
- [BetterCrewLink](https://github.com/OhMyGuus/BetterCrewlink) — Feature inspiration

## Testers

TAIKongguo, Farewell……

## License

MIT License. See [LICENSE](LICENSE).

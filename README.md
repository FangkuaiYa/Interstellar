# Interstellar Voice Chat

Real-time proximity voice chat for Among Us. A single BepInEx plugin DLL that connects to BetterCrewLink-compatible voice servers, with optional direct peer-to-peer media (STUN hole punching, encrypted UDP) and a per-peer server-relay fallback.

## Project Structure

```
AmongUs-VoiceChat/
├── Interstellar.sln
├── Interstellar.Client/       # BepInEx plugin — audio engine + game integration
│   ├── Network/               #   BCL-compatible Socket.IO protocol + server list
│   │   └── P2P/               #   P2P transport: candidates, hole punch, sealed UDP
│   ├── Routing/               #   Audio routing graph (mixer, filters, panner)
│   ├── Voice/                 #   Mic, Speaker, VCRoom, HUD buttons
│   ├── Game/                  #   Among Us integration (HUD, config)
│   │   └── UI/                #   Settings panel, windows, keybind capture
│   ├── Patches/               #   Harmony patches
│   ├── Android/               #   Android mic/speaker via Starlight
│   ├── NAudio/                #   Audio providers & effects
│   ├── Mixing/                #   Audio mixing
│   ├── Resources/             #   Embedded sprites + Locales/*.xml
│   └── Libs/                  #   Embedded libraries
├── server/                    # Go signal server (websocket rooms + roster)
├── nuget.config
└── .github/workflows/build.yml
```

## Build

**Prerequisites:** .NET 6 SDK

```bash
dotnet build Interstellar.Client/Interstellar.Client.csproj -c Release
```

NAudio and Concentus are resolved from NuGet and embedded into the plugin DLL automatically, so a single build pass produces a self-contained plugin.

**Install:** Copy `Interstellar.Client.dll` into `BepInEx/plugins/`. On Android, the same DLL is loaded from the loader's `localMods` folder.

## Transport Modes

How your voice reaches other players — **Settings → Server → Transport** (reconnects the voice room when changed), or `VoiceChat.P2P.Transport` in the config:

| Mode | Behavior |
|------|----------|
| `P2P` (default) | Direct UDP only. A peer without a validated path stays silent — strictly direct, never falls back to the server. |
| `Auto` | Direct UDP first; any peer whose hole punch fails is served over the server relay instead, so a failed punch degrades to relayed audio rather than silence. Exactly one path per peer — no frame is sent twice. |
| `Relay` | Everything rides the voice server's `signal` event (the original scheme; works with every BCL-compatible server as-is). |

The P2P socket runs in every mode, so mixed rooms (one player on `P2P`, another on `Relay`) still hear each other.

### How P2P works

1. **Signalling** — peers exchange ephemeral public keys and candidate lists (local addresses + STUN server-reflexive addresses) over the existing `signal` event; no server protocol change.
2. **STUN** — the public mapping is discovered from a configurable server list (`VoiceChat.P2P.StunServers`, default: 14 well-known servers); whichever server answers first provides the candidate.
3. **Hole punching** — both sides probe every candidate continuously (100 ms interval); a binding response validates the path and adopts the peer's true egress address. Candidates are refreshed when a new HELLO arrives; a path that goes silent re-punches automatically.
4. **Media** — Opus frames are sealed with AES-256-GCM and sent on the validated UDP path. In `P2P` mode media never rides the server, by design: no validated path, no packet.

## Voice Server Selection

The **Server** pane lists voice servers fetched at startup from the project's remote server-list JSON, with built-in entries as fallback. The last entry is **(Custom)** and uses `VoiceChat.Server.CustomURL`; the chosen index is stored in `VoiceChat.Server.ServerIndex`. **Refresh** restarts the voice room for the current game.

## Plugin Config

`BepInEx/config/com.voicechatplugin.cn.cfg`:

```ini
[VoiceChat]
MicrophoneDevice =
SpeakerDevice =
MasterVolume = 1.0
MicVolume = 1.0
HotkeyButtonScale = 1.0          # HUD mic/speaker buttons, 0.5–2.0 (stock = 1.0)
NoiseSuppression = true
EchoCancellation = true
VADEnabled = true

[VoiceChat.P2P]
Transport = P2P                  # Auto | P2P (default, direct only) | Relay
# StunServers = stun:host:3478,… # comma-separated stun: URIs (omit to keep the built-in list)

[VoiceChat.Server]
ServerIndex = 0                  # index into the settings-panel server list
CustomURL =                      # URL used when the last (Custom) entry is selected

[VoiceChat.Keybinds]
SettingsPanel = F11              # was F1 — migrated automatically on first run
PublicLobby = F2
PlayerVolume = F3
CycleMic = M
ToggleSpeaker = N

[VoiceChat.Room]
MaxChatDistance = 6
WallsBlockSound = true
OnlyHearInSight = false
ImpostorHearGhosts = false
OnlyGhostsCanTalk = false
HearInVent = true
HearVentPlayers = true
VentPrivateChat = false
CommsSabDisables = true
CameraCanHear = true
ImpostorPrivateRadio = false
OnlyMeetingOrLobby = false
PublicLobby = false              # advertise this room on the public lobby list
PublicTitle = Among Us Lobby
PublicLanguage = en
```

### Keyboard Shortcuts (defaults — all rebindable)

| Key | Function |
|-----|----------|
| `F11` | Toggle VC settings panel |
| `F2` | Public lobby browser |
| `F3` | Player volume |
| `M` | Cycle mic mode: Global → Impostor Radio → Muted |
| `N` | Toggle speaker on/off |

Chords with `Ctrl` / `Shift` / `Alt` / `Cmd` modifiers are supported (either side of a modifier counts). Rebind in **Settings → Audio → Keybinds** or edit the `VoiceChat.Keybinds` entries directly.

## Settings UI

The settings panel (`F11`) has seven categories:

**Audio** · **Devices** · **Player Volume** · **Public Lobby** · **Room** · **Server** · **Advanced**

- **Audio** — mic/speaker processing, keybinds, hotkey windows.
- **Server** — server list, **Transport** stepper (Auto / P2P / Relay), custom URL, Refresh.
- **Room** — proximity/radio rules; the host's values sync to the lobby.
- **Advanced** — Opus tuning (bitrate/FEC/DTX/update rate), diagnostics logging, HUD button scale.

On Android the bottom-left HUD buttons are raised for touch; scale them via `HotkeyButtonScale`.

## Localization

UI text is loaded from `Resources/Locales/*.xml`. Translations are maintained for **en**, **zh-Hans**, and **zh-Hant**; additional locale files (`ko`, `ja`, `de`, `ar`) ship as well.

## Troubleshooting: P2P Log Markers

From `BepInEx/LogOutput.log`:

| Line | Meaning |
|------|---------|
| `[P2P] STUN answers 7/14` | How many STUN servers replied (server-reflexive address found) |
| `[P2P] punching cid=N targets=M (candidates) … \| me=…` | Peer candidates received / our own advertised candidates |
| `[P2P] still trying (targets= tx= rx= all=)` | Probes sent / attributed to this session / received — `all` grows when the peer's probes arrive |
| `[P2P] rx probe cid=N` | First inbound probe from that peer (their NAT is reaching us) |
| `[P2P] connected cid=N via ip:port (binding response)` | UDP path validated — direct media can flow |
| `[P2P] media tx / media rx` | First frames on the direct path |
| `… no inbound for … — re-punching` | Path died; automatic re-punch |
| `Relay PLC` | Frame arrived via server relay (`Auto`/`Relay` fallback) |

In `Auto` mode, `tx>0, all=0` on both sides for a long time means neither NAT opened — the room stays on server relay for those peers, which is the intended degradation.

## CI

GitHub Actions builds on push:

- **Client** — .NET 6 BepInEx plugin (single-pass build, dependencies embedded)
- **Release** — Auto-create GitHub Release with the plugin (on tags)

## Credits

- [NAudio](https://github.com/naudio/NAudio) — .NET audio library
- [Concentus](https://github.com/lostromb/concentus) — .NET Opus codec
- [BetterCrewLink](https://github.com/OhMyGuus/BetterCrewLink) — voice server protocol reference

## License

MIT

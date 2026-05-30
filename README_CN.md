# Voice Chat Plugin（语音聊天插件）

为《Among Us》提供游戏内实时近距离语音聊天的 BepInEx 插件，基于 Interstellar 语音服务器。

## 功能特性

- **近距离语音聊天** — 根据玩家距离控制音量和左右声道，还原游戏内空间感
- **独立语音服务器** — 音频通过 Interstellar WebSocket + WebRTC 专用服务器传输，不经过 Among Us 游戏服务器，低延迟且不影响游戏网络
- **完全自包含** — 所有依赖（Interstellar、NAudio、SIPSorcery 等）以内嵌资源形式加载，无需额外安装任何外部插件
- **HUD 快捷按钮** — 右上角麦克风/扬声器切换按钮，鼠标悬停显示详细状态提示
- **说话状态条** — 屏幕顶部实时显示当前正在发言的玩家头像和名称
- **会议发言高亮** — 紧急会议投票阶段，正在发言的玩家头像边框会发光
- **多频道麦克风** — 全局频道 → 内鬼私密对讲 → 静音，三档循环切换
- **房主房间设置** — 房主可配置 11 项语音规则，通过 RPC 自动同步给所有客户端
- **中文本地化** — 原生支持简体中文和繁体中文，随游戏语言自动切换

---

## 安装

从 [Releases](https://github.com/FangkuaiYa/AmongUs-VoiceChat/releases) 下载插件。

### 方式一：ZIP 压缩包（推荐）

1. 下载对应平台的 ZIP 文件（Steam 或 Epic）
2. 将压缩包内所有文件解压到游戏根目录
3. 启动游戏即可

### 方式二：DLL 文件

1. 确保已为 Among Us 安装 [BepInEx](https://github.com/BepInEx/BepInEx)
2. 将 `VoiceChatPlugin.dll` 放入 `BepInEx/plugins/` 文件夹
3. v1.0.0 及以上版本还需安装 [Reactor](https://github.com/NuclearPowered/Reactor/releases)

---

## 游戏内操作

| 按键 | 功能 |
|------|------|
| `M`  | 切换麦克风状态（全局 → 内鬼频道 → 静音） |
| `N`  | 切换扬声器（开 / 关） |

### 麦克风状态循环

- **开启（全局频道）** — 附近所有人均可听到
- **内鬼私密频道** — 仅限内鬼之间通话（非内鬼身份时跳过此档）
- **静音** — 不传输你的语音

### HUD 图标含义

| 图标 | 状态 | 颜色 |
|------|------|------|
| 🎤 麦克风 | 全局发言 | 白色 |
| 🎤 麦克风 | 内鬼频道 | 红色 |
| 🔇 麦克风 | 已静音 | 淡红色 |
| 🔊 扬声器 | 正常播放 | 白色 |
| 🔇 扬声器 | 已静音 | 淡红色 |

---

## 房间设置

房主可在游戏大厅「语音聊天」设置面板中调整以下选项，设置将自动同步给所有玩家：

| 设置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| 最大聊天距离 | 浮点数 | 6.0 | 可听到的最远距离（1.5–20.0） |
| 墙壁阻挡语音 | 布尔 | 开启 | 墙壁会阻挡语音传播 |
| 仅视线内可听 | 布尔 | 关闭 | 只有同一房间或视线范围内的玩家才能听到 |
| 内鬼听到灵魂 | 布尔 | 关闭 | 内鬼可以听到已死亡玩家的语音 |
| 仅灵魂可说话 | 布尔 | 关闭 | 只有死亡玩家才能发言 |
| 管道内可听 | 布尔 | 开启 | 管道内可以听到管道外的声音 |
| 管道私密聊天 | 布尔 | 关闭 | 管道内的玩家只能与同在管道内的人对话 |
| 通讯破坏禁用语音 | 布尔 | 开启 | 通讯系统被破坏时语音聊天失效 |
| 监控摄像头可听 | 布尔 | 开启 | 使用监控（摄像头/门禁日志）时可以听到声音 |
| 内鬼私密对讲 | 布尔 | 关闭 | 内鬼之间可以私密通话 |
| 仅会议/大厅可聊 | 布尔 | 关闭 | 仅在紧急会议或大厅中可以语音聊天 |

---

## 配置文件

首次运行后，插件会在 `BepInEx/config/com.voicechatplugin.cn.cfg` 生成配置文件：

```ini
[VoiceChat]
MicrophoneDevice =          # 指定麦克风设备（留空使用系统默认）
SpeakerDevice =             # 指定扬声器设备（留空使用系统默认）
ServerAddress =             # 自定义语音服务器地址（留空使用官方服务器）
MasterVolume = 1.0          # 主输出音量（0.1–2.0）
MicVolume = 1.0             # 麦克风输入音量（0.1–2.0）

[VoiceChat.Room]
MaxChatDistance = 6.0       # 默认最大聊天距离（1.5–20.0）
WallsBlockSound = true
OnlyHearInSight = false
# ……（以上 11 项房间设置均可在此修改默认值）
```

---

## 自建语音服务器

本插件连接到 Interstellar 语音服务器。如果你想搭建自己的服务器，请使用 [Interstellar](https://github.com/Dolly1016/Interstellar) 服务端框架。

部署完成后，在插件配置中将 `ServerAddress` 设置为你的服务器地址（例如 `ws://your-server.com:22021`）。

---

## 从源码构建

### 前置要求

- .NET 6 SDK
- 可访问 BepInEx / AmongUs NuGet 包的网络

### 构建步骤

```bash
cd VoiceChatPlugin
dotnet build -c Release
```

将 `bin/Release/net6.0/VoiceChatPlugin.dll` 放入 `BepInEx/plugins/` 文件夹。

---

## 技术架构

- **Interstellar** — WebSocket + WebRTC 语音服务器传输层
- **SIPSorcery** — .NET WebRTC 库（RTCPeerConnection、SDP 协商、ICE 穿透）
- **NAudio** — Windows 音频采集与播放
- **BepInEx IL2CPP** — 运行于 Unity IL2CPP 后端的插件框架
- **Opus** — 音频编解码（由 Interstellar 内部处理）
- **Nebula Plugin** — 场景生命周期与语音房间管理的架构参考

### 目录结构

```
VoiceChatPlugin/
├── Reactor/                     # 多语言运行时注册框架
├── Resources/                   # 图标等嵌入资源
├── VoiceChat/                   # 语音房间、配置、音量控制、RPC 同步
├── VoiceChatPluginMain.cs       # 插件入口（含 AssemblyResolve 加载嵌入 DLL）
├── VCManager.cs                 # 场景级 MonoBehaviour 生命周期
├── VoiceChatRoomDriver.cs       # 房间驱动更新循环
├── VoiceChatHudState.cs         # HUD 按钮与麦克风/扬声器状态管理
├── PingTrackerPatch.cs          # 屏幕顶部发言状态条
├── MeetingSpeakingIndicatorPatch.cs # 会议阶段发言者高亮
└── Options.cs                   # 游戏规则设置面板注入
```

---

## 贡献者

- [AOU团队](https://github.com/All-Of-Us-Mods) — 提供了安卓适配所需的API接口
- [Interstellar](https://github.com/Dolly1016/Interstellar) — Dolly 开发的语音聊天服务端框架
- [Nebula on the Ship](https://github.com/Dolly1016/Nebula) — Dolly 开发的 Nebula 插件，语音聊天功能架构参考
- [ThreeXThreeTeam](https://github.com/ThreeXThreeTeam) — TAIKongguo 创建的开发团队，提供中国大陆 Among Us 服务器支持
- [NAudio](https://github.com/naudio/NAudio) — .NET 音频与 MIDI 库
- [BetterCrewLink](https://github.com/OhMyGuus/BetterCrewlink) — 部分设置选项与功能的灵感来源

## 测试者

TAIKongguo、Farewell……

## 许可证

基于 MIT 许可证开源。详见 [LICENSE](LICENSE)。

# Interstellar 信令服务器 · 部署教程

> 一句话：**这台机器是「联络处」，不是「喉舌」**。P2P 直连时语音根本不经过它；
> 只有「自动」模式打洞失败、或传输方式选「服务器中转」时，它才替你转发语音。
> 所以流量极小、负载极低，最便宜的机器随便跑。

---

## 1. 它到底做什么（现在的角色）

客户端有三种传输方式，服务器在其中的作用不一样：

| 传输方式 | 这台服务器做什么 |
|---|---|
| **P2P 直连**（默认） | 只做**控制面**：房间名单、打洞信令（候选地址 + 临时公钥）、静音/VAD 状态、公开大厅列表。**语音一个字节都不经过它。** |
| **自动** | 上面全部 + **给打洞失败的那几个人转发语音**（能直连的仍然走直连，逐个 peer 判断） |
| **服务器中转** | 上面全部 + **转发所有人的语音**（等于回到原版 BCL 的用法） |

具体清单：

| 做 | 不做 |
|---|---|
| 房间名单（`setClients` / `join` / `leave`） | ❌ TURN / ICE 中转（我们的插件用不到，一行都没有） |
| **信令中继**：P2P 候选地址 + 临时公钥（`P2P:` signal） | ❌ 账号鉴权、录音存储 |
| **媒体中转兜底**：`signal` 里的 base64 Opus 原样转发 | ❌ 持久化数据库、网页后台、管理面板 |
| 静音 / VAD 状态广播 | ❌ 语音解密（见下方隐私说明） |
| 公开大厅列表（内存实现） | |

### 隐私，说实话

- **走 P2P 直连的语音**：端到端加密（临时 ECDH + AES-256-GCM），服务器**看不到也解不了**。
- **走中转的语音**：是 base64 Opus 由服务器**原样转发**，服务器**理论上有能力听**——这和原版 BCL 服务器的行为完全一致。
  想要这层保证，把传输方式设成「P2P 直连」即可。

### 和原版服务器的关系

原版 BetterCrewLink 服务器是 Node/TypeScript：`express` + `socket.io@2.4.1` + `pug` 网页 +
`node-turn` TURN 服务 + `morgan`/`tracer`/`yaml`/`dotenv`（8 个运行时依赖，还带一整套网页样式）。

本服务器**零依赖、单个静态二进制、没有网页、没有 TURN**，只实现同一套事件协议：
`id` / `join` / `leave` / `VAD` / `signal` / `lobbybrowser` / `lobby` / `remove_lobby` / `join_lobby`。

> 注意：事件协议与原版一致，但本服务器**不下发 TURN/ICE 配置**（原版的 `peerConfig`、`node-turn` 那套）。
> 我们的插件不需要它（P2P 用的是自己的 STUN + 打洞），所以别指望拿它去喂老的 WebRTC 版客户端。

---

## 2. 文件

```
server/
├── dist/interstellar-signal-linux-amd64      ← 上传这个（7.2MB，静态编译，零依赖）
├── dist/interstellar-signal-windows-amd64.exe
├── interstellar-signal.service               systemd 单元
├── README.md                                 本文
└── *.go                                      源码（只用 Go 标准库）
```

实测：空载 **8.5MB 内存、8 个线程**（Windows 工作集；Linux 更低）。
不需要装 Python / Node / .NET，一个文件就是全部。

---

## 3. 部署（独立 Linux 机器）

### 3.1 上传并跑起来

```bash
sudo mkdir -p /opt/interstellar
scp dist/interstellar-signal-linux-amd64 你的用户@服务器IP:/opt/interstellar/

ssh 你的用户@服务器IP
cd /opt/interstellar
chmod +x interstellar-signal-linux-amd64
./interstellar-signal-linux-amd64 -listen :8090
```

看到这一行就是成功：

```
interstellar-signal 1.0.0 listening on ws://:8090 (no tls — point the plugin at http://…)
```

`Ctrl+C` 停止。

### 3.2 验证

本机：

```bash
curl http://127.0.0.1:8090/
# {"clients":0,"lobbies":0,"ok":true,"rooms":0,"uptime_s":5,"version":"1.0.0"}
```

从**你自己的电脑**（这一步同时验证了端口通不通）：

```bash
curl http://服务器IP:8090/
```

访问不到 → 是安全组 / 防火墙没放行，见第 4 节。

### 3.3 systemd 开机自启

```bash
sudo useradd --system --home /opt/interstellar --shell /usr/sbin/nologin interstellar
sudo chown -R interstellar:interstellar /opt/interstellar

# 按你的实际路径改一下 ExecStart，然后：
sudo cp interstellar-signal.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now interstellar-signal

systemctl status interstellar-signal      # 状态
journalctl -u interstellar-signal -f      # 实时日志
```

CentOS / RHEL 若用 firewalld：

```bash
sudo firewall-cmd --permanent --add-port=8090/tcp
sudo firewall-cmd --reload
```

---

## 4. 放行端口

| 方向 | 协议 | 端口 | 用途 |
|---|---|---|---|
| 入站 | **TCP** | 8090 | 客户端连信令服务器（**唯一必需**） |
| 入站 | UDP | — | **不需要**。媒体是点对点的，不经过这台机器 |
| 出站 | UDP | 3478 等 | 只有你在这台机器上跑 STUN 才需要 |

阿里云 / 腾讯云的安全组要**同时**检查两处：云控制台的安全组，以及机器内的
`ufw` / `firewalld` / `iptables`。

---

## 5. 把插件指过来

1. 游戏里 **F1 打开设置 → SERVER 分类 → 用 `>` 切到最后一项（Custom…）**
2. 关掉游戏，编辑 `BepInEx\config\Interstellar.cfg`：

   ```ini
   [VoiceChat.Server]
   CustomURL = http://服务器IP:8090
   ```

3. 重新进游戏（BepInEx 不会热加载 cfg，**改完必须重启游戏**）

**URL 写法**：

- 必须以 `http://` 或 `https://` 开头，插件自己会转成 `ws://` / `wss://`
- **不要写 `ws://`**，会解析失败
- 没开 TLS → `http://`；开了 TLS → `https://`

**怎么确认连上了**：

| 看哪里 | 出现这些就对了 |
|---|---|
| `BepInEx/LogOutput.log` | `[Srv] Open sid=…`、`[Srv] join room=…` |
| 服务器日志 | `[+] sid=… from=…`、`[+] join room=… sid=… pid=… cid=…` |

---

## 6. STUN（打洞用的公网地址发现）

**信令服务器**和 **STUN** 是两件事，都要有：

- 信令服务器：把双方候选地址和公钥递给对方 ← 本文这台
- STUN：告诉客户端"你的公网地址长什么样" ← 打洞的前提

同一个 cfg 里配（**这个键只在配置文件里，不进设置面板**，故意的）：

```ini
[VoiceChat.P2P]
StunServers = stun:stun.l.google.com:19302,stun:你的STUN服务器IP:3478
```

- 默认已内置 14 个公共 STUN，不填也能用
- 逗号分隔，每个都要带 `stun:` 前缀
- 你自己的 STUN 地址**只写在这里**，不会进仓库、不会进日志

验证 STUN 通没通 —— 客户端日志里：

```
[P2P] srflx candidate: 1.2.3.4:54321     ← 学到了公网地址
[P2P] punching cid=3 targets=4 (candidates)
[P2P] connected cid=3 sid=abc… via 5.6.7.8:31000 (hello)   ← 打洞成功，有声音了
```

---

## 7. 参数

| 参数 | 默认 | 说明 |
|---|---|---|
| `-listen` | `:8090` | 监听地址（环境变量 `SIGNAL_LISTEN` 同效） |
| `-ping-interval` | `25000` | 服务器心跳间隔（毫秒） |
| `-ping-timeout` | `10000` | 心跳无响应多久踢掉（毫秒） |
| `-max-clients` | `128` | 最大**同时在线连接数**，`0` = 不限。按成本控制选的小值：一个 60 人房就占掉 60 个名额，要多开房间再调大 |
| `-max-lobbies` | `200` | 最大大厅条目数，`0` = 不限（满了淘汰最旧的） |
| `-rate` | `3500` | 每连接持续消息速率（条/秒）。**中转模式下语音帧也走 `signal`**，每 peer 每帧一条 = `peer 数 × 25`（40ms 帧）或 `× 50`（20ms 帧）：60 人房 20ms 帧 ≈ 2965 条/秒，这个默认值留了余量 |
| `-burst` | `9000` | 每连接突发额度（≈ 3 秒大房满载），超出直接断开 |
| `-max-bps` | `3000000` | 每连接**上行字节/秒**的独立预算，`0` = 关。消息数为了大房放宽了，所以流量再单独设一道闸：中转满房约 680KB/s（4 倍余量），大包洪水半秒内断开 |
| `-quiet` | 关 | 只输出警告和错误 |
| `-tls-cert` / `-tls-key` | 空 | 填了就走 https/wss |
| `-version` | — | 打印版本 |

例：

```bash
./interstellar-signal-linux-amd64 -listen :443 \
  -tls-cert /etc/ssl/cert.pem -tls-key /etc/ssl/key.pem \
  -max-clients 1000 -quiet
```

---

## 7.1 流量估算（决定你要多大的机器）

一条中转语音帧 ≈ 40ms / 64kbps ≈ 320B，base64 后 ≈ **460B**；每帧给**每个 peer 各发一条**：

| 房间人数 | 每人上行（服务器收） | 满房中转时服务器总转发 |
|---|---|---|
| 15 | 161 KB/s ≈ **1.3 Mbps** | 2.4 MB/s ≈ **19 Mbps** |
| 30 | 334 KB/s ≈ **2.7 Mbps** | 10 MB/s ≈ **80 Mbps** |
| 60 | 679 KB/s ≈ **5.4 Mbps** | 40.7 MB/s ≈ **326 Mbps** |

**P2P 直连 / 自动模式下，上面这些基本归零**：媒体走 UDP 端到端，服务器只剩信令
（join/leave、每 5 秒一条心跳、打洞候选交换、VAD/静音广播、大厅列表）——一个 60 人房
也就**几 KB/s**，全服几十 KB/s。中转只发生在"自动模式下打洞失败的那几个人"身上。

所以：**给玩家用自动/直连，这台服务器几乎不耗流量**；中转是兜底，不是常态。
运维随时 `curl http://IP:8090/` 实测 `bytes_in` / `bytes_out` / `in_bps` / `out_bps`。

---

## 8. 排查

| 现象 | 原因 / 处理 |
|---|---|
| 客户端一直 `Connecting...` | 端口没放行；或 URL 写了 `https://` 但服务器没开 TLS；或 URL 写了 `ws://` |
| `[Srv] Connect err` / `Err: …` | 同上，看错误消息里的主机和端口 |
| 连上了但**别人听不到我** | 这是打洞问题，不是服务器问题 → 看下面 |
| 服务器日志没有 `[+] join` | 客户端没发出 join：房间码为空、或 `ServerIndex` 没切到 Custom |
| `setClients` 是 `{}` | **正常**，房间里当时只有你一个人 |
| 日志 `exceeded … msg/s — closing` 且玩家掉线 | 中转/Auto 模式下消息量 = 每人每帧一条，先算 `peer 数 × 25`（40ms 帧）或 `× 50`（20ms 帧），超了就调大 `-rate`/`-burst`；真被刷屏时这道闸仍会立刻断开对方 |
| 日志 `exceeded … B/s inbound — closing` | 上行超过了 `-max-bps`。满房中转每人约 680KB/s（60 人），留了 4 倍余量；正常玩家几乎不会触发，触发了基本是在推大包 |

### 打洞失败怎么查（按顺序）

客户端日志 `BepInEx/LogOutput.log`：

1. **没有 `[P2P] srflx candidate`** → STUN 全挂了。
   换 `StunServers`，或确认你自己的 STUN 从这台电脑能通（`nc -u` 测 3478）。
2. **有 `punching … targets=0`** → 信令没收到对方候选地址。
   检查服务器日志里两边是不是都在同一个 `join room=` 下。
3. **有 `punch attempt 1/2/3` 但一直没 `connected`** → 打洞被 NAT/防火墙挡住。
   - 本机防火墙放行 UDP 出站
   - 对方若是运营商级对称 NAT，纯 P2P 打不通是**预期行为**（按设计：打不通就没声音，
     不会回落到中转）
4. **`[P2P] connected cid=…` 出现了但还是没声音** → 看 `[Srv] Dec …` 解码告警，
   那是编解码问题，与网络无关。

服务器侧可用 `curl http://IP:8090/` 看 `clients` / `rooms` / `lobbies` / `uptime_s`，
以及流量统计 `bytes_in` / `bytes_out` / `in_bps` / `out_bps` 和当前限额
`max_clients` / `rate_limit` / `max_bps`（`in_bps`/`out_bps` 是两次查询之间的平均速率）。

---

## 9. 安全须知

- **没有账号体系**：任何能连到这个端口的人都可以加入任意房间码。
  房间码是 Among Us 生成的，是唯一的门槛 —— 别用太明显的码。
- 服务器能看到：谁在哪个房间、双方的公网 IP 和端口；**如果走的是中转，还能看到 base64 Opus 本身**
  （中转只是原样转发，理论上有能力听 —— 想彻底断掉这条，客户端用「P2P 直连」）。
  走 P2P 直连的语音端到端加密，服务器**看不到也解不了**，密钥不经过它、无法中间人。
- 想加密信令通道：用 `-tls-cert`/`-tls-key` 自签或 ACME，或在前面加 nginx/caddy 反代 `wss://`。
- `-rate` / `-burst` / `-max-bps` / `-max-clients` 是防滥用的兜底。消息闸和流量闸**任一命中就立刻断开**
  （日志会写清楚是哪一条）。默认值已按「60 人中转房」调过；`-max-clients 128` 是成本闸门，
  多开房间就调大它。

---

## 10. 自己编译 / 跑测试

需要 Go 1.21+（只要编译机有，目标机器不用装）。

```bash
cd server

# 协议测试（13 项：分帧契约、roster、信令中继、房间隔离、限流、心跳回收、大厅）
go test ./...

# 交叉编译 Linux 静态二进制
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 \
  go build -trimpath -ldflags="-s -w" -o interstellar-signal-linux-amd64 .
```

`dist/` 里两个预编译产物就是这么来的。

---

## 11. 协议对照（改代码前先看这个）

客户端行为的权威来源是 `Interstellar.Client/Network/ServerConnection.cs`：

```
客户端 → 服务器
  42["id", playerId, clientId]
  42["join", room, playerId, clientId, false]
  42["VAD", bool]
  42["signal", {"to": sid, "data": "..."}]
  42["lobbybrowser", bool] / 42["lobby", code, {...}] / 42["remove_lobby", code]
  42<ack>["join_lobby", lobbyId]

服务器 → 客户端
  0{"sid":…,"upgrades":[],"pingInterval":…,"pingTimeout":…}   ← 必须单独一帧
  40                                                          ← 必须单独一帧
  42["setClients", {sid: {playerId, clientId}, …}]
  42["join", sid, {playerId, clientId}]
  42["VAD", {"activity": bool, "client": {"clientId": n}}]
  42["signal", {"from": sid, "to": sid, "data": "..."}]        ← from 必填
  42["new_lobbies", […]] / 42["update_lobby", {…}] / 42["remove_lobby", id]
  43<ack>[0,"ROOMCODE"]   ← 引擎前缀 '4' + ack '3'，与 42 同帧规则一致
```

**两条不能破坏的契约**（测试 `TestHandshakeIsTwoFrames` 会守住）：

1. 引擎开放包和 `40` **必须分两帧发**。插件的 `HandleEngineIOMessage`
   只看收到内容的第一个字节，粘在一起会让它永远等不到 `40`，永远不 join。
2. 中继的 signal 必须带 `from`，接收方用它查自己的 peer 表，查不到就丢弃。

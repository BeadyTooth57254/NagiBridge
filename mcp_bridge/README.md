# NagiBridge 远程 MCP 桥

把星露谷的游戏操控装成**远程 MCP**，手机上的 MCP 客户端（operit 等）连上来就能直接控局。**同一套代码支持两种模式**：在家走局域网直连，外出走云端，甚至两者同时挂。

```
[手机 operit MCP 客户端]
      │  streamable HTTP
      ▼
[桥 server.py]  ── WebSocket 隧道 ──►  [电脑 client.py]  ──►  [游戏内 NagiBridge HTTP API]
```

游戏只跑在你电脑上，桥只负责转发。**你在家、手机和电脑同一 Wi-Fi** → 用局域网模式，零云开销；**人不在家** → 用云端模式，手机走公网到你的桥再下到电脑。

- **`server.py`**：桥。暴露 15 个工具 + `/tunnel` 隧道 + `/health`。绑定 `0.0.0.0`，局域网和云端都能用。
- **`client.py`**：电脑端常驻。可连**多个**桥（逗号分隔），把手机发来的方法转成游戏 HTTP 调用(`http://127.0.0.1:58331`)。

---

## 双实例 coop + 游戏内聊天（host 你玩，AI 当 farmhand）

星露谷 coop 是 **host 起局 + farmhand 加入**，也就是两局（双实例）。NagiBridge 在两局都装上，各自**按角色**绑定自己的 HTTP 端口（`config.json` 的 `HostPort` / `FarmhandPort`）：

| 实例 | 角色判定 | 端口（默认） | 用途 |
|---|---|---|---|
| **host**（你玩） | `IsMainPlayer == true` | **58331** | 你的游戏；**游戏内聊天**接 operit |
| **farmhand**（AI） | `IsMainPlayer == false` | **58332** | AI 用 MCP 操控 |

- **启动顺序无关紧要**：`EnsureServerStarted` 在进入存档后按 `IsMainPlayer` 判定角色，再绑对应端口；日志和 `/status` 会写明 `role=HOST|FARMHAND` 和端口，不会混淆。
- **游戏内聊天 → operit**：host 的 Nagi 聊天面板（按 `` ` `` 打开），在 `config.json` 里设 `"Mode": "operit"` + `OperitBridgeUrl`（默认 `http://127.0.0.1:8000`）+ `OperitBridgeToken`（与桥一致）。你打的字会 POST 到桥 `/ingame-in`，operit 用 `read_ingame` 读到、用 `send_ingame` 回复，回复经桥 + 隧道推回 host 的面板（`/chat/push`）。
- **AI 控 farmhand**：client 的 `NAGI_GAME_URL` 指 **58332**（farmhand）；`NAGI_HOST_URL` 指 **58331**（host，仅用于推聊天回复）。

---

## 模式一：局域网直连（默认/最简单，家用推荐）

手机和电脑**同一 Wi-Fi**，不依赖任何云服务器。

**最省事（一键启动）：**
1. 双击 `gen_token.bat` —— 生成一次性 token 并写到 `token.txt`，屏幕会显示它（之后粘到 operit）。
2. 双击 `launcher.bat` —— 自动开两个窗口（桥 8000 + 隧道客户端），token、游戏地址都自动带了。
3. operit 填 `http://192.168.100.236:8000/mcp`，鉴权 Bearer = step 1 显示的 token。

**手动（等价，想自己来也可以）：**
```pwsh
# ① 电脑上起桥（0.0.0.0:8000）
$env:NAGI_BRIDGE_TOKEN='你的强密钥'
python server.py
#   它会打印： Phone on home Wi-Fi (LAN): http://<电脑局域网IP>:8000/mcp

# ② 电脑上起客户端（连本机桥）
$env:NAGI_BRIDGE_TOKEN='你的强密钥'
$env:NAGI_GAME_URL='http://127.0.0.1:58331'      # 游戏内 NagiBridge 的 HTTP 端口
python client.py
```

**operit**：连接方式选 **streamable HTTP**，地址填 `http://<电脑局域网IP>:8000/mcp`，**鉴权选 Bearer Token，值填和电脑端一致的 `NAGI_BRIDGE_TOKEN`**（/mcp 默认就是要它，没它进不来）。
Windows 防火墙放行 TCP 8000。手机和电脑在同一 Wi-Fi 即可。
若自动探测的 IP 不对（VPN/多网卡/无外网），设 `$env:NAGI_LAN_IP='<你的局域网IP>'` 再起桥。

---

## 模式二：云端模式（可选，人不在家也能控）

把桥部署到 Zeabur（或任意服务器），手机从公网连。

1. **Zeabur**：部署 `server.py`，环境变量 `NAGI_BRIDGE_TOKEN`（和电脑端一致）、`PORT`（Zeabur 提供）。得到 `https://<your-app>`。
2. **电脑**：
```pwsh
$env:NAGI_BRIDGE_TOKEN='你的强密钥'
$env:NAGI_BRIDGE_URLS='wss://<your-app>/tunnel'
$env:NAGI_GAME_URL='http://127.0.0.1:58331'
python client.py
```
3. **operit** 填 `https://<your-app>/mcp`（streamable HTTP），鉴权 Bearer Token 值填 `NAGI_BRIDGE_TOKEN`。

---

## 模式三：两者都挂（任何地方都能控，不用切换）

电脑客户端用**逗号分隔**同时连多个桥，局域网 + 云端一起活着：

```pwsh
$env:NAGI_BRIDGE_TOKEN='你的强密钥'
$env:NAGI_BRIDGE_URLS='ws://127.0.0.1:8000/tunnel,wss://<your-app>/tunnel'   # local + cloud
$env:NAGI_GAME_URL='http://127.0.0.1:58331'
python client.py
```

在家手机连局域网 `http://<电脑IP>:8000/mcp`（快、无云开销），外出连 `https://<your-app>/mcp`。两个地址都指到同一个游戏，**不用来回改配置**。

---

## 环境变量速查

| 变量 | 作用 | 默认 |
|---|---|---|
| `PORT`（server） | 监听端口 | 8000 |
| `NAGI_LAN_IP`（server） | 覆盖自动探测的局域网 IP（VPN/多网卡/无外网时用） | 自动探测 |
| `NAGI_BRIDGE_TOKEN` | 共享密钥，server/client 一致 | changeme（必改） |
| `NAGI_BRIDGE_URLS`（client） | 逗号分隔的桥地址（可多个） | `ws://127.0.0.1:8000/tunnel` |
| `NAGI_GAME_URL`（client） | farmhand（AI 控的）游戏 HTTP API | `http://127.0.0.1:58332` |
| `NAGI_HOST_URL`（client） | host（玩家玩的）游戏 HTTP API，用于游戏内聊天回推 | `http://127.0.0.1:58331` |
| `NAGI_MODS_JSON`（client） | 键位映射文件 | `../scripts/mods_keybinds.json` |
| `NAGI_BRIDGE_MCP_AUTH`（server） | 是否要求 `/mcp` 带 `Authorization: Bearer <token>` | **开**（`1`） |

健康检查：`GET /health` 应见 `{"ok":true,"gameConnected":<bool>}`（电脑端连上为 true）。

## 安全

- `/mcp` **默认要求 Bearer token**（`NAGI_BRIDGE_MCP_AUTH=1`），operit 端鉴权填 Bearer、值 = `NAGI_BRIDGE_TOKEN`。没 token 一律 401，别关。
- `NAGI_BRIDGE_TOKEN` 是真正的门，务必设**强随机**（别用 changeme）；关掉鉴权只在你完全信任网络时才做。
- 局域网模式只暴露到你家 Wi-Fi；云端模式暴露公网，必须强 token + 保留 Bearer 鉴权。
- 局域网桥**不要在路由器做端口转发**；只有云模式走公网。
- 游戏必须开着 + 电脑端 client 挂着，/mcp 才有反应。

## 本地验证（不用游戏）

- **工具链路**：三个终端，分别跑 `mock_game.py`、`server.py`（`NAGI_BRIDGE_TOKEN=t`）、`client.py`（连 `ws://127.0.0.1:8000/tunnel`，`NAGI_GAME_URL=http://127.0.0.1:58332`），再用任意 MCP 客户端连 `http://127.0.0.1:8000/mcp`，可看到工具并调用。
- **游戏内聊天往返**：直接跑 `python test_chat_loop.py`，会用一个模拟 host 游戏把整条「`/ingame-in` → `read_ingame` → `send_ingame` → host `/chat/push`」走一遍。见到 `ALL OK` 即通。

## 加工具

`server.py` 加一个 `@mcp.tool()`，再到 `client.py` 的 `ROUTES` 里登记对应的 HTTP 映射即可。

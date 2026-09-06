# NagiBridge v1.0.0 — 联机 AI 聊天 + AI 控制 farmhand

> 发布包已带编译好的 `NagiBridge.dll`，解压即可用，**无需自己编译**。
>
> 本版发布 **两个压缩包**：
> - **`NagiBridge.zip`** —— 模组本体，解压后整个 `NagiBridge/` 文件夹放进 `Stardew Valley/Mods/`。
> - **`NagiBridge-mcp_bridge.zip`** —— 联机 AI 用的 **Python 桥**（server.py + client.py + launcher.bat + gen_token.bat），本地解压即可，**不是装进 Mods**，跑在电脑上。

## 这个版本

- **联机(co-op)支持**：host 实例绑 `58331`（游戏内聊天），farmhand 实例绑 `58332`（AI 操控网关），角色由 `IsMainPlayer` 自动判定。
- **游戏内 AI 聊天**：按 `` ` `` 打开聊天面板，`"Mode": "operit"` 时你的消息会转发到 MCP 桥（`/ingame-in`），AI（operit）用 `read_ingame` / `send_ingame` 在游戏里回你。
- **AI 控制 farmhand**：operit 通过 MCP 桥操控 farmhand（移动、使用工具、交互、种田等），共 **17 个工具**。
- **HTTP 按角色绑定 `localhost`**：不再需要 `http://+` URLACL，重启即生效。

## 安装
1. 装 [SMAPI](https://smapi.io/)（Stardew Valley 1.6+ / SMAPI 4.0+）。
2. 解压 `NagiBridge.zip`，把 `NagiBridge` 文件夹放进 `Stardew Valley/Mods/`。
3. 用 SMAPI 启动游戏。

## 联机 + AI（进阶，可选）

两个实例（host / farmhand）共用一份 `config.json`，设：

```json
{
  "Mode": "operit",
  "HostPort": 58331,
  "FarmhandPort": 58332,
  "OperitBridgeUrl": "http://127.0.0.1:8000",
  "OperitBridgeToken": "<共享 token>"
}
```

解压 `NagiBridge-mcp_bridge.zip`，双击 `launcher.bat` 起桥（server 8000 + client），手机 operit 连
`http://<电脑局域网IP>:8000/mcp`（`Authorization: Bearer <token>`）。先跑一次 `gen_token.bat` 生成 `token.txt`。详见仓库 README。

## 平台
Windows / macOS / Linux。

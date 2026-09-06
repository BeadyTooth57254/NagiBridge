# WheatStook v1.1.0 — 联机 AI 聊天 + AI 控制 farmhand

> 发布包已带编译好的 `WheatStook.dll`，解压即可用，**无需自己编译**。
>
> 本版发布 **两个压缩包**：
> - **`WheatStook.zip`** —— 模组本体，解压后整个 `WheatStook/` 文件夹放进 `Stardew Valley/Mods/`。
> - **`WheatStook-mcp_bridge.zip`** —— 联机 AI 用的 **Python 桥**（server.py + client.py + launcher.bat + gen_token.bat + `scripts/mods_keybinds.json`），本地解压即可，**不是装进 Mods**，跑在电脑上。

## 自 v1.0.0 的新增

- **项目更名 NagiBridge → WheatStook（麦垛）**：打包、manifest、README 全部对齐；兼容层 CompatRules.json 注释同步更新。
- **AI farmhand 桥梁：诚实化 + 功能扩展**：
  - `get_state` 默认**轻量**；新增 **`get_state_full`**，**只在显式调用时才全量读**背包/建筑/领地/mods/传送/附魔（省 token，避免 AI 被无关数据带偏）。
  - **`inventory`**：**一排(12格)读取**，同时注入游戏时间 + 局部地图；标注装备热键(1-9,0)与"换一排(row=N)"（星露谷没有切换背包排的热键，如实说明）。
  - **`wheatstook_selftest`**：服务绑定/config/模组数/内存/兼容 + 桥通道状态，AI 自己排查用。
  - **坐标区块化**：任何返回的坐标都带 精确(x,y) + 区块坐标 + 区块内子坐标（chunkSize 定分区）。
  - **`drop` 改为真·扔地上**（`Game1.createItemDebris`），走上去可捡回；不再"静默消耗"。
  - **游戏内聊天可见**：AI 的 `chat` 消息走 mod 自己的 ChatHud 面板（画在当前视口）并**自动弹出**，取代只渲染在单一玩家视口的 vanilla `Game1.chatBox`（之前 AI 发了你看不见）。
  - 所有响应注入 `timeOfDay`/`gameTime`；`send_ingame`(host 58331) vs `chat`(farmhand 58332) 通道区分清楚。
- **修复一批 AI 时序/真实性问题**：`handle_tool`/`handle_select`/`handle_move`/`follow`/`area` 的 ok/used/found/steps 改为同步计算或"已接受入队"（不再恒 false）；findModDir 单目录独立 try/catch；GMCM 用真实 API。
- **自动兼容层**：数据驱动 `CompatRules.json` + `enableAutoCompat` 按 UniqueId 检测（加戒指槽/超大背包/自定义作物/新地区/职业），激活列表写进 /state。
- **keybind**：静态 `scripts/mods_keybinds.json`，每次查询重读（非启动读一次）；补 Joja Express，删废弃的 FA F6。
- **Mod 知识库 / 长期记忆 / 自动反应层**：按需查询已装模组用法、`wheatstook_mem` 记忆、每天清晨简讯。

## 安装

1. 装 [SMAPI](https://smapi.io/)（Stardew Valley 1.6+ / SMAPI 4.0+）。
2. 解压 `WheatStook.zip`，把 `WheatStook` 文件夹放进 `Stardew Valley/Mods/`。
3. 用 SMAPI 启动游戏。

## 联机 + AI（进阶，可选）

两个实例（host / farmhand）共用一份 `config.json`，照 `config.example.json` 抄，最小示例：

```json
{
  "Mode": "operit",
  "HostPort": 58331,
  "FarmhandPort": 58332,
  "OperitBridgeUrl": "http://127.0.0.1:8000",
  "OperitBridgeToken": "<共享 token>"
}
```

解压 `WheatStook-mcp_bridge.zip`，双击 `launcher.bat` 起桥（server 8000 + client），手机 operit 连
`http://<电脑局域网IP>:8000/mcp`（`Authorization: Bearer <token>`）。先跑一次 `gen_token.bat` 生成 `token.txt`。详见仓库 README。

## 平台

Windows / macOS / Linux。

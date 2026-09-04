# NagiBridge — 星露谷打工指南

你是星露谷物语的农场打工仔。通过 NagiBridge HTTP API 控制角色。

## 你的端口
你控制端口 7842（host角色）。另一个AI控制端口 7843（farmhand）。不要动 7843。

## 项目位置
脚本在 scripts/ 目录下。所有 Python 脚本调用前必须加 PYTHONIOENCODING=utf-8。

## 可用脚本
- farm_row.py — 种田（翻地+播种+浇水，蛇形走位，自带pre/post检查）
- water_crops.py — 浇水（蛇形走位+实时水量检测）
- chop_trees.py — 砍树（warp精准站位，树干+树桩一口气砍完）
- clear_area.py — 开垦（两轮：move_to粗清 + warp精补）
- harvest.py — 收割（--sell 可出售）
- mine_run.py — 挖矿
- keg_manager.py — 酿酒桶（扫描→收成品→装水果→卖）
- furnace_manager.py — 熔炉（收锭→装矿+煤）
- pet_animals.py — 撸动物（遍历所有动物，没撸的撸一遍）
- fish_run.py — 钓鱼（配合Fishbot mod）
- shop_buy.py — 购物（检查背包→存箱子→传送商店→购买→返回）
- tool_agent.py — 独立tool-calling agent（LLM直接调17个游戏工具）
- stardew_api.py — API helper

所有脚本的 --port 参数用 7842。

## API 端点 (http://0.0.0.0:7842)

### 基础
/status, /state, /move, /stop, /face, /select, /position

### 操作
/use, /tool, /interact, /key, /chat, /emote, /queue

### 信息
/surroundings, /map, /alerts, /scan

### 导航
/warp, /sleep, /wakeup

### 商业
/buy, /sell, /harvest, /store, /chest, /placechest, /fishbot

### 菜单/制作/机器/动物
/menu, /menu/click, /craft, /machines, /animals

### 节日
/festival, /festival/interact, /festival/answer

### 小游戏
/minigame/state, /minigame/bot

### 游戏内聊天
/chat/push, /chat/history

### 作弊
/give, /money, /heal, /ripen, /refill, /pause, /resume

### 端点详细说明

| 端点 | 方法 | 说明 |
|---|---|---|
| `/status` | GET | 服务器状态、worldReady |
| `/state` | GET | 玩家完整状态（HP/体力/位置/背包/菜单/事件） |
| `/move` | POST | 寻路移动 `{x, y}` |
| `/stop` | POST | 停止移动 |
| `/face` | POST | 朝向 `{direction}` 0=上 1=右 2=下 3=左 |
| `/select` | POST | 选择背包物品 `{name}` |
| `/position` | POST | 直接设置位置（精确定位） |
| `/tool` | POST | 使用工具 `{name}` 或 "current" |
| `/use` | POST | 使用手持物品（放置类） |
| `/interact` | POST | 与面前格子交互 |
| `/key` | POST | 模拟按键（任意键+组合键：`"r"`/`"F5"`/`"shift+r"`/`"ctrl+1"`/`"alt+enter"`） |
| `/chat` | POST | 发聊天消息 |
| `/emote` | POST | 播放表情 |
| `/queue` | POST | 命令队列 |
| `/surroundings` | GET | 扫描周围 `?radius=10`（mod作物/物件显示可读名） |
| `/map` | GET | 地图信息（建筑/传送/NPC），非网格；要纯文字网格用 /ctx |
| `/ctx` | GET | 纯文字网格地图 `?radius=8`（一眼看全场，对AI友好，mod作物/物品显示可读名） |
| `/area` | POST | 批量浇水/清浇 `{op:"water"|"unwater", x1,y1,x2,y2}`（直改土块，不耗体力） |
| `/drop` | POST | 丢当前手持物品到地上 |
| `/follow` | POST | 自动跟随 `{target:"player:X"|"npc:X"|"x,y"}`，空 target 停 |
| `/alerts` | GET/POST | 游戏内警告队列 |
| `/scan` | GET | 扫描当前地图全部物体 |
| `/warp` | POST | 传送 `{location, x?, y?}` 矿洞用 `UndergroundMine5` |
| `/sleep` | POST | 睡觉 |
| `/wakeup` | POST | 起床 |
| `/buy` | POST | 购买 `{id, count, price?}` |
| `/sell` | POST | 出售到shipping bin |
| `/harvest` | POST | 收割作物 |
| `/store` | POST | 存物品到箱子 `{x, y, name?}` |
| `/chest` | POST | 管理箱子内容 |
| `/placechest` | POST | 放置箱子 |
| `/fishbot` | POST | 控制Fishbot `{action: "on"/"off"/"toggle"/"status"}` |
| `/menu` | GET | 菜单详情：类型、对话文本、选项列表、商店物品、按钮坐标 |
| `/menu/click` | POST | 点菜单 `{option:0}` / `{button:"ok"}` / `{x,y}` |
| `/craft` | POST | 制作物品 `{name:"Keg", count:5}` |
| `/machines` | GET | 扫描当前地图所有机器，返回状态(empty/processing/ready) |
| `/animals` | GET | 动物详情：wasPetToday/friendship/happiness/fullness/productReady |
| `/festival` | GET | 当前节日信息 |
| `/festival/interact` | POST | 节日交互 |
| `/festival/answer` | POST | 节日问答选项 |
| `/minigame/state` | GET | 小游戏状态（JotPK/CalicoJack等） |
| `/minigame/bot` | POST | 小游戏bot控制 |
| `/chat/push` | POST | 推送消息到游戏内聊天面板 `{sender, message}` |
| `/chat/history` | GET | 获取聊天历史 |
| `/give` | POST | 给物品 `{id, count}` |
| `/money` | POST | 加钱 `{amount}` |
| `/heal` | POST | 满血满体力 |
| `/ripen` | POST | 催熟作物 |
| `/refill` | POST | 填满水壶 |
| `/pause` | POST | 冻结时间 |
| `/resume` | POST | 恢复时间 |

## 远程 MCP 桥（mcp_bridge/）

把游戏操控装成远程 MCP，手机 operit 连上可直控。

- **Zeabur**：`mcp_bridge/server.py`（用 `mcp` SDK 的 FastMCP + `streamable_http_app` 当**根 app**，`app.router.add_websocket_route("/tunnel")` 挂隧道——注意 streamable app 必须当根，mount 到子路径会坏掉 session manager）
- **电脑**：`mcp_bridge/client.py` 常驻，按 `ROUTES` 把方法映射到游戏 HTTP
- **双模式**：同一套代码。LAN（server+client 同机，手机连局域网 IP）与云端（server 上 Zeabur，手机连 https）都支持；client 用 `NAGI_BRIDGE_URLS` 逗号分隔**同时挂多个桥**，LAN+云端一起活，手机选能到的那个即可
- 传输：streamable HTTP（`/mcp`），手机端模型直接驱动工具（无服务器 agent 循环）
- 安全：`NAGI_BRIDGE_TOKEN` 为隧道 token（真正的门）；**`/mcp` 默认要求 Bearer 鉴权**（`NAGI_BRIDGE_MCP_AUTH=1`），operit 端填 Bearer = token；无 token 一律 401
- 关键：游戏在 Es 电脑 NAT 后面，云服务器连不到 → 反向隧道（电脑端主动外连）是前提
- 本地验证：`mock_game.py` + `server.py` + `client.py` 三进程，用 mcp 客户端连 `/mcp` 测往返（已跑通）
- 加工具：`server.py` 加 `@mcp.tool()` + `client.py` 的 `ROUTES` 登记映射

## 模组键位映射（自动提取，AI 直接按）

新增 `scripts/extract_keybinds.py`：扫描**全部** mod 的 config，提取"哪个 mod 的哪个功能绑了哪个键"，输出到 `scripts/mods_keybinds.json`。

- 重新提取：`python scripts/extract_keybinds.py [Mods目录]`（默认 `D:\SteamLibrary\steamapps\common\Stardew Valley\Mods`）
- **自动实时读取**：`tool_agent.py` 每次启动时会自动重扫全部 mod 的 config 并刷新 `mods_keybinds.json`；运行中若检测到某 config.json 变化也会自动刷新，无需手动跑
- **游戏启动自动生成**：NagiBridge mod 在 `OnGameLaunched` 里以子进程跑 `python scripts/extract_keybinds.py`，游戏一启动就重建映射（脚本路径可用 `ModConfig.KeybindsExtractScript` 改）
- **GMCM 键位能读到**：GMCM 只是改键界面，你在 GMCM 里换键会经 SMAPI `WriteConfig` 写回各 mod 的 config.json（如 GMCM 自己的 `OpenMenuKey: "RightControl"`），所以扫 config.json 就能抓到 GMCM 改过的键
- mod 目录可用环境变量 `NAGI_MODS_PATH` 指定，默认标准游戏目录
- AI 工具集新增 `keybind` 工具：查键并返回 `keychain` 字符串，直接喂给 `/key` / `press_key`
  - 例：`keybind(query='quick stack')` → `Convenient Inventory / QuickStackKeyboardHotkey / keys:["Q"] / keychain:"q"`
  - 例：`keybind(query='npc map')` → `NPC Map Locations / MinimapToggleKey / keys:["OemPipe"]`
- `/key` 现在认识 SMAPI 键名（`Back`/`OemPipe`/`LeftControl`/`D6` 等）和组合键（`LeftControl + LeftShift + F6` → `"leftcontrol+leftshift+f6"`）
- 局限：只能抓到写进 config.json 的键位；通过 GMCM 运行时注册、或非标准存储的，抓不到。

## 踩坑记录（重要！）
1. farmhand 的 GetGrabTile 有偏移 → 统一站在目标格上方(y-1)面朝下操作
2. BeginUsingTool 有延迟，等几秒再检查结果
3. 浇水壶补水用 /refill 端点，不要尝试对水源用工具
4. 收割前先检查背包空间，背包满了会收割失败
5. 放箱子用 /placechest，不要用 /give 刷箱子到背包
6. move_to 寻路可能落点偏1格 → 操作前用 face_toward() 根据实际位置算朝向，不要硬编码方向
7. 砍树/浇水等操作前验证 facing tile 是否 == 目标 tile，对不上就换角度
8. 水壶水量用 watering_can_water() 实时检测，不要硬编码补水时机
9. **精确站位用 warp** → 放置/拆除/砍树等需要精准对齐的操作，用 `warp("Farm", x, y)` 代替 move_to
10. **砍树要砍树桩** → 树干倒了之后还有树桩，要继续砍（总共约15-18下），砍完走过去捡木头
11. **开垦两轮清扫** → Pass 1 move_to 粗清 + Pass 2 warp 精补，最后再扫一次确认
12. **背包满 /give /craft 静默掉落** → give/craft 前先检查背包空间，满了物品会掉地上
13. **存箱子用 /store** → 需要传箱子坐标 `{x, y, name}`，不是 interact 箱子
14. **大树桩(LargeStump)需铜斧** → 基础斧子砍不动

## 农场布局（y=14 生产线）
```
箱子  箱子  [Bin]  熔炉  熔炉  酒桶  酒桶  酒桶
(69)  (70)  (71)  (73)  (74)  (75)  (76)  (77)
```
扩产往右接着摆或往下开新行。坐标图见桌面 `farm_map.txt`。

### 存取箱子
```bash
# 存入（指定箱子坐标 + 物品名）
curl -X POST http://0.0.0.0:7842/store -d '{"x":70,"y":14,"name":"Wood"}'
# 存所有非工具物品
curl -X POST http://localhos0.0.0.0:7842/store -d '{"x":70,"y":14}'
```

## 协作
操作前先读 scripts/coordination.json 看AI在干什么，避免重复。做完了更新你的状态。

## 挖矿
```bash
PYTHONIOENCODING=utf-8 python3 scripts/mine_run.py --start-level 1 --max-levels 5 --hp-threshold 30 --port 7842
```
- warp传送，不需要走路找矿洞入口
- AutoCombat mod 自动打怪，脚本只管敲石头
- 血量低或体力不足自动warp回Farm

## 购物
```bash
PYTHONIOENCODING=utf-8 python3 scripts/shop_buy.py --items "493:10,491:6" --port 7842
```
- 自动检查背包→清理到箱子→传送商店→购买→传回农场
- 物品格式: item_id:count，逗号分隔

## 重启游戏（加载新DLL时需要）
```powershell
# 1. 关掉游戏
Get-Process StardewModdingAPI -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

# 2. 重新启动
Start-Process "E:\SteamLibrary\steamapps\common\Stardew Valley\StardewModdingAPI.exe"
```
启动后需要手动进存档（或等the player操作），然后轮询等待 worldReady：
```bash
# 3. 等待游戏就绪
until curl -s http://0.0.0.0:7842/status 2>/dev/null | grep -q '"worldReady":true'; do sleep 5; done
echo "Game ready!"
```

## 卡住检测
每次操作后检查 /state 的 activeMenu 字段：
- `activeMenu` 不是 null → 有对话框/菜单弹出来了，你卡住了
- 用 `/key confirm` 推进对话
- 对话可能有多页，反复调 `/key confirm` 直到 activeMenu 变回 null

## 钓鱼
Fishbot mod 已安装，F5 开关自动钓鱼。流程：
1. 确保背包有鱼竿（初始有 Bamboo Pole）
2. warp 到钓鱼点：
   - 海边: `{"location":"Beach"}`
   - 山湖: `{"location":"Mountain"}`
   - 森林河: `{"location":"Forest"}`
   - 矿洞湖: `{"location":"Mine"}`
3. 走到水边面朝水面
4. 用 `/key` 模拟按 F5 启动 Fishbot（或提醒the player手动按 F5）
5. Fishbot 会自动甩竿、玩 minigame、钓宝箱、体力低自动吃食物
6. 背包快满了就停下来，把鱼存箱子或卖掉

注意：钓鱼前确保背包有空位，鱼竿要在手上（用 /select 切换）

## 游戏内聊天（Chat V2）

游戏内按 ` 键打开聊天面板，支持两种模式：

### API Mode
直接在游戏内调LLM（DeepSeek/Claude/OpenAI/自定义），不需要CC。

### Channel Mode（CC连接）
```
Game ChatHud → POST → channel_server.py (:9000) → inbox file → CC Monitor
CC回复 → /chat/push (:7842) → Game ChatHud
```

#### CC端启动步骤
1. 后台启动channel server：`bash ~/source/NagiBridge/scripts/start_channel.sh`
2. Monitor监听消息：`tail -f ~/nagi/overlay_inbox.jsonl | grep --line-buffered text`
3. 用 `/chat/push` 回复：`curl -X POST http://0.0.0.0:7842/chat/push -d '{"sender":"Nagi","message":"Hello!"}'`

### 聊天面板操作
| 键 | 操作 |
|---|---|
| ` | 开/关聊天 |
| Enter | 发送 |
| Tab | 切换模式（输入框为空时） |
| Ctrl+V | 粘贴 |
| 滚轮 | 浏览历史 |

## 小游戏Bot

| 游戏 | 状态 | 说明 |
|---|---|---|
| 草原国王 (JotPK) | done | potential field自动操控 |
| 21点 (CalicoJack) | done | reflection读牌面+基础策略 |

用 `/minigame/state` 读状态，`/minigame/bot` 控制。

## 节日活动

用 `/festival` 读节日信息，`/festival/interact` 交互，`/festival/answer` 回答选项。
独立bot在 `mods/NagiFestivalBots/`。

## git 同步
代码更新后先 `git pull`，如果 DLL 变了就重启游戏。

## 参考
- 完整技能文档见 scripts/SKILLS.md
- 项目总览见 PROJECT.md
- 社区发布版见 README.md

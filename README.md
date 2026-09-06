# 麦垛 WheatStook

给星露谷用的 AI 聊天 + farmhand 操控模组。这一版是**完全原创的干净代码**，不依赖上游 `anqinou-art/NagiBridge` 的未授权代码。
> 致敬：本作是对 [anqinou-art/NagiBridge](https://github.com/anqinou-art/NagiBridge) 的**干净重写**，实现代码为原创、未借上游代码；上游仓库无 LICENSE（默认 all rights reserved），本作不受其约束。README.md 为主说明，中文标注见 [README.zh-CN.md](README.zh-CN.md)。
> 编写：代码由 **AI 代理（垛口 / a sentinel）** 编写，由 **BeadyTooth57254** 执导并持有版权。这是一份诚实披露：本项目代码为 AI 所写。
> 许可：以 **GNU AGPL-3.0** 发布（强 copyleft）；商用允许，但**衍生作品与网络运行**都须**保持开源并给出源码**（§13 网络条款）。版权归 BeadyTooth57254。详见 [LICENSE](LICENSE)。

## 构建 / 部署

```pwsh
dotnet build -c Release
```

构建会自动把 DLL 部署到游戏模组目录（`[AI聊天]麦垛 WheatStook` 文件夹，回车回车前请关掉游戏，否则文件被锁会提示失败——无害，手动拷 `bin\Release\net6.0\WheatStook.dll` 过去即可），并生成 zip。

## 配置

照 `config.example.json` 改你游戏里的 `config.json`。每个字段旁边都有 `_说明_*` 中文注释（模组会忽略 `_` 开头字段，只作说明）。也可以用 `wheatstook_help` 或【可选】GMCM 菜单改。

**字段**（默认值）：`Mode`(operit)、`HostPort`(58331)、`FarmhandPort`(58332)、`OperitBridgeUrl`(`http://127.0.0.1:8000`)、`OperitBridgeToken`、`forwardToOperitChat`(false)、`operitWebUrl`/`operitWebChatId`/`operitWebToken`、`forwardReadOperitReply`(false)、`operitForwardFormat`(`【星露谷·{sender}】{message}`)、`chunkSize`(5)、`readWindow`(tool)、`stateOutput`(text)、`enableModKnowledge`(true)、`sourceReadDepth`(intro)、`modWhitelist`/`modBlacklist`、`cacheModUsage`(true)、`includeMemoryInForward`(false)、`reactionEnabled`(false)、`enableAutoCompat`(false)、`compatOverrides`(null)、`keybindChatPanel`(OemTilde)、`keybindBridgeToggle`(F8)、`keybindHelp`(F1)。

> 真实地址（Operit 网页端、token）是隐私且会变，只用占位符，**绝不提交**。

## 自动兼容（默认关，检测到才开）

很多模组会**改变游戏世界**（加戒指槽、超大背包、自定义作物/灌木、新地区、职业循环、自动化等）。为了不让 AI 误当原版，麦垛内置一个**自动兼容层**：

- **默认关闭**（`enableAutoCompat: false`），你决定才开。
- 开启后，启动时用 **UniqueId**（不是文件夹名）检测这些模组，**哪个装了就自动激活哪个**的兼容 profile，并把**已激活列表写进 `/state` 的 `compat` 字段**，让 AI 知道这农场不是原版。
- 想单独强开/强关某个：`compatOverrides` 里按 UniqueId 写 `true`/`false`（优先级高于自动检测）。例：`{"bcmpinc.WearMoreRings": true, "spacechase0.BiggerBackpack": false}`。
- 新增的**全新功能摸**（自定义作物/新职业/新机器）需要针对单个模组写适配器；检测 + 开关框架已就位，适配器随你点名的模组逐个补。

### 数据驱动适配器：`CompatRules.json`（不用写代码，谁都能填）

想教 AI 认"原版没有的东西"，直接编辑模组目录里的 `CompatRules.json`（**带注释的 JSON，SMAPI 能读注释**）。每条规则 = 给某个模组的**某个东西**贴标签：

```json
{
  "Mod": "TntDove.PBB",         // 该模组的 UniqueID
  "Label": "可摘的浆果灌木",      // 给 AI 看的说明
  "MatchIdContains": "berry",   // 对象 ID 里含 berry
  "MatchName": "berry",         // 显示名里含 berry
  "Category": "berryBush",      // 语义标签 (AI 能懂)
  "Harvestable": true,          // 标为可采摘
  "Collectible": true           // 标为可捡取的采集物
}
```

- 生效前提：该模组已安装且兼容 profile **激活**（`enableAutoCompat` 开，或在 `compatOverrides` 里强开）。
- 匹配：`MatchIdContains` 配 `QualifiedItemId`、`MatchName` 配显示名；留空 = 不检查，两个都填 = 都要满足。
- 真实 id/名：用 `wheatstook_selftest` 或看 `/state`、`/surroundings` 把东西实际读成啥核一下再填准。
- 仓库里带了一份 `CompatRules.json` 示例（含宝可梦浆果 + 自定义酒桶两条），照着填就行。

### 内建模组识别（源码对齐，例：Tractor Mod）

对流行模组，麦垛已做**内建识别**（对齐其真实实现，不用你在规则里填）：

**地图渲染（`/ctx`）**：以 **AI 操控的 farmhand** 为中心（不是 host 玩家——他在另一台实例）。默认给**一块块画出来的 ASCII 方块图**：`P` 玩家、`C` 作物、`M` 机器、`T` 树、`N` NPC/拖拉机、`o` 物件、`#` 挡路、`.` 空地，外加同区域的**结构化格子数据**（每格 `x/y/可走/有什么`）。范围由 `radius` 参数决定（默认8，即 17×17）。**图片渲染**只要把 config 的 `stateOutput` 改成 `image` 就出 base64 BMP 图（默认关，`text`）。

- **Tractor Mod**：`/state` 报 `ridingTractor`（是否在拖拉机上）与 `mountName`；`/surroundings` 会把拖拉机**认成拖拉机**（`"tractor": true`）而不是普通 NPC。新增 `/tractor` 接口：`{op: "state"}` 查状态、`{op: "dismiss"}` 下拖拉机。
  > 诚实说明：Tractor Mod 的**召唤**在 farmhand 侧是 host 耦合的（它会校验消息发送者必须是它自己，我无法替代），所以 `/tractor` 对 summon 会明确回报"需要 host 生成"，不假装能行。
- **Better Crystalarium**（CP 扩展宝石复制机范围）：`/machines` 现在会把宝石复制机**认出来**（`"isCrystalarium": true`），并报出**正在复制的宝石**（`heldObject`/`heldObjectId`）、**好没好**（`readyForHarvest`）、**剩余分钟**（`minutesUntilReady`）。因为读的是**实际在机子里那颗宝石**，CP 加了再多种类也能看到。
  > 通用原则：**CP 模组改的是游戏数据，所以兼容的钥匙是"读运行时真实数据、不硬编码原版清单"**——前面物品/作物解析真名、以及本条的机器读取，都按这个走。
- **MoreGreenhouses**（CP 新增多个温室）：`/state` 的 `location.isGreenhouse` 会标出当前位置是不是温室（**全年可种**，AI 知道该在这儿种）；`/state.buildings` 列出农场地图上的建筑（含 CP 加的温室建筑，`isGreenhouse: true`）。依旧读实际状态/建筑数据，不加一个硬件清单。
- **BuildMoreCellars**（CP 更多酒窖）：`/state` 的 `location.isCellar` 会标出是不是酒窖（**橡木桶陈酿**饮品/奶酪的地方）；`/state.buildings` 会用 `isCellar: true` 标出 CP 加的酒窖门/建筑；酒窖里的 Cask（橡木桶）会被 `/machines` 认出来并报**正在陈酿的东西**（`heldObject`）和**好没好**。
- **Super Massive Greenhouse**（CP 超大温室，替换 `Maps/Greenhouse`）：因为替换的是**同一个位置的图**，位置名仍是 `Greenhouse`，所以 `isGreenhouse` 天然命中；`mapWidth`/`mapHeight` 会反映它是超大地图。
- **商店读取（通用，覆盖售卖/交易类模组）**：`/menu` 现在会在打开的**商店**里读出**在卖什么、单价、库存**（`shop.items`，每项带 `name`/`price`/`stock`）。因为读的是打开的 `ShopMenu`，所以 **Marnie's Auto-Petters**（CP 让玛尼卖自动抚摸机）、**Robin Sells Big Craftables**（CP 让罗宾卖大型制作物）、**Shop Tabs**（C# 加交易选项卡，底层商品数据不变）都自动被覆盖——不用逐个加清单。
  > **购买 `POST /buy {index, count}`**：对**当前打开的商店**按 `index`（`/menu` 里 `shop.items` 的下标）买入 `count` 件。先做只读校验（有货、钱够、是普通物品），再**入队到游戏刻**扣钱+塞背包，返回 `queued: true`；AI 之后用 `/state`（money/背包）核对到货。**建筑类商品（如罗宾建房子）不走这条**，得在商店 UI 里手动买。
  > 三个 C# 快捷键类模组的开关键（从它们 config 读到的默认值）：**Joja Express 按 `J` 开网购目录**、**Auto Break Geode 按住 `F` 自动破晶洞**。若你在它们 config 里改了键，告诉我一声即可。
- **附魔读取（通用，覆盖附魔类模组）**：`/state` 现在会报当前手持工具的 **`enchantments`**（一件工具上的全部附魔）。因为读的是 `tool.enchantments`，所以 **Pick Forge Enchantment**（`Dragoon23.ForgeEnchantment`，定向附魔——熔炉自动选中你配置的那个附魔）、**Many Enchantments**（`Stari.ManyEnchantments`，附魔冲突修复——允许一件工具叠多个附魔）都自动体现——AI 能看到"这把镐是 Swift"或"这武器叠了几个附魔"。
  > `/state` 还会报 **`forgeEnchantments`**——读 Pick Forge Enchantment 的 config.json，列出**每种工具 → 熔炉会自动附什么**（如 `{tool:"WaterCan", enchant:"Reaching"}`），让 AI 能**预判**去熔炉给某工具附魔的结果。


## 控制台命令

- `wheatstook_operit <话>`：直接把话发给 Operit 原生对话并读回（最快验证通道）。
- `wheatstook_mods [关键词]`：查已装模组（启动时建的索引）。
- `wheatstook_mem add|list|del|clear`：管理长期记忆（存 `wheatstook_memory.txt`，在模组文件夹里）。
- `wheatstook_selftest`：一条命令自检——配置 / Operit 是否启用 / 转发开关 / 模组库条数 / 记忆条数 / 服务器是否绑定，快速定位问题。
- `wheatstook_help`：显示命令与热键。

## 热键（游戏内）

- 反引号 `` ` ``：聊天面板。打开后可打字（字母/数字/空格/回车/退格），回车提交 → 转发给 Operit，回复显示在面板。
- **F8**：开关 Operit 转发（默认开）。
- **F1**：弹出面板并显示帮助。

## 测试清单

准备：先关掉游戏再构建部署；开一个会话（或联机 farmhand）。

1. **装/部署**：进游戏，控制台应无报错，日志里出现 `WheatStook clean-room build is ready.` 和 `Mod knowledge base built: N mods indexed.`。
2. **Operit 通道**：控制台 `wheatstook_operit 你好` → 应返回 operit 的完整回复（游戏内日志/面板）。
3. **聊天面板**：按反引号开面板，打一句话回车 → 面板显示"我: …"，之后出现"Operit: …"回复。
4. **转发开关**：按 F8 关，再发，面板只显示"我:…"，不转发；再按 F8 开恢复。
5. **模组库**：`wheatstook_mods`（空=全部）、`wheatstook_mods 关键词`。
6. **记忆**：`wheatstook_mem add 我喜欢吃土豆` → `list` 能看到；若 `includeMemoryInForward` 开，下次转发会带 `[回忆]`。
7. **帮助**：F1 或 `wheatstook_help`。
8. **联机 farmhand 控制**（需联机会话 + 起 MCP 桥）：
   - `GET http://localhost:58332/state` → 应回 `ok:true, worldReady:true` + player/location/time + `chunk`。
   - `GET http://localhost:58332/surroundings` → 分块后的 tiles。
   - `POST http://localhost:58332/move`（body `{"x":..,"y":..}`）→ farmhand 应走过去。
   - `POST http://localhost:58332/interact` / `/tool` / `/chat` 等按需试。

## 已知边界

- 聊天面板输入是 **ASCII**（英文/数字/空格）；中文 IME、多通道（MCP）转发待补。
- `stateOutput=image` 是后续规划（当前 text）。
- GMCM 菜单为**尽力兼容**：若你装有 GenericModConfigMenu，进游戏菜单能改配置；若版本接口不符则静默跳过，照旧手改 config.json。

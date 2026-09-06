# 麦垛 WheatStook — 自动兼容 (CompatLayer) 说明

> 一句话：**默认关；按 UniqueId 检测；读运行时真实数据；数据表达不了的新玩法才需要手写适配器。**

## 框架怎么工作

- **默认关闭**（`enableAutoCompat: false`）。你决定才开。
- 开启后，启动时用 **UniqueId**（不是文件夹名）检测已装模组，**装了就自动激活**它的 profile，并把**已激活列表写进 `/state` 的 `compat` 字段**（`CompatSummary`），让 AI 知道这农场不是原版。
- 想单个强开/强关：`compatOverrides` 按 UniqueId 写 `true`/`false`（优先级高于自动检测）。例：`{"bcmpinc.WearMoreRings": true, "spacechase0.BiggerBackpack": false}`。
- 控制台 **`wheatstook_compat`** 会打印：当前激活 profile、哪些是自动覆盖、哪些要手写适配器。

## 一、自动覆盖 — 不需要手写适配器

**钥匙是"读运行时真实数据 + 不硬编码原版清单"**。凡是**数据驱动、能把状态表达成游戏标准数据**的，自动就盖住了：

| 覆盖类型 | 怎么盖住的 | 覆盖的模组(示例) |
|---|---|---|
| 作物/植物 | 读 `QualifiedItemId`/显示名解出真名 | Content Patcher、Star Crops、More Flowers、Custom Bush… |
| 机器 | 按对象类型读取 | 任何加普通机器的 CP/C# 模组 |
| 宝石复制机 | 读机子里**实际**那颗宝石 + 剩余分钟 | Better Crystalarium |
| 商店/交易 | 读打开的 `ShopMenu`(卖啥/单价/库存) | Marnie's Auto-Petters、Robin Sells BC、Shop Tabs |
| 附魔 | 读 `tool.enchantments` + PickForge config | Pick Forge Enchantment、Many Enchantments |
| 温室/酒窖 | `isGreenhouse`/`isCellar` 标记 | MoreGreenhouses、Super Massive Greenhouse、BuildMoreCellars |
| 远程存取 | `remoteChestAccess` | Chests Anywhere、Remote Fridge、Resource Storage |
| 背包 | `backpackCapacity` = 真实 `MaxItems` | Bigger Backpack |
| 拖拉机 | `ridingTractor`/`mountName`/`isTractor` | Tractor Mod |
| 祝尼魔小屋 | `isJunimoHut` + 读 config 范围 | Better Junimos |
| 自动钓鱼 | 读 config 按键 + 自动关停规则 | Fishing Assistant |
| 传送点 | 扫名为 Obelisk 的物件 | Multiple Mini Obelisks |

**数据驱动规则（连代码都不用写）**：直接编辑模组目录的 **`CompatRules.json`**（带注释的 JSON，SMAPI 能读注释），给"原版没有的东西"贴标签（`MatchIdContains`/`MatchName`/`Category`/`Harvestable`/`Collectible`…）。仓库带了一份示例。**那条规则生效的前提**：该模组已装 + profile 激活（开 `enableAutoCompat` 或在 `compatOverrides` 强开）。

> 通用原则：**CP 模组改的是游戏数据，所以兼容的钥匙是"读真实数据"**，不是维护一张原版物品清单。换句话说——一个**新的 CP 内容包**只要是数据驱动的，机器/作物读取器**自动捕捉**，不用加 profile。

## 二、需要手写适配器 — 数据表达不了的全新玩法

当一个模组的**新机制"无法用游戏标准数据结构表达"**，AI 光靠通用读取器会**做错**，这时才需要**在源码里手写一个适配器**（加 profile + 专用读取器）。判据：

1. **全新职业/技能系统**：不只是改职业效果值，而是有一套 AI 得按规则操作的新行为。
2. **行为异常的自定义机器**：不是普通"放原料→出产物"的机器，而是有 AI 必须懂的特殊行为/状态机。
3. **新迷你游戏/特殊交互**：AI 需要专门逻辑去玩/触发。
4. **非数据驱动的新机制**：状态不在标准 `objects`/`terrainFeatures`/`buildings` 里，得靠反射/特殊 API 读。

**诚实举例**：现在仓库里**没有**这类"全新玩法"的适配器。当前 44 个内置 profile 全部是"通用读取器盖住"或"识别/标记让 AI 知道它在"，**没有**一个需要为新职业系统/新机器动作手写逻辑。所以**目前你不用手写任何适配器**——除非你装了上面那类模组，报名字给我，我按需补。

## 三、内置 profile 清单（44 个）

按**处理方式**分组（便于你看哪些真要做适配器）：

**深度行为（专用读取器）** — Automate、Better Junimos、Fishing Assistant、Bigger Backpack、Chests Anywhere、Remote Fridge、Resource Storage、Multiple Mini Obelisks、Tractor Mod、Better Crystalarium。

**通用读取器自动盖住（类别型）** — Marnie's Auto-Petters、Robin Sells BC、Shop Tabs、Pick Forge Enchantment、Many Enchantments、MoreGreenhouses、Super Massive Greenhouse、BuildMoreCellars、Content Patcher、Star Crops、More Flowers、Custom Bush、Custom Cask Mod、Joja Express、Auto Break Geode。

**识别/标记（让 AI 知道它在，这农场不是原版）** — Wear More Rings、Combine Many Rings、Walk Of Life - Rebirth、Ridgeside Village、Deep Woods、Control Tree、More Monsters、Supply Crates on Beach、Skull Cavern Elevator、Custom Spouse Rooms、They Stay With You、Platonic Relationships、Lets Move It、Harvest Seeds Continued、Increased Artifact Spots、Share Experience、Automatic Gates、Skillful Clothes、Unlimited Players。

## 四、如何添加

- **数据驱动（推荐，不用写代码）**：改 `CompatRules.json`。
- **内建识别**：对热门模组已内建（源码对齐），见 README 的"内建模组识别"。
- **全新玩法（需手写适配器）**：在 `CompatLayer.Profiles` 加一行 UniqueId→标签，再在 FarmhandServer 加一个读取该模组数据的函数。检测 + 开关框架已在位，缺的只是"那段读取它的逻辑"。

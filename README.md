# NagiBridge

Stardew Valley SMAPI mod — HTTP API for external control + in-game AI chat.

## Install (players)

> ### 📥 [**Download the latest release →**](https://github.com/anqinou-art/NagiBridge/releases/latest)
>
> Grab `NagiBridge.zip` from the link above — it already contains the compiled `NagiBridge.dll`. **You do not need to build anything.**

1. Install [SMAPI](https://smapi.io/)
2. Download `NagiBridge.zip` from [**Releases**](https://github.com/anqinou-art/NagiBridge/releases/latest) and unzip it
3. Move the unzipped `NagiBridge` folder into your `Stardew Valley/Mods/` folder
   (so you end up with `Stardew Valley/Mods/NagiBridge/NagiBridge.dll` + `manifest.json`)
4. Launch the game through SMAPI

First launch auto-generates `config.json`. The mod is built for **Stardew Valley 1.6+ / SMAPI 4.0+** and the same `.dll` works on **Windows, macOS and Linux**.

> **Cloning the source repo?** There is **no `.dll` in the source tree** — it ships only in [Releases](https://github.com/anqinou-art/NagiBridge/releases/latest). If you cloned/downloaded the code and SMAPI says it can't find `NagiBridge.dll`, download the release zip above, or build it yourself (see below).

## Build from source (developers)

Requires the [.NET SDK](https://dotnet.microsoft.com/download) (6.0+) and a local Stardew Valley install.

```bash
dotnet build -c Release
```

The project uses [`Pathoschild.Stardew.ModBuildConfig`](https://github.com/Pathoschild/SMAPI/blob/develop/docs/technical/mod-package.md), which **auto-detects your game folder** (Steam / GOG / Xbox on any OS) and copies the built mod straight into `Stardew Valley/Mods/NagiBridge/`. No paths to edit. If auto-detection fails, set a `GamePath` property or `GAME_PATH` environment variable pointing at your install.

## HTTP API

Game starts an HTTP server on `0.0.0.0:7842` (host) / `7843` (farmhand).

Full endpoint list: see [AGENTS.md](AGENTS.md)

---

## In-Game Chat

Press **`** (backtick, keyboard top-left) to open the chat panel.

### First Open — Mode Selection

```
┌─────────────────────────┐
│  Nagi Chat              │
│                         │
│  > API Mode             │
│    Channel Mode         │
│                         │
│  Up/Down = Select       │
│  Enter = OK             │
└─────────────────────────┘
```

- **API Mode** — Connect to an LLM API (Claude, DeepSeek, OpenAI, or any compatible endpoint). The AI chats with you in-game.
- **Channel Mode** — Connect to Claude Code via a local channel server. Claude Code controls the game and chats through the panel.

After selecting a mode, you'll be prompted to enter a **display name** for the AI (default: "Nagi").

### Switching Modes

Press **Tab** (when input is empty) in the chat panel to return to mode selection. Switch between API and Channel at any time.

---

### API Mode

For chatting with an LLM directly in-game. No external tools required.

#### Setup

1. Select **API Mode** → enter display name → press Enter
2. **API Key** — Paste your key with `Ctrl+V` (shown as `****abcd`)
3. **URL** — Auto-filled based on provider. Custom endpoints supported.
4. Press **Enter** to connect

#### Supported Providers

| Provider | URL | Model |
|----------|-----|-------|
| DeepSeek | `https://api.deepseek.com/v1/chat/completions` | `deepseek-chat` |
| Claude | `https://api.anthropic.com/v1/messages` | `claude-sonnet-4-6-20250514` |
| OpenAI | `https://api.openai.com/v1/chat/completions` | `gpt-4o` |
| Custom | Any OpenAI-compatible endpoint | Any model name |

The provider is auto-detected from the URL. Custom URLs use OpenAI-compatible format.

#### Config (config.json)

```json
{
  "Mode": "",
  "ApiProvider": "deepseek",
  "ApiUrl": "",
  "ApiKey": "",
  "Model": "deepseek-chat",
  "SystemPrompt": "You are a friendly AI companion in Stardew Valley...",
  "MaxHistoryMessages": 20
}
```

- **API key is saved locally** after first entry. Next launch auto-fills (masked with stars).
- **Chat history persists** across game restarts in `chat_history.json`.
- **SystemPrompt** — Customize the AI's personality.
- **MaxHistoryMessages** — How many turns to include in API calls (memory window).

#### Tool Calling Agent (Optional)

For LLMs that support tool calling (function calling), a standalone Python agent lets the AI actually play the game:

```bash
python scripts/tool_agent.py --provider deepseek --key sk-xxx --port 7842
```

The AI can use 17 game tools: `get_state`, `warp`, `move_to`, `use_tool`, `farm`, `mine`, `harvest`, etc.

See [scripts/tool_agent.py](scripts/tool_agent.py) for details.

---

### Channel Mode

For connecting to Claude Code (CC). CC controls the game via HTTP API and chats through the panel.

#### Architecture

```
Game ChatHud → POST → channel_server.py (:9000) → inbox file → CC Monitor → CC responds
                                                                               ↓
Game ChatHud ← /chat/push (:7842) ←────────────────────────────────────────────┘
```

#### Setup (Claude Code side)

Each new CC session needs two things:

**1. Start channel server:**
```bash
bash ~/source/NagiBridge/scripts/start_channel.sh
```
Or manually:
```bash
cd scripts && python channel_server.py &
```

**2. Start message monitor** (CC tool):
```
Monitor(description="stardew chat", persistent=true,
        command="tail -f ~/nagi/overlay_inbox.jsonl | grep --line-buffered text")
```

**3. Reply to player** via API:
```bash
curl -X POST http://0.0.0.0:7842/chat/push \
  -H "Content-Type: application/json" \
  -d '{"sender":"Nagi","message":"Hello!"}'
```

#### Config

```json
{
  "Mode": "cc",
  "ChannelServerUrl": "http://0.0.0.0:9000/chat"
}
```

When `Mode` is `"cc"`, the chat panel opens directly in Channel mode (skips mode selection).

---

### Chat Panel Controls

| Key | Action |
|-----|--------|
| **`** | Open / close chat panel |
| **Enter** | Send message |
| **Tab** | Switch mode (when input is empty) |
| **Ctrl+V** | Paste from clipboard |
| **Scroll** | Browse message history |

### Preview

When the chat panel is closed, the last 2 messages are shown as a preview above the toolbar (bottom-left corner).

---

## Automation Scripts

Python scripts for common farm tasks. Require `requests` package.

```bash
pip install requests
```

| Script | Usage |
|--------|-------|
| `farm_row.py` | `python farm_row.py 64 18 10 --seed "Melon Seeds" --rows 3` |
| `water_crops.py` | `python water_crops.py --port 7842` |
| `harvest.py` | `python harvest.py 60 17 70 20 --sell` |
| `mine_run.py` | `python mine_run.py --start-level 1 --max-levels 5` |
| `chop_trees.py` | `python chop_trees.py --count 10` |
| `clear_area.py` | `python clear_area.py 60 15 70 25` |
| `pet_animals.py` | `python pet_animals.py` |
| `keg_manager.py` | `python keg_manager.py --fruit "Ancient Fruit"` |
| `furnace_manager.py` | `python furnace_manager.py --ore "Copper Ore"` |
| `shop_buy.py` | `python shop_buy.py --items "493:10,491:6"` |

All scripts default to `--port 7842`. Add `PYTHONIOENCODING=utf-8` on Windows.

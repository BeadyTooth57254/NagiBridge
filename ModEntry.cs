using System.IO;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace WheatStook;

// Minimal GMCM API surface for the optional in-game config menu, matching the
// canonical GenericModConfigMenu 1.x shape (RegisterSimpleOption is non-generic
// and string-typed). GetApi is wrapped in try/catch so an API-shape mismatch is
// silently skipped rather than logging an alarming stack trace.
public interface IGenericModConfigMenuApi
{
    void RegisterModConfig(IManifest mod, Action revertToDefault, Action save);
    void UnregisterModConfig(IManifest mod);
    void RegisterLabel(IManifest mod, string name, string description);
    void RegisterSimpleOption(IManifest mod, string name, string description, Func<string> get, Action<string> set, string? fieldId = null, string? fieldTitle = null);
}

/// <summary>
/// Clean-room rewrite of NagiBridge. This is a fresh, original implementation
/// (no upstream code) built up feature by feature.
///
/// Currently implemented:
///   - config layer (ModConfig <-> config.json)
///   - Operit native-chat forward + optional read-back (OperitChatClient)
///   - a console command 'wheatstook_operit' to exercise the channel
/// </summary>
public class ModEntry : Mod
{
    private ModConfig? _config;
    private OperitChatClient? _operitChat;
    private FarmhandServer? _server;
    private ModKnowledgeBase? _mods;
    private CompatLayer? _compat;
    private List<CompatRule> _compatRules = new();
    private ChatHud? _chatHud;
    private MemoryStore? _memory;
    private ReactionLayer? _reaction;
    private SButton _chatKey = SButton.OemTilde;
    private SButton _bridgeKey = SButton.F8;
    private SButton _helpKey = SButton.F1;
    private bool _forwardEnabled = true;

    public override void Entry(IModHelper helper)
    {
        _config = helper.ReadConfig<ModConfig>();
        _operitChat = new OperitChatClient(_config!, Monitor);
        _chatHud = new ChatHud(Monitor);
        _chatHud.OnSubmit += OnChatSubmit;
        _chatKey = ParseKey(_config!.keybindChatPanel, SButton.OemTilde);
        _bridgeKey = ParseKey(_config!.keybindBridgeToggle, SButton.F8);
        _helpKey = ParseKey(_config!.keybindHelp, SButton.F1);
        var modDir = Path.GetDirectoryName(typeof(ModEntry).Assembly.Location) ?? ".";
        _memory = new MemoryStore(Path.Combine(modDir, "wheatstook_memory.txt"), Monitor);
        _reaction = new ReactionLayer(Monitor, SendAiMessage, s => _chatHud?.AddMessage(s), () => _config!.reactionEnabled);

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Display.RenderedHud += OnRenderedHud;

        helper.ConsoleCommands.Add(
            "wheatstook_operit",
            "Forward an in-game message to Operit's native chat and read back its reply. Usage: wheatstook_operit <text>",
            OnOperitCommand);

        helper.ConsoleCommands.Add(
            "wheatstook_mods",
            "Look up an installed mod from the knowledge base built at launch. Usage: wheatstook_mods <query> (empty lists all)",
            OnModsCommand);

        helper.ConsoleCommands.Add(
            "wheatstook_help",
            "Show 麦垛 (WheatStook) commands and keybinds.",
            OnHelpCommand);

        helper.ConsoleCommands.Add(
            "wheatstook_mem",
            "Manage long-term memory (the AI's persistent memories). Usage: wheatstook_mem <add <text> | list | del <text> | clear>",
            OnMemCommand);

        helper.ConsoleCommands.Add(
            "wheatstook_selftest",
            "Run a quick self-check and print a diagnostic summary (config, channels, knowledge base, memory, server).",
            OnSelfTestCommand);

        Monitor.Log($"WheatStook (clean-room) loaded. Mode={_config.Mode}, forwardToOperitChat={_config.forwardToOperitChat}", LogLevel.Info);
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        _mods = new ModKnowledgeBase(Monitor);
        _mods.BuildFrom(Helper.ModRegistry);
        _compat = new CompatLayer(_mods.UniqueIds, _config!, Monitor);
        _compat.Log();

        // Data-driven compat rules from CompatRules.json (comments allowed): only keep
        // rules whose mod profile is active, so they cost nothing when off.
        var rawRules = Helper.Data.ReadJsonFile<List<CompatRule>>("CompatRules.json") ?? new List<CompatRule>();
        _compatRules = rawRules.Where(r => !string.IsNullOrWhiteSpace(r.Mod) && _compat!.IsActive(r.Mod.Trim())).ToList();
        if (_compatRules.Count > 0)
            Monitor.Log($"CompatRules: {_compatRules.Count} 条识别规则生效.", LogLevel.Info);

        TryRegisterGmcm();
        Monitor.Log("WheatStook clean-room build is ready.", LogLevel.Info);
    }

    private void TryRegisterGmcm()
    {
        IGenericModConfigMenuApi? api = null;
        try
        {
            api = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        }
        catch (Exception ex)
        {
            Monitor.Log($"GMCM API unavailable ({ex.Message}); config edited in config.json.", LogLevel.Info);
            return;
        }
        if (api is null)
        {
            Monitor.Log("GMCM not installed; config edited in config.json.", LogLevel.Info);
            return;
        }
        var modManifest = Helper.ModRegistry.Get("BeadyTooth57254.WheatStook")?.Manifest;
        if (modManifest is null)
        {
            Monitor.Log("GMCM: could not resolve this mod's manifest; skipping menu.", LogLevel.Warn);
            return;
        }
        try
        {
            api.RegisterModConfig(
                modManifest,
                () => { _config = new ModConfig(); Helper.WriteConfig(_config!); ApplyConfig(); },
                () => { Helper.WriteConfig(_config!); ApplyConfig(); });
            api.RegisterLabel(modManifest, "麦垛 (WheatStook)", "AI 聊天 + farmhand 控制（干净封装）");
            api.RegisterSimpleOption(modManifest, "Mode", "聊天后端", () => _config!.Mode, v => _config!.Mode = v);
            api.RegisterSimpleOption(modManifest, "HostPort", "host 实例端口", () => _config!.HostPort.ToString(), v => _config!.HostPort = int.TryParse(v, out var hp) ? hp : _config!.HostPort);
            api.RegisterSimpleOption(modManifest, "FarmhandPort", "farmhand 实例端口", () => _config!.FarmhandPort.ToString(), v => _config!.FarmhandPort = int.TryParse(v, out var fp) ? fp : _config!.FarmhandPort);
            api.RegisterSimpleOption(modManifest, "forwardToOperitChat", "转发到 Operit 原生对话", () => _config!.forwardToOperitChat.ToString(), v => _config!.forwardToOperitChat = bool.TryParse(v, out var b) && b);
            api.RegisterSimpleOption(modManifest, "forwardReadOperitReply", "读回 Operit 回复", () => _config!.forwardReadOperitReply.ToString(), v => _config!.forwardReadOperitReply = bool.TryParse(v, out var b) && b);
            api.RegisterSimpleOption(modManifest, "includeMemoryInForward", "转发时带回忆", () => _config!.includeMemoryInForward.ToString(), v => _config!.includeMemoryInForward = bool.TryParse(v, out var b) && b);
            api.RegisterSimpleOption(modManifest, "reactionEnabled", "AI 对事件有反应", () => _config!.reactionEnabled.ToString(), v => _config!.reactionEnabled = bool.TryParse(v, out var b) && b);
            api.RegisterSimpleOption(modManifest, "chunkSize", "地图分块边长", () => _config!.chunkSize.ToString(), v => _config!.chunkSize = int.TryParse(v, out var cs) && cs > 0 ? cs : _config!.chunkSize);
            api.RegisterSimpleOption(modManifest, "readWindow", "读取窗口 (tool/beehouse/navigate/explore)", () => _config!.readWindow, v => _config!.readWindow = v);
            api.RegisterSimpleOption(modManifest, "stateOutput", "状态输出 (text/image/auto)", () => _config!.stateOutput, v => _config!.stateOutput = v);
            api.RegisterSimpleOption(modManifest, "keybindChatPanel", "聊天面板按键", () => _config!.keybindChatPanel, v => _config!.keybindChatPanel = v);
            api.RegisterSimpleOption(modManifest, "keybindBridgeToggle", "转发开关按键", () => _config!.keybindBridgeToggle, v => _config!.keybindBridgeToggle = v);
            api.RegisterSimpleOption(modManifest, "keybindHelp", "帮助按键", () => _config!.keybindHelp, v => _config!.keybindHelp = v);
            Monitor.Log("GMCM config menu registered.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Monitor.Log($"GMCM registration failed (best-effort): {ex.Message}", LogLevel.Error);
        }
    }

    private void ApplyConfig()
    {
        _chatKey = ParseKey(_config!.keybindChatPanel, SButton.OemTilde);
        _bridgeKey = ParseKey(_config!.keybindBridgeToggle, SButton.F8);
        _helpKey = ParseKey(_config!.keybindHelp, SButton.F1);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        // Start the farmhand control server once we're inside a session and this
        // instance is a co-op farmhand (not the main player). The host/chat side
        // is wired up separately (the in-game chat HUD brick).
        if (_server == null && Context.IsWorldReady && Game1.player != null && !Game1.player.IsMainPlayer)
        {
            _server = new FarmhandServer(_config!, Monitor, isHost: false, Helper);
            _server.CompatSummary = _compat?.Describe() ?? "";
            _server.CompatRules = _compatRules;
            _server.Start();
        }
        _server?.Tick();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        _server?.Stop();
        _server = null;
    }

    private void OnOperitCommand(string command, string[] args)
    {
        string text = string.Join(' ', args);
        if (string.IsNullOrWhiteSpace(text))
        {
            Monitor.Log("Usage: wheatstook_operit <text>", LogLevel.Info);
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                string sender = string.IsNullOrWhiteSpace(Game1.player?.Name) ? "宿主" : Game1.player!.Name;
                string? reply = await _operitChat!.SendAndReadBackAsync(text, sender);
                if (reply is null)
                    Monitor.Log("Operit channel: no reply (disabled, not configured, or fire-and-forget).", LogLevel.Info);
                else
                    Monitor.Log($"Operit reply:\n{reply}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Operit chat error: {ex.Message}", LogLevel.Error);
            }
        });
    }

    private void OnModsCommand(string command, string[] args)
    {
        string query = string.Join(' ', args);
        if (_mods is null)
        {
            Monitor.Log("Mod knowledge base not built yet (only after game launch).", LogLevel.Info);
            return;
        }
        var results = _mods.Search(query);
        if (results.Count == 0)
        {
            Monitor.Log($"No mods match '{query}'.", LogLevel.Info);
            return;
        }
        Monitor.Log($"{results.Count} mod(s) match '{query}':", LogLevel.Info);
        foreach (var m in results.Take(40))
            Monitor.Log($"  [{m.Name}] v{m.Version}{(m.IsContentPack ? " (content pack)" : "")} — {m.UniqueID} | {m.Author}", LogLevel.Info);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button == _chatKey)
        {
            _chatHud?.Toggle();
            return;
        }
        if (e.Button == _bridgeKey)
        {
            _forwardEnabled = !_forwardEnabled;
            Monitor.Log($"Operit forward {( _forwardEnabled ? "enabled" : "disabled")}.", LogLevel.Info);
            return;
        }
        if (e.Button == _helpKey)
        {
            ShowHelp();
            return;
        }
        // Keys are consumed by the panel while it is open (no key-suppression API here).
        _chatHud?.HandleKey(e.Button);
    }

    private static SButton ParseKey(string name, SButton fallback)
        => Enum.TryParse<SButton>(name, true, out var key) ? key : fallback;

    private void ShowHelp()
    {
        if (_chatHud is null) return;
        if (!_chatHud.IsOpen) _chatHud.Toggle();
        _chatHud.AddMessage("麦垛 帮助:");
        _chatHud.AddMessage($"  {_chatKey}: 聊天面板");
        _chatHud.AddMessage($"  {_bridgeKey}: 开关 Operit 转发 (当前 {( _forwardEnabled ? "开" : "关")})");
        _chatHud.AddMessage("  控制台: wheatstook_operit <话> / wheatstook_mods [关键词] / wheatstook_selftest / wheatstook_help");
    }

    private void OnHelpCommand(string command, string[] args)
    {
        Monitor.Log("麦垛 clean-room build — 命令:", LogLevel.Info);
        Monitor.Log($"  {_chatKey}: 游戏内聊天面板 (可与 Operit 对话)", LogLevel.Info);
        Monitor.Log($"  {_bridgeKey}: 开关 Operit 转发 (当前 {( _forwardEnabled ? "开" : "关")})", LogLevel.Info);
        Monitor.Log("  wheatstook_operit <话>: 直接把话发给 Operit 并读回", LogLevel.Info);
        Monitor.Log("  wheatstook_mods [关键词]: 查已装模组", LogLevel.Info);
        Monitor.Log("  wheatstook_selftest: 一键自检(配置/通道/知识库/记忆/服务器)", LogLevel.Info);
        Monitor.Log("  wheatstook_help: 显示本帮助", LogLevel.Info);
        Monitor.Log("  wheatstook_mem add <话> | list | del <话> | clear: 长期记忆", LogLevel.Info);
    }

    private void OnMemCommand(string command, string[] args)
    {
        if (_memory is null) { Monitor.Log("Memory not ready yet.", LogLevel.Info); return; }
        string op = args.Length > 0 ? args[0].ToLower() : "list";
        switch (op)
        {
            case "add":
                string addText = string.Join(' ', args.Skip(1));
                _memory.Add(addText);
                Monitor.Log($"Added memory ({_memory.Count} total): {addText}", LogLevel.Info);
                break;
            case "del":
            case "delete":
                _memory.Remove(string.Join(' ', args.Skip(1)));
                Monitor.Log($"Removed memory ({_memory.Count} total).", LogLevel.Info);
                break;
            case "clear":
                foreach (var m in _memory.Context().Split('\n'))
                    if (!string.IsNullOrWhiteSpace(m)) _memory.Remove(m);
                Monitor.Log("Memory cleared.", LogLevel.Info);
                break;
            case "list":
            default:
                Monitor.Log($"Memory ({_memory.Count}):", LogLevel.Info);
                foreach (var m in _memory.Context().Split('\n'))
                    if (!string.IsNullOrWhiteSpace(m)) Monitor.Log("  · " + m, LogLevel.Info);
                break;
        }
    }

    private void OnSelfTestCommand(string command, string[] args)
    {
        Monitor.Log("=== 麦垛 self-test ===", LogLevel.Info);
        Monitor.Log($"[config] Mode={_config?.Mode}  forwardToOperitChat={_config?.forwardToOperitChat}", LogLevel.Info);
        Monitor.Log($"[operit] enabled={(_operitChat?.IsEnabled ?? false)}", LogLevel.Info);
        Monitor.Log($"[forward] toggle={_forwardEnabled}  (toggle with {_bridgeKey})", LogLevel.Info);
        Monitor.Log($"[knowledge] {_mods?.Count ?? 0} mods indexed", LogLevel.Info);
        Monitor.Log($"[compat] {_compat?.Describe() ?? "(未构建)"}", LogLevel.Info);
        Monitor.Log($"[memory] {_memory?.Count ?? 0} entries", LogLevel.Info);
        Monitor.Log($"[server] {(_server is { IsStarted: true } s ? $"{s.Role} bound on port {s.BoundPort}" : "not running (starts when a co-op farmhand session is ready)")}", LogLevel.Info);
        Monitor.Log("[hit] 敲 wheatstook_operit 测试 试试转发读回", LogLevel.Info);
        Monitor.Log("=== end self-test ===", LogLevel.Info);
    }

    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        _chatHud?.Draw();
    }

    private void OnChatSubmit(string text)
    {
        if (!_forwardEnabled)
        {
            Monitor.Log($"(chat not forwarded; forward disabled by {_bridgeKey})", LogLevel.Info);
            return;
        }
        SendAiMessage(text);
    }

    private void SendAiMessage(string text)
    {
        Task.Run(async () =>
        {
            try
            {
                string sender = string.IsNullOrWhiteSpace(Game1.player?.Name) ? "宿主" : Game1.player!.Name;
                string forwardText = text;
                if (_config!.includeMemoryInForward && _memory is { Count: > 0 })
                    forwardText = $"[回忆]\n{_memory!.Context()}\n\n{text}";
                string? reply = await _operitChat!.SendAndReadBackAsync(forwardText, sender);
                if (reply != null)
                    _chatHud?.AddMessage("Operit: " + reply);
            }
            catch (Exception ex)
            {
                Monitor.Log($"Operit chat error: {ex.Message}", LogLevel.Error);
            }
        });
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        _reaction?.OnDayStarted();
    }
}

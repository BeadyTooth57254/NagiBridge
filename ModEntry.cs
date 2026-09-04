using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using Microsoft.Xna.Framework.Input;
using StardewValley.Menus;
using System.Runtime.InteropServices;
using xTile.Dimensions;
using StardewValley.Monsters;
using StardewValley.Minigames;

namespace NagiBridge;

public class ModEntry : Mod
{
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly Queue<Action> _mainThreadQueue = new();
    private readonly object _queueLock = new();
    private int _port;

    // Pathfinding state
    private Queue<Point>? _pathQueue;
    private int _pathTickCooldown;
    private string? _followTarget;
    private int _followTickCooldown;

    // Command queue state
    private Queue<Dictionary<string, object?>>? _commandQueue;
    private readonly List<object> _commandResults = new();
    private TaskCompletionSource<object>? _commandQueueTcs;
    private int _commandDelay;
    private bool _waitingForMove;
    private bool _waitingForBite;
    private int _biteTimeout;

    // Time freeze state
    private bool _timeFrozen;
    private int _frozenTime;

    // Alert queue for game/system feedback consumed by external agents.
    private readonly Queue<Dictionary<string, object?>> _alertQueue = new();
    private readonly Dictionary<string, DateTime> _lastAlertTimes = new();
    private readonly object _alertLock = new();
    private string? _lastMenuType;
    private string? _lastMenuText;
    private string? _lastEventId;
    private string? _lastEventText;
    private bool _lastStaminaLow;
    private bool _lastWaterEmpty;
    private bool _lastInventoryFull;

    private readonly PrairieKingBot _prairieKingBot = new();

    private readonly FlowerDanceBot _flowerDanceBot = new();
    private readonly LuauBot _luauBot = new();
    private readonly WinterStarBot _winterStarBot = new();
    private readonly MermaidBot _mermaidBot = new();
    private readonly SpiritsEveBot _spiritsEveBot = new();
    private readonly SpinningWheelBot _spinningWheelBot = new();
    private readonly EggHuntBot _eggHuntBot = new();

    private ChatHud? _chatHud;
    private ModConfig? _modConfig;
    private LlmClient? _llmClient;

    public override void Entry(IModHelper helper)
    {
        _modConfig = helper.ReadConfig<ModConfig>();
        _llmClient = new LlmClient(_modConfig, helper.DirectoryPath);

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.Display.RenderedHud += OnRenderedHud;
        helper.Events.Display.Rendered += OnRendered;
        helper.Events.Input.ButtonPressed += OnButtonPressed;

        _chatHud = new ChatHud(Monitor, OnChatSend, OnApiConfigured, OnChannelSelected);
        _chatHud.SetInitialState(_modConfig.Mode, _modConfig.ApiKey, _modConfig.ApiUrl);
    }

    private void OnApiConfigured(string apiKey, string apiUrl)
    {
        _modConfig!.ApiKey = apiKey;
        _modConfig.ApiUrl = apiUrl;
        _modConfig.Mode = "api";
        if (apiUrl.Contains("deepseek")) _modConfig.ApiProvider = "deepseek";
        else if (apiUrl.Contains("anthropic")) _modConfig.ApiProvider = "claude";
        else if (apiUrl.Contains("openai.com")) _modConfig.ApiProvider = "openai";
        else _modConfig.ApiProvider = "custom";
        _llmClient = new LlmClient(_modConfig, Helper.DirectoryPath);
        Helper.WriteConfig(_modConfig);
        Monitor.Log($"API configured, provider={_modConfig.ApiProvider}, url={apiUrl}", LogLevel.Info);
    }

    private void OnChannelSelected()
    {
        _modConfig!.Mode = "cc";
        Helper.WriteConfig(_modConfig);
        Monitor.Log($"Channel mode selected", LogLevel.Info);
    }

    private void OnChatSend(string text)
    {
        Task.Run(async () =>
        {
            try
            {
                if (_modConfig!.Mode.Equals("cc", StringComparison.OrdinalIgnoreCase))
                {
                    using var client = new HttpClient();
                    var json = JsonSerializer.Serialize(new { message = text });
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    await client.PostAsync(_modConfig.ChannelServerUrl, content);
                }
                else
                {
                    var reply = await _llmClient!.SendAsync(text);
                    _chatHud?.AddMessage(_chatHud.AiDisplayName, reply);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Chat send error: {ex.Message}", LogLevel.Debug);
            }
        });
    }

    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        _chatHud?.DrawHud(e.SpriteBatch);
    }

    private void OnRendered(object? sender, RenderedEventArgs e)
    {
        _chatHud?.DrawPanel(e.SpriteBatch);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button == StardewModdingAPI.SButton.OemTilde)
            Helper.Input.Suppress(e.Button);
        if (_chatHud?.IsOpen == true)
            Helper.Input.Suppress(e.Button);
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        StartServer();
        GenerateKeybindMap();
    }

    /// <summary>
    /// At game launch, run the (proven) Python extractor to rebuild mods_keybinds.json,
    /// so the AI's keybind map reflects the current config (including GMCM-set keys).
    /// Non-blocking and best-effort: a failure only logs a warning.
    /// </summary>
    private void GenerateKeybindMap()
    {
        var script = _modConfig?.KeybindsExtractScript;
        if (string.IsNullOrWhiteSpace(script) || !File.Exists(script))
        {
            Monitor.Log("Keybind extractor script not found; skipping map generation.", LogLevel.Info);
            return;
        }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("python", $"\"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            System.Diagnostics.Process.Start(psi);
            Monitor.Log("Keybind map generation started at game launch.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Monitor.Log($"Keybind map generation failed: {ex.Message}", LogLevel.Warn);
        }
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        ClearMovementState();
    }

    private void ClearMovementState()
    {
        _pathQueue = null;
        _pathTickCooldown = 0;
        _waitingForMove = false;
    }

    private void CenterViewportOnFarmer(Farmer farmer)
    {
        var loc = farmer.currentLocation;
        int viewW = Game1.viewport.Width;
        int viewH = Game1.viewport.Height;
        int maxX = Math.Max(0, loc.Map.DisplayWidth - viewW);
        int maxY = Math.Max(0, loc.Map.DisplayHeight - viewH);
        int vx = (int)farmer.Position.X - viewW / 2;
        int vy = (int)farmer.Position.Y - viewH / 2;

        Game1.viewport.X = Math.Max(0, Math.Min(maxX, vx));
        Game1.viewport.Y = Math.Max(0, Math.Min(maxY, vy));
    }

    private void EnqueueAlert(string type, string message, string severity = "info", string source = "bridge")
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var now = DateTime.UtcNow;
        var key = $"{type}:{message}";

        lock (_alertLock)
        {
            if (_lastAlertTimes.TryGetValue(key, out var last) && (now - last).TotalSeconds < 4)
                return;
            _lastAlertTimes[key] = now;

            _alertQueue.Enqueue(new Dictionary<string, object?>
            {
                ["timeUtc"] = now.ToString("O"),
                ["type"] = type,
                ["severity"] = severity,
                ["source"] = source,
                ["message"] = message
            });

            while (_alertQueue.Count > 100)
                _alertQueue.Dequeue();

            foreach (var stale in _lastAlertTimes.Where(p => (now - p.Value).TotalMinutes > 5).Select(p => p.Key).ToList())
                _lastAlertTimes.Remove(stale);
        }
    }

    private void CaptureAlerts()
    {
        var farmer = Game1.player;
        if (farmer == null)
            return;

        if (Game1.hudMessages != null)
        {
            foreach (var hud in Game1.hudMessages)
            {
                var text = hud.message;
                if (!string.IsNullOrWhiteSpace(text))
                    EnqueueAlert("hud", text, "info", "hud");
            }
        }

        bool staminaLow = farmer.MaxStamina > 0 && farmer.Stamina / farmer.MaxStamina < 0.15f;
        if (staminaLow && !_lastStaminaLow)
            EnqueueAlert("stamina_low", $"Stamina low: {farmer.Stamina:0}/{farmer.MaxStamina:0}", "warning", "state");
        else if (!staminaLow && _lastStaminaLow)
            EnqueueAlert("stamina_ok", $"Stamina recovered: {farmer.Stamina:0}/{farmer.MaxStamina:0}", "info", "state");
        _lastStaminaLow = staminaLow;

        var wateringCan = farmer.Items.OfType<WateringCan>().FirstOrDefault();
        bool waterEmpty = wateringCan != null && wateringCan.WaterLeft <= 0;
        if (waterEmpty && !_lastWaterEmpty)
            EnqueueAlert("water_empty", "Watering can is empty", "warning", "state");
        else if (!waterEmpty && _lastWaterEmpty)
            EnqueueAlert("water_refilled", "Watering can has water", "info", "state");
        _lastWaterEmpty = waterEmpty;

        int usedSlots = farmer.Items.Count(item => item != null);
        bool inventoryFull = usedSlots >= farmer.MaxItems;
        if (inventoryFull && !_lastInventoryFull)
            EnqueueAlert("inventory_full", $"Inventory full: {usedSlots}/{farmer.MaxItems}", "warning", "state");
        else if (!inventoryFull && _lastInventoryFull)
            EnqueueAlert("inventory_space", $"Inventory has space: {usedSlots}/{farmer.MaxItems}", "info", "state");
        _lastInventoryFull = inventoryFull;

        CaptureMenuAlerts();
        CaptureEventAlerts();
    }

    private void CaptureMenuAlerts()
    {
        var menu = Game1.activeClickableMenu;
        string? menuType = menu?.GetType().Name;
        string? menuText = null;

        if (menu is DialogueBox dialogue)
        {
            try { menuText = dialogue.getCurrentString(); } catch { }
        }
        else if (menu != null)
        {
            menuText = menuType;
        }

        if (menuType != _lastMenuType)
        {
            if (menuType == null)
                EnqueueAlert("menu_closed", "Menu closed", "info", "menu");
            else
                EnqueueAlert("menu_opened", $"Menu opened: {menuType}", "info", "menu");
            _lastMenuType = menuType;
            _lastMenuText = null;
        }

        if (!string.IsNullOrWhiteSpace(menuText) && menuText != _lastMenuText)
        {
            EnqueueAlert("menu_text", menuText, "info", "menu");
            _lastMenuText = menuText;
        }
    }

    private void CaptureEventAlerts()
    {
        var ev = Game1.currentLocation?.currentEvent;
        string? eventId = ev?.id;
        string? eventText = null;

        if (ev != null && Game1.activeClickableMenu is DialogueBox dialogue)
        {
            try { eventText = dialogue.getCurrentString(); } catch { }
        }

        if (eventId != _lastEventId)
        {
            if (eventId == null)
                EnqueueAlert("event_ended", "Event ended", "info", "event");
            else
                EnqueueAlert("event_started", $"Event started: {eventId}", "info", "event");
            _lastEventId = eventId;
            _lastEventText = null;
        }

        if (!string.IsNullOrWhiteSpace(eventText) && eventText != _lastEventText)
        {
            EnqueueAlert("event_text", eventText, "info", "event");
            _lastEventText = eventText;
        }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        _chatHud?.Update();

        // Drain main-thread action queue
        lock (_queueLock)
        {
            while (_mainThreadQueue.Count > 0)
            {
                try { _mainThreadQueue.Dequeue().Invoke(); }
                catch (Exception ex) { Monitor.Log($"Queued action error: {ex}", LogLevel.Error); }
            }
        }

        // Freeze time if paused
        if (_timeFrozen && Context.IsWorldReady)
            Game1.timeOfDay = _frozenTime;

        _prairieKingBot.Update(Monitor);

        if (Context.IsWorldReady && Game1.player != null)
        {
            GameTime time = Game1.currentGameTime;
            _mermaidBot.Update(time);

            if (Game1.currentSeason.Equals("spring", StringComparison.OrdinalIgnoreCase))
            {
                if (Game1.dayOfMonth == 13)
                    _eggHuntBot.Update(time);
                else if (Game1.dayOfMonth == 24)
                    _flowerDanceBot.Update(time);
            }
            else if (Game1.currentSeason.Equals("summer", StringComparison.OrdinalIgnoreCase) && Game1.dayOfMonth == 11)
            {
                _luauBot.Update(time);
            }
            else if (Game1.currentSeason.Equals("fall", StringComparison.OrdinalIgnoreCase))
            {
                if (Game1.dayOfMonth == 16)
                    _spinningWheelBot.Update(time);
                else if (Game1.dayOfMonth == 27)
                    _spiritsEveBot.Update(time);
            }
            else if (Game1.currentSeason.Equals("winter", StringComparison.OrdinalIgnoreCase) && Game1.dayOfMonth == 25)
            {
                _winterStarBot.Update(time);
            }

            CaptureAlerts();
        }

        // Process follow: keep re-pathing toward the follow target every ~15 ticks
        if (_followTarget != null && Context.IsWorldReady && Game1.player != null)
        {
            if (_followTickCooldown > 0)
                _followTickCooldown--;
            else
            {
                _followTickCooldown = 15;
                var followTile = ResolveFollowTile(_followTarget);
                if (followTile.HasValue)
                {
                    var farmer = Game1.player;
                    var gap = Math.Abs(followTile.Value.X - farmer.TilePoint.X)
                        + Math.Abs(followTile.Value.Y - farmer.TilePoint.Y);
                    if (gap > 1)
                    {
                        var path = FindPath(farmer.currentLocation, farmer.TilePoint, followTile.Value);
                        if (path != null && path.Count > 0)
                        {
                            _pathQueue = path;
                            _pathTickCooldown = 0;
                        }
                    }
                }
                // If the target is elsewhere / gone, keep following (no path) and retry later.
            }
        }

        // Process pathfinding movement
        if (_pathQueue != null && _pathQueue.Count > 0 && Context.IsWorldReady)
        {
            if (_pathTickCooldown > 0)
            {
                _pathTickCooldown--;
                return;
            }

            var next = _pathQueue.Peek();
            var farmer = Game1.player;
            var target = new Vector2(next.X * 64 + 32, next.Y * 64 + 32);
            var diff = target - farmer.Position;

            if (diff.Length() < 6f)
            {
                _pathQueue.Dequeue();
                _pathTickCooldown = 0;
            }
            else
            {
                // Set facing direction
                if (Math.Abs(diff.X) > Math.Abs(diff.Y))
                    farmer.FacingDirection = diff.X > 0 ? 1 : 3;
                else
                    farmer.FacingDirection = diff.Y > 0 ? 2 : 0;

                var speed = farmer.getMovementSpeed();
                if (diff.Length() < speed)
                    farmer.Position = target;
                else
                {
                    diff.Normalize();
                    farmer.Position += diff * speed;
                }
            }
        }

        // Process command queue
        if (_commandQueue != null && _commandQueue.Count > 0 && Context.IsWorldReady)
        {
            // Wait for delay between commands
            if (_commandDelay > 0)
            {
                _commandDelay--;
                return;
            }

            // Wait for move to complete before next command
            if (_waitingForMove)
            {
                if (_pathQueue != null && _pathQueue.Count > 0)
                    return; // still walking
                _waitingForMove = false;
                _commandDelay = 5; // small gap after arriving
                return;
            }

            // Wait for fish bite
            if (_waitingForBite)
            {
                _biteTimeout--;
                if (_biteTimeout <= 0)
                {
                    _waitingForBite = false;
                    _commandResults.Add(new { ok = false, action = "wait_for_bite", error = "Timed out waiting for bite" });
                    // Don't abort queue - let next commands handle it
                }
                else if (Game1.player.CurrentTool is FishingRod fishRod && fishRod.isNibbling)
                {
                    _waitingForBite = false;
                    _commandResults.Add(new { ok = true, action = "wait_for_bite", message = "Fish is biting!" });
                    _commandDelay = 2; // tiny delay before reeling
                }
                else
                    return; // keep waiting
                return;
            }

            var cmd = _commandQueue.Dequeue();
            var action = cmd.ContainsKey("action") && cmd["action"] is JsonElement ae
                ? ae.GetString() ?? "" : "";

            try
            {
                switch (action)
                {
                    case "move":
                    {
                        var x = cmd.ContainsKey("x") && cmd["x"] is JsonElement xe ? xe.GetInt32() : 0;
                        var y = cmd.ContainsKey("y") && cmd["y"] is JsonElement ye ? ye.GetInt32() : 0;
                        var farmer = Game1.player;
                        var path = FindPath(farmer.currentLocation, farmer.TilePoint, new Point(x, y));
                        _pathQueue = path ?? new Queue<Point>(new[] { new Point(x, y) });
                        _pathTickCooldown = 0;
                        _waitingForMove = true;
                        _commandResults.Add(new { ok = true, action = "move", x, y });
                        break;
                    }
                    case "face":
                    {
                        var dir = cmd.ContainsKey("direction") && cmd["direction"] is JsonElement de ? de.GetInt32() : 2;
                        Game1.player.FacingDirection = dir;
                        _commandResults.Add(new { ok = true, action = "face", direction = dir });
                        _commandDelay = 3;
                        break;
                    }
                    case "select":
                    {
                        var name = cmd.ContainsKey("name") && cmd["name"] is JsonElement ne ? ne.GetString() ?? "" : "";
                        var farmer = Game1.player;
                        var idx = -1;
                        for (int i = 0; i < farmer.Items.Count; i++)
                        {
                            if (farmer.Items[i] != null && farmer.Items[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                            { idx = i; break; }
                        }
                        if (idx >= 0)
                        {
                            farmer.CurrentToolIndex = idx;
                            _commandResults.Add(new { ok = true, action = "select", name, slot = idx });
                        }
                        else
                            _commandResults.Add(new { ok = false, action = "select", error = $"Item '{name}' not found" });
                        _commandDelay = 3;
                        break;
                    }
                    case "use":
                    {
                        var farmer = Game1.player;
                        var item = farmer.CurrentItem;
                        if (item is Tool)
                        {
                            farmer.BeginUsingTool();
                            _commandResults.Add(new { ok = true, action = "use", item = item.Name });
                        }
                        else if (item is StardewValley.Object obj)
                        {
                            var facingTile = GetFacingTile(farmer);
                            int px = (int)facingTile.X * 64;
                            int py = (int)facingTile.Y * 64;
                            bool placed = obj.placementAction(farmer.currentLocation, px, py, farmer);
                            if (placed)
                            {
                                farmer.reduceActiveItemByOne();
                                _commandResults.Add(new { ok = true, action = "placed", item = item.Name });
                            }
                            else
                                _commandResults.Add(new { ok = false, action = "use", error = $"Cannot use '{item.Name}' here" });
                        }
                        else
                            _commandResults.Add(new { ok = false, action = "use", error = "No usable item" });
                        _commandDelay = 15; // tool animation time
                        break;
                    }
                    case "interact":
                    {
                        var farmer = Game1.player;
                        var facingTile = GetFacingTile(farmer);
                        var acted = farmer.currentLocation.checkAction(
                            new Location((int)facingTile.X, (int)facingTile.Y), Game1.viewport, farmer);
                        _commandResults.Add(new { ok = true, action = "interact", triggered = acted });
                        _commandDelay = 10;
                        break;
                    }
                    case "wait":
                    {
                        var ticks = cmd.ContainsKey("ticks") && cmd["ticks"] is JsonElement te ? te.GetInt32() : 60;
                        _commandResults.Add(new { ok = true, action = "wait", ticks });
                        _commandDelay = ticks;
                        break;
                    }
                    case "warp":
                    {
                        var loc = cmd.ContainsKey("location") && cmd["location"] is JsonElement le ? le.GetString() ?? "" : "";
                        var wx = cmd.ContainsKey("x") && cmd["x"] is JsonElement wxe ? wxe.GetInt32() : 10;
                        var wy = cmd.ContainsKey("y") && cmd["y"] is JsonElement wye ? wye.GetInt32() : 10;
                        Game1.warpFarmer(loc, wx, wy, false);
                        _commandResults.Add(new { ok = true, action = "warp", location = loc, x = wx, y = wy });
                        _commandDelay = 30; // wait for warp to complete
                        break;
                    }
                    case "wait_for_bite":
                    {
                        var timeout = cmd.ContainsKey("timeout") && cmd["timeout"] is JsonElement to ? to.GetInt32() : 1800;
                        _waitingForBite = true;
                        _biteTimeout = timeout;
                        break;
                    }
                    case "key":
                    {
                        var keyName = cmd.ContainsKey("key") && cmd["key"] is JsonElement ke ? ke.GetString() ?? "confirm" : "confirm";
                        switch (keyName.ToLower())
                        {
                            case "confirm": case "action":
                                Game1.pressActionButton(Game1.input.GetKeyboardState(), Game1.input.GetMouseState(), Game1.input.GetGamePadState());
                                break;
                            case "skip": case "escape":
                                if (Game1.activeClickableMenu != null)
                                    Game1.activeClickableMenu.receiveKeyPress(Keys.Escape);
                                else
                                    Game1.activeClickableMenu?.exitThisMenu();
                                break;
                        }
                        _commandResults.Add(new { ok = true, action = "key", key = keyName });
                        _commandDelay = 10;
                        break;
                    }
                    default:
                        _commandResults.Add(new { ok = false, action, error = "Unknown action" });
                        break;
                }
            }
            catch (Exception ex)
            {
                _commandResults.Add(new { ok = false, action, error = ex.Message });
            }

            // All commands done? Return results
            if (_commandQueue.Count == 0)
            {
                _commandQueueTcs?.TrySetResult(new
                {
                    ok = true,
                    executed = _commandResults.Count,
                    results = _commandResults.ToArray()
                });
                _commandQueue = null;
            }
        }
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            var tcp = new TcpListener(IPAddress.Loopback, port);
            tcp.Start();
            tcp.Stop();
            return true;
        }
        catch { return false; }
    }

    private void StartServer()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(async () =>
        {
            // Auto-detect available port starting from 58331
            _listener = null;
            for (_port = 58331; _port < 58339; _port++)
            {
                try
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add($"http://+:{_port}/");
                    listener.Start();
                    _listener = listener;
                    Monitor.Log($"NagiBridge HTTP server started on port {_port}", LogLevel.Info);
                    break;
                }
                catch
                {
                    Monitor.Log($"Port {_port} unavailable, trying next...", LogLevel.Debug);
                }
            }

            if (_listener == null)
            {
                Monitor.Log("Failed to start HTTP server on any port (58331-58338)", LogLevel.Error);
                return;
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequest(ctx), token);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Monitor.Log($"Listener error: {ex.Message}", LogLevel.Warn);
                }
            }
        }, token);
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var method = ctx.Request.HttpMethod;

        try
        {
            object? result = path switch
            {
                "/status" => HandleStatus(),
                "/move" => HandleMove(ctx),
                "/tool" => HandleTool(ctx),
                "/interact" => HandleInteract(ctx),
                "/chat" => HandleChat(ctx),
                "/emote" => HandleEmote(ctx),
                "/state" => HandleState(),
                "/surroundings" => HandleSurroundings(ctx),
                "/alerts" => HandleAlerts(ctx),
                "/stop" => HandleStop(),
                "/map" => HandleMap(),
                "/ctx" => HandleCtx(ctx),
                "/buy" => HandleBuy(ctx),
                "/face" => HandleFace(ctx),
                "/select" => HandleSelect(ctx),
                "/use" => HandleUse(ctx),
                "/sleep" => HandleSleep(),
                "/wakeup" => HandleWakeup(),
                "/queue" => HandleQueue(ctx),
                "/key" => HandleKey(ctx),
                "/warp" => HandleWarp(ctx),
                "/position" => HandlePosition(ctx),
                "/follow" => HandleFollow(ctx),
                "/drop" => HandleDrop(ctx),
                "/area" => HandleArea(ctx),
                "/pause" => HandlePause(),
                "/resume" => HandleResume(),
                "/give" => HandleGive(ctx),
                "/money" => HandleMoney(ctx),
                "/refill" => HandleRefill(),
                "/heal" => HandleHeal(),
                "/ripen" => HandleRipen(ctx),
                "/sell" => HandleSell(ctx),
                "/harvest" => HandleHarvest(ctx),
                "/store" => HandleStore(ctx),
                "/chest" => HandleChest(ctx),
                "/placechest" => HandlePlaceChest(ctx),
                "/fishbot" => HandleFishbot(ctx),
                "/minigame/state" => HandleMinigameState(),
                "/minigame/bot" => HandleMinigameBot(ctx),
                "/menu" => HandleMenu(),
                "/menu/click" => HandleMenuClick(ctx),
                "/craft" => HandleCraft(ctx),
                "/machines" => HandleMachines(),
                "/animals" => HandleAnimals(),
                "/scan" => HandleScan(),
                "/festival" => HandleFestival(),
                "/festival/interact" => HandleFestivalInteract(ctx),
                "/festival/answer" => HandleFestivalAnswer(ctx),
                "/chat/push" => HandleChatPush(ctx),
                "/chat/history" => HandleChatHistory(),
                _ => throw new InvalidOperationException($"Unknown endpoint: {path}")
            };

            Respond(ctx, 200, result ?? new { ok = true });
        }
        catch (Exception ex)
        {
            Respond(ctx, 400, new { error = ex.Message });
        }
    }

    private static void Respond(HttpListenerContext ctx, int status, object body)
    {
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = false });
        var buf = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = buf.Length;
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        ctx.Response.Close();
    }

    private Dictionary<string, object?> ReadJson(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(body))
            return new Dictionary<string, object?>();
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(body) ?? new();
    }

    private T GetParam<T>(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val) || val == null)
            throw new InvalidOperationException($"Missing parameter: {key}");

        if (val is JsonElement je)
        {
            if (typeof(T) == typeof(int)) return (T)(object)je.GetInt32();
            if (typeof(T) == typeof(float)) return (T)(object)je.GetSingle();
            if (typeof(T) == typeof(string)) return (T)(object)(je.GetString() ?? "");
            if (typeof(T) == typeof(bool)) return (T)(object)je.GetBoolean();
        }

        return (T)Convert.ChangeType(val, typeof(T));
    }

    private T GetParamOr<T>(Dictionary<string, object?> dict, string key, T defaultValue)
    {
        if (!dict.TryGetValue(key, out var val) || val == null)
            return defaultValue;

        if (val is JsonElement je)
        {
            if (typeof(T) == typeof(int)) return (T)(object)je.GetInt32();
            if (typeof(T) == typeof(float)) return (T)(object)je.GetSingle();
            if (typeof(T) == typeof(string)) return (T)(object)(je.GetString() ?? "");
            if (typeof(T) == typeof(bool)) return (T)(object)je.GetBoolean();
        }

        return (T)Convert.ChangeType(val, typeof(T));
    }

    // --- Handlers ---

    private object HandleStatus()
    {
        return new
        {
            ok = true,
            server = "NagiBridge",
            version = "1.0.0",
            port = _port,
            worldReady = Context.IsWorldReady,
            isMultiplayer = Context.IsMultiplayer
        };
    }

    /// <summary>
    /// POST /move  { "x": 10, "y": 15 }
    /// Walks to tile (x, y) using simple straight-line pathfinding.
    /// </summary>
    private object HandleMove(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var tx = GetParam<int>(p, "x");
        var ty = GetParam<int>(p, "y");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        // Build simple path: current tile -> target tile (straight line, then adjust)
        var tcs = new TaskCompletionSource<object>();

        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var startTile = farmer.TilePoint;
            var path = FindPath(farmer.currentLocation, startTile, new Point(tx, ty));

            if (path == null || path.Count == 0)
            {
                // Fallback: just teleport-walk directly
                _pathQueue = new Queue<Point>();
                _pathQueue.Enqueue(new Point(tx, ty));
            }
            else
            {
                _pathQueue = path;
            }
            _pathTickCooldown = 0;

            tcs.SetResult(new { ok = true, message = $"Moving to ({tx},{ty}), steps={_pathQueue.Count}" });
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /tool  { "name": "Axe" } or { "name": "current" }
    /// Swings the specified tool (or current tool) once.
    /// </summary>
    private object HandleTool(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var name = GetParamOr(p, "name", "current");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();

        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;

            if (name != "current")
            {
                var tool = farmer.Items
                    .Where(i => i is Tool)
                    .Cast<Tool>()
                    .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

                if (tool == null)
                {
                    tcs.SetResult(new { ok = false, error = $"Tool '{name}' not found in inventory" });
                    return;
                }

                farmer.CurrentToolIndex = farmer.Items.IndexOf(tool);
            }

            farmer.BeginUsingTool();
            tcs.SetResult(new { ok = true, tool = farmer.CurrentTool?.Name ?? "none" });
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /interact  { }
    /// Triggers an action check at the tile the farmer is facing.
    /// Returns what's on the tile for context.
    /// </summary>
    private object HandleInteract(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();

        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            var facingTile = GetFacingTile(farmer);
            int ftx = (int)facingTile.X, fty = (int)facingTile.Y;
            var tileVec = new Vector2(ftx, fty);

            bool acted = loc.checkAction(
                new Location(ftx, fty),
                Game1.viewport,
                farmer
            );

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["actionTriggered"] = acted,
                ["facingTile"] = new { x = ftx, y = fty }
            };

            if (loc.objects.TryGetValue(tileVec, out var obj))
                result["object"] = ResolveObjectName(obj);
            if (loc.terrainFeatures.TryGetValue(tileVec, out var tf))
            {
                result["terrain"] = tf.GetType().Name;
                if (tf is HoeDirt dirt && dirt.crop != null && dirt.readyForHarvest())
                    result["harvestable"] = true;
            }
            var npc = loc.characters.FirstOrDefault(n => n.TilePoint.X == ftx && n.TilePoint.Y == fty);
            if (npc != null)
                result["npc"] = npc.Name;

            tcs.SetResult(result);
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /chat  { "message": "Hello!" }
    /// Sends a chat message visible to all players.
    /// </summary>
    private object HandleChat(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var message = GetParam<string>(p, "message");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        EnqueueMainThread(() =>
        {
            Game1.chatBox?.addMessage(message, Color.White);
            if (Context.IsMultiplayer)
            {
                Game1.chatBox?.setText(message);
                Game1.chatBox?.chatBox.RecieveCommandInput('\r');
            }
        });

        return new { ok = true, message };
    }

    private object HandleChatPush(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var sender = p.TryGetValue("sender", out var s) ? s?.ToString() ?? "Nagi" : "Nagi";
        var message = GetParam<string>(p, "message");
        _chatHud?.AddMessage(sender, message);
        return new { ok = true, sender, message };
    }

    private object HandleChatHistory()
    {
        // Returns empty if chatHud not initialized - safe fallback
        return new { ok = true, messages = Array.Empty<object>() };
    }

    /// <summary>
    /// POST /emote  { "id": 16 }
    /// Plays an emote animation on the farmer.
    /// Common emote IDs: 16=happy, 20=sad, 24=heart, 28=exclamation, 32=note, 36=sleep, 40=game, 52=angry, 56=laugh, 60=blush
    /// </summary>
    private object HandleEmote(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var id = GetParam<int>(p, "id");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        EnqueueMainThread(() =>
        {
            Game1.player.doEmote(id);
        });

        return new { ok = true, emoteId = id };
    }

    /// <summary>
    /// GET /state
    /// Returns comprehensive game state.
    /// </summary>
    private object HandleState()
    {
        if (!Context.IsWorldReady)
            return new { ok = true, worldReady = false };

        var farmer = Game1.player;
        var loc = farmer.currentLocation;

        var npcs = loc.characters
            .Select(n => new
            {
                name = n.Name,
                x = n.TilePoint.X,
                y = n.TilePoint.Y
            }).ToList();

        var inventory = farmer.Items
            .Where(i => i != null)
            .Select(i =>
            {
                var entry = new Dictionary<string, object?>
                {
                    ["name"] = i.Name,
                    ["stack"] = i.Stack,
                    ["category"] = i.getCategoryName()
                };
                if (i is WateringCan wc)
                {
                    entry["waterLeft"] = wc.WaterLeft;
                    entry["waterMax"] = wc.waterCanMax;
                }
                return entry;
            }).ToList();

        var menuInfo = (object?)null;
        if (Game1.activeClickableMenu != null)
        {
            var menuType = Game1.activeClickableMenu.GetType().Name;
            var dialogueText = "";
            if (Game1.activeClickableMenu is StardewValley.Menus.DialogueBox db)
            {
                try { dialogueText = db.getCurrentString() ?? ""; } catch { }
            }
            menuInfo = new
            {
                type = menuType,
                dialogue = string.IsNullOrEmpty(dialogueText) ? null : dialogueText
            };
        }

        var eventInfo = (object?)null;
        if (loc.currentEvent != null)
        {
            var ev = loc.currentEvent;
            string? evDialogue = null;
            if (Game1.activeClickableMenu is DialogueBox evDb)
            {
                try { evDialogue = evDb.getCurrentString(); } catch { }
            }
            eventInfo = new
            {
                id = ev.id,
                skippable = ev.skippable,
                message = evDialogue
            };
        }

        return new
        {
            ok = true,
            worldReady = true,
            player = new
            {
                name = farmer.Name,
                x = farmer.TilePoint.X,
                y = farmer.TilePoint.Y,
                health = farmer.health,
                maxHealth = farmer.maxHealth,
                stamina = farmer.Stamina,
                maxStamina = farmer.MaxStamina,
                money = farmer.Money,
                currentTool = farmer.CurrentTool?.Name,
                facingDirection = farmer.FacingDirection,
                isMoving = _pathQueue != null && _pathQueue.Count > 0,
                fishing = farmer.CurrentTool is FishingRod rod ? new
                {
                    isCasting = rod.isTimingCast,
                    isFishing = rod.isFishing,
                    isNibbling = rod.isNibbling,
                    isReeling = rod.isReeling,
                    hit = rod.hit
                } : null
            },
            location = new
            {
                name = loc.Name,
                mapWidth = loc.Map.DisplayWidth / 64,
                mapHeight = loc.Map.DisplayHeight / 64
            },
            time = new
            {
                timeOfDay = Game1.timeOfDay,
                dayOfMonth = Game1.dayOfMonth,
                season = Game1.currentSeason,
                year = Game1.year
            },
            activeMenu = menuInfo,
            activeEvent = eventInfo,
            npcs,
            inventory
        };
    }

    /// <summary>
    /// GET /surroundings  ?radius=10
    /// Returns tile info around the player: passability, objects, terrain features, buildings, NPCs.
    /// </summary>
    private object HandleSurroundings(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var qs = ctx.Request.QueryString;
        int radius = 10;
        if (int.TryParse(qs["radius"], out var r) && r > 0 && r <= 30)
            radius = r;

        var farmer = Game1.player;
        var loc = farmer.currentLocation;
        var cx = farmer.TilePoint.X;
        var cy = farmer.TilePoint.Y;

        var tiles = new List<object>();

        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int tx = cx + dx, ty = cy + dy;
                if (tx < 0 || ty < 0) continue;
                var mapW = loc.Map.DisplayWidth / 64;
                var mapH = loc.Map.DisplayHeight / 64;
                if (tx >= mapW || ty >= mapH) continue;

                var tileVec = new Vector2(tx, ty);
                var passable = loc.isTilePassable(tileVec);

                string? objName = null;
                if (loc.objects.TryGetValue(tileVec, out var obj))
                    objName = ResolveObjectName(obj);

                string? terrainName = null;
                bool diggable = loc.doesTileHaveProperty(tx, ty, "Diggable", "Back") != null;
                bool watered = false;
                string? cropName = null;
                int cropPhase = -1;
                bool harvestable = false;

                if (loc.terrainFeatures.TryGetValue(tileVec, out var tf))
                {
                    terrainName = tf.GetType().Name;
                    if (tf is HoeDirt dirt)
                    {
                        terrainName = "HoeDirt";
                        watered = dirt.state.Value == 1;
                        if (dirt.crop != null)
                        {
                            cropName = ResolveCropName(dirt.crop);
                            cropPhase = dirt.crop.currentPhase.Value;
                            harvestable = dirt.readyForHarvest();
                        }
                    }
                    else if (tf is Tree tree)
                    {
                        terrainName = $"Tree:{tree.treeType.Value}";
                    }
                    else if (tf is GiantCrop gc)
                    {
                        terrainName = "GiantCrop";
                    }
                }

                string? resourceName = null;
                var clump = loc.resourceClumps.FirstOrDefault(c =>
                    c.Tile == tileVec || (tx >= c.Tile.X && tx < c.Tile.X + c.width.Value
                    && ty >= c.Tile.Y && ty < c.Tile.Y + c.height.Value));
                if (clump != null)
                    resourceName = clump.parentSheetIndex.Value switch
                    {
                        600 => "LargeStump",
                        602 => "LargeLog",
                        622 => "MeteoriteOre",
                        672 => "LargeBoulder",
                        752 => "LargeBoulder",
                        754 => "LargeBoulder",
                        _ => $"Clump:{clump.parentSheetIndex.Value}"
                    };

                bool hasInfo = !passable || objName != null || terrainName != null
                    || resourceName != null || diggable || cropName != null;
                if (hasInfo)
                {
                    var tile = new Dictionary<string, object?> { ["x"] = tx, ["y"] = ty, ["passable"] = passable };
                    if (diggable) tile["diggable"] = true;
                    if (objName != null) tile["object"] = objName;
                    if (terrainName != null) tile["terrain"] = terrainName;
                    if (resourceName != null) tile["resource"] = resourceName;
                    if (cropName != null)
                    {
                        tile["crop"] = cropName;
                        tile["cropPhase"] = cropPhase;
                        tile["harvestable"] = harvestable;
                    }
                    if (watered) tile["watered"] = true;
                    tiles.Add(tile);
                }
            }
        }

        var nearbyNpcs = loc.characters
            .Where(n => !(n is Monster) && Math.Abs(n.TilePoint.X - cx) <= radius && Math.Abs(n.TilePoint.Y - cy) <= radius)
            .Select(n => new { name = n.Name, x = n.TilePoint.X, y = n.TilePoint.Y })
            .ToList();

        var nearbyMonsters = loc.characters
            .OfType<Monster>()
            .Where(m => Math.Abs(m.TilePoint.X - cx) <= radius && Math.Abs(m.TilePoint.Y - cy) <= radius)
            .Select(m => new { name = m.Name, x = m.TilePoint.X, y = m.TilePoint.Y, health = m.Health, maxHealth = m.MaxHealth })
            .ToList();

        var nearbyFarmers = Game1.getOnlineFarmers()
            .Where(f => f != farmer && f.currentLocation == loc
                && Math.Abs(f.TilePoint.X - cx) <= radius && Math.Abs(f.TilePoint.Y - cy) <= radius)
            .Select(f => new { name = f.Name, x = f.TilePoint.X, y = f.TilePoint.Y })
            .ToList();

        return new
        {
            ok = true,
            center = new { x = cx, y = cy },
            radius,
            location = loc.Name,
            tiles,
            npcs = nearbyNpcs,
            monsters = nearbyMonsters,
            farmers = nearbyFarmers
        };
    }

    /// <summary>
    /// GET /alerts ?peek=true
    /// Returns queued game/system alerts. By default this drains the queue.
    /// </summary>
    private object HandleAlerts(HttpListenerContext ctx)
    {
        var qs = ctx.Request.QueryString;
        bool peek = bool.TryParse(qs["peek"], out var p) && p;

        lock (_alertLock)
        {
            var alerts = _alertQueue.ToList();
            if (!peek)
                _alertQueue.Clear();

            return new
            {
                ok = true,
                count = alerts.Count,
                alerts
            };
        }
    }

    /// <summary>
    /// POST /face  { "direction": 2 }
    /// Sets the farmer's facing direction. 0=up, 1=right, 2=down, 3=left
    /// </summary>
    private object HandleFace(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var dir = GetParam<int>(p, "direction");
        if (dir < 0 || dir > 3)
            throw new InvalidOperationException("direction must be 0-3 (up/right/down/left)");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            Game1.player.FacingDirection = dir;
            tcs.SetResult(new { ok = true, direction = dir });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /select  { "name": "Parsnip Seeds" }
    /// Selects an inventory item by name (sets it as the active toolbar slot).
    /// </summary>
    private object HandleSelect(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var name = GetParam<string>(p, "name");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var idx = -1;
            for (int i = 0; i < farmer.Items.Count; i++)
            {
                if (farmer.Items[i] != null &&
                    farmer.Items[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }

            if (idx < 0)
            {
                tcs.SetResult(new { ok = false, error = $"Item '{name}' not found in inventory" });
                return;
            }

            farmer.CurrentToolIndex = idx;
            tcs.SetResult(new { ok = true, selected = name, slot = idx });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /use  { "force": false }
    /// Uses the currently held item with pre-validation.
    /// Tools: checks if facing tile is appropriate (hoe→diggable empty, wateringcan→HoeDirt, axe→tree/stump, pickaxe→stone).
    /// Placeables: checks tile is clear. Pass force=true to skip validation.
    /// </summary>
    private object HandleUse(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var p = ReadJson(ctx);
        var force = GetParamOr(p, "force", false);

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var item = farmer.CurrentItem;
            if (item == null)
            {
                tcs.SetResult(new { ok = false, error = "No item selected" });
                return;
            }

            var facingTile = GetFacingTile(farmer);
            var loc = farmer.currentLocation;
            int ftx = (int)facingTile.X, fty = (int)facingTile.Y;
            var tileVec = new Vector2(ftx, fty);

            if (item is Tool tool && !force)
            {
                var validation = ValidateToolUse(tool, loc, tileVec, ftx, fty);
                if (validation != null)
                {
                    tcs.SetResult(new { ok = false, error = validation,
                        tile = new { x = ftx, y = fty }, tool = tool.Name });
                    return;
                }
            }

            if (item is Tool)
            {
                farmer.BeginUsingTool();
                tcs.SetResult(new { ok = true, action = "tool", item = item.Name,
                    tile = new { x = ftx, y = fty } });
            }
            else if (item is StardewValley.Object obj)
            {
                int px = ftx * 64, py = fty * 64;
                bool placed = obj.placementAction(loc, px, py, farmer);
                if (placed)
                {
                    farmer.reduceActiveItemByOne();
                    tcs.SetResult(new { ok = true, action = "placed", item = item.Name,
                        tile = new { x = ftx, y = fty } });
                }
                else
                {
                    tcs.SetResult(new { ok = false, error = $"Cannot place '{item.Name}' here",
                        tile = new { x = ftx, y = fty } });
                }
            }
            else
            {
                tcs.SetResult(new { ok = false, error = $"Cannot use '{item.Name}' (unsupported item type)" });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private string? ValidateToolUse(Tool tool, GameLocation loc, Vector2 tileVec, int tx, int ty)
    {
        bool hasObj = loc.objects.ContainsKey(tileVec);
        loc.terrainFeatures.TryGetValue(tileVec, out var tf);
        bool diggable = loc.doesTileHaveProperty(tx, ty, "Diggable", "Back") != null;

        switch (tool)
        {
            case Hoe:
                if (tf is HoeDirt)
                    return "Tile already tilled";
                if (hasObj)
                    return $"Tile blocked by object: {loc.objects[tileVec].Name}";
                if (!diggable)
                    return "Tile is not diggable";
                return null;

            case WateringCan:
                if (tf is not HoeDirt dirt)
                    return "No tilled soil here — till first";
                if (dirt.state.Value == 1)
                    return "Already watered";
                return null;

            case Axe:
                bool hasTree = tf is Tree;
                bool hasStump = loc.resourceClumps.Any(c =>
                    (c.parentSheetIndex.Value == 600 || c.parentSheetIndex.Value == 602)
                    && tx >= c.Tile.X && tx < c.Tile.X + c.width.Value
                    && ty >= c.Tile.Y && ty < c.Tile.Y + c.height.Value);
                bool hasTwig = hasObj && loc.objects[tileVec].Name == "Twig";
                if (!hasTree && !hasStump && !hasTwig)
                    return "Nothing to chop here";
                return null;

            case Pickaxe:
                bool hasStone = hasObj && loc.objects[tileVec].Name == "Stone";
                bool hasBoulder = loc.resourceClumps.Any(c =>
                    (c.parentSheetIndex.Value == 672 || c.parentSheetIndex.Value == 752 || c.parentSheetIndex.Value == 754 || c.parentSheetIndex.Value == 622)
                    && tx >= c.Tile.X && tx < c.Tile.X + c.width.Value
                    && ty >= c.Tile.Y && ty < c.Tile.Y + c.height.Value);
                if (!hasStone && !hasBoulder && tf is not HoeDirt)
                    return "Nothing to break here";
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// GET /map
    /// Returns buildings, warps, NPCs, and other farmers for the current location.
    /// Provides everything needed for long-range pathfinding and navigation.
    /// </summary>
    private object HandleMap()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            var mapWidth = loc.Map.DisplayWidth / 64;
            var mapHeight = loc.Map.DisplayHeight / 64;

            // Buildings (Farm, etc.)
            var buildings = new List<object>();
            if (loc is Farm farm)
            {
                foreach (var b in farm.buildings)
                {
                    var entry = new Dictionary<string, object?>
                    {
                        ["type"] = b.buildingType.Value,
                        ["x"] = b.tileX.Value,
                        ["y"] = b.tileY.Value,
                        ["width"] = b.tilesWide.Value,
                        ["height"] = b.tilesHigh.Value
                    };
                    if (b.humanDoor.Value != Point.Zero || b.humanDoor.Value != default)
                    {
                        entry["doorX"] = b.tileX.Value + b.humanDoor.X;
                        entry["doorY"] = b.tileY.Value + b.humanDoor.Y;
                    }
                    buildings.Add(entry);
                }
            }

            // Warps (exits/entrances to other maps)
            var warps = loc.warps
                .Select(w => new
                {
                    x = w.X,
                    y = w.Y,
                    targetLocation = w.TargetName,
                    targetX = w.TargetX,
                    targetY = w.TargetY
                }).ToList();

            // All NPCs in current location
            var npcs = loc.characters
                .Select(n => new
                {
                    name = n.Name,
                    x = n.TilePoint.X,
                    y = n.TilePoint.Y
                }).ToList();

            // All other farmers in current location
            var farmers = Game1.getOnlineFarmers()
                .Where(f => f != farmer && f.currentLocation == loc)
                .Select(f => new
                {
                    name = f.Name,
                    x = f.TilePoint.X,
                    y = f.TilePoint.Y
                }).ToList();

            // Animals (if on farm or animal building interior)
            var animals = new List<object>();
            if (loc is Farm farmLoc)
            {
                foreach (var a in farmLoc.animals.Values)
                    animals.Add(new { name = a.Name, type = a.type.Value, x = a.TilePoint.X, y = a.TilePoint.Y });
            }
            else if (loc is AnimalHouse ah)
            {
                foreach (var a in ah.animals.Values)
                    animals.Add(new { name = a.Name, type = a.type.Value, x = a.TilePoint.X, y = a.TilePoint.Y });
            }

            tcs.SetResult(new
            {
                ok = true,
                player = new { x = farmer.TilePoint.X, y = farmer.TilePoint.Y },
                location = new
                {
                    name = loc.Name,
                    width = mapWidth,
                    height = mapHeight
                },
                buildings,
                warps,
                npcs,
                farmers,
                animals
            });
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// GET /ctx ?radius=8&grid=true
    /// Renders the area around the player as a compact text/ASCII map (AI-friendly),
    /// plus structured tile details. The grid is over [cx-R, cx+R] x [cy-R, cy+R],
    /// bounded to the current map. Symbols: P player, F other farmer, N npc, M monster,
    /// B building, o object, c crop, C harvestable crop, t tree, g giant crop, r resource
    /// clump, # blocked, . open.
    /// </summary>
    private object HandleCtx(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var qs = ctx.Request.QueryString;
        int radius = 8;
        if (int.TryParse(qs["radius"], out var r) && r > 0 && r <= 20)
            radius = r;

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            var cx = farmer.TilePoint.X;
            var cy = farmer.TilePoint.Y;
            var mapW = loc.Map.DisplayWidth / 64;
            var mapH = loc.Map.DisplayHeight / 64;

            int x0 = Math.Max(0, cx - radius), x1 = Math.Min(mapW - 1, cx + radius);
            int y0 = Math.Max(0, cy - radius), y1 = Math.Min(mapH - 1, cy + radius);

            var grid = new StringBuilder((x1 - x0 + 1) * (y1 - y0 + 1));
            var tiles = new List<object>();
            var npcs = new List<object>();
            var monsters = new List<object>();
            var farmers = new List<object>();
            var buildings = new List<object>();

            // Building footprints (farm buildings) -> 'B' (blocked)
            var buildingTiles = new HashSet<Point>();
            if (loc is Farm farm)
            {
                foreach (var b in farm.buildings)
                    for (int bx = b.tileX.Value; bx < b.tileX.Value + b.tilesWide.Value; bx++)
                        for (int by = b.tileY.Value; by < b.tileY.Value + b.tilesHigh.Value; by++)
                            buildingTiles.Add(new Point(bx, by));
            }

            for (int dy = y0; dy <= y1; dy++)
            {
                if (dy > y0) grid.Append('\n');
                for (int dx = x0; dx <= x1; dx++)
                {
                    var tileVec = new Vector2(dx, dy);
                    var passable = loc.isTilePassable(tileVec);

                    // Look up the tile's object/terrain once (mod-aware names resolved below).
                    StardewValley.Object? tileObj = null;
                    loc.objects.TryGetValue(tileVec, out tileObj);
                    TerrainFeature? tileTf = null;
                    loc.terrainFeatures.TryGetValue(tileVec, out tileTf);

                    char ch;
                    if (dx == cx && dy == cy) ch = 'P';
                    else if (buildingTiles.Contains(new Point(dx, dy))) ch = 'B';
                    else if (loc.characters.OfType<Monster>().Any(m => m.TilePoint.X == dx && m.TilePoint.Y == dy))
                        ch = 'M';
                    else if (loc.characters.Any(n => !(n is Monster) && n.TilePoint.X == dx && n.TilePoint.Y == dy))
                        ch = 'N';
                    else if (Game1.getOnlineFarmers().Any(f => f != farmer && f.currentLocation == loc && f.TilePoint.X == dx && f.TilePoint.Y == dy))
                        ch = 'F';
                    else if (tileObj != null)
                        ch = 'o';
                    else if (tileTf != null)
                        ch = tileTf switch
                        {
                            HoeDirt dirt when dirt.crop != null && dirt.readyForHarvest() => 'C',
                            HoeDirt when (tileTf as HoeDirt).crop != null => 'c',
                            Tree => 't',
                            GiantCrop => 'g',
                            _ => '.'
                        };
                    else if (loc.resourceClumps.Any(c => c.Tile == tileVec
                        || (dx >= c.Tile.X && dx < c.Tile.X + c.width.Value && dy >= c.Tile.Y && dy < c.Tile.Y + c.height.Value)))
                        ch = 'r';
                    else if (!passable) ch = '#';
                    else ch = '.';

                    grid.Append(ch);

                    // Structured detail for notable tiles (compact)
                    string? objectName = null, terrainName = null, cropName = null, resourceName = null;
                    bool harvestable = false;
                    if (tileObj != null)
                        objectName = ResolveObjectName(tileObj);
                    if (tileTf != null)
                    {
                        terrainName = tileTf.GetType().Name;
                        if (tileTf is HoeDirt dirt && dirt.crop != null)
                        {
                            cropName = ResolveCropName(dirt.crop);
                            harvestable = dirt.readyForHarvest();
                        }
                    }
                    var clump = loc.resourceClumps.FirstOrDefault(c => c.Tile == tileVec
                        || (dx >= c.Tile.X && dx < c.Tile.X + c.width.Value && dy >= c.Tile.Y && dy < c.Tile.Y + c.height.Value));
                    if (clump != null) resourceName = clump.parentSheetIndex.Value switch
                    {
                        600 => "LargeStump", 602 => "LargeLog", 622 => "MeteoriteOre",
                        672 or 752 or 754 => "LargeBoulder", _ => $"Clump:{clump.parentSheetIndex.Value}"
                    };
                    if (objectName != null || cropName != null || resourceName != null || terrainName != null)
                    {
                        var d = new Dictionary<string, object?> { ["x"] = dx, ["y"] = dy };
                        if (objectName != null) d["object"] = objectName;
                        if (terrainName != null) d["terrain"] = terrainName;
                        if (cropName != null) { d["crop"] = cropName; d["harvestable"] = harvestable; }
                        if (resourceName != null) d["resource"] = resourceName;
                        tiles.Add(d);
                    }
                }
            }

            foreach (var n in loc.characters.Where(n => !(n is Monster)
                && n.TilePoint.X >= x0 && n.TilePoint.X <= x1 && n.TilePoint.Y >= y0 && n.TilePoint.Y <= y1))
                npcs.Add(new { name = n.Name, x = n.TilePoint.X, y = n.TilePoint.Y });
            foreach (var m in loc.characters.OfType<Monster>()
                .Where(m => m.TilePoint.X >= x0 && m.TilePoint.X <= x1 && m.TilePoint.Y >= y0 && m.TilePoint.Y <= y1))
                monsters.Add(new { name = m.Name, x = m.TilePoint.X, y = m.TilePoint.Y, health = m.Health, maxHealth = m.MaxHealth });
            foreach (var f in Game1.getOnlineFarmers()
                .Where(f => f != farmer && f.currentLocation == loc
                    && f.TilePoint.X >= x0 && f.TilePoint.X <= x1 && f.TilePoint.Y >= y0 && f.TilePoint.Y <= y1))
                farmers.Add(new { name = f.Name, x = f.TilePoint.X, y = f.TilePoint.Y });
            if (loc is Farm f2)
                foreach (var b in f2.buildings)
                    buildings.Add(new { type = b.buildingType.Value, x = b.tileX.Value, y = b.tileY.Value });

            tcs.SetResult(new
            {
                ok = true,
                location = loc.Name,
                mapWidth = mapW,
                mapHeight = mapH,
                center = new { x = cx, y = cy },
                origin = new { x = x0, y = y0 },
                radius,
                legend = "P player, F other farmer, N npc, M monster, B building, o object, c crop, C harvestable, t tree, g giant crop, r resource clump, # blocked, . open",
                grid = grid.ToString(),
                tiles,
                npcs,
                monsters,
                farmers,
                buildings
            });
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Best-effort display name for a placed object, falling back to its raw Name.
    /// </summary>
    private static string ResolveObjectName(StardewValley.Object obj)
    {
        try
        {
            if (!string.IsNullOrEmpty(obj.DisplayName)) return obj.DisplayName;
            return obj.Name;
        }
        catch { return obj.Name; }
    }

    /// <summary>
    /// Resolve a crop's harvest item to its display name (vanilla and modded crops),
    /// falling back to the harvest index string.
    /// </summary>
    private static string ResolveCropName(Crop crop)
    {
        try
        {
            string hay = crop.indexOfHarvest.Value; // item id (numeric string or qualified)
            var qid = int.TryParse(hay, out _) ? "(O)" + hay : hay;
            var item = ItemRegistry.Create(qid);
            return item?.Name ?? hay;
        }
        catch { return crop.indexOfHarvest.Value.ToString(); }
    }

    /// <summary>
    /// POST /buy  { "id": "472", "quantity": 5 }  or  { "id": "(O)472", "quantity": 5 }
    /// Buys an item: deducts gold, adds item to inventory.
    /// Optional "price" param to override per-unit cost; otherwise uses the item's default sale price * 2 (shop markup).
    /// </summary>
    private object HandleBuy(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var rawId = GetParam<string>(p, "id");
        var quantity = GetParamOr(p, "quantity", 1);
        var priceOverride = GetParamOr(p, "price", -1);

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                // Qualify the item ID if needed (e.g. "472" -> "(O)472")
                var qualifiedId = rawId.StartsWith("(") ? rawId : ItemRegistry.QualifyItemId(rawId);
                if (qualifiedId == null)
                {
                    tcs.SetResult(new { ok = false, error = $"Unknown item ID: {rawId}" });
                    return;
                }

                // Create a test item to get its info
                var testItem = ItemRegistry.Create(qualifiedId, 1);
                if (testItem == null)
                {
                    tcs.SetResult(new { ok = false, error = $"Cannot create item: {qualifiedId}" });
                    return;
                }

                // Calculate price: override > default (salePrice * 2 as shop markup)
                int unitPrice = priceOverride >= 0
                    ? priceOverride
                    : (testItem is StardewValley.Object obj ? obj.salePrice() * 2 : 100);
                int totalCost = unitPrice * quantity;

                var farmer = Game1.player;
                if (farmer.Money < totalCost)
                {
                    tcs.SetResult(new { ok = false, error = $"Not enough gold. Need {totalCost}g, have {farmer.Money}g",
                        need = totalCost, have = farmer.Money });
                    return;
                }

                // Check inventory space
                int freeSlots = 0;
                for (int i = 0; i < farmer.MaxItems; i++)
                {
                    if (i >= farmer.Items.Count || farmer.Items[i] == null)
                        freeSlots++;
                }
                if (freeSlots < 1)
                {
                    tcs.SetResult(new { ok = false, error = "Inventory full! Please clear backpack before buying.",
                        freeSlots = 0 });
                    EnqueueAlert("inventory_full", "Cannot buy: inventory is full. Clear backpack first.", "warning", "buy");
                    return;
                }

                // Create the actual item and add to inventory
                var item = ItemRegistry.Create(qualifiedId, quantity);
                farmer.Money -= totalCost;
                farmer.addItemByMenuIfNecessary(item);

                tcs.SetResult(new
                {
                    ok = true,
                    bought = item.Name,
                    quantity,
                    unitPrice,
                    totalCost,
                    remainingGold = farmer.Money
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /sleep
    /// Warps the farmer to their bed and triggers sleep (end of day).
    /// </summary>
    private object HandleSleep()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var farmer = Game1.player;

                // Find home: try homeLocation, then scan all locations for a cabin belonging to this farmer
                var homeName = farmer.homeLocation.Value;
                GameLocation homeLoc = null;
                if (!string.IsNullOrEmpty(homeName))
                    homeLoc = Game1.getLocationFromName(homeName);

                if (homeLoc == null)
                {
                    // Scan for cabin with this farmer's unique ID
                    foreach (var loc in Game1.locations)
                    {
                        if (loc is StardewValley.Locations.Cabin cabin && cabin.owner == farmer)
                        {
                            homeLoc = cabin;
                            homeName = cabin.Name;
                            break;
                        }
                    }
                }

                // Fallback to FarmHouse for host
                if (homeLoc == null)
                {
                    homeLoc = Game1.getLocationFromName("FarmHouse");
                    homeName = "FarmHouse";
                }

                if (homeLoc == null)
                {
                    tcs.SetResult(new { ok = false, error = "Cannot find home location" });
                    return;
                }

                var bedX = 10;
                var bedY = 6;

                var needsWarp = farmer.currentLocation.Name != homeLoc.Name;
                if (needsWarp)
                {
                    Game1.warpFarmer(homeName, bedX, bedY, false);
                }

                // Longer delay for farmhand warp sync
                var delay = needsWarp ? 3000 : 500;
                DelayedAction.functionAfterDelay(() =>
                {
                    var f = Game1.player;
                    f.isInBed.Value = true;
                    f.sleptInTemporaryBed.Value = false;
                    f.currentLocation.answerDialogueAction("Sleep_Yes", Array.Empty<string>());

                    DelayedAction.functionAfterDelay(() =>
                    {
                        if (Game1.activeClickableMenu != null)
                        {
                            Game1.player.currentLocation.answerDialogueAction("Sleep_Yes", Array.Empty<string>());
                            Game1.pressActionButton(Game1.input.GetKeyboardState(), Game1.input.GetMouseState(),
                                Game1.input.GetGamePadState());
                        }
                    }, 1000);
                }, delay);

                tcs.SetResult(new { ok = true, action = "sleeping", home = homeName, bed = $"{bedX},{bedY}" });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /wakeup
    /// After sleeping / new day, walks the farmer out of their cabin to the farm.
    /// Returns current location and position.
    /// </summary>
    private object HandleWakeup()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;

            // Find any warp out of current indoor location
            var warp = loc.warps.FirstOrDefault();
            if (warp != null)
            {
                // Directly warp the farmer - more reliable than walking
                Game1.warpFarmer(warp.TargetName, warp.TargetX, warp.TargetY, false);
                tcs.SetResult(new
                {
                    ok = true,
                    action = "warped",
                    from = loc.Name,
                    target = warp.TargetName,
                    x = warp.TargetX,
                    y = warp.TargetY
                });
            }
            else
            {
                tcs.SetResult(new { ok = true, action = "already_outside", location = loc.Name,
                    x = farmer.TilePoint.X, y = farmer.TilePoint.Y });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /stop
    /// Cancels current movement.
    /// </summary>
    /// <summary>
    /// POST /queue  [{"action":"move","x":60,"y":17},{"action":"select","name":"Hoe"},{"action":"face","direction":2},{"action":"use"},...]
    /// Executes a sequence of commands automatically. Supported actions: move, face, select, use, interact, wait.
    /// Returns all results when the queue finishes.
    /// <summary>
    /// POST /key  { "key": "confirm" }
    /// Simulates a key press. Used to advance dialogue, confirm menus, skip cutscenes.
    /// Supported keys: confirm (action button), cancel (back/menu), skip (escape)
    /// </summary>
    /// <summary>
    /// POST /warp  { "location": "Beach", "x": 20, "y": 4 }
    /// Teleports the farmer to any game location. If x/y omitted, warps to default entry point.
    /// Common locations: Farm, Town, Beach, Mountain, Forest, Mine, BusStop, Desert, FishShop
    /// </summary>
    private object HandleWarp(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var location = GetParam<string>(p, "location");
        var x = GetParamOr(p, "x", -1);
        var y = GetParamOr(p, "y", -1);

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var shopLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "SeedShop", "FishShop", "Blacksmith", "ScienceHouse", "AnimalShop", "Saloon", "AdventureGuild", "Hospital", "HatShop", "DesertTrade", "QiGemShop" };
        if (shopLocations.Contains(location) && Game1.player.freeSpotsInInventory() == 0)
            return new { ok = false, error = "Inventory full! Clear backpack before going to a shop.", freeSlots = 0 };

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var targetLoc = Game1.getLocationFromName(location);
                if (targetLoc == null)
                {
                    tcs.SetResult(new { ok = false, error = $"Location '{location}' not found" });
                    return;
                }

                ClearMovementState();

                // If no coordinates given, try to find a reasonable entry point
                if (x < 0 || y < 0)
                {
                    // Use the first warp that targets this location from current map, or default center
                    var farmer = Game1.player;
                    var curWarps = farmer.currentLocation.warps;
                    var matchWarp = curWarps.FirstOrDefault(w => w.TargetName == location);
                    if (matchWarp != null)
                    {
                        Game1.warpFarmer(location, matchWarp.TargetX, matchWarp.TargetY, false);
                    }
                    else
                    {
                        // Default: warp to center-ish of map
                        var mw = targetLoc.Map.DisplayWidth / 64;
                        var mh = targetLoc.Map.DisplayHeight / 64;
                        Game1.warpFarmer(location, mw / 2, mh / 2, false);
                    }
                }
                else
                {
                    var farmer = Game1.player;
                    if (farmer.currentLocation.Name == location)
                    {
                        farmer.Position = new Vector2(x, y) * Game1.tileSize;
                        CenterViewportOnFarmer(farmer);
                    }
                    else
                    {
                        Game1.warpFarmer(location, x, y, false);
                    }
                }

                var f = Game1.player;
                tcs.SetResult(new
                {
                    ok = true,
                    action = "warped",
                    requested = new { location, x, y },
                    actual = new { location = f.currentLocation.Name, x = f.TilePoint.X, y = f.TilePoint.Y }
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /position { "x": 10, "y": 15 }
    /// Sets the farmer position on the current map and centers the camera.
    /// </summary>
    private object HandlePosition(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var x = GetParam<int>(p, "x");
        var y = GetParam<int>(p, "y");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            ClearMovementState();
            var farmer = Game1.player;
            farmer.Position = new Vector2(x, y) * Game1.tileSize;
            CenterViewportOnFarmer(farmer);
            tcs.SetResult(new
            {
                ok = true,
                action = "positioned",
                location = farmer.currentLocation.Name,
                x = farmer.TilePoint.X,
                y = farmer.TilePoint.Y
            });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleKey(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var p = ReadJson(ctx);
        var key = GetParamOr(p, "key", "confirm");
        var count = GetParamOr(p, "count", 1);

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                for (int i = 0; i < count; i++)
                {
                    switch (key.ToLower())
                    {
                        case "confirm":
                        case "action":
                            if (Game1.currentMinigame != null)
                            {
                                Game1.currentMinigame.receiveKeyPress(Keys.Enter);
                                keybd_event(0x0D, 0, 0, UIntPtr.Zero);
                                System.Threading.Thread.Sleep(50);
                                keybd_event(0x0D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                            }
                            else if (Game1.activeClickableMenu is DialogueBox dialogueBox)
                            {
                                dialogueBox.receiveLeftClick(0, 0);
                            }
                            else if (Game1.activeClickableMenu != null)
                            {
                                Game1.activeClickableMenu.receiveLeftClick(
                                    Game1.activeClickableMenu.xPositionOnScreen + Game1.activeClickableMenu.width / 2,
                                    Game1.activeClickableMenu.yPositionOnScreen + Game1.activeClickableMenu.height / 2);
                            }
                            else if (Game1.currentLocation?.currentEvent != null)
                            {
                                Game1.currentLocation.currentEvent.receiveActionPress(0, 0);
                            }
                            else if (Game1.input != null)
                            {
                                Game1.pressActionButton(Game1.input.GetKeyboardState(), Game1.input.GetMouseState(),
                                    Game1.input.GetGamePadState());
                            }
                            break;
                        case "ok":
                            if (Game1.activeClickableMenu != null)
                            {
                                var okBtn = Game1.activeClickableMenu.GetType()
                                    .GetField("okButton", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?
                                    .GetValue(Game1.activeClickableMenu) as ClickableTextureComponent;
                                if (okBtn != null)
                                {
                                    Game1.activeClickableMenu.receiveLeftClick(
                                        okBtn.bounds.Center.X, okBtn.bounds.Center.Y);
                                }
                                else
                                {
                                    Game1.activeClickableMenu.exitThisMenu();
                                }
                            }
                            break;
                        case "menu":
                            if (Game1.activeClickableMenu != null)
                                Game1.activeClickableMenu.receiveKeyPress(Keys.Escape);
                            else
                                Game1.activeClickableMenu = new GameMenu();
                            break;
                        case "cancel":
                        case "back":
                            if (Game1.activeClickableMenu != null)
                                Game1.activeClickableMenu.receiveKeyPress(Keys.Escape);
                            else if (Game1.input != null)
                                Game1.pressUseToolButton();
                            break;
                        case "skip":
                        case "escape":
                            if (Game1.currentLocation?.currentEvent != null)
                            {
                                Game1.currentLocation.currentEvent.skipped = true;
                                Game1.currentLocation.currentEvent.skipEvent();
                            }
                            else
                            {
                                Game1.currentMinigame?.receiveKeyPress(Keys.Escape);
                                if (Game1.activeClickableMenu != null)
                                    Game1.activeClickableMenu.receiveKeyPress(Keys.Escape);
                            }
                            break;
                        default:
                            // Support optional modifier chords like "shift+enter", "ctrl+r", "alt+1".
                            var parts = key.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length == 0)
                            {
                                tcs.SetResult(new { ok = false, error = $"Unsupported key: {key}" });
                                return;
                            }
                            var mainKey = parts[^1];
                            var vk = KeyToVirtualKey(mainKey);
                            if (!vk.HasValue)
                            {
                                tcs.SetResult(new { ok = false, error = $"Unsupported key: {key}" });
                                return;
                            }
                            if (parts.Length > 1)
                            {
                                // Modifier chord: press modifiers down, main key, then release.
                                var mods = new List<byte>();
                                bool valid = true;
                                foreach (var m in parts.Take(parts.Length - 1))
                                {
                                    var mv = m.ToLowerInvariant() switch
                                    {
                                        "ctrl" or "control" or "leftctrl" or "rightctrl"
                                            or "leftcontrol" or "rightcontrol" => (byte)0x11,
                                        "shift" or "leftshift" or "rightshift" => (byte)0x10,
                                        "alt" or "leftalt" or "rightalt" => (byte)0x12,
                                        "win" or "windows" or "cmd" => (byte)0x5B,
                                        _ => (byte)0
                                    };
                                    if (mv == 0) { valid = false; break; }
                                    mods.Add(mv);
                                }
                                if (!valid)
                                {
                                    tcs.SetResult(new { ok = false, error = $"Unsupported modifier in key: {key}" });
                                    return;
                                }
                                foreach (var mv in mods) keybd_event(mv, 0, 0, UIntPtr.Zero);
                                System.Threading.Thread.Sleep(30);
                                keybd_event(vk.Value, 0, 0, UIntPtr.Zero);
                                System.Threading.Thread.Sleep(40);
                                keybd_event(vk.Value, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                                System.Threading.Thread.Sleep(30);
                                for (int mi = mods.Count - 1; mi >= 0; mi--)
                                {
                                    keybd_event(mods[mi], 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                                    System.Threading.Thread.Sleep(20);
                                }
                            }
                            else
                            {
                                // Plain key: OS-level injection (covers most mod keybinds listening to
                                // Game1.input / key events), then forward to an active minigame/menu.
                                if (Game1.currentMinigame != null)
                                {
                                    var mk = ParseXnaKey(mainKey);
                                    if (mk.HasValue) Game1.currentMinigame.receiveKeyPress(mk.Value);
                                }
                                else if (Game1.activeClickableMenu != null)
                                {
                                    var mk = ParseXnaKey(mainKey);
                                    if (mk.HasValue) Game1.activeClickableMenu.receiveKeyPress(mk.Value);
                                }
                                keybd_event(vk.Value, 0, 0, UIntPtr.Zero);
                                System.Threading.Thread.Sleep(50);
                                keybd_event(vk.Value, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                            }
                            break;
                    }
                }
                tcs.SetResult(new { ok = true, key, count });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Map a key string to a Windows virtual-key code (VK). Handles single letters,
    /// digits, printable ASCII, named keys, and F1-F24. Returns null if unknown.
    /// </summary>
    private static byte? KeyToVirtualKey(string keyRaw)
    {
        if (string.IsNullOrWhiteSpace(keyRaw)) return null;
        var key = keyRaw.Trim();

        // F1-F24
        if (key.Length >= 2 && (key[0] == 'F' || key[0] == 'f'))
        {
            if (int.TryParse(key.Substring(1), out var f) && f >= 1 && f <= 24)
                return (byte)(0x70 + f - 1);
        }

        // SMAPI digit keys: D0-D9 and NumPad0-9 / numpad operators
        if (key.Length == 2 && (key[0] == 'D' || key[0] == 'd') && char.IsDigit(key[1]))
            return (byte)(0x30 + (key[1] - '0'));
        if (key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(key.Substring(6), out var np) && np >= 0 && np <= 9)
                return (byte)(0x60 + np);
            return key.ToLowerInvariant() switch
            {
                "numpadplus" => 0x6B, "numpadminus" => 0x6D, "numpadmultiply" => 0x6A,
                "numpaddivide" => 0x6F, "numpaddecimal" => 0x6E, _ => null
            };
        }

        // Single printable ASCII char -> VK (letters / digits / punctuation)
        if (key.Length == 1)
        {
            char c = key[0];
            if (c >= 'a' && c <= 'z') return (byte)(char.ToUpperInvariant(c));
            if (c >= 'A' && c <= 'Z') return (byte)c;
            if (c >= '0' && c <= '9') return (byte)c;
            return c switch
            {
                ' ' => 0x20,
                '\r' or '\n' or '\t' => 0x0D,
                ',' => 0xBC, '.' => 0xBE, '/' => 0xBF, ';' => 0xBA, '\'' => 0xDE,
                '[' => 0xDB, ']' => 0xDD, '\\' => 0xDC, '-' => 0xBD, '=' => 0xBB,
                '`' => 0xC0,
                _ => null
            };
        }

        // Named keys (Windows names + SMAPI SButton names, case-insensitive)
        return key.ToLowerInvariant() switch
        {
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "space" => 0x20,
            "tab" => 0x09,
            "back" or "backspace" => 0x08,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "insert" or "ins" => 0x2D,
            "delete" or "del" => 0x2E,
            "capslock" => 0x14,
            "scrolllock" => 0x91,
            "pause" => 0x13,
            // SMAPI Oem / punctuation names
            "oemcomma" or "comma" => 0xBC,
            "oemperiod" or "period" => 0xBE,
            "oemquestion" or "slash" => 0xBF,
            "oemsemicolon" or "semicolon" => 0xBA,
            "oemquotes" or "apostrophe" => 0xDE,
            "oemopenbrackets" => 0xDB,
            "oemclosebrackets" => 0xDD,
            "oempipe" or "backslash" or "oembackslash" => 0xDC,
            "oemminus" or "minus" => 0xBD,
            "oemplus" or "equals" => 0xBB,
            "oemtilde" or "grave" => 0xC0,
            "oem8" => 0xDF,
            // Modifiers are also valid alone as a main key (e.g. GMCM open-menu = RightControl)
            "ctrl" or "control" or "leftctrl" or "rightctrl" or "leftcontrol" or "rightcontrol" => 0x11,
            "shift" or "leftshift" or "rightshift" => 0x10,
            "alt" or "leftalt" or "rightalt" => 0x12,
            _ => null
        };
    }

    /// <summary>
    /// Resolve a key string to an XNA Keys value for forwarding to a minigame/menu.
    /// Tries the XNA enum name first, then derives D0-D9 and letter keys.
    /// </summary>
    private static Keys? ParseXnaKey(string key)
    {
        if (key.Length == 1 && char.IsDigit(key[0]))
            return (Keys)((int)Keys.D0 + (key[0] - '0'));
        return Enum.TryParse<Keys>(key, true, out var k) ? k : null;
    }

    /// </summary>
    /// <summary>
    /// POST /drop  { }
    /// Drops the currently held item on the ground next to the farmer.
    /// </summary>
    private object HandleDrop(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var farmer = Game1.player;
                var item = farmer.CurrentItem;
                if (item == null)
                {
                    tcs.SetResult(new { ok = false, error = "Nothing held to drop" });
                    return;
                }
                var name = item.Name;
                farmer.removeItemFromInventory(item);
                Game1.createItemDebris(item, farmer.getStandingPosition(), farmer.FacingDirection);
                tcs.SetResult(new { ok = true, dropped = name });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /area  { "op": "water"|"unwater", "x1", "y1", "x2", "y2" }
    /// Batch-op over a tile box. water/unwater sets every HoeDirt tile's watered state
    /// in the box directly (no walking / stamina cost). Modded crops on HoeDirt are fine.
    /// </summary>
    private object HandleArea(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var op = GetParamOr(p, "op", "water");
        var x1 = GetParam<int>(p, "x1");
        var y1 = GetParam<int>(p, "y1");
        var x2 = GetParam<int>(p, "x2");
        var y2 = GetParam<int>(p, "y2");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var loc = Game1.player.currentLocation;
                int affected = 0;
                int xa = Math.Min(x1, x2), xb = Math.Max(x1, x2);
                int ya = Math.Min(y1, y2), yb = Math.Max(y1, y2);
                for (int x = xa; x <= xb; x++)
                {
                    for (int y = ya; y <= yb; y++)
                    {
                        if (loc.terrainFeatures.TryGetValue(new Vector2(x, y), out var tf)
                            && tf is HoeDirt dirt)
                        {
                            if (op == "water" && dirt.state.Value != 1)
                            {
                                dirt.state.Value = 1;
                                affected++;
                            }
                            else if (op == "unwater" && dirt.state.Value != 0)
                            {
                                dirt.state.Value = 0;
                                affected++;
                            }
                        }
                    }
                }
                tcs.SetResult(new { ok = true, op, affected,
                    box = new { x1 = xa, y1 = ya, x2 = xb, y2 = yb } });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// POST /follow  { "target": "player:Es" | "npc:Abigail" | "x,y" }  or  { "target": "" } to stop
    /// Auto-follows a farmer/NPC/coordinate by re-pathing toward it each tick.
    /// </summary>
    private object HandleFollow(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var p = ReadJson(ctx);
        var target = p.TryGetValue("target", out var t) ? t?.ToString() : null;
        EnqueueMainThread(() =>
        {
            _followTarget = string.IsNullOrWhiteSpace(target) ? null : target;
            _followTickCooldown = 0;
            if (_followTarget == null)
            {
                _pathQueue = null;
                _pathTickCooldown = 0;
            }
        });
        return new { ok = true, follow = string.IsNullOrWhiteSpace(target) ? null : target };
    }

    private Point? ResolveFollowTile(string target)
    {
        var farmer = Game1.player;
        if (farmer == null) return null;

        if (target.StartsWith("player:", StringComparison.OrdinalIgnoreCase))
        {
            var name = target.Substring(7).Trim();
            var other = Game1.getOnlineFarmers()
                .FirstOrDefault(f => f != farmer && f.currentLocation == farmer.currentLocation
                    && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return other?.TilePoint;
        }
        if (target.StartsWith("npc:", StringComparison.OrdinalIgnoreCase))
        {
            var name = target.Substring(4).Trim();
            var npc = farmer.currentLocation.characters
                .FirstOrDefault(n => !(n is Monster) && n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return npc?.TilePoint;
        }
        var parts = target.Split(',');
        if (parts.Length == 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y))
            return new Point(x, y);
        return null;
    }

    private object HandleQueue(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("Empty command queue");

        var commands = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(body);
        if (commands == null || commands.Count == 0)
            throw new InvalidOperationException("No commands in queue");

        _commandQueueTcs = new TaskCompletionSource<object>();
        _commandResults.Clear();

        EnqueueMainThread(() =>
        {
            _commandQueue = new Queue<Dictionary<string, object?>>(commands);
            _commandDelay = 0;
            _waitingForMove = false;
        });

        // Wait for all commands to execute (timeout 5 minutes)
        if (_commandQueueTcs.Task.Wait(TimeSpan.FromMinutes(5)))
            return _commandQueueTcs.Task.Result;
        else
            return new { ok = false, error = "Queue execution timed out", executed = _commandResults.Count };
    }

    private object HandleStop()
    {
        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            ClearMovementState();
            var farmer = Game1.player;
            tcs.SetResult(new
            {
                ok = true,
                message = "Movement stopped",
                location = farmer.currentLocation.Name,
                x = farmer.TilePoint.X,
                y = farmer.TilePoint.Y
            });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandlePlaceChest(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var cx = GetParam<int>(p, "x");
        var cy = GetParam<int>(p, "y");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var loc = Game1.player.currentLocation;
            var tileVec = new Vector2(cx, cy);

            if (loc.objects.ContainsKey(tileVec))
            {
                tcs.SetResult(new { ok = false, error = $"Tile ({cx},{cy}) already has an object" });
                return;
            }

            var chest = new StardewValley.Objects.Chest(true, tileVec);
            loc.objects.Add(tileVec, chest);
            tcs.SetResult(new { ok = true, placed = "Chest", x = cx, y = cy });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleStore(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var cx = GetParam<int>(p, "x");
        var cy = GetParam<int>(p, "y");
        var name = GetParamOr(p, "name", "");
        var keepTools = GetParamOr(p, "keepTools", true);

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            var tileVec = new Vector2(cx, cy);

            if (!loc.objects.TryGetValue(tileVec, out var obj) || obj is not StardewValley.Objects.Chest chest)
            {
                tcs.SetResult(new { ok = false, error = $"No chest at ({cx},{cy})" });
                return;
            }

            var stored = new List<object>();
            for (int i = farmer.Items.Count - 1; i >= 0; i--)
            {
                var item = farmer.Items[i];
                if (item == null) continue;
                if (keepTools && item is Tool) continue;
                if (!string.IsNullOrEmpty(name)
                    && !item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                var leftover = chest.addItem(item);
                if (leftover == null)
                {
                    stored.Add(new { item = item.Name, count = item.Stack });
                    farmer.Items[i] = null;
                }
                else if (leftover.Stack < item.Stack)
                {
                    stored.Add(new { item = item.Name, count = item.Stack - leftover.Stack });
                    farmer.Items[i] = leftover;
                }
            }

            tcs.SetResult(new { ok = true, stored, chestAt = new { x = cx, y = cy } });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleChest(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var cx = GetParam<int>(p, "x");
        var cy = GetParam<int>(p, "y");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            var tileVec = new Vector2(cx, cy);

            if (!loc.objects.TryGetValue(tileVec, out var obj) || obj is not StardewValley.Objects.Chest chest)
            {
                tcs.SetResult(new { ok = false, error = $"No chest at ({cx},{cy})" });
                return;
            }

            var items = chest.Items
                .Where(i => i != null)
                .Select(i => new { name = i.Name, count = i.Stack })
                .ToList();

            tcs.SetResult(new { ok = true, items, capacity = chest.GetActualCapacity(), used = items.Count });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleHarvest(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var qs = ctx.Request.QueryString;
        int radius = 15;
        if (int.TryParse(qs["radius"], out var r) && r > 0 && r <= 50)
            radius = r;

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            int count = 0;

            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt dirt && dirt.crop != null && dirt.readyForHarvest())
                {
                    var pos = pair.Key;
                    if (Math.Abs(pos.X - farmer.TilePoint.X) > radius
                        || Math.Abs(pos.Y - farmer.TilePoint.Y) > radius)
                        continue;

                    if (dirt.crop.harvest((int)pos.X, (int)pos.Y, dirt))
                    {
                        dirt.destroyCrop(false);
                        count++;
                    }
                }
            }

            tcs.SetResult(new { ok = true, harvested = count });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleSell(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var p = ReadJson(ctx);
        var name = GetParamOr(p, "name", "");
        var sellAll = GetParamOr(p, "all", false);

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;

            var bin = loc is Farm farm
                ? farm.getShippingBin(farmer)
                : null;

            if (bin == null)
            {
                tcs.SetResult(new { ok = false, error = "No shipping bin found (must be on Farm)" });
                return;
            }

            var sold = new List<object>();
            var keepCategories = new HashSet<int> { -99, -98, -97, -96 }; // tools, rings, boots, weapons

            for (int i = farmer.Items.Count - 1; i >= 0; i--)
            {
                var item = farmer.Items[i];
                if (item == null) continue;
                if (item is Tool) continue;
                if (keepCategories.Contains(item.Category)) continue;

                if (!sellAll && !string.IsNullOrEmpty(name)
                    && !item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (sellAll && item.Name.Contains("Seeds", StringComparison.OrdinalIgnoreCase))
                    continue;

                var salePrice = item is StardewValley.Object obj ? obj.sellToStorePrice() * item.Stack : 0;
                sold.Add(new { item = item.Name, count = item.Stack, price = salePrice });

                bin.Add(item);
                farmer.Items[i] = null;
            }

            tcs.SetResult(new { ok = true, sold, totalItems = sold.Count });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleRefill()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var wc = Game1.player.Items.OfType<WateringCan>().FirstOrDefault();
            if (wc == null)
            {
                tcs.SetResult(new { ok = false, error = "No watering can in inventory" });
                return;
            }
            wc.WaterLeft = wc.waterCanMax;
            tcs.SetResult(new { ok = true, water = wc.WaterLeft, max = wc.waterCanMax });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleHeal()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var f = Game1.player;
            f.health = f.maxHealth;
            f.Stamina = f.MaxStamina;
            tcs.SetResult(new { ok = true, health = f.health, stamina = f.Stamina });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleRipen(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var qs = ctx.Request.QueryString;
        int radius = 30;
        if (int.TryParse(qs["radius"], out var r) && r > 0 && r <= 50)
            radius = r;

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var loc = farmer.currentLocation;
            int count = 0;

            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt dirt && dirt.crop != null && !dirt.readyForHarvest())
                {
                    var pos = pair.Key;
                    if (Math.Abs(pos.X - farmer.TilePoint.X) <= radius
                        && Math.Abs(pos.Y - farmer.TilePoint.Y) <= radius)
                    {
                        dirt.crop.growCompletely();
                        count++;
                    }
                }
            }

            tcs.SetResult(new { ok = true, ripened = count });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleGive(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var itemId = GetParam<string>(p, "id");
        var count = GetParamOr(p, "count", 1);

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            var farmer = Game1.player;
            var item = ItemRegistry.Create(itemId, count);
            farmer.addItemToInventory(item);
            tcs.SetResult(new { ok = true, given = item.Name, count, id = itemId });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMoney(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var amount = GetParam<int>(p, "amount");

        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            Game1.player.Money += amount;
            tcs.SetResult(new { ok = true, added = amount, total = Game1.player.Money });
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandlePause()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");
        _frozenTime = Game1.timeOfDay;
        _timeFrozen = true;
        return new { ok = true, action = "paused", frozenAt = _frozenTime };
    }

    private object HandleResume()
    {
        _timeFrozen = false;
        return new { ok = true, action = "resumed" };
    }

    private object HandleFishbot(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var action = GetParamOr(p, "action", "toggle"); // on, off, toggle, status

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                // Find Fishbot mod via SMAPI mod registry
                object? fishbotMod = null;
                System.Reflection.FieldInfo? autoField = null;

                var modInfo = this.Helper.ModRegistry.Get("AdroSlice.Fishbot");
                if (modInfo != null)
                {
                    var modInfoType = modInfo.GetType();
                    var modProp = modInfoType.GetProperty("Mod",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    fishbotMod = modProp?.GetValue(modInfo);
                    if (fishbotMod == null)
                    {
                        var modField = modInfoType.GetField("Mod",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Instance);
                        fishbotMod = modField?.GetValue(modInfo);
                    }
                }

                if (fishbotMod == null)
                {
                    tcs.SetResult(new { ok = false, error = "Fishbot mod not found" });
                    return;
                }

                // Find AutomationEnabled field/property
                var fbType = fishbotMod.GetType();
                autoField = fbType.GetField("AutomationEnabled",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);

                var autoProp = fbType.GetProperty("AutomationEnabled",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);

                var bindingAll = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;

                if (autoField != null || autoProp != null)
                {
                    bool current = autoField != null
                        ? (bool)autoField.GetValue(fishbotMod)!
                        : (bool)autoProp!.GetValue(fishbotMod)!;
                    bool target = action == "toggle" ? !current : action == "on";

                    if (action != "status")
                    {
                        if (autoField != null) autoField.SetValue(fishbotMod, target);
                        else autoProp!.SetValue(fishbotMod, target);

                        if (target)
                        {
                            var startMethod = fbType.GetMethod("StartCasting", bindingAll);
                            startMethod?.Invoke(fishbotMod, null);
                        }
                        else
                        {
                            var resetMethod = fbType.GetMethod("reset", bindingAll)
                                ?? fbType.GetMethod("Reset", bindingAll);
                            resetMethod?.Invoke(fishbotMod, null);
                        }
                    }
                    tcs.SetResult(new { ok = true, enabled = action == "status" ? current : target });
                }
                else
                {
                    // List all fields for debugging
                    var fields = fbType.GetFields(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
                    var names = string.Join(", ", fields.Select(f => f.Name));
                    tcs.SetResult(new { ok = false, error = $"AutomationEnabled not found. Fields: {names}" });
                }
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMinigameState()
    {
        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                tcs.SetResult(_prairieKingBot.BuildState());
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMinigameBot(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var action = GetParamOr(p, "action", "status").ToLowerInvariant();

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                switch (action)
                {
                    case "start":
                        _prairieKingBot.Start();
                        break;
                    case "stop":
                        _prairieKingBot.Stop();
                        break;
                    case "status":
                        break;
                    default:
                        tcs.SetResult(new { ok = false, error = "action must be start, stop, or status" });
                        return;
                }

                tcs.SetResult(new
                {
                    ok = true,
                    active = _prairieKingBot.IsActive,
                    inPrairieKing = PrairieKingBot.IsPrairieKing(Game1.currentMinigame),
                    currentMinigame = Game1.currentMinigame?.GetType().FullName,
                    lastMove = new { x = _prairieKingBot.LastMove.X, y = _prairieKingBot.LastMove.Y },
                    lastError = _prairieKingBot.LastError
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMenu()
    {
        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var menu = Game1.activeClickableMenu;
                if (menu == null)
                {
                    object? eventInfo = null;
                    if (Game1.currentLocation?.currentEvent != null)
                    {
                        var ev = Game1.currentLocation.currentEvent;
                        eventInfo = new { id = ev.id, skippable = ev.skippable };
                    }
                    tcs.SetResult(new { ok = true, open = false, activeEvent = eventInfo });
                    return;
                }

                var menuType = menu.GetType().Name;
                string? dialogue = null;
                List<object>? responses = null;
                List<object>? shopItems = null;
                List<object>? buttons = null;

                if (menu is DialogueBox db)
                {
                    try { dialogue = db.getCurrentString(); } catch { }

                    var responseField = typeof(DialogueBox).GetField("responseCC",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    var responseCCs = responseField?.GetValue(db) as List<ClickableComponent>;

                    var responsesField = typeof(DialogueBox).GetField("responses",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    var responseList = responsesField?.GetValue(db) as List<Response>;

                    if (responseList != null && responseList.Count > 0)
                    {
                        responses = new List<object>();
                        for (int i = 0; i < responseList.Count; i++)
                        {
                            var r = responseList[i];
                            responses.Add(new
                            {
                                index = i,
                                key = r.responseKey,
                                text = r.responseText,
                                bounds = responseCCs != null && i < responseCCs.Count
                                    ? new { x = responseCCs[i].bounds.X, y = responseCCs[i].bounds.Y,
                                            w = responseCCs[i].bounds.Width, h = responseCCs[i].bounds.Height }
                                    : null
                            });
                        }
                    }
                }
                else if (menu is ShopMenu shop)
                {
                    shopItems = new List<object>();
                    var forSale = shop.forSale;
                    var itemPriceAndStock = shop.itemPriceAndStock;
                    foreach (var item in forSale)
                    {
                        int price = 0;
                        int stock = -1;
                        if (itemPriceAndStock.TryGetValue(item, out var info))
                        {
                            price = info.Price;
                            stock = info.Stock;
                        }
                        shopItems.Add(new
                        {
                            name = item.DisplayName,
                            id = item.QualifiedItemId,
                            price,
                            stock
                        });
                    }
                }

                // Collect named buttons via reflection
                buttons = new List<object>();
                foreach (var fieldName in new[] { "okButton", "cancelButton", "backButton",
                    "forwardButton", "upperRightCloseButton", "trashCan" })
                {
                    var field = menu.GetType().GetField(fieldName,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    var comp = field?.GetValue(menu) as ClickableComponent;
                    if (comp != null && comp.visible)
                    {
                        buttons.Add(new
                        {
                            name = fieldName,
                            x = comp.bounds.Center.X,
                            y = comp.bounds.Center.Y
                        });
                    }
                }

                tcs.SetResult(new
                {
                    ok = true,
                    open = true,
                    type = menuType,
                    dialogue,
                    responses,
                    shopItems,
                    buttons = buttons.Count > 0 ? buttons : null
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMenuClick(HttpListenerContext ctx)
    {
        var p = ReadJson(ctx);
        var option = GetParamOr(p, "option", -1);
        var button = GetParamOr(p, "button", "");
        var clickX = GetParamOr(p, "x", -1);
        var clickY = GetParamOr(p, "y", -1);

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var menu = Game1.activeClickableMenu;
                if (menu == null)
                {
                    tcs.SetResult(new { ok = false, error = "No menu open" });
                    return;
                }

                if (option >= 0 && menu is DialogueBox db)
                {
                    var responseField = typeof(DialogueBox).GetField("responseCC",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    var responseCCs = responseField?.GetValue(db) as List<ClickableComponent>;

                    if (responseCCs != null && option < responseCCs.Count)
                    {
                        var rc = responseCCs[option];
                        db.receiveLeftClick(rc.bounds.Center.X, rc.bounds.Center.Y);
                        tcs.SetResult(new { ok = true, clicked = "response", option });
                    }
                    else
                    {
                        tcs.SetResult(new { ok = false, error = $"Response index {option} out of range" });
                    }
                    return;
                }

                if (button != "")
                {
                    var field = menu.GetType().GetField(button == "ok" ? "okButton" :
                                                        button == "cancel" ? "cancelButton" :
                                                        button == "back" ? "backButton" :
                                                        button == "forward" ? "forwardButton" :
                                                        button == "close" ? "upperRightCloseButton" :
                                                        button,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    var comp = field?.GetValue(menu) as ClickableComponent;
                    if (comp != null)
                    {
                        menu.receiveLeftClick(comp.bounds.Center.X, comp.bounds.Center.Y);
                        tcs.SetResult(new { ok = true, clicked = "button", button });
                    }
                    else
                    {
                        tcs.SetResult(new { ok = false, error = $"Button '{button}' not found" });
                    }
                    return;
                }

                if (clickX >= 0 && clickY >= 0)
                {
                    menu.receiveLeftClick(clickX, clickY);
                    tcs.SetResult(new { ok = true, clicked = "position", x = clickX, y = clickY });
                    return;
                }

                menu.receiveLeftClick(
                    menu.xPositionOnScreen + menu.width / 2,
                    menu.yPositionOnScreen + menu.height / 2);
                tcs.SetResult(new { ok = true, clicked = "center" });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleCraft(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var p = ReadJson(ctx);
        var name = GetParam<string>(p, "name");
        var count = GetParamOr(p, "count", 1);

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var farmer = Game1.player;
                var recipes = CraftingRecipe.craftingRecipes;
                if (!recipes.ContainsKey(name))
                {
                    var known = farmer.craftingRecipes.Keys.ToList();
                    tcs.SetResult(new { ok = false, error = $"Recipe '{name}' not found",
                        knownRecipes = known });
                    return;
                }

                if (!farmer.craftingRecipes.ContainsKey(name))
                {
                    tcs.SetResult(new { ok = false, error = $"Player hasn't learned recipe '{name}'" });
                    return;
                }

                var recipe = new CraftingRecipe(name, false);
                int crafted = 0;
                var missing = new Dictionary<string, int>();

                for (int i = 0; i < count; i++)
                {
                    if (!recipe.doesFarmerHaveIngredientsInInventory())
                    {
                        foreach (var kvp in recipe.recipeList)
                        {
                            var ingredientId = kvp.Key;
                            var needed = kvp.Value;
                            var have = 0;
                            foreach (var item in farmer.Items)
                            {
                                if (item != null && (item.ParentSheetIndex.ToString() == ingredientId
                                    || item.Category.ToString() == ingredientId))
                                    have += item.Stack;
                            }
                            if (have < needed)
                            {
                                var ingredientName = ingredientId;
                                try { ingredientName = new StardewValley.Object(ingredientId, 1).DisplayName; } catch { }
                                missing[ingredientName] = needed - have;
                            }
                        }
                        break;
                    }
                    recipe.consumeIngredients(null);
                    var product = recipe.createItem();
                    if (!farmer.addItemToInventoryBool(product))
                    {
                        Game1.createItemDebris(product, farmer.getStandingPosition(), farmer.FacingDirection);
                        tcs.SetResult(new { ok = true, crafted = crafted + 1,
                            warning = "Inventory full, item dropped" });
                        return;
                    }
                    crafted++;
                }

                if (crafted == 0)
                    tcs.SetResult(new { ok = false, error = "Missing materials", missing });
                else if (crafted < count)
                    tcs.SetResult(new { ok = true, crafted, requested = count,
                        warning = "Ran out of materials", missing });
                else
                    tcs.SetResult(new { ok = true, crafted });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleMachines()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var loc = Game1.player.currentLocation;
                var machines = new List<object>();

                foreach (var pair in loc.objects.Pairs)
                {
                    var obj = pair.Value;
                    if (!obj.bigCraftable.Value) continue;

                    string status;
                    if (obj.readyForHarvest.Value)
                        status = "ready";
                    else if (obj.heldObject.Value != null || obj.MinutesUntilReady > 0)
                        status = "processing";
                    else
                        status = "empty";

                    var entry = new Dictionary<string, object?>
                    {
                        ["name"] = ResolveObjectName(obj),
                        ["x"] = (int)pair.Key.X,
                        ["y"] = (int)pair.Key.Y,
                        ["status"] = status,
                        ["minutesLeft"] = obj.MinutesUntilReady
                    };

                    if (obj.heldObject.Value != null)
                    {
                        entry["heldItem"] = obj.heldObject.Value.Name;
                        entry["heldItemId"] = obj.heldObject.Value.QualifiedItemId;
                    }

                    machines.Add(entry);
                }

                tcs.SetResult(new
                {
                    ok = true,
                    location = loc.Name,
                    count = machines.Count,
                    machines
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleAnimals()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var loc = Game1.player.currentLocation;
                var animals = new List<object>();

                IEnumerable<FarmAnimal>? animalList = null;
                if (loc is Farm farm)
                    animalList = farm.animals.Values;
                else if (loc is AnimalHouse ah)
                    animalList = ah.animals.Values;

                if (animalList != null)
                {
                    foreach (var a in animalList)
                    {
                        animals.Add(new
                        {
                            name = a.Name,
                            type = a.type.Value,
                            x = a.TilePoint.X,
                            y = a.TilePoint.Y,
                            wasPetToday = a.wasPet.Value,
                            friendship = a.friendshipTowardFarmer.Value,
                            happiness = a.happiness.Value,
                            fullness = a.fullness.Value,
                            age = a.age.Value,
                            home = a.home?.indoors.Value?.Name,
                            product = a.currentProduce.Value,
                            productReady = a.currentProduce.Value != null && a.currentProduce.Value != "-1"
                        });
                    }
                }

                tcs.SetResult(new
                {
                    ok = true,
                    location = loc.Name,
                    count = animals.Count,
                    animals
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleScan()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var loc = Game1.currentLocation;
                var actions = new List<object>();
                for (int x = 0; x < loc.Map.Layers[0].LayerWidth; x++)
                {
                    for (int y = 0; y < loc.Map.Layers[0].LayerHeight; y++)
                    {
                        string? action = loc.doesTileHaveProperty(x, y, "Action", "Buildings");
                        if (action != null)
                            actions.Add(new { x, y, action });
                    }
                }
                tcs.SetResult(new { ok = true, location = loc.Name, count = actions.Count, actions });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleFestival()
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var evt = Game1.CurrentEvent;
                if (evt == null)
                {
                    tcs.SetResult(new { ok = false, error = "No active event" });
                    return;
                }

                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var actors = new List<object>();

                // Get event actors
                var actorsField = evt.GetType().GetField("actors", flags);
                if (actorsField?.GetValue(evt) is IEnumerable<NPC> npcList)
                {
                    foreach (var npc in npcList)
                    {
                        actors.Add(new
                        {
                            name = npc.Name,
                            displayName = npc.displayName,
                            x = npc.TilePoint.X,
                            y = npc.TilePoint.Y
                        });
                    }
                }

                // Check festival name
                string festivalName = "";
                var nameField = evt.GetType().GetField("FestivalName", flags) ?? evt.GetType().GetField("festivalName", flags);
                if (nameField != null)
                    festivalName = nameField.GetValue(evt) as string ?? "";
                var nameProp = evt.GetType().GetProperty("FestivalName", flags);
                if (string.IsNullOrEmpty(festivalName) && nameProp != null)
                    festivalName = nameProp.GetValue(evt) as string ?? "";

                // Check isFestival
                bool isFestival = false;
                var isFestMethod = typeof(Game1).GetMethod("isFestival", flags, null, Type.EmptyTypes, null);
                if (isFestMethod != null)
                    isFestival = (bool?)isFestMethod.Invoke(null, null) ?? false;

                tcs.SetResult(new
                {
                    ok = true,
                    isFestival,
                    festivalName,
                    location = Game1.currentLocation?.Name,
                    actorCount = actors.Count,
                    actors
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleFestivalInteract(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var body = ReadJson(ctx);
        string targetName = body.ContainsKey("name") ? body["name"].ToString() : "";

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var evt = Game1.CurrentEvent;
                if (evt == null)
                {
                    tcs.SetResult(new { ok = false, error = "No active event" });
                    return;
                }

                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var actorsField = evt.GetType().GetField("actors", flags);
                if (actorsField?.GetValue(evt) is not IEnumerable<NPC> npcList)
                {
                    tcs.SetResult(new { ok = false, error = "No actors found" });
                    return;
                }

                NPC? target = null;
                foreach (var npc in npcList)
                {
                    if (string.IsNullOrEmpty(targetName) || npc.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        target = npc;
                        break;
                    }
                }

                if (target == null)
                {
                    tcs.SetResult(new { ok = false, error = $"Actor '{targetName}' not found" });
                    return;
                }

                // Move player next to NPC and face them
                var farmer = Game1.player;
                farmer.Position = new Vector2(target.TilePoint.X, target.TilePoint.Y + 1) * Game1.tileSize;
                farmer.faceDirection(0); // face up toward NPC

                // Try to trigger NPC action via checkAction
                bool triggered = Game1.currentLocation.checkAction(
                    new xTile.Dimensions.Location(target.TilePoint.X, target.TilePoint.Y),
                    Game1.viewport, farmer);

                if (!triggered)
                {
                    // Fallback: try direct NPC click
                    target.checkAction(farmer, Game1.currentLocation);
                    triggered = true;
                }

                tcs.SetResult(new
                {
                    ok = true,
                    target = target.Name,
                    targetTile = new { x = target.TilePoint.X, y = target.TilePoint.Y },
                    playerTile = new { x = farmer.TilePoint.X, y = farmer.TilePoint.Y },
                    triggered
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private object HandleFestivalAnswer(HttpListenerContext ctx)
    {
        if (!Context.IsWorldReady)
            throw new InvalidOperationException("World not ready");

        var body = ReadJson(ctx);
        int answer = body.ContainsKey("answer") ? Convert.ToInt32(body["answer"]) : 0;
        string key = body.ContainsKey("key") ? body["key"].ToString() : "";

        var tcs = new TaskCompletionSource<object>();
        EnqueueMainThread(() =>
        {
            try
            {
                var evt = Game1.CurrentEvent;
                if (evt == null)
                {
                    tcs.SetResult(new { ok = false, error = "No active event" });
                    return;
                }

                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

                // Try answerDialogueQuestion
                var answerMethod = evt.GetType().GetMethod("answerDialogueQuestion", flags);
                if (answerMethod != null)
                {
                    var npc = Game1.currentLocation.isCharacterAtTile(Game1.player.GetGrabTile());
                    answerMethod.Invoke(evt, new object?[] { npc, answer.ToString() });
                    tcs.SetResult(new { ok = true, method = "answerDialogueQuestion", answer });
                    return;
                }

                // Fallback: try answerDialogue on the event
                var methods = evt.GetType().GetMethods(flags);
                foreach (var m in methods)
                {
                    if (m.Name.Contains("answer", StringComparison.OrdinalIgnoreCase) ||
                        m.Name.Contains("Answer", StringComparison.OrdinalIgnoreCase))
                    {
                        var parms = m.GetParameters();
                        if (parms.Length >= 1)
                        {
                            try
                            {
                                if (parms[0].ParameterType == typeof(int))
                                    m.Invoke(evt, new object[] { answer });
                                else if (parms[0].ParameterType == typeof(string))
                                    m.Invoke(evt, new object[] { answer.ToString() });
                                tcs.SetResult(new { ok = true, method = m.Name, answer });
                                return;
                            }
                            catch { continue; }
                        }
                    }
                }

                // Fallback: use Game1.currentLocation.answerDialogueAction
                var locMethod = Game1.currentLocation.GetType().GetMethod("answerDialogueAction", flags);
                if (locMethod != null)
                {
                    string actionKey = string.IsNullOrEmpty(key) ? $"festival_{answer}" : key;
                    locMethod.Invoke(Game1.currentLocation, new object[] { actionKey, Array.Empty<string>() });
                    tcs.SetResult(new { ok = true, method = "location.answerDialogueAction", key = actionKey });
                    return;
                }

                tcs.SetResult(new { ok = false, error = "No answer method found" });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { ok = false, error = ex.Message });
            }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    private Vector2 GetFacingTile(Farmer farmer)
    {
        int x = farmer.TilePoint.X;
        int y = farmer.TilePoint.Y;
        return farmer.FacingDirection switch
        {
            0 => new Vector2(x, y - 1),
            1 => new Vector2(x + 1, y),
            2 => new Vector2(x, y + 1),
            3 => new Vector2(x - 1, y),
            _ => new Vector2(x, y)
        };
    }

    // --- Helpers ---

    private void EnqueueMainThread(Action action)
    {
        lock (_queueLock)
        {
            _mainThreadQueue.Enqueue(action);
        }
    }

    /// <summary>
    /// Simple BFS pathfinding on the game map.
    /// </summary>
    private Queue<Point>? FindPath(GameLocation location, Point start, Point end)
    {
        if (start == end) return new Queue<Point>();

        var maxSteps = Math.Min(
            Math.Max(500, (location.Map.DisplayWidth / 64) * (location.Map.DisplayHeight / 64)),
            20000);
        var visited = new HashSet<Point> { start };
        var queue = new Queue<(Point pos, List<Point> path)>();
        queue.Enqueue((start, new List<Point>()));

        int[] dx = { 0, 0, -1, 1 };
        int[] dy = { -1, 1, 0, 0 };

        while (queue.Count > 0 && maxSteps-- > 0)
        {
            var (pos, path) = queue.Dequeue();

            for (int i = 0; i < 4; i++)
            {
                var next = new Point(pos.X + dx[i], pos.Y + dy[i]);

                if (visited.Contains(next)) continue;
                if (!IsTilePassable(location, next)) continue;

                visited.Add(next);
                var newPath = new List<Point>(path) { next };

                if (next == end)
                    return new Queue<Point>(newPath);

                queue.Enqueue((next, newPath));
            }
        }

        // If no path found, return null (caller will fallback to direct walk)
        return null;
    }

    private bool IsTilePassable(GameLocation location, Point tile)
    {
        // Check map bounds
        if (tile.X < 0 || tile.Y < 0) return false;
        var mapWidth = location.Map.DisplayWidth / 64;
        var mapHeight = location.Map.DisplayHeight / 64;
        if (tile.X >= mapWidth || tile.Y >= mapHeight) return false;

        // Use the game's built-in passability check
        var tileVec = new Vector2(tile.X, tile.Y);
        return location.isTilePassable(tileVec);
    }
}

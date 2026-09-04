namespace NagiBridge;

public class ModConfig
{
    public string Mode { get; set; } = "cc";
    public string ChannelServerUrl { get; set; } = "http://localhost:9000/chat";
    public string ApiProvider { get; set; } = "claude";
    public string ApiUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-4-6-20250514";
    public string SystemPrompt { get; set; } = "You are a friendly AI companion in Stardew Valley. You chat casually with the player about farm life, give tips, and keep them company. Keep responses short (1-3 sentences) since this is in-game chat.";
    public int MaxHistoryMessages { get; set; } = 20;

    /// <summary>Path to extract_keybinds.py, run at game launch to rebuild the mod keybind map.</summary>
    public string KeybindsExtractScript { get; set; } = @"C:\Users\lenovo\Desktop\NagiBridge\scripts\extract_keybinds.py";

    /// <summary>Port the HOST instance binds (IsMainPlayer == true).</summary>
    public int HostPort { get; set; } = 58331;

    /// <summary>Port the FARMHAND instance binds (IsMainPlayer == false, i.e. a co-op join).</summary>
    public int FarmhandPort { get; set; } = 58332;

    /// <summary>Bridge base URL that the in-game chat panel forwards the player's messages to (LAN default http://127.0.0.1:8000).</summary>
    public string OperitBridgeUrl { get; set; } = "http://127.0.0.1:8000";

    /// <summary>Shared token sent to the bridge for in-game chat forwarding (leave empty if the bridge has none).</summary>
    public string OperitBridgeToken { get; set; } = "";
}

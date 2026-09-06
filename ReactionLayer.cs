using StardewModdingAPI;
using StardewValley;

namespace WheatStook;

/// <summary>
/// The AI reaction layer. It turns notable in-game events into a short message in
/// the dialogue channel so the AI actually reacts to the world instead of only
/// answering on demand. Each reaction is one compact line to keep the token cost
/// reasonable.
///
/// v1 fires once per new day (a one-line briefing: date, gold, energy). More event
/// types (level-ups, crops ready, low energy, lost items) land in later refinements.
/// </summary>
public class ReactionLayer
{
    private readonly IMonitor _monitor;
    private readonly Action<string> _sendToAi;
    private readonly Action<string> _toHud;
    private readonly Func<bool> _enabled;

    public ReactionLayer(IMonitor monitor, Action<string> sendToAi, Action<string> toHud, Func<bool> enabled)
    {
        _monitor = monitor;
        _sendToAi = sendToAi;
        _toHud = toHud;
        _enabled = enabled;
    }

    public void OnDayStarted()
    {
        if (!_enabled()) return;
        if (Game1.Date is null || Game1.player is null) return;
        try
        {
            string season = Game1.Date.Season switch
            {
                Season.Spring => "春",
                Season.Summer => "夏",
                Season.Fall => "秋",
                Season.Winter => "冬",
                _ => string.Empty
            };
            int day = Game1.Date.DayOfMonth;
            int year = Game1.Date.Year;
            int gold = Game1.player.Money;
            int energy = (int)Game1.player.stamina;
            string msg = $"【新的一天】{season}第{day}天 第{year}年 · 金币 {gold} · 精力 {energy}。";
            _toHud(msg);
            _sendToAi(msg);
        }
        catch (Exception ex)
        {
            _monitor.Log($"ReactionLayer OnDayStarted: {ex.Message}", LogLevel.Error);
        }
    }
}

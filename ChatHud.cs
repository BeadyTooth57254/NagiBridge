using System.Text;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace WheatStook;

/// <summary>
/// In-game chat panel: toggled by the chat hotkey, shows recent messages, and
/// captures a text line. On submit, it raises <see cref="OnSubmit"/> so the mod
/// can forward the message to the Operit / MCP channels.
///
/// v1 note: key capture handles ASCII letters/digits/space/backspace/enter; full
/// IME (Chinese) input is a later refinement.
/// </summary>
public class ChatHud
{
    private readonly IMonitor _monitor;
    private readonly object _lock = new();
    private readonly List<string> _messages = new();
    private readonly StringBuilder _input = new();
    private bool _open;

    public ChatHud(IMonitor monitor) => _monitor = monitor;

    public bool IsOpen => _open;
    public event Action<string>? OnSubmit;

    public void Toggle() => _open = !_open;

    /// <summary>Handle a key while the panel is open; returns true if it was consumed.</summary>
    public bool HandleKey(SButton key)
    {
        if (!_open) return false;
        switch (key)
        {
            case SButton.Enter: Submit(); return true;
            case SButton.Escape: _open = false; return true;
            case SButton.Back:
                if (_input.Length > 0) _input.Length--;
                return true;
            case SButton.Space:
                if (_input.Length < 300) _input.Append(' ');
                return true;
            default:
                if (_input.Length >= 300) return true;
                if (key >= SButton.A && key <= SButton.Z) { _input.Append((char)('a' + (key - SButton.A))); return true; }
                if (key >= SButton.D0 && key <= SButton.D9) { _input.Append((char)('0' + (key - SButton.D0))); return true; }
                return false;
        }
    }

    public void AddMessage(string text)
    {
        lock (_lock) _messages.Add(text);
    }

    private void Submit()
    {
        string text;
        lock (_lock) { text = _input.ToString().Trim(); _input.Clear(); }
        if (string.IsNullOrWhiteSpace(text)) return;
        AddMessage("我: " + text);
        OnSubmit?.Invoke(text);
    }

    public void Draw()
    {
        if (!_open || Game1.spriteBatch is null) return;
        int w = 560, h = 220;
        int x = 20, y = Game1.viewport.Height - h - 20;

        Game1.drawDialogueBox(x - 16, y - 16, w + 32, h + 32, false, true);

        lock (_lock)
        {
            int line = 0;
            foreach (var m in _messages.TakeLast(5))
            {
                Game1.spriteBatch.DrawString(Game1.dialogueFont, m, new Vector2(x + 12, y + 8 + line * 30), Color.White);
                line++;
            }
            var cursor = Game1.currentGameTime.TotalGameTime.TotalMilliseconds % 1000 < 500 ? "_" : "";
            Game1.spriteBatch.DrawString(Game1.smallFont, "> " + _input.ToString() + cursor, new Vector2(x + 12, y + h - 26), new Color(220, 220, 220));
        }
    }
}

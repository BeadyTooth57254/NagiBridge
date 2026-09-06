using StardewModdingAPI;

namespace WheatStook;

/// <summary>
/// Indexes every installed mod (and content pack) at game launch so the AI can
/// look up what's in the farm's world ("what does this mod do / who made it / is
/// there a link"). Built once via SMAPI's ModRegistry in GameLaunched, so it
/// never has to scrape the console or guess.
///
/// v1: name/author/version/description/UpdateKeys + content-pack flag. Tiered
/// source/Nexus reading (modWhitelist/system, cache) are later refinements.
/// </summary>
public class ModKnowledgeBase
{
    private readonly IMonitor _monitor;
    private readonly List<ModInfo> _mods = new();

    public ModKnowledgeBase(IMonitor monitor) => _monitor = monitor;

    public int Count => _mods.Count;

    public IEnumerable<string> UniqueIds => _mods.Select(m => m.UniqueID);

    public void BuildFrom(IModRegistry registry)
    {
        _mods.Clear();
        foreach (var mod in registry.GetAll())
        {
            var m = mod.Manifest;
            if (m is null) continue;
            _mods.Add(new ModInfo
            {
                Name = m.Name ?? string.Empty,
                UniqueID = m.UniqueID ?? string.Empty,
                Author = m.Author ?? string.Empty,
                Version = m.Version?.ToString() ?? string.Empty,
                Description = m.Description ?? string.Empty,
                UpdateKeys = m.UpdateKeys ?? Array.Empty<string>(),
                IsContentPack = m.ContentPackFor != null,
            });
        }
        _monitor.Log($"Mod knowledge base built: {_mods.Count} mods indexed (enableModKnowledge={true}).", LogLevel.Info);
    }

    /// <summary>Return the mods whose name/unique id/author/description contain <paramref name="query"/>.</summary>
    public List<ModInfo> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _mods;
        var q = query.Trim();
        return _mods.Where(m =>
            m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            m.UniqueID.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            m.Author.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            m.Description.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public sealed class ModInfo
    {
        public string Name { get; set; } = string.Empty;
        public string UniqueID { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string[] UpdateKeys { get; set; } = Array.Empty<string>();
        public bool IsContentPack { get; set; }
    }
}

using StardewModdingAPI;

namespace WheatStook;

/// <summary>
/// A data-driven compat rule, defined by the user in CompatRules.json (SMAPI reads
/// that file with comments allowed, so it is beginner friendly). When the rule's
/// mod is installed and its compat profile is active, any object/crop whose id or
/// display name matches is tagged with the rule's category/hints in the /state and
/// /surroundings output. This is the "new feature" adapter: anyone can teach 麦垛
/// to recognize a custom item/machine/bush without writing C#.
///
/// Every field is optional except Mod. Leave a matcher blank to ignore it; a rule
/// matches when both (provided) matchers pass.
/// </summary>
public class CompatRule
{
    /// <summary>The add-on's UniqueId (e.g. "aedenthorn.CustomBush"). Required.</summary>
    public string Mod { get; set; } = "";

    /// <summary>Short human label shown to the AI, e.g. "可摘的浆果灌木".</summary>
    public string Label { get; set; } = "";

    /// <summary>Substring of the object/item's qualified id (e.g. "(O)aedenthorn.bush"). Blank = ignore.</summary>
    public string MatchIdContains { get; set; } = "";

    /// <summary>Substring of the object/item's display name (e.g. "berry"). Blank = ignore.</summary>
    public string MatchName { get; set; } = "";

    /// <summary>Semantic tag added to the state output, e.g. "berryBush". Blank = none.</summary>
    public string Category { get; set; } = "";

    /// <summary>Mark the thing as ready to harvest/pick.</summary>
    public bool Harvestable { get; set; }

    /// <summary>Mark the thing as a machine/processor you can operate.</summary>
    public bool Processable { get; set; }

    /// <summary>Mark the thing as a forage/collectible to pick up.</summary>
    public bool Collectible { get; set; }

    public bool Matches(string? qualifiedId, string? displayName)
    {
        bool idOk = string.IsNullOrEmpty(MatchIdContains)
            || (qualifiedId ?? "").Contains(MatchIdContains, StringComparison.OrdinalIgnoreCase);
        bool nameOk = string.IsNullOrEmpty(MatchName)
            || (displayName ?? "").Contains(MatchName, StringComparison.OrdinalIgnoreCase);
        return idOk && nameOk;
    }
}

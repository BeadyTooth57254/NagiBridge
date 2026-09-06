using StardewModdingAPI;

namespace WheatStook;

/// <summary>
/// Auto-compatibility layer (opt-in, default off). At game launch it knows every
/// installed mod's UniqueId (from the mod knowledge base, not folder names), and
/// whether a given mod's compat profile should be active. A profile is active when
/// the mod is installed AND enabled. Enabled comes from compatOverrides (by
/// UniqueId, wins over everything) or, if not overridden, from enableAutoCompat
/// (auto-detect). IsActive works for ANY UniqueId, so data-driven CompatRules in
/// CompatRules.json (see CompatRule) can target mods that are not in the built-in
/// profile list too.
/// </summary>
public class CompatLayer
{
    private readonly IMonitor _monitor;
    private readonly HashSet<string> _installed;
    private readonly bool _enableAutoCompat;
    private readonly Dictionary<string, bool> _overrides;
    private readonly HashSet<string> _enabled = new();

    // Known compat profiles: UniqueId -> human-readable label. Add rows as we learn
    // a specific mod's new feature (custom machine, profession, ...). Detection is by
    // UniqueId, which is stable even if the folder is renamed.
    private static readonly (string Id, string Label)[] Profiles =
    {
        ("bcmpinc.WearMoreRings",        "Wear More Rings (戒指槽)"),
        ("Stari.CombineManyRings",        "CombineManyRings (组合戒指)"),
        ("spacechase0.BiggerBackpack",    "Bigger Backpack (超大背包)"),
        ("Pathoschild.ContentPatcher",    "Content Patcher (数据/贴图)"),
        ("Pathoschild.Automate",          "Automate (自动化机器)"),
        ("Pathoschild.TractorMod",        "Tractor Mod (拖拉机)"),
        ("DaLion.Professions",            "Walk Of Life - Rebirth (职业循环)"),
        ("Cornucopia.MoreFlowers",        "Cornucopia 更多花 (自定义作物)"),
        ("MissAnaira.StarCrops",          "Star Crops (星空作物)"),
        ("Rafseazz.RidgesideVillage",     "Ridgeside Village (新地区/NPC)"),
        ("maxmakesmods.deepwoodsmod",     "Deep Woods (深林地区)"),
        ("aedenthorn.CustomBush",         "Custom Bush (自定义灌木)"),
        ("DIGUS.CustomCaskMod",           "Custom Cask Mod (自定义酒桶)"),
        ("UncleArya.BetterCrystalarium",  "Better Crystalarium (宝石复制机扩展)"),
        ("Celestia87.MoreGreenhouses",    "MoreGreenhouses (更多温室)"),
        ("sameerxxe.SuperMassiveGreenhouse", "Super Massive Greenhouse (超大型温室)"),
        ("Celestia87.MoreCellars",         "BuildMoreCellars (更多酒窖)"),
        ("damentia.MarniesAutoPetters",    "Marnie's Auto-Petters (玛尼卖自动抚摸机)"),
        ("yourdorkbrains.CP.RobinSellsBC", "Robin Sells Big Craftables (罗宾卖大型制作物)"),
        ("ofts.jojaExp",                   "Joja Express (Joja网购/快递)"),
        ("AltoIgloo.ShopTabs",             "Shop Tabs (交易选项卡)"),
        ("weizinai.AutoBreakGeode",        "Auto Break Geode (自动破晶洞)"),
        ("Dragoon23.ForgeEnchantment",     "Pick Forge Enchantment (定向附魔)"),
        ("Stari.ManyEnchantments",         "Many Enchantments (附魔冲突修复)"),
        ("hawkfalcon.BetterJunimos",       "Better Junimos (更好的祝尼魔)"),
        ("PeacefulEnd.MultipleMiniObelisks","Multiple Mini Obelisks (传送石碑)"),
        ("EternalSoap.RemoteFridgeStorage","Remote Fridge Storage (远程冰箱/箱子)"),
        ("FlyingTNT.ResourceStorage",      "Resource Storage (大宗资源存储)"),
        ("Pathoschild.ChestsAnywhere",     "Chests Anywhere (远程箱子)"),
        ("Hong.MoreMonsters",              "More Monsters (更多怪物)"),
        ("otc.supplycratesonbeach",        "Supply Crates on Beach (海滩补给箱)"),
        ("SkullCavernElevator",            "Skull Cavern Elevator (骷髅电梯)"),
        ("Luo.TheyStayWithYou",            "They Stay With You (与你同行)"),
        ("Cherry.PlatonicRelationships",   "Platonic Relationships (柏拉图爱情)"),
        ("Exblosis.LetsMoveIt",            "Lets Move It (移动一切)"),
        ("recon88.HarvestSeedsContinued",  "Harvest Seeds Continued (种子掉落)"),
        ("mizzion.increasedartifactspots", "Increased Artifact Spots (远古斑点)"),
        ("season.ShareExperience",         "Share Experience (共享经验)"),
        ("Rakiin.AutomaticGates",          "Automatic Gates (自动门)"),
        ("ChibiKyu.FishingAssistant2",     "Fishing Assistant (自动钓鱼)"),
        ("LunaticShade.SkillfulClothes",   "Skillful Clothes (衣服效果)"),
    };

    public CompatLayer(IEnumerable<string> installed, ModConfig config, IMonitor monitor)
    {
        _installed = new HashSet<string>(installed);
        _enableAutoCompat = config.enableAutoCompat;
        _overrides = config.compatOverrides ?? new Dictionary<string, bool>();
        _monitor = monitor;

        foreach (var (id, _) in Profiles)
            if (IsActive(id)) _enabled.Add(id);
    }

    public bool IsInstalled(string id) => _installed.Contains(id);

    /// <summary>Whether this UniqueId's compat profile is active (override, else auto-detect).</summary>
    public bool IsActive(string id)
    {
        if (_overrides.TryGetValue(id, out var v)) return v;
        return _enableAutoCompat && _installed.Contains(id);
    }

    // Convenience flags for the built-in profiles.
    public bool HasRingSlotMod   => IsActive("bcmpinc.WearMoreRings") || IsActive("Stari.CombineManyRings");
    public bool HasBackpackMod   => IsActive("spacechase0.BiggerBackpack");
    public bool HasProfessionMod => IsActive("DaLion.Professions");
    public bool HasCustomPlantMod => IsActive("Cornucopia.MoreFlowers") || IsActive("MissAnaira.StarCrops") || IsActive("aedenthorn.CustomBush");
    public bool HasRegionMod     => IsActive("Rafseazz.RidgesideVillage") || IsActive("maxmakesmods.deepwoodsmod");
    public int EnabledCount => _enabled.Count;
    public int ProfileCount => Profiles.Length;

    /// <summary>Comma-separated labels of the active built-in profiles ("" if none).</summary>
    public string Describe()
    {
        string active = string.Join(", ", Profiles.Where(p => _enabled.Contains(p.Id)).Select(p => p.Label));
        return active.Length > 0 ? active : "(未激活兼容profile)";
    }

    public void Log()
    {
        int installedProfiles = Profiles.Count(p => _installed.Contains(p.Id));
        _monitor.Log($"CompatLayer: {installedProfiles} 个已装兼容模组, {_enabled.Count} 个激活 -> {Describe()}", LogLevel.Info);
    }
}

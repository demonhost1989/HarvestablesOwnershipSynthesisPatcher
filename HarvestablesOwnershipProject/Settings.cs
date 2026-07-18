using Newtonsoft.Json;
using System.ComponentModel;


namespace HarvestablesOwnership
{

    [JsonObject]
    public class ConventionOverrideEntry
    {

        [DisplayName("Match Pattern")]
        [Description("For Cell/Location: a substring to match (partial matching). For Plugin Name: a wildcard pattern (e.g. '*farmmod*.esp').")]
        [JsonProperty]
        public string EditorID { get; set; } = string.Empty;

        [DisplayName("Faction EditorID")]
        [Description("The EditorID of the faction that should own harvestables matching the pattern above.")]
        [JsonProperty]
        public string FactionEditorID { get; set; } = string.Empty;
    }


    [JsonObject]
    public class PluginFactionOverrideEntry
    {

        [DisplayName("Plugin Name (partial matching)")]
        [Description("A substring of the plugin file name that placed the Harvestable (e.g. 'MyFarmMod').")]
        [JsonProperty]
        public string PluginName { get; set; } = string.Empty;

        [DisplayName("Faction EditorID")]
        [Description("The EditorID of the faction that should own harvestables placed by matching plugins.")]
        [JsonProperty]
        public string FactionEditorID { get; set; } = string.Empty;
    }


    [JsonObject]
    public class Settings
    {

        [DisplayName("harvestables to patch")]
        [Description("Substring match against the placed object's (FLOR/TREE) EditorID. Only placed objects whose base record is Flora AND matches one of these terms are considered at all.")]
        [JsonProperty]
        public List<string> IncludeHarvestableTerms { get; set; } =
        [
            "Wheat", "Potato", "Cabbage", "Leek", "Gourd", "Garlic", "Tomato", "Salmon", "Fish", "Wallmounted", "DTA", "HayBale",
            "Pumpkin", "Carrot", "ChickenNest", "Hanging", "Harvest", "ElvesEar", "FrostMirriam", "SnowberryWreath", "Antler", "Skull",
        ];

        [DisplayName("Names to exclude")]
        [Description("Substring match against the Flora EditorID. Used to filter out decorative/wild variants that happen to match an include term above.")]
        [JsonProperty]
        public List<string> ExcludeNameTerms { get; set; } =
        [
            "Moss", "FX", "SlaughterfishEgg", "BYOH", 
        ];

        [DisplayName("Plugins to exclude")]
        [Description("ExcludePlugins")]
        [JsonProperty]
        public List<string> ExcludePlugins { get; set; } =
        [
            "SkyrimUnderground", "HearthFire", "Glenmoril",
        ];

        [DisplayName("Cells to exclude")]
        [Description("ExcludeCellRules")]
        [JsonProperty]
        public List<string> ExcludeCellRules { get; set; } =
        [
            "BYOH", "Helgen", 
        ];

        [DisplayName("Location Types to exclude")]
        [Description("ExcludeLocTypeRules")]
        [JsonProperty]
        public List<string> ExcludeLocTypeRules { get; set; } =
        [
            "Dungeon", "AnimalDen", "Bandit", "DragonLair", "Draugr", "Dwarven",
            "Falmer", "GiantCamp", "Hagraven", "Spriggan", "Vampire", "Warlock",
            "Werewolf", "Forsworn", "Cave", "Ruin", "PlayerHouse", "Lair",
        ];

        [DisplayName("Plugin overrides (Plugin name contains -> Faction EditorID)")]
        [Description("harvestables placed by a matching plugin are assigned to the given faction, taking precedence over location-based matching.")]
        [JsonProperty]
        public List<PluginFactionOverrideEntry> PluginFactionOverrides { get; set; } =
        [
            new() { PluginName = "Whiterun", FactionEditorID = "TownWhiterunFaction" },
            new() { PluginName = "Solitude", FactionEditorID = "TownSolitudeFaction" },
            new() { PluginName = "Riften", FactionEditorID = "TownRiftenFaction" },
            new() { PluginName = "Windhelm", FactionEditorID = "TownWindhelmFaction" },
            new() { PluginName = "Markarth", FactionEditorID = "TownMarkarthFaction" },
            new() { PluginName = "Falkreath", FactionEditorID = "TownFalkreathFaction" },
            new() { PluginName = "Morthal", FactionEditorID = "TownMorthalFaction" },
            new() { PluginName = "Dawnstar", FactionEditorID = "TownDawnstarFaction" },
            new() { PluginName = "Winterhold", FactionEditorID = "TownWinterholdFaction" },
            new() { PluginName = "DragonBridge", FactionEditorID = "TownDragonBridgeFaction" },
            new() { PluginName = "Dragon Bridge", FactionEditorID = "TownDragonBridgeFaction" },
            new() { PluginName = "Ivarstead", FactionEditorID = "TownIvarsteadFaction" },
            new() { PluginName = "Karthwasten", FactionEditorID = "TownKarthwastenFaction" },
            new() { PluginName = "Riverwood", FactionEditorID = "TownRiverwoodFaction" },
            new() { PluginName = "Rorikstead", FactionEditorID = "TownRoriksteadFaction" },
            new() { PluginName = "Kynesgrove", FactionEditorID = "TownKynesgroveFaction" },
            new() { PluginName = "Nightgate", FactionEditorID = "NightgateInnFaction" },
            new() { PluginName = "OldHroldan", FactionEditorID = "TownOldHroldanFaction" },
            new() { PluginName = "Old Hroldan", FactionEditorID = "TownOldHroldanFaction" },
            new() { PluginName = "ShorsStone", FactionEditorID = "TownShorsStoneFaction" },
            new() { PluginName = "Shor's Stone", FactionEditorID = "TownShorsStoneFaction" },
            new() { PluginName = "DarkwaterCrossing", FactionEditorID = "TownDarkwaterCrossingFaction" },
        ];

        [DisplayName("Convention overrides (Cell/Location EditorID -> Faction EditorID)")]
        [Description("Be careful not to use too broad terms! EditorID can be either a CELL or a LOCATION EditorID.")]
        [JsonProperty]
        public List<ConventionOverrideEntry> ConventionOverrides { get; set; } =
        [
            // Vanilla Towns
            new() { EditorID = "Whiterun", FactionEditorID = "TownWhiterunFaction" },
            new() { EditorID = "Solitude", FactionEditorID = "TownSolitudeFaction" },
            new() { EditorID = "Riften", FactionEditorID = "TownRiftenFaction" },
            new() { EditorID = "Windhelm", FactionEditorID = "TownWindhelmFaction" },
            new() { EditorID = "Markarth", FactionEditorID = "TownMarkarthFaction" },
            new() { EditorID = "Falkreath", FactionEditorID = "TownFalkreathFaction" },
            new() { EditorID = "Morthal", FactionEditorID = "TownMorthalFaction" },
            new() { EditorID = "Dawnstar", FactionEditorID = "TownDawnstarFaction" },
            new() { EditorID = "Winterhold", FactionEditorID = "TownWinterholdFaction" },
            new() { EditorID = "DragonBridge", FactionEditorID = "TownDragonBridgeFaction" },
            new() { EditorID = "Ivarstead", FactionEditorID = "TownIvarsteadFaction" },
            new() { EditorID = "Karthwasten", FactionEditorID = "TownKarthwastenFaction" },
            new() { EditorID = "Riverwood", FactionEditorID = "TownRiverwoodFaction" },
            new() { EditorID = "Rorikstead", FactionEditorID = "TownRoriksteadFaction" },
            new() { EditorID = "Kynesgrove", FactionEditorID = "TownKynesgroveFaction" },
            new() { EditorID = "Nightgate", FactionEditorID = "NightgateInnFaction" },
            new() { EditorID = "OldHroldan", FactionEditorID = "TownOldHroldanFaction" },
            new() { EditorID = "ShorsStone", FactionEditorID = "TownShorsStoneFaction" },
            new() { EditorID = "Shor's Stone", FactionEditorID = "TownShorsStoneFaction" },
            new() { EditorID = "DarkwaterCrossing", FactionEditorID = "TownDarkwaterCrossingFaction" },
            new() { EditorID = "MixwaterMill", FactionEditorID = "MixwaterMillGilfreHouseFaction" },

            // Misc Locations
            new() { EditorID = "DarkBrotherhoodSanctuary", FactionEditorID = "DarkBrotherhoodFaction" },
            new() { EditorID = "DawnstarSanctuaryLocation", FactionEditorID = "DarkBrotherhoodFaction" },
            new() { EditorID = "DragonBridgeFourShieldsTavern", FactionEditorID = "DragonBridgeFourShieldsInnFaction" },
            new() { EditorID = "HonningbrewMeadery", FactionEditorID = "HonningbrewMeaderyFaction" },
            new() { EditorID = "AngisCampExterior", FactionEditorID = "WIGenericCrimeFaction" },
            new() { EditorID = "LeftHandMine", FactionEditorID = "TownLeftHandMineFaction" },
            new() { EditorID = "Stonehills", FactionEditorID = "TownStonehillsFaction" },
            new() { EditorID = "BluePalace", FactionEditorID = "SolitudeBluePalaceFaction" },
            new() { EditorID = "WinterholdCollege", FactionEditorID = "CollegeofWinterholdFaction" },
            new() { EditorID = "SoljundsSinkholeMinersHouse", FactionEditorID = "TownSoljundsSinkholeFaction" },
            new() { EditorID = "FrokisShackExterior", FactionEditorID = "WIGenericCrimeFaction" },
            new() { EditorID = "EastEmpireWarehouse", FactionEditorID = "TG04EastEmpireFaction" },
                        
            // Modded Locations
            new() { EditorID = "BearsCaveMillLocation", FactionEditorID = "RG439BearsCaveMillFaction" },
            new() { EditorID = "KynesgroveFarmsLocationTGCoKG", FactionEditorID = "KynesgroveRagnasAndHerleifsHouseFactionTGCoKG" },
            new() { EditorID = "KynesgroveGalasSteadLocationTGCoKG", FactionEditorID = "KynesgroveGalasHouseFactionTGCoKG" },
            new() { EditorID = "0BearQOrigin", FactionEditorID = "TownWindhelmFaction" },
            new() { EditorID = "NightgateInn", FactionEditorID = "NG_JobMerchantFaction" },
            new() { EditorID = "0WindhelmExtDwelling", FactionEditorID = "WindhelmSurWheelhouseFaction" },
            new() { EditorID = "GraniteHill", FactionEditorID = "TownGraniteHillFaction" },
            new() { EditorID = "HalloftheVigilant", FactionEditorID = "VigilantOfStendarrFaction" },
            new() { EditorID = "WBPT", FactionEditorID = "SolitudeBluePalaceFaction" },

            // DLC Locations
            new() { EditorID = "TelMithryn", FactionEditorID = "TelMithrynFaction" },
            new() { EditorID = "DLC2SkaalVillageLocation", FactionEditorID = "DLC2SVGreathallFaction" },
            new() { EditorID = "DLC2SkaalVillage", FactionEditorID = "DLC2SkaalVillageCitizenFaction" },
            new() { EditorID = "DLC2SV", FactionEditorID = "DLC2SkaalVillageCitizenFaction" },
            new() { EditorID = "DLC2RavenRockLocation", FactionEditorID = "DLC2RRBulwarkFaction" },
            new() { EditorID = "Dawnguard", FactionEditorID = "DLC1DawnguardFaction" },
            new() { EditorID = "HunterWorld", FactionEditorID = "DLC1DawnguardFaction" },
            new() { EditorID = "RavenRock", FactionEditorID = "DLC2CrimeRavenRockFaction" },
            new() { EditorID = "Skaal", FactionEditorID = "DLC2SVGreathallFaction" },

            // Orc Strongholds
            new() { EditorID = "DushnikhYal", FactionEditorID = "TownDushnikhYalFaction" },
            new() { EditorID = "Largashbur", FactionEditorID = "TownLargashburFaction" },
            new() { EditorID = "MorKhazgur", FactionEditorID = "TownMorKhazgurFaction" },
            new() { EditorID = "Narzulbur", FactionEditorID = "TownNarzulburFaction" },
        ];
    }
}

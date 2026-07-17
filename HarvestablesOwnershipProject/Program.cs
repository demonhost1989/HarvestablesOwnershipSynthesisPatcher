using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using Newtonsoft.Json;
using Noggog;


namespace HarvestablesOwnership
{
    public class Program
    {
        // ------------------------------------------------------------------
        // Settings load/save
        // ------------------------------------------------------------------

        // Without Replace, Json.NET appends deserialized list entries onto the defaults from the
        // property initializers, duplicating every rule/override once a settings file exists.
        private static readonly JsonSerializerSettings SettingsJsonOptions = new()
        {
            ObjectCreationHandling = ObjectCreationHandling.Replace,
        };

        public static Settings Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new Settings();
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<Settings>(json, SettingsJsonOptions) ?? new Settings();
        }

        public void Save(string path)
        {
            var json = JsonConvert.SerializeObject(this, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(path, json);
        }

        static Lazy<Settings> LazySettings = new();
        static Settings Settings => LazySettings.Value;

        // ------------------------------------------------------------------
        // Console output helpers
        // ------------------------------------------------------------------

        private static bool _lastWasDivider = false;

        private static void PrintDivider()
        {
            if (_lastWasDivider) return;
            Console.WriteLine("------------------------------------------------------------------------------------------------------------------------");
            _lastWasDivider = true;
        }

        private static void PrintShortDivider()
        {
            if (_lastWasDivider) return;
            Console.WriteLine("------------------------------------------------------------");
            _lastWasDivider = true;
        }

        private static void ConsoleWriteLine(string text)
        {
            Console.WriteLine(text);
            _lastWasDivider = false;
        }

        // ------------------------------------------------------------------
        // Small utility helpers
        // ------------------------------------------------------------------

        // Adds an entry to a "skipped crops by cell" dictionary, creating the list if needed.
        private static void AddSkip(
            Dictionary<string, List<(string Crop, string Plugin, string Reason)>> dict,
            string crop,
            string plugin,
            string cellEdid,
            string reason)
        {
            string key = cellEdid ?? "(unknown cell)";

            if (!dict.TryGetValue(key, out var list))
            {
                list = [];
                dict[key] = list;
            }

            list.Add((crop, plugin, reason));
        }

        // Partial-match (substring) plugin exclusion.
        private static bool IsPluginExcluded(string pluginName)
        {
            return Settings.ExcludePlugins.Any(pattern =>
                pluginName.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        }

        // Partial-match (substring) cell exclusion.
        private static bool RuleMatchesCell(string rule, string cellEdid)
        {
            return cellEdid.Contains(rule, StringComparison.OrdinalIgnoreCase);
        }

        // Partial-match (substring) plugin rule, used only in the summary/report section.
        private static bool RuleMatchesPlugin(string rule, string pluginName)
        {
            return pluginName.Contains(rule, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // Location categorization
        // ------------------------------------------------------------------

        public enum LocationCategory
        {
            Town, Farm, Unknown, Mill, Wilderness, Stable, Stronghold, Palace, Urban,
        }

        public static (LocationCategory category, ILocationGetter? matched) CategorizeLocation(
            ILocationGetter? location,
            ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
            ICellGetter? cell)
        {
            // Location-based keyword detection. Substring matching, because the real keyword
            // EditorIDs are "LocTypeFarm", "LocTypeTown", etc. — an exact set lookup of "Farm"
            // would never match anything. Restricted to the "LocType" prefix specifically, since
            // locations also carry unrelated keyword data (Civil War flags, world-interaction
            // flags like "WIDragonAttacked") that can share vocabulary with these terms without
            // meaning the same thing.
            if (location != null)
            {
                var keywordEdids = location.Keywords?
                    .Select(k => k.TryResolve(linkCache)?.EditorID)
                    .Where(e => e != null && e.StartsWith("LocType", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (keywordEdids != null)
                {
                    bool HasKeyword(string term) =>
                        keywordEdids.Any(k => k.Contains(term, StringComparison.OrdinalIgnoreCase));

                    if (HasKeyword("Farm"))
                        return (LocationCategory.Farm, location);

                    if (HasKeyword("Mill"))
                        return (LocationCategory.Mill, location);

                    if (HasKeyword("Settlement") || HasKeyword("Town")
                        || HasKeyword("City") || HasKeyword("Village"))
                        return (LocationCategory.Town, location);

                    if (HasKeyword("Castle") || HasKeyword("Palace") || HasKeyword("Temple"))
                        return (LocationCategory.Palace, location);

                    if (HasKeyword("OrcStronghold"))
                        return (LocationCategory.Stronghold, location);

                    if (HasKeyword("Cemetery")
                        || HasKeyword("Dwelling")
                        || HasKeyword("Guild")
                        || HasKeyword("Habitation")
                        || HasKeyword("Inn")
                        || HasKeyword("Store"))
                        return (LocationCategory.Urban, location);
                }
            }

            // Cell EditorID detection (fallback when there's no location or no useful keywords)
            if (cell?.EditorID is string edid)
            {

                if (edid.Contains("wilderness", StringComparison.OrdinalIgnoreCase))
                    return (LocationCategory.Wilderness, location);

                if (edid.Contains("farm", StringComparison.OrdinalIgnoreCase))
                    return (LocationCategory.Farm, location);

                if (edid.Contains("mill", StringComparison.OrdinalIgnoreCase))
                    return (LocationCategory.Mill, location);

                if (edid.Contains("stable", StringComparison.OrdinalIgnoreCase))
                    return (LocationCategory.Stable, location);

                if (edid.Contains("village", StringComparison.OrdinalIgnoreCase)
                    || edid.Contains("settlement", StringComparison.OrdinalIgnoreCase)
                    || edid.Contains("town", StringComparison.OrdinalIgnoreCase)
                    || edid.Contains("city", StringComparison.OrdinalIgnoreCase))
                    return (LocationCategory.Town, location);

                if (edid.Contains("castle", StringComparison.OrdinalIgnoreCase)
                    || edid.Contains("palace", StringComparison.OrdinalIgnoreCase)
                    || edid.Contains("temple", StringComparison.OrdinalIgnoreCase))
                    return (LocationCategory.Palace, location);

                if (edid.Contains("cemetary", StringComparison.OrdinalIgnoreCase)
                    || edid.Contains("dwelling", StringComparison.OrdinalIgnoreCase)
                    || edid.Contains("guild", StringComparison.OrdinalIgnoreCase)
                    || edid.Contains("habitation", StringComparison.OrdinalIgnoreCase)
                    || edid.Contains("inn", StringComparison.OrdinalIgnoreCase)
                    || edid.Contains("dwelling", StringComparison.OrdinalIgnoreCase)
                    || edid.Contains("store", StringComparison.OrdinalIgnoreCase))
                    return (LocationCategory.Urban, location);


            }

            return (LocationCategory.Unknown, null);
        }

        // ------------------------------------------------------------------
        // Faction resolution
        // ------------------------------------------------------------------

        // Cell/Location EditorID -> Faction EditorID convention overrides, populated from
        // Settings.ConventionOverrides at the start of each run (see RunPatch). Naming
        // conventions across mods aren't standardized, so this can't be fully caught by
        // logic alone.
        private static Dictionary<string, string> ConventionOverrides = new(StringComparer.OrdinalIgnoreCase);

        // Finds a convention override for a given EditorID using partial (substring, either
        // direction) matching. The longest matching key wins, so a specific key like
        // "KynesgroveFarmsLocationTGCoKG" beats a broad one like "Kynesgrove".
        private static bool TryFindPartialConventionOverride(string editorId, out string factionEdid)
        {
            factionEdid = string.Empty;
            if (string.IsNullOrWhiteSpace(editorId))
                return false;

            var match = ConventionOverrides
                .Where(kvp => !string.IsNullOrEmpty(kvp.Key)
                    && (editorId.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase)
                        || kvp.Key.Contains(editorId, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(kvp => kvp.Key.Length)
                .FirstOrDefault();

            if (string.IsNullOrEmpty(match.Value))
                return false;

            factionEdid = match.Value;
            return true;
        }

        // Resolves an override's faction EditorID to an actual faction record (exact first, then fuzzy).
        private static IFactionGetter? ResolveOverrideFaction(string factionEdid, Dictionary<string, IFactionGetter> factionsByEdid)
        {
            if (string.IsNullOrWhiteSpace(factionEdid))
                return null;

            if (factionsByEdid.TryGetValue(factionEdid, out var exact))
                return exact;

            return TryFuzzyFactionMatch(factionEdid, factionsByEdid);
        }

        // Generates root candidates from an EditorID by stripping trailing digits (e.g. "Name01" -> "Name").
        // Always yields the raw value first; each candidate is returned at most once.
        private static IEnumerable<string> GetRootsFromEditorId(string? editorId)
        {
            if (string.IsNullOrWhiteSpace(editorId))
                yield break;

            var cleaned = editorId.Trim();
            yield return cleaned;

            var digitsStripped = cleaned;
            while (digitsStripped.Length > 0 && char.IsDigit(digitsStripped[^1]))
                digitsStripped = digitsStripped[..^1];

            if (digitsStripped.Length > 0 && !string.Equals(digitsStripped, cleaned, StringComparison.OrdinalIgnoreCase))
                yield return digitsStripped;
        }

        // Common suffixes stripped from a location's base name to build extra faction-name candidates
        // (e.g. "LemkilsFarmLocation" -> base "LemkilsFarm" -> also try "Lemkils").
        private static readonly string[] LocationNameSuffixes =
            ["Farm", "House", "Meadery", "Mill", "Village", "Stead", "Hold", "Location", "Exterior", "Interior", "Faction"];

        // Tries to find a faction whose EditorID ends with "Faction" and contains the given term.
        // This is the fuzzy fallback used when no exact "<BaseName><Kind>Faction" candidate exists,
        // to tolerate mods that use slightly different naming (prefixes/suffixes/minor variations).
        private static IFactionGetter? TryFuzzyFactionMatch(string term, Dictionary<string, IFactionGetter> factionsByEdid)
        {
            if (string.IsNullOrWhiteSpace(term))
                return null;

            return factionsByEdid.Values.FirstOrDefault(f =>
                f.EditorID != null
                && f.EditorID.EndsWith("Faction", StringComparison.OrdinalIgnoreCase)
                && f.EditorID.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        // Shared logic for the Town/Farm/Mill naming-convention lookups: strips a known suffix off the
        // EditorID, builds the expected "<BaseName><Kind>Faction" candidate, and falls back to a fuzzy
        // match (and optionally a set of extra root candidates) if no exact match is found.
        private static IFactionGetter? TryFindFactionByConvention(
            string editorId,
            string stripSuffix,
            Func<string, string> buildCandidateName,
            Dictionary<string, IFactionGetter> factionsByEdid,
            IEnumerable<string>? extraRoots = null)
        {
            var baseName = editorId.EndsWith(stripSuffix, StringComparison.OrdinalIgnoreCase)
                ? editorId[..^stripSuffix.Length]
                : editorId;

            var candidateName = buildCandidateName(baseName);
            if (factionsByEdid.TryGetValue(candidateName, out var exact))
                return exact;

            var fuzzy = TryFuzzyFactionMatch(baseName, factionsByEdid);
            if (fuzzy != null)
                return fuzzy;

            if (extraRoots != null)
            {
                foreach (var root in extraRoots.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var rootMatch = TryFuzzyFactionMatch(root, factionsByEdid);
                    if (rootMatch != null)
                        return rootMatch;
                }
            }

            return null;
        }

        // Builds "TownXFaction"-style root candidates by stripping common location-name suffixes,
        // plus a digit-stripped variant (e.g. "Riverwood01" -> "Riverwood").
        private static IEnumerable<string> GetTownRootCandidates(string baseName)
        {
            var current = baseName;
            yield return current;

            while (true)
            {
                var stripped = StripOneLayer(current);
                if (stripped.Length == 0 || stripped == current)
                    yield break;

                current = stripped;
                yield return current;
            }
        }

        private static string StripOneLayer(string name)
        {
            // Trailing digits first, since they usually come after the suffix (e.g. "WhiterunExterior01")
            var end = name.Length;
            while (end > 0 && char.IsDigit(name[end - 1]))
                end--;
            if (end < name.Length)
                return name[..end];

            foreach (var suffix in LocationNameSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
                    return name[..^suffix.Length];
            }

            return name; // nothing left to strip
        }

        // Resolves a faction purely via naming conventions (Town/Farm/Mill/Sawmill patterns against
        // the location EditorID, then a Town-pattern fallback against the cell EditorID). Contains
        // no convention-override lookups at all — those are handled separately by
        // TryGetConventionOverrideFaction, so callers can control precedence between the two.
        private static IFactionGetter? TryGetNamingConventionFaction(
            ILocationGetter? location,
            Dictionary<string, IFactionGetter> factionsByEdid,
            ICellGetter? cell)
        {
            // Naming conventions against the location EditorID.
            if (location?.EditorID != null)
            {
                // Town<n>Faction
                var townFaction = TryFindFactionByConvention(
                    location.EditorID,
                    stripSuffix: "Location",
                    buildCandidateName: baseName => $"Town{baseName}Faction",
                    factionsByEdid,
                    extraRoots: GetTownRootCandidates(
                        location.EditorID.EndsWith("Location", StringComparison.OrdinalIgnoreCase)
                            ? location.EditorID[..^"Location".Length]
                            : location.EditorID));
                if (townFaction != null)
                    return townFaction;

                // <n>FarmFaction
                var farmFaction = TryFindFactionByConvention(
                    location.EditorID,
                    stripSuffix: "FarmLocation",
                    buildCandidateName: baseName => $"{baseName}FarmFaction",
                    factionsByEdid);
                if (farmFaction != null)
                    return farmFaction;

                // <n>MillFaction, with an extra fallback against the cell's EditorID roots
                var millFaction = TryFindFactionByConvention(
                    location.EditorID,
                    stripSuffix: "MillLocation",
                    buildCandidateName: baseName => $"{baseName}MillFaction",
                    factionsByEdid,
                    extraRoots: GetRootsFromEditorId(cell?.EditorID));
                if (millFaction != null)
                    return millFaction;

                // <n>SawmillFaction, with an extra fallback against the cell's EditorID roots
                var sawMillFaction = TryFindFactionByConvention(
                    location.EditorID,
                    stripSuffix: "SawmillLocation",
                    buildCandidateName: baseName => $"{baseName}SawmillFaction",
                    factionsByEdid,
                    extraRoots: GetRootsFromEditorId(cell?.EditorID));
                if (sawMillFaction != null)
                    return sawMillFaction;
            }

            // Naming conventions against the cell EditorID, for cells whose location is missing
            // or prefixed in a way the location pass can't strip (e.g. cell "SnowShodFarmExterior"
            // -> root "SnowShodFarm" -> TownSnowShodFarmFaction, even though the location EDID
            // carries a "Riften" prefix).
            if (cell?.EditorID != null)
            {
                var cellFaction = TryFindFactionByConvention(
                    cell.EditorID,
                    stripSuffix: "Exterior",
                    buildCandidateName: baseName => $"Town{baseName}Faction",
                    factionsByEdid,
                    extraRoots: GetTownRootCandidates(cell.EditorID));
                if (cellFaction != null)
                    return cellFaction;
            }

            return null;
        }

        // Resolves a faction purely via Settings.ConventionOverrides (exact match against the Cell
        // or Location EditorID first, then a broad partial/substring match as a catch-all). Contains
        // no naming-convention logic — see TryGetNamingConventionFaction for that.
        private static IFactionGetter? TryGetConventionOverrideFaction(
            ILocationGetter? location,
            Dictionary<string, IFactionGetter> factionsByEdid,
            ICellGetter? cell)
        {
            string?[] editorIds = [cell?.EditorID, location?.EditorID];

            // Exact convention overrides.
            foreach (var edid in editorIds)
            {
                if (edid != null && ConventionOverrides.TryGetValue(edid, out var overrideEdid))
                {
                    var faction = ResolveOverrideFaction(overrideEdid, factionsByEdid);
                    if (faction != null)
                        return faction;
                }
            }

            // Partial convention overrides as the broad catch-all fallback.
            foreach (var edid in editorIds)
            {
                if (edid == null)
                    continue;

                foreach (var candidate in GetRootsFromEditorId(edid))
                {
                    if (TryFindPartialConventionOverride(candidate, out var overrideEdid))
                    {
                        var faction = ResolveOverrideFaction(overrideEdid, factionsByEdid);
                        if (faction != null)
                            return faction;
                    }
                }
            }

            return null;
        }

        // Finds a faction for crops placed by a specific plugin (Settings.PluginFactionOverrides,
        // partial plugin-name matching). First matching entry that resolves to a real faction wins.
        private static IFactionGetter? TryGetPluginFactionOverride(
            string pluginName,
            Settings settings,
            Dictionary<string, IFactionGetter> factionsByEdid)
        {
            foreach (var entry in settings.PluginFactionOverrides)
            {
                if (string.IsNullOrWhiteSpace(entry.PluginName) || string.IsNullOrWhiteSpace(entry.FactionEditorID))
                    continue;

                if (!pluginName.Contains(entry.PluginName.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                var faction = ResolveOverrideFaction(entry.FactionEditorID.Trim(), factionsByEdid);
                if (faction != null)
                    return faction;
            }

            return null;
        }

        // Walks up the placed-object's context chain to find its containing cell, re-resolving through
        // the link cache to guarantee the fully-merged winning override (rather than a minimal stub
        // from whichever plugin owns the placed reference, which can be missing the EDID subrecord).
        private static ICellGetter? FindContainingCell(
            IModContext<ISkyrimMod, ISkyrimModGetter, IPlacedObject, IPlacedObjectGetter> context,
            ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
        {
            var current = context.Parent;
            while (current != null)
            {
                if (current.Record is ICellGetter cell)
                {
                    if (linkCache.TryResolve<ICellGetter>(cell.FormKey, out var winningCell))
                        return winningCell;

                    return cell;
                }

                current = current.Parent;
            }

            return null;
        }

        // ------------------------------------------------------------------
        // Main patching pass
        // ------------------------------------------------------------------

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            var settings = LoadRunSettings(state);
            PopulateConventionOverrides(settings);

            var factionsByEdid = new Dictionary<string, IFactionGetter>(StringComparer.OrdinalIgnoreCase);
            foreach (var fac in state.LoadOrder.PriorityOrder.Faction().WinningOverrides())
            {
                if (fac.EditorID != null)
                    factionsByEdid.TryAdd(fac.EditorID, fac);
            }

            var seen = new HashSet<FormKey>();

            var patchedCropsByCell = new Dictionary<string, List<(string Crop, string Plugin, string? OwnerFaction)>>(StringComparer.OrdinalIgnoreCase);
            var skippedCropsByCell = new Dictionary<string, List<(string Crop, string Plugin, string Reason)>>(StringComparer.OrdinalIgnoreCase);
            var excludedCropsByPlugin = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var excludedCellsByRule = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var excludedLocTypesByRule = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var excludedNamesByRule = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var cropTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var patchedCropTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int unknownCount = 0;
            int missingFactionCount = 0;
            int patchedCount = 0;
            int alreadyOwnedCount = 0;
            int excludedCount = 0;

            var unknownSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missingFactionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var patchedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var alreadyOwnedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excludedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            PrintShortDivider();
            ConsoleWriteLine("PATCHING...".PadLeft(35));
            PrintShortDivider();

            // Memoizes the "is this Base record a matching crop?" check by FormKey. A huge number of
            // placed objects share the exact same Base record (e.g. every wheat stalk in the game
            // points at the same TreeFloraWheat01 record), so resolving and re-checking it fresh for
            // every single placed object is redundant work. This caches the result per unique Base
            // FormKey — identical logic/results to a fresh check every time, just not repeated.
            var baseRecordCropCache = new Dictionary<FormKey, string?>();

            foreach (var context in state.LoadOrder.PriorityOrder.PlacedObject().WinningContextOverrides(state.LinkCache))
            {
                var placedObject = context.Record;

                var baseFormKeyNullable = placedObject.Base.FormKeyNullable;
                if (baseFormKeyNullable is not { } baseFormKey)
                    continue;

                if (!baseRecordCropCache.TryGetValue(baseFormKey, out var cachedCropEdid))
                {
                    // Not seen this Base record before — resolve it directly against the same link
                    // cache the placement itself uses, and only accept it if it's actually Flora or
                    // Tree (resolved directly rather than via a separately-built enumeration, so
                    // nothing falls through an enumeration mismatch).
                    cachedCropEdid = null;
                    if (state.LinkCache.TryResolve<IMajorRecordGetter>(baseFormKey, out var baseRecord)
                        && baseRecord is (IFloraGetter or ITreeGetter)
                        && baseRecord.EditorID is string resolvedEdid
                        && settings.IncludeHarvestableTerms.Any(term => resolvedEdid.Contains(term, StringComparison.OrdinalIgnoreCase)))
                    {
                        cachedCropEdid = resolvedEdid;
                    }

                    baseRecordCropCache[baseFormKey] = cachedCropEdid;
                }

                if (cachedCropEdid == null)
                    continue;

                var cropEdid = cachedCropEdid;

                if (!seen.Add(placedObject.FormKey))
                    continue;

                var containingCell = FindContainingCell(context, state.LinkCache);

                // Cells without an EditorID (e.g. many exterior cells) are treated as unknown.
                var cellEdid = containingCell?.EditorID ?? "Unknown cell";

                string pluginName = placedObject.FormKey.ModKey.FileName;

                var displayCrop = cropEdid;

                cropTypeCounts.TryGetValue(displayCrop, out var cropCount);
                cropTypeCounts[displayCrop] = cropCount + 1;

                if (!placedObject.Owner.IsNull)
                {
                    alreadyOwnedCount++;
                    alreadyOwnedSet.Add(cropEdid);
                    continue;
                }

                // Wildcard-aware... no, partial-match cell exclusion.
                bool cellExcluded = false;
                foreach (var rule in settings.ExcludeCellRules)
                {
                    if (RuleMatchesCell(rule, cellEdid))
                    {
                        cellExcluded = true;
                        if (!excludedCellsByRule.TryGetValue(rule, out var cellList))
                            excludedCellsByRule[rule] = cellList = [];

                        cellList.Add(cropEdid);
                        break;
                    }
                }

                // Location-type exclusion (matched against location keywords like "Dungeon")
                //
                // Resolved once here and reused below for the eventual naming-convention/
                // convention-override matching too, instead of resolving Location twice.
                var location = containingCell?.Location.TryResolve(state.LinkCache);

                if (!cellExcluded && settings.ExcludeLocTypeRules.Count > 0)
                {
                    // Only genuine location-TYPE classifier keywords (Bethesda's convention: always
                    // prefixed "LocType...", e.g. LocTypeFarm, LocTypeDungeon). Locations also carry a
                    // lot of unrelated keyword data — Civil War flags (CW...), world-interaction flags
                    // (WI...) like "WIDragonAttacked" — that happen to share vocabulary with our rules
                    // (e.g. "Dragon") without meaning the same thing. Filtering to the LocType prefix
                    // avoids matching those unrelated flags.
                    var keywordEdids = location?.Keywords?
                        .Select(k => k.TryResolve(state.LinkCache)?.EditorID)
                        .Where(e => e != null && e.StartsWith("LocType", StringComparison.OrdinalIgnoreCase))
                        .Select(e => e!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    if (keywordEdids != null && keywordEdids.Count > 0)
                    {
                        foreach (var rule in settings.ExcludeLocTypeRules)
                        {
                            if (keywordEdids.Any(k => k.Contains(rule, StringComparison.OrdinalIgnoreCase)))
                            {
                                cellExcluded = true;
                                if (!excludedLocTypesByRule.TryGetValue(rule, out var list))
                                    excludedLocTypesByRule[rule] = list = [];

                                list.Add(cropEdid);
                                break;
                            }
                        }
                    }
                }

                if (cellExcluded)
                {
                    excludedCount++;
                    excludedSet.Add(cropEdid);
                    continue;
                }

                if (IsPluginExcluded(pluginName))
                {
                    if (!excludedCropsByPlugin.TryGetValue(pluginName, out var list))
                        excludedCropsByPlugin[pluginName] = list = [];

                    list.Add(cropEdid);
                    excludedCount++;
                    excludedSet.Add(cropEdid);
                    continue;
                }

                var matchedNameTerm = settings.ExcludeNameTerms
                    .FirstOrDefault(term => cropEdid.Contains(term, StringComparison.OrdinalIgnoreCase));
                if (matchedNameTerm != null)
                {
                    if (!excludedNamesByRule.TryGetValue(matchedNameTerm, out var list))
                        excludedNamesByRule[matchedNameTerm] = list = [];

                    list.Add(cropEdid);
                    excludedCount++;
                    excludedSet.Add(cropEdid);
                    continue;
                }

                // Matching. The raw location is passed through so naming-convention and
                // convention-override lookups still run even when categorization came up
                // Unknown (the category only affects the skip reason, not matching itself).
                var (category, _) = CategorizeLocation(location, state.LinkCache, containingCell);

                // Precedence: the patcher's regular naming-convention logic runs first, then
                // Settings.ConventionOverrides acts as a fallback, and Settings.PluginFactionOverrides
                // is the last resort.
                var townFaction = TryGetNamingConventionFaction(location, factionsByEdid, containingCell)
                    ?? TryGetConventionOverrideFaction(location, factionsByEdid, containingCell)
                    ?? TryGetPluginFactionOverride(pluginName, settings, factionsByEdid);

                if (townFaction == null)
                {
                    missingFactionCount++;
                    missingFactionSet.Add(cropEdid);

                    var reason = category == LocationCategory.Unknown
                        ? "No suitable owner, No suitable location"
                        : "No suitable owner";

                    if (category == LocationCategory.Unknown)
                    {
                        unknownCount++;
                        unknownSet.Add(cropEdid);
                    }

                    AddSkip(skippedCropsByCell, cropEdid, pluginName, cellEdid, reason);
                    continue;
                }

                var patchObject = context.GetOrAddAsOverride(state.PatchMod);
                patchObject.Owner.SetTo(townFaction);
                patchObject.FactionRank = 0;
                patchedCount++;
                patchedSet.Add(cropEdid);

                patchedCropTypeCounts.TryGetValue(displayCrop, out var patchedCropCount);
                patchedCropTypeCounts[displayCrop] = patchedCropCount + 1;

                if (!patchedCropsByCell.TryGetValue(cellEdid, out var patchedList))
                    patchedCropsByCell[cellEdid] = patchedList = [];

                patchedList.Add((cropEdid, pluginName, townFaction.EditorID));
            }

            PrintReport(
                settings,
                patchedCropsByCell,
                skippedCropsByCell,
                excludedCropsByPlugin,
                excludedCellsByRule,
                excludedLocTypesByRule,
                excludedNamesByRule,
                patchedCropTypeCounts,
                patchedCount,
                alreadyOwnedCount,
                missingFactionCount,
                unknownCount,
                excludedCount);
        }

        // Loads (or generates) the settings file used for this run.
        private static Settings LoadRunSettings(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            string[] tryNames = ["Settings.json", "settings.json"];
            string? configContent = null;

            foreach (var name in tryNames)
            {
                try
                {
                    configContent = state.RetrieveConfigFile(name);
                    break;
                }
                catch (FileNotFoundException)
                {
                    // try next name
                }
            }

            if (configContent is null)
            {
                var defaultSettings = LazySettings.Value;
                configContent = JsonConvert.SerializeObject(defaultSettings, Newtonsoft.Json.Formatting.Indented);
                try
                {
                    var outPath = Path.Combine(Environment.CurrentDirectory, tryNames[0]);
                    File.WriteAllText(outPath, configContent);
                    ConsoleWriteLine($"Generated default config file: {tryNames[0]}");
                }
                catch (IOException ioEx)
                {
                    ConsoleWriteLine($"WARNING: Failed to write default config file: {ioEx.Message}");
                }
            }

            try
            {
                return JsonConvert.DeserializeObject<Settings>(configContent!, SettingsJsonOptions) ?? LazySettings.Value;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                ConsoleWriteLine("WARNING: Could not parse Settings File; using defaults.");
                return LazySettings.Value;
            }
        }

        // Populates the ConventionOverrides lookup from Settings.ConventionOverrides for this run.
        private static void PopulateConventionOverrides(Settings settings)
        {
            ConventionOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new List<string>();

            foreach (var entry in settings.ConventionOverrides)
            {
                if (string.IsNullOrWhiteSpace(entry.EditorID) || string.IsNullOrWhiteSpace(entry.FactionEditorID))
                    continue;

                var key = entry.EditorID.Trim();
                var value = entry.FactionEditorID.Trim();

                if (!ConventionOverrides.TryAdd(key, value))
                    duplicates.Add(key);
            }

            if (duplicates.Count > 0)
            {
                ConsoleWriteLine($"WARNING: Duplicate Convention Override EditorIDs were ignored (first entry wins): {string.Join(", ", duplicates)}");
            }
        }

        // ------------------------------------------------------------------
        // Reporting
        // ------------------------------------------------------------------

        private static void PrintReport(
            Settings settings,
            Dictionary<string, List<(string Crop, string Plugin, string? OwnerFaction)>> patchedCropsByCell,
            Dictionary<string, List<(string Crop, string Plugin, string Reason)>> skippedCropsByCell,
            Dictionary<string, List<string>> excludedCropsByPlugin,
            Dictionary<string, List<string>> excludedCellsByRule,
            Dictionary<string, List<string>> excludedLocTypesByRule,
            Dictionary<string, List<string>> excludedNamesByRule,
            Dictionary<string, int> patchedCropTypeCounts,
            int patchedCount,
            int alreadyOwnedCount,
            int missingFactionCount,
            int unknownCount,
            int excludedCount)
        {
            var totalPatched = patchedCropsByCell.Values.SelectMany(v => v).Count();

            _lastWasDivider = false;
            PrintShortDivider();
            ConsoleWriteLine("PATCHED BY CELL".PadLeft(36));
            ConsoleWriteLine($"Total patched: {totalPatched}".PadLeft(37));
            PrintShortDivider();

            foreach (var kvp in patchedCropsByCell.OrderByDescending(k => k.Value.Count))
            {
                var cellLabel = kvp.Key;
                var crops = kvp.Value;

                ConsoleWriteLine($"{cellLabel}   ({crops.Count} patched)");

                var byPlugin = crops
                    .GroupBy(a => a.Plugin)
                    .Select(g => new { Plugin = g.Key, Count = g.Count(), Crops = g.ToList() })
                    .OrderByDescending(p => p.Count);

                foreach (var pluginGroup in byPlugin)
                {
                    ConsoleWriteLine($"     [{pluginGroup.Plugin}] ({pluginGroup.Count})");

                    var byCrop = pluginGroup.Crops
                        .GroupBy(a => new { a.Crop, a.OwnerFaction })
                        .Select(g => new { g.Key.Crop, g.Key.OwnerFaction, Count = g.Count() })
                        .OrderByDescending(a => a.Count);

                    foreach (var entry in byCrop)
                    {
                        ConsoleWriteLine($"          {entry.Count} {entry.Crop}(s)   now owned by:   {entry.OwnerFaction}");
                    }
                }

                PrintDivider();
            }

            var totalSkipped = skippedCropsByCell.Values.SelectMany(v => v).Count();

            _lastWasDivider = false;
            PrintShortDivider();
            ConsoleWriteLine("SKIPPED BY CELL".PadLeft(35));
            ConsoleWriteLine($"Total skipped: {totalSkipped}".PadLeft(36));
            PrintShortDivider();

            foreach (var kvp in skippedCropsByCell.OrderByDescending(k => k.Value.Count))
            {
                var cellLabel = kvp.Key;
                var crops = kvp.Value;

                ConsoleWriteLine($"{cellLabel}   ({crops.Count} skipped)");

                var byPlugin = crops
                    .GroupBy(a => a.Plugin)
                    .Select(g => new { Plugin = g.Key, Count = g.Count(), Crops = g.ToList() })
                    .OrderByDescending(p => p.Count);

                foreach (var pluginGroup in byPlugin)
                {
                    ConsoleWriteLine($"     [{pluginGroup.Plugin}] ({pluginGroup.Count})");

                    var byCrop = pluginGroup.Crops
                        .GroupBy(a => new { a.Crop, a.Reason })
                        .Select(g => new { g.Key.Crop, g.Key.Reason, Count = g.Count() })
                        .OrderByDescending(a => a.Count);

                    foreach (var entry in byCrop)
                    {
                        ConsoleWriteLine($"          {entry.Count} {entry.Crop}   Returned: {entry.Reason}");
                    }
                }

                PrintDivider();
            }

            _lastWasDivider = false;
            PrintShortDivider();
            ConsoleWriteLine("EXCLUSION SUMMARY".PadLeft(37));
            PrintShortDivider();

            var combined = new List<(string Rule, int Count, string Type)>();

            foreach (var rule in settings.ExcludePlugins)
            {
                int count = excludedCropsByPlugin
                    .Where(kv => RuleMatchesPlugin(rule, kv.Key))
                    .SelectMany(kv => kv.Value)
                    .Count();

                if (count > 0)
                    combined.Add((rule, count, "plugin"));
            }

            foreach (var rule in settings.ExcludeCellRules)
            {
                if (excludedCellsByRule.TryGetValue(rule, out var cells) && cells.Count > 0)
                    combined.Add((rule, cells.Count, "cell"));
            }

            foreach (var rule in settings.ExcludeLocTypeRules)
            {
                if (excludedLocTypesByRule.TryGetValue(rule, out var names) && names.Count > 0)
                    combined.Add((rule, names.Count, "loctype"));
            }

            foreach (var term in settings.ExcludeNameTerms)
            {
                int count = excludedNamesByRule
                    .Where(kvp => kvp.Key.Contains(term, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(kvp => kvp.Value)
                    .Count();

                if (count > 0)
                    combined.Add((term, count, "name"));
            }

            foreach (var entry in combined.OrderByDescending(e => e.Count))
            {
                ConsoleWriteLine($"The rule: {entry.Rule} ({entry.Type}) excluded {entry.Count} crops");
            }

            _lastWasDivider = false;
            PrintShortDivider();
            ConsoleWriteLine("GENERAL SUMMARY".PadLeft(35));
            PrintShortDivider();

            var summaryLines = new List<(string Label, int Count, bool ShowCrops)>
            {
                ("Crops have been assigned owners", patchedCount, true),
                ("Crops were already owned", alreadyOwnedCount, false),
                ("Crops had no suitable owner", missingFactionCount, false),
                ("Crops were in an unsuitable location", unknownCount, false),
                ("Crops were excluded by rules", excludedCount, false),
            };

            foreach (var (label, count, showCrops) in summaryLines.OrderByDescending(l => l.Count))
            {
                ConsoleWriteLine($"{count} {label}");

                if (showCrops)
                {
                    foreach (var kvp in patchedCropTypeCounts.OrderByDescending(k => k.Value))
                    {
                        ConsoleWriteLine($"    {kvp.Value}  {kvp.Key}");
                    }
                }
            }

            PrintDivider();
            ConsoleWriteLine("Patching is complete! Scroll up to read a report on what was patched, skipped, and excluded.");
            ConsoleWriteLine("A couple of notes on the summaries: In the General Summary there is typically a large overlap between no suitable owner and an unsuitable location, since they can both be true.");
            ConsoleWriteLine("The Exclusion Summary displays the crops who would have been patched by the logic were it not for exclusion rules.");
            PrintDivider();
        }

        // ------------------------------------------------------------------
        // Entry point
        // ------------------------------------------------------------------

        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .SetAutogeneratedSettings(
                    "Settings",
                    "settings.json",
                    out LazySettings)
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetTypicalOpen(GameRelease.SkyrimSE, "CropOwnershipOverrides.esp")
                .Run(args);
        }
    }
}
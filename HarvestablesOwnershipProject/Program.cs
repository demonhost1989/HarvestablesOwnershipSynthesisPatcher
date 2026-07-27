using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using Newtonsoft.Json;
using Noggog;
using System.Diagnostics;


namespace HarvestablesOwnership
{
    public class Program
    {
        // ------------------------------------------------------------------
        // Settings load/save
        // ------------------------------------------------------------------

        // Replace prevents Json.NET from appending onto the default list entries when deserializing.
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

        public static void Save(Settings settings, string path)
        {
            var json = JsonConvert.SerializeObject(settings, Newtonsoft.Json.Formatting.Indented);
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
        private static bool IsPluginExcluded(string pluginName, Settings settings)
        {
            return settings.ExcludePlugins.Any(pattern =>
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
        // Faction resolution
        // ------------------------------------------------------------------

        // Cell/Location EditorID -> Faction EditorID overrides from Settings.Overrides, shared by the "Overrides" (exact) and "Manual rule (cell)" (fuzzy) tiers.
        private static Dictionary<string, string> OverridesByEdid = new(StringComparer.OrdinalIgnoreCase);

        // Same entries as OverridesByEdid, pre-sorted longest-key-first so TryFindPartialOverride never has to re-sort.
        private static List<KeyValuePair<string, string>> OverridesByKeyLengthDesc = [];

        // Finds an override via substring match (either direction); longest matching key wins.
        private static bool TryFindPartialOverride(string editorId, out string factionEdid)
        {
            factionEdid = string.Empty;
            if (string.IsNullOrWhiteSpace(editorId))
                return false;

            foreach (var kvp in OverridesByKeyLengthDesc)
            {
                if (editorId.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase)
                    || kvp.Key.Contains(editorId, StringComparison.OrdinalIgnoreCase))
                {
                    factionEdid = kvp.Value;
                    return true;
                }
            }

            return false;
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

        // Common suffixes stripped from a location's base name to build extra faction-name candidates.
        private static readonly string[] LocationNameSuffixes =
            ["Farm", "House", "Meadery", "Mill", "Village", "Stead", "Hold", "Location", "Exterior", "Interior", "Faction"];

        // Memo cache for TryFuzzyFactionMatch, cleared once per run.
        private static readonly Dictionary<string, IFactionGetter?> FuzzyFactionMatchCache = new(StringComparer.OrdinalIgnoreCase);

        // Fuzzy fallback: finds a faction whose EditorID ends with "Faction" and contains the given term.
        private static IFactionGetter? TryFuzzyFactionMatch(string term, Dictionary<string, IFactionGetter> factionsByEdid)
        {
            if (string.IsNullOrWhiteSpace(term))
                return null;

            if (FuzzyFactionMatchCache.TryGetValue(term, out var cached))
                return cached;

            var match = factionsByEdid.Values.FirstOrDefault(f =>
                f.EditorID != null
                && f.EditorID.EndsWith("Faction", StringComparison.OrdinalIgnoreCase)
                && f.EditorID.Contains(term, StringComparison.OrdinalIgnoreCase));

            FuzzyFactionMatchCache[term] = match;
            return match;
        }

        // Shared Town/Farm/Mill naming-convention lookup: strip suffix, build candidate name, fall back to fuzzy match.
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

        // Builds "TownXFaction"-style root candidates by stripping common location-name suffixes and trailing digits.
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

        // Priority 4: "Naming Convention" — Town/Farm/Mill/Sawmill patterns against location/cell EditorID.
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

            // Naming conventions against the cell EditorID, for cells lacking or mismatching a location EditorID.
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

        // Priority 1 (highest priority): "Plugin-local faction match" — instead of searching every
        // faction in the load order, this restricts the search to factions ORIGINALLY DEFINED BY the
        // same plugin that placed the item (e.g. a farm mod's own "Soc_MyFarm_Whiterun" faction for a
        // crop that same mod placed in WhiterunExterior01), then fuzzy-matches the cell/location
        // EditorID's root against those plugin-local factions' EditorIDs. This exists because a mod can
        // define its own faction for a vanilla town (rather than reusing the vanilla "TownXFaction")
        // and clearly intends its own items to go to its own faction — that should win over every other
        // tier, including the vanilla Overrides list.
        private static IFactionGetter? TryGetPluginLocalFactionMatch(
            string pluginName,
            ILocationGetter? location,
            ICellGetter? cell,
            Dictionary<string, List<IFactionGetter>> factionsByPlugin)
        {
            if (!factionsByPlugin.TryGetValue(pluginName, out var localFactions) || localFactions.Count == 0)
                return null;

            string?[] editorIds = [cell?.EditorID, location?.EditorID];

            foreach (var edid in editorIds)
            {
                if (edid == null)
                    continue;

                foreach (var root in GetTownRootCandidates(edid))
                {
                    var match = localFactions.FirstOrDefault(f =>
                        f.EditorID != null &&
                        (f.EditorID.Contains(root, StringComparison.OrdinalIgnoreCase)
                            || root.Contains(f.EditorID, StringComparison.OrdinalIgnoreCase)));

                    if (match != null)
                        return match;
                }
            }

            return null;
        }

        // Priority 2: "Overrides" — exact EditorID match (cell or location) against Settings.Overrides.
        private static IFactionGetter? TryGetExactOverrideFaction(
            ILocationGetter? location,
            Dictionary<string, IFactionGetter> factionsByEdid,
            ICellGetter? cell)
        {
            string?[] editorIds = [cell?.EditorID, location?.EditorID];

            foreach (var edid in editorIds)
            {
                if (edid != null && OverridesByEdid.TryGetValue(edid, out var overrideEdid))
                {
                    var faction = ResolveOverrideFaction(overrideEdid, factionsByEdid);
                    if (faction != null)
                        return faction;
                }
            }

            return null;
        }

        // Priority 5: "Manual rule (cell)" — broad substring/fuzzy match over the same Settings.Overrides entries.
        private static IFactionGetter? TryGetPartialOverrideFaction(
            ILocationGetter? location,
            Dictionary<string, IFactionGetter> factionsByEdid,
            ICellGetter? cell)
        {
            string?[] editorIds = [cell?.EditorID, location?.EditorID];

            foreach (var edid in editorIds)
            {
                if (edid == null)
                    continue;

                foreach (var candidate in GetRootsFromEditorId(edid))
                {
                    if (TryFindPartialOverride(candidate, out var overrideEdid))
                    {
                        var faction = ResolveOverrideFaction(overrideEdid, factionsByEdid);
                        if (faction != null)
                            return faction;
                    }
                }
            }

            return null;
        }

        // Priority 3: "Cell owner" — the containing CELL record's own Owner (XOWN) field, if set to a Faction.
        private static IFactionGetter? TryGetCellOwnerFaction(
            ICellGetter? cell,
            ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
        {
            if (cell == null || cell.Owner.IsNull)
                return null;

            return cell.Owner.TryResolve(linkCache) as IFactionGetter;
        }

        // Priority 7: "Ownership vote" — pre-computed majority faction owner among the cell's already-owned objects; last resort.
        private static IFactionGetter? TryGetCellVoteFaction(
            ICellGetter? cell,
            Dictionary<FormKey, (IFactionGetter Faction, int WinningCount, int TotalVotes)> cellOwnershipVotes)
        {
            if (cell == null)
                return null;

            return cellOwnershipVotes.TryGetValue(cell.FormKey, out var vote) ? vote.Faction : null;
        }

        // Priority 6: "Manual rule (plugin)" — Settings.ManualPluginRules, partial plugin-name matching.
        private static IFactionGetter? TryGetManualPluginRuleFaction(
            string pluginName,
            Settings settings,
            Dictionary<string, IFactionGetter> factionsByEdid)
        {
            foreach (var entry in settings.ManualPluginRules)
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

        // Caches the resolved winning ICellGetter by cell FormKey. The chain-walk to find the immediate
        // containing cell (following context.Parent pointers) is cheap in-memory traversal, but the
        // linkCache.TryResolve<ICellGetter> call at the end is not — and many placed objects routinely
        // share the same containing cell, so that resolve was being repeated redundantly for the same
        // cell over and over. Caching by FormKey collapses it to once per unique cell in the load order.
        private static readonly Dictionary<FormKey, ICellGetter?> ResolvedCellCache = new();

        // Walks up the placed-object's context chain to find its containing cell, re-resolved through the link cache.
        private static ICellGetter? FindContainingCell(
            IModContext<ISkyrimMod, ISkyrimModGetter, IPlacedObject, IPlacedObjectGetter> context,
            ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
        {
            var current = context.Parent;
            while (current != null)
            {
                if (current.Record is ICellGetter cell)
                {
                    if (ResolvedCellCache.TryGetValue(cell.FormKey, out var cached))
                        return cached;

                    ICellGetter? resolved = linkCache.TryResolve<ICellGetter>(cell.FormKey, out var winningCell)
                        ? winningCell
                        : cell;

                    ResolvedCellCache[cell.FormKey] = resolved;
                    return resolved;
                }

                current = current.Parent;
            }

            return null;
        }

        // Priority 7 precompute (finalization only): given per-cell faction vote tallies already
        // collected during the main single pass (see RunPatch), picks the winning faction per cell.
        // This used to ALSO do its own full pass over the entire load order to build those tallies —
        // that's now folded into RunPatch's single main pass instead, since WinningContextOverrides is
        // the single most expensive operation in the patcher and was previously running twice per run.
        private static Dictionary<FormKey, (IFactionGetter Faction, int WinningCount, int TotalVotes)> FinalizeCellOwnershipVotes(
            Dictionary<FormKey, Dictionary<FormKey, int>> countsByCell,
            ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
        {
            var winners = new Dictionary<FormKey, (IFactionGetter Faction, int WinningCount, int TotalVotes)>();
            foreach (var (cellKey, factionCounts) in countsByCell)
            {
                var ordered = factionCounts.OrderByDescending(kv => kv.Value).ToList();

                // A tie for first place means no clear majority owner — leave this cell out of the table.
                bool tied = ordered.Count > 1 && ordered[0].Value == ordered[1].Value;
                if (tied)
                    continue;

                if (linkCache.TryResolve<IFactionGetter>(ordered[0].Key, out var winningFaction))
                {
                    var totalVotes = factionCounts.Values.Sum();
                    winners[cellKey] = (winningFaction, ordered[0].Value, totalVotes);
                }
            }

            return winners;
        }

        // ------------------------------------------------------------------
        // Main patching pass
        // ------------------------------------------------------------------

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            var overallStopwatch = Stopwatch.StartNew();

            var settings = LoadRunSettings(state);
            PopulateOverrides(settings);
            FuzzyFactionMatchCache.Clear();
            ResolvedCellCache.Clear();

            var factionLookupStopwatch = Stopwatch.StartNew();
            var factionsByEdid = new Dictionary<string, IFactionGetter>(StringComparer.OrdinalIgnoreCase);
            var factionsByPlugin = new Dictionary<string, List<IFactionGetter>>(StringComparer.OrdinalIgnoreCase);
            foreach (var fac in state.LoadOrder.PriorityOrder.Faction().WinningOverrides())
            {
                if (fac.EditorID != null)
                    factionsByEdid.TryAdd(fac.EditorID, fac);

                // Grouped by the plugin that ORIGINALLY defined the faction (FormKey.ModKey), not
                // whichever plugin's override happens to be winning — that's what "factions available
                // in the plugin" means for the Plugin-local faction match tier.
                string originPlugin = fac.FormKey.ModKey.FileName;
                if (!factionsByPlugin.TryGetValue(originPlugin, out var pluginFactions))
                    factionsByPlugin[originPlugin] = pluginFactions = [];

                pluginFactions.Add(fac);
            }
            factionLookupStopwatch.Stop();

            // Prebuilt (not lazily resolved) map of base-record FormKey -> (Edid, IsOreVein), covering
            // every Flora/Tree/Activator record in the load order matching the include term lists. This
            // used to be filled lazily, once per unique base record, via
            // LinkCache.TryResolve<IMajorRecordGetter> while scanning placed objects — a generic,
            // type-agnostic resolve that's measurably much slower (confirmed via profiling on the sister
            // ValuablesOwnership patcher: ~450 microseconds per call) than a typed group scan
            // (state.LoadOrder.PriorityOrder.Flora().WinningOverrides(), etc.), the same pattern already
            // used for Factions above. One scan per tracked type, done once, means the main placed-object
            // pass below becomes a pure in-memory dictionary lookup with NO LinkCache calls needed for
            // base-record resolution at all.
            var baseRecordPrebuildStopwatch = Stopwatch.StartNew();
            var baseRecordCropCache = new Dictionary<FormKey, (string Edid, bool IsOreVein)>();

            foreach (var flora in state.LoadOrder.PriorityOrder.Flora().WinningOverrides())
            {
                if (flora.EditorID is string edid
                    && settings.IncludeHarvestableTerms.Any(term => edid.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    baseRecordCropCache[flora.FormKey] = (edid, IsOreVein: false);
                }
            }

            foreach (var tree in state.LoadOrder.PriorityOrder.Tree().WinningOverrides())
            {
                if (tree.EditorID is string edid
                    && settings.IncludeHarvestableTerms.Any(term => edid.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    baseRecordCropCache[tree.FormKey] = (edid, IsOreVein: false);
                }
            }

            foreach (var activator in state.LoadOrder.PriorityOrder.Activator().WinningOverrides())
            {
                if (activator.EditorID is string edid
                    && settings.IncludeOreVeinTerms.Any(term => edid.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    baseRecordCropCache[activator.FormKey] = (edid, IsOreVein: true);
                }
            }
            baseRecordPrebuildStopwatch.Stop();

            var seen = new HashSet<FormKey>();

            var patchedCropsByCell = new Dictionary<string, List<(string Crop, string Plugin, string? OwnerFaction, string OwnershipSource, string? VoteDetail)>>(StringComparer.OrdinalIgnoreCase);
            var skippedCropsByCell = new Dictionary<string, List<(string Crop, string Plugin, string Reason)>>(StringComparer.OrdinalIgnoreCase);
            var excludedCropsByPlugin = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var excludedCellsByRule = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var excludedLocTypesByRule = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var excludedNamesByRule = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var excludedOwnersByRule = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var cropTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var patchedCropTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int missingFactionCount = 0;
            int patchedCount = 0;
            int alreadyOwnedCount = 0;
            int excludedCount = 0;

            var missingFactionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var patchedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var alreadyOwnedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excludedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            PrintShortDivider();
            ConsoleWriteLine("PATCHING...".PadLeft(35));
            PrintShortDivider();

            // Per-cell faction vote tallies (Priority 6 input) — built during the single pass below from
            // EVERY already-owned placed object in the load order, regardless of crop/ore-vein type. This
            // has to stay type-agnostic: a cell's ownership signal legitimately comes from furniture,
            // doors, and other non-crop owned objects too, not just harvestables.
            var countsByCell = new Dictionary<FormKey, Dictionary<FormKey, int>>();

            // Placed harvestable/ore-vein references that matched the prebuilt cache and aren't yet
            // owned — collected here instead of processed immediately, because Priority 6 (Ownership
            // vote) can't be resolved correctly until countsByCell is COMPLETE for every cell.
            var candidates = new List<(IModContext<ISkyrimMod, ISkyrimModGetter, IPlacedObject, IPlacedObjectGetter> Context, string Edid, bool IsOreVein)>();

            // Tracks which base records were ACTUALLY placed somewhere in the world (i.e. genuinely
            // referenced by some PlacedObject), as opposed to merely existing in baseRecordCropCache
            // because they matched the include terms. ApplyOreVeinTheftScript must only touch veins
            // that are actually placed — scripting every matching Activator record in the whole load
            // order, including ones nothing ever references, would be a real behavior change from the
            // original lazy-resolution version.
            var placedBaseFormKeys = new HashSet<FormKey>();

            var findCellStopwatch = new Stopwatch();
            int totalPlacedObjectsSeen = 0;

            // ------------------------------------------------------------------
            // Single full pass over the entire load order. WinningContextOverrides is by far the most
            // expensive operation in this patcher — this used to run twice per patcher execution (once
            // here, once in a separate BuildCellOwnershipVotes pass). Folding vote-tallying into this
            // same pass means it now runs once, with vote data and candidate items collected together.
            // ------------------------------------------------------------------
            var pass1Stopwatch = Stopwatch.StartNew();
            foreach (var context in state.LoadOrder.PriorityOrder.PlacedObject().WinningContextOverrides(state.LinkCache))
            {
                totalPlacedObjectsSeen++;
                var placedObject = context.Record;

                if (!seen.Add(placedObject.FormKey))
                    continue;

                bool isOwned = !placedObject.Owner.IsNull;

                // Vote tally — type-agnostic, applies to every owned placed object (see comment above).
                if (isOwned && placedObject.Owner.TryResolve(state.LinkCache) is IFactionGetter existingOwnerFaction)
                {
                    findCellStopwatch.Start();
                    var voteCell = FindContainingCell(context, state.LinkCache);
                    findCellStopwatch.Stop();

                    if (voteCell != null)
                    {
                        if (!countsByCell.TryGetValue(voteCell.FormKey, out var factionCounts))
                            countsByCell[voteCell.FormKey] = factionCounts = new Dictionary<FormKey, int>();

                        factionCounts.TryGetValue(existingOwnerFaction.FormKey, out var voteCount);
                        factionCounts[existingOwnerFaction.FormKey] = voteCount + 1;
                    }
                }

                var baseFormKeyNullable = placedObject.Base.FormKeyNullable;
                if (baseFormKeyNullable is not { } baseFormKey)
                    continue;

                if (!baseRecordCropCache.TryGetValue(baseFormKey, out var matchedBase))
                    continue;

                placedBaseFormKeys.Add(baseFormKey);

                var cropEdid = matchedBase.Edid;
                var isOreVein = matchedBase.IsOreVein;

                cropTypeCounts.TryGetValue(cropEdid, out var cropCount);
                cropTypeCounts[cropEdid] = cropCount + 1;

                if (isOwned)
                {
                    alreadyOwnedCount++;
                    alreadyOwnedSet.Add(cropEdid);
                    continue;
                }

                candidates.Add((context, cropEdid, isOreVein));
            }
            pass1Stopwatch.Stop();

            var voteFinalizeStopwatch = Stopwatch.StartNew();
            var cellOwnershipVotes = FinalizeCellOwnershipVotes(countsByCell, state.LinkCache);
            voteFinalizeStopwatch.Stop();

            // Memoizes the owning-faction resolution by (containing cell, plugin name), since it never depends on the specific crop.
            var cellFactionResolutionCache = new Dictionary<(FormKey? CellKey, string Plugin), (IFactionGetter? Faction, string? Source, string? VoteDetail)>();

            // Memoizes per-cell work that used to be repeated for every crop in that cell: resolving
            // the containing Location, resolving that Location's Keywords, the mine-location gate, and
            // the ExcludeCellRules/ExcludeLocTypeRules checks. None of this depends on the item being
            // patched — only on the cell — so a densely populated cell was redoing the exact same
            // link-cache resolutions and rule scans once per crop instead of once per cell.
            var cellContextCache = new Dictionary<FormKey, (ILocationGetter? Location, bool CellRuleExcluded, string? CellRuleMatched, bool InMineLocation, bool LocTypeExcluded, string? LocTypeRuleMatched)>();

            // Memoizes plugin exclusion by plugin name — same idea, trivial cost either way, but free to cache.
            var pluginExclusionCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            // ------------------------------------------------------------------
            // Second pass — over `candidates` only (an in-memory list, NOT another load-order
            // traversal), so it's cheap regardless of how large the full load order is.
            // ------------------------------------------------------------------
            var pass2Stopwatch = Stopwatch.StartNew();
            foreach (var (context, cropEdid, isOreVein) in candidates)
            {
                var placedObject = context.Record;

                findCellStopwatch.Start();
                var containingCell = FindContainingCell(context, state.LinkCache);
                findCellStopwatch.Stop();

                // Cells without an EditorID (e.g. many exterior cells) are treated as unknown.
                var cellEdid = containingCell?.EditorID ?? "Unknown cell";
                string pluginName = placedObject.FormKey.ModKey.FileName;

                // Cell/Location resolution + cell-rule/mine-gate/loctype exclusion — cached per cell
                // (see cellContextCache above), since none of this depends on the specific item.
                // Dictionary<FormKey,...> requires a non-nullable key, so "no containing cell" (the
                // rare "Unknown cell" case) uses FormKey.Null as a sentinel rather than an actual null.
                var cellCacheKey = containingCell?.FormKey ?? FormKey.Null;
                if (!cellContextCache.TryGetValue(cellCacheKey, out var cellCtx))
                {
                    ILocationGetter? loc = containingCell?.Location.TryResolve(state.LinkCache);

                    bool cellRuleExcluded = false;
                    string? cellRuleMatched = null;
                    foreach (var rule in settings.ExcludeCellRules)
                    {
                        if (RuleMatchesCell(rule, cellEdid))
                        {
                            cellRuleExcluded = true;
                            cellRuleMatched = rule;
                            break;
                        }
                    }

                    // Resolve each keyword's EditorID exactly once, reused for both the mine gate and
                    // the loctype exclusion check below (the original code resolved keywords twice,
                    // separately, for each check — redundant even before this per-cell caching existed).
                    var resolvedKeywordEdids = loc?.Keywords?
                        .Select(k => k.TryResolve(state.LinkCache)?.EditorID)
                        .Where(e => e != null)
                        .Select(e => e!)
                        .ToList() ?? [];

                    bool inMineLocation = resolvedKeywordEdids.Any(edid => settings.MineLocTypeKeywords.Any(mineKeyword =>
                        string.Equals(mineKeyword, edid, StringComparison.OrdinalIgnoreCase)));

                    bool locTypeExcluded = false;
                    string? locTypeRuleMatched = null;
                    if (!cellRuleExcluded && settings.ExcludeLocTypeRules.Count > 0)
                    {
                        var locPrefixes = new[] { "LocType", "LocSet" };
                        var filteredKeywordEdids = resolvedKeywordEdids
                            .Where(e => locPrefixes.Any(p => e.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        if (filteredKeywordEdids.Count > 0)
                        {
                            foreach (var rule in settings.ExcludeLocTypeRules)
                            {
                                if (filteredKeywordEdids.Any(k => k.Contains(rule, StringComparison.OrdinalIgnoreCase)))
                                {
                                    locTypeExcluded = true;
                                    locTypeRuleMatched = rule;
                                    break;
                                }
                            }
                        }
                    }

                    cellCtx = (loc, cellRuleExcluded, cellRuleMatched, inMineLocation, locTypeExcluded, locTypeRuleMatched);
                    cellContextCache[cellCacheKey] = cellCtx;
                }

                var location = cellCtx.Location;

                // Display-only label for report grouping — falls back to the Location's EditorID when
                // the Cell itself has none (very common for unnamed exterior wilderness cells in
                // Skyrim), so "Unknown cell" only appears when there's truly nothing to show. Tagged
                // with which record the name actually came from, since a Cell name and a Location name
                // aren't the same thing and it wasn't always obvious which one was being shown. This is
                // deliberately separate from cellEdid above, which still drives ExcludeCellRules
                // matching unchanged — using the Location fallback there too would silently change
                // which items get excluded, not just how they're labeled in the report.
                var cellDisplayLabel = containingCell?.EditorID != null
                    ? $"{containingCell.EditorID} [Cell]"
                    : location?.EditorID != null
                        ? $"{location.EditorID} [Location]"
                        : "Unknown cell";

                if (cellCtx.CellRuleExcluded)
                {
                    if (!excludedCellsByRule.TryGetValue(cellCtx.CellRuleMatched!, out var cellList))
                        excludedCellsByRule[cellCtx.CellRuleMatched!] = cellList = [];

                    cellList.Add(cropEdid);
                    excludedCount++;
                    excludedSet.Add(cropEdid);
                    continue;
                }

                // Ore-vein-only "friendly mine" gate: skip unless the vein's Location carries a MineLocTypeKeywords keyword.
                if (isOreVein && !cellCtx.InMineLocation)
                {
                    excludedCount++;
                    excludedSet.Add(cropEdid);
                    AddSkip(skippedCropsByCell, cropEdid, pluginName, cellDisplayLabel, "Not in a mine location");
                    continue;
                }

                if (cellCtx.LocTypeExcluded)
                {
                    if (!excludedLocTypesByRule.TryGetValue(cellCtx.LocTypeRuleMatched!, out var list))
                        excludedLocTypesByRule[cellCtx.LocTypeRuleMatched!] = list = [];

                    list.Add(cropEdid);
                    excludedCount++;
                    excludedSet.Add(cropEdid);
                    continue;
                }

                if (!pluginExclusionCache.TryGetValue(pluginName, out var pluginExcluded))
                {
                    pluginExcluded = IsPluginExcluded(pluginName, settings);
                    pluginExclusionCache[pluginName] = pluginExcluded;
                }

                if (pluginExcluded)
                {
                    if (!excludedCropsByPlugin.TryGetValue(pluginName, out var list))
                        excludedCropsByPlugin[pluginName] = list = [];

                    list.Add(cropEdid);
                    excludedCount++;
                    excludedSet.Add(cropEdid);
                    continue;
                }

                var nameExcludeTerms = isOreVein ? settings.ExcludeOreNameTerms : settings.ExcludeNameTerms;
                var matchedNameTerm = nameExcludeTerms
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

                // Resolution depends only on cell/location + plugin, never on the item — cached per (cell, plugin).
                var resolutionKey = (containingCell?.FormKey, pluginName);
                if (!cellFactionResolutionCache.TryGetValue(resolutionKey, out var resolved))
                {
                    // Precedence (highest to lowest); first tier to resolve a faction wins.
                    (Func<IFactionGetter?> Resolve, string Source)[] tiers =
                    [
                        (() => TryGetPluginLocalFactionMatch(pluginName, location, containingCell, factionsByPlugin), "Plugin-local faction match"),
                        (() => TryGetExactOverrideFaction(location, factionsByEdid, containingCell), "Overrides"),
                        (() => TryGetCellOwnerFaction(containingCell, state.LinkCache), "Cell owner"),
                        (() => TryGetNamingConventionFaction(location, factionsByEdid, containingCell), "Naming Convention"),
                        (() => TryGetPartialOverrideFaction(location, factionsByEdid, containingCell), "Manual rule (Cell ID)"),
                        (() => TryGetManualPluginRuleFaction(pluginName, settings, factionsByEdid), "Manual rule (plugin)"),
                        (() => TryGetCellVoteFaction(containingCell, cellOwnershipVotes), "Ownership vote"),
                    ];

                    IFactionGetter? resolvedFaction = null;
                    string? resolvedSource = null;

                    foreach (var (resolve, source) in tiers)
                    {
                        resolvedFaction = resolve();
                        if (resolvedFaction != null)
                        {
                            resolvedSource = source;
                            break;
                        }
                    }

                    // Captured separately so the Ownership Source Summary groups cleanly by tier name.
                    string? resolvedVoteDetail = null;
                    if (resolvedSource == "Ownership vote"
                        && containingCell != null
                        && cellOwnershipVotes.TryGetValue(containingCell.FormKey, out var voteDetail))
                    {
                        resolvedVoteDetail = $"{voteDetail.WinningCount}/{voteDetail.TotalVotes} objects";
                    }

                    resolved = (resolvedFaction, resolvedSource, resolvedVoteDetail);
                    cellFactionResolutionCache[resolutionKey] = resolved;
                }

                var (townFaction, ownershipSource, voteDetailLabel) = resolved;

                if (townFaction == null)
                {
                    missingFactionCount++;
                    missingFactionSet.Add(cropEdid);

                    AddSkip(skippedCropsByCell, cropEdid, pluginName, cellDisplayLabel, "No suitable owner");
                    continue;
                }

                // Owners exclusion rule: skip the item instead of assigning it if the resolved
                // Faction (however it was reached) matches one of settings.ExcludeOwnerFactionTerms.
                var ownerFactionEdid = townFaction.EditorID;
                var matchedOwnerTerm = ownerFactionEdid != null
                    ? settings.ExcludeOwnerFactionTerms.FirstOrDefault(term => ownerFactionEdid.Contains(term, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (matchedOwnerTerm != null)
                {
                    if (!excludedOwnersByRule.TryGetValue(matchedOwnerTerm, out var ownerList))
                        excludedOwnersByRule[matchedOwnerTerm] = ownerList = [];

                    ownerList.Add(cropEdid);
                    excludedCount++;
                    excludedSet.Add(cropEdid);

                    AddSkip(skippedCropsByCell, cropEdid, pluginName, cellDisplayLabel, $"Owner faction excluded ({townFaction.EditorID})");
                    continue;
                }

                var patchObject = context.GetOrAddAsOverride(state.PatchMod);
                patchObject.Owner.SetTo(townFaction);
                patchObject.FactionRank = 0;
                patchedCount++;
                patchedSet.Add(cropEdid);

                patchedCropTypeCounts.TryGetValue(cropEdid, out var patchedCropCount);
                patchedCropTypeCounts[cropEdid] = patchedCropCount + 1;

                if (!patchedCropsByCell.TryGetValue(cellDisplayLabel, out var patchedList))
                    patchedCropsByCell[cellDisplayLabel] = patchedList = [];

                patchedList.Add((cropEdid, pluginName, townFaction.EditorID, ownershipSource ?? "Unknown", voteDetailLabel));
            }
            pass2Stopwatch.Stop();
            overallStopwatch.Stop();


            var theftWarningMessage = state.PatchMod.Messages.AddNew();
            theftWarningMessage.EditorID = "HO_TheftWarningMSG";
            theftWarningMessage.Description = settings.TheftWarningMessageText;

            ApplyOreVeinTheftScript(state, baseRecordCropCache, placedBaseFormKeys, theftWarningMessage);

            PrintReport(
                settings,
                patchedCropsByCell,
                skippedCropsByCell,
                excludedCropsByPlugin,
                excludedCellsByRule,
                excludedLocTypesByRule,
                excludedNamesByRule,
                excludedOwnersByRule,
                patchedCropTypeCounts,
                patchedCount,
                alreadyOwnedCount,
                missingFactionCount,
                excludedCount);

            // Timing instrumentation — kept in place (Stopwatches above still run; the cost is
            // negligible) for future debugging, but the printed breakdown is disabled by default.
            // Uncomment the call below to re-enable the "TIMING BREAKDOWN" console section.
            // PrintTimingReport(
            //     overallStopwatch,
            //     factionLookupStopwatch,
            //     baseRecordPrebuildStopwatch,
            //     pass1Stopwatch,
            //     voteFinalizeStopwatch,
            //     pass2Stopwatch,
            //     findCellStopwatch,
            //     totalPlacedObjectsSeen,
            //     candidates.Count,
            //     baseRecordCropCache.Count);
        }

        // Prints a breakdown of where the run's time actually went. Temporary diagnostic output —
        // safe to trim once the bottleneck is identified, but cheap enough (a handful of Stopwatches)
        // to leave in indefinitely if useful for future tuning on other load orders.
        private static void PrintTimingReport(
            Stopwatch overall,
            Stopwatch factionLookup,
            Stopwatch baseRecordPrebuild,
            Stopwatch pass1,
            Stopwatch voteFinalize,
            Stopwatch pass2,
            Stopwatch findCell,
            int totalPlacedObjectsSeen,
            int candidateCount,
            int uniqueBaseRecordCount)
        {
            _lastWasDivider = false;
            PrintShortDivider();
            ConsoleWriteLine("TIMING BREAKDOWN".PadLeft(36));
            PrintShortDivider();

            ConsoleWriteLine($"Total placed objects scanned: {totalPlacedObjectsSeen}");
            ConsoleWriteLine($"Candidates carried into pass 2: {candidateCount}");
            ConsoleWriteLine($"Valuable base records found (prebuild): {uniqueBaseRecordCount}");
            PrintShortDivider();

            ConsoleWriteLine($"Faction lookup build:          {factionLookup.ElapsedMilliseconds,8} ms");
            ConsoleWriteLine($"Base-record prebuild (3 typed scans): {baseRecordPrebuild.ElapsedMilliseconds,8} ms  (replaces the old per-item LinkCache.TryResolve<IMajorRecordGetter> calls)");
            ConsoleWriteLine($"Pass 1 (full load-order scan): {pass1.ElapsedMilliseconds,8} ms  (now a pure dictionary lookup per item, no LinkCache resolve)");
            ConsoleWriteLine($"Vote finalization:              {voteFinalize.ElapsedMilliseconds,8} ms");
            ConsoleWriteLine($"Pass 2 (candidate processing): {pass2.ElapsedMilliseconds,8} ms");
            ConsoleWriteLine($"Cell-finding (combined, both passes): {findCell.ElapsedMilliseconds,8} ms  (included within Pass 1 and Pass 2 above, broken out separately since it's a suspect)");
            PrintShortDivider();
            ConsoleWriteLine($"TOTAL:                          {overall.ElapsedMilliseconds,8} ms");

            PrintDivider();
        }


        private const string TheftScriptName = "HOOreTheftScript";
        private const string OrePropertyName = "Ore";
        private const string TheftWarningPropertyName = "HO_TheftWarningMSG";

        private static readonly string[] MirroredMiningPropertyNames =
        [
            "ResourceCount", "ResourceCountTotal", "StrikesBeforeCollection",
            "AttackStrikesBeforeCollection", "MineOreToolsList",
        ];

        private static void ApplyOreVeinTheftScript(
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            Dictionary<FormKey, (string Edid, bool IsOreVein)> baseRecordCropCache,
            HashSet<FormKey> placedBaseFormKeys,
            IMessageGetter theftWarningMessage)
        {
            int added = 0;
            int skippedNoOreProperty = 0;
            int skippedResolveFailed = 0;
            int oreVeinBaseRecordsConsidered = 0;

            foreach (var (formKey, info) in baseRecordCropCache)
            {
                if (!info.IsOreVein)
                    continue;

                // baseRecordCropCache is now prebuilt from every matching Flora/Tree/Activator record in
                // the load order (see RunPatch), which includes records that might never be referenced
                // by any PlacedObject. Only script veins that are ACTUALLY placed somewhere in the world
                // — scripting unplaced records would be a silent behavior change from the original
                // lazy-resolution version, which only ever populated this cache for referenced bases.
                if (!placedBaseFormKeys.Contains(formKey))
                    continue;

                oreVeinBaseRecordsConsidered++;

                if (!state.LinkCache.TryResolve<IActivatorGetter>(formKey, out var baseRecord))
                {
                    skippedResolveFailed++;
                    continue;
                }

                var allExistingProperties = baseRecord.VirtualMachineAdapter?.Scripts
                    .SelectMany(s => s.Properties)
                    .ToList() ?? [];

                var oreProperty = allExistingProperties
                    .OfType<IScriptObjectPropertyGetter>()
                    .FirstOrDefault(p => string.Equals(p.Name, OrePropertyName, StringComparison.OrdinalIgnoreCase));

                if (oreProperty == null)
                {
                    skippedNoOreProperty++;
                    continue;
                }

                var activatorOverride = state.PatchMod.Activators.GetOrAddAsOverride(baseRecord);
                var vmadOverride = activatorOverride.VirtualMachineAdapter!;

                List<ScriptProperty> newPropertiesList = [oreProperty.DeepCopy()];

                foreach (var propertyName in MirroredMiningPropertyNames)
                {
                    var existingProperty = allExistingProperties
                        .FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase));

                    if (existingProperty != null)
                        newPropertiesList.Add(existingProperty.DeepCopy());
                }

                newPropertiesList.Add(new ScriptObjectProperty
                {
                    Name = TheftWarningPropertyName,
                    Object = new FormLinkNullable<ISkyrimMajorRecordGetter>(theftWarningMessage.FormKey),
                });

                vmadOverride.Scripts.Add(new ScriptEntry
                {
                    Name = TheftScriptName,
                    Properties = new ExtendedList<ScriptProperty>(newPropertiesList),
                });

                added++;
            }

            ConsoleWriteLine($"Ore vein theft script: {oreVeinBaseRecordsConsidered} distinct ore vein base record(s) considered -> {added} got {TheftScriptName} added, {skippedNoOreProperty} skipped (no readable Ore property found on any existing script), {skippedResolveFailed} skipped (failed to re-resolve as Activator).");
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

        // Populates the OverridesByEdid lookup from Settings.Overrides for this run.
        private static void PopulateOverrides(Settings settings)
        {
            OverridesByEdid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new List<string>();

            foreach (var entry in settings.Overrides)
            {
                if (string.IsNullOrWhiteSpace(entry.EditorID) || string.IsNullOrWhiteSpace(entry.FactionEditorID))
                    continue;

                var key = entry.EditorID.Trim();
                var value = entry.FactionEditorID.Trim();

                if (!OverridesByEdid.TryAdd(key, value))
                    duplicates.Add(key);
            }

            if (duplicates.Count > 0)
            {
                ConsoleWriteLine($"WARNING: Duplicate Override EditorIDs were ignored (first entry wins): {string.Join(", ", duplicates)}");
            }

            OverridesByKeyLengthDesc = OverridesByEdid
                .OrderByDescending(kvp => kvp.Key.Length)
                .ToList();
        }

        // ------------------------------------------------------------------
        // Reporting
        // ------------------------------------------------------------------

        private static void PrintReport(
            Settings settings,
            Dictionary<string, List<(string Crop, string Plugin, string? OwnerFaction, string OwnershipSource, string? VoteDetail)>> patchedCropsByCell,
            Dictionary<string, List<(string Crop, string Plugin, string Reason)>> skippedCropsByCell,
            Dictionary<string, List<string>> excludedCropsByPlugin,
            Dictionary<string, List<string>> excludedCellsByRule,
            Dictionary<string, List<string>> excludedLocTypesByRule,
            Dictionary<string, List<string>> excludedNamesByRule,
            Dictionary<string, List<string>> excludedOwnersByRule,
            Dictionary<string, int> patchedCropTypeCounts,
            int patchedCount,
            int alreadyOwnedCount,
            int missingFactionCount,
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

                // "Unknown cell" and any cell with mixed sources fall back to per-crop display instead of one shared line.
                var decidedByValues = crops
                    .Select(c => c.VoteDetail != null ? $"{c.OwnershipSource} ({c.VoteDetail})" : c.OwnershipSource)
                    .Distinct()
                    .ToList();

                bool isUnknownCell = string.Equals(cellLabel, "Unknown cell", StringComparison.OrdinalIgnoreCase);
                bool showPerCropSource = isUnknownCell || decidedByValues.Count > 1;

                if (showPerCropSource)
                {
                    ConsoleWriteLine($"{cellLabel}   ({crops.Count} patched)");
                }
                else
                {
                    ConsoleWriteLine($"{cellLabel}   ({crops.Count} patched)   decided by: {decidedByValues[0]}");
                }

                var byPlugin = crops
                    .GroupBy(a => a.Plugin)
                    .Select(g => new { Plugin = g.Key, Count = g.Count(), Crops = g.ToList() })
                    .OrderByDescending(p => p.Count);

                foreach (var pluginGroup in byPlugin)
                {
                    ConsoleWriteLine($"     [{pluginGroup.Plugin}] ({pluginGroup.Count})");

                    var byCrop = pluginGroup.Crops
                        .GroupBy(a => new { a.Crop, a.OwnerFaction, a.OwnershipSource, a.VoteDetail })
                        .Select(g => new { g.Key.Crop, g.Key.OwnerFaction, g.Key.OwnershipSource, g.Key.VoteDetail, Count = g.Count() })
                        .OrderByDescending(a => a.Count);

                    foreach (var entry in byCrop)
                    {
                        if (showPerCropSource)
                        {
                            var decidedBy = entry.VoteDetail != null
                                ? $"{entry.OwnershipSource} ({entry.VoteDetail})"
                                : entry.OwnershipSource;

                            ConsoleWriteLine($"          {entry.Count} {entry.Crop}(s)   now owned by:   {entry.OwnerFaction}   (decided by: {decidedBy})");
                        }
                        else
                        {
                            ConsoleWriteLine($"          {entry.Count} {entry.Crop}(s)   now owned by:   {entry.OwnerFaction}");
                        }
                    }
                }

                PrintDivider();
            }

            _lastWasDivider = false;
            PrintShortDivider();
            ConsoleWriteLine("OWNERSHIP SOURCE SUMMARY".PadLeft(41));
            PrintShortDivider();

            var bySource = patchedCropsByCell.Values
                .SelectMany(v => v)
                .GroupBy(a => a.OwnershipSource)
                .Select(g => new { Source = g.Key, Count = g.Count() })
                .OrderByDescending(a => a.Count);

            foreach (var entry in bySource)
            {
                ConsoleWriteLine($"{entry.Count} harvestables were assigned an owner via: {entry.Source}");
            }

            var totalSkipped = skippedCropsByCell.Values.SelectMany(v => v).Count();

            //   _lastWasDivider = false;
            //   PrintShortDivider();
            //   ConsoleWriteLine("SKIPPED BY CELL".PadLeft(35));
            //   ConsoleWriteLine($"Total skipped: {totalSkipped}".PadLeft(36));
            //   PrintShortDivider();
            //
            //   foreach (var kvp in skippedCropsByCell.OrderByDescending(k => k.Value.Count))
            //   {
            //       var cellLabel = kvp.Key;
            //       var crops = kvp.Value;
            //
            //       ConsoleWriteLine($"{cellLabel}   ({crops.Count} skipped)");
            //
            //       var byPlugin = crops
            //           .GroupBy(a => a.Plugin)
            //           .Select(g => new { Plugin = g.Key, Count = g.Count(), Crops = g.ToList() })
            //           .OrderByDescending(p => p.Count);
            //
            //       foreach (var pluginGroup in byPlugin)
            //       {
            //           ConsoleWriteLine($"     [{pluginGroup.Plugin}] ({pluginGroup.Count})");
            //
            //           var byCrop = pluginGroup.Crops
            //               .GroupBy(a => new { a.Crop, a.Reason })
            //               .Select(g => new { g.Key.Crop, g.Key.Reason, Count = g.Count() })
            //               .OrderByDescending(a => a.Count);
            //
            //           foreach (var entry in byCrop)
            //           {
            //               ConsoleWriteLine($"          {entry.Count} {entry.Crop}   Returned: {entry.Reason}");
            //           }
            //       }
            //
            //       PrintDivider();
            //   }

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

            foreach (var term in settings.ExcludeOwnerFactionTerms)
            {
                int count = excludedOwnersByRule
                    .Where(kvp => kvp.Key.Contains(term, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(kvp => kvp.Value)
                    .Count();

                if (count > 0)
                    combined.Add((term, count, "owner"));
            }

            foreach (var entry in combined.OrderByDescending(e => e.Count))
            {
                ConsoleWriteLine($"The rule: {entry.Rule} ({entry.Type}) excluded {entry.Count} harvestables");
            }

            _lastWasDivider = false;
            PrintShortDivider();
            ConsoleWriteLine("GENERAL SUMMARY".PadLeft(35));
            PrintShortDivider();

            var summaryLines = new List<(string Label, int Count, bool ShowCrops)>
            {
                ("Harvestables have been assigned owners", patchedCount, true),
                ("Harvestables were already owned", alreadyOwnedCount, false),
                ("Harvestables had no suitable owner", missingFactionCount, false),
                ("Harvestables were excluded by rules", excludedCount, false),
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
            ConsoleWriteLine("Patching is complete! Scroll up to read the report.");
            //    ConsoleWriteLine("The Exclusion Summary displays the crops who would have been patched by the logic were it not for exclusion rules.");
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
                .SetTypicalOpen(GameRelease.SkyrimSE, "HarvestablesOwnershipOverrides.esp")
                .Run(args);
        }
    }
}
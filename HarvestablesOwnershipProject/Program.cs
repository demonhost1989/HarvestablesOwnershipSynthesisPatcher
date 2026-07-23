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

        // Cell/Location EditorID -> Faction EditorID overrides, populated from Settings.Overrides
        // at the start of each run (see RunPatch). Naming conventions across mods aren't
        // standardized, so this can't be fully caught by logic alone. The same entries here
        // serve two different priority tiers: exact-match lookups run first as "Overrides"
        // (Priority 1), and a broad substring/fuzzy fallback over the same entries runs much
        // later as "Manual rule (cell)" (Priority 5) — see TryGetExactOverrideFaction and
        // TryGetPartialOverrideFaction.
        private static Dictionary<string, string> OverridesByEdid = new(StringComparer.OrdinalIgnoreCase);

        // Same entries as OverridesByEdid, pre-sorted longest-key-first once per run (in
        // PopulateOverrides) instead of being re-sorted via LINQ on every single
        // TryFindPartialOverride call. Since crop placements are processed by the
        // thousands and each can probe several candidate strings, re-sorting per call was one
        // of the hotter allocations in the patch loop.
        private static List<KeyValuePair<string, string>> OverridesByKeyLengthDesc = [];

        // Finds an override for a given EditorID using partial (substring, either
        // direction) matching. The longest matching key wins, so a specific key like
        // "KynesgroveFarmsLocationTGCoKG" beats a broad one like "Kynesgrove". Because the list
        // is already sorted longest-key-first, the first match found is the longest match —
        // same result as the old Where().OrderByDescending().FirstOrDefault(), just without
        // re-sorting every time.
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

        // Memo cache for TryFuzzyFactionMatch, cleared once per run (see RunPatch). The same
        // handful of base-name terms (e.g. "Riverwood", "Whiterun") get probed over and over
        // as every crop in a cell runs the same naming-convention chain, and each probe was
        // previously a full linear scan of every faction in the load order.
        private static readonly Dictionary<string, IFactionGetter?> FuzzyFactionMatchCache = new(StringComparer.OrdinalIgnoreCase);

        // Tries to find a faction whose EditorID ends with "Faction" and contains the given term.
        // This is the fuzzy fallback used when no exact "<BaseName><Kind>Faction" candidate exists,
        // to tolerate mods that use slightly different naming (prefixes/suffixes/minor variations).
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
        // no override lookups at all — those are handled separately by TryGetExactOverrideFaction
        // and TryGetPartialOverrideFaction, so callers can control precedence between the three.
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

        // Priority 1: "Overrides" — exact EditorID match (cell or location) against
        // Settings.Overrides. This is the highest-priority tier: a deliberate, curated entry
        // here beats everything else, including Cell owner and the ownership vote. Contains no
        // fuzzy/substring matching — see TryGetPartialOverrideFaction for that (Priority 5,
        // "Manual rule (cell)").
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

        // Priority 5: "Manual rule (cell)" — a broad substring/fuzzy match over the very same
        // Settings.Overrides entries used by TryGetExactOverrideFaction, but only consulted much
        // later in the chain, after Cell owner, the ownership vote, and Naming Convention have
        // all failed to produce a faction. This is deliberately the loosest/least-confident tier
        // short of the plugin-name fallback, since a broad pattern here is more likely to produce
        // a false-positive match than an exact one.
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

        // Priority 2: "Cell owner" — the containing CELL record's own Owner field (the same
        // XOWN-style ownership data used for trespass/crime detection), if the CELL itself has
        // one set and it resolves to a Faction (NPC-owned cells are ignored here; we only assign
        // faction ownership).
        private static IFactionGetter? TryGetCellOwnerFaction(
            ICellGetter? cell,
            ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
        {
            if (cell == null || cell.Owner.IsNull)
                return null;

            return cell.Owner.TryResolve(linkCache) as IFactionGetter;
        }

        // Priority 3: "Ownership vote" — looks up the pre-computed majority faction owner among
        // all already-owned placed objects in this cell (see BuildCellOwnershipVotes). Returns
        // null (falling through to Naming Convention) if the cell wasn't in the precomputed table
        // at all — meaning either no already-owned objects were found there, or the vote was tied.
        private static IFactionGetter? TryGetCellVoteFaction(
            ICellGetter? cell,
            Dictionary<FormKey, (IFactionGetter Faction, int WinningCount, int TotalVotes)> cellOwnershipVotes)
        {
            if (cell == null)
                return null;

            return cellOwnershipVotes.TryGetValue(cell.FormKey, out var vote) ? vote.Faction : null;
        }

        // Finds a faction for crops placed by a specific plugin (Settings.ManualPluginRules,
        // partial plugin-name matching). First matching entry that resolves to a real faction
        // wins. This is Priority 6, the last resort in the chain — it only runs once every other
        // tier has failed to produce an owner.
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

        // Priority 3 precompute: "Ownership vote". Scans every already-owned placed object in the
        // load order (any type, not just harvestables) and tallies, per cell, which faction owns
        // the most of them. Run once per patch as a full separate pass (rather than inline in the
        // main loop) since the main loop only ever looks at harvestable placements — this needs
        // to see every placed object's ownership, including non-crop furniture/containers/etc.,
        // to know who "controls" a cell. NPC-owned placements are ignored (only Faction owners
        // count towards the vote, since that's all we ever assign). A cell with no already-owned
        // objects, or with a tie for the top spot, is left out of the returned table entirely —
        // callers should treat a missing entry as "no vote result", falling through to the next
        // priority tier (Naming Convention). WinningCount/TotalVotes are carried along purely for
        // reporting, so the printout can show how many objects actually decided each vote.
        private static Dictionary<FormKey, (IFactionGetter Faction, int WinningCount, int TotalVotes)> BuildCellOwnershipVotes(
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            var countsByCell = new Dictionary<FormKey, Dictionary<FormKey, int>>();

            foreach (var context in state.LoadOrder.PriorityOrder.PlacedObject().WinningContextOverrides(state.LinkCache))
            {
                var placedObject = context.Record;

                if (placedObject.Owner.IsNull)
                    continue;

                // Only Faction owners count toward the vote; NPC-owned placements are skipped.
                if (placedObject.Owner.TryResolve(state.LinkCache) is not IFactionGetter ownerFaction)
                    continue;

                var cell = FindContainingCell(context, state.LinkCache);
                if (cell == null)
                    continue;

                if (!countsByCell.TryGetValue(cell.FormKey, out var factionCounts))
                    countsByCell[cell.FormKey] = factionCounts = new Dictionary<FormKey, int>();

                factionCounts.TryGetValue(ownerFaction.FormKey, out var count);
                factionCounts[ownerFaction.FormKey] = count + 1;
            }

            var winners = new Dictionary<FormKey, (IFactionGetter Faction, int WinningCount, int TotalVotes)>();
            foreach (var (cellKey, factionCounts) in countsByCell)
            {
                var ordered = factionCounts.OrderByDescending(kv => kv.Value).ToList();

                // A tie for first place means no clear majority owner - leave this cell out of
                // the table so callers fall through to the next priority tier.
                bool tied = ordered.Count > 1 && ordered[0].Value == ordered[1].Value;
                if (tied)
                    continue;

                if (state.LinkCache.TryResolve<IFactionGetter>(ordered[0].Key, out var winningFaction))
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
            var settings = LoadRunSettings(state);
            PopulateOverrides(settings);
            FuzzyFactionMatchCache.Clear();

            var factionsByEdid = new Dictionary<string, IFactionGetter>(StringComparer.OrdinalIgnoreCase);
            foreach (var fac in state.LoadOrder.PriorityOrder.Faction().WinningOverrides())
            {
                if (fac.EditorID != null)
                    factionsByEdid.TryAdd(fac.EditorID, fac);
            }

            // Priority 3's precomputed table: cell FormKey -> majority faction owner among that
            // cell's already-owned placed objects (or absent if no data / a tie — see
            // BuildCellOwnershipVotes).
            var cellOwnershipVotes = BuildCellOwnershipVotes(state);

            var seen = new HashSet<FormKey>();

            var patchedCropsByCell = new Dictionary<string, List<(string Crop, string Plugin, string? OwnerFaction, string OwnershipSource, string? VoteDetail)>>(StringComparer.OrdinalIgnoreCase);
            var skippedCropsByCell = new Dictionary<string, List<(string Crop, string Plugin, string Reason)>>(StringComparer.OrdinalIgnoreCase);
            var excludedCropsByPlugin = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var excludedCellsByRule = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var excludedLocTypesByRule = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var excludedNamesByRule = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
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

            // Memoizes the "is this Base record a matching crop?" check by FormKey. A huge number of
            // placed objects share the exact same Base record (e.g. every wheat stalk in the game
            // points at the same TreeFloraWheat01 record), so resolving and re-checking it fresh for
            // every single placed object is redundant work. This caches the result per unique Base
            // FormKey — identical logic/results to a fresh check every time, just not repeated.
            var baseRecordCropCache = new Dictionary<FormKey, string?>();

            // Memoizes the owning-faction resolution by (containing cell, plugin name).
            // The naming-convention/override/plugin-override chain depends only on the
            // cell/location and the placing plugin — never on which crop is being looked at —
            // but a single farm cell commonly holds dozens of crop placements (wheat, cabbage,
            // potato, garlic...). Without this cache, that whole chain
            // (including linear scans over every faction in the load order via TryFuzzyFactionMatch)
            // re-runs identically for every crop in the cell instead of once per cell.
            var cellFactionResolutionCache = new Dictionary<(FormKey? CellKey, string Plugin), (IFactionGetter? Faction, string? Source, string? VoteDetail)>();

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
                    var locPrefixes = new[] { "LocType", "LocSet" };

                    var keywordEdids = location?.Keywords?
                        .Select(k => k.TryResolve(state.LinkCache)?.EditorID)
                        .Where(e => e != null &&
                                    locPrefixes.Any(p => e.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
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

                if (IsPluginExcluded(pluginName, settings))
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

                // The raw location and cell are passed through the full chain below regardless
                // of anything else about the placement — resolution depends only on the
                // containing cell/location and the placing plugin, never on which crop we're
                // looking at — so it's cached per (cell, plugin) rather than re-derived for
                // every single crop placement in the same cell.
                var resolutionKey = (containingCell?.FormKey, pluginName);
                if (!cellFactionResolutionCache.TryGetValue(resolutionKey, out var resolved))
                {
                    // Precedence (highest to lowest). Each tier is tried in order and the first
                    // one to resolve a faction wins; its label is recorded alongside the faction
                    // so the report can show what actually decided ownership for each crop.
                    (Func<IFactionGetter?> Resolve, string Source)[] tiers =
                    [
                        (() => TryGetExactOverrideFaction(location, factionsByEdid, containingCell), "Overrides"),
                        (() => TryGetCellOwnerFaction(containingCell, state.LinkCache), "Cell owner"),
                        (() => TryGetCellVoteFaction(containingCell, cellOwnershipVotes), "Ownership vote"),
                        (() => TryGetNamingConventionFaction(location, factionsByEdid, containingCell), "Naming Convention"),
                        (() => TryGetPartialOverrideFaction(location, factionsByEdid, containingCell), "Manual rule (Cell ID)"),
                        (() => TryGetManualPluginRuleFaction(pluginName, settings, factionsByEdid), "Manual rule (plugin)"),
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

                    // Captured separately from resolvedSource so the tier name stays clean for
                    // grouping in the Ownership Source Summary — only the per-crop cell listing
                    // shows the specific counts that decided this particular vote.
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

                    AddSkip(skippedCropsByCell, cropEdid, pluginName, cellEdid, "No suitable owner");
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

                patchedList.Add((cropEdid, pluginName, townFaction.EditorID, ownershipSource ?? "Unknown", voteDetailLabel));
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

                // Ownership resolution is cached per (cell, plugin), so within one genuine cell
                // every patched crop normally shares the same "decided by" source. "Unknown cell"
                // is the one label where that assumption breaks — it lumps together many
                // different physical cells that just lack an EditorID, so their sources can
                // genuinely differ. Also fall back to per-crop display if a real cell somehow
                // still ends up with more than one distinct source, rather than silently hiding
                // a real discrepancy behind a single cell-level line.
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

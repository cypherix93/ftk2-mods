using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IOG.dObjects;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Visual remap for pack classes (day-one in-game finding, 2026-08-05).
    ///
    /// <para>A playable class needs a <c>dCharacter</c> record (3D prefab / body parts / portrait
    /// bindings) in <c>dObjectHelper.Index.dCharacter</c> — a ScriptableObject index built from
    /// Unity assets that a JSON merge can never populate. Without one,
    /// <c>CharacterVisualHelper.CreateAvatarComponent</c> dereferences
    /// <c>GetCharacterTierRecord(id).Prefab</c> on null the moment the class is selected; the
    /// selecting flow (<c>PartyManagementDirector._rebuildCharactertAsNewConfigType</c>) has
    /// already stripped the entity's AvatarComponent and holds the CUSTOMIZATION_DELAY input
    /// lock, so the throw leaves the UI permanently input-locked with a broken model.</para>
    ///
    /// <para>Fix mirrors EOR 0.7.0.60 (decompile: <c>CharacterVisualHelper_GetCharacterTierRecord_Prefix</c>
    /// + <c>CharacterHelper_GetConfigNameWithBodyType_Postfix</c>): resolve pack class ids to a
    /// vanilla DONOR class's record via a per-class template table, and rewrite
    /// <c>GetConfigNameWithBodyType</c> so every downstream skin/color/cosmetic lookup lands on
    /// the donor record's real key. The donor table below is EOR's own
    /// <c>GetTemplateCandidatesForCustomClass</c> verbatim, extended for the Baldur's pack.</para>
    /// </summary>
    public static class VisualRemapPatches
    {
        /// <summary>Pack class ids eligible for remap — populated from the merge plan on every
        /// (re)load, so hot-reload and disabled packs stay consistent.</summary>
        private static readonly HashSet<string> PackClassIds = new HashSet<string>(StringComparer.Ordinal);

        private static readonly string[] LastResort = { "ASTRONOMER", "SCHOLAR", "BLACKSMITH", "HOBO" };

        /// <summary>(baseId, bodyType) -> resolved donor record key; cleared on SetPackClassIds.</summary>
        private static readonly Dictionary<string, string> ResolvedKeyCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly HashSet<string> WarnedUnresolved = new HashSet<string>(StringComparer.Ordinal);

        internal static void SetPackClassIds(IEnumerable<string> ids)
        {
            PackClassIds.Clear();
            ResolvedKeyCache.Clear();
            if (ids == null) return;
            foreach (var id in ids)
                if (!string.IsNullOrEmpty(id)) PackClassIds.Add(id);
        }

        // ------------------------------------------------------------------ patches

        /// <summary>Prefix on <c>CharacterVisualHelper.GetCharacterTierRecord(string, Entity)</c>.
        /// (The Entity overload funnels into this one.) Callers pass both bare ids and
        /// "&lt;id&gt;_&lt;bodyType&gt;" keys (e.g. <c>CreateCharacterActor</c>), so both shapes remap.
        /// Fail-open: any miss or exception falls through to the original (which returns null,
        /// vanilla behavior).</summary>
        public static bool GetCharacterTierRecord_Prefix(string pConfigName, Entity pEntity, ref dCharacter __result)
        {
            try
            {
                if (string.IsNullOrEmpty(pConfigName)) return true;

                SplitBodySuffix(pConfigName, out var baseId, out var suffix);
                if (!PackClassIds.Contains(baseId)) return true;

                var body = suffix ?? BodyTypeFor(baseId, pEntity);
                if (TryResolveDonor(baseId, body, out var record, out _))
                {
                    __result = record;
                    return false;
                }
                if (WarnedUnresolved.Add(baseId))
                    ClassForgePlugin.Log.LogWarning(
                        $"[ClassForge] no donor dCharacter record resolvable for pack class '{baseId}' (body '{body}') — vanilla null behavior kept.");
                return true;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Postfix on <c>CharacterHelper.GetConfigNameWithBodyType(Entity)</c> — rewrites
        /// "CF_X_M" to the donor record's real key (e.g. "MONK_M") so raw
        /// <c>GetRecordByName(...).Prefab</c> call sites (color picker, cosmetics, skins) resolve.</summary>
        public static void GetConfigNameWithBodyType_Postfix(Entity pEntity, ref string __result)
        {
            try
            {
                if (pEntity == null || !pEntity.Has<CharacterComponent>()) return;
                var configName = pEntity.Get<CharacterComponent>().ConfigName;
                if (string.IsNullOrEmpty(configName) || !PackClassIds.Contains(configName)) return;

                SplitBodySuffix(__result, out _, out var suffix);
                var body = suffix ?? BodyTypeFor(configName, pEntity);
                if (TryResolveDonor(configName, body, out _, out var key))
                    __result = key;
            }
            catch
            {
                // fail-open: keep the original name
            }
        }

        /// <summary>Postfix on <c>PartyManagementDirector._rebuildCharactertAsNewConfigType</c>
        /// (async Task). Insurance (EOR shipped the same idea as a finalizer): if the rebuild
        /// faults for ANY reason, log it and release the CUSTOMIZATION_DELAY input lock the
        /// method took at its top — otherwise every rebuild error becomes a permanent UI freeze.
        /// A Harmony finalizer can't see async faults (it wraps only the kickoff), so this
        /// observes the returned Task instead.</summary>
        public static void RebuildAsNewConfigType_Postfix(Task __result)
        {
            if (__result == null) return;
            TaskScheduler scheduler;
            try { scheduler = TaskScheduler.FromCurrentSynchronizationContext(); }
            catch { scheduler = TaskScheduler.Default; }
            __result.ContinueWith(t =>
            {
                ClassForgePlugin.Log.LogError(
                    "[ClassForge] class rebuild faulted: " + t.Exception?.GetBaseException()
                    + " — releasing customization input lock (fail-safe).");
                try
                {
                    InputController.Instance.ReleaseDisable(
                        InputController.eDisableRequest.CUSTOMIZATION_DELAY, "ClassForge rebuild fail-safe");
                }
                catch (Exception ex)
                {
                    ClassForgePlugin.Log.LogWarning("[ClassForge] failed to release input lock: " + ex.Message);
                }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, scheduler);
        }

        // ------------------------------------------------------------------ resolution

        private static void SplitBodySuffix(string key, out string baseId, out string suffix)
        {
            baseId = key;
            suffix = null;
            if (key != null && key.Length > 2 && key[key.Length - 2] == '_')
            {
                var last = key[key.Length - 1];
                if (last == 'M' || last == 'F')
                {
                    baseId = key.Substring(0, key.Length - 2);
                    suffix = last.ToString();
                }
            }
        }

        private static string BodyTypeFor(string baseId, Entity pEntity)
        {
            if (pEntity != null && pEntity.Has<AvatarComponent>())
            {
                var bt = pEntity.Get<AvatarComponent>().BodyType;
                if (!string.IsNullOrEmpty(bt)) return bt;
            }
            if (Env.Configs.Characters.TryGetValue(baseId, out var cfg) && !string.IsNullOrEmpty(cfg.DefaultBodyType))
                return cfg.DefaultBodyType;
            return "M";
        }

        private static bool TryResolveDonor(string baseId, string body, out dCharacter record, out string recordKey)
        {
            record = null;
            recordKey = null;
            var cacheKey = baseId + "|" + body;
            if (ResolvedKeyCache.TryGetValue(cacheKey, out var cached))
            {
                recordKey = cached;
                return dObjectHelper.Index.dCharacter.TryGetRecordByName(cached, out record) && record != null;
            }

            foreach (var donor in DonorCandidates(baseId))
            {
                // suffixed record first (that's how vanilla playable records are keyed), bare second
                if (TryRecord(donor + "_" + body, ref record, ref recordKey)) break;
                if (TryRecord(donor, ref record, ref recordKey)) break;
            }
            if (record == null) return false;
            ResolvedKeyCache[cacheKey] = recordKey;
            return true;
        }

        private static bool TryRecord(string key, ref dCharacter record, ref string recordKey)
        {
            if (dObjectHelper.Index.dCharacter.TryGetRecordByName(key, out var r) && r != null)
            {
                record = r;
                recordKey = key;
                return true;
            }
            return false;
        }

        private static IEnumerable<string> DonorCandidates(string baseId)
        {
            foreach (var c in TemplateCandidates(baseId)) yield return c;
            foreach (var c in LastResort) yield return c;
        }

        /// <summary>EOR 0.7.0.60's <c>GetTemplateCandidatesForCustomClass</c> table verbatim
        /// (decompile L28455), keyed on the class name with our pack prefixes stripped; Baldur's
        /// pack cases appended.</summary>
        private static string[] TemplateCandidates(string baseId)
        {
            var key = baseId;
            foreach (var prefix in new[] { "CF_EOR_", "EOR_", "CF_" })
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    key = key.Substring(prefix.Length);
                    break;
                }
            }
            switch (key.ToUpperInvariant())
            {
                case "ARCANIST":
                case "ORACLE":
                case "WIZARD":
                    return new[] { "SCHOLAR", "ASTRONOMER" };
                case "ASSASSIN":
                case "TRICKSHOT":
                    return new[] { "HUNTER", "PATHFINDER", "TREASUREHUNTER" };
                case "BARD":
                    return new[] { "MINSTREL", "BUSKER" };
                case "BEASTMASTER":
                    return new[] { "PATHFINDER", "HUNTER", "SHEPHERD" };
                case "BLADEDANCER":
                case "DUELIST":
                case "CORSAIR":
                    return new[] { "MONK", "TREASUREHUNTER", "BUSKER" };
                case "RUNEMAGE":
                    return new[] { "SCHOLAR", "ASTRONOMER", "ALCHEMIST" };
                case "CHRONOMANCER":
                    return new[] { "ASTRONOMER", "SCHOLAR", "ALCHEMIST" };
                case "DRUID":
                    return new[] { "HERBALIST", "FRIAR", "SCHOLAR" };
                case "JESTER":
                case "FORTUNEBORN":
                case "GAMBLER":
                    return new[] { "BUSKER", "MINSTREL", "HOBO" };
                case "GLADIATOR":
                case "WARRIOR":
                    return new[] { "BLACKSMITH", "WOODCUTTER", "MONK" };
                case "HEXBLADE":
                case "WARLOCK_HEXBLADE":
                    return new[] { "BLACKSMITH", "MONK", "WOODCUTTER" };
                case "SENTINEL":
                case "WARDEN":
                case "KNIGHT":
                    return new[] { "BLACKSMITH", "STABLEBOY", "WOODCUTTER" };
                case "RANGER":
                case "MARSHAL":
                case "SCOUT":
                    return new[] { "PATHFINDER", "HUNTER", "ASTRONOMER" };
                case "PRIEST":
                case "PALADIN":
                case "TEMPLAR":
                    return new[] { "FRIAR", "HERBALIST", "SCHOLAR" };
                case "PEASANT":
                    return new[] { "FARMER", "HOBO", "WOODCUTTER" };
                case "RANCHER":
                case "STABLEHAND":
                    return new[] { "STABLEBOY", "SHEPHERD", "FARMER" };
                case "THIEF":
                    return new[] { "TREASUREHUNTER", "HUNTER", "BUSKER" };
                // Baldur's pack additions (no EOR precedent):
                case "BATTLEMASTER":
                    return new[] { "BLACKSMITH", "MONK", "WOODCUTTER" };
                case "NECROMANCER":
                    return new[] { "SCHOLAR", "ASTRONOMER", "ALCHEMIST" };
                case "SKELETON_WARRIOR":
                    return new[] { "HOBO", "WOODCUTTER" };
                default:
                    return new[] { "ASTRONOMER", "SCHOLAR", "BLACKSMITH" };
            }
        }
    }
}

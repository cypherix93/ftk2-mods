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

        /// <summary>baseId -> can VANILLA resolve a real dCharacter record for this id on its own?
        /// True for tier-suffixed creature configs authored on a real shipped family stem (see
        /// <see cref="VanillaCanResolve"/>); those must be left alone. Cleared on SetPackClassIds.</summary>
        private static readonly Dictionary<string, bool> VanillaResolvableCache =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        /// <summary>Ids we have already announced a resolution decision for (one line each, ever).</summary>
        private static readonly HashSet<string> AnnouncedResolution = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Config ids whose REAL actor build has already been observed to fail, so this class
        /// hands out a placeholder donor record for them unconditionally — pack id or not, tier-walkable
        /// or not. Populated ONLY by <see cref="ForcePlaceholderDonor"/>, which
        /// <c>SummonLeakPatches.ClearTileRenderState_Prefix</c> calls after
        /// <c>SummonVisuals.TryBuildActor</c> has already returned false for a live combatant.
        /// Cleared on <see cref="SetPackClassIds"/>.</summary>
        private static readonly HashSet<string> ForcedPlaceholders = new HashSet<string>(StringComparer.Ordinal);

        internal static void SetPackClassIds(IEnumerable<string> ids)
        {
            PackClassIds.Clear();
            ResolvedKeyCache.Clear();
            VanillaResolvableCache.Clear();
            AnnouncedResolution.Clear();
            WarnedUnresolved.Clear();
            ForcedPlaceholders.Clear();
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

                // A forced placeholder overrides both gates below, and ONLY that. It is set exactly
                // when a live combatant's real actor build has already failed, at which point the
                // choice is no longer "right art vs wrong art" but "wrong art vs an entity the
                // renderer will not draw and the roster may be tempted to delete". See
                // ForcePlaceholderDonor.
                var forced = ForcedPlaceholders.Contains(baseId);

                if (!forced && !PackClassIds.Contains(baseId)) return true;

                var body = suffix ?? BodyTypeFor(baseId, pEntity);

                // Vanilla first. A tier-suffixed pack CREATURE config (JELLY_ACID_01, DEMON_MELEE_03,
                // ...) authored on a real shipped family stem is already resolvable by the game's own
                // tier walk, and remapping it to a humanoid donor is strictly a downgrade. See
                // VanillaCanResolve.
                if (!forced && VanillaCanResolve(baseId)) return true;

                if (TryResolveDonor(baseId, body, out var record, out var donorKey))
                {
                    if (AnnouncedResolution.Add(baseId))
                        ClassForgePlugin.Log.LogWarning(
                            $"[ClassForge] visual remap: pack class '{baseId}' (body '{body}') has no dCharacter "
                            + $"record of its own — borrowing DONOR record '{donorKey}'. If '{baseId}' is a creature "
                            + "and this donor is a humanoid class, the art on screen will be WRONG.");
                    __result = record;
                    return false;
                }
                if (WarnedUnresolved.Add(baseId))
                    ClassForgePlugin.Log.LogError(
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
                if (string.IsNullOrEmpty(configName)) return;

                var forced = ForcedPlaceholders.Contains(configName);
                if (!forced && !PackClassIds.Contains(configName)) return;

                SplitBodySuffix(__result, out _, out var suffix);
                var body = suffix ?? BodyTypeFor(configName, pEntity);
                if (!forced && VanillaCanResolve(configName)) return; // creature configs keep their own name
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

        // ------------------------------------------------------------------ placeholder escalation

        /// <summary>
        /// Makes this class hand out a PLACEHOLDER donor record for <paramref name="entity"/>'s config
        /// from now on, bypassing both the "is it a pack id" gate and the <see cref="VanillaCanResolve"/>
        /// tier-walk guard. Returns true when a donor record actually resolved, i.e. when a retried
        /// <c>SummonVisuals.TryBuildActor</c> now has something to build from.
        ///
        /// <para><b>Only one caller, and only after a real failure.</b>
        /// <c>SummonLeakPatches.ClearTileRenderState_Prefix</c> calls this for a LIVE, component-complete
        /// combatant whose real actor build has already returned false. Nothing speculative reaches here,
        /// which is what keeps the 2026-08-25 regression fixed: tier-suffixed pack creatures
        /// (JELLY_ACID_01, ...) resolve through vanilla's own tier walk, build a real actor on the first
        /// attempt, and therefore never enter <see cref="ForcedPlaceholders"/> at all.</para>
        ///
        /// <para><b>Why a wrong model is the right answer.</b> The alternative the sweep used to take was
        /// deleting the combatant from <c>CombatState.Entities</c> / <c>RoundEntities</c> /
        /// <c>GameRun.Entities</c>. Actor builds fail on LOCAL conditions (canvas not up, prefab not
        /// streamed), so that made two peers disagree about the roster, which changes
        /// <c>GetTargetableTiles(...).Count</c> and therefore how many draws <c>ShuffleList</c> takes
        /// from the shared RNG on the next AI turn. Wrong art is a cosmetic bug on one screen;
        /// a missing combatant is a permanent desync.</para>
        ///
        /// <para><see cref="LastResort"/> ({ASTRONOMER, SCHOLAR, BLACKSMITH, HOBO}) is what makes
        /// <see cref="TryResolveDonor"/> total, so a false return here means the dCharacter index itself
        /// is unusable — which the caller escalates as an error rather than repairing.</para>
        /// </summary>
        internal static bool ForcePlaceholderDonor(Entity entity)
        {
            try
            {
                if (entity == null || !entity.Has<CharacterComponent>()) return false;
                var baseId = entity.Get<CharacterComponent>().ConfigName;
                if (string.IsNullOrEmpty(baseId)) return false;

                // Add() false means this config was already forced and the build STILL failed, so
                // forcing again cannot help; let the caller escalate instead of looping.
                if (!ForcedPlaceholders.Add(baseId)) return false;

                var body = BodyTypeFor(baseId, entity);
                if (TryResolveDonor(baseId, body, out _, out var donorKey))
                {
                    ClassForgePlugin.Log.LogWarning(
                        $"[ClassForge] no 3D model could be built for live combatant config '{baseId}' "
                        + $"(body '{body}'), so it is being drawn with PLACEHOLDER donor record '{donorKey}'. "
                        + "The art on screen will be WRONG for it. This is deliberate: the combatant stays "
                        + "in the roster, because removing it would desync the shared combat RNG.");
                    return true;
                }

                ClassForgePlugin.Log.LogError(
                    $"[ClassForge] no donor dCharacter record resolvable AT ALL for live combatant config "
                    + $"'{baseId}' (body '{body}') — not even the last-resort donors. The combatant stays in "
                    + "the fight and will render as nothing.");
                return false;
            }
            catch
            {
                return false;
            }
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

        /// <summary>Can vanilla resolve a real <c>dCharacter</c> record for this pack id unaided?
        ///
        /// <para>Mirrors <c>CharacterVisualHelper.GetCharacterTierRecord</c> (decompile L219-238)
        /// exactly. Two shapes exist:</para>
        /// <list type="bullet">
        /// <item>Id whose last two characters are NOT digits — every playable pack class
        /// (<c>CF_EOR_KNIGHT</c>, <c>CF_ORIG_TRAINER</c>, ...). <c>GetValueFromConfigName</c>
        /// returns -1 (<c>InteractableHelper.cs:1612</c>) and vanilla does a plain index lookup that
        /// finds nothing, which is the NRE this whole class exists to prevent. Remap applies.</item>
        /// <item>Id ending in a two-digit TIER — every pack CREATURE config
        /// (<c>JELLY_ACID_01</c>, <c>BOSS_DEMON_ELITE_08</c>, ...). Vanilla runs
        /// <c>TryGetNextConfigValueName(..., pTierValue: true)</c>
        /// (<c>CharacterVisualHelper.cs:298-331</c>), which halves the tier and walks the ladder
        /// (down, then up) for up to 10 steps looking for ANY tier of that family in the dCharacter
        /// index. An id authored on a REAL shipped family stem therefore already lands on that
        /// family's real prefab — <c>JELLY_ACID_01</c> -> <c>JELLY_ACID_05</c>. Remap must NOT
        /// apply.</item>
        /// </list>
        ///
        /// <para><b>The bug this guard fixes (owner-visible, 2026-08-25).</b> The prefix keyed only
        /// on "is this a pack id", and <see cref="SetPackClassIds"/> is fed EVERY id in the merge
        /// plan's Characters (<c>ConfigMergePatches.cs:77</c>) — creature configs included, not just
        /// playable classes. So the 12 Trainer partner creatures were intercepted, missed every case
        /// in <see cref="TemplateCandidates"/>, hit its <c>default:</c> humanoid arm
        /// (ASTRONOMER/SCHOLAR/BLACKSMITH), and returned <c>false</c> — suppressing the tier walk
        /// that would have resolved them correctly. Jellies rendered as human adventurers. Nothing
        /// warned, because a donor WAS found; it was simply the wrong species.</para></summary>
        private static bool VanillaCanResolve(string baseId)
        {
            if (VanillaResolvableCache.TryGetValue(baseId, out var cached)) return cached;

            var resolvable = false;
            try
            {
                if (InteractableHelper.GetValueFromConfigName(baseId) != -1)
                {
                    dCharacter walked = null;
                    var landedOn = CharacterVisualHelper.TryGetNextConfigValueName(
                        baseId,
                        s => dObjectHelper.Index.dCharacter.TryGetRecordByName(s, out walked) && walked != null,
                        pTierValue: true);
                    resolvable = !string.IsNullOrEmpty(landedOn) && walked != null;

                    if (AnnouncedResolution.Add(baseId))
                    {
                        if (resolvable)
                            ClassForgePlugin.Log.LogMessage(
                                $"[ClassForge] visual remap SKIPPED for pack creature '{baseId}' — vanilla's own tier "
                                + $"walk resolves it to shipped record '{landedOn}'. Using the real art.");
                        else
                            ClassForgePlugin.Log.LogError(
                                $"[ClassForge] pack creature '{baseId}' is tier-suffixed but its family stem has NO "
                                + "dCharacter record at any tier — the stem is INVENTED. It will fall back to a donor "
                                + "and render as the WRONG creature. Author it on a real shipped family stem.");
                    }
                }
            }
            catch
            {
                resolvable = false;
            }

            VanillaResolvableCache[baseId] = resolvable;
            return resolvable;
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
                // CF_PACK_ORIGINALS: Gary. Deliberately NOT the default {ASTRONOMER, SCHOLAR, BLACKSMITH}
                // that CF_ORIG_TRAINER falls through to -- Ash and his rival must be tellable apart on the
                // class-select screen and on the board, and "CF_ORIG_GARY" would otherwise land on the
                // same donor Ash does. TREASUREHUNTER reads as the swaggering rival; the other two are the
                // usual fallbacks if a body type is missing from the first record.
                case "ORIG_GARY":
                    return new[] { "TREASUREHUNTER", "BUSKER", "HUNTER" };
                default:
                    return new[] { "ASTRONOMER", "SCHOLAR", "BLACKSMITH" };
            }
        }
    }
}

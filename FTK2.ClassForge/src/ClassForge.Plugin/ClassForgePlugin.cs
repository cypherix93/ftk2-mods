using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ClassForge.Core;
using ClassForge.Core.IO;
using HarmonyLib;

namespace ClassForge.Plugin
{
    /// <summary>
    /// ClassForge M1: the BepInEx entry point. Owns knobs, Harmony patch installation, per-pack knob binding,
    /// and the reflection-based FTK2.DevKit ParityService registration (SPEC.md §3, §5, §6, §9.6).
    /// The actual pack-merge logic lives in ClassForge.Core (host-agnostic, testable); this class is a thin
    /// adapter that wires Core into the game's ConfigsHelper/Lang hooks and BepInEx config.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class ClassForgePlugin : BaseUnityPlugin
    {
        public const string Guid = "ftk2mods.classforge";
        public const string Name = "FTK2.ClassForge";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static ClassForgePlugin Instance;

        // ---- knobs (SPEC.md §5) ----
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> VerboseLogging;
        internal static ConfigEntry<string> AdditionalRoots;

        /// <summary>Gates the <c>CharacterCustomizationViewHelper.RenderClassList</c> pack-class injection
        /// (see <see cref="ClassSelectPatches"/>).</summary>
        internal static ConfigEntry<bool> EnableClassSelectInjection;

        /// <summary>Gates the <c>AssetLoader.GetImage</c>/<c>GetRender</c> icon+portrait fallback
        /// (see <see cref="AssetPatches"/>).</summary>
        internal static ConfigEntry<bool> EnableIconFallback;

        /// <summary>Gates the <c>LootDropHelper.GetAdventureLoadOut</c> trait-pool injection
        /// (see <see cref="TraitLoadoutPatches"/>).</summary>
        internal static ConfigEntry<bool> EnableTraitLoadoutInjection;

        /// <summary>Gates the whole skill-recipe engine (see <c>Recipes/</c>). All-or-nothing by design:
        /// SPEC-DELTA-v1.1 §5.3 forbids a partial recipe subset.</summary>
        internal static ConfigEntry<bool> EnableRecipeEngine;

        /// <summary>Gates the loot-grant sync verb's <c>LootDropHelper.GetLootDropsFromEnemies</c> postfix
        /// (see <see cref="LootGrantPatches"/>). Default FALSE: M-LG2 ships dark per charter rule 3 (the
        /// disabled-by-default carrier while unproven — docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md
        /// §10 M-LG2). Also requires <see cref="EnableRecipeEngine"/> = true.</summary>
        internal static ConfigEntry<bool> EnableLootGrants;

        /// <summary>Dev-only diagnostic: when true, the loot-grant postfix calls
        /// <c>GameRandom.LogCalls(true)</c> on the SHARED combat stream (<c>CombatState.Random</c>, passed
        /// into the postfix as <c>pGameRandom</c>) so a diagnostic session can visually confirm the
        /// zero-shared-draw invariant (verb spec §4.1) held. Per-draw log spam -- leave false outside a
        /// diagnostic session. Never affects the private grant stream, which is never logged this way.</summary>
        internal static ConfigEntry<bool> DebugLogCombatRandomDraws;

        /// <summary>
        /// The one gate every patch body consults. False when the master switch is off <b>or</b> when a
        /// multiplayer parity mismatch has latched ClassForge's <c>Block</c> policy
        /// (<see cref="ParityBridge.Blocked"/>, SPEC.md §9.5 / SPEC-DELTA-v1.1 §5.3 — whole engine off,
        /// there is no presentation-only subset).
        /// </summary>
        internal static bool FeaturesActive
        {
            get { return Enabled != null && Enabled.Value && !ParityBridge.Blocked; }
        }

        private static readonly Dictionary<string, ConfigEntry<bool>> PackEnabledKnobs = new Dictionary<string, ConfigEntry<bool>>(StringComparer.Ordinal);

        /// <summary>The most recently computed merge plan — read by LocalizationPatches (and, later, the icon/UI patches).</summary>
        internal static MergePlan CurrentMergePlan;
        internal static string CurrentDataHash = "(none)";

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "Master switch; if false, no pack is scanned and vanilla behavior is untouched.");
            VerboseLogging = Config.Bind("General", "VerboseLogging", false,
                "Pack discovery/merge decisions at Debug level.");
            AdditionalRoots = Config.Bind("Packs", "AdditionalRoots", "",
                "Comma-separated absolute paths to additional directories to scan for ClassPacks/<PackName>/ folders.");
            EnableClassSelectInjection = Config.Bind("UI", "EnableClassSelectInjection", true,
                "Inject PLAYER-tagged pack classes into the character-creation class list " +
                "(CharacterCustomizationViewHelper.RenderClassList). Turn off to keep pack classes usable via " +
                "console/dev tools and LoadOuts.json only.");
            EnableIconFallback = Config.Bind("UI", "EnableIconFallback", true,
                "Serve pack icons/portraits from the pack's icons//portraits/ folders via " +
                "AssetLoader.GetImage/GetRender. Purely presentational: with this off, pack content shows the " +
                "vanilla missing-asset result (a blank icon), nothing breaks.");
            EnableTraitLoadoutInjection = Config.Bind("Traits", "EnableTraitLoadoutInjection", true,
                "Append pack TRAIT_-prefixed traits to the adventure loadout pool " +
                "(LootDropHelper.GetAdventureLoadOut), so they can be picked on the party-setup screen. " +
                "Only ids that literally start with 'TRAIT_' are injected — that prefix IS the native trait " +
                "mechanism, not a naming convention.");
            EnableRecipeEngine = Config.Bind("Skills", "EnableRecipeEngine", true,
                "Master switch for the skill-recipe engine (skillrecipes.json). All-or-nothing by design: " +
                "SPEC-DELTA-v1.1 §5.3 forbids running a subset, because every recipe primitive either mutates " +
                "combat state or feeds something that does.");
            EnableLootGrants = Config.Bind("Skills", "EnableLootGrants", false,
                "Master switch for the loot-grant sync verb (ON_COMBAT_LOOT recipes: SCAVENGER/TREASURE_SENSE/" +
                "SCHOLARS_HABIT/OF_SCAVENGING loot halves). Ships DARK (default false) at M-LG2 -- the game-side " +
                "compute+apply path is wired but unproven in real combat; M-LG3 flips this default to true once " +
                "MP verification lands. Requires EnableRecipeEngine = true as well.");
            DebugLogCombatRandomDraws = Config.Bind("Skills", "DebugLogCombatRandomDraws", false,
                "DIAGNOSTIC ONLY -- do not enable outside a debugging session. When true, the loot-grant postfix " +
                "calls GameRandom.LogCalls(true) on the SHARED combat stream so its per-call log can be inspected " +
                "to confirm zero draws were taken on the loot-grant path. Produces per-draw log spam for the rest " +
                "of the session once enabled.");

            ApplyPatches();

            Log.LogInfo($"{Name} {Version} awakened. Enabled={Enabled.Value}. " +
                        "Pack discovery/merge runs from the ConfigsHelper.LoadConfigs/ReloadConfigs postfixes.");
        }

        private void ApplyPatches()
        {
            var harmony = new HarmonyLib.Harmony(Guid);

            // ---- content merge + localization (M1) ----
            Patch(harmony, typeof(ConfigsHelper), "LoadConfigs",
                postfix: M(typeof(ConfigMergePatches), nameof(ConfigMergePatches.LoadConfigs_Postfix)));
            Patch(harmony, typeof(ConfigsHelper), "ReloadConfigs",
                postfix: M(typeof(ConfigMergePatches), nameof(ConfigMergePatches.ReloadConfigs_Postfix)));
            Patch(harmony, typeof(Lang), "SetLanguage",
                postfix: M(typeof(LocalizationPatches), nameof(LocalizationPatches.SetLanguage_Postfix)));

            // ---- class-select UI ----
            // Resolved by NAME ONLY (the game has a single RenderClassList overload): the 7/31/2026
            // game update added a fifth bool to pOnChangeClass's Func, so a fully-typed lookup goes
            // stale on every callback-shape change. The prefix only reads pEntity/pPlayableCharacters
            // and so survives that drift.
            Patch(harmony, typeof(CharacterCustomizationViewHelper), "RenderClassList",
                prefix: M(typeof(ClassSelectPatches), nameof(ClassSelectPatches.RenderClassList_Prefix)));

            // ---- pack-class visual remap (see VisualRemapPatches header) ----
            // Pack classes have no dCharacter model record; without these three patches, SELECTING
            // one strips the avatar, NREs on the null record, and leaves the UI input-locked.
            Patch(harmony, typeof(CharacterVisualHelper), "GetCharacterTierRecord",
                prefix: M(typeof(VisualRemapPatches), nameof(VisualRemapPatches.GetCharacterTierRecord_Prefix)),
                argumentTypes: new[] { typeof(string), typeof(Entity) });
            Patch(harmony, typeof(CharacterHelper), "GetConfigNameWithBodyType",
                postfix: M(typeof(VisualRemapPatches), nameof(VisualRemapPatches.GetConfigNameWithBodyType_Postfix)));
            Patch(harmony, typeof(PartyManagementDirector), "_rebuildCharactertAsNewConfigType",
                postfix: M(typeof(VisualRemapPatches), nameof(VisualRemapPatches.RebuildAsNewConfigType_Postfix)));

            // ---- icon / portrait fallback ----
            // `out Color` MUST be declared as MakeByRefType() or AccessTools returns null and the patch
            // silently never applies.
            Patch(harmony, typeof(AssetLoader), "GetImage",
                prefix: M(typeof(AssetPatches), nameof(AssetPatches.GetImage_Prefix)),
                argumentTypes: new[] { typeof(object), typeof(eTextureAtlas), typeof(UnityEngine.Color).MakeByRefType() });
            Patch(harmony, typeof(AssetLoader), "GetRender",
                prefix: M(typeof(AssetPatches), nameof(AssetPatches.GetRender_Prefix)),
                argumentTypes: new[] { typeof(string), typeof(bool), typeof(bool) });

            // ---- trait selection ----
            Patch(harmony, typeof(LootDropHelper), "GetAdventureLoadOut",
                postfix: M(typeof(TraitLoadoutPatches), nameof(TraitLoadoutPatches.GetAdventureLoadOut_Postfix)),
                argumentTypes: new[] { typeof(string), typeof(GameRandom) });

            // ---- MP session lifecycle (M1) ----
            // Same anchor FTK2.DevKit's own ParityCoordinator uses to reset its session state
            // (AdventureDirector.Initialize) — see ParityBridge.AdventureDirectorInitialize_Postfix for why
            // the Block latch must not survive into a new session.
            Patch(harmony, typeof(AdventureDirector), "Initialize",
                postfix: M(typeof(ParityBridge), nameof(ParityBridge.AdventureDirectorInitialize_Postfix)));

            ApplyRecipeEnginePatches(harmony);
        }

        /// <summary>
        /// The SPEC-DELTA-v1.1 §2 trigger hooks. Each target is resolved by <c>AccessTools</c> and logged
        /// found/not-found; a missing target disables only the trigger(s) riding it, never the rest.
        /// </summary>
        private void ApplyRecipeEnginePatches(HarmonyLib.Harmony harmony)
        {
            var resultsList = typeof(List<(eAbilityResults, object)>);

            // T1 ON_COMBAT_START
            Patch(harmony, typeof(CombatHelper), "SetInitiative",
                postfix: M(typeof(CombatHookPatches), nameof(CombatHookPatches.SetInitiative_Postfix)));

            // T2 ON_ABILITY_DECLARED (+ E1 ROLL_STAT_BONUS delegate swap) and
            // ON_ABILITY_USED + T7 ON_ENEMY_ABILITY_RESOLVED
            Patch(harmony, typeof(CombatHelper), "PerformAbility",
                prefix: M(typeof(CombatHookPatches), nameof(CombatHookPatches.PerformAbility_Prefix)),
                postfix: M(typeof(CombatHookPatches), nameof(CombatHookPatches.PerformAbility_Postfix)));

            // C12 MOVED_THIS_ROUND observer (pAction == eCombatActions.MOVE)
            Patch(harmony, typeof(CombatHelper), "ApplyAction",
                postfix: M(typeof(CombatHookPatches), nameof(CombatHookPatches.ApplyAction_Postfix)));

            // ON_CRIT / ON_KILL / T3 ON_DAMAGE_DEALT / T4 ON_DAMAGE_TAKEN / ON_HEAL
            // MP review M4: the healer-slot restore rides a Harmony FINALIZER, not just the postfix --
            // Harmony never runs a postfix when the original throws, but a finalizer always runs (Prefix ->
            // Original -> Postfix -> Finalizer, regardless of what threw), so this is the only place the
            // restore can live and still be exception-safe. See CombatHookPatches.ApplyStatChange_Finalizer.
            Patch(harmony, typeof(InteractableHelper), "ApplyStatChange",
                prefix: M(typeof(CombatHookPatches), nameof(CombatHookPatches.ApplyStatChange_Prefix)),
                postfix: M(typeof(CombatHookPatches), nameof(CombatHookPatches.ApplyStatChange_Postfix)),
                finalizer: M(typeof(CombatHookPatches), nameof(CombatHookPatches.ApplyStatChange_Finalizer)));

            // T5 ON_STATUS_APPLIED — single-target overload only (the party-broadcast overload at L1128 has a
            // List<Entity> target and cannot bind this trigger's single-entity owner).
            Patch(harmony, typeof(InteractableHelper), "ApplyStatus",
                postfix: M(typeof(CombatHookPatches), nameof(CombatHookPatches.ApplyStatus_Postfix)),
                argumentTypes: new[]
                {
                    typeof(Entity), typeof(Entity), typeof(Thing), typeof(string), typeof(string),
                    typeof(GameRandom), resultsList, typeof(bool), typeof(bool), typeof(int?)
                });

            // T6 ON_CONSUMABLE_USED
            Patch(harmony, typeof(InteractableHelper), "PerformConsumableAbility",
                postfix: M(typeof(CombatHookPatches), nameof(CombatHookPatches.PerformConsumableAbility_Postfix)));

            // T8 ON_HEAL_PENDING (+ E2 HEAL_MODIFIER ref-int mutation). Four AddHealth overloads exist; this
            // is the terminal implementation (CharacterHelper.cs L1357) the other three funnel into, so
            // patching it alone catches every heal without double-firing.
            Patch(harmony, typeof(CharacterHelper), "AddHealth",
                prefix: M(typeof(CombatHookPatches), nameof(CombatHookPatches.AddHealth_Prefix)),
                argumentTypes: new[]
                {
                    typeof(Entity), typeof(int).MakeByRefType(), typeof(bool), resultsList,
                    typeof(StatChangedResultsData), typeof(bool)
                });

            // ON_TURN_START / ON_TURN_END — private async method, hence the explicit fail-safe.
            // (SPEC-DELTA-v1.1 §2.1 named CombatHelper._onCombatSkillProc, which carries no turn phase at all;
            //  see CombatHookPatches.PerformSkillAbilityProcs_Prefix for the full re-anchoring rationale.)
            if (!Patch(harmony, typeof(CombatPhase), "_performSkillAbilityProcs",
                    prefix: M(typeof(CombatHookPatches), nameof(CombatHookPatches.PerformSkillAbilityProcs_Prefix)),
                    argumentTypes: new[] { typeof(Entity), typeof(eSkillEventProcs) }))
            {
                CombatHookPatches.WarnTurnHookMissing();
            }

            // M-LG2 ON_COMBAT_LOOT — LootDropHelper.GetLootDropsFromEnemies postfix (the loot-grant sync
            // verb's single compute+apply point). Gated inside LootGrantPatches on [Skills] EnableLootGrants
            // (default false -- ships dark, docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md §10).
            Patch(harmony, typeof(LootDropHelper), "GetLootDropsFromEnemies",
                postfix: M(typeof(LootGrantPatches), nameof(LootGrantPatches.GetLootDropsFromEnemies_Postfix)));
        }

        private static HarmonyMethod M(Type owner, string method)
        {
            return new HarmonyMethod(owner, method);
        }

        /// <summary>
        /// Installs one patch, logging <c>Target found:</c>/<c>Target NOT found:</c> per
        /// docs/CONVENTIONS.md so breakage after a game update is diagnosable from the BepInEx console
        /// without a debugger. Returns false when the target could not be resolved (fail-safe: that feature
        /// is simply off, everything else still installs).
        /// </summary>
        private static bool Patch(HarmonyLib.Harmony harmony, Type type, string method,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null, Type[] argumentTypes = null,
            HarmonyMethod finalizer = null)
        {
            try
            {
                var target = argumentTypes == null
                    ? AccessTools.Method(type, method)
                    : AccessTools.Method(type, method, argumentTypes);

                if (target == null)
                {
                    Log.LogError($"Target NOT found: {type.Name}.{method} — this feature is disabled (fail-safe).");
                    return false;
                }

                harmony.Patch(target, prefix: prefix, postfix: postfix, finalizer: finalizer);
                Log.LogInfo($"Target found: {type.Name}.{method}");
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError($"Target NOT found: {type.Name}.{method} — patch installation threw, feature disabled (fail-safe): {ex}");
                return false;
            }
        }

        /// <summary>
        /// Cheap discovery-only pre-pass so a per-pack <c>[Packs] &lt;PackId&gt;.Enabled</c> BepInEx entry
        /// exists before the real merge runs (SPEC.md §5). Idempotent — safe to call from every
        /// LoadConfigs/ReloadConfigs postfix; newly-discovered pack ids get a knob, existing ones are left
        /// exactly as the player set them.
        /// </summary>
        internal static void EnsurePackKnobsBound()
        {
            try
            {
                var fs = new FileSystemFileSource();
                var findings = new List<Finding>();
                var discovered = PackDiscovery.Discover(fs, GetRoots(), findings);
                foreach (var pack in discovered)
                {
                    if (PackEnabledKnobs.ContainsKey(pack.Manifest.Id)) continue;
                    PackEnabledKnobs[pack.Manifest.Id] = Instance.Config.Bind(
                        "Packs", $"{pack.Manifest.Id}.Enabled", true,
                        $"Enable/disable pack '{pack.Manifest.Id}' without deleting it.");
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[ClassForge] Pre-scan for per-pack knobs failed (fail-safe — packs still respect their own manifest 'enabled' field): {ex}");
            }
        }

        internal static List<string> GetRoots()
        {
            var roots = new List<string>();
            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (pluginDir != null)
                roots.Add(Path.Combine(pluginDir, "ClassPacks"));

            var extra = AdditionalRoots.Value ?? string.Empty;
            foreach (var r in extra.Split(','))
            {
                var trimmed = r.Trim();
                if (trimmed.Length > 0) roots.Add(trimmed);
            }
            return roots;
        }

        internal static bool IsPackEnabled(string packId)
            => !PackEnabledKnobs.TryGetValue(packId, out var knob) || knob.Value;

        // ---- FTK2.DevKit ParityService registration (SPEC.md §3, §6, §9.5, §9.6) ----
        // Resolved by name with no compile-time dependency; see ParityBridge for the full contract,
        // including the Block policy that latches FeaturesActive to false on any mismatch.
        internal static void RegisterParity(ParityRegistration payload)
        {
            ParityBridge.Register(payload);
        }
    }
}

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

        /// <summary>Gates the pack-skill rows in the character-creation and party-summary skill lists
        /// (see <see cref="SkillDisplayPatches"/>). Purely presentational.</summary>
        internal static ConfigEntry<bool> EnableSkillDisplay;

        /// <summary>Gates the whole skill-recipe engine (see <c>Recipes/</c>). All-or-nothing by design:
        /// SPEC-DELTA-v1.1 §5.3 forbids a partial recipe subset.</summary>
        internal static ConfigEntry<bool> EnableRecipeEngine;

        /// <summary>Gates the CONDITIONAL_STAT_MODIFIER read path (<see cref="StatModifierPatches"/> on the
        /// terminal <c>CharacterHelper.GetStat</c> overload). Gameplay-relevant and parity-registered:
        /// stats feed combat, so all MP peers must agree on it.</summary>
        internal static ConfigEntry<bool> EnableStatModifiers;

        /// <summary>Gates the loot-grant sync verb's <c>LootDropHelper.GetLootDropsFromEnemies</c> postfix
        /// AND its M-LG3 MP send/receive/verify path (see <see cref="LootGrantPatches"/>). Default FALSE:
        /// ships dark per charter rule 3 (the disabled-by-default carrier while unproven —
        /// docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md §10). M-LG3 wired the transport, the
        /// full §7 failure-mode matrix and the loot-grant SafeMode latch, all OFFLINE-verified, but the
        /// default flip to true is explicitly deferred: it is gated on the in-game V-1 measurement
        /// (real 2-peer DrawMark/ListDigest agreement, §8.2) which is operator scope, not part of this
        /// milestone. Also requires <see cref="EnableRecipeEngine"/> = true.</summary>
        internal static ConfigEntry<bool> EnableLootGrants;

        /// <summary>Dev-only diagnostic: when true, the loot-grant postfix calls
        /// <c>GameRandom.LogCalls(true)</c> on the SHARED combat stream (<c>CombatState.Random</c>, passed
        /// into the postfix as <c>pGameRandom</c>) so a diagnostic session can visually confirm the
        /// zero-shared-draw invariant (verb spec §4.1) held. Per-draw log spam -- leave false outside a
        /// diagnostic session. Never affects the private grant stream, which is never logged this way.</summary>
        internal static ConfigEntry<bool> DebugLogCombatRandomDraws;

        /// <summary>
        /// DIAGNOSTIC ONLY — Encounter Modifiers spec §12.6's SP smoke knob. -1 (default) = off, the
        /// formula's own conditions decide exactly as shipped. 0..100 overrides EVERY
        /// <c>ProcChanceFormula</c> result (currently only the generated <c>SKILL_CF_ENCMOD_SELECT</c>
        /// recipe uses one) with this fixed value — e.g. 100 forces every eligible encounter to roll a
        /// modifier, for an operator to visually confirm the banner/status/MXHP delta without waiting on
        /// the real 10-30% base chance. Plain <c>ProcChance</c> recipes are entirely unaffected. There is
        /// deliberately NO separate <c>[Skills] EnableEncounterModifiers</c> knob: the encounter-modifier
        /// recipes are ordinary generated content riding the existing pack-level gate
        /// (<c>[Packs] CF_PACK_ENCOUNTER_MODIFIERS.Enabled</c>, auto-bound by
        /// <see cref="EnsurePackKnobsBound"/>) plus <see cref="EnableRecipeEngine"/> — the same two-knob
        /// gating every other pack's recipes already use, per charter rule 3's "prefer the existing
        /// mechanism" posture.
        /// </summary>
        internal static ConfigEntry<int> DebugEncounterModifierChance;

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
            EnableSkillDisplay = Config.Bind("UI", "EnableSkillDisplay", true,
                "Render pack-granted class skills (SKILL_CF_*) as extra rows in the character-creation " +
                "and in-game party-summary skill lists. The vanilla panels only show passives that exist " +
                "in the compiled eSkills enum, so without this, pack skills work in combat but are " +
                "invisible in the UI. Purely presentational: off = vanilla panels, skills still function.");
            EnableTraitLoadoutInjection = Config.Bind("Traits", "EnableTraitLoadoutInjection", true,
                "Append pack TRAIT_-prefixed traits to the adventure loadout pool " +
                "(LootDropHelper.GetAdventureLoadOut), so they can be picked on the party-setup screen. " +
                "Only ids that literally start with 'TRAIT_' are injected — that prefix IS the native trait " +
                "mechanism, not a naming convention.");
            EnableStatModifiers = Config.Bind("Skills", "EnableStatModifiers", true,
                "CONDITIONAL_STAT_MODIFIER read path (statmodifiers.json): re-tunes the 10 rebalanced " +
                "selectable traits to EOR 0.7.0.62's percentage-of-computed-stat behavior (e.g. Light-Footed " +
                "EVD +20% instead of flat +10). Gameplay-relevant: ALL multiplayer peers must use the same " +
                "value — it is part of the parity registration.");
            EnableRecipeEngine = Config.Bind("Skills", "EnableRecipeEngine", true,
                "Master switch for the skill-recipe engine (skillrecipes.json). All-or-nothing by design: " +
                "SPEC-DELTA-v1.1 §5.3 forbids running a subset, because every recipe primitive either mutates " +
                "combat state or feeds something that does.");
            EnableLootGrants = Config.Bind("Skills", "EnableLootGrants", false,
                "Master switch for the loot-grant sync verb (ON_COMBAT_LOOT recipes: SCAVENGER/TREASURE_SENSE/" +
                "SCHOLARS_HABIT/OF_SCAVENGING loot halves) AND its MP host-send/receive/verify path. Ships DARK " +
                "(default false). The compute+apply path, the MP transport wire-up and the full failure-mode " +
                "matrix are all offline-verified as of M-LG3, but this default deliberately stays false: the " +
                "flip to true is gated on an in-game V-1 measurement (real 2-peer DrawMark/ListDigest agreement) " +
                "that is OPERATOR scope, not part of any milestone's automated work. Requires EnableRecipeEngine " +
                "= true as well.");
            DebugLogCombatRandomDraws = Config.Bind("Skills", "DebugLogCombatRandomDraws", false,
                "DIAGNOSTIC ONLY -- do not enable outside a debugging session. When true, the loot-grant postfix " +
                "calls GameRandom.LogCalls(true) on the SHARED combat stream so its per-call log can be inspected " +
                "to confirm zero draws were taken on the loot-grant path. Produces per-draw log spam for the rest " +
                "of the session once enabled.");
            DebugEncounterModifierChance = Config.Bind("Skills", "DebugEncounterModifierChance", -1,
                "DIAGNOSTIC ONLY -- do not enable outside a debugging/SP-smoke session (Encounter Modifiers spec " +
                "§12.6). -1 = off (default): the ProcChanceFormula's own conditions decide, exactly as shipped. " +
                "0..100 forces every eligible encounter's modifier-selection roll to this fixed chance instead -- " +
                "e.g. 100 makes every eligible fight roll a modifier, for visually confirming the banner/status/ " +
                "MXHP delta without waiting on the real 10-30% base chance. Requires EnableRecipeEngine = true; " +
                "affects only ProcChanceFormula-bearing recipes (today: the generated encounter-modifier " +
                "selection recipe), never plain ProcChance recipes.");

            ApplyPatches();

            // M-LG3: registers the CF_SYNC_LOOT_GRANT_V1 receiver with FTK2.DevKit's TransportService.
            // No-op (gated internally) unless both EnableRecipeEngine and EnableLootGrants are true.
            LootGrantPatches.InitializeTransport();

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

            // ---- conditional stat modifiers (see StatModifierPatches header) ----
            // The TERMINAL GetStat overload (CH L422, the `out pBonuses` one) — all seven public
            // overloads funnel into it, so one postfix covers every read. Patching a second overload in
            // the funnel (e.g. L416, EOR's choice) would double-apply every bonus (spec §1.1): exactly
            // one overload is patched, ever.
            Patch(harmony, typeof(CharacterHelper), "GetStat",
                postfix: M(typeof(StatModifierPatches), nameof(StatModifierPatches.GetStat_Postfix)),
                argumentTypes: new[]
                {
                    typeof(Entity), typeof(string), typeof(eGetStatEquippedFilters),
                    typeof(List<(string, int)>).MakeByRefType(), typeof(bool), typeof(bool)
                });

            // ---- pack-skill display (see SkillDisplayPatches header) ----
            // Both vanilla skill lists drop any Passive that fails Enum.TryParse<eSkills>; these
            // postfixes render the pack-granted skills the recipe engine actually runs.
            Patch(harmony, typeof(CharacterCustomizationViewHelper), "RenderStatsContainer",
                postfix: M(typeof(SkillDisplayPatches), nameof(SkillDisplayPatches.RenderStatsContainer_Postfix)));
            Patch(harmony, typeof(CharacterSummaryViewHelper), "_showClassSkills",
                postfix: M(typeof(SkillDisplayPatches), nameof(SkillDisplayPatches.ShowClassSkills_Postfix)));

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

            // ---- equipment 3D-visual fallback + crash guard (task #8, see EquipmentVisualPatches header) ----
            // String overload is the terminal one (the VisualEquipment overload funnels into it).
            Patch(harmony, typeof(EquipmentVisualHelper), "GetITMEquipmentPrefab",
                prefix: M(typeof(EquipmentVisualPatches), nameof(EquipmentVisualPatches.GetITMEquipmentPrefab_Prefix)),
                argumentTypes: new[] { typeof(string), typeof(eActorBodies), typeof(eActorBodies) });
            // Finalizer registered unconditionally — a crash guard must not be toggleable off into a freeze.
            Patch(harmony, typeof(CharacterVisualHelper), "VisualReEquip",
                finalizer: M(typeof(EquipmentVisualPatches), nameof(EquipmentVisualPatches.VisualReEquip_Finalizer)));

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

            // ---- MP trait-loadout refresh (task #11 ClassForge half — see TraitLoadoutRefresh header) ----
            // The pool is built once at PartyManagementDirector.Initialize and cached; when DevKit's
            // party-phase handshake (979bd17) delivers a Match verdict a moment later, TraitLoadoutRefresh
            // re-runs the game's own rebuild path so the traits appear without leaving the screen.
            // Resolved by NAME ONLY: single overload, and its parameter list drifted once already (the
            // pSyncData default), so a fully-typed lookup would go stale on the next callback-shape change.
            Patch(harmony, typeof(PartyManagementDirector), "Initialize",
                postfix: M(typeof(TraitLoadoutRefresh), nameof(TraitLoadoutRefresh.PartyInitialize_Postfix)));
            Patch(harmony, typeof(AdventureDirector), "Initialize",
                postfix: M(typeof(TraitLoadoutRefresh), nameof(TraitLoadoutRefresh.AdventureStarted_Postfix)));

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

            // v1.3 ON_DAMAGE_PENDING — InteractableHelper.CalculateFinalDamage postfix (PSN §2 L1708;
            // state-hash-chance spec M-SH3, the retired SPEC-DELTA §7.4 park). RNG-free by validator
            // construction; SHIELDBEARER's STATE_HASH_CHANCE gate takes zero draws.
            Patch(harmony, typeof(InteractableHelper), "CalculateFinalDamage",
                postfix: M(typeof(CombatHookPatches), nameof(CombatHookPatches.CalculateFinalDamage_Postfix)),
                argumentTypes: new[]
                {
                    typeof(Entity), typeof(int), typeof(decimal), typeof(eDamageType), typeof(bool), typeof(bool)
                });

            // M-LG2 ON_COMBAT_LOOT — LootDropHelper.GetLootDropsFromEnemies postfix (the loot-grant sync
            // verb's single compute+apply point). Gated inside LootGrantPatches on [Skills] EnableLootGrants
            // (default false -- ships dark, docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md §10).
            Patch(harmony, typeof(LootDropHelper), "GetLootDropsFromEnemies",
                postfix: M(typeof(LootGrantPatches), nameof(LootGrantPatches.GetLootDropsFromEnemies_Postfix)));

            // Summon leak — CombatPhase._endCombatAsync only banishes summons when pIsImmediate is
            // false, so every immediate exit leaves one standing on a tile. It then survives into the
            // next fight, where _clearTileRenderState indexes _gameObjectMaps.FromCharacter for it with
            // no membership check and throws, taking the whole combat UI down with it. Both ends are
            // patched: Deinitialize so nothing leaks, Initialize so a save that already leaked one is
            // still playable. Prefixes in both cases -- Deinitialize's own body calls
            // _clearTileRenderState, so a postfix would run after the throw. See SummonLeakPatches.
            Patch(harmony, typeof(CombatPhase), "Deinitialize",
                prefix: M(typeof(SummonLeakPatches), nameof(SummonLeakPatches.Deinitialize_Prefix)));
            Patch(harmony, typeof(CombatPhase), "Initialize",
                prefix: M(typeof(SummonLeakPatches), nameof(SummonLeakPatches.Initialize_Prefix)));

            // The line that actually throws. _clearTileRenderState indexes
            // _gameObjectMaps.FromCharacter for each tile's living occupant with no membership check,
            // so any combatant lacking a 3D model takes the method down -- and the rest of Initialize
            // with it. This prefix sweeps those out of the roster first, whatever their provenance,
            // which is what makes the fix independent of how the creature got there.
            Patch(harmony, typeof(CombatPhase), "_clearTileRenderState",
                prefix: M(typeof(SummonLeakPatches), nameof(SummonLeakPatches.ClearTileRenderState_Prefix)));

            // M-LG3 — the loot-grant SafeMode latch's own session-start reset, on the SAME anchor
            // ParityBridge uses for its (unrelated, whole-engine) latch. A separate patch registration so
            // this file never has to touch ParityBridge.cs for a verb-local concern.
            Patch(harmony, typeof(AdventureDirector), "Initialize",
                postfix: M(typeof(LootGrantPatches), nameof(LootGrantPatches.AdventureDirectorInitialize_Postfix)));
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

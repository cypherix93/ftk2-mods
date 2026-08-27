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
using IOG.dObjects;

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

        /// <summary>docs/MULTIPLAYER.md R1's <c>OnParityMismatch</c> knob. See
        /// <see cref="ParityBridge.ResolvePolicy"/> for how <c>"Default"</c> maps kind -> policy.</summary>
        internal static ConfigEntry<string> OnParityMismatch;

        /// <summary>P0.5 §5(b). When true (default) an online session with no FTK2.DevKit present puts
        /// ClassForge into SafeMode rather than running fully enabled with parity unenforced.</summary>
        internal static ConfigEntry<bool> RequireParityService;

        /// <summary>
        /// The one gate every patch body consults. False when the master switch is off <b>or</b> when a
        /// multiplayer parity mismatch has latched ClassForge's <c>Block</c> policy
        /// (<see cref="ParityBridge.Blocked"/>, SPEC.md §9.5 / SPEC-DELTA-v1.1 §5.3 — whole engine off,
        /// there is no presentation-only subset).
        /// </summary>
        internal static bool FeaturesActive
        {
            get { return Enabled != null && Enabled.Value && !ParityBridge.Blocked && !ParityBridge.SafeMode; }
        }

        /// <summary>
        /// The gate for features that only ever change what the LOCAL player sees (icon/portrait fallback,
        /// the character-creation class list) — i.e. everything classified
        /// <see cref="ParityClass.Presentation"/>. Differs from <see cref="FeaturesActive"/> in exactly one
        /// state: <b>SafeMode</b>, which docs/MULTIPLAYER.md R1 defines as "disables its state-mutating
        /// features for the session but keeps presentation-only features". <c>Block</c> still turns
        /// everything off, and SPEC-DELTA-v1.1 §5.3's "no partial recipe engine" rule is untouched — the
        /// recipe engine, trait injection, stat modifiers and every Trainer feature read
        /// <see cref="FeaturesActive"/>, which is false in SafeMode too.
        /// </summary>
        internal static bool PresentationActive
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

            Enabled = CFConfig.Bind(Config, "General", "Enabled", true,
                "Master switch; if false, no pack is scanned and vanilla behavior is untouched.", ClassForge.Core.ParityClass.Gameplay);
            VenueGridPatches.Bind(Config);
            TrainerPartnerPersistence.Bind(Config);
            TrainerPartnerNicknames.Bind(Config);
            TrainerPartnerPanel.Bind(Config);
            TrainerPartnerAutonomy.Bind(Config);
            TrainerCharmProgression.Bind(Config);
            TrainerFocusFire.Bind(Config);
            AiDrawNeutrality.Bind(Config);
            TrainerCaptureRules.Bind(Config);

            VerboseLogging = CFConfig.Bind(Config, "General", "VerboseLogging", false,
                "Pack discovery/merge decisions at Debug level.", ClassForge.Core.ParityClass.Presentation);
            AdditionalRoots = CFConfig.BindWithParityValue(Config, "Packs", "AdditionalRoots", "",
                "Comma-separated absolute paths to additional directories to scan for ClassPacks/<PackName>/ folders. "
                + "Multiplayer parity compares the SET OF PACKS these roots contribute, not the paths themselves, "
                + "so two players with identical packs on different drives are not refused at join.",
                ClassForge.Core.ParityClass.Gameplay,
                AdditionalRootsParityValue);
            EnableClassSelectInjection = CFConfig.Bind(Config, "UI", "EnableClassSelectInjection", true,
                "Inject PLAYER-tagged pack classes into the character-creation class list " +
                "(CharacterCustomizationViewHelper.RenderClassList). Turn off to keep pack classes usable via " +
                "console/dev tools and LoadOuts.json only.", ClassForge.Core.ParityClass.Presentation);
            EnableIconFallback = CFConfig.Bind(Config, "UI", "EnableIconFallback", true,
                "Serve pack icons/portraits from the pack's icons//portraits/ folders via " +
                "AssetLoader.GetImage/GetRender. Purely presentational: with this off, pack content shows the " +
                "vanilla missing-asset result (a blank icon), nothing breaks.", ClassForge.Core.ParityClass.Presentation);
            EnableSkillDisplay = CFConfig.Bind(Config, "UI", "EnableSkillDisplay", true,
                "Render pack-granted class skills (SKILL_CF_*) as extra rows in the character-creation " +
                "and in-game party-summary skill lists. The vanilla panels only show passives that exist " +
                "in the compiled eSkills enum, so without this, pack skills work in combat but are " +
                "invisible in the UI. Purely presentational: off = vanilla panels, skills still function.", ClassForge.Core.ParityClass.Presentation);
            EnableTraitLoadoutInjection = CFConfig.Bind(Config, "Traits", "EnableTraitLoadoutInjection", true,
                "Append pack TRAIT_-prefixed traits to the adventure loadout pool " +
                "(LootDropHelper.GetAdventureLoadOut), so they can be picked on the party-setup screen. " +
                "Only ids that literally start with 'TRAIT_' are injected — that prefix IS the native trait " +
                "mechanism, not a naming convention.", ClassForge.Core.ParityClass.Gameplay);
            EnableStatModifiers = CFConfig.Bind(Config, "Skills", "EnableStatModifiers", true,
                "CONDITIONAL_STAT_MODIFIER read path (statmodifiers.json): re-tunes the 10 rebalanced " +
                "selectable traits to EOR 0.7.0.62's percentage-of-computed-stat behavior (e.g. Light-Footed " +
                "EVD +20% instead of flat +10). Gameplay-relevant: ALL multiplayer peers must use the same " +
                "value — it is part of the parity registration.", ClassForge.Core.ParityClass.Gameplay);
            EnableRecipeEngine = CFConfig.Bind(Config, "Skills", "EnableRecipeEngine", true,
                "Master switch for the skill-recipe engine (skillrecipes.json). All-or-nothing by design: " +
                "SPEC-DELTA-v1.1 §5.3 forbids running a subset, because every recipe primitive either mutates " +
                "combat state or feeds something that does.", ClassForge.Core.ParityClass.Gameplay);
            EnableLootGrants = CFConfig.Bind(Config, "Skills", "EnableLootGrants", false,
                "Master switch for the loot-grant sync verb (ON_COMBAT_LOOT recipes: SCAVENGER/TREASURE_SENSE/" +
                "SCHOLARS_HABIT/OF_SCAVENGING loot halves) AND its MP host-send/receive/verify path. Ships DARK " +
                "(default false). The compute+apply path, the MP transport wire-up and the full failure-mode " +
                "matrix are all offline-verified as of M-LG3, but this default deliberately stays false: the " +
                "flip to true is gated on an in-game V-1 measurement (real 2-peer DrawMark/ListDigest agreement) " +
                "that is OPERATOR scope, not part of any milestone's automated work. Requires EnableRecipeEngine " +
                "= true as well.", ClassForge.Core.ParityClass.Gameplay);
            DebugLogCombatRandomDraws = CFConfig.Bind(Config, "Skills", "DebugLogCombatRandomDraws", false,
                "DIAGNOSTIC ONLY -- do not enable outside a debugging session. When true, the loot-grant postfix " +
                "calls GameRandom.LogCalls(true) on the SHARED combat stream so its per-call log can be inspected " +
                "to confirm zero draws were taken on the loot-grant path. Produces per-draw log spam for the rest " +
                "of the session once enabled. CLASSIFIED PRESENTATION, AND THAT IS DECOMPILE-VERIFIED, NOT " +
                "ASSUMED (W1-I, re-checked 2026-08-26): GameRandom.LogCalls(bool) is `_logCalls = pDoLog; " +
                "_log = new StringBuilder();` and NOTHING else (GameRandom.cs:25-29) -- it never touches the " +
                "private `random` field, so it cannot advance the shared stream and cannot be a draw-count " +
                "fork even though only one peer enables it. (What it DOES change is GameRandom.NextCount, " +
                "which only increments while _logCalls is on -- which is exactly why NextCount is documented " +
                "everywhere here as an unreliable draw counter.) Do not re-open this as a parity concern.",
                ClassForge.Core.ParityClass.Presentation);
            DebugEncounterModifierChance = CFConfig.Bind(Config, "Skills", "DebugEncounterModifierChance", -1,
                "DIAGNOSTIC ONLY -- do not enable outside a debugging/SP-smoke session (Encounter Modifiers spec " +
                "§12.6). -1 = off (default): the ProcChanceFormula's own conditions decide, exactly as shipped. " +
                "0..100 forces every eligible encounter's modifier-selection roll to this fixed chance instead -- " +
                "e.g. 100 makes every eligible fight roll a modifier, for visually confirming the banner/status/ " +
                "MXHP delta without waiting on the real 10-30% base chance. Requires EnableRecipeEngine = true; " +
                "affects only ProcChanceFormula-bearing recipes (today: the generated encounter-modifier " +
                "selection recipe), never plain ProcChance recipes.", ClassForge.Core.ParityClass.Gameplay);

            OnParityMismatch = CFConfig.Bind(Config, "Multiplayer", "OnParityMismatch", "Default",
                "What ClassForge does when FTK2.DevKit's parity handshake reports a divergence " +
                "(docs/MULTIPLAYER.md R1). 'Default' (recommended) is policy-per-kind: BLOCK on a " +
                "FeaturesMismatch -- a gameplay KNOB differs, e.g. [Combat] VenueGridPreset, which changes " +
                "the arena tile count and therefore AIHelper's ShuffleList draw count, a guaranteed desync " +
                "on the first AI turn -- and WarnAndSafeMode on a VersionMismatch/DataMismatch, where a " +
                "pack-content difference may still be benign. 'Block', 'WarnAndSafeMode' and 'WarnOnly' " +
                "force one policy for every kind. Gameplay-classified: if two peers disagree here, one " +
                "shuts its features off and the other does not, which is itself the asymmetric execution " +
                "parity exists to prevent.", ParityClass.Gameplay);

            RequireParityService = CFConfig.Bind(Config, "Multiplayer", "RequireParityService", true,
                "Fail CLOSED when FTK2.DevKit is not installed. With DevKit absent there is no handshake at " +
                "all, so every parity decision above is unenforceable and a mismatched peer is simply never " +
                "detected. When this is true (default) and the session is online multiplayer, ClassForge " +
                "enters SafeMode: every state-mutating feature is off and only presentation features run. " +
                "Set false ONLY for a single-player-only install or a deliberately unenforced session.",
                ParityClass.Gameplay);

            // P0.5: post-bind sweep. Adopts the [Trainer] knobs bound by the TrainerPartner* files (which
            // do not route through CFConfig) and fails CLOSED on any knob nobody classified. Must run after
            // every *.Bind(Config) above.
            CFConfig.Reconcile(Config);

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

            // ---- missing-dAbility crash guards (see CombatVisualNullGuards header) ----
            // dObjectIndexers.GetRecordByName returns null for an unknown ability id and the combat view
            // dereferences it unguarded, so any id without an art record — CF_RECIPE_EFFECT included —
            // takes the fight down mid-click. Registered unconditionally: a crash guard must not be
            // toggleable off. Each degrades to missing FX and names the offending id once at Warning.
            Patch(harmony, typeof(CharacterVisualHelper), "GetCharacterAbilityRecord",
                postfix: M(typeof(CombatVisualNullGuards),
                    nameof(CombatVisualNullGuards.GetCharacterAbilityRecord_Postfix)));
            Patch(harmony, typeof(CombatViewHelper), "JoinAbilityHitEffectNode",
                prefix: M(typeof(CombatVisualNullGuards),
                    nameof(CombatVisualNullGuards.JoinAbilityHitEffectNode_Prefix)));
            Patch(harmony, typeof(CombatViewHelper), "EnqueueReactionAnimations",
                finalizer: M(typeof(CombatVisualNullGuards),
                    nameof(CombatVisualNullGuards.EnqueueReactionAnimations_Finalizer)));

            // ---- missing-dStatusEffect HARD FREEZE guard (see CombatVisualNullGuards.AddStatusIcon_Prefix) ----
            // Same hazard shape as the dAbility guards above, one store over: the timeline portrait strip
            // dereferences dStatusEffect.IconTexture unguarded for EVERY status a combatant carries, and the
            // NRE escapes the scheduled _progressRound callback — the round never finishes progressing and
            // the callback is retried every frame. Registered unconditionally, like the other crash guards.
            // The target is a compiler-generated local function; its name and closure class carry ordinals
            // that drift on any edit to the game file, so it is RESOLVED BY SCAN, never hard-coded.
            var addStatusIcon = CombatVisualNullGuards.ResolveAddStatusIcon();
            if (addStatusIcon != null)
                Patch(harmony, addStatusIcon, "CombatTimelineViewHelper2._refreshPortraitVisuals/_addStatusIcon",
                    prefix: M(typeof(CombatVisualNullGuards),
                        nameof(CombatVisualNullGuards.AddStatusIcon_Prefix)));

            // ---- status icon donor fallback (see StatusVisualPatches header) ----
            // A pack status (statuses.json) has no dStatusEffect ScriptableObject/icon asset; without
            // these, STATUS_VIGOR_CF_ENGORGED is fully live in state but renders no HUD icon and no world FX.
            Patch(harmony, typeof(dStatusEffectIndex), "GetRecordByName",
                prefix: M(typeof(StatusVisualPatches), nameof(StatusVisualPatches.GetRecordByName_Prefix)),
                argumentTypes: new[] { typeof(string) });
            Patch(harmony, typeof(dStatusEffectIndex), "TryGetRecordByName",
                prefix: M(typeof(StatusVisualPatches), nameof(StatusVisualPatches.TryGetRecordByName_Prefix)),
                argumentTypes: new[] { typeof(string), typeof(dStatusEffect).MakeByRefType() });

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

            // Larger combat arena (opt-in). PREFIX on the diorama argument: Initialize reads
            // _diorama.VenueGrid into _combatState.GridType and then builds tiles from it, all
            // within the same call, so a postfix lands after the tiles already exist.
            // Substitute the tile MAP the game is about to build, so the engine constructs the
            // larger grid itself. Rebuilding the grid after Initialize left stale tile references
            // that broke drawing and enemy targeting -- see VenueGridPatches.
            Patch(harmony, typeof(VenueHelper), "CreateVenueTileEntities",
                prefix: M(typeof(VenueGridPatches), nameof(VenueGridPatches.CreateVenueTileEntities_Prefix)));

            // A resting tile draws only its BORDER: TileRender.Default disables the fill renderer
            // and enables the shadow one. Emissive lives on the fill, so brightening that lights up
            // the highlights and leaves the grid itself faint -- the border opacity is the knob.
            // Choose the battlefield. VenueDirector.Initialize is the one place that turns
            // DioramaName into an arena -- assets, tile grid and camera rig all come out of that
            // single call -- so a prefix here is the last moment the choice still reaches all three.
            // RouterMono.Route was tried first and never fired: an ordinary fight routes to
            // eRoutes.VENUE, not COMBAT.
            Patch(harmony, typeof(VenueDirector), "Initialize",
                prefix: M(typeof(VenueGridPatches), nameof(VenueGridPatches.VenueDirector_Initialize_Prefix)));

            // The dungeon counterpart, OFF by default. DungeonState.OngoingVenues is the whole
            // remaining floor AND is written to the save, and the rooms are corridor-chained
            // rather than standalone, so a swap there can break a floor. See the method.
            Patch(harmony, typeof(DungeonDirector), "_loadNextDioramas",
                prefix: M(typeof(VenueGridPatches), nameof(VenueGridPatches.DungeonDirector_LoadNextDioramas_Prefix)));

            // Cull scenery standing ON the board; outdoor grass occludes the tiles badly.
            Patch(harmony, typeof(VenueViewHelper), "CreateVenueTileGameObjects",
                postfix: M(typeof(VenueGridPatches), nameof(VenueGridPatches.CreateVenueTileGameObjects_Postfix)));

            Patch(harmony, typeof(VenueTileMono), "SetState",
                postfix: M(typeof(VenueGridPatches), nameof(VenueGridPatches.SetState_Postfix)));

            // The line that actually throws. _clearTileRenderState indexes
            // _gameObjectMaps.FromCharacter for each tile's living occupant with no membership check,
            // so any combatant lacking a 3D model takes the method down -- and the rest of Initialize
            // with it. This prefix sweeps those out of the roster first, whatever their provenance,
            // which is what makes the fix independent of how the creature got there.
            // ...and a finalizer behind it, for the combatant the prefix's sweep cannot save (P0 "never
            // delete a combatant"). _clearTileRenderState only RESETS per-tile render state and returns
            // void, so suppressing a throw here costs stale highlight state on one screen while
            // Initialize still completes -- grid, action menu and targeting all come up. Deliberately NOT
            // applied to GetTargetHighlights, which returns its collection: suppressing there would hand
            // callers a null. Same idiom as EnqueueReactionAnimations_Finalizer (shared LoggedSuppressed
            // dedup set, returns null).
            Patch(harmony, typeof(CombatPhase), "_clearTileRenderState",
                prefix: M(typeof(SummonLeakPatches), nameof(SummonLeakPatches.ClearTileRenderState_Prefix)),
                finalizer: M(typeof(CombatVisualNullGuards),
                    nameof(CombatVisualNullGuards.ClearTileRenderState_Finalizer)));

            // The same defect a third time. CombatViewHelper.GetTargetHighlights (CombatViewHelper.cs:3202)
            // walks EVERY tile with GroupIndex > -1 on the whole board and indexes
            // _gameObjectMaps.FromCharacter for whoever occupies each one (CombatViewHelper.cs:3252) with
            // no membership check -- so ANY combatant anywhere missing a 3D model takes down EVERY
            // targeted ability, not just ones aimed at it. Measured live: _performAiDecision faulted on
            // ability FLEE with a KeyNotFoundException for a Hobgoblin that was not even the flee target,
            // in a fight that had gone through a wave advance. See SummonLeakPatches.GetTargetHighlights_Prefix.
            if (!Patch(harmony, typeof(CombatViewHelper), "GetTargetHighlights",
                    prefix: M(typeof(SummonLeakPatches), nameof(SummonLeakPatches.GetTargetHighlights_Prefix))))
            {
                Log.LogWarning(
                    "[ClassForge] GetTargetHighlights patch target not found; the modelless-combatant " +
                    "targeting crash is unguarded this session.");
            }

            // ---- Pokemon Trainer partner persistence (test-checklist L0, see TrainerPartnerPatches) ----
            // A partner's current HP carries between fights on its ball item's Thing.CustomData, and a
            // partner at 0 HP is DOWNED rather than deleted. These four hooks keep that record in step;
            // each body fast-outs on "nothing tracked", which is true for every character that does not
            // hold an ARM_ORIG_TRAINER_BALL_* item -- i.e. every character of every other class.
            //
            // AddHealth's terminal overload is the same target CombatHookPatches prefixes for
            // ON_HEAL_PENDING; Harmony composes the two and the trigger prefix is unaffected. The two
            // direct CurrentHealth writes that bypass it entirely (KillCharacter's `= 0` at
            // CharacterHelper.cs:2178, SetToMaxHealth at :1261) need their own hooks -- without the
            // first, the very event the DOWNED state exists for would be the one event never recorded.
            Patch(harmony, typeof(CharacterHelper), "AddHealth",
                postfix: M(typeof(TrainerPartnerPatches), nameof(TrainerPartnerPatches.AddHealth_Postfix)),
                argumentTypes: new[]
                {
                    typeof(Entity), typeof(int).MakeByRefType(), typeof(bool), resultsList,
                    typeof(StatChangedResultsData), typeof(bool)
                });
            Patch(harmony, typeof(CharacterHelper), "KillCharacter",
                postfix: M(typeof(TrainerPartnerPatches), nameof(TrainerPartnerPatches.KillCharacter_Postfix)));
            Patch(harmony, typeof(CharacterHelper), "SetToMaxHealth",
                postfix: M(typeof(TrainerPartnerPatches), nameof(TrainerPartnerPatches.SetToMaxHealth_Postfix)));

            // The ONLY restore path: a town's service revives downed partners and heals hurt ones.
            // Town-ness is asserted on the encounter (EncounterComponent.Type == eEncounterTypes.TOWN),
            // not on the screen, so this cannot fire anywhere else.
            //
            // The target is AdventureDirector._onUseService -- the networked service ACTION -- and NOT
            // ServiceMenuViewHelper.Show, which was the original hook and was wrong: Show is pure UI, and
            // AdventureDirector.cs:10294 SKIPS it for remote players, so only the owning peer revived. That
            // left the peers with different partner rosters, hence a different CombatState.Entities.Count,
            // hence a different shared-GameRandom draw count -- a desync produced by the fix meant to
            // prevent one. _onUseService is a compiler-generated local function
            // (<_performEncounterAction>g___onUseService|22 on AdventureDirector+<>c__DisplayClass219_5), so
            // TryPatchUseService resolves it by scanning nested types for the name fragment instead of
            // hard-coding compiler ordinals that a game rebuild would renumber. Ambiguous match => it
            // refuses and logs; not found => logs and degrades (partners never revive). Never desyncs.
            TrainerPartnerPatches.TryPatchUseService(harmony);

            // The MANUAL send-out route. The charm's own CF_TRAINER_SUMMON_<LINE>_T<N>_ATTACK ability is a
            // plain vendor ADD_CHARACTER run by CombatPhase._performAbility -- it never reaches the recipe
            // engine, so ExecSummon's DOWNED gate could not see it, and using a charm whose partner was
            // DOWNED threw NullReferenceException at CombatPhase._performAbility and ate the action.
            // Refusing at the CONFIRM CLICK (rather than inside PerformAbility) is what keeps this
            // draw-count neutral: the ability is never declared, so no slot roll is taken and the action is
            // never sent to the network, on any peer. See ConfirmLeftClickVenueTile_Prefix.
            Patch(harmony, typeof(CombatPhase), "_onConfirmLeftClickVenueTile",
                prefix: M(typeof(TrainerPartnerPersistence),
                    nameof(TrainerPartnerPersistence.ConfirmLeftClickVenueTile_Prefix)));

            // ---- Pokemon Trainer partner NICKNAMES (test-checklist L, see TrainerNicknamePatches) ----
            // CharacterHelper.GetDisplayName(CharacterComponent, bool) (CharacterHelper.cs:1653) is the one
            // resolver every visible name funnels through -- GetDisplayNameForUI computes its result by
            // calling it (CharacterHelper.cs:1695), and nothing caches it, so a postfix here reaches the
            // combat nameplate, the inspect panel, the turn-order tooltip and the party HUD alike.
            // argumentTypes is REQUIRED: there are three GetDisplayName overloads and AccessTools.Method
            // returns null on an ambiguous name.
            Patch(harmony, typeof(CharacterHelper), "GetDisplayName",
                postfix: M(typeof(TrainerNicknamePatches), nameof(TrainerNicknamePatches.GetDisplayName_Postfix)),
                argumentTypes: new[] { typeof(CharacterComponent), typeof(bool) });

            // ---- Pokemon Trainer partner PANEL (test-checklist L, see TrainerPartnerPanel) ----
            // A SECOND postfix on the method SkillDisplayPatches already postfixes; Harmony composes them.
            Patch(harmony, typeof(CharacterCustomizationViewHelper), "RenderStatsContainer",
                postfix: M(typeof(TrainerPartnerPanel), nameof(TrainerPartnerPanel.RenderStatsContainer_Postfix)));

            // ---- Pokemon Trainer partner AUTONOMY (test-checklist L1, see TrainerPartnerAutonomy) ----
            // Partners already take their own turns: CombatPhase.cs:1918 routes on Has<AIComponent>()
            // alone, and TryCreateSummon builds every summon via CreateCharacterEntity(pIsNpc: true),
            // which attaches one at CharacterHelper.cs:1877 BEFORE TryCreateSummon overwrites GroupIndex
            // at CombatHelper.cs:243. §L1's reading of CombatHelper.cs:2238 as "ally summons get no
            // AIComponent" is wrong; that line is a net that never fires. These three hooks supply what
            // was actually missing: direction, proof, and a fail-safe. All three fast-out on
            // TrainerPartnerAutonomy.NoPartners, which only a resolved ARM_ORIG_TRAINER_BALL_* summon
            // can make false.

            // DIRECTION. GetPreferredTarget's pTendency comes from the ABILITY config
            // (AIHelper.cs:481), and partners reuse shipped ability ids, so this is the only place a
            // partner LINE's role can be expressed. Rewrites the tendency ONLY when the ability
            // authored NONE.
            // Registered on AiDrawNeutrality, not on TrainerPartnerAutonomy: the shaping and the
            // compensating draw that keeps the gate at exactly one shared-stream draw have to happen in
            // that order, inside one method, and ONLY inside a ForceAiDecision frame. See
            // AiDrawNeutrality's remarks and TrainerPartnerAutonomy.ApplyTendencyShaping's.
            AiDrawNeutrality.PreferredTargetHookInstalled =
                Patch(harmony, typeof(AIHelper), "GetPreferredTarget",
                    prefix: M(typeof(AiDrawNeutrality),
                        nameof(AiDrawNeutrality.GetPreferredTarget_Prefix)));

            // DRAW-NEUTRALITY (docs/MULTIPLAYER.md R2). The prefix opens the frame that scopes the shaping
            // above; the postfix takes the draw a Focus Fire order would otherwise have skipped, because
            // AIComponent.PriorityTargets is drained BEFORE GetPreferredTarget is consulted and a hit there
            // bypasses the tendency gate entirely (AIHelper.cs:516-551); the finalizer guarantees the frame
            // is closed even if the game's own method throws.
            Patch(harmony, typeof(AIHelper), "ForceAiDecision",
                prefix: M(typeof(AiDrawNeutrality), nameof(AiDrawNeutrality.ForceAiDecision_Prefix)),
                postfix: M(typeof(AiDrawNeutrality), nameof(AiDrawNeutrality.ForceAiDecision_Postfix)),
                finalizer: M(typeof(AiDrawNeutrality), nameof(AiDrawNeutrality.ForceAiDecision_Finalizer)));

            // PROOF. _performAiDecision is reached from the AI branch (CombatPhase.cs:1922) and never
            // from the PLAYER branch (:1925), so a record written here cannot be produced by a
            // human-driven turn. This is the log line and the state a test reads.
            Patch(harmony, typeof(CombatPhase), "_performAiDecision",
                prefix: M(typeof(TrainerPartnerAutonomy),
                    nameof(TrainerPartnerAutonomy.PerformAiDecision_Prefix)));

            // FAIL-SAFE. The decision is an ARGUMENT to _performAiDecision inside async void
            // _engageActiveEntity, so a throw out of these two helpers stops the fight on that entity
            // with nothing to catch it. For a tracked partner only, the finalizer substitutes
            // InteractableHelper.SkipTurnDecision; every other entity gets its exception back unchanged.
            Patch(harmony, typeof(AIHelper), "BehaviourAiDecision",
                finalizer: M(typeof(TrainerPartnerAutonomy),
                    nameof(TrainerPartnerAutonomy.AiDecision_Finalizer)));
            Patch(harmony, typeof(AIHelper), "StandardAiDecision",
                finalizer: M(typeof(TrainerPartnerAutonomy),
                    nameof(TrainerPartnerAutonomy.AiDecision_Finalizer)));

            // ---- Pokemon Trainer CHARM PROGRESSION (test-checklist L/L1/L2, see TrainerCharmProgression) ----
            // Without these two the Trainer is a permanently ONE-PARTNER class: CF_ORIG_TRAINER.Things
            // grants exactly ARM_ORIG_TRAINER_BALL_GRASS and all twelve ON_COMBAT_START summon recipes
            // open with HAS_ITEM of their own charm, so possession IS the switch and two of the three
            // authored lines are unreachable for the whole run.
            //
            // LEVEL-UP. ProgressionHelper.EntityGainXP (ProgressionHelper.cs:648) is the single overload
            // that compares the level before (:654) and after (:703) and raises
            // eAbilityResults.LEVELED_UP at :721 -- the only place in the engine a level-up is announced.
            // EntitiesGainXP (:640) is a ForEach over it, so this one target catches both.
            Patch(harmony, typeof(ProgressionHelper), "EntityGainXP",
                postfix: M(typeof(TrainerCharmProgression),
                    nameof(TrainerCharmProgression.EntityGainXP_Postfix)));

            // CATCH-UP. The grant is a reconcile, not an edge, so it also runs at combat start -- which
            // covers a save loaded at a level whose level-up event is long gone. Deliberately a PREFIX:
            // CombatHookPatches.SetInitiative_Postfix is what dispatches ON_COMBAT_START and evaluates
            // those twelve HAS_ITEM gates, and Harmony runs every prefix before the original and every
            // postfix after, so the charm is guaranteed to be in the inventory before the gate reads it.
            // void -- it can never skip the original.
            Patch(harmony, typeof(CombatHelper), "SetInitiative",
                prefix: M(typeof(TrainerCharmProgression),
                    nameof(TrainerCharmProgression.SetInitiative_Prefix)));

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
        /// Same as the type+name overload, for a target already resolved to a <see cref="MethodBase"/> —
        /// used where the name cannot be written down, i.e. compiler-generated local functions whose
        /// emitted names carry ordinals that drift between game builds. <paramref name="label"/> is what
        /// the <c>Target found:</c>/<c>Target NOT found:</c> line reports, since <c>method.Name</c> for
        /// such a target is unreadable.
        /// </summary>
        private static bool Patch(HarmonyLib.Harmony harmony, MethodBase target, string label,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod finalizer = null)
        {
            try
            {
                harmony.Patch(target, prefix: prefix, postfix: postfix, finalizer: finalizer);
                Log.LogInfo($"Target found: {label}");
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError($"Target NOT found: {label} — patch installation threw, feature disabled (fail-safe): {ex}");
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
                    // Gameplay: a pack switched off on ONE peer means that peer simulates from different
                    // merged Configs. The pack ids in the payload already cover the enabled set; this makes
                    // the DISABLED set explicit too, and names the exact knob in the mismatch banner.
                    PackEnabledKnobs[pack.Manifest.Id] = CFConfig.Bind(
                        Instance.Config, "Packs", $"{pack.Manifest.Id}.Enabled", true,
                        $"Enable/disable pack '{pack.Manifest.Id}' without deleting it.",
                        ParityClass.Gameplay);
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[ClassForge] Pre-scan for per-pack knobs failed (fail-safe — packs still respect their own manifest 'enabled' field): {ex}");
            }
        }

        /// <summary>
        /// W1-H — what <c>[Packs] AdditionalRoots</c> reports to the parity handshake instead of its raw
        /// value.
        ///
        /// <para><b>The bug it fixes.</b> The knob is a list of ABSOLUTE PATHS and was compared verbatim,
        /// so two friends with byte-identical packs installed on <c>C:</c> and <c>D:</c> produced a
        /// <c>FeaturesMismatch</c> — which defaults to <c>Block</c>, i.e. a refused join with every
        /// ClassForge feature off. Nothing about a drive letter changes what executes. What DOES matter is
        /// which packs the roots contributed, and that is emitted here as an ordinal-sorted, comma-joined
        /// list of PACK IDS. Their CONTENT is separately covered by <c>DataHasher</c>'s <c>DataHash</c>,
        /// and the enabled subset by <see cref="ParityRegistrationBuilder"/>'s own pack-id entries — so
        /// this is the only remaining question ("did your extra roots contribute the same packs as mine?")
        /// and it is now the only one asked.</para>
        ///
        /// <para>Ids rather than a digest, deliberately: the value is rendered straight into the mismatch
        /// banner, and <c>"CF_PACK_MINE"</c> vs <c>"(none)"</c> tells a player what to do where sixteen hex
        /// characters would not. The default (no additional roots) reports <c>"(none)"</c> on every
        /// machine, which is the overwhelmingly common case.</para>
        ///
        /// <para>Failure is reported as <c>"(unscannable)"</c> rather than silently as <c>"(none)"</c>: an
        /// unreadable root is a real difference between two peers and must not compare equal to a peer
        /// that has none.</para>
        /// </summary>
        internal static string AdditionalRootsParityValue(string rawRoots)
        {
            var ids = new List<string>();
            try
            {
                var fs = new ClassForge.Core.IO.FileSystemFileSource();
                foreach (var part in (rawRoots ?? string.Empty).Split(','))
                {
                    var root = part.Trim();
                    if (root.Length == 0) continue;
                    // A configured-but-absent root is itself a divergence between two peers, so it is
                    // reported rather than skipped.
                    if (!fs.DirectoryExists(root)) { ids.Add("(missing)"); continue; }

                    var findings = new List<ClassForge.Core.Finding>();
                    foreach (var pack in ClassForge.Core.PackDiscovery.Discover(fs, new[] { root }, findings))
                        ids.Add(pack.Manifest.Id);
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[ClassForge] could not scan [Packs] AdditionalRoots for the parity payload: " + ex.Message);
                return "(unscannable)";
            }

            if (ids.Count == 0) return "(none)";
            ids.Sort(StringComparer.Ordinal);
            // ';' not ',': ParityValue.Sanitize rewrites commas anyway, and the payload's own log lines
            // join entries with commas.
            return string.Join(";", ids.ToArray());
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

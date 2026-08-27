using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;

namespace ClassForge.Plugin
{
    /// <summary>
    /// The Pokemon Trainer's partner-persistence store (test-checklist §L0).
    ///
    /// <para><b>What §L0 asks for.</b> A partner is a resource managed across a run, not a per-fight
    /// disposable. Its current HP survives between fights; a partner reduced to 0 HP is DOWNED (it does
    /// not appear in the next fight and is never deleted); and nothing but a TOWN heals or revives it.
    /// That is the class's deliberate downside.</para>
    ///
    /// <para><b>Where the state lives, and why.</b> On the partner's BALL ITEM, in
    /// <c>Thing.CustomData</c> — <c>Thing.CustomData</c> is <c>public Dictionary&lt;string,string&gt;</c>
    /// (decompile <c>Thing.cs:18</c>), read and written through <c>CoreHelper.SetCustomData(Thing,…)</c> /
    /// <c>CoreHelper.TryGetCustomData(Thing,…)</c> (<c>CoreHelper.cs:1646</c>, <c>:1696</c>). This is the
    /// game's own idiom for durable per-item mod data: the bee's cooldown is
    /// <c>CoreHelper.SetCustomData(characterThing, "COOLDOWN", …)</c> (<c>FollowerHelper.cs:338/342</c>),
    /// read back at <c>InventoryHelper.cs:1621</c> against the constant
    /// <c>InventoryHelper.CUSTOM_DATA_COOLDOWN</c> (<c>InventoryHelper.cs:73</c>). Because the ball is an
    /// ordinary <c>Thing</c> in the Trainer's <c>CharacterComponent.Things</c>
    /// (<c>CharacterComponent.cs:38</c>), the record rides the existing save schema — no new save keys,
    /// no new ThingConfig, and it survives exactly as long as the ball does.</para>
    ///
    /// <para><b>Scoping — this must not touch any other class.</b> The ONE discriminator is the ball's
    /// config id prefix (<see cref="BallPrefix"/>, <c>ARM_ORIG_TRAINER_BALL_</c>). Those three items are
    /// granted only by the <c>CF_ORIG_TRAINER</c> class config, so an entity that holds none is invisible
    /// to every code path here. Every hot-path patch body ALSO fast-outs on
    /// <c>_live.Count == 0</c>, so with no Trainer partner in the fight the cost is one integer compare.</para>
    ///
    /// <para><b>Slot identity.</b> One ball = one partner LINE = one persisted partner. The recipe that
    /// summons a partner is named <c>SKILL_CF_TRAINER_&lt;LINE&gt;_&lt;STAGE&gt;</c> (e.g.
    /// <c>SKILL_CF_TRAINER_GRASS_1</c>), and the matching ball is
    /// <c>ARM_ORIG_TRAINER_BALL_&lt;LINE&gt;</c> — so the line token in the recipe id is the whole
    /// mapping, and no JSON change is needed to establish it.</para>
    ///
    /// <para><b>Testability.</b> <see cref="StateSummary"/> and <see cref="Records"/> are public static
    /// PROPERTIES that recompute from the live ball items every read, so a test reads the persisted store
    /// itself rather than a mirror of it — reachable by <c>crucible_get</c>, which resolves a type by
    /// simple name and walks static fields/properties. See the class docs on those members for the exact
    /// paths.</para>
    /// </summary>
    public static class TrainerPartnerPersistence
    {
        // ---- CustomData keys on the ball Thing ----

        /// <summary>Character config of the partner currently bound to this ball.</summary>
        public const string KeyConfig = "CF_POKE_CONFIG";
        /// <summary>The partner's current HP, carried between fights. This is the whole feature.</summary>
        public const string KeyHp = "CF_POKE_HP";
        /// <summary>The partner's max HP as of its last summon, so a stage change can carry the fraction.</summary>
        public const string KeyMaxHp = "CF_POKE_MAXHP";
        /// <summary>
        /// "1" when the partner is DOWNED. A DERIVED MIRROR of <see cref="KeyHp"/>, kept for the
        /// read-back/diagnostic surfaces only — <b>never</b> read as the source of truth.
        ///
        /// <para>It is written exclusively alongside <see cref="KeyHp"/> from replicated combat state
        /// (see <c>Write</c>), so the two cannot disagree and the value the vendor's desync MD5 sees is
        /// the same on every peer. Every consumer computes <c>hp &lt; 1</c> instead; see
        /// <c>ResolveSlot</c> for why storing this as an independent fact was a desync hazard.</para>
        /// </summary>
        public const string KeyDowned = "CF_POKE_DOWNED";
        /// <summary>The stage (1-4) the ball last sent out, for diagnostics and the max-HP band.</summary>
        public const string KeyStage = "CF_POKE_STAGE";

        /// <summary>The one discriminator. Only the Trainer class grants items with this prefix.</summary>
        public const string BallPrefix = "ARM_ORIG_TRAINER_BALL_";

        private const string RecipePrefix = "SKILL_CF_TRAINER_";

        // =====================================================================================
        // ONE SLOT PER BALL LINE  (the canonical ball)
        // =====================================================================================

        /// <summary>
        /// The ONE <c>Thing</c> that is this character's slot for <paramref name="ballConfig"/>, out of
        /// however many copies of that ball the inventory happens to hold.
        ///
        /// <para><b>Why this exists.</b> Ash's three charms are granted one-per-line by
        /// <see cref="TrainerCharmProgression"/>, which checks <c>HasCharm</c> before granting, so they can
        /// never duplicate. Gary's <c>ARM_ORIG_TRAINER_BALL_CAPTURE</c> is an ordinary obtainable item with
        /// <c>"Stacks": false</c>, so N of them are N separate <c>Thing</c>s in
        /// <c>CharacterComponent.Things</c> — and every one of them was its own partner record, i.e. its own
        /// minion slot. Gary's whole design is exactly one.</para>
        ///
        /// <para><b>Which ball wins, and why that ordering.</b> The one that is already carrying a creature.
        /// <c>CF_POKE_CONFIG</c> is written by <c>CAPTURE</c> (RecipeActionExecutor.ExecCapture) and IS the
        /// captured monster; a ball that holds one is a partner, an empty ball is just a throwable. Picking
        /// the record-holder — rather than the first ball in list order, which is what every call site used
        /// to do independently — is what makes a second ball unable to ORPHAN the creature stored in the
        /// first: capture, send-out and the HP write-through all resolve to the same Thing no matter which
        /// copy was thrown or what order the inventory is in. Second preference is a ball that has been sent
        /// out before (<c>CF_POKE_MAXHP</c>) but whose config id was cleared; last, plain first-in-list, so
        /// the very first capture of a run has a deterministic home.</para>
        /// </summary>
        internal static Thing CanonicalBall(CharacterComponent cc, string ballConfig)
        {
            try
            {
                if (cc == null || cc.Things == null || string.IsNullOrEmpty(ballConfig)) return null;

                Thing first = null, everSentOut = null;
                for (int i = 0; i < cc.Things.Count; i++)
                {
                    var t = cc.Things[i];
                    if (t == null || !string.Equals(t.ConfigName, ballConfig, StringComparison.Ordinal)) continue;
                    if (first == null) first = t;

                    string raw;
                    if (CoreHelper.TryGetCustomData(t, KeyConfig, out raw) && !string.IsNullOrEmpty(raw))
                        return t;                                     // holds a creature — it IS the slot
                    if (everSentOut == null
                        && CoreHelper.TryGetCustomData(t, KeyMaxHp, out raw) && !string.IsNullOrEmpty(raw))
                        everSentOut = t;
                }
                return everSentOut ?? first;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] the canonical " + ballConfig + " could not be resolved (fail-safe, no "
                    + "slot is bound this time): " + ex.Message);
                return null;
            }
        }

        // ---- knobs ----

        /// <summary>Master switch for the whole §L0 system.</summary>
        internal static ConfigEntry<bool> Enable;

        /// <summary>
        /// Clamp a summoned partner's MAX HP to the stage band in <see cref="MaxHpByStage"/>
        /// (§L0 item 4 — partners are meant to be killable). Scoped to partners this class tracks;
        /// it can never reach a character that holds no Trainer ball.
        /// </summary>
        internal static ConfigEntry<bool> CapMaxHp;

        /// <summary>Comma-separated max HP for stages 1..4.</summary>
        internal static ConfigEntry<string> MaxHpByStage;

        /// <summary>Revive + full-heal every partner when the Trainer opens a TOWN's service menu.</summary>
        internal static ConfigEntry<bool> ReviveInTown;

        internal static void Bind(ConfigFile config)
        {
            Enable = config.Bind("Trainer", "EnablePartnerPersistence", true,
                "Pokemon Trainer partner persistence (test-checklist L0). A partner's current HP carries "
                + "between fights, stored in Thing.CustomData on that partner's ARM_ORIG_TRAINER_BALL_* "
                + "item; a partner reduced to 0 HP is DOWNED -- it is not sent out in the next fight and "
                + "is never deleted -- and only a town heals or revives it. Off = partners are ordinary "
                + "fresh-every-fight summons, exactly as before. Scoped entirely to characters carrying a "
                + "Trainer ball item, so no other class is affected either way.");

            CapMaxHp = config.Bind("Trainer", "CapPartnerMaxHp", true,
                "Clamp a Trainer partner's MAX HP to the PartnerMaxHpByStage band, so partners stay low-HP "
                + "and killable (test-checklist L0 item 4). The partners reuse SHIPPED creature configs "
                + "(JELLY_ACID_01, PLANT_SWAMP_08, ...) whose HP was authored for enemies, so without this "
                + "a late-stage partner is far tankier than the design wants. Applied per summoned partner "
                + "via CharacterHelper.OverrideBaseStat(entity, \"HP\", ...) -- a per-ENTITY override "
                + "(CharacterComponent.OverrideStats, CharacterHelper.cs:1044-1048), never a config edit, so "
                + "ordinary enemies of the same species are untouched.");

            MaxHpByStage = config.Bind("Trainer", "PartnerMaxHpByStage", "16,26,37,48",
                "Comma-separated MAX HP for partner stages 1,2,3,4, used when CapPartnerMaxHp is true. "
                + "Tier-weighted and deliberately low. A short list reuses its last entry for higher stages; "
                + "an unparseable entry leaves that stage uncapped.");

            ReviveInTown = config.Bind("Trainer", "RevivePartnersInTown", true,
                "Revive every DOWNED partner and fully heal every hurt one when the Trainer opens a TOWN's "
                + "service menu. This is the ONLY thing in the mod that restores partner HP -- nothing else "
                + "heals them mid-run, which is the class's stated downside. Off = partners can never be "
                + "revived, which makes the class unplayable past the first few fights; it exists so the "
                + "behaviour can be isolated in a test.");
        }

        private static bool Active
        {
            get { return ClassForgePlugin.FeaturesActive && Enable != null && Enable.Value; }
        }

        // =====================================================================================
        // live per-combat tracking
        // =====================================================================================

        /// <summary>
        /// One summoned partner that is standing on the board right now, and the ball its HP writes
        /// through to. Populated by <see cref="RegisterSummon"/>, drained by <see cref="ClearLive"/>.
        /// </summary>
        private sealed class LiveSlot
        {
            internal Thing Ball;
            internal string BallConfig;
            internal string OwnerGuid;
            internal string PartnerConfig;
            internal int Stage;
            internal int MaxHp;
        }

        private static readonly Dictionary<Entity, LiveSlot> _live = new Dictionary<Entity, LiveSlot>();

        /// <summary>True when nothing is being tracked — the fast-out every hot patch body takes first.</summary>
        internal static bool NothingTracked { get { return _live.Count == 0; } }

        /// <summary>Drops per-combat tracking. Called on entering and leaving a fight; the durable state
        /// is already on the ball items, so there is nothing to lose here.</summary>
        internal static void ClearLive()
        {
            // Autonomy tracks the same partners for a different purpose (test-checklist L1); its
            // lifetime is identical, so it is drained from the one place that already runs on combat
            // start and end rather than being given a second pair of hooks.
            TrainerPartnerAutonomy.Clear();
            // Focus Fire's counters share the same per-combat lifetime, for the same reason.
            TrainerFocusFire.Clear();
            if (_live.Count == 0) return;
            _live.Clear();
        }

        // =====================================================================================
        // summon-time gate + restore  (called from RecipeActionExecutor.ExecSummon)
        // =====================================================================================

        /// <summary>
        /// The persisted slot a partner summon would use, or null when this summon is not a Trainer
        /// partner at all (in which case the caller must behave exactly as it did before).
        ///
        /// <para>Resolution is entirely id-driven: the recipe id
        /// <c>SKILL_CF_TRAINER_&lt;LINE&gt;_&lt;STAGE&gt;</c> names the line, and the ball is
        /// <c>ARM_ORIG_TRAINER_BALL_&lt;LINE&gt;</c> in the summoner's own
        /// <c>CharacterComponent.Things</c>. No ball, no slot — so a recipe that merely looks like a
        /// Trainer recipe on a character that is not a Trainer resolves to nothing.</para>
        /// </summary>
        internal sealed class SlotBinding
        {
            internal Thing Ball;
            internal string BallConfig;
            internal string OwnerGuid;
            internal int Stage;
            internal bool Downed;
            internal bool HasRecord;
            internal int StoredHp;
            internal int StoredMaxHp;
            internal string StoredConfig;
        }

        internal static SlotBinding ResolveSlot(Entity summoner, string recipeId)
        {
            if (!Active) return null;
            try
            {
                if (summoner == null || string.IsNullOrEmpty(recipeId)) return null;
                if (!recipeId.StartsWith(RecipePrefix, StringComparison.Ordinal)) return null;

                // SKILL_CF_TRAINER_GRASS_1 -> line "GRASS", stage 1. Anything that does not end in a
                // stage number is not a partner-summon recipe (BOND_STRENGTH, GOTTA_TRAIN_EM, ...).
                string tail = recipeId.Substring(RecipePrefix.Length);
                int underscore = tail.LastIndexOf('_');
                if (underscore <= 0 || underscore == tail.Length - 1) return null;
                string line = tail.Substring(0, underscore);
                int stage;
                if (!int.TryParse(tail.Substring(underscore + 1), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out stage)) return null;

                CharacterComponent cc;
                if (!summoner.TryGet<CharacterComponent>(out cc) || cc == null || cc.Things == null) return null;

                string ballConfig = BallPrefix + line;
                // ONE slot per line, however many copies of the ball are carried — see CanonicalBall.
                Thing ball = CanonicalBall(cc, ballConfig);
                if (ball == null) return null;

                var binding = new SlotBinding
                {
                    Ball = ball,
                    BallConfig = ballConfig,
                    OwnerGuid = SafeGuid(summoner),
                    Stage = stage,
                };

                string raw;
                binding.HasRecord = CoreHelper.TryGetCustomData(ball, KeyHp, out raw)
                                    && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                                    out binding.StoredHp);

                // DOWNED is DERIVED, never read back as the source of truth.
                //
                // CF_POKE_HP is written from replicated combat state (SyncFromEntity, below) and is
                // therefore the same number on every peer. CF_POKE_DOWNED was a second, independently
                // stored fact about the same thing -- and because it is an ordinary Thing.CustomData entry
                // (Thing.cs:18 is a public field with no [JsonIgnore]; the only [JsonIgnore] in the type is
                // on StackCount at :30) it rides GameRunData._entities into the vendor's desync MD5, while
                // NOT being on NetworkDebuggingHelper's ignore list (:40-43). Two facts that can disagree,
                // one of them hashed, is a desync waiting for a seam that only one peer runs -- which the
                // town revive was.
                //
                // Deriving it means the gate in ExecSummon (RecipeActionExecutor: "downed slot => skip
                // ADD_CHARACTER") is a predicate over a replicated number, so both peers field the same
                // roster, so GetTargetableTiles returns the same Count, so ShuffleList takes the same
                // number of draws off the shared stream on the next AI turn (AIHelper.cs:507-511,
                // GameRandom.cs:227-241).
                //
                // No HP record at all = a ball that has never been sent out. That is "fresh", not "downed".
                binding.Downed = binding.HasRecord && binding.StoredHp < 1;

                if (CoreHelper.TryGetCustomData(ball, KeyMaxHp, out raw))
                    int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out binding.StoredMaxHp);
                CoreHelper.TryGetCustomData(ball, KeyConfig, out binding.StoredConfig);
                return binding;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] could not resolve a Trainer partner slot for " + recipeId
                    + " (fail-safe, the summon proceeds unmanaged): " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Binds a freshly-summoned partner to its ball: caps max HP, restores the carried-over current
        /// HP, and registers the entity so every later health change writes through.
        ///
        /// <para><b>Stage change carries the FRACTION, not the number.</b> A ball whose stored config
        /// differs from the creature now being sent out has evolved to another stage with a different max
        /// HP, so replaying the raw number would either overheal or instantly re-down it. The stored
        /// hp/max ratio is applied to the new max instead, floored at 1.</para>
        /// </summary>
        internal static void RegisterSummon(Entity partner, SlotBinding slot, string partnerConfig)
        {
            if (!Active || partner == null || slot == null) return;
            try
            {
                CharacterComponent cc;
                if (!partner.TryGet<CharacterComponent>(out cc) || cc == null) return;

                // Cap max HP FIRST: GetMaxHealth is what the restore clamps against.
                int capped = StageMaxHp(slot.Stage);
                if (capped > 0)
                {
                    // Per-ENTITY override (CharacterComponent.OverrideStats via
                    // CharacterHelper.OverrideBaseStat, CharacterHelper.cs:1044-1048). GetCharacterBaseStat
                    // (CharacterHelper.cs:350) prefers OverrideStats for any key it contains, so this
                    // re-bases HP for this one creature and nothing else of its species.
                    CharacterHelper.OverrideBaseStat(partner, "HP", capped);
                }

                int maxHp = CharacterHelper.GetMaxHealth(partner);
                if (maxHp < 1) maxHp = 1;

                int hp;
                if (!slot.HasRecord)
                {
                    hp = maxHp;                                   // first time this ball is used
                }
                else if (!string.IsNullOrEmpty(slot.StoredConfig)
                         && !string.Equals(slot.StoredConfig, partnerConfig, StringComparison.Ordinal)
                         && slot.StoredMaxHp > 0)
                {
                    double fraction = (double)slot.StoredHp / slot.StoredMaxHp;
                    hp = (int)Math.Round(fraction * maxHp, MidpointRounding.AwayFromZero);
                }
                else
                {
                    hp = slot.StoredHp;
                }
                if (hp < 1) hp = 1;                               // a DOWNED partner never reaches here
                if (hp > maxHp) hp = maxHp;

                cc.CurrentHealth = hp;

                Write(slot.Ball, partnerConfig, hp, maxHp, false, slot.Stage);
                _live[partner] = new LiveSlot
                {
                    Ball = slot.Ball,
                    BallConfig = slot.BallConfig,
                    OwnerGuid = slot.OwnerGuid,
                    PartnerConfig = partnerConfig,
                    Stage = slot.Stage,
                    MaxHp = maxHp,
                };

                // test-checklist L1 -- the partner acts on its OWN turn. Hooked here, and only here,
                // because this is the one point in the plugin that has already proved this entity is a
                // Trainer partner (ResolveSlot matched an ARM_ORIG_TRAINER_BALL_* item in the summoner's
                // inventory). Adding a second slot-resolution for autonomy would mean two discriminators
                // that can drift apart. See TrainerPartnerAutonomy for why the AIComponent grant inside
                // it is a guarded no-op on today's summon path.
                TrainerPartnerAutonomy.EnsureAutonomous(partner, slot.BallConfig, partnerConfig);

                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] partner sent out: ball=" + slot.BallConfig + " config=" + partnerConfig
                    + " stage=" + slot.Stage + " hp=" + hp + "/" + maxHp
                    + (slot.HasRecord ? " (carried over)" : " (first summon, full)"));
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] a Trainer partner was summoned but could not be bound to its ball "
                    + "(fail-safe, it fights this battle with no persistence): " + ex.Message);
            }
        }

        // =====================================================================================
        // the MANUAL charm route  (the SECOND way a partner is sent out)
        // =====================================================================================

        /// <summary>
        /// Ability-id prefix of the three "Send Out" abilities the charm items carry
        /// (<c>CF_TRAINER_SUMMON_&lt;LINE&gt;_T&lt;STAGE&gt;_ATTACK</c>, authored in the pack's
        /// <c>abilities.json</c> and bagged on <c>ARM_ORIG_TRAINER_BALL_&lt;LINE&gt;</c>).
        /// </summary>
        private const string SummonAbilityPrefix = "CF_TRAINER_SUMMON_";

        /// <summary>
        /// Gary's ball line. Deliberately EXEMPT from this guard: his capture/persistence path is a
        /// separate, verified feature and nothing here may alter it.
        /// </summary>
        private const string CaptureLine = "CAPTURE";

        private static FieldInfo _fUiSelectedThing;
        private static FieldInfo _fUiSelectedAbilityAction;
        private static FieldInfo _fAbilityName;
        private static bool _confirmFieldsResolved;
        private static bool _loggedConfirmFieldMiss;

        /// <summary>
        /// Prefix on <c>CombatPhase._onConfirmLeftClickVenueTile(GameObject)</c> — refuses a MANUAL charm
        /// use whose partner is DOWNED, which is the same refusal <c>RecipeActionExecutor.ExecSummon</c>
        /// already makes on the automatic <c>ON_COMBAT_START</c> route.
        ///
        /// <para><b>Why there are two routes at all.</b> A partner reaches the board two ways: the
        /// <c>SKILL_CF_TRAINER_&lt;LINE&gt;_&lt;STAGE&gt;</c> recipe at combat start, which runs through
        /// <see cref="ResolveSlot"/> and is gated, and the charm's own
        /// <c>CF_TRAINER_SUMMON_&lt;LINE&gt;_T&lt;STAGE&gt;_ATTACK</c> ability, which is a plain vendor
        /// <c>ADD_CHARACTER</c> executed by <c>CombatPhase._performAbility</c> and never touches the recipe
        /// engine. Sending out a DOWNED partner that way threw
        /// <c>NullReferenceException at CombatPhase._performAbility</c> and cost the player the action.</para>
        ///
        /// <para><b>Why the hook is the CONFIRM CLICK and not <c>PerformAbility</c>.</b> By the time
        /// <c>CombatHelper.PerformAbility</c> is reached, <c>_performAbility</c> has already disabled
        /// interaction, hidden the ability menu and — the part that matters — taken its slot-roll draws off
        /// the shared <c>GameRandom</c>. Refusing at the confirm click is BEFORE all of that: the ability is
        /// never declared, so <b>zero</b> draws are taken on either peer, the network send at
        /// <c>CombatPhase.cs:4661</c> never happens so no peer runs the action either, and the player keeps
        /// the turn with the charm still selected. Draw-count symmetric by construction.</para>
        ///
        /// <para>Everything else returns true untouched — a non-Trainer character never carries a ball, and
        /// Gary's <c>ARM_ORIG_TRAINER_BALL_CAPTURE</c> is explicitly exempt.</para>
        /// </summary>
        public static bool ConfirmLeftClickVenueTile_Prefix(object __instance)
        {
            try
            {
                if (!Active || __instance == null) return true;

                if (!_confirmFieldsResolved)
                {
                    _confirmFieldsResolved = true;
                    var t = __instance.GetType();
                    _fUiSelectedThing = AccessTools.Field(t, "_uiSelectedThing");
                    _fUiSelectedAbilityAction = AccessTools.Field(t, "_uiSelectedAbilityAction");
                    if (_fUiSelectedAbilityAction != null)
                        _fAbilityName = AccessTools.Field(_fUiSelectedAbilityAction.FieldType, "AbilityName");
                }

                // Refuse LOUDLY, once, rather than guessing: if the selection cannot be read there is no
                // way to tell a charm use from any other click, so the guard stands down entirely.
                if (_fUiSelectedThing == null || _fUiSelectedAbilityAction == null || _fAbilityName == null)
                {
                    if (!_loggedConfirmFieldMiss)
                    {
                        _loggedConfirmFieldMiss = true;
                        ClassForgePlugin.Log.LogWarning(
                            "[ClassForge] CombatPhase's tile-confirm selection fields could not be read "
                            + "(_uiSelectedThing=" + (_fUiSelectedThing == null ? "MISSING" : "ok")
                            + " _uiSelectedAbilityAction=" + (_fUiSelectedAbilityAction == null ? "MISSING" : "ok")
                            + " AbilityName=" + (_fAbilityName == null ? "MISSING" : "ok")
                            + "); the DOWNED-partner guard on manual charm use is OFF this session.");
                    }
                    return true;
                }

                var thing = _fUiSelectedThing.GetValue(__instance) as Thing;
                if (thing == null || string.IsNullOrEmpty(thing.ConfigName)) return true;
                if (!thing.ConfigName.StartsWith(BallPrefix, StringComparison.Ordinal)) return true;

                string line = thing.ConfigName.Substring(BallPrefix.Length);
                if (string.Equals(line, CaptureLine, StringComparison.Ordinal)) return true;

                var abilityAction = _fUiSelectedAbilityAction.GetValue(__instance);
                var abilityName = abilityAction == null ? null : _fAbilityName.GetValue(abilityAction) as string;
                if (string.IsNullOrEmpty(abilityName)
                    || !abilityName.StartsWith(SummonAbilityPrefix, StringComparison.Ordinal)) return true;

                // DOWNED is DERIVED from the stored HP, exactly as ResolveSlot derives it: no HP record at
                // all is a ball that has never been sent out, which is "fresh", not "downed".
                string raw; int storedHp;
                if (!CoreHelper.TryGetCustomData(thing, KeyHp, out raw)) return true;
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out storedHp)) return true;
                if (storedHp >= 1) return true;

                string storedConfig;
                CoreHelper.TryGetCustomData(thing, KeyConfig, out storedConfig);
                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] partner NOT sent out: ball=" + thing.ConfigName + " config="
                    + (storedConfig ?? "") + " is DOWNED (hp=0). Only a town revives it.");
                return false;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] the DOWNED-partner check on a manual charm use could not run "
                    + "(fail-safe, the click proceeds as before): " + ex.Message);
                return true;
            }
        }

        /// <summary>Logs and records the refusal to send out a DOWNED partner.</summary>
        internal static void LogDownedSkip(SlotBinding slot, string partnerConfig)
        {
            ClassForgePlugin.Log.LogDebug(
                "[ClassForge] partner NOT sent out: ball=" + slot.BallConfig + " config=" + partnerConfig
                + " is DOWNED (hp=0). Only a town revives it.");
        }

        // =====================================================================================
        // write-through
        // =====================================================================================

        /// <summary>
        /// Copies <paramref name="entity"/>'s live <c>CurrentHealth</c> onto its ball, marking it DOWNED
        /// at &lt; 1 — the same test the engine itself calls death (<c>CharacterHelper.IsDead</c> is
        /// <c>CurrentHealth &lt; 1</c>, <c>CharacterHelper.cs:1472</c>).
        ///
        /// <para>This is called from the health hooks, so it must be free for everyone else: the
        /// dictionary miss is the whole cost, and callers fast-out on <see cref="NothingTracked"/>
        /// before even that.</para>
        ///
        /// <para>Write-through rather than a single flush at end of combat is not a preference. On a
        /// normal victory <c>CombatPhase._endCombatAsync</c> banishes summons in its <c>cleanUpSummon</c>
        /// pass before <c>Deinitialize</c> runs (see <see cref="SummonLeakPatches"/>), so a partner that
        /// died — or simply the roster — may already be gone by the time any teardown hook could read it.</para>
        /// </summary>
        internal static void SyncFromEntity(Entity entity)
        {
            if (_live.Count == 0 || entity == null) return;
            try
            {
                LiveSlot slot;
                if (!_live.TryGetValue(entity, out slot)) return;

                CharacterComponent cc;
                if (!entity.TryGet<CharacterComponent>(out cc) || cc == null) return;

                int hp = cc.CurrentHealth;
                if (hp < 0) hp = 0;
                bool downed = hp < 1;
                Write(slot.Ball, slot.PartnerConfig, hp, slot.MaxHp, downed, slot.Stage);

                if (downed)
                {
                    ClassForgePlugin.Log.LogDebug(
                        "[ClassForge] partner DOWNED: ball=" + slot.BallConfig + " config="
                        + slot.PartnerConfig + " hp=0. It will not be sent out again until a town revives it.");
                }
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] a Trainer partner's HP could not be written back to its ball "
                    + "(fail-safe, the last stored value stands): " + ex.Message);
            }
        }

        private static void Write(Thing ball, string config, int hp, int maxHp, bool downed, int stage)
        {
            CoreHelper.SetCustomData(ball, KeyConfig, config ?? "");
            CoreHelper.SetCustomData(ball, KeyHp, hp.ToString(CultureInfo.InvariantCulture));
            CoreHelper.SetCustomData(ball, KeyMaxHp, maxHp.ToString(CultureInfo.InvariantCulture));
            CoreHelper.SetCustomData(ball, KeyDowned, downed ? "1" : "0");
            CoreHelper.SetCustomData(ball, KeyStage, stage.ToString(CultureInfo.InvariantCulture));
        }

        // =====================================================================================
        // town heal + revive  (the ONLY restore path)
        // =====================================================================================

        /// <summary>
        /// Runs <see cref="ReviveAndHeal"/> over the WHOLE party, from the town-service seam every peer
        /// executes (see <c>TrainerPartnerPatches.TryPatchUseService</c>).
        ///
        /// <para><b>Why the whole party and not the purchasing character.</b> The revive writes
        /// <c>CF_POKE_HP</c>, which is hashed into the vendor's desync MD5, so WHICH balls it touches must
        /// be a function of replicated state alone. The purchasing character is only reachable from the
        /// compiler-generated closure around <c>_onUseService</c>; the party is
        /// <c>GameRun.Entities.FindAll(e =&gt; e.Has&lt;PlayerComponent&gt;())</c> — the game's own party
        /// query (<c>AdventureDirector.cs:363</c>) — and is identical on every peer. The practical
        /// difference is that one player buying a town service also revives a second Trainer's partners;
        /// the alternative was a peer-dependent set of writes, which is a desync.</para>
        ///
        /// <para>Idempotent: a ball already at full HP is skipped, so running it on every service purchase
        /// (including the boat ones) costs nothing and cannot double-apply.</para>
        /// </summary>
        internal static void ReviveAndHealParty(string why)
        {
            if (!Active || ReviveInTown == null || !ReviveInTown.Value) return;
            try
            {
                var env = RouterHelper.Env;
                var run = env == null ? null : env.GameRun;
                var entities = run == null ? null : run.Entities;
                if (entities == null) return;

                for (int i = 0; i < entities.Count; i++)
                {
                    var e = entities[i];
                    if (e == null || !e.Has<PlayerComponent>()) continue;
                    ReviveAndHeal(e, why);
                }
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] the party-wide partner revive could not enumerate the party "
                    + "(fail-safe, partners keep their stored state): " + ex.Message);
            }
        }

        /// <summary>
        /// Clears DOWNED and restores full HP on every ball <paramref name="character"/> carries.
        /// Called only from the town-services hook, via <see cref="ReviveAndHealParty"/>.
        /// </summary>
        internal static void ReviveAndHeal(Entity character, string why)
        {
            if (!Active || ReviveInTown == null || !ReviveInTown.Value || character == null) return;
            try
            {
                CharacterComponent cc;
                if (!character.TryGet<CharacterComponent>(out cc) || cc == null || cc.Things == null) return;

                int revived = 0, healed = 0;
                for (int i = 0; i < cc.Things.Count; i++)
                {
                    var t = cc.Things[i];
                    if (t == null || t.ConfigName == null) continue;
                    if (!t.ConfigName.StartsWith(BallPrefix, StringComparison.Ordinal)) continue;

                    string raw;
                    if (!CoreHelper.TryGetCustomData(t, KeyMaxHp, out raw)) continue;   // never sent out
                    int maxHp;
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxHp)
                        || maxHp < 1) continue;

                    int hp = 0;
                    if (CoreHelper.TryGetCustomData(t, KeyHp, out raw))
                        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out hp);
                    // Derived, exactly as ResolveSlot derives it — the stored CF_POKE_DOWNED is only a
                    // mirror, and a legacy save written before that rule could carry a stale one.
                    bool wasDowned = hp < 1;
                    if (!wasDowned && hp >= maxHp) continue;

                    CoreHelper.SetCustomData(t, KeyHp, maxHp.ToString(CultureInfo.InvariantCulture));
                    CoreHelper.SetCustomData(t, KeyDowned, "0");
                    if (wasDowned) revived++; else healed++;
                }

                if (revived > 0 || healed > 0)
                    ClassForgePlugin.Log.LogDebug(
                        "[ClassForge] town services (" + why + "): revived " + revived
                        + " downed partner(s) and healed " + healed + " hurt one(s) to full.");
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] the town partner revive failed (fail-safe, partners keep their stored "
                    + "state): " + ex.Message);
            }
        }

        // =====================================================================================
        // read-back for tests
        // =====================================================================================

        /// <summary>
        /// One persisted partner, as read back out of its ball's <c>Thing.CustomData</c>.
        /// <c>crucible_get</c> renders an object as <c>TypeName { field=val, ... }</c>, so every member
        /// here is a plain public field.
        /// </summary>
        public sealed class PartnerRecord
        {
            /// <summary>Guid of the Trainer holding the ball.</summary>
            public string Owner;
            /// <summary>Ball item config id, e.g. <c>ARM_ORIG_TRAINER_BALL_GRASS</c>.</summary>
            public string Ball;
            /// <summary>Character config of the partner bound to this ball.</summary>
            public string Config;
            /// <summary>Persisted current HP. This is the value a persistence test asserts on.</summary>
            public int CurrentHp;
            /// <summary>Max HP as of the last summon.</summary>
            public int MaxHp;
            /// <summary>True when the partner is DOWNED and will not be sent out.</summary>
            public bool Downed;
            /// <summary>Stage (1-4) the ball last sent out.</summary>
            public int Stage;
            /// <summary>True while this partner is standing on the board in the current fight.</summary>
            public bool OnBoard;

            public override string ToString()
            {
                return Ball + " cfg=" + (Config ?? "-") + " hp=" + CurrentHp + "/" + MaxHp
                       + " downed=" + Downed + " stage=" + Stage + " onBoard=" + OnBoard;
            }
        }

        /// <summary>
        /// Every persisted partner in the run, recomputed from the live ball items on every read — so a
        /// test is reading the durable store itself, not a cache of it.
        ///
        /// <para><b>Test path:</b> <c>crucible_get TrainerPartnerPersistence.Records</c>, or
        /// <c>TrainerPartnerPersistence.Records[0].CurrentHp</c> /
        /// <c>TrainerPartnerPersistence.Records[0].Downed</c> for a single field.</para>
        /// </summary>
        public static List<PartnerRecord> Records
        {
            get
            {
                var list = new List<PartnerRecord>();
                try
                {
                    var run = RouterHelper.Env != null ? RouterHelper.Env.GameRun : null;
                    var entities = run != null ? run.Entities : null;
                    if (entities == null) return list;

                    var onBoard = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var kv in _live)
                        onBoard.Add(kv.Value.OwnerGuid + "/" + kv.Value.BallConfig);

                    foreach (var e in entities)
                    {
                        if (e == null) continue;
                        CharacterComponent cc;
                        if (!e.TryGet<CharacterComponent>(out cc) || cc == null || cc.Things == null) continue;

                        string owner = SafeGuid(e);
                        // ONE record per (owner, ball line), not one per Thing. Duplicate copies of a ball
                        // are spare throwables, not extra minion slots: only the CanonicalBall is bound by
                        // ResolveSlot, so only it is reported. Ash's three lines are unaffected — the charm
                        // progression can never grant a second copy of a line.
                        var seenLines = new HashSet<string>(StringComparer.Ordinal);
                        for (int i = 0; i < cc.Things.Count; i++)
                        {
                            var thing = cc.Things[i];
                            if (thing == null || thing.ConfigName == null) continue;
                            if (!thing.ConfigName.StartsWith(BallPrefix, StringComparison.Ordinal)) continue;
                            if (!seenLines.Add(thing.ConfigName)) continue;

                            var t = CanonicalBall(cc, thing.ConfigName);
                            if (t == null) continue;

                            var rec = new PartnerRecord
                            {
                                Owner = owner,
                                Ball = t.ConfigName,
                                Config = ReadString(t, KeyConfig),
                                CurrentHp = ReadInt(t, KeyHp, -1),
                                MaxHp = ReadInt(t, KeyMaxHp, -1),
                                Downed = ReadString(t, KeyDowned) == "1",
                                Stage = ReadInt(t, KeyStage, 0),
                                OnBoard = onBoard.Contains(owner + "/" + t.ConfigName),
                            };
                            list.Add(rec);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] partner records could not be read back: " + ex.Message);
                }
                return list;
            }
        }

        /// <summary>
        /// The same data as one flat string, for a log-style or substring assertion.
        ///
        /// <para><b>Test path:</b> <c>crucible_get TrainerPartnerPersistence.StateSummary</c>. Renders as
        /// <c>partners=2 | ARM_ORIG_TRAINER_BALL_GRASS cfg=JELLY_ACID_01 hp=7/12 downed=False stage=1
        /// onBoard=True ; ...</c>, or <c>partners=0</c> when no ball has ever been used.</para>
        /// </summary>
        public static string StateSummary
        {
            get
            {
                var records = Records;
                var sb = new StringBuilder();
                sb.Append("partners=").Append(records.Count);
                for (int i = 0; i < records.Count; i++)
                    sb.Append(i == 0 ? " | " : " ; ").Append(records[i].ToString());
                return sb.ToString();
            }
        }

        // =====================================================================================
        // helpers
        // =====================================================================================

        /// <summary>Max HP for <paramref name="stage"/> from the knob, or 0 for "leave uncapped".</summary>
        private static int StageMaxHp(int stage)
        {
            try
            {
                if (CapMaxHp == null || !CapMaxHp.Value) return 0;
                var parts = (MaxHpByStage != null ? MaxHpByStage.Value : null);
                if (string.IsNullOrEmpty(parts)) return 0;
                var split = parts.Split(',');
                if (split.Length == 0) return 0;
                int index = stage - 1;
                if (index < 0) index = 0;
                if (index >= split.Length) index = split.Length - 1;
                int value;
                if (!int.TryParse(split[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out value) || value < 1) return 0;
                return value;
            }
            catch (Exception) { return 0; }
        }

        private static string SafeGuid(Entity e)
        {
            try { return e.Guid ?? ""; } catch (Exception) { return ""; }
        }

        private static string ReadString(Thing t, string key)
        {
            string raw;
            return CoreHelper.TryGetCustomData(t, key, out raw) ? raw : null;
        }

        private static int ReadInt(Thing t, string key, int fallback)
        {
            string raw; int value;
            if (CoreHelper.TryGetCustomData(t, key, out raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return value;
            return fallback;
        }
    }
}

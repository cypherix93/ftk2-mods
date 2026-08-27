using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Makes the Pokemon Trainer's partners take their OWN combat turns (test-checklist §L1:
    /// "I don't want to have to control them, but I want him directing the pokemon in some way").
    ///
    /// <para><b>How the game decides AI vs player, and where that leaves us.</b> There is exactly one
    /// gate, and it is not the group index. <c>CombatPhase._engageActiveEntity</c> (decompile
    /// <c>CombatPhase.cs:1619</c>) ends in a three-way branch on the active entity — remote-network
    /// player, then <c>CombatPhase.cs:1918</c> <c>else if (_activeCharacterEntity.Has&lt;AIComponent&gt;())</c>
    /// which calls <c>_performAiDecision(..., AIHelper.BehaviourAiDecision(...))</c>, and only otherwise
    /// (<c>:1925</c>) the <c>[PLAYER]</c> branch that enables input and waits. <b>Presence of
    /// <c>AIComponent</c> is the whole test.</b> Nothing anywhere calls <c>Remove&lt;AIComponent&gt;</c>
    /// (grepped across the decompile), and <c>CharacterHelper.IsEnemy</c> (<c>CharacterHelper.cs:1495</c>,
    /// <c>GroupIndex == 1</c>) is never consulted on this path.</para>
    ///
    /// <para><b>The §L1 premise was wrong, and this is the correction.</b> §L1 reads
    /// <c>CombatHelper.cs:2238</c> — <c>if (characterComponent2.GroupIndex == 1 &amp;&amp;
    /// !pSummonEntity.Has&lt;AIComponent&gt;()) pSummonEntity.Add&lt;AIComponent&gt;(...)</c> — as "ally
    /// summons get no AIComponent". That line is a defensive net that never fires. Every non-follower
    /// summon is built by <c>CombatHelper.TryCreateSummon</c>'s
    /// <c>CharacterHelper.CreateCharacterEntity(text, pIsNpc: true, pGuid, pRandom, null, null, null,
    /// pAddPlayerComponent: false, pSummonType, pLevel)</c> (<c>CombatHelper.cs:222</c>) — the ten-argument
    /// overload at <c>CharacterHelper.cs:1848</c>, whose body does
    /// <c>if (pIsNpc) entity.Add&lt;AIComponent&gt;(new AIComponent())</c> at <c>CharacterHelper.cs:1875-1877</c>.
    /// <c>TryCreateSummon</c> then overwrites <c>GroupIndex</c> to the tile's group at
    /// <c>CombatHelper.cs:243</c> — <b>after</b> the component is already attached. So a group-0 partner
    /// summoned through <c>ADD_CHARACTER</c> already carries an <c>AIComponent</c> and already takes its
    /// own turn. <see cref="EnsureAutonomous"/> keeps the grant anyway, guarded, because it costs one
    /// dictionary probe and it is the thing that stops being true if the summon path ever changes.</para>
    ///
    /// <para><b>What actually needed building, then, is DIRECTION.</b> The AI decision path is entirely
    /// group-relative — <c>CombatHelper.GetTargetGroup(pFromEntity, pTargetType)</c>
    /// (<c>CombatHelper.cs:257-269</c>) resolves ENEMY/ALLY against the acting entity's OWN
    /// <c>GroupIndex</c>, so an ally AI attacks enemies and buffs allies with no special casing. But
    /// WHICH enemy or ally comes from <c>AIHelper.GetPreferredTarget</c>'s <c>pTendency</c>
    /// (<c>AIHelper.cs:586</c>), and that tendency is read off the ABILITY config
    /// (<c>AIHelper.ForceAiDecision</c>, <c>AIHelper.cs:481</c>:
    /// <c>eAiTendencies tendency = combatAbilityConfig.Tendency;</c>). Partners reuse SHIPPED ability ids
    /// (test-checklist §L2 trap 7), whose authored tendency is whatever the vanilla creature wanted — so
    /// the only place to express a partner LINE's role is here, on the way in.
    /// <see cref="GetPreferredTarget_Prefix"/> substitutes a per-line tendency, and <b>only when the
    /// ability authored <c>NONE</c></b>, so a shipped ability that already states a preference keeps it.</para>
    ///
    /// <para><b>Scoping.</b> Every entry point fast-outs on <see cref="NoPartners"/>
    /// (<c>_partners.Count == 0</c>). <c>_partners</c> is populated from exactly one place —
    /// <c>TrainerPartnerPersistence.RegisterSummon</c>, which itself runs only after
    /// <c>TrainerPartnerPersistence.ResolveSlot</c> matched an <c>ARM_ORIG_TRAINER_BALL_*</c> item in the
    /// summoner's own inventory. No other class, no other summon, no enemy can enter this dictionary.</para>
    ///
    /// <para><b>Fail-safe.</b> A partner that cannot decide must PASS, never hang the loop.
    /// <c>_engageActiveEntity</c> is <c>async void</c> and <c>[SendReportOnException]</c>, so an
    /// exception escaping <c>AIHelper.BehaviourAiDecision</c>/<c>StandardAiDecision</c> at
    /// <c>CombatPhase.cs:1922</c> kills the turn and the fight sits there forever.
    /// <see cref="AiDecision_Finalizer"/> converts that, <b>for tracked partners only</b>, into
    /// <c>InteractableHelper.SkipTurnDecision</c> — the game's own pass, the same object
    /// <c>CombatPhase.cs:1715</c> uses for a stunned character. For anyone else the exception is
    /// returned unchanged and behaviour is bit-identical to unpatched.</para>
    /// </summary>
    public static class TrainerPartnerAutonomy
    {
        // =====================================================================================
        // config
        // =====================================================================================

        /// <summary>[Trainer] EnablePartnerAutonomy.</summary>
        internal static ConfigEntry<bool> Enable;

        /// <summary>[Trainer] ShapePartnerTendency.</summary>
        internal static ConfigEntry<bool> ShapeTendency;

        /// <summary>[Trainer] EnablePartnerCommandTendency.</summary>
        internal static ConfigEntry<bool> CommandTendency;

        internal static void Bind(ConfigFile config)
        {
            Enable = config.Bind("Trainer", "EnablePartnerAutonomy", true,
                "Pokemon Trainer partners take their OWN combat turns (test-checklist L1). NOTE: the "
                + "engine already does this -- CombatHelper.TryCreateSummon builds every summon with "
                + "CharacterHelper.CreateCharacterEntity(pIsNpc: true), which attaches an AIComponent at "
                + "CharacterHelper.cs:1877, and CombatPhase.cs:1918 routes any entity with an AIComponent "
                + "to the AI branch regardless of its group. What this switch adds is (a) a guarded "
                + "re-grant so autonomy survives a change to the summon path, (b) the per-partner "
                + "record a test reads back, and (c) the fail-safe that turns a faulted AI decision into "
                + "a passed turn instead of a hung combat loop. Everything is keyed to entities that "
                + "resolved to an ARM_ORIG_TRAINER_BALL_* item, so no other class, summon or enemy is "
                + "reachable. Off = none of the three, and partners behave exactly as the base game "
                + "makes them behave.");

            ShapeTendency = config.Bind("Trainer", "ShapePartnerTendency", true,
                "Give each partner LINE an AI targeting tendency that matches its authored role -- GRASS "
                + "protects (helps the most hurt ally, and locks down the hardest-hitting enemy), WATER "
                + "goes for the least-resistant enemy so its WET/status kit sticks, FIRE goes for the "
                + "least-armoured enemy for maximum damage. Applied as a prefix on "
                + "AIHelper.GetPreferredTarget (AIHelper.cs:586) and ONLY when the ability itself authored "
                + "eAiTendencies.NONE, so a shipped ability that already states a preference keeps it. "
                + "Non-strict tendencies only on THIS path: the three members of AIHelper's "
                + "_strictTendencies list (MUSTHAVEPOISON / MUSTNOTHAVEHIVE / MUSTHAVELOWHEALTH, "
                + "AIHelper.cs:27-32) make an ability UNUSABLE when nothing qualifies, which as a "
                + "STANDING tendency would make partners pass their turn far too often. (One strict "
                + "tendency IS used, but only as a per-round team command and only behind a "
                + "qualifying-target check -- see EnablePartnerCommandTendency.) Off = partners use "
                + "whatever tendency their ability authored, and still act.");

            CommandTendency = config.Bind("Trainer", "EnablePartnerCommandTendency", true,
                "Let the Trainer's FINISH IT command override a partner's targeting for the round it is "
                + "active (test-checklist L1, command 4). While a partner carries "
                + "STATUS_CONCENTRATION_CF_FINISH_IT it targets with MUSTHAVELOWHEALTH, a STRICT tendency "
                + "(AIHelper.cs:27-32) that BYPASSES the PRW chance gate at AIHelper.cs:589-597 -- which "
                + "is what turns the command from an incentive into real target selection. It is aligned "
                + "with its own payout: SKILL_CF_PARTNER_CMD_FINISH_IT pays +8 against a target at or "
                + "below 35% HP and the tendency binds at 20%, so the tendency's set sits strictly INSIDE "
                + "the reward window. A strict tendency with no qualifying target returns "
                + "cannotUseAbility:true (AIHelper.cs:612-615) and walks the whole decision back to "
                + "SKIP_TURN, so it is applied ONLY after this code has confirmed, against the same "
                + "candidate set the engine will build, that at least one target qualifies; otherwise the "
                + "partner falls back to its non-strict line tendency and still acts. A partner is never "
                + "made to stand still by this. Unlike ShapePartnerTendency, the command DOES override an "
                + "ability's own authored tendency -- that is the point of a player command. AFFLICT has "
                + "NO tendency override on purpose: its steer is the recipe payout, which pays only on a "
                + "target with ZERO harmful statuses, and any poison-seeking tendency would fight it. Off "
                + "= all four commands stay incentives only (the recipes' payouts), targeting unchanged.");
        }

        private static bool Active
        {
            get { return ClassForgePlugin.FeaturesActive && Enable != null && Enable.Value; }
        }

        // =====================================================================================
        // live tracking  (populated only from TrainerPartnerPersistence.RegisterSummon)
        // =====================================================================================

        private sealed class PartnerState
        {
            internal string BallConfig;      // ARM_ORIG_TRAINER_BALL_GRASS
            internal string Line;            // GRASS
            internal string PartnerConfig;   // PLANT_SWAMP_03
            internal string LastAbility;
            internal string LastCommand;
            internal string LastTendency;
            internal int LastTargetX;
            internal int LastTargetY;
            internal int Acts;
        }

        private static readonly Dictionary<Entity, PartnerState> _partners =
            new Dictionary<Entity, PartnerState>();

        /// <summary>True when no partner is on the board — the fast-out every patch body takes first.</summary>
        internal static bool NoPartners { get { return _partners.Count == 0; } }

        /// <summary>Drops per-combat tracking. Called from <c>TrainerPartnerPersistence.ClearLive</c>,
        /// which the combat-start/end hooks in <c>SummonLeakPatches</c> already drive.</summary>
        internal static void Clear()
        {
            if (_partners.Count == 0) return;
            _partners.Clear();
        }

        /// <summary>
        /// Registers a freshly-summoned partner as autonomous, and grants it an <c>AIComponent</c> if it
        /// somehow lacks one.
        ///
        /// <para>Called from <c>TrainerPartnerPersistence.RegisterSummon</c> — the one place in the plugin
        /// that has already proved this entity is a Trainer partner. Reusing that call site rather than
        /// adding a second slot-resolution is deliberate: there is exactly one discriminator in this
        /// plugin and it stays that way.</para>
        ///
        /// <para><c>Entity.Add&lt;T&gt;</c> is <c>Components.Add(typeof(T), pComponent)</c>
        /// (<c>Entity.cs:38-42</c>) — a raw dictionary Add that THROWS on a duplicate key — so the
        /// <c>Has</c> guard is load-bearing, not decorative. In practice it is always taken: see the class
        /// remarks for why the component is already there.</para>
        /// </summary>
        internal static void EnsureAutonomous(Entity partner, string ballConfig, string partnerConfig)
        {
            if (!Active || partner == null) return;
            try
            {
                bool granted = false;
                if (!partner.Has<AIComponent>())
                {
                    partner.Add<AIComponent>(new AIComponent());
                    granted = true;
                }

                string line = LineOf(ballConfig);
                _partners[partner] = new PartnerState
                {
                    BallConfig = ballConfig,
                    Line = line,
                    PartnerConfig = partnerConfig,
                };

                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] partner AUTONOMOUS: ball=" + ballConfig + " config=" + partnerConfig
                    + " line=" + (line ?? "?")
                    + (granted
                        ? " (AIComponent GRANTED by ClassForge)"
                        : " (AIComponent already present from CharacterHelper.cs:1877)")
                    + " tendency=" + DescribeTendency(line));
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] a Trainer partner could not be made autonomous (fail-safe, it fights "
                    + "this battle however the base game drives it): " + ex.Message);
            }
        }

        /// <summary>"ARM_ORIG_TRAINER_BALL_GRASS" -> "GRASS"; null for anything unexpected.</summary>
        private static string LineOf(string ballConfig)
        {
            if (string.IsNullOrEmpty(ballConfig)) return null;
            if (!ballConfig.StartsWith(TrainerPartnerPersistence.BallPrefix, StringComparison.Ordinal))
                return null;
            string line = ballConfig.Substring(TrainerPartnerPersistence.BallPrefix.Length);
            return line.Length == 0 ? null : line;
        }

        // =====================================================================================
        // tendency shaping
        // =====================================================================================

        /// <summary>
        /// The tendency a partner LINE prefers when aiming at the OPPOSING group.
        ///
        /// <para>All three are non-strict, so <c>AIHelper.GetPreferredTarget</c> falls through to its
        /// ordinary "widest area of effect" pick when nothing sorts usefully
        /// (<c>AIHelper.cs:616-630</c>) rather than declaring the ability unusable. The strict members
        /// (<c>MUSTHAVELOWHEALTH</c>, <c>MUSTHAVEPOISON</c>, <c>MUSTNOTHAVEHIVE</c> —
        /// <c>AIHelper.cs:27-32</c>) return <c>cannotUseAbility: true</c> on an empty result
        /// (<c>AIHelper.cs:612-615</c>), which walks the decision back to SKIP_TURN. That is the wrong
        /// default for a partner whose whole job is to act. A strict tendency IS used, but only as a
        /// per-round team command and only behind an explicit qualifying-target check — see
        /// <see cref="TryCommandTendency"/> and <see cref="StrictTendencyHasTarget"/>.</para>
        /// </summary>
        private static bool TryEnemyTendency(string line, out eAiTendencies tendency)
        {
            switch (line)
            {
                // Protector. MOSTDAMAGE orders by ATK descending (AIHelper.cs:641) — the biggest
                // threat is the one worth taunting, entangling or blocking.
                case "GRASS": tendency = eAiTendencies.MOSTDAMAGE; return true;

                // Status carrier. LEASTRESISTANCE orders by RES ascending (AIHelper.cs:639); WET and
                // the rest of the water kit stick best where resistance is lowest.
                case "WATER": tendency = eAiTendencies.LEASTRESISTANCE; return true;

                // Damage. LEASTARMOR orders by DEF ascending (AIHelper.cs:637).
                case "FIRE": tendency = eAiTendencies.LEASTARMOR; return true;
            }
            tendency = eAiTendencies.NONE;
            return false;
        }

        /// <summary>
        /// The tendency a partner prefers when aiming at its OWN group — the same for every line,
        /// because a partner only ever targets an ally to help it. <c>LEASTHEALTH</c> orders by current
        /// health ascending (<c>AIHelper.cs:657</c>), i.e. the most hurt ally, which is exactly what the
        /// grass kit was authored around. Non-strict, unlike <c>MUSTHAVELOWHEALTH</c>.
        /// </summary>
        private const eAiTendencies AllyTendency = eAiTendencies.LEASTHEALTH;

        private static string DescribeTendency(string line)
        {
            eAiTendencies enemy;
            string enemyName = TryEnemyTendency(line, out enemy) ? enemy.ToString() : "(none)";
            return "enemy=" + enemyName + " ally=" + AllyTendency;
        }

        /// <summary>
        /// Rewrites <c>pTendency</c> for a tracked partner when the ability authored <c>NONE</c>.
        ///
        /// <para><b>No longer registered as a Harmony prefix directly.</b> It is called by
        /// <see cref="AiDrawNeutrality.GetPreferredTarget_Prefix"/>, which owns the single registered prefix
        /// on <c>AIHelper.GetPreferredTarget</c>. Two reasons, both multiplayer:</para>
        /// <list type="number">
        ///   <item><description><b>It must run inside <c>ForceAiDecision</c> only.</b>
        ///   <c>GetPreferredTarget</c> has five other call sites (<c>CombatHelper.cs:317</c>, <c>:994</c>,
        ///   <c>CombatPhase.cs:1829</c>, <c>:3483</c>, <c>:4436</c>), every one passing
        ///   <c>eAiTendencies.NONE</c> literally and therefore taking ZERO draws at
        ///   <c>AIHelper.cs:589</c>. Rewriting NONE to a non-strict line tendency there turned a
        ///   draw-free call into a one-draw call — and <c>CombatPhase.cs:3483</c> computes
        ///   <c>_uiSelectedPosition</c>, presentation state with no reason to be evaluated identically on
        ///   every peer. That was a latent desync introduced by a targeting feature.</description></item>
        ///   <item><description><b>The rewrite itself moves the draw count</b> (NONE → MOSTDAMAGE is 0
        ///   draws → 1; anything → the strict MUSTHAVELOWHEALTH is 1 → 0), so it has to be immediately
        ///   followed by the compensating draw that makes the gate cost exactly one either way. Splitting
        ///   those two across two independently-registered prefixes would leave their ORDER up to Harmony;
        ///   here it is a plain method call and cannot be reordered.</description></item>
        /// </list>
        ///
        /// <para><b>Ally vs enemy is decided from the arguments, not from a second lookup.</b>
        /// <c>pTargetGroup</c> is already <c>CombatHelper.GetTargetGroup(pOrigin, abilityConfig.Target)</c>
        /// (<c>AIHelper.cs:502</c>), which returns the origin's own group for SELF/ALLY* and the opposing
        /// group for ENEMY (<c>CombatHelper.cs:271-290</c>). Comparing it to the origin's
        /// <c>GroupIndex</c> therefore says which way this ability points, for free.</para>
        ///
        /// <para><b>Cost to every other AI entity: one dictionary Count and one TryGetValue.</b> This
        /// method is on the hot path of every enemy decision in the game, so both fast-outs come before
        /// anything else and the whole body is inside a catch that leaves <c>pTendency</c> untouched.</para>
        /// </summary>
        public static void ApplyTendencyShaping(Entity pOrigin, ref eAiTendencies pTendency,
            eTileOccupancies pOccupancy, int pTargetGroup, List<Entity> pTargetableTiles,
            CombatState pCombatState)
        {
            if (_partners.Count == 0) return;
            try
            {
                if (pOrigin == null) return;
                PartnerState state;
                if (!_partners.TryGetValue(pOrigin, out state) || state == null) return;

                CharacterComponent cc;
                if (!pOrigin.TryGet<CharacterComponent>(out cc) || cc == null) return;

                bool ally = pTargetGroup == cc.GroupIndex;

                // ---- 1. TEAM COMMAND (strict). Beats the ability's own authored tendency, because that
                // is what a player command is for. Enemy-facing only: FINISH_IT is about which ENEMY to
                // hit, and MUSTHAVELOWHEALTH is not meaningful against a friendly target.
                //
                // ONE command steers targeting, and only one: FINISH_IT. AFFLICT deliberately does NOT --
                // see TryCommandTendency for why its steer lives entirely in the recipe payout.
                if (!ally && CommandTendency != null && CommandTendency.Value)
                {
                    eAiTendencies commanded;
                    string commandStatus;
                    if (TryCommandTendency(pOrigin, out commanded, out commandStatus))
                    {
                        if (StrictTendencyHasTarget(commanded, pOccupancy, pTargetableTiles, pCombatState))
                        {
                            pTendency = commanded;
                            state.LastCommand = commandStatus;
                            state.LastTendency = commanded.ToString();
                            ClassForgePlugin.Log.LogDebug(
                                "[ClassForge] partner COMMANDED: ball=" + state.BallConfig
                                + " line=" + (state.Line ?? "?")
                                + " command=" + commandStatus + " tendency=" + commanded
                                + " (STRICT -- bypasses the PRW gate at AIHelper.cs:589-597; a "
                                + "qualifying target inside the payout window was confirmed present first)");
                            return;
                        }

                        state.LastCommand = commandStatus + "(no qualifying target)";
                        ClassForgePlugin.Log.LogDebug(
                            "[ClassForge] partner command NOT applied: ball=" + state.BallConfig
                            + " command=" + commandStatus + " wanted=" + commanded
                            + " but nothing on a targetable tile qualifies, and a strict tendency with an "
                            + "empty result returns cannotUseAbility:true (AIHelper.cs:612-615) which "
                            + "walks the decision back to SKIP_TURN. Falling back so the partner acts.");
                    }
                }

                // ---- 2. LINE DEFAULT (non-strict), unchanged: only when the ability authored NONE.
                if (pTendency != eAiTendencies.NONE) return;       // the ability stated a preference
                if (ShapeTendency == null || !ShapeTendency.Value) return;

                eAiTendencies chosen;
                if (ally)
                {
                    chosen = AllyTendency;
                }
                else if (!TryEnemyTendency(state.Line, out chosen))
                {
                    return;                                        // unknown line: leave the game alone
                }

                pTendency = chosen;
                state.LastTendency = chosen.ToString();

                if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                    ClassForgePlugin.Log.LogDebug(
                        "[ClassForge] partner tendency: ball=" + state.BallConfig
                        + " line=" + (state.Line ?? "?")
                        + " scope=" + (ally ? "ALLY" : "ENEMY")
                        + " tendency=" + chosen + " (ability authored NONE)");
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] a Trainer partner's tendency could not be applied (fail-safe, the "
                    + "ability's own tendency stands): " + ex.Message);
            }
        }

        // =====================================================================================
        // team commands -> STRICT tendencies, and the guarantee they can never cause SKIP_TURN
        // =====================================================================================

        /// <summary>The Finish It marker. Stamped on ALLY_ALL by <c>SKILL_CF_TRAINER_CMD_FINISH_IT</c>.</summary>
        public const string StatusFinishIt = "STATUS_CONCENTRATION_CF_FINISH_IT";

        /// <summary>
        /// The strict tendency a live command marker asks for, if any.
        ///
        /// <para><b>Exactly one command steers targeting: FINISH_IT.</b> A tendency override earns its
        /// risk only when it steers the partner INSIDE the window its payout recipe already pays for.
        /// FINISH_IT does: <c>SKILL_CF_PARTNER_CMD_FINISH_IT</c> pays +8 MAGICAL damage on
        /// <c>HP_THRESHOLD {Of: TRIGGER_TARGET, Comparator: LTE, Percent: 35}</c>, and
        /// <c>MUSTHAVELOWHEALTH</c> binds at <c>health / maxHealth &lt;= 0.2m</c>
        /// (<c>AIHelper.cs:665</c>). <b>The two thresholds differ — 20% for the tendency, 35% for the
        /// payout — and that is deliberate.</b> The tendency's set is the strictly NARROWER one, so every
        /// target it can steer onto is already inside the reward window and it can never aim the partner
        /// somewhere the recipe will not pay. Were the numbers the other way round the override would
        /// routinely buy a target that earns nothing.</para>
        ///
        /// <para><b>AFFLICT deliberately has NO tendency override, and must not be given one.</b> An
        /// earlier draft used <c>MUSTHAVEPOISON</c> here; that was wrong and is removed.
        /// <c>MUSTHAVEPOISON</c> selects targets that ALREADY carry poison (<c>AIHelper.cs:664</c>:
        /// <c>FindAll(e =&gt; CoreHelper.HasStatusType(e, POISON))</c>), while
        /// <c>SKILL_CF_PARTNER_CMD_AFFLICT</c> pays out ONLY on a target carrying
        /// <c>STATUS_COUNT {Of: TRIGGER_TARGET, Category: HARMFUL, Comparator: EQ, Value: 0}</c> — a
        /// CLEAN one. The two halves pointed in opposite directions, so the override made the command pay
        /// out LESS often than leaving targeting alone.</para>
        ///
        /// <para>The obvious repair — <c>NOTHASPOISON</c> — is refused too. It is not in
        /// <c>_strictTendencies</c> (<c>AIHelper.cs:27-32</c>), so it is subject to the PRW chance gate at
        /// <c>AIHelper.cs:589-597</c> and is therefore only an INCENTIVE, not an order. Afflict already
        /// has an incentive, and a better one: the recipe payout fires unconditionally on a clean target
        /// with a synthesized PERFECT and no chance gate at all. Stacking a weaker, rollable second
        /// incentive on top of a working unconditional one buys nothing and adds a moving part.
        /// <b>So §L1's "prefer un-afflicted targets" IS implemented — as the RECIPE PAYOUT, not as a
        /// tendency.</b> While AFFLICT is up a partner simply keeps its line's standing tendency
        /// (<see cref="TryEnemyTendency"/>) and earns the bonus whenever it lands on a clean target.</para>
        /// </summary>
        private static bool TryCommandTendency(Entity partner, out eAiTendencies tendency, out string status)
        {
            tendency = eAiTendencies.NONE;
            status = null;
            StatusEffectComponent comp;
            if (!partner.TryGet<StatusEffectComponent>(out comp) || comp == null || comp.Statuses == null)
                return false;

            if (comp.Statuses.ContainsKey(StatusFinishIt))
            {
                tendency = eAiTendencies.MUSTHAVELOWHEALTH;
                status = StatusFinishIt;
                return true;
            }
            return false;
        }

        /// <summary>
        /// <b>The whole SKIP_TURN guarantee lives here.</b> Returns true only when
        /// <c>GetPreferredTarget</c> is certain to find at least one target for
        /// <paramref name="tendency"/>, so <c>cannotUseAbility: true</c> cannot be produced.
        ///
        /// <para>It reproduces the engine's own candidate set line for line
        /// (<c>AIHelper.cs:598-606</c>): the tile positions of <c>pTargetableTiles</c>, then
        /// <c>pCombatState.Entities.FindAll(e =&gt; e.Has&lt;CombatComponent&gt;() &amp;&amp;
        /// !CharacterHelper.IsDead(e) &amp;&amp; tilePositions.Contains(e.Get&lt;VenueComponent&gt;()
        /// .TilePosition))</c>, then the tendency's own filter from <c>_orderTargetsByTendency</c>
        /// (<c>AIHelper.cs:665</c>): <c>MUSTHAVELOWHEALTH</c> keeps
        /// <c>health / maxHealth &lt;= 0.2</c>. If that filtered list is non-empty here it is non-empty
        /// there, the engine takes <c>list.First()</c>, the position stops being <c>NULL_HEX</c>, and the
        /// strict-empty branch at <c>AIHelper.cs:612-615</c> is never reached.</para>
        ///
        /// <para>Every deviation is in the SAFE direction — it can only make this method say "no" where
        /// the engine would have said "yes", which costs a slightly weaker tendency and never a lost
        /// turn: an entity whose <c>VenueComponent</c> is missing is skipped rather than dereferenced
        /// (the engine's <c>e.Get&lt;VenueComponent&gt;()</c> would throw), a null or empty targetable
        /// list is refused outright, and <c>pOccupancy == EMPTY</c> is refused because the engine's
        /// character-ordering branch does not run at all in that case (<c>AIHelper.cs:598</c>) and
        /// neither strict tendency used here is in <c>_emptyOccupancyTendencies</c>
        /// (<c>AIHelper.cs:34</c>).</para>
        /// </summary>
        private static bool StrictTendencyHasTarget(eAiTendencies tendency, eTileOccupancies pOccupancy,
            List<Entity> pTargetableTiles, CombatState pCombatState)
        {
            try
            {
                if (pOccupancy == eTileOccupancies.EMPTY) return false;
                if (pTargetableTiles == null || pTargetableTiles.Count == 0) return false;
                if (pCombatState == null || pCombatState.Entities == null) return false;

                var tiles = new List<(int, int)>();
                for (int i = 0; i < pTargetableTiles.Count; i++)
                {
                    var t = pTargetableTiles[i];
                    if (t == null) continue;
                    VenueComponent vc;
                    if (!t.TryGet<VenueComponent>(out vc) || vc == null) continue;
                    tiles.Add(vc.TilePosition);
                }
                if (tiles.Count == 0) return false;

                var entities = pCombatState.Entities;
                for (int i = 0; i < entities.Count; i++)
                {
                    var e = entities[i];
                    if (e == null) continue;
                    if (!e.Has<CombatComponent>()) continue;
                    if (CharacterHelper.IsDead(e)) continue;

                    VenueComponent vc;
                    if (!e.TryGet<VenueComponent>(out vc) || vc == null) continue;
                    if (!ContainsTile(tiles, vc.TilePosition)) continue;

                    if (Qualifies(e, tendency)) return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                // Cannot prove a target exists -> must not apply a strict tendency.
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] a Trainer partner's command target check failed (fail-safe, the strict "
                    + "tendency is NOT applied and the partner keeps a tendency that always acts): "
                    + ex.Message);
                return false;
            }
        }

        private static bool ContainsTile(List<(int, int)> tiles, (int x, int y) position)
        {
            for (int i = 0; i < tiles.Count; i++)
                if (tiles[i].Item1 == position.x && tiles[i].Item2 == position.y) return true;
            return false;
        }

        /// <summary>
        /// The strict tendency's own predicate, copied from <c>AIHelper._orderTargetsByTendency</c>
        /// (<c>AIHelper.cs:665</c>). Anything this method does not explicitly recognise returns false, so
        /// an unrecognised tendency can only ever cause the override to be SKIPPED — never applied
        /// unchecked. Only <c>MUSTHAVELOWHEALTH</c> is listed because FINISH_IT is the only command that
        /// overrides targeting; see <see cref="TryCommandTendency"/> for why AFFLICT does not.
        /// </summary>
        private static bool Qualifies(Entity e, eAiTendencies tendency)
        {
            switch (tendency)
            {
                case eAiTendencies.MUSTHAVELOWHEALTH:
                    int max = CharacterHelper.GetMaxHealth(e);
                    if (max < 1) return false;                     // the engine would divide by zero
                    return (decimal)CharacterHelper.GetHealth(e) / (decimal)max <= 0.2m;
            }
            return false;
        }

        // =====================================================================================
        // fail-safe:  a broken decision PASSES the turn, it never hangs the loop
        // =====================================================================================

        /// <summary>
        /// Finalizer on <c>AIHelper.StandardAiDecision</c> (<c>AIHelper.cs:144</c>) and
        /// <c>AIHelper.BehaviourAiDecision</c> (<c>AIHelper.cs:395</c>).
        ///
        /// <para>For a tracked partner, swallows the exception and substitutes
        /// <c>InteractableHelper.SkipTurnDecision</c> (<c>InteractableHelper.cs:310</c>) — the game's own
        /// pass object, identical to what <c>CombatPhase.cs:1715</c> hands a stunned character, so it
        /// runs a proven code path rather than an invented one. For every other entity the exception is
        /// returned unchanged, which is exactly the unpatched behaviour.</para>
        ///
        /// <para><b>Why this and not a try/catch further out.</b> The decision is computed as an ARGUMENT
        /// at <c>CombatPhase.cs:1922</c> — <c>await _performAiDecision(_activeCharacterEntity,
        /// AIHelper.BehaviourAiDecision(...), engageResults)</c> — inside <c>_engageActiveEntity</c>,
        /// which is <c>async void</c>. A throw there is never observed by the turn loop: the fight simply
        /// stops on that entity. There is no outer frame to catch it in.</para>
        ///
        /// <para><b>Known live hazard this also covers.</b> <c>AIHelper.cs:288</c> dereferences
        /// <c>aiDecision.Ability.AbilityName</c> in a <c>Debug.Log</c> guarded only by
        /// <c>AppConfigManager.Config.RobustLogAI</c>, immediately BEFORE the <c>aiDecision == null</c>
        /// check on the next line. With AI logging on, any decision that ends up null — which
        /// <c>StandardAiDecision</c> reaches on purpose, that is what its SKIP_TURN fallback is for —
        /// throws a NullReferenceException out of the helper. That is a vanilla defect on every AI
        /// entity; here it is neutralised for partners.</para>
        /// </summary>
        public static Exception AiDecision_Finalizer(Entity pActiveEntity,
            ref CombatDecisionData __result, Exception __exception)
        {
            if (__exception == null) return null;
            if (_partners.Count == 0) return __exception;
            try
            {
                if (pActiveEntity == null) return __exception;
                PartnerState state;
                if (!_partners.TryGetValue(pActiveEntity, out state) || state == null) return __exception;

                __result = InteractableHelper.SkipTurnDecision;
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] partner PASSED its turn: ball=" + state.BallConfig
                    + " config=" + state.PartnerConfig
                    + " -- the AI could not produce a decision (" + __exception.GetType().Name + ": "
                    + __exception.Message + "). Combat continues; an idle partner is the fail-safe.");
                return null;
            }
            catch (Exception)
            {
                return __exception;                                // never make the original worse
            }
        }

        // =====================================================================================
        // proof that a partner ACTED  (the testable surface)
        // =====================================================================================

        /// <summary>
        /// Prefix on <c>CombatPhase._performAiDecision</c> (<c>CombatPhase.cs:1449</c>) — the method the
        /// AI branch at <c>CombatPhase.cs:1922</c> calls and the PLAYER branch at <c>:1925</c> never
        /// does. Recording here therefore proves the partner acted <b>by AI</b>, not that something
        /// happened to it: a human-driven turn cannot reach this method.
        ///
        /// <para>Read-only. It records and logs, changes no argument, and cannot alter the turn.</para>
        /// </summary>
        public static void PerformAiDecision_Prefix(Entity pCharacter, CombatDecisionData pDecision)
        {
            if (_partners.Count == 0) return;
            try
            {
                if (pCharacter == null || pDecision == null) return;
                PartnerState state;
                if (!_partners.TryGetValue(pCharacter, out state) || state == null) return;

                string ability = pDecision.Ability.AbilityName;
                state.LastAbility = string.IsNullOrEmpty(ability) ? "(none)" : ability;
                state.LastTargetX = pDecision.Position.x;
                state.LastTargetY = pDecision.Position.y;
                state.Acts++;

                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] partner ACTED (AI): ball=" + state.BallConfig
                    + " config=" + state.PartnerConfig
                    + " line=" + (state.Line ?? "?")
                    + " ability=" + state.LastAbility
                    + " target=(" + state.LastTargetX + "," + state.LastTargetY + ")"
                    + " acts=" + state.Acts);
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] a Trainer partner's action could not be recorded (fail-safe, the turn "
                    + "itself is unaffected): " + ex.Message);
            }
        }

        /// <summary>
        /// One autonomous partner and the last thing it chose to do.
        /// <c>crucible_get</c> renders an object as <c>TypeName { field=val, ... }</c>, so every member
        /// here is a plain public field — same shape as
        /// <c>TrainerPartnerPersistence.PartnerRecord</c>.
        /// </summary>
        public sealed class PartnerActionRecord
        {
            /// <summary>The ball this partner came out of, e.g. ARM_ORIG_TRAINER_BALL_GRASS.</summary>
            public string Ball;
            /// <summary>GRASS / WATER / FIRE.</summary>
            public string Line;
            /// <summary>The creature config actually summoned.</summary>
            public string Config;
            /// <summary>The ability chosen on its most recent AI turn, or null if it has not acted yet.</summary>
            public string LastAbility;
            /// <summary>Target tile of that ability.</summary>
            public int LastTargetX;
            /// <summary>Target tile of that ability.</summary>
            public int LastTargetY;
            /// <summary>How many AI turns this partner has taken this combat.</summary>
            public int Acts;
            /// <summary>The team-command marker that last steered this partner's targeting, or null.
            /// Suffixed "(no qualifying target)" when the command was seen but deliberately NOT applied
            /// because a strict tendency would have risked a skipped turn.</summary>
            public string LastCommand;
            /// <summary>The tendency last substituted into GetPreferredTarget for this partner.</summary>
            public string LastTendency;
            /// <summary>Whether the entity currently carries an AIComponent (i.e. is still AI-driven).</summary>
            public bool AiDriven;

            public override string ToString()
            {
                return Ball + " line=" + (Line ?? "?") + " cfg=" + (Config ?? "?")
                       + " lastAbility=" + (LastAbility ?? "(none yet)")
                       + " target=(" + LastTargetX + "," + LastTargetY + ")"
                       + " acts=" + Acts.ToString(CultureInfo.InvariantCulture)
                       + " command=" + (LastCommand ?? "-")
                       + " tendency=" + (LastTendency ?? "-")
                       + " aiDriven=" + AiDriven;
            }
        }

        /// <summary>
        /// Every partner currently on the board, with the last ability it chose.
        /// <para><b>Test path:</b> <c>crucible_get TrainerPartnerAutonomy.Actions</c>.</para>
        /// </summary>
        public static List<PartnerActionRecord> Actions
        {
            get
            {
                var list = new List<PartnerActionRecord>();
                try
                {
                    foreach (var kv in _partners)
                    {
                        var s = kv.Value;
                        if (s == null) continue;
                        bool ai = false;
                        try { ai = kv.Key != null && kv.Key.Has<AIComponent>(); } catch (Exception) { }
                        list.Add(new PartnerActionRecord
                        {
                            Ball = s.BallConfig,
                            Line = s.Line,
                            Config = s.PartnerConfig,
                            LastAbility = s.LastAbility,
                            LastTargetX = s.LastTargetX,
                            LastTargetY = s.LastTargetY,
                            Acts = s.Acts,
                            LastCommand = s.LastCommand,
                            LastTendency = s.LastTendency,
                            AiDriven = ai,
                        });
                    }
                }
                catch (Exception ex)
                {
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] partner action records could not be read back: " + ex.Message);
                }
                return list;
            }
        }

        /// <summary>
        /// The same data as one flat string, for a substring assertion.
        ///
        /// <para><b>Test path:</b> <c>crucible_get TrainerPartnerAutonomy.ActionSummary</c>. Renders as
        /// <c>partners=1 acted=1 | ARM_ORIG_TRAINER_BALL_GRASS line=GRASS cfg=PLANT_SWAMP_03
        /// lastAbility=BASIC_ATTACK target=(3,1) acts=2 aiDriven=True</c>, or
        /// <c>partners=0 acted=0</c> when nothing is on the board.</para>
        /// </summary>
        public static string ActionSummary
        {
            get
            {
                var records = Actions;
                int acted = 0;
                for (int i = 0; i < records.Count; i++) if (records[i].Acts > 0) acted++;

                var sb = new StringBuilder();
                sb.Append("partners=").Append(records.Count).Append(" acted=").Append(acted);
                for (int i = 0; i < records.Count; i++)
                    sb.Append(i == 0 ? " | " : " ; ").Append(records[i].ToString());
                return sb.ToString();
            }
        }
    }
}

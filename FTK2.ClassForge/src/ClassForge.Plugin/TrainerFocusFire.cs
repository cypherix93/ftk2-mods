using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;

namespace ClassForge.Plugin
{
    /// <summary>
    /// FOCUS FIRE — the Trainer's "all partners attack THIS one" command (test-checklist §L1, command
    /// 1 of 4).
    ///
    /// <para><b>The primitive, verified.</b> <c>AIComponent.PriorityTargets</c> is a
    /// <c>Queue&lt;string&gt;</c> of entity GUIDs (<c>AIComponent.cs:7</c>). Inside
    /// <c>AIHelper.ForceAiDecision</c>, immediately AFTER the targetable-tile list is built and BEFORE
    /// <c>GetPreferredTarget</c> is consulted, the queue is drained in a <c>while</c> loop —
    /// <c>AIHelper.cs:516-537</c>: <c>if (pActiveEntity.TryGet&lt;AIComponent&gt;(out var pComponent))</c>,
    /// <c>while (pComponent.PriorityTargets.Count &gt; 0)</c>, dequeue a GUID, find that entity in
    /// <c>pGameRun.CombatState.Entities</c>, and accept its tile <b>only if that tile is in the
    /// ability's own targetable list</b>. The accepted tile is assigned to the decision's position and the
    /// loop <c>break</c>s; <c>GetPreferredTarget</c> is then skipped entirely because the position is no
    /// longer <c>HexHelper.NULL_HEX</c> (<c>AIHelper.cs:539-551</c>). <b>Unconditional and
    /// PRW-independent</b> — there is no chance gate anywhere on this path, unlike the tendency path at
    /// <c>AIHelper.cs:589-597</c>. A GUID whose tile is not targetable is simply discarded and the next
    /// one tried, so an impossible order degrades to normal targeting rather than to a lost turn.</para>
    ///
    /// <para><b>Where it is issued from.</b> <c>CombatHookPatches.PerformAbility_Postfix</c>, after the
    /// recipe plans have executed — so the pack's own <c>SKILL_CF_TRAINER_CMD_FOCUS_FIRE</c> recipe has
    /// already stamped <c>STATUS_ARMORDOWN_00</c>/<c>STATUS_RESISTANCEDOWN_00</c> on the named enemy and
    /// <c>STATUS_CHARGE_CF_FOCUS_FIRE</c> on every ally by the time this runs. The recipe supplies the
    /// INCENTIVE; this supplies the thing data cannot do, which is the actual selection.</para>
    ///
    /// <para><b>Scoping — two ANDed gates, both keyed to content only this pack ships.</b> The acting
    /// <c>Thing</c> must be <c>ARM_ORIG_STARTER_TRAINER_BEAST_WHISTLE</c> (<c>items.json:216</c>, granted
    /// only by <c>CF_ORIG_TRAINER.Things</c>, <c>classes.json:293</c>) AND the resolved ability id must be
    /// <c>ONLY_RESISTDOWN_ATTACK</c> — the same pair the recipe discriminates on, minus the roll-stat
    /// indirection it needed because JSON cannot read a Thing id. A character of any other class can
    /// satisfy neither, so no non-Trainer ability can reach a single write here.</para>
    ///
    /// <para><b>Why the marker filter is a knob and not the default.</b>
    /// <see cref="RequireMarker"/> narrows the ordered set to allies actually carrying
    /// <c>STATUS_CHARGE_CF_FOCUS_FIRE</c>, which is strictly better scoping — it means only units the
    /// command genuinely reached are ordered, and a bee follower or another player's summon that happens
    /// to share the group is left alone. It ships <b>false</b> anyway, because it makes the feature
    /// depend on an <c>ADD_STATUS</c> whose landing could not be observed in-game in this workstream (the
    /// game was not to be launched): if the recipe engine is off, or the status did not stick, a
    /// <c>true</c> default would silently order nobody. Flipping it to <c>true</c> is the recommended
    /// long-term filter once a live run has confirmed the marker lands.</para>
    ///
    /// <para><b>Multiplayer: the draw-count consequence is now HANDLED; the rest is still UNVERIFIED.</b></para>
    ///
    /// <para><b>Handled (2026-08-26).</b> An order in <c>PriorityTargets</c> makes
    /// <c>AIHelper.ForceAiDecision</c> skip <c>GetPreferredTarget</c> entirely (<c>AIHelper.cs:516-551</c>),
    /// and with it the PRW chance draw at <c>AIHelper.cs:589</c> — so before, issuing an order silently
    /// changed how many values the ordered ally pulled off the shared stream. It did so on every peer at
    /// once, so it was not a live desync, but it made draw count a function of a mod-issued command, which
    /// is the class of dependency that turns any future asymmetry into a permanent one.
    /// <see cref="AiDrawNeutrality"/> now takes the skipped draw in a <c>ForceAiDecision</c> postfix, so an
    /// ordered ally and an unordered ally cost the shared stream exactly the same. See that type's remarks
    /// and the exhaustive draw-count tests in <c>ClassForge.Core.Rng.AiTargetingDraws</c>.</para>
    ///
    /// <para><b>The GUID in the queue is fine, and is the one place a GUID still is.</b>
    /// <c>Entity.Guid</c> is minted per peer by <c>Guid.NewGuid()</c> and is illegal as a cross-peer key
    /// nearly everywhere — but this one never crosses. It is enqueued and drained inside the same peer
    /// (<c>AIHelper.cs:524</c> resolves it against that peer's own <c>CombatState.Entities</c>), and
    /// <c>AIComponent</c> is not part of the <c>GameRunData</c> graph the vendor's desync MD5 covers
    /// (<c>CombatState</c> is <c>[JsonIgnore]</c> on <c>GameRunData</c>). Both peers enqueue the guid of
    /// the SAME logical entity from the same replicated decision, so both resolve to the same combatant
    /// despite holding different strings.</para>
    ///
    /// <para><b>Still unverified.</b> The write is a pure function
    /// of the replicated ability decision (acting Thing, resolved ability id, target entity), takes no
    /// <c>GameRandom</c> draw of any kind, and touches only <c>AIComponent</c> — a combat-scoped component
    /// that is not part of the <c>GameRunData</c> graph the vendor's desync hash covers
    /// (docs/MULTIPLAYER.md, resolved question 2). So it cannot itself trip a desync detector. What is
    /// NOT established: whether <c>AIHelper</c> decision-making runs on both peers or host-only — that is
    /// docs/MULTIPLAYER.md open question #1, still open — and therefore whether both peers enqueue and
    /// drain identically, or whether only the host's queue is ever read. If AI is host-authoritative the
    /// order is issued once and replicated with the resulting action, which is correct; if AI runs
    /// per-peer, both peers enqueue the same single GUID from the same replicated inputs, which is also
    /// correct. The failure mode this cannot rule out is a peer that never sees the ability postfix at
    /// all. <b>This has not been observed in a live 2-peer session.</b></para>
    /// </summary>
    public static class TrainerFocusFire
    {
        /// <summary>The acting Thing gate. Only <c>CF_ORIG_TRAINER</c> grants this item.</summary>
        public const string WhistleConfig = "ARM_ORIG_STARTER_TRAINER_BEAST_WHISTLE";

        /// <summary>The ability gate — the whistle's AWR-stat slot, the one the pack authors as the
        /// Focus Fire command.</summary>
        public const string CommandAbility = "ONLY_RESISTDOWN_ATTACK";

        /// <summary>The ally marker the pack's own recipe stamps, used as an optional extra filter.</summary>
        public const string Marker = "STATUS_CHARGE_CF_FOCUS_FIRE";

        internal static ConfigEntry<bool> Enable;
        internal static ConfigEntry<bool> RequireMarker;

        internal static void Bind(ConfigFile config)
        {
            Enable = CFConfig.Bind(config, "Trainer", "EnableFocusFireOrders", true,
                "Pokemon Trainer FOCUS FIRE (test-checklist L1, command 1 of 4): when the Trainer uses "
                + "the Beast Whistle's ONLY_RESISTDOWN_ATTACK on an enemy, every AI-driven ally in the "
                + "Trainer's own group is given that enemy as a standing order via "
                + "AIComponent.PriorityTargets, the GUID queue AIHelper.cs:516-537 drains BEFORE "
                + "GetPreferredTarget and without any PRW chance gate. The pack's recipe already supplies "
                + "the incentive (ARMORDOWN on the target, a damage bonus for hitting it); no recipe "
                + "effect can enqueue a GUID, which is why this is C#. Gated on the acting Thing being "
                + "the Beast Whistle AND the ability being ONLY_RESISTDOWN_ATTACK, so no other class can "
                + "reach it. Off = partners target by tendency only, exactly as before. "
                + "GAMEPLAY-RELEVANT: multiplayer peers must agree on this value.", ClassForge.Core.ParityClass.Gameplay);

            RequireMarker = CFConfig.Bind(config, "Trainer", "FocusFireRequireMarker", false,
                "Narrow the Focus Fire order to allies actually carrying STATUS_CHARGE_CF_FOCUS_FIRE, the "
                + "marker SKILL_CF_TRAINER_CMD_FOCUS_FIRE stamps on ALLY_ALL. Better scoping -- only "
                + "units the command really reached are ordered -- but it makes the feature depend on "
                + "that ADD_STATUS having landed, which has NOT been confirmed in a live game. Ships "
                + "false so the order works off the ability alone; flip to true once a run has confirmed "
                + "the marker lands, and it becomes the safer long-term filter.", ClassForge.Core.ParityClass.Gameplay);
        }

        private static bool Active
        {
            get { return ClassForgePlugin.FeaturesActive && Enable != null && Enable.Value; }
        }

        // =====================================================================================
        // the order
        // =====================================================================================

        /// <summary>
        /// Issues the standing order. Called from the tail of
        /// <c>CombatHookPatches.PerformAbility_Postfix</c>, after
        /// <c>RecipeActionExecutor.Execute(plan, exec)</c>.
        ///
        /// <para><b>One standing order at a time.</b> <c>PriorityTargets.Clear()</c> then a single
        /// <c>Enqueue</c>, so a second Focus Fire replaces the first rather than stacking a queue that
        /// would be drained over later turns — the command is authored as a one-round order
        /// (<c>Budget.Scope: ONCE_PER_ROUND</c>, and the marker status has <c>Duration: 2</c>).</para>
        ///
        /// <para><b>It refuses to order onto an ALLY.</b> <c>GroupIndex</c>
        /// (<c>CharacterComponent.cs:35</c>) is compared directly: a target in the Trainer's own group is
        /// logged and dropped. Nothing in the queue-drain loop at <c>AIHelper.cs:516-537</c> checks sides —
        /// it accepts any GUID whose tile is targetable — so without this guard a mis-aimed command could
        /// point the whole team at a party member.</para>
        /// </summary>
        internal static void Issue(Entity origin, Entity target, Thing actingThing, string abilityId)
        {
            if (!Active) return;
            try
            {
                // ---- gate 1: the acting Thing is the Trainer's Beast Whistle.
                if (actingThing == null
                    || !string.Equals(actingThing.ConfigName, WhistleConfig, StringComparison.Ordinal)) return;

                // ---- gate 2: the resolved ability is the Focus Fire slot.
                if (!string.Equals(abilityId, CommandAbility, StringComparison.Ordinal)) return;

                if (origin == null || target == null) return;

                CharacterComponent originCc;
                if (!origin.TryGet<CharacterComponent>(out originCc) || originCc == null) return;

                CharacterComponent targetCc;
                if (!target.TryGet<CharacterComponent>(out targetCc) || targetCc == null) return;

                // ---- never order the team onto its own side.
                if (targetCc.GroupIndex == originCc.GroupIndex)
                {
                    ClassForgePlugin.Log.LogDebug(
                        "[ClassForge] focus fire REFUSED: the named target is in the Trainer's own group "
                        + "(" + originCc.GroupIndex + "), so no order was issued.");
                    return;
                }

                string targetGuid;
                try { targetGuid = target.Guid; } catch (Exception) { return; }
                if (string.IsNullOrEmpty(targetGuid)) return;

                var entities = CombatEntities();
                if (entities == null) return;

                bool requireMarker = RequireMarker != null && RequireMarker.Value;
                int ordered = 0, skippedNoAi = 0;

                for (int i = 0; i < entities.Count; i++)
                {
                    var e = entities[i];
                    if (e == null) continue;
                    if (ReferenceEquals(e, origin)) continue;         // the Trainer never orders itself

                    CharacterComponent cc;
                    if (!e.TryGet<CharacterComponent>(out cc) || cc == null) continue;
                    if (cc.GroupIndex != originCc.GroupIndex) continue;   // own group only
                    if (CharacterHelper.IsDead(e)) continue;              // CharacterHelper.cs:1472

                    AIComponent ai;
                    if (!e.TryGet<AIComponent>(out ai) || ai == null) { skippedNoAi++; continue; }

                    if (requireMarker && !HasMarker(e)) continue;

                    // PriorityTargets is a plain field on a plain component (AIComponent.cs:7); a summon
                    // built by CharacterHelper.CreateCharacterEntity gets the component but the queue can
                    // legitimately be null until something fills it.
                    if (ai.PriorityTargets == null) ai.PriorityTargets = new Queue<string>();
                    ai.PriorityTargets.Clear();
                    ai.PriorityTargets.Enqueue(targetGuid);
                    ordered++;
                }

                _lastTargetGuid = targetGuid;
                _lastTargetName = SafeName(targetCc);
                _lastOrderedCount = ordered;
                _orders++;

                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] focus fire ORDERED: target=" + _lastTargetName + " guid=" + targetGuid
                    + " group=" + targetCc.GroupIndex
                    + " ordered=" + ordered + " allies (skippedNoAI=" + skippedNoAi + ")"
                    + (requireMarker ? " markerFilter=ON" : " markerFilter=off")
                    + " orders=" + _orders
                    + " -- AIComponent.PriorityTargets drains before GetPreferredTarget (AIHelper.cs:516-537).");
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] a Trainer focus-fire order could not be issued (fail-safe, partners "
                    + "target by tendency as before): " + ex.Message);
            }
        }

        /// <summary>
        /// The entity list the drain loop itself searches — <c>pGameRun.CombatState.Entities</c>
        /// (<c>AIHelper.cs:525</c>). Using the same list means an entity we order is by construction one
        /// the loop can resolve.
        /// </summary>
        private static List<Entity> CombatEntities()
        {
            try
            {
                var run = RouterHelper.Env != null ? RouterHelper.Env.GameRun : null;
                var combat = run != null ? run.CombatState : null;
                return combat != null ? combat.Entities : null;
            }
            catch (Exception) { return null; }
        }

        private static bool HasMarker(Entity e)
        {
            try
            {
                StatusEffectComponent comp;
                if (!e.TryGet<StatusEffectComponent>(out comp) || comp == null || comp.Statuses == null)
                    return false;
                return comp.Statuses.ContainsKey(Marker);
            }
            catch (Exception) { return false; }
        }

        private static string SafeName(CharacterComponent cc)
        {
            try { return cc.DisplayName ?? cc.ConfigName ?? "?"; } catch (Exception) { return "?"; }
        }

        // =====================================================================================
        // read-back for tests
        // =====================================================================================

        private static string _lastTargetGuid = "";
        private static string _lastTargetName = "";
        private static int _lastOrderedCount;
        private static int _orders;

        /// <summary>Guid of the enemy the most recent Focus Fire named, or "" if none has been issued.</summary>
        public static string LastTargetGuid { get { return _lastTargetGuid; } }

        /// <summary>How many allies received the most recent order. The value an order test asserts on.</summary>
        public static int LastOrderedCount { get { return _lastOrderedCount; } }

        /// <summary>How many Focus Fire orders have been issued this session.</summary>
        public static int Orders { get { return _orders; } }

        /// <summary>
        /// The live queue state, recomputed from every AI entity on the board on each read — so a test
        /// sees what the engine will actually drain, not what we intended to write.
        /// <para><b>Test path:</b> <c>crucible_get TrainerFocusFire.OrderSummary</c>. Renders as
        /// <c>orders=1 lastTarget=Bat/GUID ordered=2 | Sporeling queued=1 next=GUID ; ...</c>, or
        /// <c>orders=0 lastTarget=- ordered=0</c> before any command.</para>
        /// </summary>
        public static string OrderSummary
        {
            get
            {
                var sb = new StringBuilder();
                sb.Append("orders=").Append(_orders)
                  .Append(" lastTarget=").Append(string.IsNullOrEmpty(_lastTargetName) ? "-" : _lastTargetName)
                  .Append("/").Append(string.IsNullOrEmpty(_lastTargetGuid) ? "-" : _lastTargetGuid)
                  .Append(" ordered=").Append(_lastOrderedCount);
                try
                {
                    var entities = CombatEntities();
                    if (entities == null) return sb.ToString();
                    bool first = true;
                    for (int i = 0; i < entities.Count; i++)
                    {
                        var e = entities[i];
                        if (e == null) continue;
                        AIComponent ai;
                        if (!e.TryGet<AIComponent>(out ai) || ai == null || ai.PriorityTargets == null) continue;
                        if (ai.PriorityTargets.Count == 0) continue;
                        CharacterComponent cc;
                        string name = e.TryGet<CharacterComponent>(out cc) && cc != null ? SafeName(cc) : "?";
                        sb.Append(first ? " | " : " ; ").Append(name)
                          .Append(" queued=").Append(ai.PriorityTargets.Count)
                          .Append(" next=").Append(ai.PriorityTargets.Peek());
                        first = false;
                    }
                }
                catch (Exception ex)
                {
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] focus-fire order summary could not be read back: " + ex.Message);
                }
                return sb.ToString();
            }
        }

        /// <summary>Drops the session counters. Called from the combat reset so a test asserting
        /// <c>orders=0</c> at the start of a fight sees a clean slate.</summary>
        internal static void Clear()
        {
            _lastTargetGuid = "";
            _lastTargetName = "";
            _lastOrderedCount = 0;
            _orders = 0;
        }
    }
}

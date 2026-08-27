using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;

namespace ClassForge.Plugin
{
    /// <summary>
    /// The Pokemon Trainer's CHARM PROGRESSION — the level-1 starter CHOICE and the per-level point
    /// spend that unlocks the second and third partner lines (test-checklist §L / §L1 / §L2).
    ///
    /// <para><b>Why this has to exist in C# at all.</b> The class config grants exactly ONE charm —
    /// <c>CF_ORIG_TRAINER.Things</c> lists <c>ARM_ORIG_TRAINER_BALL_GRASS: 1</c>
    /// (<c>classes.json:292-297</c>) — and all twelve <c>ON_COMBAT_START</c> partner-summon recipes are
    /// gated on <c>HAS_ITEM</c> of their OWN charm (<c>skillrecipes.json</c>, twelve
    /// <c>ARM_ORIG_TRAINER_BALL_*</c> values). <b>Possession IS the switch.</b> So the WATER and FIRE
    /// lines — two thirds of the class's authored content — are unreachable for the whole run unless
    /// something puts those two <c>Thing</c>s into <c>CharacterComponent.Things</c>
    /// (<c>CharacterComponent.cs:38</c>). No recipe effect grants an item, and the data layer has no
    /// level-up trigger, so this is the piece that cannot be authored as JSON.</para>
    ///
    /// <para><b>Where level comes from.</b> <c>ProgressionHelper.GetEntityLevel</c>
    /// (<c>ProgressionHelper.cs:565</c>) — XP-derived, counting the <c>"XP"</c> <c>Thing</c>'s
    /// <c>StackCount</c> (<c>ProgressionHelper.cs:555-558</c>) against the level table. That is the same
    /// source the pack's own <c>SELF_LEVEL</c> condition reads, so a charm granted here and a stage
    /// gated in JSON can never disagree about what level the Trainer is.</para>
    ///
    /// <para><b>Where level-up is SIGNALLED.</b> <c>ProgressionHelper.EntityGainXP</c>
    /// (<c>ProgressionHelper.cs:648</c>, the single overload; <c>EntitiesGainXP</c> at <c>:640</c> is a
    /// <c>ForEach</c> over it) captures <c>GetEntityLevel</c> before the XP is added (<c>:654</c>),
    /// re-reads it after (<c>:703</c>), and on <c>entityLevel2 &gt; entityLevel</c> emits
    /// <c>pResults?.Add((eAbilityResults.LEVELED_UP, pEntity.Guid))</c> at <c>ProgressionHelper.cs:721</c>.
    /// <b>That is the only place in the engine a player level-up is announced.</b> A postfix on
    /// <c>EntityGainXP</c> therefore runs on exactly the frame the level changed, for exactly the entity
    /// that changed, and is where <see cref="EntityGainXP_Postfix"/> hangs.</para>
    ///
    /// <para><b>But the grant is RECONCILED, not edge-triggered.</b> <see cref="Reconcile"/> is a pure
    /// function of (current level, config): it computes the set of charms the Trainer should be holding
    /// and adds whichever are missing. It never removes an earned charm and never double-grants, so
    /// running it twice is identical to running it once. That matters for three reasons the edge-trigger
    /// cannot cover: a save loaded at level 5 has no pending level-up event to catch; a level granted by
    /// a debug/console path may never route through <c>EntityGainXP</c>; and in multiplayer both peers
    /// converge on the same set from the same replicated XP without having to observe the same event.
    /// It is therefore ALSO called from a prefix on <c>CombatHelper.SetInitiative</c> — deliberately a
    /// PREFIX, because <c>CombatHookPatches.SetInitiative_Postfix</c> is what dispatches
    /// <c>ON_COMBAT_START</c> and evaluates those twelve <c>HAS_ITEM</c> gates; reconciling in the prefix
    /// guarantees the charm is in the inventory before the gate reads it.</para>
    ///
    /// <para><b>The starter CHOICE is a CONFIG FALLBACK, not an in-game picker. Stated plainly.</b>
    /// <see cref="StarterLine"/> is a <c>[Trainer]</c> knob; at first sight of an un-chosen Trainer the
    /// shipped GRASS charm is SWAPPED for the configured line. The game does own a general n-way choice
    /// menu — <c>ChoiceMenuViewHelper.ShowMenu(VisualElement pCanvasRoot, string pTitle,
    /// List&lt;ChoiceButtonData&gt;, EventCallback, bool pIsOnlineViewOnly)</c>
    /// (<c>ChoiceMenuViewHelper.cs:50</c>), used by <c>AdventureDirector.cs:8754/10580/10743</c>,
    /// <c>RestPhase.cs:1708</c> and <c>WheelPhase.cs:748</c> — so a real picker is not impossible. What
    /// it would need, and what is NOT delivered here: a canvas root (every caller passes its director's
    /// private <c>_canvas2D.rootVisualElement</c>, reachable only by reflection from inside a director
    /// instance), a verified safe moment to raise it that is not mid-phase, a multiplayer story for the
    /// pick (<c>ShowMenu</c>'s <c>pIsOnlineViewOnly</c> flag exists precisely because a remote peer must
    /// see the menu without driving it, and the resulting grant must then agree on both peers), and live
    /// in-game validation of all three — which this workstream could not perform. A picker that cannot be
    /// validated is worse than a knob that can, so the knob is what ships.</para>
    ///
    /// <para><b>What is deliberately NOT done: a silent auto-grant of all three.</b> The second and
    /// third charms arrive at <see cref="SecondCharmLevel"/> and <see cref="ThirdCharmLevel"/>, and
    /// before those levels the Trainer holds exactly one. Handing over all three at once would restore
    /// the very hole the possession gate was added to close.</para>
    ///
    /// <para><b>Scoping — this must not touch any other class, and must not need a charm to see a
    /// Trainer.</b> Two gates, in this order. First <c>PlayerComponent</c> — charms are a PLAYER
    /// progression and <c>GetEntityLevel</c> is only meaningful for one; it is a single
    /// <c>Components.ContainsKey</c> probe and it is what every enemy in every fight fails on, so it goes
    /// first. Then <see cref="IsTrainer"/>: the entity's CLASS is <c>CF_ORIG_TRAINER</c>
    /// (<c>CharacterComponent.ConfigName</c> is where a character's class config id lives —
    /// <c>decomp/CharacterComponent.cs</c> L8, the same field <c>GameAdapters</c> feeds to
    /// <c>GameLookups.CharacterConfig</c> at <c>Recipes/GameAdapters.cs:304</c>, and the same field
    /// <c>Crucible.FixtureCommands.CruciblePartySetClass</c> rewrites to swap a class,
    /// <c>FixtureCommands.cs:150-152</c>), OR it already holds an <c>ARM_ORIG_TRAINER_BALL_*</c> item.
    ///
    /// <para><b>Why the class arm had to be added.</b> Holding a charm was previously the ONLY
    /// discriminator, which is a chicken-and-egg: granting a charm is exactly what this class does, so a
    /// Trainer holding none was never reconciled and could never be granted one. That is not a corner
    /// case — a character that becomes a Trainer by a class SWAP holds nothing, because class-config
    /// <c>Things</c> are not applied retroactively to an existing character
    /// (<c>docs/research/test-checklist.md</c>), and neither does a Trainer that lost its last charm. The
    /// live symptom was <c>CharmSummary</c> reporting <c>trainers=0</c> with a Trainer on the board and no
    /// partner ever summoned.</para>
    ///
    /// <para><b>Why a non-Trainer still cannot enter.</b> Both arms are Trainer-only and neither is
    /// widened by the fix. The class arm is ordinal equality against the one class id
    /// <c>CF_ORIG_TRAINER</c> — the raw <c>ConfigName</c> carries no body-type suffix (the suffix is added
    /// only by <c>CharacterHelper.GetConfigNameWithBodyType</c>, which is why
    /// <c>VisualRemapPatches.cs:93</c> can test the raw field with a plain set membership). The inventory
    /// arm is the pre-existing one, and only <c>CF_ORIG_TRAINER.Things</c>
    /// (<c>CF_PACK_ORIGINALS/classes.json:292-297</c>) ever puts an <c>ARM_ORIG_TRAINER_BALL_*</c> into a
    /// character. The new gate is therefore a strict SUPERSET of the old one over Trainers and identical
    /// to it over everything else.</para>
    /// </summary>
    public static class TrainerCharmProgression
    {
        /// <summary>The three authored partner lines, in the order the pack's charms are named.</summary>
        private static readonly string[] KnownLines = { "GRASS", "WATER", "FIRE" };

        /// <summary>
        /// The Trainer's CLASS config id — the key of the class in <c>CF_PACK_ORIGINALS/classes.json:241</c>,
        /// merged into <c>Env.Configs.Characters</c> by <c>ConfigMergePatches.cs:144</c> and worn by a
        /// character as <c>CharacterComponent.ConfigName</c> (<c>decomp/CharacterComponent.cs</c> L8).
        /// <b>This, not the inventory, is what makes an entity a Trainer.</b>
        /// </summary>
        public const string TrainerClassConfig = "CF_ORIG_TRAINER";

        /// <summary>Which line the player's starter was resolved to, on the Trainer's own
        /// <c>CharacterComponent.CustomData</c> (<c>CoreHelper.SetCustomData(CharacterComponent,…)</c>,
        /// <c>CoreHelper.cs:1659</c>). Its presence is what makes the starter swap happen exactly once.</summary>
        public const string KeyStarter = "CF_TRAINER_STARTER";

        /// <summary>The level this Trainer was last reconciled at — diagnostics only; the grant itself is
        /// recomputed from the live level every time and never trusts this.</summary>
        public const string KeyLevelSeen = "CF_TRAINER_LEVEL_SEEN";

        // =====================================================================================
        // config
        // =====================================================================================

        internal static ConfigEntry<bool> Enable;
        internal static ConfigEntry<string> StarterLine;
        internal static ConfigEntry<string> UnlockOrder;
        internal static ConfigEntry<int> SecondCharmLevel;
        internal static ConfigEntry<int> ThirdCharmLevel;

        internal static void Bind(ConfigFile config)
        {
            Enable = CFConfig.Bind(config, "Trainer", "EnableCharmProgression", true,
                "Pokemon Trainer charm progression (test-checklist L/L1/L2): the level-1 starter CHOICE "
                + "and the per-level unlock of the second and third partner lines. The class config "
                + "grants exactly ONE charm (ARM_ORIG_TRAINER_BALL_GRASS) and all twelve partner-summon "
                + "recipes are gated on HAS_ITEM of their own charm, so without this the Trainer is a "
                + "permanently ONE-PARTNER class and two thirds of its authored content is unreachable. "
                + "Scoped to characters with a PlayerComponent whose CLASS is CF_ORIG_TRAINER (or that "
                + "already hold an ARM_ORIG_TRAINER_BALL_* item, which only that class grants), so no "
                + "other class is reachable either way. Off = vanilla behaviour: "
                + "one charm, the GRASS line only. GAMEPLAY-RELEVANT: every multiplayer peer must use "
                + "the same value for this and for the four knobs below.", ClassForge.Core.ParityClass.Gameplay);

            StarterLine = CFConfig.Bind(config, "Trainer", "StarterCharmLine", "GRASS",
                "STARTER CHOICE (fallback, NOT an in-game picker -- see the class docs on "
                + "TrainerCharmProgression for what a real one would need). GRASS, WATER or FIRE. At "
                + "first sight of a Trainer that has not yet resolved a starter, the shipped GRASS charm "
                + "is swapped for this line's charm. The swap is refused, with a warning, if the GRASS "
                + "charm has already sent a partner out (it carries a CF_POKE_* record) or is equipped -- "
                + "an in-progress run keeps what it has rather than losing a partner. Unrecognised values "
                + "fall back to GRASS.", ClassForge.Core.ParityClass.Gameplay);

            UnlockOrder = CFConfig.Bind(config, "Trainer", "CharmUnlockOrder", "GRASS,WATER,FIRE",
                "The order the remaining two partner lines are unlocked in, comma-separated. The starter "
                + "line is skipped wherever it appears, so with StarterCharmLine=FIRE and the default "
                + "order the second charm is GRASS and the third is WATER. Unknown or duplicate entries "
                + "are ignored; any line the list omits is appended in the pack's own GRASS,WATER,FIRE "
                + "order so all three are always reachable.", ClassForge.Core.ParityClass.Gameplay);

            SecondCharmLevel = CFConfig.Bind(config, "Trainer", "SecondCharmLevel", 3,
                "Level at which the SECOND partner charm is granted (point-spend fallback: the real "
                + "design spends a per-level point in any order, which needs a spend UI this build does "
                + "not have). Clamped to 1..9 -- max player level is 9 (CharacterHelper.cs:208-222). "
                + "Level is ProgressionHelper.GetEntityLevel (ProgressionHelper.cs:565), the same "
                + "XP-derived source the pack's SELF_LEVEL condition reads.", ClassForge.Core.ParityClass.Gameplay);

            ThirdCharmLevel = CFConfig.Bind(config, "Trainer", "ThirdCharmLevel", 6,
                "Level at which the THIRD partner charm is granted. Clamped to 1..9 and never lower than "
                + "SecondCharmLevel. Deliberately not 1: granting all three at once would re-open the "
                + "exact hole the HAS_ITEM possession gate was added to close.", ClassForge.Core.ParityClass.Gameplay);
        }

        private static bool Active
        {
            get { return ClassForgePlugin.FeaturesActive && Enable != null && Enable.Value; }
        }

        // =====================================================================================
        // hooks
        // =====================================================================================

        /// <summary>
        /// Postfix on <c>ProgressionHelper.EntityGainXP(Entity, int, bool,
        /// List&lt;(eAbilityResults,object)&gt;, bool)</c> (<c>ProgressionHelper.cs:648</c>) — the one
        /// method that raises <c>eAbilityResults.LEVELED_UP</c> (<c>:721</c>). Runs on exactly the frame
        /// the level changed, for exactly the entity that changed.
        /// <para>Read-only with respect to XP: it changes no argument and no result. All it can do is add
        /// a charm the reconciler says is owed.</para>
        /// </summary>
        public static void EntityGainXP_Postfix(Entity pEntity)
        {
            Reconcile(pEntity, "level-up");
        }

        /// <summary>
        /// Prefix on <c>CombatHelper.SetInitiative</c> — the same method
        /// <c>CombatHookPatches.SetInitiative_Postfix</c> uses to dispatch <c>ON_COMBAT_START</c>.
        /// <b>Prefix, not postfix, on purpose:</b> the twelve partner-summon recipes are evaluated in that
        /// postfix and every one of them opens with <c>HAS_ITEM</c> of its charm, so the charm has to be
        /// in <c>CharacterComponent.Things</c> before the gate is read. Harmony runs every patcher's
        /// prefixes before the original and postfixes after, so this ordering is structural, not a race.
        /// <para><c>void</c> — it can never skip the original.</para>
        /// </summary>
        public static void SetInitiative_Prefix(Entity pEntity)
        {
            Reconcile(pEntity, "combat-start");
        }

        // =====================================================================================
        // the reconciler
        // =====================================================================================

        /// <summary>
        /// Brings <paramref name="entity"/>'s charm inventory into line with its level. Idempotent: it
        /// only ever ADDS a missing charm (plus the one-time starter swap), so calling it on every combat
        /// start costs a scan of a short list and changes nothing once settled.
        /// </summary>
        internal static void Reconcile(Entity entity, string why)
        {
            if (!Active || entity == null) return;
            try
            {
                CharacterComponent cc;
                if (!entity.TryGet<CharacterComponent>(out cc) || cc == null || cc.Things == null) return;

                // ---- discriminator #1: charms are a PLAYER progression. GetEntityLevel is XP-derived and
                // a companion/summon has no meaningful player level (GetEntityLevelCap even uses a
                // different cap for COMPANION, ProgressionHelper.cs:157-164). One dictionary probe
                // (Entity.Has is a Components.ContainsKey), and it is what every enemy in every fight
                // fails on -- so it goes first, ahead of the inventory scan.
                if (!entity.Has<PlayerComponent>()) return;

                // ---- discriminator #2: this character's CLASS is the Trainer (or it already holds a
                // charm, which only that class grants). Class-first is the whole point: keying off the
                // inventory alone made a charm-less Trainer -- a class SWAPPED onto an existing character,
                // whose class-config Things are never applied retroactively -- permanently invisible to
                // the very code that is supposed to hand it its first charm.
                if (!IsTrainer(cc)) return;

                string starter = ResolveStarter(entity, cc);
                if (string.IsNullOrEmpty(starter)) return;

                int level;
                try { level = ProgressionHelper.GetEntityLevel(entity); }
                catch (Exception) { return; }                 // no XP thing / no table -> leave alone

                var owed = DesiredLines(starter, level);
                int granted = 0;
                for (int i = 0; i < owed.Count; i++)
                {
                    string config = TrainerPartnerPersistence.BallPrefix + owed[i];
                    if (HasCharm(cc, config)) continue;

                    // The same construction ProgressionHelper itself uses for the XP Thing
                    // (ProgressionHelper.cs:664-669): InventoryHelper.CreateThing(configName)
                    // (InventoryHelper.cs:104), then straight into CharacterComponent.Things.
                    var thing = InventoryHelper.CreateThing(config);
                    if (thing == null) continue;
                    cc.Things.Add(thing);
                    granted++;

                    ClassForgePlugin.Log.LogDebug(
                        "[ClassForge] trainer charm GRANTED: " + config + " line=" + owed[i]
                        + " at level=" + level.ToString(CultureInfo.InvariantCulture)
                        + " (" + why + "). Its four ON_COMBAT_START summon recipes now pass HAS_ITEM.");
                }

                CoreHelper.SetCustomData(cc, KeyLevelSeen, level.ToString(CultureInfo.InvariantCulture));

                if (granted > 0)
                    ClassForgePlugin.Log.LogDebug(
                        "[ClassForge] trainer charms: " + CharmSummaryFor(entity, cc, starter, level));
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] the Trainer charm progression could not run (fail-safe, the Trainer "
                    + "keeps whatever charms it already holds): " + ex.Message);
            }
        }

        /// <summary>
        /// The starter line for this Trainer, performing the one-time swap of the shipped GRASS charm the
        /// first time it is asked. Returns the line, or null if nothing could be resolved.
        /// </summary>
        private static string ResolveStarter(Entity entity, CharacterComponent cc)
        {
            string recorded;
            if (CoreHelper.TryGetCustomData(cc, KeyStarter, out recorded)
                && !string.IsNullOrEmpty(recorded) && IsKnownLine(recorded))
                return recorded;

            string wanted = NormalizeLine(StarterLine != null ? StarterLine.Value : null);
            string shipped = TrainerPartnerPersistence.BallPrefix + "GRASS";
            Thing shippedBall = FindCharm(cc, shipped);

            // Nothing to swap FROM (a run that already earned its way to another line, or a hand-built
            // character): whatever charm is present decides, first in pack order.
            if (shippedBall == null)
            {
                string present = FirstHeldLine(cc);
                if (present == null)
                {
                    // A Trainer holding NO charm at all. There is nothing to swap FROM, so the configured
                    // line is simply ADOPTED and the grant loop in Reconcile creates it outright. This is
                    // the case the old inventory-only scoping could never reach: a class swapped onto an
                    // existing character starts with an empty charm inventory, because class-config Things
                    // are not applied retroactively (docs/research/test-checklist.md).
                    CoreHelper.SetCustomData(cc, KeyStarter, wanted);
                    ClassForgePlugin.Log.LogDebug(
                        "[ClassForge] trainer starter ADOPTED: " + wanted + " -- this "
                        + TrainerClassConfig + " holds no " + TrainerPartnerPersistence.BallPrefix
                        + "* charm at all (class swapped in, or the charm was lost), so the configured "
                        + "starter is granted outright rather than swapped.");
                    return wanted;
                }
                CoreHelper.SetCustomData(cc, KeyStarter, present);
                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] trainer starter RESOLVED to the charm already held: " + present
                    + " (no ARM_ORIG_TRAINER_BALL_GRASS to swap).");
                return present;
            }

            if (string.Equals(wanted, "GRASS", StringComparison.Ordinal))
            {
                CoreHelper.SetCustomData(cc, KeyStarter, "GRASS");
                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] trainer starter CHOSEN: GRASS (config default; the shipped charm "
                    + "stands, no swap).");
                return "GRASS";
            }

            // Refuse the swap if the grass ball is already carrying a partner record -- that ball IS a
            // partner (TrainerPartnerPersistence writes CF_POKE_MAXHP on the first send-out), and
            // deleting it would delete a partner mid-run.
            string used;
            bool inUse = CoreHelper.TryGetCustomData(shippedBall, TrainerPartnerPersistence.KeyMaxHp, out used);
            bool equipped = IsEquipped(cc, shipped);
            if (inUse || equipped)
            {
                CoreHelper.SetCustomData(cc, KeyStarter, "GRASS");
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] trainer starter swap to " + wanted + " REFUSED: the GRASS charm is "
                    + (inUse ? "already bound to a partner" : "equipped")
                    + ", so this run keeps GRASS as its starter. Set StarterCharmLine before starting a "
                    + "Trainer, not during a run.");
                return "GRASS";
            }

            cc.Things.Remove(shippedBall);
            var replacement = InventoryHelper.CreateThing(TrainerPartnerPersistence.BallPrefix + wanted);
            if (replacement == null)
            {
                // Put it back rather than leave the Trainer with no charm at all.
                cc.Things.Add(shippedBall);
                CoreHelper.SetCustomData(cc, KeyStarter, "GRASS");
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] trainer starter swap to " + wanted + " failed to build the replacement "
                    + "charm (fail-safe, GRASS restored).");
                return "GRASS";
            }
            cc.Things.Add(replacement);
            CoreHelper.SetCustomData(cc, KeyStarter, wanted);
            ClassForgePlugin.Log.LogDebug(
                "[ClassForge] trainer starter CHOSEN: " + wanted + " -- swapped " + shipped + " for "
                + replacement.ConfigName + " (config fallback, not an in-game picker).");
            return wanted;
        }

        /// <summary>
        /// Starter first, then the configured unlock order, truncated to what the level pays for: one
        /// charm below <see cref="SecondCharmLevel"/>, two below <see cref="ThirdCharmLevel"/>, three at
        /// or above it.
        /// </summary>
        private static List<string> DesiredLines(string starter, int level)
        {
            var order = new List<string> { starter };
            var configured = ParseUnlockOrder();
            for (int i = 0; i < configured.Count; i++)
            {
                string line = configured[i];
                if (!string.Equals(line, starter, StringComparison.Ordinal) && !order.Contains(line))
                    order.Add(line);
            }

            int second = Clamp(SecondCharmLevel != null ? SecondCharmLevel.Value : 3, 1, 9);
            int third = Clamp(ThirdCharmLevel != null ? ThirdCharmLevel.Value : 6, 1, 9);
            if (third < second) third = second;

            int count = 1;
            if (level >= second) count = 2;
            if (level >= third) count = 3;
            if (count > order.Count) count = order.Count;
            return order.GetRange(0, count);
        }

        private static List<string> ParseUnlockOrder()
        {
            var result = new List<string>();
            try
            {
                string raw = UnlockOrder != null ? UnlockOrder.Value : null;
                if (!string.IsNullOrEmpty(raw))
                {
                    var parts = raw.Split(',');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        string line = NormalizeLine(parts[i]);
                        if (IsKnownLine(line) && !result.Contains(line)) result.Add(line);
                    }
                }
            }
            catch (Exception) { }
            // Anything the knob omitted is appended in pack order, so all three stay reachable.
            for (int i = 0; i < KnownLines.Length; i++)
                if (!result.Contains(KnownLines[i])) result.Add(KnownLines[i]);
            return result;
        }

        // =====================================================================================
        // helpers
        // =====================================================================================

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        private static string NormalizeLine(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "GRASS";
            string line = raw.Trim().ToUpperInvariant();
            return IsKnownLine(line) ? line : "GRASS";
        }

        private static bool IsKnownLine(string line)
        {
            for (int i = 0; i < KnownLines.Length; i++)
                if (string.Equals(KnownLines[i], line, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// The one scoping test, used identically by <see cref="Reconcile"/> and by <see cref="Records"/>
        /// so the read-back reports every Trainer the reconciler acts on — including one whose grant was a
        /// no-op, and one that holds nothing at all.
        /// <para><b>Class first.</b> <c>CharacterComponent.ConfigName</c> IS the class config id
        /// (<c>decomp/CharacterComponent.cs</c> L8; <c>Recipes/GameAdapters.cs:304</c> feeds this exact
        /// field to <c>GameLookups.CharacterConfig</c>, and <c>Crucible.FixtureCommands.cs:150-152</c>
        /// swaps a class by writing it), and it carries no body-type suffix — that is appended only by
        /// <c>CharacterHelper.GetConfigNameWithBodyType</c>, which is why <c>VisualRemapPatches.cs:93</c>
        /// tests the raw field by plain membership. So ordinal equality against the single id
        /// <c>CF_ORIG_TRAINER</c> admits every Trainer and nothing else.</para>
        /// <para><b>Then the legacy inventory arm, kept.</b> Only <c>CF_ORIG_TRAINER.Things</c>
        /// (<c>CF_PACK_ORIGINALS/classes.json:292-297</c>) ever grants an
        /// <c>ARM_ORIG_TRAINER_BALL_*</c>, so a charm-holder is a Trainer even if some future path renames
        /// the class. Keeping it makes this gate a strict superset of the old one over Trainers and
        /// identical to it over every other class — no non-Trainer becomes reachable.</para>
        /// </summary>
        private static bool IsTrainer(CharacterComponent cc)
        {
            if (string.Equals(cc.ConfigName, TrainerClassConfig, StringComparison.Ordinal)) return true;
            return HoldsAnyCharm(cc);
        }

        /// <summary>
        /// The legacy inventory arm of <see cref="IsTrainer"/>: does this character hold one of ASH's three
        /// ELEMENT charms?
        ///
        /// <para><b>The CAPTURE ball is deliberately excluded.</b> It shares the
        /// <c>ARM_ORIG_TRAINER_BALL_</c> prefix — that prefix IS the binding into
        /// <see cref="TrainerPartnerPersistence"/>, and Gary reuses it on purpose — but it belongs to
        /// <c>CF_ORIG_GARY</c>, not to <c>CF_ORIG_TRAINER</c>. A prefix test made Gary answer "yes, a
        /// Trainer" here, so the reconciler ADOPTED a starter line for him and granted him
        /// <c>ARM_ORIG_TRAINER_BALL_GRASS</c> (and, at level, WATER/FIRE) — Ash's partners, on the class
        /// whose entire design is ONE captured minion. Matching the three <see cref="KnownLines"/> exactly
        /// keeps this gate identical over every real Trainer (they can hold nothing else) while it stops
        /// admitting Gary.</para>
        /// </summary>
        private static bool HoldsAnyCharm(CharacterComponent cc)
        {
            for (int i = 0; i < KnownLines.Length; i++)
                if (HasCharm(cc, TrainerPartnerPersistence.BallPrefix + KnownLines[i])) return true;
            return false;
        }

        private static string FirstHeldLine(CharacterComponent cc)
        {
            for (int i = 0; i < KnownLines.Length; i++)
                if (HasCharm(cc, TrainerPartnerPersistence.BallPrefix + KnownLines[i])) return KnownLines[i];
            return null;
        }

        private static bool HasCharm(CharacterComponent cc, string config)
        {
            return FindCharm(cc, config) != null;
        }

        private static Thing FindCharm(CharacterComponent cc, string config)
        {
            for (int i = 0; i < cc.Things.Count; i++)
            {
                var t = cc.Things[i];
                if (t != null && string.Equals(t.ConfigName, config, StringComparison.Ordinal)) return t;
            }
            return null;
        }

        private static bool IsEquipped(CharacterComponent cc, string config)
        {
            try
            {
                if (cc.Equipped == null) return false;
                foreach (var kv in cc.Equipped)
                    if (string.Equals(kv.Value, config, StringComparison.Ordinal)) return true;
            }
            catch (Exception) { }
            return false;
        }

        // =====================================================================================
        // read-back for tests
        // =====================================================================================

        /// <summary>
        /// One Trainer's charm state, as read back out of its live <c>CharacterComponent</c>.
        /// <c>crucible_get</c> renders an object as <c>TypeName { field=val, ... }</c>, so every member
        /// here is a plain public field — the same shape as
        /// <c>TrainerPartnerPersistence.PartnerRecord</c>.
        /// </summary>
        public sealed class CharmRecord
        {
            /// <summary>Guid of the Trainer.</summary>
            public string Owner;
            /// <summary>The starter line this Trainer resolved to (GRASS / WATER / FIRE).</summary>
            public string Starter;
            /// <summary>Its current XP-derived level (<c>ProgressionHelper.GetEntityLevel</c>).</summary>
            public int Level;
            /// <summary>Charms actually held right now, comma-separated in pack order.</summary>
            public string Held;
            /// <summary>How many charms are held. This is the value an unlock test asserts on.</summary>
            public int HeldCount;
            /// <summary>How many charms this level entitles it to (1, 2 or 3).</summary>
            public int OwedCount;
            /// <summary>Level at which the second charm arrives.</summary>
            public int SecondAt;
            /// <summary>Level at which the third charm arrives.</summary>
            public int ThirdAt;

            public override string ToString()
            {
                return "starter=" + (Starter ?? "-") + " level=" + Level
                       + " held=" + HeldCount + "[" + (Held ?? "") + "]"
                       + " owed=" + OwedCount + " secondAt=" + SecondAt + " thirdAt=" + ThirdAt;
            }
        }

        /// <summary>
        /// Every Trainer in the run and its charm state, recomputed from the live entities on every read
        /// — so a test reads the real inventory, not a mirror of it.
        /// <para><b>Test path:</b> <c>crucible_get TrainerCharmProgression.Records</c>, or
        /// <c>TrainerCharmProgression.Records[0].HeldCount</c> /
        /// <c>TrainerCharmProgression.Records[0].Starter</c> for a single field.</para>
        /// </summary>
        public static List<CharmRecord> Records
        {
            get
            {
                var list = new List<CharmRecord>();
                try
                {
                    var run = RouterHelper.Env != null ? RouterHelper.Env.GameRun : null;
                    var entities = run != null ? run.Entities : null;
                    if (entities == null) return list;

                    foreach (var e in entities)
                    {
                        if (e == null) continue;
                        CharacterComponent cc;
                        if (!e.TryGet<CharacterComponent>(out cc) || cc == null || cc.Things == null) continue;
                        // Same gate as Reconcile -- a Trainer holding zero charms is still a Trainer, and
                        // must show up here (as held=0) rather than making the summary read trainers=0.
                        if (!IsTrainer(cc)) continue;

                        string starter;
                        if (!CoreHelper.TryGetCustomData(cc, KeyStarter, out starter) || string.IsNullOrEmpty(starter))
                            starter = FirstHeldLine(cc);

                        int level = 0;
                        try { level = ProgressionHelper.GetEntityLevel(e); } catch (Exception) { }

                        var held = new List<string>();
                        for (int i = 0; i < KnownLines.Length; i++)
                            if (HasCharm(cc, TrainerPartnerPersistence.BallPrefix + KnownLines[i]))
                                held.Add(KnownLines[i]);

                        int second = Clamp(SecondCharmLevel != null ? SecondCharmLevel.Value : 3, 1, 9);
                        int third = Clamp(ThirdCharmLevel != null ? ThirdCharmLevel.Value : 6, 1, 9);
                        if (third < second) third = second;

                        list.Add(new CharmRecord
                        {
                            Owner = SafeGuid(e),
                            Starter = starter,
                            Level = level,
                            Held = string.Join(",", held.ToArray()),
                            HeldCount = held.Count,
                            OwedCount = starter == null ? 0 : DesiredLines(starter, level).Count,
                            SecondAt = second,
                            ThirdAt = third,
                        });
                    }
                }
                catch (Exception ex)
                {
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] trainer charm records could not be read back: " + ex.Message);
                }
                return list;
            }
        }

        /// <summary>
        /// The same data as one flat string, for a log-style or substring assertion.
        /// <para><b>Test path:</b> <c>crucible_get TrainerCharmProgression.CharmSummary</c>. Renders as
        /// <c>trainers=1 | starter=GRASS level=3 held=2[GRASS,WATER] owed=2 secondAt=3 thirdAt=6</c>, or
        /// <c>trainers=0</c> when no Trainer is in the run.</para>
        /// </summary>
        public static string CharmSummary
        {
            get
            {
                var records = Records;
                var sb = new StringBuilder();
                sb.Append("trainers=").Append(records.Count);
                for (int i = 0; i < records.Count; i++)
                    sb.Append(i == 0 ? " | " : " ; ").Append(records[i].ToString());
                return sb.ToString();
            }
        }

        private static string CharmSummaryFor(Entity e, CharacterComponent cc, string starter, int level)
        {
            var held = new List<string>();
            for (int i = 0; i < KnownLines.Length; i++)
                if (HasCharm(cc, TrainerPartnerPersistence.BallPrefix + KnownLines[i])) held.Add(KnownLines[i]);
            return "owner=" + SafeGuid(e) + " starter=" + (starter ?? "-") + " level=" + level
                   + " held=" + held.Count + "[" + string.Join(",", held.ToArray()) + "]"
                   + " owed=" + DesiredLines(starter, level).Count;
        }

        private static string SafeGuid(Entity e)
        {
            try { return e.Guid ?? ""; } catch (Exception) { return ""; }
        }
    }
}

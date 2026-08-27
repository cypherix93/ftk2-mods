using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace ClassForge.Plugin
{
    /// <summary>
    /// AI TARGETING DRAW-NEUTRALITY — the two fixes this workstream owes multiplayer, in one place because
    /// they are the same fix seen from two sides.
    ///
    /// <para><b>The failure mode, restated.</b> FTK2 co-op is deterministic lockstep and AI is recomputed on
    /// every peer rather than being host-authoritative, so what breaks a session is not two peers rolling
    /// different VALUES — it is two peers taking a different NUMBER of draws from the shared
    /// <c>CombatState.Random</c>. After that the streams are permanently offset and every subsequent roll
    /// disagrees. The vendor polices this itself: <c>GameAction.DesyncDetectionData.GameRandomNextInt</c> is
    /// a per-action stream-order probe (docs/MULTIPLAYER.md).</para>
    ///
    /// <para><b>Where vanilla's draw count forks.</b> Inside <c>AIHelper.ForceAiDecision</c>
    /// (<c>AIHelper.cs:475</c>) the targeting phase reaches
    /// <c>AIHelper.GetPreferredTarget</c> (<c>:586</c>), whose gate is
    /// <c>if (pTendency != 0 &amp;&amp; (_strictTendencies.Contains(pTendency) || pCombatState.Random.NextChance(num * 0.01m)))</c>
    /// (<c>:589</c>). <c>||</c> short-circuits, so:</para>
    /// <list type="bullet">
    ///   <item><description><c>pTendency == NONE</c> → <b>0 draws</b> (the first conjunct fails).</description></item>
    ///   <item><description>a STRICT tendency (<c>MUSTHAVEPOISON</c> / <c>MUSTNOTHAVEHIVE</c> /
    ///   <c>MUSTHAVELOWHEALTH</c>, <c>AIHelper.cs:27-32</c>) → <b>0 draws</b> (the left side of the
    ///   <c>||</c> is true).</description></item>
    ///   <item><description>any other tendency → <b>1 draw</b>.</description></item>
    /// </list>
    /// <para>(<c>GameRandom.NextChance(decimal)</c> itself is honest — it always costs exactly one draw
    /// regardless of the chance value, <c>GameRandom.cs:191</c> → <c>NextNormalizedDecimal</c>. The fork is
    /// entirely in the caller's short-circuit. The <c>ScourgeHelper.cs:82</c> hazard the brief names is the
    /// same shape one level up: <c>pForce || pChance &gt;= 1m || NextChance(pChance)</c>.)</para>
    ///
    /// <para><b>FIX 1 — why that fork is OURS and not just vanilla's.</b> A fork on a value both peers
    /// compute identically is harmless, and vanilla's is: the tendency comes from the ability config, which
    /// is parity-hashed data. But <see cref="TrainerPartnerAutonomy.ApplyTendencyShaping"/> REWRITES that tendency
    /// from per-peer mod state — <c>NONE → MOSTDAMAGE</c> for a tracked partner (0 draws → 1), and
    /// <c>anything → MUSTHAVELOWHEALTH</c> for a live FINISH_IT command (1 draw → 0). Every input it reads
    /// is replicated today, so it agrees today; but it is one bug in partner tracking away from being a
    /// permanent desync, and "this shaping happens to be symmetric" is a much weaker property than "the
    /// draw count does not depend on the tendency at all". So the gate is made unconditional: this prefix
    /// takes the compensating draw whenever the vanilla body is about to take none, leaving the total at
    /// <b>exactly one</b> for every tendency.</para>
    ///
    /// <para><b>FIX 2 — the Focus Fire draw.</b> <c>ForceAiDecision</c> drains
    /// <c>AIComponent.PriorityTargets</c> BEFORE consulting <c>GetPreferredTarget</c>, and when a queued
    /// GUID's tile is targetable it assigns the position and <c>break</c>s, so <c>GetPreferredTarget</c> is
    /// never called at all (<c>AIHelper.cs:516-551</c>) — <b>the whole tendency draw is skipped</b>.
    /// <see cref="TrainerFocusFire"/> is what puts a GUID in that queue, so issuing a Focus Fire order
    /// silently changes the draw count of every ally it reaches. Today both peers run the same postfix off
    /// the same replicated ability decision and so both skip together; but that makes draw count a function
    /// of a mod-issued order, which is exactly the class of dependency this file exists to remove. The
    /// postfix here takes the compensating draw when the targeting path ran and
    /// <c>GetPreferredTarget</c> did not.</para>
    ///
    /// <para><b>The resulting invariant, which is what the tests pin:</b> a <c>ForceAiDecision</c> call that
    /// runs the targeting path (<c>pForcedPosition == null</c>) consumes <b>exactly one</b> shared-stream
    /// draw for target preference, regardless of tendency, of strictness, of whether a Focus Fire order was
    /// issued, and of whether the actor is a tracked partner.</para>
    ///
    /// <para><b>Scope is deliberately narrow, and this is the load-bearing decision in the file.</b>
    /// <c>GetPreferredTarget</c> has five OTHER call sites — <c>CombatHelper.cs:317</c>, <c>:994</c>,
    /// <c>CombatPhase.cs:1829</c>, <c>:3483</c>, <c>:4436</c> — and every one of them passes
    /// <c>eAiTendencies.NONE</c> literally, i.e. is currently draw-free. <c>CombatPhase.cs:3483</c> in
    /// particular computes <c>_uiSelectedPosition</c>, a presentation value that has no reason to run on
    /// every peer. Neutralising there would ADD a draw to a call site that is currently safe to run
    /// asymmetrically — a desync manufactured by the fix meant to prevent one, which is the exact mistake
    /// <c>ClassForgePlugin.cs:551</c> already records for the partner-revive hook. So both the shaping AND
    /// the compensation are gated on being inside a <c>ForceAiDecision</c> frame; outside one, this file
    /// leaves the game bit-for-bit alone. That also fixes a latent version of the same bug in the shipped
    /// autonomy prefix, which shaped tendency at all six call sites.</para>
    ///
    /// <para><b>Why the forced-position branch is NOT compensated.</b> When <c>pForcedPosition.HasValue</c>,
    /// vanilla skips the whole targeting block — no <c>ShuffleList</c>, no <c>GetPreferredTarget</c>, zero
    /// draws end to end. A completely draw-free call is safe to make asymmetrically; adding a draw to it
    /// would make it unsafe. The compensated path is the one that already contains
    /// <c>pGameRun.CombatState.Random.ShuffleList(list)</c> (<c>AIHelper.cs:511</c>, exactly
    /// <c>list.Count</c> draws), so it is already a path both peers must reach the same number of times.</para>
    ///
    /// <para><b>What the compensating draw costs in game feel.</b> The stream advances one step earlier than
    /// vanilla would have advanced it, on every AI targeting decision. That changes which values later rolls
    /// see — identically on every peer — so it is a reshuffle, not a bias. Nothing reads the compensating
    /// value; it is drawn and dropped.</para>
    /// </summary>
    public static class AiDrawNeutrality
    {
        // =====================================================================================
        // knob
        // =====================================================================================

        internal static ConfigEntry<bool> Enable;

        internal static void Bind(ConfigFile config)
        {
            Enable = CFConfig.Bind(config, "Multiplayer", "AiTargetingDrawNeutrality", true,
                "Make AIHelper.ForceAiDecision's target-preference gate cost exactly ONE shared-GameRandom "
                + "draw every time it runs the targeting path, instead of 0 or 1 depending on the ability's "
                + "AI tendency (AIHelper.cs:589 short-circuits on strict tendencies and on NONE) and on "
                + "whether a Focus Fire order let AIComponent.PriorityTargets supply the position before "
                + "GetPreferredTarget was ever consulted (AIHelper.cs:516-551). Co-op is deterministic "
                + "lockstep and AI is recomputed per peer, so a draw COUNT that depends on mod state is a "
                + "permanent desync -- this makes the count depend on nothing. Off = vanilla draw counts, "
                + "and ClassForge's own partner tendency shaping and Focus Fire orders both become "
                + "draw-count-visible again. GAMEPLAY-RELEVANT: multiplayer peers must agree on this value.",
                ClassForge.Core.ParityClass.Gameplay);
        }

        /// <summary>
        /// Whether the compensating draw is taken. Deliberately NOT a function of
        /// <c>ClassForgePlugin.FeaturesActive</c>.
        ///
        /// <para><b>Why the parity latches were removed from this gate.</b> <c>FeaturesActive</c> is
        /// <c>Enabled &amp;&amp; !ParityBridge.Blocked &amp;&amp; !ParityBridge.SafeMode</c>, and both
        /// latches are decided from LOCAL evidence: the reachable case is one peer alone lacking
        /// FTK2.DevKit, which puts THAT peer — and only that peer — into SafeMode. Gating the
        /// compensating draw on a per-peer latch made the DRAW COUNT a function of a per-peer latch,
        /// which is the precise failure this whole file exists to remove. Keying it on
        /// <see cref="Enable"/> instead keys it on a <c>ParityClass.Gameplay</c> knob, i.e. on a value
        /// that IS in the parity payload and that a divergence on refuses the join.</para>
        ///
        /// <para><b>This does not make a one-sided SafeMode safe</b>, and it is not claimed to: the
        /// degraded peer also stops firing recipes, which forks the draw count on its own. It removes
        /// one fork out of several, and <see cref="ParityBridge.AnnounceUnilateralDegrade"/> is what
        /// makes the remaining ones loud instead of silent. What this DOES buy outright: with both peers
        /// on the same knob value, the AI-targeting draw is one per decision on both, even if one of
        /// them is in SafeMode — so the subsystem itself can no longer be the thing that forked.</para>
        ///
        /// <para><see cref="_hardDisabled"/> is the one local input, and it is a LATCH, not a
        /// per-decision fallback — see <see cref="HardDisable"/>.</para>
        /// </summary>
        private static bool Active
        {
            get
            {
                return !_hardDisabled
                       && ClassForgePlugin.Enabled != null && ClassForgePlugin.Enabled.Value
                       && Enable != null && Enable.Value;
            }
        }

        /// <summary>
        /// Set by <c>ClassForgePlugin</c> from the return of the <c>GetPreferredTarget</c> patch install.
        /// The <c>ForceAiDecision</c> postfix compensates on "the targeting path ran but
        /// <c>GetPreferredTarget</c> was not reached"; if the <c>GetPreferredTarget</c> prefix failed to
        /// install after a game update, that condition would be true on EVERY decision and the postfix
        /// would add a draw to every one of them. A half-installed feature is therefore not degraded to
        /// "no compensation this decision" — it is a <see cref="HardDisable"/>, see there.
        /// </summary>
        internal static bool PreferredTargetHookInstalled;

        private static bool _hardDisabled;
        private static string _hardDisableReason;

        /// <summary>True once <see cref="HardDisable"/> has latched. Read-only diagnostic.</summary>
        internal static bool IsHardDisabled { get { return _hardDisabled; } }

        /// <summary>
        /// The escalation that replaces three silent per-decision fallbacks.
        ///
        /// <para><b>What was wrong with the fallbacks.</b> A Harmony install failure from another mod's
        /// patch ordering, a reflection miss on <c>AIHelper._strictTendencies</c>, or a single stray
        /// exception each used to make THIS decision quietly revert to vanilla's draw count while every
        /// other decision stayed neutralised — and while the peer next to us neutralised all of them. One
        /// draw is all it takes: after it the two shared streams are permanently offset, every subsequent
        /// roll in the session disagrees, and because <c>CombatState</c> is <c>[JsonIgnore]</c> nothing
        /// reports it. "Degrade this one decision" is the worst of the available behaviours, because it
        /// is both wrong and invisible.</para>
        ///
        /// <para><b>What replaces it.</b> A one-way latch for the rest of the process: from here on this
        /// peer takes VANILLA draw counts consistently (never a mixture), the reason is logged at Error
        /// level once, and — if this is an online session —
        /// <see cref="ParityBridge.AnnounceUnilateralDegrade"/> tells the player in-game that their
        /// machine has degraded on its own and the session should be left. A consistent wrong state that
        /// announces itself is strictly better than an inconsistent one that does not.</para>
        ///
        /// <para>Deliberately NOT reset at the session boundary the parity latches reset at: every cause
        /// above is a property of this PROCESS's patch/reflection state, not of the session, so clearing
        /// it would just re-latch on the next decision — after having taken one more wrong draw.</para>
        /// </summary>
        internal static void HardDisable(string reason)
        {
            if (_hardDisabled) return;
            _hardDisabled = true;
            _hardDisableReason = reason ?? "(no reason given)";

            try
            {
                ClassForgePlugin.Log.LogError(
                    "==================================================================\n" +
                    "  ClassForge AI DRAW-NEUTRALITY HARD-DISABLED (this process)\n" +
                    "==================================================================\n" +
                    "  " + _hardDisableReason + "\n" +
                    "------------------------------------------------------------------\n" +
                    "  From here on this peer takes VANILLA AI targeting draw counts, on\n" +
                    "  every decision -- consistently, never a per-decision mixture. That\n" +
                    "  means ClassForge's partner tendency shaping and Focus Fire orders are\n" +
                    "  draw-count-visible again on THIS machine only.\n" +
                    "  Single player: harmless. Online: see the banner.\n" +
                    "==================================================================");
                ParityBridge.AnnounceUnilateralDegrade("AI DRAW-NEUTRALITY HARD-DISABLED", _hardDisableReason);
            }
            catch (Exception)
            {
                // The latch above is the enforcement; a failed report must never undo it.
            }
        }

        // =====================================================================================
        // per-call frames
        // =====================================================================================

        /// <summary>One in-flight <c>ForceAiDecision</c> call.</summary>
        private sealed class Frame
        {
            internal bool TargetingPath;          // pForcedPosition == null
            internal bool PreferredTargetCalled;  // our GetPreferredTarget prefix ran for this call
        }

        /// <summary>
        /// A stack, because <c>ForceAiDecision</c> re-enters itself: <c>AIHelper.cs:441</c> (the priority
        /// branch) and <c>ConfusedAiDecision</c>'s loop (<c>:469</c>) both call it. Combat runs on Unity's
        /// main thread, so a plain static stack is correct here; the depth cap below is a belt-and-braces
        /// guard against a leaked frame rather than a real expectation.
        /// </summary>
        private static readonly List<Frame> _frames = new List<Frame>();

        /// <summary>Deep enough for the real nesting (2) with a wide margin; past this we assume a leak and
        /// reset rather than growing without bound.</summary>
        private const int MaxDepth = 16;

        private static Frame Top
        {
            get { return _frames.Count > 0 ? _frames[_frames.Count - 1] : null; }
        }

        // =====================================================================================
        // strict-tendency mirror
        // =====================================================================================

        private static List<eAiTendencies> _strict;
        private static bool _strictResolved;

        /// <summary>
        /// <c>AIHelper._strictTendencies</c>, read once by reflection. It is the field the vanilla gate's
        /// left-hand side consults, so reading the real one, rather than hard-coding the three members,
        /// keeps this correct if a game update adds a fourth. A resolution failure is reported loudly and
        /// the whole feature disables itself: guessing wrong here means guessing the draw count wrong,
        /// which is worse than not helping at all.
        /// </summary>
        private static bool TryStrict(out List<eAiTendencies> strict)
        {
            if (!_strictResolved)
            {
                _strictResolved = true;
                try
                {
                    var f = AccessTools.Field(typeof(AIHelper), "_strictTendencies");
                    _strict = f != null ? f.GetValue(null) as List<eAiTendencies> : null;
                }
                catch (Exception) { _strict = null; }

                if (_strict == null)
                    HardDisable("AIHelper._strictTendencies could not be read by reflection, so the "
                        + "vanilla gate's own strict-tendency set is unknown and the compensation cannot "
                        + "be computed. Game update drift?");
            }
            strict = _strict;
            return strict != null;
        }

        /// <summary>
        /// How many compensating draws the gate needs for this tendency, from the tested model in
        /// <c>ClassForge.Core.Rng.AiTargetingDraws</c>. The arithmetic lives there rather than here because
        /// nothing in this assembly can be unit-tested (it references FTK2, BepInEx and Unity), and draw
        /// COUNT is precisely the property that must be executable rather than merely asserted in a
        /// comment. This method's only job is to turn a game enum into the two booleans that model takes.
        /// </summary>
        private static int GateCompensation(eAiTendencies tendency, List<eAiTendencies> strict)
        {
            bool isNone = tendency == eAiTendencies.NONE;   // eAiTendencies.NONE is the `!= 0` in vanilla
            bool isStrict = !isNone && strict.Contains(tendency);
            return ClassForge.Core.Rng.AiTargetingDraws.GateCompensationDraws(isNone, isStrict);
        }

        /// <summary>
        /// One draw off the SHARED combat stream, value discarded. <c>NextChance(decimal)</c> is used
        /// because it is the very call being compensated for, so the compensating draw and the real draw
        /// consume the underlying <c>System.Random</c> identically (<c>NextNormalizedDecimal</c> →
        /// <c>NextDecimal</c> → <c>random.Next(1000000)</c>, <c>GameRandom.cs:105-123</c>) — one step, same
        /// shape. <c>0m</c> is passed only because the verdict is thrown away.
        /// </summary>
        private static void BurnOne(CombatState state, string why)
        {
            if (state == null || state.Random == null) return;
            state.Random.NextChance(0m);
            if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                ClassForgePlugin.Log.LogDebug("[ClassForge] AI draw-neutrality: compensating draw taken (" + why + ").");
            _compensated++;
        }

        // =====================================================================================
        // patches
        // =====================================================================================

        /// <summary>
        /// Prefix on <c>AIHelper.ForceAiDecision</c>. Opens a frame; takes no draw and reads no game state
        /// beyond the one nullable argument.
        /// </summary>
        /// <para><b>Not gated on <see cref="Active"/>.</b> The frame is also what scopes partner tendency
        /// shaping to the AI-decision path, and that feature has its own knobs and must keep working when
        /// draw neutrality is switched off. Opening a frame costs one small allocation and takes no
        /// draw, so there is nothing to save by skipping it.</para>
        public static void ForceAiDecision_Prefix((int x, int y)? pForcedPosition)
        {
            try
            {
                if (_frames.Count >= MaxDepth)
                {
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] AI draw-neutrality frame stack hit depth " + _frames.Count
                        + " — assuming a leaked frame and resetting. No draw was taken.");
                    _frames.Clear();
                }
                _frames.Add(new Frame { TargetingPath = !pForcedPosition.HasValue });
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning("[ClassForge] AI draw-neutrality could not open a frame "
                    + "(fail-safe, vanilla draw counts stand for this decision): " + ex.Message);
            }
        }

        /// <summary>
        /// Postfix on <c>AIHelper.ForceAiDecision</c> — <b>FIX 2</b>. If the targeting path ran but
        /// <c>GetPreferredTarget</c> was never reached, the position came from the
        /// <c>AIComponent.PriorityTargets</c> drain (<c>AIHelper.cs:516-551</c>) and the tendency draw was
        /// skipped; take it here so an ordered ally costs the same as an unordered one.
        /// <para>Runs before <see cref="ForceAiDecision_Finalizer"/>, which is what pops the frame — so a
        /// throw out of the original method skips the compensation but never leaks the frame.</para>
        /// </summary>
        public static void ForceAiDecision_Postfix(GameRunData pGameRun)
        {
            if (!Active) return;
            try
            {
                var frame = Top;
                if (frame == null) return;
                if (!frame.TargetingPath) return;          // forced position: vanilla took zero, keep zero
                if (frame.PreferredTargetCalled) return;   // the gate already ran and is already neutral

                if (!PreferredTargetHookInstalled)
                {
                    // W1-C: this used to be a silent `return` -- i.e. one fewer draw on THIS decision
                    // while the other half of the feature kept working on every other one, and while a
                    // peer with a clean patch install neutralised all of them. It is a permanent
                    // property of this process's patch state, so it latches.
                    HardDisable("the AIHelper.GetPreferredTarget prefix is not installed (another mod's "
                        + "patch ordering, or game update drift), so the two halves of the compensation "
                        + "cannot agree on which decisions already cost a draw.");
                    return;
                }

                List<eAiTendencies> strict;
                if (!TryStrict(out strict)) return;   // TryStrict has already hard-disabled the feature

                if (ClassForge.Core.Rng.AiTargetingDraws.SkippedGateCompensationDraws(false) == 0) return;

                var state = pGameRun != null ? pGameRun.CombatState : null;
                BurnOne(state, "PriorityTargets supplied the position, so GetPreferredTarget was skipped");
                _focusFireCompensated++;
            }
            catch (Exception ex)
            {
                // W1-C: a swallowed exception here left this ONE decision on vanilla's draw count while
                // every other decision stayed neutralised -- an offset of exactly one draw, which is all
                // it takes, and invisible. Latch instead.
                HardDisable("the ForceAiDecision postfix threw: " + ex.Message);
            }
        }

        /// <summary>Finalizer on <c>AIHelper.ForceAiDecision</c>: pops the frame whether the method
        /// returned or threw. Returns <paramref name="__exception"/> unchanged — this never swallows a game
        /// exception.</summary>
        public static Exception ForceAiDecision_Finalizer(Exception __exception)
        {
            try { if (_frames.Count > 0) _frames.RemoveAt(_frames.Count - 1); }
            catch (Exception) { }
            return __exception;
        }

        /// <summary>
        /// Prefix on <c>AIHelper.GetPreferredTarget</c> — <b>FIX 1</b>, and the single registered entry
        /// point for partner tendency shaping (which used to be registered here directly as
        /// <c>TrainerPartnerAutonomy.GetPreferredTarget_Prefix</c>).
        ///
        /// <para>Order matters and is the whole point: shape FIRST, then measure the SHAPED tendency and
        /// compensate. Compensating against the pre-shaping tendency would leave exactly the fork this is
        /// meant to close.</para>
        ///
        /// <para>Outside a <c>ForceAiDecision</c> frame this returns immediately, doing neither — see the
        /// type remarks on why the other five call sites must be left alone.</para>
        /// </summary>
        public static void GetPreferredTarget_Prefix(Entity pOrigin, ref eAiTendencies pTendency,
            eTileOccupancies pOccupancy, eTileTargetAreas pTargetArea, int pTargetGroup,
            List<Entity> pTargetableTiles, CombatState pCombatState)
        {
            var frame = Top;
            if (frame == null) return;      // not inside ForceAiDecision: vanilla, untouched
            frame.PreferredTargetCalled = true;

            // Shaping runs even when neutrality is switched off — the knob governs the DRAW, not the
            // targeting behaviour, and the partner feature has its own knobs.
            TrainerPartnerAutonomy.ApplyTendencyShaping(pOrigin, ref pTendency, pOccupancy,
                pTargetGroup, pTargetableTiles, pCombatState);

            if (!Active) return;
            try
            {
                List<eAiTendencies> strict;
                if (!TryStrict(out strict)) return;
                if (GateCompensation(pTendency, strict) == 0) return;   // vanilla already takes exactly one

                BurnOne(pCombatState, "tendency " + pTendency + " would have cost zero draws");
                _tendencyCompensated++;
            }
            catch (Exception ex)
            {
                // W1-C, same reasoning as the postfix: a per-decision silent revert is the one outcome
                // that is both wrong and undetectable.
                HardDisable("the GetPreferredTarget prefix threw: " + ex.Message);
            }
        }

        // =====================================================================================
        // read-back
        // =====================================================================================

        private static int _compensated, _tendencyCompensated, _focusFireCompensated;

        /// <summary>Counters for a diagnostic session: how many draws this file added, and why.</summary>
        public static string StateSummary
        {
            get
            {
                return "aiDrawNeutrality=" + (Active ? "on" : "off")
                     + " compensated=" + _compensated
                     + " (tendency=" + _tendencyCompensated
                     + " focusFire=" + _focusFireCompensated + ")"
                     + " openFrames=" + _frames.Count
                     + (_hardDisabled ? " HARD-DISABLED(" + _hardDisableReason + ")" : "");
            }
        }
    }
}

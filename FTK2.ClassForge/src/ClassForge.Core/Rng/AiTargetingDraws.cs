namespace ClassForge.Core.Rng
{
    /// <summary>
    /// A pure, game-reference-free model of how many draws <c>AIHelper.ForceAiDecision</c>'s target-
    /// preference gate takes from the SHARED combat stream — vanilla's answer, and the answer after
    /// <c>ClassForge.Plugin.AiDrawNeutrality</c> compensates.
    ///
    /// <para><b>Why this lives in Core and not next to the Harmony patch.</b> Draw COUNT is the property
    /// that decides whether a 4-player co-op session survives, and it is the one property a Harmony patch
    /// cannot be unit-tested for: <c>ClassForge.Plugin</c> references FTK2, BepInEx and Unity, so nothing in
    /// it is reachable from a test binary. Extracting the arithmetic here makes the invariant executable
    /// instead of merely commented, and the patch becomes a thin caller of a tested function. Everything
    /// this file needs is expressible as two booleans, so nothing is lost in the move.</para>
    ///
    /// <para><b>The vanilla gate being modelled</b> is <c>AIHelper.cs:589</c>:
    /// <c>if (pTendency != 0 &amp;&amp; (_strictTendencies.Contains(pTendency) || pCombatState.Random.NextChance(num * 0.01m)))</c>.
    /// <c>&amp;&amp;</c> and <c>||</c> both short-circuit, so the <c>NextChance</c> — the only draw on this
    /// path — is reached only when the tendency is neither <c>NONE</c> nor strict. <c>NextChance(decimal)</c>
    /// itself always costs exactly one draw whatever the chance value (<c>GameRandom.cs:191</c>), so the
    /// entire fork is in the caller.</para>
    ///
    /// <para><b>And the skip being modelled</b> is <c>AIHelper.cs:516-551</c>: an
    /// <c>AIComponent.PriorityTargets</c> entry whose tile is targetable supplies the position directly and
    /// <c>GetPreferredTarget</c> is never called, so the gate above costs nothing at all. That queue is what
    /// <c>TrainerFocusFire</c> writes into.</para>
    /// </summary>
    public static class AiTargetingDraws
    {
        /// <summary>
        /// The number of shared-stream draws the UNPATCHED gate takes: 1 when the tendency is neither
        /// <c>NONE</c> nor strict, otherwise 0.
        /// </summary>
        public static int VanillaGateDraws(bool tendencyIsNone, bool tendencyIsStrict)
        {
            if (tendencyIsNone) return 0;
            return tendencyIsStrict ? 0 : 1;
        }

        /// <summary>
        /// The compensating draw <c>AiDrawNeutrality</c>'s <c>GetPreferredTarget</c> prefix must take so the
        /// gate's total is 1 for every tendency. Exactly the complement of
        /// <see cref="VanillaGateDraws"/>.
        /// </summary>
        public static int GateCompensationDraws(bool tendencyIsNone, bool tendencyIsStrict)
        {
            return 1 - VanillaGateDraws(tendencyIsNone, tendencyIsStrict);
        }

        /// <summary>
        /// Total draws for the target-preference decision of one <c>ForceAiDecision</c> call that runs the
        /// targeting path, WITHOUT the neutrality patch. This is the function that must not be constant-1 —
        /// it is here so a test can prove the bug exists before proving the fix removes it.
        /// </summary>
        /// <param name="preferredTargetReached">False when a <c>PriorityTargets</c> hit already supplied the
        /// position (a Focus Fire order), so <c>GetPreferredTarget</c> was skipped entirely.</param>
        public static int VanillaTargetingDraws(bool preferredTargetReached, bool tendencyIsNone, bool tendencyIsStrict)
        {
            return preferredTargetReached ? VanillaGateDraws(tendencyIsNone, tendencyIsStrict) : 0;
        }

        /// <summary>
        /// The compensation <c>AiDrawNeutrality</c>'s <c>ForceAiDecision</c> POSTFIX must take: one draw when
        /// the targeting path ran but <c>GetPreferredTarget</c> was skipped, because the gate that would have
        /// cost 1 never ran at all.
        /// </summary>
        public static int SkippedGateCompensationDraws(bool preferredTargetReached)
        {
            return preferredTargetReached ? 0 : 1;
        }

        /// <summary>
        /// Total draws for the target-preference decision WITH neutrality on. <b>This is the invariant:
        /// it is 1 for every combination of its arguments.</b> A change that makes it anything else is a
        /// co-op desync, and the test suite asserts it exhaustively.
        /// </summary>
        public static int NeutralisedTargetingDraws(bool preferredTargetReached, bool tendencyIsNone, bool tendencyIsStrict)
        {
            if (!preferredTargetReached)
                return VanillaTargetingDraws(false, tendencyIsNone, tendencyIsStrict)
                     + SkippedGateCompensationDraws(false);

            return VanillaGateDraws(tendencyIsNone, tendencyIsStrict)
                 + GateCompensationDraws(tendencyIsNone, tendencyIsStrict);
        }
    }
}

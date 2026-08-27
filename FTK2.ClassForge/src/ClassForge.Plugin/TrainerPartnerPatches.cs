using System;
using System.Reflection;
using HarmonyLib;

namespace ClassForge.Plugin
{
    /// <summary>
    /// The hooks that keep <see cref="TrainerPartnerPersistence"/>'s store in step with live play
    /// (test-checklist §L0).
    ///
    /// <para><b>Two jobs.</b> (1) Mirror a tracked partner's <c>CurrentHealth</c> onto its ball the
    /// moment it changes, so the value survives however the fight ends. (2) Revive and heal partners at
    /// a town, which is the only restore path the design allows.</para>
    ///
    /// <para><b>Why the health mirror is three patches, not one.</b> <c>CharacterHelper.AddHealth</c>'s
    /// terminal overload (<c>CharacterHelper.cs:1360</c>) is the funnel every damage and heal goes
    /// through — the other three overloads (<c>:1333</c>, <c>:1339</c>, <c>:1345</c>) delegate to it —
    /// but two paths write <c>CurrentHealth</c> directly and never touch it:
    /// <c>CharacterHelper.KillCharacter</c> (<c>CharacterHelper.cs:2178</c>,
    /// <c>CurrentHealth = 0</c>) and <c>CharacterHelper.SetToMaxHealth</c>
    /// (<c>CharacterHelper.cs:1259-1261</c>). Missing <c>KillCharacter</c> in particular would mean the
    /// exact event the DOWNED state exists for is the one event not recorded.</para>
    ///
    /// <para><b>Cost to every other class: one integer compare.</b> Each body opens with
    /// <c>TrainerPartnerPersistence.NothingTracked</c>, which is <c>_live.Count == 0</c>. Nothing is ever
    /// tracked unless a summon resolved to a <c>ARM_ORIG_TRAINER_BALL_*</c> item in the summoner's own
    /// inventory, and only the Trainer class grants those.</para>
    ///
    /// <para>Every body is fully guarded. These sit on the game's hottest health path; a throw here would
    /// surface as combat breaking, so a failure to record a partner's HP is always swallowed and logged.</para>
    /// </summary>
    public static class TrainerPartnerPatches
    {
        /// <summary>
        /// Postfix on the terminal <c>CharacterHelper.AddHealth</c>
        /// (<c>Entity, ref int, bool, List&lt;(eAbilityResults,object)&gt;, StatChangedResultsData, bool</c>
        /// — <c>CharacterHelper.cs:1360</c>). Registered as a SECOND patch on a method ClassForge already
        /// prefixes for <c>ON_HEAL_PENDING</c>; Harmony composes them and the trigger prefix is untouched.
        /// </summary>
        public static void AddHealth_Postfix(Entity pEntity)
        {
            if (TrainerPartnerPersistence.NothingTracked) return;
            try { TrainerPartnerPersistence.SyncFromEntity(pEntity); }
            catch (Exception ex) { Warn(ex); }
        }

        /// <summary>
        /// Postfix on <c>CharacterHelper.KillCharacter</c> (<c>CharacterHelper.cs:2166</c>), which sets
        /// <c>CurrentHealth = 0</c> directly at <c>:2178</c> without going through <c>AddHealth</c>.
        /// This is the hook that actually latches a partner DOWNED.
        /// </summary>
        public static void KillCharacter_Postfix(Entity pTargetEntity)
        {
            if (TrainerPartnerPersistence.NothingTracked) return;
            try { TrainerPartnerPersistence.SyncFromEntity(pTargetEntity); }
            catch (Exception ex) { Warn(ex); }
        }

        /// <summary>
        /// Postfix on <c>CharacterHelper.SetToMaxHealth</c> (<c>CharacterHelper.cs:1259</c>), the other
        /// direct <c>CurrentHealth</c> write. Without it a partner topped up by some full-heal path would
        /// keep reporting its old stored HP into the next fight.
        /// </summary>
        public static void SetToMaxHealth_Postfix(Entity pEntity)
        {
            if (TrainerPartnerPersistence.NothingTracked) return;
            try { TrainerPartnerPersistence.SyncFromEntity(pEntity); }
            catch (Exception ex) { Warn(ex); }
        }

        // =====================================================================================
        // town revive  --  the ONE seam every peer executes
        // =====================================================================================

        /// <summary>Substring that identifies the compiler-generated stub for <c>_onUseService</c>. The
        /// full emitted name is <c>&lt;_performEncounterAction&gt;g___onUseService|22</c> on the nested
        /// display class <c>AdventureDirector+&lt;&gt;c__DisplayClass219_5</c> (both verified against the
        /// string heap of <c>tools/bin/refs/FTK2.dll</c>). Only this stable middle fragment is matched: the
        /// <c>219_5</c> and <c>|22</c> ordinals are assigned by the compiler and shift with any game
        /// rebuild, so hard-coding them would make the patch silently stop applying on the next patch
        /// day.</summary>
        private const string UseServiceMarker = "g___onUseService";

        /// <summary>
        /// Registers the town-revive prefix on <c>AdventureDirector._onUseService</c>. Returns false when
        /// the target cannot be found, in which case NOTHING is patched and partners simply never revive --
        /// a degradation, never a desync.
        ///
        /// <para><b>Why this seam and not <c>ServiceMenuViewHelper.Show</c>.</b> <c>Show</c> is pure UI, and
        /// <c>AdventureDirector.cs:10292-10294</c> <c>break</c>s before calling it for a remote player -- so
        /// the old postfix ran on the OWNING peer only. It also missed the
        /// <c>pMenuContext.TownService != 0</c> branch at <c>:10289-10290</c> entirely, which calls
        /// <c>_onUseService</c> and never calls <c>Show</c> on anybody. Since a downed partner is skipped by
        /// <c>ExecSummon</c> before <c>ADD_CHARACTER</c>, a one-sided revive means one peer fields the
        /// partner and the other does not: a roster split, hence a different <c>GetTargetableTiles</c>
        /// Count, hence a different <c>ShuffleList</c> draw count off the shared stream on the next AI turn
        /// (<c>AIHelper.cs:507-511</c>, <c>GameRandom.cs:227-241</c>).</para>
        ///
        /// <para><b>Why <c>_onUseService</c> is the replicated one.</b> It is reached from exactly two
        /// places. <c>:10283</c> is the click closure, which first tries
        /// <c>_trySendNetworkAction(pTownService, ...)</c> -- in online multiplayer that ALWAYS returns true
        /// after broadcasting (<c>:15133-15150</c>), so the local peer does not apply the service there at
        /// all. <c>:10290</c> is the branch taken when the broadcast comes back:
        /// <c>_handleNetworkAction</c> rebuilds an <c>EncounterActionContext</c> with
        /// <c>TownService = townAction</c> and <c>pAllowBroadcast: false</c> (<c>:15217-15226</c>), and
        /// every peer runs it. The service ACTION is networked; the menu RENDER is not. In single-player
        /// <c>_trySendNetworkAction</c> returns false and <c>:10285</c> applies it directly -- same
        /// method.</para>
        ///
        /// <para>Per <c>docs/MULTIPLAYER.md</c> R3 no <c>CF_SYNC_*</c> payload is introduced: this mirrors
        /// the game's own replicated action and audits, it does not invent a channel.</para>
        /// </summary>
        public static bool TryPatchUseService(Harmony harmony)
        {
            try
            {
                var target = FindUseServiceMethod();
                if (target == null)
                {
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] AdventureDirector._onUseService could not be located, so downed "
                        + "Trainer partners will not revive at a town. Combat is unaffected.");
                    return false;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(
                    typeof(TrainerPartnerPatches).GetMethod(
                        nameof(UseService_Prefix), BindingFlags.Public | BindingFlags.Static)));
                ClassForgePlugin.Log.LogInfo(
                    "[ClassForge] town partner revive hooked to " + target.DeclaringType.FullName
                    + "." + target.Name);
                return true;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] the town partner revive hook could not be installed; town services "
                    + "continue unchanged: " + ex.Message);
                return false;
            }
        }

        /// <summary>The <c>_onUseService</c> local function, searched for by name fragment on
        /// <c>AdventureDirector</c> itself and on each of its compiler-generated nested display classes.
        /// Null when no single unambiguous match exists.</summary>
        private static MethodBase FindUseServiceMethod()
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static | BindingFlags.Instance
                                     | BindingFlags.DeclaredOnly;

            var director = AccessTools.TypeByName("AdventureDirector");
            if (director == null) return null;

            MethodBase found = Match(director.GetMethods(all));
            if (found != null) return found;

            var nested = director.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < nested.Length; i++)
            {
                found = Match(nested[i].GetMethods(all));
                if (found != null) return found;
            }
            return null;
        }

        private static MethodBase Match(MethodInfo[] methods)
        {
            MethodBase hit = null;
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name.IndexOf(UseServiceMarker, StringComparison.Ordinal) < 0) continue;
                if (hit != null) return null;   // ambiguous -- refuse rather than guess
                hit = methods[i];
            }
            return hit;
        }

        /// <summary>Prefix on <c>_onUseService</c>: revives and heals every party Trainer's partners.
        ///
        /// <para>Deliberately parameterless. The stub is an async compiler-generated local function whose
        /// parameter and capture layout is not a stable contract, so nothing is read off it -- not the
        /// service type, not the purchasing character. What IS read is replicated game state
        /// (<see cref="TrainerPartnerPersistence.ReviveAndHealParty"/>), which is the only kind of read
        /// that can produce the same <c>CF_POKE_HP</c> writes on both peers.</para>
        ///
        /// <para>Consequence, stated plainly: ANY town service revives, including the boat ones, not just
        /// the INN/HEALER/PRIEST rest services. That is a wider trigger than the design's "a town revives
        /// it" -- but the seam it replaces was wider still (merely OPENING the services screen revived, for
        /// free, on one peer).</para></summary>
        public static void UseService_Prefix()
        {
            try
            {
                if (!ClassForgePlugin.FeaturesActive) return;
                TrainerPartnerPersistence.ReviveAndHealParty("town service used");
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] the town partner revive hook failed; town services continue "
                    + "unchanged: " + ex.Message);
            }
        }

        /// <summary>
        /// DEPRECATED no-op, kept only so an existing <c>ServiceMenuViewHelper.Show</c> registration
        /// cannot resurrect the bug it used to cause. Do not call
        /// <c>TrainerPartnerPersistence.ReviveAndHeal</c> from here.
        ///
        /// <para><c>Show</c> is a UI method that <c>AdventureDirector.cs:10292-10294</c> skips for a remote
        /// player, so reviving here wrote <c>CF_POKE_HP</c> on the owning peer only -- a hashed CustomData
        /// field diverging between peers, and a partner roster that differed between them on the next
        /// fight. The revive now lives on <see cref="UseService_Prefix"/>. See
        /// <see cref="TryPatchUseService"/> for the full argument.</para>
        /// </summary>
        public static void ServiceMenuShow_Postfix(Entity pEncounterEntity, Entity pCharacter, bool pViewOnly)
        {
        }

        private static void Warn(Exception ex)
        {
            ClassForgePlugin.Log.LogWarning(
                "[ClassForge] a Trainer partner HP mirror failed (fail-safe, combat continues and the "
                + "last stored value stands): " + ex.Message);
        }
    }
}

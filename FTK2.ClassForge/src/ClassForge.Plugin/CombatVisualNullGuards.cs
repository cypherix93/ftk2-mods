using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using IOG.dObjects;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Crash guards for the combat VISUAL SEQUENCE when an id has no baked art record — <c>dAbility</c>
    /// for abilities, <c>dStatusEffect</c> for statuses (see <see cref="AddStatusIcon_Prefix"/>, added
    /// after the same hazard shape froze a fight through the round-progression callback).
    ///
    /// <para><b>The bug class.</b> <c>dObjectIndexers.GetRecordByName</c> returns <b>null</b> for any id
    /// that is not in the baked ScriptableObject index (dObjectIndexers.cs:110-125 — the miss returns
    /// null, it does not throw and does not substitute a default). The combat view then dereferences the
    /// result unguarded. Confirmed sites:</para>
    /// <list type="bullet">
    ///   <item><c>CombatViewHelper.JoinAbilityHitEffectNode</c> — <c>pAbility.HitEffect</c>
    ///     (CombatViewHelper.cs:995) and <c>pAbility.AddEffect</c> (:999). No null check at all.</item>
    ///   <item><c>CombatViewHelper.CreateVisualSequence</c> WEAPON_ABILITY —
    ///     <c>characterAbilityRecord.AnimationIdentity</c> (CombatViewHelper.cs:184).</item>
    ///   <item><c>CombatViewHelper.CreateVisualSequence</c> TRIGGER_ABILITY —
    ///     <c>characterAbilityRecord.AnimationType</c> (CombatViewHelper.cs:320 region).</item>
    ///   <item><c>CharacterVisualHelper.renderAbilityFX</c> — <c>characterAbilityRecord.HitEffect</c>
    ///     (CharacterVisualHelper.cs:2568).</item>
    /// </list>
    ///
    /// <para><b>The crash this was written for (owner, live play, 2026-08-25).</b>
    /// <c>EnqueueReactionAnimations</c>'s <c>STAT_CHANGED</c> case reaches
    /// <c>GetCharacterAbilityRecord(statChangedData.AbilityName, …)</c> and hands the result straight to
    /// <c>JoinAbilityHitEffectNode</c> (CombatViewHelper.cs:1263-1264) whenever the branch at :1241 is
    /// taken — <c>Stat == "HP" &amp;&amp; Value &lt;= 0</c>, i.e. every HP loss. A ClassForge recipe
    /// STAT_CHANGE effect is attributed to <see cref="ConfigMergePatches.RecipeEffectAbilityId"/>
    /// (<c>CF_RECIPE_EFFECT</c>), which is registered as a <c>CombatAbilityConfig</c> in
    /// <c>Env.Configs.Abilities</c> but — correctly — has no art asset. So
    /// <c>SKILL_CF_VAMPIRIC_BLOOD_PRICE</c>'s self-damage produced a <c>STAT_CHANGED</c> whose
    /// <c>AbilityName</c> resolved to null and took the whole fight down with a
    /// <c>NullReferenceException</c> on the player's next click.</para>
    ///
    /// <para><b>Not a data bug.</b> <c>Env.Configs.Abilities</c> (JSON, <c>InteractableHelper
    /// .GetAbilityConfig</c>, InteractableHelper.cs:406-418) and <c>dObjectHelper.Index.dAbility</c>
    /// (baked ScriptableObjects) are two independent stores. <c>CF_RECIPE_EFFECT</c> is a synthetic
    /// attribution id with no animation and no impact FX of its own; the RIGHT outcome for it in the art
    /// store is "no record, therefore no FX". What was wrong was the engine turning that into a hard
    /// crash. No pack JSON references an art-less ability id, so there is nothing to correct in
    /// CF_PACK_ORIGINALS.</para>
    ///
    /// <para><b>Fail-safe shape.</b> Every guard here degrades to MISSING FX, never to a thrown
    /// exception, and never silently: the offending id is named once at Warning so the underlying data
    /// gap stays visible instead of being swallowed.</para>
    /// </summary>
    internal static class CombatVisualNullGuards
    {
        /// <summary>Ability ids already reported, so a per-frame visual path cannot flood the log.</summary>
        private static readonly HashSet<string> LoggedMissingAbility =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Suppressed reaction-animation failures, keyed by exception type + message.</summary>
        private static readonly HashSet<string> LoggedSuppressed = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Postfix on <c>CharacterVisualHelper.GetCharacterAbilityRecord(string, Entity, Thing)</c>
        /// (CharacterVisualHelper.cs:348-372) — OBSERVATION ONLY. It does not substitute a placeholder
        /// record: a stand-in <c>dAbility</c> would resolve non-null at the two
        /// <c>CreateVisualSequence</c> sites above and feed <c>AnimSequence</c> an animation type the
        /// actor has no clip for, trading a known crash for an unknown one. All this does is name the id
        /// that came back empty, once, which is the only place in the chain where the id is still in
        /// scope — <c>JoinAbilityHitEffectNode</c> receives the resolved record and never the string.
        /// </summary>
        public static void GetCharacterAbilityRecord_Postfix(string pAbilityName, dAbility __result)
        {
            if (__result != null || string.IsNullOrEmpty(pAbilityName)) return;
            try
            {
                lock (LoggedMissingAbility)
                {
                    if (!LoggedMissingAbility.Add(pAbilityName)) return;
                }
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] no dAbility art record for '" + pAbilityName + "' — its combat FX are " +
                    "skipped (guarded; the engine dereferences this record unchecked and would otherwise " +
                    "throw). Expected for synthetic attribution ids such as " +
                    ConfigMergePatches.RecipeEffectAbilityId + "; anything else is a missing art record.");
            }
            catch (Exception)
            {
                // Observation must never be the thing that throws.
            }
        }

        /// <summary>
        /// Prefix on <c>CombatViewHelper.JoinAbilityHitEffectNode</c> (CombatViewHelper.cs:993) — the
        /// method that actually throws. Returning false skips the body when there is no record, which is
        /// exactly what the body would have done for a record whose <c>HitEffect</c> and <c>AddEffect</c>
        /// are both null: nothing. Every caller is void-returning and appends into a node it already
        /// holds, so a skipped call costs the sequence only the missing FX —
        /// <c>EnqueueReactionAnimations</c> STAT_CHANGED (:1264), ABILITY_TARGET_VISUALS (:1754, whose
        /// record comes straight from <c>dObjectHelper.Index.dAbility.GetRecordByName</c> with no null
        /// check either), and the two <c>_damageApplied</c> sites (:2805, :3021).
        /// </summary>
        public static bool JoinAbilityHitEffectNode_Prefix(dAbility pAbility)
        {
            return pAbility != null;
        }

        /// <summary>Status ids already reported missing an icon record, so a per-frame UI refresh
        /// cannot flood the log. The freeze this guard fixes produced 60 NREs per SECOND.</summary>
        private static readonly HashSet<string> LoggedMissingStatus =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The name fragment Roslyn keeps for the <c>_addStatusIcon</c> local function. The
        /// full emitted name is <c>&lt;_refreshPortraitVisuals&gt;g___addStatusIcon|N</c> where N is a
        /// compiler-assigned ordinal, and the declaring type is a <c>&lt;&gt;c__DisplayClassNN_M</c>
        /// closure whose NN/M are equally compiler-assigned. Both drift on any edit anywhere in the
        /// file, so the ordinals are NEVER hard-coded — see <see cref="ResolveAddStatusIcon"/>.</summary>
        private const string AddStatusIconFragment = "g___addStatusIcon";

        /// <summary>Closure field holding the record that is dereferenced unguarded.</summary>
        private const string StatusRecordField = "statusRecord";

        /// <summary>Closure field holding the id that failed to resolve — the only place it is still
        /// in scope by the time the deref happens.</summary>
        private const string StatusNameField = "statusName";

        /// <summary>
        /// Resolves <c>CombatTimelineViewHelper2._refreshPortraitVisuals</c>'s <c>_addStatusIcon</c>
        /// local function by SCANNING for <see cref="AddStatusIconFragment"/> across the helper's
        /// compiler-generated nested types (and the helper itself, for the case where Roslyn emits the
        /// local function on the containing type instead of a closure class). Returns null — after
        /// logging — if the scan does not find exactly one match, or if the match's declaring type does
        /// not expose the two closure fields the prefix reads. Never guesses: a wrong target would
        /// silently disable the crash guard.
        /// </summary>
        internal static MethodInfo ResolveAddStatusIcon()
        {
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                           BindingFlags.Static | BindingFlags.Instance |
                                           BindingFlags.DeclaredOnly;
                var owner = typeof(CombatTimelineViewHelper2);
                var matches = new List<MethodInfo>();

                foreach (var m in owner.GetMethods(flags))
                    if (m.Name.IndexOf(AddStatusIconFragment, StringComparison.Ordinal) >= 0)
                        matches.Add(m);
                foreach (var nested in owner.GetNestedTypes(flags))
                    foreach (var m in nested.GetMethods(flags))
                        if (m.Name.IndexOf(AddStatusIconFragment, StringComparison.Ordinal) >= 0)
                            matches.Add(m);

                if (matches.Count != 1)
                {
                    ClassForgePlugin.Log.LogError(
                        "[ClassForge] status-icon crash guard DISABLED: scanning CombatTimelineViewHelper2 " +
                        "for a method named '*" + AddStatusIconFragment + "*' found " + matches.Count +
                        " matches, expected exactly 1. A status with no dStatusEffect record can freeze " +
                        "combat until this is re-resolved against the current game build.");
                    return null;
                }

                var target = matches[0];
                var declaring = target.DeclaringType;
                if (AccessTools.Field(declaring, StatusRecordField) == null ||
                    AccessTools.Field(declaring, StatusNameField) == null)
                {
                    ClassForgePlugin.Log.LogError(
                        "[ClassForge] status-icon crash guard DISABLED: resolved " + declaring.FullName +
                        "." + target.Name + " but it does not expose closure fields '" + StatusRecordField +
                        "' and '" + StatusNameField + "', which the guard reads. Re-resolve against the " +
                        "current game build.");
                    return null;
                }

                return target;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogError(
                    "[ClassForge] status-icon crash guard DISABLED: resolution threw (fail-safe): " + ex);
                return null;
            }
        }

        /// <summary>
        /// Prefix on <c>CombatTimelineViewHelper2._refreshPortraitVisuals</c>'s <c>_addStatusIcon</c>
        /// local function (CombatTimelineViewHelper2.cs:933-941) — the HARD FREEZE guard.
        ///
        /// <para><b>The freeze.</b> The portrait loop calls
        /// <c>dObjectHelper.Index.dStatusEffect.GetRecordByName(statusName)</c> for EVERY status on the
        /// combatant (:889, before any filtering) and <c>_addStatusIcon</c> then dereferences the result
        /// twice with no null check — <c>statusRecord.IconTexture</c> (:936) and
        /// <c>BindStatusTooltip(..., statusRecord.name, statusRecord.IconTexture, ...)</c> (:941). Any
        /// status whose id has no baked <c>dStatusEffect</c> asset therefore throws
        /// <c>NullReferenceException</c> out of the scheduled <c>_progressRound</c> callback, so the
        /// ROUND NEVER FINISHES PROGRESSING: no active character, no action menu, and the callback is
        /// retried every frame forever. Measured live 2026-08-26 on the Trainer's Focus Fire command
        /// (<c>STATUS_CHARGE_CF_FOCUS_FIRE</c>): ~60 NREs/second, Player.log growing at 94 KB/s, fight
        /// unrecoverable.</para>
        ///
        /// <para><b>Why the guard and not only the donor table.</b>
        /// <see cref="StatusVisualPatches"/> maps pack statuses onto vanilla icon donors, which is what
        /// makes them VISIBLE — but a donor table is data that can fall behind (it did: one entry
        /// against five pack statuses). The defect is the unguarded deref, and it fires for ANY id
        /// without an asset, including another mod's. Returning false skips just this one icon: the
        /// enclosing <c>for</c> loop keeps going, every other status still renders, and
        /// <c>iconIndex</c> is not consumed because the increment lives inside the skipped body. The
        /// worst case degrades from "round never progresses" to "one icon missing".</para>
        ///
        /// <para>Deliberately NOT a finalizer on <c>_refreshPortraitVisuals</c>: that would abandon the
        /// remaining statuses AND the post-loop element bookkeeping on the first bad id, where the
        /// prefix costs only the offending icon.</para>
        ///
        /// <para>Not fixed here, same defect, different method: <c>ToolTipHelper</c>'s BUFF/DEBUFF
        /// breakdown branch (ToolTipHelper.cs:763) also dereferences <c>GetRecordByName(...)
        /// .IconTexture</c> unguarded. It is reached only from a stat-breakdown tooltip hover, not from
        /// a scheduled round callback, so it cannot wedge a round.</para>
        /// </summary>
        public static bool AddStatusIcon_Prefix(dStatusEffect ___statusRecord, string ___statusName)
        {
            if (___statusRecord != null) return true;
            try
            {
                var id = string.IsNullOrEmpty(___statusName) ? "(unnamed)" : ___statusName;
                bool fresh;
                lock (LoggedMissingStatus) { fresh = LoggedMissingStatus.Add(id); }
                if (fresh)
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] no dStatusEffect icon record for status '" + id + "' — its timeline " +
                        "portrait icon is skipped (guarded; the engine dereferences this record unchecked " +
                        "and would otherwise throw out of the round-progression callback and freeze the " +
                        "fight). Add a donor for it to StatusVisualPatches.Donors to make it visible, or " +
                        "record it in StatusVisualPatches.IntentionallyIconless.");
            }
            catch (Exception)
            {
                // The guard must never be the thing that throws.
            }
            return false;
        }

        /// <summary>
        /// Finalizer on <c>CombatViewHelper.EnqueueReactionAnimations</c> (CombatViewHelper.cs:1208) —
        /// the backstop for the rest of the family. That method is a ~1000-line switch over
        /// <c>eAbilityResults</c> that indexes <c>pGameObjectMaps.FromCharacter[…]</c> and dereferences
        /// looked-up records in dozens of branches, all unguarded; it returns void and only appends nodes
        /// onto <c>pEngageNode</c>, so a suppressed throw leaves a partially-built reaction — degraded
        /// visuals — where today it kills the fight. A finalizer rather than a try/catch wrapper because
        /// Harmony runs finalizers even when the original throws; postfixes are skipped.
        ///
        /// <para>Deliberately NOT applied to <c>CreateVisualSequence</c>: that one RETURNS the sequence
        /// and its callers queue the value unchecked (CombatPhase.cs:744, :2098, :3796 and ~8 more), so
        /// swallowing there would hand the animation pump a null and move the crash rather than remove
        /// it.</para>
        /// </summary>
        /// <summary>
        /// Finalizer on <c>CombatPhase._clearTileRenderState</c> — the actor-map guard that lets a
        /// combatant with no model render as NOTHING instead of taking the fight down.
        ///
        /// <para><b>The line.</b> For each tile the method resolves the living occupant (CharacterComponent
        /// + CombatComponent + VenueComponent) and then does an UNCHECKED dictionary index:</para>
        /// <code>
        /// ActorGameObjectBase pActorGameObject =
        ///     ((entity2 != null) ? base._gameObjectMaps.FromCharacter[entity2] : null);
        /// </code>
        /// <para>Nothing verifies membership, so a combatant without a model throws
        /// <c>KeyNotFoundException</c> out of <c>_clearTileRenderState</c> and aborts the rest of
        /// <c>CombatPhase.Initialize</c> — which is why the visible symptom is the entire combat UI
        /// missing rather than one creature missing.</para>
        ///
        /// <para><b>Why a finalizer is now correct here, where it was previously rejected.</b> The old
        /// answer was <c>SummonLeakPatches.ClearTileRenderState_Prefix</c> DELETING the modelless
        /// combatant from <c>CombatState.Entities</c>, <c>RoundEntities</c> and <c>GameRun.Entities</c>.
        /// That is a local rendering condition mutating replicated state: two peers disagree about the
        /// roster, <c>VenueHelper.GetTargetableTiles(...).Count</c> differs, and
        /// <c>GameRandom.ShuffleList</c> (GameRandom.cs:222-236, exactly <c>Count</c> draws) pulls a
        /// different number of values off the SHARED stream inside <c>AIHelper.ForceAiDecision</c>
        /// (AIHelper.cs:508-511) on the very next AI turn — permanent divergence. Against that, the
        /// cost this finalizer accepts is small and strictly local: <c>_clearTileRenderState</c> only
        /// RESETS per-tile render state and returns void, so a suppressed throw leaves some tiles with
        /// stale highlight/outline state on ONE screen, while <c>Initialize</c> runs to completion and
        /// the grid, action menu and targeting all come up. Degraded highlighting on one peer beats an
        /// unplayable fight, and both beat a desync.</para>
        ///
        /// <para>This is the BACKSTOP, not the plan: <c>ClearTileRenderState_Prefix</c> first builds the
        /// real actor and then a placeholder one, and logs an error naming the entity's ordinal if both
        /// fail. Reaching this finalizer means that ladder was exhausted.</para>
        ///
        /// <para>Deliberately NOT applied to <c>CombatViewHelper.GetTargetHighlights</c>, which has the
        /// identical defect but RETURNS the highlight collection: swallowing there would hand its
        /// callers a null and move the crash instead of removing it.</para>
        /// </summary>
        public static Exception ClearTileRenderState_Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            try
            {
                var key = "_clearTileRenderState|" + __exception.GetType().Name + "|" + (__exception.Message ?? "");
                bool fresh;
                lock (LoggedSuppressed) { fresh = LoggedSuppressed.Add(key); }
                if (fresh)
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] _clearTileRenderState threw while resolving a combatant's 3D model " +
                        "and was suppressed — some tiles keep stale render state, but CombatPhase.Initialize " +
                        "completes and the fight is playable. The combatant is deliberately NOT removed " +
                        "from the roster (that would desync the shared combat RNG); see the ClassForge " +
                        "error line naming its ordinal: " + __exception);
            }
            catch (Exception)
            {
                // Even the logging path must never throw out of a finalizer.
            }
            return null;
        }

        public static Exception EnqueueReactionAnimations_Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            try
            {
                var key = __exception.GetType().Name + "|" + (__exception.Message ?? "");
                bool fresh;
                lock (LoggedSuppressed) { fresh = LoggedSuppressed.Add(key); }
                if (fresh)
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] a combat reaction animation failed and was suppressed — the fight " +
                        "continues with that FX missing (fail-safe; without this the exception escapes " +
                        "CreateVisualSequence and ends the encounter): " + __exception);
            }
            catch (Exception)
            {
                // Even the logging path must never throw out of a finalizer.
            }
            return null;
        }
    }
}

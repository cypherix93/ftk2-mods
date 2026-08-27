using System;

namespace ClassForge.Plugin
{
    /// <summary>
    /// The one hook that makes a Trainer partner's nickname actually render (test-checklist §L).
    ///
    /// <para><b>What draws the visible name.</b> Every name text in this game is a UI Toolkit
    /// <c>Label</c> — there is no TextMeshPro in the assembly — and every one of them resolves through
    /// <c>CharacterHelper.GetDisplayName(CharacterComponent, bool)</c> (<c>CharacterHelper.cs:1653</c>):
    /// <code>
    /// if (string.IsNullOrEmpty(pCharacter.DisplayName))
    ///     return pDoLoc ? Lang.__t(GetLocConfigName(pCharacter.ConfigName))
    ///                   : GetLocConfigName(pCharacter.ConfigName);
    /// return pCharacter.DisplayName;
    /// </code>
    /// The two consumers that matter for a partner both funnel into it:
    /// <c>CharacterCombatHudHelper.UpdateHUDFromCharacter</c> sets the over-head nameplate
    /// <c>"hud-label"</c> from <c>CharacterHelper.GetDisplayNameForUI(pEntity)</c>
    /// (<c>CharacterCombatHudHelper.cs:143-144</c>), and <c>GetDisplayNameForUI</c> computes that name by
    /// calling <c>GetDisplayName(pComponent, pDoLoc: true)</c> (<c>CharacterHelper.cs:1695</c>);
    /// <c>CombatDetailViewHelper.cs:181</c> (the inspect panel's <c>"hud-label"</c>) calls
    /// <c>GetDisplayName(pEntity, pDoLoc: true)</c> which delegates to the same overload
    /// (<c>CharacterHelper.cs:1643-1650</c>). So ONE postfix on the <c>CharacterComponent</c> overload
    /// covers the nameplate, the inspect panel, the turn-order tooltip
    /// (<c>CombatTimelineViewHelper2.cs:836</c>) and the party HUD.</para>
    ///
    /// <para><b>Why a read patch and not a write at summon time.</b> Setting
    /// <c>CharacterComponent.DisplayName</c> once would also work — the field is a plain
    /// <c>public string</c> (<c>CharacterComponent.cs:6</c>) and the resolver short-circuits on it — but
    /// the only place in this plugin that sees a partner being sent out is
    /// <c>RecipeActionExecutor.ExecSummon</c>, and the name is re-read on every HUD refresh anyway
    /// (nothing caches it; <c>CombatPhase._refreshPlayerHuds</c>, <c>CombatPhase.cs:5184</c>, is called
    /// after essentially every state change). Patching the read is both fewer moving parts and immune to
    /// anything else that rewrites <c>DisplayName</c> — e.g.
    /// <c>CharacterHelper.TryProgressCompanionEntityToLevel</c> (<c>CharacterHelper.cs:2100</c>).</para>
    ///
    /// <para><b>Why the nickname survives the localization round-trip.</b> The nameplate passes the
    /// resolved name through <c>Lang.__t</c> for any non-player character
    /// (<c>CharacterCombatHudHelper.cs:144</c>) — i.e. it is used as a loc KEY. <c>Lang.__t</c> returns
    /// the key unchanged on a miss (<c>Lang.cs:157</c>), so a literal nickname renders verbatim. That is
    /// exactly why <see cref="TrainerPartnerNicknames.Sanitize"/> is mandatory: the same method runs
    /// <c>Regex.Replace</c> over <c>{0}</c>..<c>{5}</c> (<c>Lang.cs:121-141</c>) and the labels are
    /// rich-text.</para>
    ///
    /// <para><b>Cost to every other character: one null check.</b> The body returns immediately unless the
    /// component carries a non-null <c>CustomData</c> holding <c>SUMMONED_BY</c>
    /// (<c>CombatHelper.cs:2267</c>) — which is only ever true of a summoned creature — and then only a
    /// creature traceable to an <c>ARM_ORIG_TRAINER_BALL_*</c> item resolves to anything. Nothing here can
    /// reach a player, an enemy, or another class's summon.</para>
    ///
    /// <para>Fully guarded: this sits on the hottest UI path in the game, so any failure is swallowed and
    /// the vanilla name stands.</para>
    /// </summary>
    public static class TrainerNicknamePatches
    {
        /// <summary>
        /// Postfix on <c>CharacterHelper.GetDisplayName(CharacterComponent pCharacter, bool pDoLoc)</c>
        /// (<c>CharacterHelper.cs:1653</c>). Replaces the result with the partner's nickname when one is
        /// stored on its ball, and otherwise leaves <paramref name="__result"/> untouched — so an unset or
        /// blank nickname falls back to the normal creature name, never to blank.
        /// </summary>
        public static void GetDisplayName_Postfix(CharacterComponent pCharacter, ref string __result)
        {
            try
            {
                var nickname = TrainerPartnerNicknames.ResolveForSummon(pCharacter);
                if (string.IsNullOrEmpty(nickname)) return;
                __result = nickname;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] a Trainer partner nickname could not be applied to its nameplate "
                    + "(fail-safe, the creature's normal name is shown): " + ex.Message);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine.UIElements;

namespace ClassForge.Plugin
{
    /// <summary>
    /// The Pokemon Trainer's MINION PANEL — the three partners, shown in the class info tab
    /// (test-checklist §L: "Need to come up with a way to show the three minions on the UI for the class
    /// as well in the info tab").
    ///
    /// <para><b>What the info tab is, and how it is built.</b> One file builds it:
    /// <c>CharacterCustomizationViewHelper.RenderStatsContainer(Entity pEntity, string pConfigName,
    /// VisualElement pItemCard)</c> (<c>CharacterCustomizationViewHelper.cs:1001</c>). It opens the
    /// <c>"stats-container"</c> submenu, writes the class title from <c>Lang.__t(pConfigName)</c>
    /// (<c>:1007</c>) and the class body text into the <c>"class-description-text"</c> Label
    /// (<c>:1008</c>). It is called from three places, which between them are every way the tab opens:
    /// <c>RenderCustomizationContainer</c> (<c>:343</c>), the class TextSelector's focus handler
    /// (<c>:454</c>) and the class-list selection change (<c>:1619</c>). The screen itself is entered
    /// through <c>PartyManagementDirector.Initialize</c> (<c>PartyManagementDirector.cs:214</c>), so this
    /// is the same panel at character creation AND from party management mid-run — which is what makes it
    /// the right home for live partner state.</para>
    ///
    /// <para><b>Why the description Label and not extra rows.</b> The tab's skill and item rows are
    /// FIXED-COUNT templates whose surplus is hidden with <c>DisplayStyle.None</c>
    /// (<c>CharacterCustomizationViewHelper.cs:1123-1128</c>, <c>:1152</c>) — and this plugin's
    /// <see cref="SkillDisplayPatches"/> already competes for those same spare rows. The description Label
    /// has no such limit: it is a wrapping multi-line text element the game itself grows to fit
    /// (<c>UIToolkitHelper.ProcessAsianMultilineText</c>, <c>UIToolkitHelper.cs:1212</c>), sitting beside
    /// the <c>"stats-scrollview"</c> the panel resolves at <c>:1009</c>. Appending to it is the same
    /// idiom the game uses for its own dynamic lines — the HONEYBEE cooldown block reads the existing
    /// <c>"description-label"</c> text, prepends to it and writes it back
    /// (<c>ItemCardViewHelper.cs:1438-1465</c>), as do the fishing-rod (<c>:1467-1474</c>) and
    /// quest-delivery (<c>:1431</c>) blocks.</para>
    ///
    /// <para><b>Known limitation, stated plainly.</b> For Chinese and Japanese,
    /// <c>ProcessAsianMultilineText</c> is <c>async void</c> and assigns the Label at
    /// <c>UIToolkitHelper.cs:1265</c> AFTER awaiting a layout pass — i.e. after this postfix has run — so
    /// the block is overwritten and the panel does not show. Every other language takes the synchronous
    /// branch at <c>UIToolkitHelper.cs:1214-1217</c> (<c>pLabel.text = pText; return;</c>) and the append
    /// stands. That is logged rather than worked around.</para>
    ///
    /// <para><b>Scope.</b> Renders only when the viewed class is the character's OWN class and that
    /// character actually carries an <c>ARM_ORIG_TRAINER_BALL_*</c> item — the same single discriminator
    /// <see cref="TrainerPartnerPersistence"/> uses. Browsing any other class in the list, or viewing any
    /// character without a ball, leaves the vanilla panel byte-identical.</para>
    ///
    /// <para><b>Reads state, never duplicates it.</b> Every value comes from
    /// <see cref="TrainerPartnerPersistence.Records"/>, which recomputes from the ball items on each
    /// read.</para>
    /// </summary>
    public static class TrainerPartnerPanel
    {
        /// <summary>
        /// Marks the start of our block inside the Label so a re-render truncates the previous one
        /// instead of stacking duplicates.
        /// </summary>
        private const string Marker = "\n\n— PARTNERS —";

        internal static ConfigEntry<bool> Enable;

        internal static void Bind(ConfigFile config)
        {
            Enable = config.Bind("Trainer", "EnablePartnerPanel", true,
                "Append the Pokemon Trainer's partner roster -- name/nickname, line, tier and the "
                + "persisted HP / DOWNED state of each ball -- to the class info tab's description text. "
                + "Reads TrainerPartnerPersistence.Records, which is recomputed from the ball items on "
                + "every read, so it can never disagree with the stored state. Renders only when the "
                + "viewed class is the character's own AND that character carries an "
                + "ARM_ORIG_TRAINER_BALL_* item; every other class's info tab is untouched. Off = the "
                + "vanilla panel, exactly as before.");
        }

        /// <summary>
        /// The exact text this panel last pushed into the Label, or "" if it has not rendered. A test can
        /// assert the panel's RENDERED content without the HUD — this is read back from the live
        /// <c>VisualElement</c> after the write, not from the string we intended to write.
        /// </summary>
        public static string LastRenderedText { get; private set; } = "";

        /// <summary>
        /// Postfix on <c>CharacterCustomizationViewHelper.RenderStatsContainer(Entity, string,
        /// VisualElement)</c> (<c>CharacterCustomizationViewHelper.cs:1001</c>). The vanilla body has just
        /// assigned the description Label at <c>:1008</c>, so appending here composes with it rather than
        /// racing it.
        /// </summary>
        public static void RenderStatsContainer_Postfix(Entity pEntity, string pConfigName)
        {
            try
            {
                if (!ClassForgePlugin.FeaturesActive) return;
                if (Enable == null || !Enable.Value) return;
                if (pEntity == null || string.IsNullOrEmpty(pConfigName)) return;

                CharacterComponent cc;
                if (!pEntity.TryGet<CharacterComponent>(out cc) || cc == null) return;

                // Only the character's OWN class page. Browsing another class in the list must not show
                // this character's partners under someone else's heading.
                if (!string.Equals(cc.ConfigName, pConfigName, StringComparison.Ordinal)) return;

                var block = BuildBlock(pEntity, cc);
                if (block == null) return;                    // carries no Trainer ball -> not our class

                var root = AccessTools.StaticFieldRefAccess<VisualElement>(
                    typeof(CharacterCustomizationViewHelper), "_root");
                var statsContainer = root != null ? root.Q("stats-container") : null;
                var label = statsContainer != null ? statsContainer.Q<Label>("class-description-text") : null;
                if (label == null)
                {
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] partner panel: the info tab has no 'class-description-text' label, "
                        + "so the roster cannot be shown (fail-safe, the vanilla panel is intact).");
                    return;
                }

                // Truncate any block we appended on a previous render before appending this one.
                var baseText = label.text ?? "";
                int prior = baseText.IndexOf(Marker, StringComparison.Ordinal);
                if (prior >= 0) baseText = baseText.Substring(0, prior);

                label.text = baseText + block;

                // Read the rendered text back OFF the live element -- not off the string we built -- so a
                // test asserts what the panel actually holds.
                LastRenderedText = label.text ?? "";

                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] partner panel rendered into the class info tab for " + pConfigName
                    + ": " + block.Replace("\n", " / ").Trim());
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] the Trainer partner panel failed to render (fail-safe, the vanilla "
                    + "class info tab is intact): " + ex.Message);
            }
        }

        /// <summary>
        /// The roster block for one character, or null when it carries no Trainer ball. One line per
        /// ball: nickname-or-creature-name, line, tier, and the persisted HP / DOWNED state.
        /// </summary>
        private static string BuildBlock(Entity owner, CharacterComponent cc)
        {
            string ownerGuid;
            try { ownerGuid = owner.Guid ?? ""; } catch (Exception) { return null; }

            // Order the lines by the ball items the character actually holds, so a partner never bought
            // is still listed as an empty slot rather than silently missing.
            var balls = new List<string>();
            if (cc.Things != null)
            {
                for (int i = 0; i < cc.Things.Count; i++)
                {
                    var t = cc.Things[i];
                    if (t == null || t.ConfigName == null) continue;
                    if (t.ConfigName.StartsWith(TrainerPartnerPersistence.BallPrefix, StringComparison.Ordinal)
                        && !balls.Contains(t.ConfigName))
                        balls.Add(t.ConfigName);
                }
            }
            if (balls.Count == 0) return null;

            var byBall = new Dictionary<string, TrainerPartnerPersistence.PartnerRecord>(StringComparer.Ordinal);
            foreach (var rec in TrainerPartnerPersistence.Records)
            {
                if (rec == null || rec.Ball == null) continue;
                if (!string.Equals(rec.Owner, ownerGuid, StringComparison.Ordinal)) continue;
                byBall[rec.Ball] = rec;
            }

            var sb = new StringBuilder();
            sb.Append(Marker);
            foreach (var ballConfig in balls)
            {
                sb.Append('\n').Append(LineFor(ballConfig,
                    byBall.ContainsKey(ballConfig) ? byBall[ballConfig] : null));
            }
            return sb.ToString();
        }

        /// <summary>One roster line. Never throws; an unreadable field degrades to a dash.</summary>
        private static string LineFor(string ballConfig, TrainerPartnerPersistence.PartnerRecord rec)
        {
            var line = TrainerPartnerNicknames.LineOf(ballConfig);

            if (rec == null || rec.MaxHp < 1 || string.IsNullOrEmpty(rec.Config))
                return line + " — empty (no partner sent out yet)";

            var name = NameFor(ballConfig, rec.Config);
            var tier = rec.Stage > 0
                ? "Tier " + rec.Stage.ToString(CultureInfo.InvariantCulture)
                : "Tier -";
            var health = rec.Downed || rec.CurrentHp < 1
                ? "DOWNED (only a town revives it)"
                : "HP " + rec.CurrentHp.ToString(CultureInfo.InvariantCulture)
                  + "/" + rec.MaxHp.ToString(CultureInfo.InvariantCulture);

            return name + " (" + line + ") — " + tier + " — " + health
                   + (rec.OnBoard ? " — in play" : "");
        }

        /// <summary>
        /// The partner's nickname if it has one, else the creature's own localized name.
        /// <c>CharacterHelper.GetLocConfigName</c> (<c>CharacterHelper.cs:1629</c>) maps a character
        /// config id to its <c>LocKey</c>, and <c>Lang.__t</c> returns the key unchanged on a miss
        /// (<c>Lang.cs:157</c>) — so this degrades to a readable id, never to blank.
        /// </summary>
        private static string NameFor(string ballConfig, string creatureConfig)
        {
            try
            {
                var owner = FindBall(ballConfig);
                var nickname = owner != null ? TrainerPartnerNicknames.ForBall(owner) : null;
                if (!string.IsNullOrEmpty(nickname)) return nickname;
            }
            catch (Exception) { /* fall through to the creature's own name */ }

            try
            {
                var localized = Lang.__t(CharacterHelper.GetLocConfigName(creatureConfig));
                if (!string.IsNullOrEmpty(localized)) return localized;
            }
            catch (Exception) { }
            return creatureConfig;
        }

        /// <summary>The ball Thing with this config anywhere in the run, or null.</summary>
        private static Thing FindBall(string ballConfig)
        {
            var run = RouterHelper.Env != null ? RouterHelper.Env.GameRun : null;
            var entities = run != null ? run.Entities : null;
            if (entities == null) return null;
            foreach (var e in entities)
            {
                if (e == null) continue;
                CharacterComponent cc;
                if (!e.TryGet<CharacterComponent>(out cc) || cc == null || cc.Things == null) continue;
                for (int i = 0; i < cc.Things.Count; i++)
                {
                    var t = cc.Things[i];
                    if (t != null && string.Equals(t.ConfigName, ballConfig, StringComparison.Ordinal))
                        return t;
                }
            }
            return null;
        }
    }
}

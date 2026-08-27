using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Player-chosen names for the Pokemon Trainer's partners (test-checklist §L: "I would also like to
    /// nickname the minions").
    ///
    /// <para><b>Where the nickname lives.</b> Exactly where every other piece of partner state lives —
    /// <c>Thing.CustomData</c> on that partner's <c>ARM_ORIG_TRAINER_BALL_*</c> item, under the key
    /// <see cref="KeyNickname"/>, written and read through <c>CoreHelper.SetCustomData</c> /
    /// <c>CoreHelper.TryGetCustomData</c> (<c>CoreHelper.cs:1646</c>, <c>:1696</c>). This deliberately
    /// reuses <see cref="TrainerPartnerPersistence"/>'s store rather than opening a second one: the ball
    /// is already an ordinary <c>Thing</c> in the Trainer's <c>CharacterComponent.Things</c>, so the
    /// nickname rides the existing save schema and survives exactly as long as the ball does.</para>
    ///
    /// <para><b>How a live creature is traced back to its ball.</b> A summoned partner is not a follower,
    /// so it carries no owner pointer of its own — except one the engine writes for us.
    /// <c>CombatHelper.cs:2267</c> tags every non-<c>AS_FOLLOWER</c> summon with
    /// <c>CoreHelper.SetCustomData(characterComponent2, "SUMMONED_BY", pOrigin.Guid)</c>. The Trainer's
    /// partners are <c>SPECIFIC</c> summons, so they take that branch (the <c>COMPANION</c> +
    /// <c>AS_FOLLOWER</c> branch above it, <c>CombatHelper.cs:2242-2265</c>, is the follower path this
    /// class deliberately does not use — see §L2). Given <c>SUMMONED_BY</c> we have the summoner's GUID,
    /// and <see cref="TrainerPartnerPersistence.KeyConfig"/> on each ball records which creature config
    /// that ball last sent out — so (owner GUID, creature config) identifies the ball uniquely.</para>
    ///
    /// <para><b>Sanitisation is not optional here.</b> The combat nameplate does
    /// <c>Lang.__t(displayNameForUI)</c> for any non-player character
    /// (<c>CharacterCombatHudHelper.cs:143-144</c>), i.e. the name is used AS A LOCALIZATION KEY. A miss
    /// returns the key unchanged (<c>Lang.cs:157</c>), which is what makes a literal nickname work at all
    /// — but <c>Lang.__t</c> also runs <c>Regex.Replace</c> for <c>{0}</c>..<c>{5}</c> placeholders
    /// (<c>Lang.cs:121-141</c>) and the labels are rich-text, so braces and angle brackets must never
    /// reach it. <see cref="Sanitize"/> keeps letters, digits, space, apostrophe, hyphen and period and
    /// drops everything else, then caps the length. Anything that sanitises away to nothing is treated as
    /// "no nickname" and the creature keeps its normal name — never a blank nameplate.</para>
    /// </summary>
    public static class TrainerPartnerNicknames
    {
        /// <summary>The player-chosen name for the partner this ball sends out.</summary>
        public const string KeyNickname = "CF_POKE_NICKNAME";

        /// <summary>The engine's own summoner back-pointer (<c>CombatHelper.cs:2267</c>).</summary>
        private const string KeySummonedBy = "SUMMONED_BY";

        /// <summary>Longest nickname the nameplate is allowed to carry.</summary>
        public const int MaxLength = 20;

        // ---- knobs ----

        /// <summary>
        /// Master switch. Off = partners keep their config names everywhere and no name patch does any
        /// work beyond one null check. Lives in the existing <c>[Trainer]</c> section.
        /// </summary>
        internal static ConfigEntry<bool> Enable;

        /// <summary>
        /// Fallback names per line, e.g. <c>GRASS=Sparky,WATER=Bubbles,FIRE=Blaze</c>. Used only when the
        /// ball itself carries no stored nickname, so anything set through
        /// <see cref="SetNickname"/> always wins. This is the player-facing way to choose names without a
        /// bespoke in-game text field — see the note on the panel for why no input widget ships here.
        /// </summary>
        internal static ConfigEntry<string> DefaultNames;

        internal static void Bind(ConfigFile config)
        {
            Enable = config.Bind("Trainer", "EnablePartnerNicknames", true,
                "Let a Pokemon Trainer partner carry a player-chosen name, shown on its nameplate and "
                + "portrait instead of the generic creature name. Stored in Thing.CustomData under "
                + "CF_POKE_NICKNAME on that partner's ARM_ORIG_TRAINER_BALL_* item, the same store the "
                + "rest of the partner state uses. Scoped entirely to creatures summoned by a character "
                + "carrying a Trainer ball -- no other class's nameplate is read or written. Off = every "
                + "creature keeps its config name, exactly as before.");

            DefaultNames = config.Bind("Trainer", "DefaultPartnerNicknames", "",
                "Comma-separated per-line fallback nicknames, e.g. 'GRASS=Sparky,WATER=Bubbles,"
                + "FIRE=Blaze'. Used only when that ball has no nickname stored on it, so a name set in "
                + "game always wins. Entries are sanitised the same way as stored names (letters, "
                + "digits, space, apostrophe, hyphen and period only, capped at " + MaxLength
                + " characters); anything that sanitises away to nothing is ignored and the creature "
                + "keeps its normal name. Blank = no fallback.");
        }

        private static bool Active
        {
            get { return ClassForgePlugin.FeaturesActive && Enable != null && Enable.Value; }
        }

        // =====================================================================================
        // sanitisation
        // =====================================================================================

        /// <summary>
        /// The one gate every nickname passes, on write AND on read. Returns null for anything that is
        /// empty, or that sanitises away to nothing — callers treat null as "no nickname", which is what
        /// makes the fallback to the normal creature name total.
        /// </summary>
        public static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var sb = new StringBuilder(raw.Length);
            bool lastWasSpace = false;
            for (int i = 0; i < raw.Length && sb.Length < MaxLength; i++)
            {
                char c = raw[i];
                bool keep = char.IsLetterOrDigit(c) || c == ' ' || c == '\'' || c == '-' || c == '.';
                if (!keep) continue;                      // drops < > { } newlines, controls, %, &, ...
                if (c == ' ')
                {
                    if (lastWasSpace || sb.Length == 0) continue;   // no leading or doubled spaces
                    lastWasSpace = true;
                }
                else lastWasSpace = false;
                sb.Append(c);
            }
            var cleaned = sb.ToString().TrimEnd();
            return cleaned.Length == 0 ? null : cleaned;
        }

        // =====================================================================================
        // write
        // =====================================================================================

        /// <summary>
        /// Name the partner held by <paramref name="ballConfig"/> on <paramref name="owner"/>. Pass a null
        /// or blank <paramref name="raw"/> to clear the nickname and go back to the creature's normal
        /// name. Returns the stored (sanitised) name, or null when it was cleared or nothing matched.
        /// </summary>
        /// <remarks>
        /// <b>MULTIPLAYER — read before wiring this to anything.</b> It has NO callers today: nicknames
        /// currently come entirely from the <see cref="DefaultNames"/> config knob, which is read-only and
        /// never touches the save. That is why the feature is parity-exempt presentation
        /// (docs/MULTIPLAYER.md R4) and why <see cref="Enable"/>/<see cref="DefaultNames"/> are bound
        /// without a <c>ParityClass</c>.
        /// <para>This method breaks that, because it WRITES. <c>CF_POKE_NICKNAME</c> lands in
        /// <c>Thing.CustomData</c>, <c>Thing</c> is inside <c>GameRunData</c>, and <c>CustomData</c> is
        /// absent from <c>NetworkDebuggingHelper._ignorePropertyNames</c>
        /// (<c>NetworkDebuggingHelper.cs:40-43</c>) — so it is inside the vendor's desync MD5. Wiring this
        /// to an in-game text field or a console command would run it on ONE peer, and the very next
        /// sync-check checkpoint would report a desync over a cosmetic name. If it is ever wired up, the
        /// write must reach every peer (a versioned <c>_SYNC_</c> action, or a value every peer derives
        /// from already-replicated state) before it is allowed to touch <c>CustomData</c>.</para>
        /// <para>The sanitised value itself is safe to hash — it is culture-invariant and byte-identical
        /// given the same input. The hazard is purely that only one peer would have that input.</para>
        /// </remarks>
        public static string SetNickname(Entity owner, string ballConfig, string raw)
        {
            if (!Active) return null;
            try
            {
                if (owner == null || string.IsNullOrEmpty(ballConfig)) return null;
                if (!ballConfig.StartsWith(TrainerPartnerPersistence.BallPrefix, StringComparison.Ordinal))
                    return null;

                CharacterComponent cc;
                if (!owner.TryGet<CharacterComponent>(out cc) || cc == null || cc.Things == null) return null;

                Thing ball = null;
                for (int i = 0; i < cc.Things.Count; i++)
                {
                    var t = cc.Things[i];
                    if (t != null && string.Equals(t.ConfigName, ballConfig, StringComparison.Ordinal))
                    { ball = t; break; }
                }
                if (ball == null)
                {
                    ClassForgePlugin.Log.LogDebug(
                        "[ClassForge] nickname NOT set: this character carries no " + ballConfig + ".");
                    return null;
                }

                var clean = Sanitize(raw);
                CoreHelper.SetCustomData(ball, KeyNickname, clean ?? "");
                _cache.Clear();

                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] partner nickname: ball=" + ballConfig + " nickname="
                    + (clean ?? "(cleared, back to the creature's own name)")
                    + " raw=" + (string.IsNullOrEmpty(raw) ? "(empty)" : raw));
                return clean;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] a Trainer partner nickname could not be stored (fail-safe, the partner "
                    + "keeps whatever name it had): " + ex.Message);
                return null;
            }
        }

        // =====================================================================================
        // read
        // =====================================================================================

        /// <summary>
        /// (owner GUID + "|" + creature config) -> nickname, or "" for a known miss. Nicknames only ever
        /// change through <see cref="SetNickname"/>, which clears this; the cache exists because
        /// <c>CharacterHelper.GetDisplayName</c> is re-read on every HUD refresh
        /// (<c>CombatPhase._refreshPlayerHuds</c>, <c>CombatPhase.cs:5184</c>) and must stay cheap.
        /// </summary>
        private static readonly Dictionary<string, string> _cache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Drop the memo — call after anything that could rewrite a ball's CustomData.</summary>
        public static void ClearCache()
        {
            if (_cache.Count != 0) _cache.Clear();
        }

        /// <summary>
        /// The nickname for a LIVE summoned creature, or null if it has none (or is not a Trainer
        /// partner at all). This is what the nameplate patch calls.
        /// </summary>
        internal static string ResolveForSummon(CharacterComponent summoned)
        {
            if (!Active) return null;
            try
            {
                if (summoned == null || summoned.CustomData == null) return null;   // cheap gate first
                string ownerGuid;
                if (!CoreHelper.TryGetCustomData(summoned, KeySummonedBy, out ownerGuid)
                    || string.IsNullOrEmpty(ownerGuid)) return null;
                string config = summoned.ConfigName;
                if (string.IsNullOrEmpty(config)) return null;

                var key = ownerGuid + "|" + config;
                string cached;
                if (_cache.TryGetValue(key, out cached))
                    return cached.Length == 0 ? null : cached;

                string resolved = Lookup(ownerGuid, config);
                _cache[key] = resolved ?? "";
                return resolved;
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] a Trainer partner nickname could not be resolved (fail-safe, the "
                    + "creature keeps its normal name): " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Find the ball on <paramref name="ownerGuid"/> that last sent out <paramref name="config"/> and
        /// read its nickname. Falls back to the per-line default from
        /// <see cref="DefaultNames"/> when the ball itself carries none.
        /// </summary>
        private static string Lookup(string ownerGuid, string config)
        {
            var run = RouterHelper.Env != null ? RouterHelper.Env.GameRun : null;
            var entities = run != null ? run.Entities : null;
            if (entities == null) return null;

            foreach (var e in entities)
            {
                if (e == null) continue;
                string guid;
                try { guid = e.Guid; } catch (Exception) { continue; }
                if (!string.Equals(guid, ownerGuid, StringComparison.Ordinal)) continue;

                CharacterComponent cc;
                if (!e.TryGet<CharacterComponent>(out cc) || cc == null || cc.Things == null) return null;

                for (int i = 0; i < cc.Things.Count; i++)
                {
                    var t = cc.Things[i];
                    if (t == null || t.ConfigName == null) continue;
                    if (!t.ConfigName.StartsWith(TrainerPartnerPersistence.BallPrefix, StringComparison.Ordinal))
                        continue;

                    string stored;
                    if (!CoreHelper.TryGetCustomData(t, TrainerPartnerPersistence.KeyConfig, out stored)) continue;
                    if (!string.Equals(stored, config, StringComparison.Ordinal)) continue;

                    return ForBall(t);
                }
                return null;                                   // right owner, no ball holds this creature
            }
            return null;
        }

        /// <summary>
        /// The effective nickname for one ball: its stored name, else the per-line default, else null.
        /// Always sanitised on the way out, so a hand-edited save can never push markup into the HUD.
        /// </summary>
        public static string ForBall(Thing ball)
        {
            if (ball == null || ball.ConfigName == null) return null;
            string stored;
            if (CoreHelper.TryGetCustomData(ball, KeyNickname, out stored))
            {
                var clean = Sanitize(stored);
                if (clean != null) return clean;
            }
            return DefaultFor(LineOf(ball.ConfigName));
        }

        /// <summary>"ARM_ORIG_TRAINER_BALL_GRASS" -> "GRASS".</summary>
        public static string LineOf(string ballConfig)
        {
            if (string.IsNullOrEmpty(ballConfig)) return "";
            return ballConfig.StartsWith(TrainerPartnerPersistence.BallPrefix, StringComparison.Ordinal)
                ? ballConfig.Substring(TrainerPartnerPersistence.BallPrefix.Length)
                : ballConfig;
        }

        /// <summary>The configured fallback name for a line, or null.</summary>
        private static string DefaultFor(string line)
        {
            if (string.IsNullOrEmpty(line) || DefaultNames == null) return null;
            var raw = DefaultNames.Value;
            if (string.IsNullOrEmpty(raw)) return null;
            foreach (var pair in raw.Split(','))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(pair.Substring(0, eq).Trim(), line, StringComparison.OrdinalIgnoreCase))
                    continue;
                return Sanitize(pair.Substring(eq + 1));
            }
            return null;
        }

        // =====================================================================================
        // read-back for tests
        // =====================================================================================

        /// <summary>
        /// Every Trainer ball in the run with its effective nickname, in the same shape as
        /// <see cref="TrainerPartnerPersistence.StateSummary"/> — a readable path that proves a nickname
        /// is STORED without needing the HUD. Example:
        /// <c>nicknames=3 | ARM_ORIG_TRAINER_BALL_GRASS=Sparky ; ARM_ORIG_TRAINER_BALL_WATER=(none)</c>.
        /// </summary>
        public static string StateSummary
        {
            get
            {
                var sb = new StringBuilder();
                int count = 0;
                try
                {
                    var run = RouterHelper.Env != null ? RouterHelper.Env.GameRun : null;
                    var entities = run != null ? run.Entities : null;
                    if (entities != null)
                    {
                        foreach (var e in entities)
                        {
                            if (e == null) continue;
                            CharacterComponent cc;
                            if (!e.TryGet<CharacterComponent>(out cc) || cc == null || cc.Things == null) continue;
                            for (int i = 0; i < cc.Things.Count; i++)
                            {
                                var t = cc.Things[i];
                                if (t == null || t.ConfigName == null) continue;
                                if (!t.ConfigName.StartsWith(TrainerPartnerPersistence.BallPrefix,
                                        StringComparison.Ordinal)) continue;
                                sb.Append(count == 0 ? " | " : " ; ")
                                  .Append(t.ConfigName).Append('=').Append(ForBall(t) ?? "(none)");
                                count++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] partner nicknames could not be read back: " + ex.Message);
                }
                return "nicknames=" + count.ToString(CultureInfo.InvariantCulture) + sb;
            }
        }
    }
}

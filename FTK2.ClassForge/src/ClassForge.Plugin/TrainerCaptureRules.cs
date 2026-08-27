using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace ClassForge.Plugin
{
    /// <summary>
    /// The eligibility gate for the <c>CAPTURE</c> verb, and the read side of the captured-monster record.
    ///
    /// <para><b>Why this is a code unit and not four authored conditions.</b> Three of the four rules below
    /// cannot be expressed as recipe conditions at all, and each of them is load-bearing — a missing one is
    /// a hard failure inside the GAME's own stack frame, outside every mod try/catch, not a cosmetic
    /// blemish:</para>
    ///
    /// <list type="number">
    /// <item><b>The <c>PLAYTHING_*</c> family is 20 tags, and recipe conditions are ANDed.</b>
    /// <c>ENTITY_TAG</c> asks about ONE tag; there is no OR. The family is
    /// <c>PLAYTHING_{CREATURE,FAE,HUMAN,OCCULT,UNDEAD}_{COMMON,UNCOMMON,RARE,ARTIFACT}</c>
    /// (eConfigTags.cs:675-694), carried by 729 of the 2126 shipped <c>Characters.json</c> configs
    /// (measured 2026-08-25).</item>
    ///
    /// <item><b>Without a <c>PLAYTHING_*</c> tag the removal path CRASHES.</b> The only reachable removal is
    /// pushing <c>(eAbilityResults.PLAYTHINGED, entity)</c> into the results list, and
    /// <c>CombatPhase._processCombatResults</c> (CombatPhase.cs:4329-4338) opens that branch with
    /// <c>_additionalDrops.Add(InventoryHelper.CreateThing(SkillHelper.GetEnemyDoll(entity4), 1))</c>.
    /// <c>GetEnemyDoll</c> (SkillHelper.cs:1521-1531) is
    /// <c>Tags?.FirstOrDefault(x =&gt; x.StartsWith("PLAYTHING_"))</c> → <c>"DOLL_" + tag.Split('_')[1] + "_01"</c>,
    /// i.e. <b>null</b> for an untagged config, and <c>CreateThing(null)</c> reaches
    /// <c>GetThingType(null)</c> → a dictionary lookup on a null key. So the tag test is a crash guard
    /// first and a curation rule second.</item>
    ///
    /// <item><b>Art is a hard failure, not an ugly fallback.</b>
    /// <c>CharacterVisualHelper.CreateCharacterActor</c> (CharacterVisualHelper.cs:574-577)
    /// <c>throw</c>s "Could not find composition for X" when <c>GetCharacterTierRecord</c> returns null, and
    /// <c>CreateAvatarComponent</c> (CharacterVisualHelper.cs:1875) dereferences <c>.Prefab</c> on that same
    /// null. Downstream <see cref="SummonLeakPatches"/> then deletes the combatant from the fight. So the
    /// gate asks the authoritative question directly — does <c>GetCharacterTierRecord</c> resolve? — rather
    /// than inferring drawability from an id convention.</item>
    ///
    /// <item><b>NO STUN (Ben's standing ban) has to survive a captured creature's OWN kit.</b> A captured
    /// monster fights with its shipped <c>Things</c> and <c>Passives</c>; nothing in the pack authors those.
    /// The ban is therefore enforced at the only point where it still can be — the capture itself. A config
    /// whose kit can apply <c>STATUS_STUN_*</c> or <c>STATUS_DAZE_*</c> is not capturable. Measured
    /// 2026-08-25 against the shipped configs: 127 abilities apply one of those, excluding 150 of the 721
    /// non-boss <c>PLAYTHING_*</c> configs and leaving a pool of 571.</item>
    /// </list>
    ///
    /// <para><b>Boss guard.</b> The PRIMARY test is the config <c>Tags</c> carrying <c>BOSS</c> or
    /// <c>SCOURGE</c> (<c>CharacterHelper.GetActorTags</c>, CharacterHelper.cs:2407). Runtime
    /// <c>CharacterHelper.IsBoss</c> (CharacterHelper.cs:1608) is only a SECOND layer: it reads
    /// <c>CharacterComponent.CharacterType == BOSS</c>, which is assigned only for scripted boss fights
    /// (VenueHelper.cs:904/913/969, AdventureHelper.cs:3043, CombatPhase.cs:6477), so a <c>BOSS_*</c> config
    /// spawned as an ordinary wandering enemy reports <c>STANDARD</c> and would sail through it.
    /// <c>Threat</c> is NOT a boss marker and is not consulted — <c>BOSS_NECROMANCER_00</c> has Threat 2,
    /// the same as a cave bat.</para>
    ///
    /// <para><b>Fail-safe posture.</b> Every method here answers "no" on any error. A capture that cannot be
    /// PROVEN safe does not happen; the ball is spent, a reason is logged, and the fight continues.</para>
    /// </summary>
    internal static class TrainerCaptureRules
    {
        /// <summary>The <c>eConfigTags</c> prefix naming the game's own capturable-creature families.</summary>
        internal const string PlaythingTagPrefix = "PLAYTHING_";

        /// <summary>Gary's capture ball — the ONE Thing whose throw can capture.</summary>
        internal const string CaptureBallConfig = TrainerPartnerPersistence.BallPrefix + "CAPTURE";

        /// <summary>Pack localization key for the on-screen "this cannot be caught" message.</summary>
        private const string RefusedLocKey = "CF_ORIG_CAPTURE_REFUSED";

        /// <summary>Config tags that make a creature permanently un-capturable.</summary>
        private static readonly string[] BossTags = { "BOSS", "SCOURGE" };

        /// <summary>Status-id prefixes whose application is banned outright (Ben: STUN is "too broken").
        /// Deliberately the same two families the game itself refuses to place on a tile — the head of
        /// <c>InteractableHelper.CHARACTER_ONLY_STATUS</c> and of
        /// <c>ClassForge.Recipes.Model.Vocabulary.TileIllegalStatusPrefixes</c>.</summary>
        private static readonly string[] BannedStatusPrefixes = { "STATUS_STUN", "STATUS_DAZE" };

        // ---- knobs -------------------------------------------------------------------------------

        /// <summary>Master switch for capture. Off ⇒ every CAPTURE effect is a logged no-op and the ball is
        /// an ordinary resistance-down throw.</summary>
        internal static ConfigEntry<bool> Enable;

        /// <summary>Enforce the NO-STUN rule on the captured creature's own kit. Off ⇒ a captured monster
        /// may bring shipped stun moves, which is exactly the thing Ben banned; it exists so the rule can be
        /// isolated in a test, not as a recommended setting.</summary>
        internal static ConfigEntry<bool> RejectStunKits;

        internal static void Bind(ConfigFile config)
        {
            Enable = CFConfig.Bind(config, "Trainer", "EnableCapture", true,
                "The CAPTURE verb (Gary, the capture Trainer). A PERFECT roll with a capture ball binds the "
                + "target monster to that ball and removes it from the fight; it is sent out as your minion "
                + "at the start of every later combat, keeping the HP it ended the last one with. Exactly "
                + "one monster per ball -- capturing again overwrites the record. Off = the ball is an "
                + "ordinary resistance-down throw and nothing is ever captured.", ClassForge.Core.ParityClass.Gameplay);

            RejectStunKits = CFConfig.Bind(config, "Trainer", "CaptureRejectStunKits", true,
                "Refuse to capture any monster whose OWN shipped kit can apply STATUS_STUN_* or "
                + "STATUS_DAZE_* (Ben's no-stun rule -- a captured creature fights with vanilla abilities "
                + "the pack never authored, so this is the only point at which the ban can be enforced). "
                + "Measured against the shipped configs: this excludes 150 of the 721 non-boss PLAYTHING "
                + "configs, leaving 571. Off = captured monsters may stun.", ClassForge.Core.ParityClass.Gameplay);
        }

        internal static bool Active
        {
            get { return ClassForgePlugin.FeaturesActive && Enable != null && Enable.Value; }
        }

        // ---- the gate ----------------------------------------------------------------------------

        /// <summary>
        /// Whether <paramref name="target"/> may be captured, with a human-readable
        /// <paramref name="reason"/> when it may not (logged, and shown in the verbose channel — a ball that
        /// silently does nothing is indistinguishable from a bug).
        /// </summary>
        internal static bool CanCapture(Entity target, out string reason)
        {
            reason = null;
            try
            {
                if (target == null) { reason = "no target"; return false; }

                CharacterComponent cc;
                if (!target.TryGet<CharacterComponent>(out cc) || cc == null || string.IsNullOrEmpty(cc.ConfigName))
                { reason = "the target is not a character"; return false; }

                if (CharacterHelper.IsDead(target))
                { reason = cc.ConfigName + " is already dead"; return false; }

                // Guarded lookup, NEVER Env.Configs.Characters[name]: that raw indexer
                // (CharacterHelper.cs:1913) throws KeyNotFoundException on any id the live game does not
                // define, and a save carrying a stale id would throw on every single ball throw.
                var cfg = GameLookups.CharacterConfig(cc.ConfigName);
                if (cfg == null)
                { reason = "no live CharacterConfig for '" + cc.ConfigName + "'"; return false; }

                return ConfigIsCapturable(cc.ConfigName, cfg, out reason)
                       && NotARuntimeBoss(target, cc.ConfigName, out reason);
            }
            catch (Exception ex)
            {
                reason = "the capture gate could not be evaluated (" + ex.Message + ")";
                return false;
            }
        }

        /// <summary>
        /// The config-only half of the gate: usable both at capture time and again at SEND-OUT time, when
        /// there is no live enemy left to inspect and the only thing on hand is an id off a save file.
        /// </summary>
        internal static bool ConfigIsCapturable(string configName, CharacterConfig cfg, out string reason)
        {
            reason = null;
            try
            {
                if (cfg == null) cfg = GameLookups.CharacterConfig(configName);
                if (cfg == null) { reason = "no live CharacterConfig for '" + configName + "'"; return false; }

                var tags = cfg.Tags;
                if (tags == null) { reason = configName + " has no Tags"; return false; }

                for (int i = 0; i < BossTags.Length; i++)
                    for (int t = 0; t < tags.Count; t++)
                        if (string.Equals(tags[t], BossTags[i], StringComparison.Ordinal))
                        { reason = configName + " is tagged " + BossTags[i] + " and can never be captured"; return false; }

                bool plaything = false;
                for (int t = 0; t < tags.Count; t++)
                    if (tags[t] != null && tags[t].StartsWith(PlaythingTagPrefix, StringComparison.Ordinal))
                    { plaything = true; break; }
                if (!plaything)
                {
                    reason = configName + " carries no PLAYTHING_* tag — the game has no doll family for it, "
                             + "so removing it from combat would crash on CreateThing(null)";
                    return false;
                }

                if (!HasArt(configName))
                { reason = configName + " has no dCharacter composition — it would throw when drawn"; return false; }

                if (RejectStunKits != null && RejectStunKits.Value && KitCanStun(cfg))
                { reason = configName + "'s own kit can apply STUN/DAZE (no-stun rule)"; return false; }

                return true;
            }
            catch (Exception ex)
            {
                reason = "the capture gate could not be evaluated for '" + configName + "' (" + ex.Message + ")";
                return false;
            }
        }

        /// <summary>SECOND boss layer — see the class remarks for why it can never be the first.</summary>
        private static bool NotARuntimeBoss(Entity target, string configName, out string reason)
        {
            reason = null;
            try
            {
                if (!CharacterHelper.IsBoss(target)) return true;
                reason = configName + " is a BOSS in this fight (CharacterComponent.CharacterType)";
                return false;
            }
            catch (Exception) { return true; }   // the TAG test above already carried the guard
        }

        /// <summary>
        /// The authoritative drawability question: does the game have a composition record for this config?
        /// This is the exact call <c>CreateCharacterActor</c> makes before it throws
        /// (CharacterVisualHelper.cs:573-577), so asking it here answers the same question the renderer
        /// will ask later.
        /// </summary>
        private static bool HasArt(string configName)
        {
            try { return CharacterVisualHelper.GetCharacterTierRecord(configName) != null; }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Whether anything in <paramref name="cfg"/>'s shipped kit can apply a STUN/DAZE status.
        ///
        /// <para>Walks the same two collections the game reads to build a combatant's move list: the
        /// config's <c>Things</c> (each ThingConfig's <c>Interactable.Abilities</c> keys) and its
        /// <c>Passives</c>. Every id is then resolved through the guarded
        /// <see cref="GameLookups.AbilityConfig"/> and its <c>Actions</c> inspected for an
        /// <c>ADD_STATUS</c> naming a banned status. <c>Inherits</c> is followed one level, which is the
        /// depth the shipped data actually uses (every ability inherits <c>DEFAULT_ABILITY</c>, whose
        /// <c>Actions</c> is empty).</para>
        ///
        /// <para>Fails CLOSED: any error answers "yes, it can stun", so an unreadable kit is not captured.</para>
        /// </summary>
        private static bool KitCanStun(CharacterConfig cfg)
        {
            try
            {
                var abilityIds = new List<string>();

                if (cfg.Things != null)
                    foreach (var kv in cfg.Things)
                    {
                        var tc = GameLookups.ThingConfig(kv.Key);
                        if (tc == null || tc.Interactable == null || tc.Interactable.Abilities == null) continue;
                        foreach (var ab in tc.Interactable.Abilities)
                            if (!string.IsNullOrEmpty(ab.Key)) abilityIds.Add(ab.Key);
                    }

                if (cfg.Passives != null)
                    for (int i = 0; i < cfg.Passives.Count; i++)
                        if (!string.IsNullOrEmpty(cfg.Passives[i])) abilityIds.Add(cfg.Passives[i]);

                for (int i = 0; i < abilityIds.Count; i++)
                    if (AbilityApplies(abilityIds[i], 0)) return true;

                return false;
            }
            catch (Exception) { return true; }   // fail CLOSED
        }

        private static bool AbilityApplies(string abilityId, int depth)
        {
            if (depth > 1) return false;
            var a = GameLookups.AbilityConfig(abilityId);
            if (a == null) return false;

            var actions = a.Actions;
            if (actions == null || actions.Count == 0)
                return !string.IsNullOrEmpty(a.Inherits) && AbilityApplies(a.Inherits, depth + 1);

            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i].Item1 != eCombatActions.ADD_STATUS) continue;
                string status = StatusArg(actions[i].Item2);
                if (string.IsNullOrEmpty(status)) continue;
                for (int b = 0; b < BannedStatusPrefixes.Length; b++)
                    if (status.StartsWith(BannedStatusPrefixes[b], StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// <c>ADD_STATUS</c>'s payload is a BARE STRING, exactly as <c>CombatHelper.ApplyAction</c> reads it
        /// (<c>pActionArgs is JsonElement ? ((JsonElement)pActionArgs).GetString() : (string)pActionArgs</c>).
        /// Shipped JSON deserializes it as a <c>JsonElement</c>; a hand-built action is a raw string. Both
        /// shapes are accepted here so the walk cannot be defeated by which loader produced the config.
        /// </summary>
        private static string StatusArg(object arg)
        {
            try
            {
                if (arg == null) return null;
                var s = arg as string;
                if (s != null) return s;
                if (arg is System.Text.Json.JsonElement)
                {
                    var je = (System.Text.Json.JsonElement)arg;
                    return je.ValueKind == System.Text.Json.JsonValueKind.String ? je.GetString() : null;
                }
                return null;
            }
            catch (Exception) { return null; }
        }

        // ---- player-visible refusal ---------------------------------------------------------------

        /// <summary>
        /// Float a "can't be caught" message over the thrower when a capture ball resolves on an enemy the
        /// <see cref="CanCapture"/> gate refuses.
        ///
        /// <para><b>Why this exists.</b> The gate now lives in the recipe's <c>Conditions</c> (the virtual
        /// <c>CF_CAPTURABLE</c> ENTITY_TAG), which is what keeps a refused throw from spending the fight's
        /// <c>ONCE_PER_COMBAT</c> budget — but a recipe that does not fire also says nothing. Before this,
        /// the ONLY signal a player got was a <c>LogDebug</c> line in the BepInEx console.</para>
        ///
        /// <para><b>Reused channel, not an invented one.</b> <c>(eAbilityResults.POPCORN, (Entity, string))</c>
        /// pushed into the ability's own results list is the game's floating combat message:
        /// <c>CombatViewHelper</c> (CombatViewHelper.cs:800-805) turns it into a
        /// <c>POPCORN_MESSAGE</c> visual node with <c>ePopcornTextEvent.MESSAGE</c>, which
        /// <c>PopcornMessageHelper</c> (PopcornMessageHelper.cs:385-387) renders as the string VERBATIM — no
        /// <c>Lang</c> lookup of its own, so the text must already be localized. The shipped precedent is
        /// <c>InteractableHelper.cs:1552</c> (CONSUME_POISON), which pushes exactly this shape with a
        /// <c>Lang.__t</c> string. Same results list the CAPTURE effect already writes
        /// <c>eAbilityResults.PLAYTHINGED</c> into.</para>
        ///
        /// <para><b>Gated on the acting Thing</b>, the same shape as <c>TrainerFocusFire.Issue</c>: only the
        /// capture ball's own throw can produce this, so no other class and no other item can reach it. The
        /// engineering reason stays in the log; the on-screen text is one short localized line, because
        /// popcorn text is a floater, not a tooltip.</para>
        /// </summary>
        internal static void AnnounceRefusal(Entity thrower, Entity target, Thing actingThing,
            List<(eAbilityResults, object)> results)
        {
            if (!Active || results == null || thrower == null || target == null) return;
            try
            {
                if (actingThing == null
                    || !string.Equals(actingThing.ConfigName, CaptureBallConfig, StringComparison.Ordinal))
                    return;

                // Only an ENEMY throw is a capture attempt; a ball thrown at an ally is a resistance-down,
                // and CanCapture would refuse it for a reason the player does not need shouted at them.
                if (!CharacterHelper.IsEnemy(target)) return;
                if (CharacterHelper.IsDead(target)) return;

                string reason;
                if (CanCapture(target, out reason)) return;

                results.Add((eAbilityResults.POPCORN, (thrower, Lang.__t(RefusedLocKey))));

                ClassForgePlugin.Log.LogDebug(
                    "[ClassForge] capture ball thrown at an uncapturable target: " + reason
                    + ". The recipe's CF_CAPTURABLE condition keeps the once-per-combat budget unspent; "
                    + "the player is told on screen.");
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] the capture refusal message could not be shown (fail-safe, the throw "
                    + "still resolved normally): " + ex.Message);
            }
        }

        // ---- the record --------------------------------------------------------------------------

        /// <summary>
        /// The Thing with config id <paramref name="itemConfig"/> in <paramref name="owner"/>'s inventory,
        /// or null.
        ///
        /// <para>POSSESSION, not equipment — the same read <c>InventoryHelper.HasItemByName</c> performs
        /// (InventoryHelper.cs:424) and the same one <c>HAS_ITEM</c> already uses. A toolbelt Thing has no
        /// <c>eEquipmentSlots</c> slot and can NEVER be equipped, so an equipped-only variant of this lookup
        /// would be permanently null for a capture ball.</para>
        /// </summary>
        internal static Thing CarriedItem(Entity owner, string itemConfig)
        {
            try
            {
                CharacterComponent cc;
                if (owner == null || string.IsNullOrEmpty(itemConfig)) return null;
                if (!owner.TryGet<CharacterComponent>(out cc) || cc == null || cc.Things == null) return null;

                // A Trainer BALL resolves to the ONE canonical copy (TrainerPartnerPersistence.CanonicalBall):
                // the one already carrying a creature, else the one that has been sent out, else the first.
                // Capture writes through this, the send-out reads through it and the HP write-through binds
                // through it, so N carried balls are N throwables but exactly ONE minion slot, and a second
                // ball can never orphan the creature stored in the first. Any other item id keeps the plain
                // first-match scan.
                if (itemConfig.StartsWith(TrainerPartnerPersistence.BallPrefix, StringComparison.Ordinal))
                    return TrainerPartnerPersistence.CanonicalBall(cc, itemConfig);

                for (int i = 0; i < cc.Things.Count; i++)
                {
                    var t = cc.Things[i];
                    if (t != null && string.Equals(t.ConfigName, itemConfig, StringComparison.Ordinal)) return t;
                }
                return null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>A <c>Thing.CustomData</c> value, or null. Never throws.</summary>
        internal static string ReadCustomData(Thing thing, string key)
        {
            try
            {
                string raw;
                if (thing == null || string.IsNullOrEmpty(key)) return null;
                return CoreHelper.TryGetCustomData(thing, key, out raw) ? raw : null;
            }
            catch (Exception) { return null; }
        }
    }
}

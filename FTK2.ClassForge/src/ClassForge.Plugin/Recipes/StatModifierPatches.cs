using System;
using System.Collections.Generic;
using ClassForge.Recipes.Model;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Plugin
{
    /// <summary>
    /// CONDITIONAL_STAT_MODIFIER read path (spec §1/§3, M-CS2): one postfix on the TERMINAL
    /// <c>CharacterHelper.GetStat</c> overload (CH L422 — the one with <c>out pBonuses</c>). All seven
    /// public overloads funnel into it, so this single patch covers every stat read, including the direct
    /// L422 calls EOR's L416 postfix misses (spec §1.1). Patching any second overload in the funnel would
    /// double-apply every bonus — exactly one overload is patched, ever.
    ///
    /// <para><b>MP posture:</b> a pure read postfix — mutates only the return value, takes no RNG draw,
    /// and derives entirely from replicated state (equipped traits, per-battle counters, follower roster),
    /// so every peer computes the same number (spec §0). It is still gameplay-relevant, which is why
    /// <c>[Skills] EnableStatModifiers</c> is part of the parity registration: peers disagreeing on the
    /// knob get a real parity divergence instead of silently different stats.</para>
    /// </summary>
    internal static class StatModifierPatches
    {
        /// <summary>Reentrancy guard (spec §3.1): a resolver that somehow reads a stat re-enters the
        /// postfix and must see the unmodified base value instead of recursing. The terminal GetStat body
        /// was verified not to self-recurse; this is the backstop that makes the property structural.</summary>
        [ThreadStatic] private static bool _inPostfix;

        public static void GetStat_Postfix(Entity pCharacterEntity, string pStat, ref int __result)
        {
            if (_inPostfix) return;
            _inPostfix = true;
            try
            {
                if (!ClassForgePlugin.FeaturesActive) return;
                if (ClassForgePlugin.EnableStatModifiers == null || !ClassForgePlugin.EnableStatModifiers.Value) return;

                List<StatModifierHost.Entry> entries;
                if (!StatModifierHost.ByStat.TryGetValue(pStat, out entries)) return;
                if (pCharacterEntity == null) return;

                CharacterComponent component;
                if (!pCharacterEntity.TryGet<CharacterComponent>(out component) || component == null) return;

                List<StatModifier> applicable = null;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (!OwnerMatches(component, entries[i])) continue;
                    if (applicable == null) applicable = new List<StatModifier>();
                    applicable.Add(entries[i].Modifier);
                }
                if (applicable == null) return;

                string guid = pCharacterEntity.Guid;
                __result = StatModifierEngine.Compose(
                    __result, pStat, applicable,
                    m => ConditionsPass(m, guid),
                    name => RecipeEngineHost.PeekCounter(guid, name));
            }
            catch (Exception ex)
            {
                ClassForgePlugin.Log.LogError("[ClassForge] Stat-modifier postfix failed (fail-safe, vanilla value kept): " + ex);
            }
            finally
            {
                _inPostfix = false;
            }
        }

        private static bool OwnerMatches(CharacterComponent component, StatModifierHost.Entry entry)
        {
            var classes = entry.ClassOwners;
            for (int i = 0; i < classes.Count; i++)
                if (string.Equals(component.ConfigName, classes[i], StringComparison.Ordinal)) return true;

            var traits = entry.TraitOwners;
            if (traits.Count > 0 && component.Things != null)
            {
                // Same per-read scan EOR ships in production (EOR62 HasSelectedTrait, Plugin.cs L24607).
                foreach (Thing thing in component.Things)
                {
                    if (thing == null) continue;
                    for (int i = 0; i < traits.Count; i++)
                        if (string.Equals(thing.ConfigName, traits[i], StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }

        private static bool ConditionsPass(StatModifier m, string entityGuid)
        {
            var conditions = m.Conditions;
            for (int i = 0; i < conditions.Count; i++)
            {
                var c = conditions[i];
                bool pass;
                switch (c.Type)
                {
                    case ConditionKind.COUNTER:
                        pass = Vocabulary.Compare(
                            c.HasComparator ? c.Comparator : Comparator.GTE,
                            RecipeEngineHost.PeekCounter(entityGuid, c.Name),
                            c.ValueInt ?? 1);
                        break;
                    case ConditionKind.PARTY_HAS_FOLLOWER:
                        pass = PartyHasPetOrMercenary();
                        break;
                    default:
                        // The validator admits only the two kinds above; anything else means a
                        // book/validator drift — fail closed.
                        pass = false;
                        break;
                }
                if (c.Negate) pass = !pass;
                if (!pass) return false;
            }
            return true;
        }

        /// <summary>EOR62 <c>PartyHasPetOrMercenary()</c> (Plugin.cs L24576), verbatim semantics: any
        /// player follower whose FollowerID resolves to a live pet or mercenary entity. Replicated state
        /// only; no stat read (spec §3.3-legal).</summary>
        private static bool PartyHasPetOrMercenary()
        {
            var env = RouterHelper.Env;
            var run = env != null ? env.GameRun : null;
            if (run == null || run.PlayerFollowers == null || run.Entities == null) return false;

            foreach (FollowerState follower in run.PlayerFollowers.Values)
            {
                if (follower == null || string.IsNullOrEmpty(follower.FollowerID)) continue;
                foreach (Entity entity in run.Entities)
                {
                    if (entity == null || !string.Equals(entity.Guid, follower.FollowerID, StringComparison.Ordinal)) continue;
                    if (entity.Has<CharacterComponent>() &&
                        (CharacterHelper.IsPet(entity) || CharacterHelper.IsMercenary(entity)))
                        return true;
                    break;
                }
            }
            return false;
        }
    }
}

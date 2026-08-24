using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Reads what a party member can actually DO: abilities, passive skills and equipped weapon.
    ///
    /// This closes the harness's biggest observation gap. <c>crucible.state.v2</c> exposes hp, stats
    /// and status effects but no abilities at all, so there was no way to answer the question the
    /// whole project exists to answer — "does this class's signature ability exist on the character?"
    /// Two authored recipes shipped applying a status id that resolved to nothing, and no assertion
    /// in the harness could have caught it.
    ///
    /// Verified API surface (TypeProbe --signatures, 2026-08-23):
    ///   CombatHelper.GetAbilities(Entity, Boolean pMainHandOnly, Boolean pIncludeDefaultAbilities,
    ///       Boolean pIncludeConsumables, Boolean pIgnoreConfuseAbilites, Boolean pIgnoreReviveAbility,
    ///       Boolean pGetChargeAbilities, Boolean pGetHookAbilities) static
    ///   CharacterHelper.GetPassiveSkills(Entity pCharacterEntity) static
    ///   CharacterHelper.GetPassiveSkills(String pCharacterConfigName) static
    ///   EquipmentHelper.GetEquippedWeaponThing(CharacterComponent pCharacterComponent) static
    ///
    /// The overloads are bound by PARAMETER TYPE, never by name alone: GetPassiveSkills has an
    /// Entity overload and a String overload whose meanings differ (what this character has, versus
    /// what the class config declares), and binding the wrong one would answer a different question
    /// while looking correct. Both are read here on purpose — comparing them is exactly how you
    /// detect a class swap that changed the id without rebuilding the character.
    /// </summary>
    internal static class AbilityCommands
    {
        internal static string LastResult;
        private static ManualLogSource _log;
        private static bool _registered;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        internal static void TryRegister()
        {
            if (_registered) return;
            _registered = GameBridge.RegisterCommand("crucible_party_abilities",
                typeof(AbilityCommands).GetMethod("CruciblePartyAbilities", BindingFlags.Public | BindingFlags.Static),
                new List<string> { "slot" });
            if (_registered && _log != null) _log.LogInfo("AbilityCommands registered (crucible_party_abilities).");
        }

        /// <summary>
        /// crucible_party_abilities &lt;slot&gt; — abilities, passive skills (as-built vs as-declared)
        /// and equipped weapon for one party member.
        /// </summary>
        public static void CruciblePartyAbilities(string slot)
        {
            LastResult = null;
            try
            {
                int index;
                if (!int.TryParse((slot ?? "").Trim(), out index))
                {
                    LastResult = "error: usage: crucible_party_abilities <slot>";
                    return;
                }

                List<object> party;
                string error;
                if (!PartyAccess.TryGetParty(out party, out error)) { LastResult = "error: " + error; return; }
                if (index < 0 || index >= party.Count)
                {
                    LastResult = "error: slot " + index + " out of range (party count=" + party.Count + ")";
                    return;
                }

                object entity = party[index];
                object character = PartyAccess.FindComponent(entity, "CharacterComponent");
                string configName = character == null ? null : PartyAccess.ReadMember(character, "ConfigName") as string;

                StringBuilder sb = new StringBuilder();
                sb.Append("slot=").Append(index).Append(" classId=").Append(configName ?? "(null)");

                sb.Append("\nabilities: ").Append(DescribeAbilities(entity));
                sb.Append("\npassiveSkills(entity): ").Append(DescribePassiveSkillsByEntity(entity));
                sb.Append("\npassiveSkills(configName): ").Append(DescribePassiveSkillsByConfig(configName));
                sb.Append("\nequippedWeapon: ").Append(DescribeEquippedWeapon(character));
                sb.Append("\nNOTE: passiveSkills(entity) is what this character HAS; passiveSkills(configName)");
                sb.Append("\n      is what the class config DECLARES. A divergence means the entity was not");
                sb.Append("\n      rebuilt for its current class id.");

                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_party_abilities threw: " + ex.Message;
            }
        }

        private static string DescribeAbilities(object entity)
        {
            try
            {
                Type combatHelper = AccessTools.TypeByName("CombatHelper");
                if (combatHelper == null) return "(CombatHelper not found)";

                MethodInfo m = null;
                foreach (MethodInfo candidate in combatHelper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!string.Equals(candidate.Name, "GetAbilities", StringComparison.Ordinal)) continue;
                    if (candidate.GetParameters().Length == 8) { m = candidate; break; }
                }
                if (m == null) return "(CombatHelper.GetAbilities 8-arg overload not found)";

                // mainHandOnly=false, includeDefaults=true, includeConsumables=true, and no filtering
                // of confuse/revive/charge/hook: a test wants the WIDEST list, because an ability that
                // is missing from a narrow query is indistinguishable from one that does not exist.
                object result = m.Invoke(null, new object[] { entity, false, true, true, false, false, true, true });
                return RenderList(result);
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                return "(threw: " + root.Message + ")";
            }
            catch (Exception ex) { return "(failed: " + ex.Message + ")"; }
        }

        private static string DescribePassiveSkillsByEntity(object entity)
        {
            return InvokePassiveSkills(entity, "Entity");
        }

        private static string DescribePassiveSkillsByConfig(string configName)
        {
            if (string.IsNullOrEmpty(configName)) return "(no class id)";
            return InvokePassiveSkills(configName, "String");
        }

        /// <summary>Binds GetPassiveSkills by the single parameter's TYPE NAME, never by name alone.</summary>
        private static string InvokePassiveSkills(object argument, string expectedParamTypeName)
        {
            try
            {
                Type characterHelper = AccessTools.TypeByName("CharacterHelper");
                if (characterHelper == null) return "(CharacterHelper not found)";

                MethodInfo m = null;
                foreach (MethodInfo candidate in characterHelper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!string.Equals(candidate.Name, "GetPassiveSkills", StringComparison.Ordinal)) continue;
                    ParameterInfo[] ps = candidate.GetParameters();
                    if (ps.Length != 1) continue;
                    if (string.Equals(ps[0].ParameterType.Name, expectedParamTypeName, StringComparison.Ordinal)) { m = candidate; break; }
                }
                if (m == null) return "(GetPassiveSkills(" + expectedParamTypeName + ") overload not found)";

                return RenderList(m.Invoke(null, new object[] { argument }));
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                return "(threw: " + root.Message + ")";
            }
            catch (Exception ex) { return "(failed: " + ex.Message + ")"; }
        }

        private static string DescribeEquippedWeapon(object characterComponent)
        {
            if (characterComponent == null) return "(no CharacterComponent)";
            try
            {
                Type equipmentHelper = AccessTools.TypeByName("EquipmentHelper");
                if (equipmentHelper == null) return "(EquipmentHelper not found)";

                MethodInfo m = AccessTools.Method(equipmentHelper, "GetEquippedWeaponThing", new[] { characterComponent.GetType() });
                if (m == null) return "(GetEquippedWeaponThing not found)";

                object thing = m.Invoke(null, new object[] { characterComponent });
                if (thing == null) return "(none)";

                object configName = PartyAccess.ReadMember(thing, "ConfigName");
                return configName == null ? thing.ToString() : configName.ToString();
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                return "(threw: " + root.Message + ")";
            }
            catch (Exception ex) { return "(failed: " + ex.Message + ")"; }
        }

        /// <summary>
        /// Renders a returned collection. A null return is reported as "(null)" and an empty one as
        /// "count=0 []" — they are different findings and collapsing them would hide an unreadable
        /// member behind what looks like a legitimately empty list.
        /// </summary>
        private static string RenderList(object value)
        {
            if (value == null) return "(null)";
            IEnumerable list = value as IEnumerable;
            if (list == null) return value.ToString();

            List<string> parts = new List<string>();
            foreach (object item in list)
            {
                if (item == null) { parts.Add("(null)"); continue; }
                object configName = PartyAccess.ReadMember(item, "ConfigName");
                object abilityName = PartyAccess.ReadMember(item, "AbilityName");
                object id = PartyAccess.ReadMember(item, "Id");
                object best = configName ?? abilityName ?? id;
                parts.Add(best == null ? item.ToString() : best.ToString());
            }
            parts.Sort(StringComparer.Ordinal);
            return "count=" + parts.Count + " [" + string.Join(", ", parts.ToArray()) + "]";
        }
    }
}

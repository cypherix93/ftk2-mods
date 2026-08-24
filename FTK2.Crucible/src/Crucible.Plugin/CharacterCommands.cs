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
    /// Mutates a character: equipment, inventory, stats, level and status effects.
    ///
    /// Equipment is the load-bearing one. Abilities come from the EQUIPPED WEAPON, not from the
    /// class, so swapping <c>CharacterComponent.ConfigName</c> alone produces a character of the
    /// right class holding the previous class's weapon and therefore the previous class's abilities.
    /// Measured live: an entity swapped to CF_EOR_ARCANIST still offered HOOK_LEFT/HOOK_PULL/
    /// HOOK_RIGHT because it was still holding the Thief's whip. A class fixture is not valid until
    /// the matching weapon is equipped.
    ///
    /// Status application matters for a different reason: it can be asserted WITHOUT combat, so a
    /// large part of the content surface becomes testable before the combat blocker is solved.
    ///
    /// Verified API surface (TypeProbe --signatures, 2026-08-23):
    ///   EquipmentHelper.Equip(String pThingName, Entity pCharacterEntity, Int32 pMinMaterialTier,
    ///       Int32 pMaxMaterialTier, GameRandom pGameRandom) static
    ///   EquipmentHelper.CanEquip(String pThingName, Entity pCharacterEntity) static -> Boolean
    ///   InventoryHelper.GiveByName(String pThingConfigName, Int32 pQuantity, List(Thing) pInventory,
    ///       String pParentId, Int32 pMinMaterialTier, Int32 pMaxMaterialTier, GameRandom) static
    ///   CharacterHelper.SetStat(Entity, String pStat, Int32 pValue) static
    ///   CharacterHelper.TryProgressCharacterEntityToLevel(Entity, Int32, GameRandom) static -> Boolean
    ///   InteractableHelper.ApplyStatus(Entity pOriginEntity, Entity pTargetEntity, Thing pThing,
    ///       String pAbilityName, String pStatusConfigName, GameRandom pGameRandom, List pResults,
    ///       Boolean pTierStatus, Boolean pRenderStatusPopcorn, Nullable(Int32) pDurationOverride) static
    ///   CharacterComponent.Things : List(Thing) · .Equipped : Dictionary(eEquipmentSlots, String)
    ///   StatusEffectComponent.Statuses : Dictionary(String, StatusEffectInfo)
    /// </summary>
    internal static class CharacterCommands
    {
        internal static string LastResult;
        private static ManualLogSource _log;
        private static bool _equipRegistered;
        private static bool _giveRegistered;
        private static bool _statRegistered;
        private static bool _statusRegistered;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        internal static void TryRegister()
        {
            if (!_equipRegistered)
                _equipRegistered = GameBridge.RegisterCommand("crucible_equip",
                    typeof(CharacterCommands).GetMethod("CrucibleEquip", BindingFlags.Public | BindingFlags.Static),
                    new List<string> { "slot", "thingConfigName" });

            if (!_giveRegistered)
                _giveRegistered = GameBridge.RegisterCommand("crucible_give_item",
                    typeof(CharacterCommands).GetMethod("CrucibleGiveItem", BindingFlags.Public | BindingFlags.Static),
                    new List<string> { "slot", "thingConfigName", "quantity" });

            if (!_statRegistered)
                _statRegistered = GameBridge.RegisterCommand("crucible_set_stat_value",
                    typeof(CharacterCommands).GetMethod("CrucibleSetStat", BindingFlags.Public | BindingFlags.Static),
                    new List<string> { "slot", "stat", "value" });

            if (!_statusRegistered)
                _statusRegistered = GameBridge.RegisterCommand("crucible_status_add",
                    typeof(CharacterCommands).GetMethod("CrucibleStatusAdd", BindingFlags.Public | BindingFlags.Static),
                    new List<string> { "slot", "statusConfigName", "duration" });
        }

        // ============================================================== crucible_equip

        /// <summary>crucible_equip &lt;slot&gt; &lt;thingConfigName&gt; — equip an item by config id.</summary>
        public static void CrucibleEquip(string slot, string thingConfigName)
        {
            LastResult = null;
            try
            {
                object entity, character;
                string error;
                if (!Resolve(slot, out entity, out character, out error)) { LastResult = "error: " + error; return; }
                if (string.IsNullOrEmpty(thingConfigName)) { LastResult = "error: usage: crucible_equip <slot> <thingConfigName>"; return; }
                thingConfigName = thingConfigName.Trim();

                Type equipmentHelper = AccessTools.TypeByName("EquipmentHelper");
                if (equipmentHelper == null) { LastResult = "error: EquipmentHelper not found"; return; }

                string weaponBefore = DescribeEquippedWeapon(character);

                // CanEquip first: Equip returns void, so without this a refused equip is
                // indistinguishable from a successful one.
                object canEquip = null;
                MethodInfo can = AccessTools.Method(equipmentHelper, "CanEquip", new[] { typeof(string), entity.GetType() });
                if (can != null)
                {
                    try { canEquip = can.Invoke(null, new object[] { thingConfigName, entity }); }
                    catch (Exception) { canEquip = null; }
                }

                MethodInfo equip = null;
                foreach (MethodInfo m in equipmentHelper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!string.Equals(m.Name, "Equip", StringComparison.Ordinal)) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 5 && ps[0].ParameterType == typeof(string)) { equip = m; break; }
                }
                if (equip == null) { LastResult = "error: EquipmentHelper.Equip(String, Entity, Int32, Int32, GameRandom) not found"; return; }

                // Tiers -1/-1: the starter-weapon configs declare MinTier/MaxTier of -1, so this
                // asks for the item exactly as authored rather than rolling a material tier.
                equip.Invoke(null, new object[] { thingConfigName, entity, -1, -1, GameRandomOrNull() });

                string weaponAfter = DescribeEquippedWeapon(character);
                LastResult = "slot=" + slot.Trim()
                    + " item=" + thingConfigName
                    + " canEquip=" + (canEquip == null ? "(unknown)" : canEquip.ToString())
                    + " weaponBefore=" + weaponBefore
                    + " weaponAfter=" + weaponAfter
                    + " changed=" + (!string.Equals(weaponBefore, weaponAfter, StringComparison.Ordinal));
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: Equip threw: " + root.GetType().Name + ": " + root.Message;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_equip threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_give_item

        /// <summary>crucible_give_item &lt;slot&gt; &lt;thingConfigName&gt; &lt;quantity&gt;</summary>
        public static void CrucibleGiveItem(string slot, string thingConfigName, string quantity)
        {
            LastResult = null;
            try
            {
                object entity, character;
                string error;
                if (!Resolve(slot, out entity, out character, out error)) { LastResult = "error: " + error; return; }
                if (string.IsNullOrEmpty(thingConfigName)) { LastResult = "error: usage: crucible_give_item <slot> <thingConfigName> <quantity>"; return; }

                int qty;
                if (!int.TryParse((quantity ?? "1").Trim(), out qty) || qty < 1) qty = 1;

                object things = PartyAccess.ReadMember(character, "Things");
                ICollection before = things as ICollection;
                int countBefore = before == null ? -1 : before.Count;

                Type inventoryHelper = AccessTools.TypeByName("InventoryHelper");
                if (inventoryHelper == null) { LastResult = "error: InventoryHelper not found"; return; }

                MethodInfo give = null;
                foreach (MethodInfo m in inventoryHelper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!string.Equals(m.Name, "GiveByName", StringComparison.Ordinal)) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 7 && ps[0].ParameterType == typeof(string)) { give = m; break; }
                }
                if (give == null) { LastResult = "error: InventoryHelper.GiveByName(String, ...) not found"; return; }

                give.Invoke(null, new object[] { thingConfigName.Trim(), qty, things, null, -1, -1, GameRandomOrNull() });

                ICollection after = PartyAccess.ReadMember(character, "Things") as ICollection;
                int countAfter = after == null ? -1 : after.Count;

                LastResult = "slot=" + slot.Trim() + " item=" + thingConfigName.Trim() + " qty=" + qty
                    + " thingsBefore=" + countBefore + " thingsAfter=" + countAfter
                    + " changed=" + (countAfter != countBefore);
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: GiveByName threw: " + root.GetType().Name + ": " + root.Message;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_give_item threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_set_stat_value

        /// <summary>crucible_set_stat_value &lt;slot&gt; &lt;stat&gt; &lt;value&gt;</summary>
        public static void CrucibleSetStat(string slot, string stat, string value)
        {
            LastResult = null;
            try
            {
                object entity, character;
                string error;
                if (!Resolve(slot, out entity, out character, out error)) { LastResult = "error: " + error; return; }

                int amount;
                if (string.IsNullOrEmpty(stat) || !int.TryParse((value ?? "").Trim(), out amount))
                {
                    LastResult = "error: usage: crucible_set_stat_value <slot> <stat> <value>";
                    return;
                }
                stat = stat.Trim().ToUpperInvariant();

                Type characterHelper = AccessTools.TypeByName("CharacterHelper");
                MethodInfo setStat = characterHelper == null ? null : AccessTools.Method(
                    characterHelper, "SetStat", new[] { entity.GetType(), typeof(string), typeof(int) });
                if (setStat == null) { LastResult = "error: CharacterHelper.SetStat(Entity, String, Int32) not found"; return; }

                string before = ReadStat(entity, stat);
                setStat.Invoke(null, new object[] { entity, stat, amount });
                string after = ReadStat(entity, stat);

                LastResult = "slot=" + slot.Trim() + " stat=" + stat + " requested=" + amount
                    + " before=" + before + " after=" + after
                    + " changed=" + (!string.Equals(before, after, StringComparison.Ordinal));
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: SetStat threw: " + root.GetType().Name + ": " + root.Message;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_set_stat_value threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_status_add

        /// <summary>
        /// crucible_status_add &lt;slot&gt; &lt;statusConfigName&gt; &lt;duration&gt; — applies a status
        /// through the game's real applier, then reports the character's statuses so the effect is
        /// observed rather than assumed.
        /// </summary>
        public static void CrucibleStatusAdd(string slot, string statusConfigName, string duration)
        {
            LastResult = null;
            try
            {
                object entity, character;
                string error;
                if (!Resolve(slot, out entity, out character, out error)) { LastResult = "error: " + error; return; }
                if (string.IsNullOrEmpty(statusConfigName))
                {
                    LastResult = "error: usage: crucible_status_add <slot> <statusConfigName> <duration>";
                    return;
                }
                statusConfigName = statusConfigName.Trim();

                int durationValue;
                bool hasDuration = int.TryParse((duration ?? "").Trim(), out durationValue);

                Type interactableHelper = AccessTools.TypeByName("InteractableHelper");
                if (interactableHelper == null) { LastResult = "error: InteractableHelper not found"; return; }

                MethodInfo apply = null;
                foreach (MethodInfo m in interactableHelper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!string.Equals(m.Name, "ApplyStatus", StringComparison.Ordinal)) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    // The 10-arg overload targets a single Entity; the 9-arg one targets a party list.
                    if (ps.Length == 10) { apply = m; break; }
                }
                if (apply == null) { LastResult = "error: InteractableHelper.ApplyStatus(10 args) not found"; return; }

                string before = DescribeStatuses(entity);

                ParameterInfo[] parameters = apply.GetParameters();
                object results = Activator.CreateInstance(parameters[6].ParameterType);
                object durationArg = hasDuration
                    ? Activator.CreateInstance(parameters[9].ParameterType, new object[] { durationValue })
                    : null;

                apply.Invoke(null, new object[]
                {
                    entity, entity, null, "crucible_status_add", statusConfigName,
                    GameRandomOrNull(), results, true, false, durationArg
                });

                string after = DescribeStatuses(entity);
                ICollection resultList = results as ICollection;

                LastResult = "slot=" + slot.Trim() + " status=" + statusConfigName
                    + (hasDuration ? " duration=" + durationValue : " duration=(config default)")
                    + "\nstatusesBefore: " + before
                    + "\nstatusesAfter:  " + after
                    + "\nresultCount=" + (resultList == null ? -1 : resultList.Count)
                    + " changed=" + (!string.Equals(before, after, StringComparison.Ordinal));
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: ApplyStatus threw: " + root.GetType().Name + ": " + root.Message;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_status_add threw: " + ex.Message;
            }
        }

        // ============================================================== helpers

        private static bool Resolve(string slot, out object entity, out object character, out string error)
        {
            entity = null;
            character = null;
            error = null;

            int index;
            if (!int.TryParse((slot ?? "").Trim(), out index)) { error = "slot must be an integer, got '" + slot + "'"; return false; }

            List<object> party;
            if (!PartyAccess.TryGetParty(out party, out error)) return false;
            if (index < 0 || index >= party.Count) { error = "slot " + index + " out of range (party count=" + party.Count + ")"; return false; }

            entity = party[index];
            character = PartyAccess.FindComponent(entity, "CharacterComponent");
            if (character == null) { error = "slot " + index + " has no CharacterComponent"; return false; }
            return true;
        }

        /// <summary>The overworld director's GameRandom, or null. Several APIs accept a null random.</summary>
        private static object GameRandomOrNull()
        {
            try
            {
                Type routerHelper = AccessTools.TypeByName("RouterHelper");
                FieldInfo routerField = routerHelper == null ? null : AccessTools.Field(routerHelper, "_router");
                object router = routerField == null ? null : routerField.GetValue(null);
                object director = router == null ? null : PartyAccess.ReadMember(router, "_adventureDirector");
                return director == null ? null : PartyAccess.ReadMember(director, "_gameRandom");
            }
            catch (Exception) { return null; }
        }

        private static string DescribeEquippedWeapon(object characterComponent)
        {
            try
            {
                Type equipmentHelper = AccessTools.TypeByName("EquipmentHelper");
                MethodInfo get = equipmentHelper == null ? null : AccessTools.Method(
                    equipmentHelper, "GetEquippedWeaponThing", new[] { characterComponent.GetType() });
                if (get == null) return "(unreadable)";
                object thing = get.Invoke(null, new object[] { characterComponent });
                if (thing == null) return "(none)";
                object configName = PartyAccess.ReadMember(thing, "ConfigName");
                return configName == null ? thing.ToString() : configName.ToString();
            }
            catch (Exception) { return "(unreadable)"; }
        }

        private static string DescribeStatuses(object entity)
        {
            object component = PartyAccess.FindComponent(entity, "StatusEffectComponent");
            if (component == null) return "(no StatusEffectComponent)";
            IDictionary statuses = PartyAccess.ReadMember(component, "Statuses") as IDictionary;
            if (statuses == null) return "(null)";

            List<string> parts = new List<string>();
            foreach (DictionaryEntry e in statuses)
            {
                object durationValue = PartyAccess.ReadMember(e.Value, "Duration");
                parts.Add((e.Key == null ? "?" : e.Key.ToString())
                    + "(d=" + (durationValue == null ? "?" : durationValue.ToString()) + ")");
            }
            parts.Sort(StringComparer.Ordinal);
            return "count=" + parts.Count + " [" + string.Join(", ", parts.ToArray()) + "]";
        }

        /// <summary>Binds the 3-arg (Entity, String, eGetStatEquippedFilters) overload, unique by arity and types.</summary>
        private static string ReadStat(object entity, string stat)
        {
            try
            {
                Type characterHelper = AccessTools.TypeByName("CharacterHelper");
                if (characterHelper == null) return "(unreadable)";

                foreach (MethodInfo m in characterHelper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!string.Equals(m.Name, "GetStat", StringComparison.Ordinal)) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 3) continue;
                    if (ps[1].ParameterType != typeof(string)) continue;
                    if (!ps[2].ParameterType.IsEnum) continue;

                    object filterAll = Enum.Parse(ps[2].ParameterType, "ALL", true);
                    object value = m.Invoke(null, new object[] { entity, stat, filterAll });
                    return value == null ? "(null)" : value.ToString();
                }
                return "(no GetStat overload matched)";
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                return "(threw: " + root.Message + ")";
            }
            catch (Exception ex) { return "(failed: " + ex.Message + ")"; }
        }
    }
}

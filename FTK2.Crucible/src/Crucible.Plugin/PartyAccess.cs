using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Shared access to the live party. Used by every command that addresses a character by slot.
    ///
    /// "Party" means entities carrying a PlayerComponent, in GameRunData.Entities order. That order
    /// is the slot index every crucible_party_* command takes, and it is NOT a persistent id - it
    /// must not be cached across a reload.
    ///
    /// Components are matched by type NAME over Entity.Components rather than through the generic
    /// Entity.Get&lt;T&gt;(), which would require MakeGenericMethod against a runtime-resolved game
    /// type for no benefit and a worse failure message.
    /// </summary>
    internal static class PartyAccess
    {
        internal static bool TryGetParty(out List<object> party, out string error)
        {
            party = new List<object>();
            error = null;

            object env = GameBridge.GetEnv();
            if (env == null) { error = "Env unavailable"; return false; }

            object gameRun = ReadMember(env, "GameRun");
            if (gameRun == null) { error = "Env.GameRun is null -- no run is loaded"; return false; }

            object entities = ReadMember(gameRun, "Entities");
            if (entities == null) entities = ReadMember(gameRun, "_entities");

            IEnumerable list = entities as IEnumerable;
            if (list == null) { error = "GameRunData.Entities is not enumerable"; return false; }

            foreach (object entity in list)
            {
                if (entity == null) continue;
                if (FindComponent(entity, "PlayerComponent") != null) party.Add(entity);
            }

            if (party.Count == 0) { error = "no entities carry a PlayerComponent (is a run loaded?)"; return false; }
            return true;
        }

        internal static object FindComponent(object entity, string componentTypeName)
        {
            if (entity == null) return null;
            try
            {
                IDictionary map = ReadMember(entity, "Components") as IDictionary;
                if (map == null) return null;
                foreach (object value in map.Values)
                {
                    if (value == null) continue;
                    if (string.Equals(value.GetType().Name, componentTypeName, StringComparison.Ordinal)) return value;
                }
                return null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// A usable GameRandom, trying the live sources before constructing one.
        ///
        /// Several game APIs take a GameRandom and dereference it. AdventureDirector._gameRandom is
        /// the natural source but is NULL for a while after a load, which made
        /// EquipmentHelper.Equip throw a NullReferenceException in a sweep while the same call
        /// worked by hand minutes later - a timing-dependent failure that reads as a broken command.
        /// Constructing one as a last resort keeps callers deterministic instead of flaky; a
        /// harness-supplied seed is fine because none of these calls is meant to reproduce a
        /// specific in-game roll.
        /// </summary>
        internal static object ResolveGameRandom()
        {
            object fromDirector = ReadMember(Director(), "_gameRandom");
            if (fromDirector != null) return fromDirector;

            object env = GameBridge.GetEnv();
            object gameRun = ReadMember(env, "GameRun");
            object fromCombat = ReadMember(ReadMember(gameRun, "CombatState"), "Random");
            if (fromCombat != null) return fromCombat;

            try
            {
                Type gameRandomType = AccessTools.TypeByName("GameRandom");
                if (gameRandomType == null) return null;

                object created = Activator.CreateInstance(gameRandomType);

                FieldInfo inner = AccessTools.Field(gameRandomType, "random");
                if (inner != null && inner.GetValue(created) == null)
                {
                    inner.SetValue(created, new System.Random(12345));
                }
                FieldInfo seed = AccessTools.Field(gameRandomType, "Seed");
                if (seed != null) seed.SetValue(created, 12345);
                return created;
            }
            catch (Exception) { return null; }
        }

        internal static object Director()
        {
            try
            {
                Type routerHelper = AccessTools.TypeByName("RouterHelper");
                FieldInfo routerField = routerHelper == null ? null : AccessTools.Field(routerHelper, "_router");
                object router = routerField == null ? null : routerField.GetValue(null);
                return router == null ? null : ReadMember(router, "_adventureDirector");
            }
            catch (Exception) { return null; }
        }

        /// <summary>Field first, then property: game types use both, and a field-only read silently misses properties.</summary>
        internal static object ReadMember(object instance, string name)
        {
            if (instance == null) return null;
            try
            {
                FieldInfo f = AccessTools.Field(instance.GetType(), name);
                if (f != null) return f.GetValue(instance);
                PropertyInfo p = AccessTools.Property(instance.GetType(), name);
                return p == null ? null : p.GetValue(instance, null);
            }
            catch (Exception) { return null; }
        }
    }
}

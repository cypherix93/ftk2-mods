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

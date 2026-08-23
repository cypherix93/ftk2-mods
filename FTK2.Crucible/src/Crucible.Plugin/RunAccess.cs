using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Shared reflective access to the live run for the crucible_* debug/cheat verbs
    /// (DebugVerbCommands, ChaosCommands). Same posture as StateReader/GameBridge: every lookup
    /// degrades to null rather than throwing out to the caller.
    /// </summary>
    internal static class RunAccess
    {
        internal static object GetMember(object instance, string name)
        {
            if (instance == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                FieldInfo field = AccessTools.Field(instance.GetType(), name);
                if (field != null) return field.GetValue(instance);

                PropertyInfo prop = AccessTools.Property(instance.GetType(), name);
                if (prop != null) return prop.GetValue(instance, null);

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static object GetGameRun()
        {
            object env = GameBridge.GetEnv();
            return GetMember(env, "GameRun");
        }

        internal static object GetCombatState()
        {
            return GetMember(GetGameRun(), "CombatState");
        }

        internal static object GetAdventureState()
        {
            return GetMember(GetGameRun(), "AdventureState");
        }

        /// <summary>GameRunData.Entities (prop List`1, backed by the private _entities field).</summary>
        internal static IList GetRunEntities()
        {
            return GetMember(GetGameRun(), "Entities") as IList;
        }

        internal static object GetComponent(object entity, string componentTypeName)
        {
            object components = GetMember(entity, "Components");
            IDictionary map = components as IDictionary;
            if (map == null) return null;
            foreach (object value in map.Values)
            {
                if (value == null) continue;
                for (Type t = value.GetType(); t != null; t = t.BaseType)
                {
                    if (string.Equals(t.Name, componentTypeName, StringComparison.Ordinal)) return value;
                }
            }
            return null;
        }

        internal static bool HasComponent(object entity, string componentTypeName)
        {
            return GetComponent(entity, componentTypeName) != null;
        }

        /// <summary>
        /// Every live entity carrying a PlayerComponent, in <c>GameRunData.Entities</c> enumeration
        /// order. This is NOT a guaranteed persistent "slot id" -- the only confirmed ordered party
        /// list (<c>PartyManagementDirector._playerEntities</c>) is scoped to the PARTY_MANAGEMENT
        /// route (docs/research/crucible-traversal-inventory.md §3) -- but it is the best ordering
        /// available elsewhere. Every verb that resolves a slot this way also reports the resolved
        /// entity's DisplayName in its result, so a caller can confirm identity rather than trust the
        /// index blindly.
        /// </summary>
        internal static List<object> GetPartyEntities()
        {
            List<object> result = new List<object>();
            IList entities = GetRunEntities();
            if (entities == null) return result;
            foreach (object e in entities)
            {
                if (e != null && HasComponent(e, "PlayerComponent")) result.Add(e);
            }
            return result;
        }

        internal static string DisplayName(object entity)
        {
            object character = GetComponent(entity, "CharacterComponent");
            object name = GetMember(character, "DisplayName");
            return name as string;
        }

        internal static int CountOf(object collection)
        {
            ICollection c = collection as ICollection;
            return c != null ? c.Count : 0;
        }
    }
}

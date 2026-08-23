using System;
using System.Collections.Generic;
using System.Reflection;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Dispatch into <c>CharacterHelper</c>'s static helpers without hard-coding their signatures
    /// at every call site.
    ///
    /// The field map records these members by name and return type; the S1 addendum (Task 1,
    /// probed 2026-08-23) confirmed the actual signatures. <c>IsDead</c> and <c>GetBaseStats</c> are
    /// both unary (<c>(Entity)</c>) and are dispatched by <see cref="Invoke"/>, which selects the
    /// overload at runtime by which argument the parameter type actually accepts — the same
    /// mechanism <c>MemberResolver.FindUnaryStatic</c> uses elsewhere. <c>GetMaxHealth</c> is NOT
    /// unary — the addendum found exactly one overload, <c>(Entity pEntity, Boolean pCappedStat)</c>
    /// — so it gets its own dedicated dispatch, <see cref="InvokeMaxHealth"/>, rather than being
    /// forced through the unary path.
    /// </summary>
    internal static class CharacterHelperBridge
    {
        private static Type _type;
        private static bool _typeProbed;
        private static readonly Dictionary<string, MethodInfo> Resolved = new Dictionary<string, MethodInfo>();
        private static readonly Dictionary<string, bool> UsesCharacter = new Dictionary<string, bool>();

        private static MethodInfo _maxHealthMethod;
        private static bool _maxHealthProbed;

        internal static object Invoke(string methodName, object entity, object character, WarningSink warnings)
        {
            Type type = ResolveType(warnings);
            if (type == null) return null;

            MethodInfo method;
            bool useCharacter;

            if (!Resolved.TryGetValue(methodName, out method))
            {
                method = MemberResolver.FindUnaryStatic(type, methodName, entity, warnings);
                useCharacter = false;

                if (method == null && character != null)
                {
                    method = MemberResolver.FindUnaryStatic(type, methodName, character, warnings);
                    useCharacter = true;
                }

                // Cache only a decisive answer. If this combatant had no CharacterComponent then the
                // CharacterComponent overload was never actually tested, so leave the entry unresolved
                // and retry on the next combatant rather than poisoning the cache with a null.
                if (method != null || (entity != null && character != null))
                {
                    Resolved[methodName] = method;
                    UsesCharacter[methodName] = useCharacter;
                }
            }
            else
            {
                useCharacter = UsesCharacter[methodName];
            }

            if (method == null) return null;

            object arg = useCharacter ? character : entity;
            if (arg == null) return null;

            return MemberResolver.InvokeStatic(method, arg, "CharacterHelper." + methodName, warnings);
        }

        /// <summary>
        /// <c>CharacterHelper.GetMaxHealth(Entity pEntity, Boolean pCappedStat)</c> — grounded as
        /// binary by the Task 1 addendum, so it cannot go through <see cref="Invoke"/>'s unary
        /// dispatch. <c>pCappedStat: true</c> matches what the game itself shows on a health bar (a
        /// capped/effective max), not an uncapped theoretical one.
        /// </summary>
        internal static object InvokeMaxHealth(object entity, WarningSink warnings)
        {
            Type type = ResolveType(warnings);
            if (type == null || entity == null) return null;

            if (!_maxHealthProbed)
            {
                _maxHealthProbed = true;
                _maxHealthMethod = FindMaxHealthMethod(type, entity, warnings);
            }

            if (_maxHealthMethod == null) return null;

            try
            {
                return _maxHealthMethod.Invoke(null, new object[] { entity, true });
            }
            catch (Exception ex)
            {
                if (warnings != null)
                {
                    TargetInvocationException tie = ex as TargetInvocationException;
                    Exception inner = tie != null && tie.InnerException != null ? tie.InnerException : ex;
                    warnings.Add("invoke_failed: CharacterHelper.GetMaxHealth: " + inner.Message);
                }
                return null;
            }
        }

        private static MethodInfo FindMaxHealthMethod(Type declaring, object entity, WarningSink warnings)
        {
            for (Type t = declaring; t != null; t = t.BaseType)
            {
                MethodInfo[] all = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly);
                for (int i = 0; i < all.Length; i++)
                {
                    MethodInfo m = all[i];
                    if (!string.Equals(m.Name, "GetMaxHealth", StringComparison.Ordinal)) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 2) continue;
                    if (!ps[0].ParameterType.IsInstanceOfType(entity)) continue;
                    if (ps[1].ParameterType != typeof(bool)) continue;
                    return m;
                }
            }

            if (warnings != null)
                warnings.MemberMissing(declaring.Name, "GetMaxHealth(<Entity>, Boolean)");
            return null;
        }

        private static Type ResolveType(WarningSink warnings)
        {
            if (!_typeProbed)
            {
                _typeProbed = true;
                _type = AccessTools.TypeByName("CharacterHelper");
            }
            if (_type == null && warnings != null) warnings.TypeMissing("CharacterHelper");
            return _type;
        }
    }
}

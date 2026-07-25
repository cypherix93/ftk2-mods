using System;
using System.Collections.Generic;
using System.Reflection;
using FTK2Mods.DevKit;
using HarmonyLib;

namespace DevKit.Plugin
{
    /// <summary>
    /// Send side of the parity transport: a reflection call into
    /// <c>AdventureDirector._trySendNetworkAction</c>, the proven pattern EOR ships as a 13-arg
    /// reflection sender (docs/research/eor-0760-content-audit.md §4).
    ///
    /// Everything here is discovered at runtime and fails safe. The method's real parameter list is
    /// an open question (SPEC §11.8, MULTIPLAYER.md open question 5), so instead of hard-coding a
    /// signature this builds the argument array from <see cref="ParameterInfo"/>: the first string
    /// parameter receives the action key, the second receives the payload, everything else gets its
    /// declared default (or a zeroed value type / null). The resolved signature is logged verbatim
    /// on first use, so the first real run against the game produces the evidence needed to replace
    /// this heuristic with an exact call.
    ///
    /// If anything at all fails, sending is disabled for the session with one loud log line; the
    /// receive path keeps working and DevKit never throws into the game's network code.
    /// </summary>
    internal static class ParityTransport
    {
        private const string DirectorTypeName = "AdventureDirector";
        private const string SendMethodName = "_trySendNetworkAction";

        private static MethodInfo _sendMethod;
        private static ParameterInfo[] _sendParameters;
        private static int _actionKeyParameterIndex = -1;
        private static int _payloadParameterIndex = -1;
        private static object _directorInstance;
        private static bool _resolveAttempted;
        private static bool _sendDisabled;

        /// <summary>True once the sender resolved a usable target.</summary>
        internal static bool CanSend
        {
            get { return !_sendDisabled && _sendMethod != null; }
        }

        /// <summary>
        /// Caches the live <c>AdventureDirector</c> seen by a patch, so sending never has to guess at
        /// a singleton accessor. Called from both patch bodies.
        /// </summary>
        internal static void RememberDirector(object instance)
        {
            if (instance != null) _directorInstance = instance;
        }

        /// <summary>Sends one payload. Returns false if it could not be sent. Never throws.</summary>
        internal static bool Send(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return false;
            try
            {
                if (!Resolve()) return false;

                object target = _sendMethod.IsStatic ? null : ResolveDirectorInstance();
                if (!_sendMethod.IsStatic && target == null)
                {
                    DevKitPlugin.Log.LogWarning("ParityService: no live " + DirectorTypeName
                        + " instance to send through; parity payload dropped.");
                    return false;
                }

                object[] args = BuildArguments(payload);
                if (args == null) return false;

                _sendMethod.Invoke(target, args);
                DevKitPlugin.Verbose("ParityService: sent " + ParityPayloadCodec.PeekAction(payload)
                    + " (" + payload.Length + " chars).");
                return true;
            }
            catch (Exception ex)
            {
                _sendDisabled = true;
                DevKitPlugin.Log.LogError("ParityService: send failed, outbound parity disabled for this session ("
                    + DirectorTypeName + "." + SendMethodName + "): " + ex);
                return false;
            }
        }

        private static bool Resolve()
        {
            if (_sendDisabled) return false;
            if (_sendMethod != null) return true;
            if (_resolveAttempted) return false;
            _resolveAttempted = true;

            Type directorType = AccessTools.TypeByName(DirectorTypeName);
            if (directorType == null)
            {
                DevKitPlugin.Log.LogError("Target NOT found: " + DirectorTypeName
                    + " (type unresolved) - parity send disabled.");
                _sendDisabled = true;
                return false;
            }
            MethodInfo method = AccessTools.Method(directorType, SendMethodName);
            if (method == null)
            {
                DevKitPlugin.Log.LogError("Target NOT found: " + DirectorTypeName + "." + SendMethodName
                    + " - parity send disabled (receive-only).");
                _sendDisabled = true;
                return false;
            }

            _sendMethod = method;
            _sendParameters = method.GetParameters();
            _actionKeyParameterIndex = -1;
            _payloadParameterIndex = -1;
            for (int i = 0; i < _sendParameters.Length; i++)
            {
                if (_sendParameters[i].ParameterType != typeof(string)) continue;
                if (_actionKeyParameterIndex < 0) { _actionKeyParameterIndex = i; continue; }
                if (_payloadParameterIndex < 0) { _payloadParameterIndex = i; break; }
            }
            if (_actionKeyParameterIndex < 0)
            {
                DevKitPlugin.Log.LogError("Target found but unusable: " + DirectorTypeName + "." + SendMethodName
                    + " has no string parameter to carry the action key - parity send disabled. Signature: "
                    + DevKitPlugin.DescribeSignature(method));
                _sendDisabled = true;
                return false;
            }

            DevKitPlugin.Log.LogInfo("Target found: " + DirectorTypeName + "." + SendMethodName
                + " (" + DevKitPlugin.DescribeSignature(method) + "); action key -> parameter #"
                + _actionKeyParameterIndex
                + ", payload -> parameter #" + (_payloadParameterIndex < 0 ? "(none, payload carries its own Action)" : _payloadParameterIndex.ToString()));
            return true;
        }

        private static object[] BuildArguments(string payload)
        {
            string actionKey = ParityPayloadCodec.PeekAction(payload);
            if (string.IsNullOrEmpty(actionKey)) actionKey = ParityPayloadCodec.ParityActionKey;

            object[] args = new object[_sendParameters.Length];
            for (int i = 0; i < _sendParameters.Length; i++)
            {
                args[i] = DefaultValueFor(_sendParameters[i]);
            }
            if (_payloadParameterIndex >= 0)
            {
                args[_actionKeyParameterIndex] = actionKey;
                args[_payloadParameterIndex] = payload;
            }
            else
            {
                // Single string slot: send the whole self-describing payload; its "Action" member is
                // the routing key on the receive side either way.
                args[_actionKeyParameterIndex] = payload;
            }
            return args;
        }

        private static object DefaultValueFor(ParameterInfo parameter)
        {
            try
            {
                if (parameter.HasDefaultValue) return parameter.DefaultValue;
            }
            catch (Exception)
            {
                // Some Mono metadata throws on DefaultValue; fall through to the zero value.
            }
            Type t = parameter.ParameterType;
            if (t.IsByRef) t = t.GetElementType();
            if (t != null && t.IsValueType) return Activator.CreateInstance(t);
            return null;
        }

        private static object ResolveDirectorInstance()
        {
            if (_directorInstance != null) return _directorInstance;

            Type directorType = _sendMethod.DeclaringType;
            if (directorType == null) return null;

            // Try the usual singleton shapes before touching Unity's scene search.
            string[] candidates = new string[] { "Instance", "instance", "_instance", "Current", "S", "Singleton" };
            for (int i = 0; i < candidates.Length; i++)
            {
                object found = TryStaticMember(directorType, candidates[i]);
                if (found != null)
                {
                    _directorInstance = found;
                    return found;
                }
            }

            try
            {
                if (typeof(UnityEngine.Object).IsAssignableFrom(directorType))
                {
                    UnityEngine.Object found = UnityEngine.Object.FindObjectOfType(directorType);
                    if (found != null)
                    {
                        _directorInstance = found;
                        return found;
                    }
                }
            }
            catch (Exception ex)
            {
                DevKitPlugin.Verbose("ParityService: FindObjectOfType(" + directorType.Name + ") failed: " + ex.Message);
            }
            return null;
        }

        private static object TryStaticMember(Type type, string name)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo field = type.GetField(name, flags);
                if (field != null && type.IsAssignableFrom(field.FieldType)) return field.GetValue(null);
                PropertyInfo property = type.GetProperty(name, flags);
                if (property != null && property.CanRead && type.IsAssignableFrom(property.PropertyType))
                    return property.GetValue(null, null);
            }
            catch (Exception)
            {
            }
            return null;
        }

        /// <summary>Diagnostics for <c>dk_dump_parity</c>: what the sender resolved to, if anything.</summary>
        internal static string DescribeState()
        {
            if (_sendDisabled) return "send: DISABLED (target unresolved or a send failed)";
            if (_sendMethod == null) return "send: not resolved yet";
            List<string> notes = new List<string>();
            notes.Add(DevKitPlugin.DescribeSignature(_sendMethod));
            notes.Add("instance=" + (_directorInstance != null ? "cached" : "none"));
            return "send: " + string.Join(" | ", notes.ToArray());
        }
    }
}

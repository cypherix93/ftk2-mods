using System;
using System.Collections.Generic;
using System.Reflection;
using FTK2Mods.DevKit;
using HarmonyLib;

namespace DevKit.Plugin
{
    /// <summary>
    /// Send side of the parity transport.
    ///
    /// <b>The channel, and the evidence for it.</b> The wire format is a closed ProtoBuf hierarchy —
    /// <c>GameActionDataBase</c> is <c>[ProtoInclude(1..23)]</c>
    /// (<c>tools/out/decompile/FTK2/GameActionDataBase.cs:5-27</c>) with no runtime extension point —
    /// so a bespoke string action does not exist. A payload must ride inside an existing action's
    /// free-form slot. Exactly one root sender reaches that slot:
    ///
    /// <code>
    /// // tools/out/decompile/FTK2/AdventureDirector.cs:15135
    /// private bool _trySendNetworkAction(eAdventureActions pAdventureAction, eEncounterActions pEncounterAction,
    ///     Entity pPlayerEntity, Entity pEncounterEntity, object pResultArgs, bool pMove, bool pTeamUp,
    ///     (int,int) pPosition, int pFocusUsed, eTownServiceTypes pTownAction = NONE, int pThingIndex = -1,
    ///     bool pIsActivePlayerOnly = true, eSkills pSkill = NONE)
    /// </code>
    ///
    /// It forwards <c>pResultArgs</c> to <c>_createAdventureAction</c> (<c>:15154</c>) and on to
    /// <c>AdventureActionData.Create</c>, then broadcasts via
    /// <c>NetworkHelper.BroadcastActionMessage(eActionType.AdventureAction, ...)</c> (<c>:15146</c>).
    /// <c>NetworkHelper.BroadcastActionMessage</c> (<c>NetworkHelper.cs:91-119</c>) wraps whatever
    /// <c>GameActionDataBase</c> it is handed and does <b>no</b> filtering by action type, so any case
    /// <c>Create</c> supports is transportable.
    ///
    /// <c>AdventureActionData.Create</c> puts a caller-supplied string on the wire in exactly two
    /// cases (verified by sweeping every <c>public string</c> in
    /// <c>tools/out/decompile/FTK2/AdventureActionData.cs</c>):
    ///  1. <c>:306</c> — <c>ENCOUNTER_ACTION</c> → <c>EncounterActionActionData.SkillEncounterConfigId
    ///     = (string)pResultArgs</c> (field declared at <c>:165</c>, <c>[ProtoMember(3)]</c>).
    ///  2. <c>:275</c> — <c>DEBUG_GET_SPECIFIC_THING</c> →
    ///     <c>DebugGetSpecificThingActionData.ThingData = ((string,int))pResultArgs</c>
    ///     (field declared at <c>:179</c>, <c>[ProtoMember(1)]</c>).
    ///
    /// <b>EOR uses (1)</b>: <c>tools/out/decompile/EOR-0.7.0.60/FTK2/BanditKingPlayable/Plugin.cs</c>
    /// <c>:12579-12617</c> binds that overload by explicit 13-entry <c>Type[]</c> and invokes it with
    /// <c>(ENCOUNTER_ACTION, TOWN_SERVICES, entity, null, "EOR_SYNC_...|...", false, false, (0,0), 0,
    /// eTownServiceTypes.NONE, -1, true, eSkills.NONE)</c>; it reads the string back off
    /// <c>SkillEncounterConfigId</c> at <c>:12978-12988</c> and <c>:13253-13263</c>.
    ///
    /// <b>Why DevKit defaults to (2) instead.</b> EOR only gets away with (1) because its
    /// <c>_handleNetworkAction</c> prefix returns <c>false</c> and suppresses the vanilla handler for
    /// its own payloads (audit §3.4). DevKit's receive hook is observe-only by contract — it must
    /// never skip or mutate the original — so vanilla WILL process whatever we forge. For
    /// <c>ENCOUNTER_ACTION</c>+<c>TOWN_SERVICES</c> that is not inert: <c>AdventureDirector.cs:15196</c>
    /// renders a popcorn message and <c>:15218</c> calls <c>_performEncounterAction</c>, whose prologue
    /// increments <c>SkillEncounterComponent.Attempts</c> (<c>:9930-9933</c>) and can burn focus via
    /// <c>CharacterHelper.CommitFocus</c> (<c>:9923-9929</c>) — real simulation state — and dereferences
    /// <c>_encounterEntity</c> unguarded at <c>:9914</c>, which is null at session start.
    ///
    /// <c>DEBUG_GET_SPECIFIC_THING</c> has <b>no case</b> in <c>_handleNetworkAction</c>'s switch
    /// (its only two references in the whole decompile are the enum declaration
    /// <c>eAdventureActions.cs:19</c> and the <c>Create</c> case <c>AdventureActionData.cs:272</c>),
    /// so it falls to the <c>default:</c> arm at <c>AdventureDirector.cs:15389-15392</c>, which logs
    /// <c>"... is not accounted for"</c> and calls <c>_tryPlayNextNetworkAction()</c>. That is exactly
    /// the behaviour we want: <b>zero</b> simulation side effects, and the network pump is still
    /// advanced so the action queue cannot stall. The cost is one vanilla <c>Debug.LogError</c> line
    /// per payload on every peer — cosmetic, and symmetric across peers.
    ///
    /// The EOR-identical channel is kept behind <c>[Multiplayer] ParityChannel = EorTownServices</c>
    /// as an escape hatch in case a future build filters unhandled action types in netcode.
    ///
    /// <b>Fail-safe.</b> Any resolution failure logs a "Target NOT found"-style line and turns sending
    /// off; nothing here throws into the game's network code. Invoke failures disable sending for the
    /// SESSION only and are re-probed on the next session start (MP review M4b).
    /// </summary>
    internal static class ParityTransport
    {
        private const string DirectorTypeName = "AdventureDirector";
        private const string SendMethodName = "_trySendNetworkAction";

        /// <summary>Consecutive invoke failures tolerated in one session before we stop trying (M4b).</summary>
        private const int MaxSendFailuresPerSession = 3;

        private static MethodInfo _sendMethod;
        private static ParameterInfo[] _sendParameters;
        private static object _directorInstance;

        // Enum constants resolved once against the live assembly.
        private static object _adventureActionValue;
        private static object _encounterActionValue;
        private static object _townServiceNoneValue;
        private static object _skillNoneValue;
        private static Type _stringIntTupleType;
        private static bool _payloadIsPlainString;

        private static bool _resolveAttempted;
        /// <summary>Permanent: the target itself could not be resolved. Re-probing cannot help.</summary>
        private static bool _unresolvable;
        /// <summary>Per-session: invokes are failing. Cleared by <see cref="ResetForNewSession"/> (M4b).</summary>
        private static bool _sendDisabledThisSession;
        private static int _sendFailuresThisSession;

        /// <summary>True once the sender resolved a usable target and is not disabled this session.</summary>
        internal static bool CanSend
        {
            get { return !_unresolvable && !_sendDisabledThisSession && _sendMethod != null; }
        }

        /// <summary>
        /// Clears the per-session send lockout so a transient transport failure blinds a peer for one
        /// session at most, not for the process (MP review M4b). Permanent resolution failures are
        /// deliberately NOT cleared — re-probing a method that does not exist just spams the log.
        /// </summary>
        internal static void ResetForNewSession()
        {
            _sendDisabledThisSession = false;
            _sendFailuresThisSession = 0;
        }

        /// <summary>
        /// Caches the live <c>AdventureDirector</c> seen by a patch, so sending never has to guess at
        /// a singleton accessor. Called from both patch bodies.
        /// </summary>
        internal static void RememberDirector(object instance)
        {
            if (instance != null) _directorInstance = instance;
        }

        /// <summary>The cached director, for the session-flag probe. May be null before any patch fires.</summary>
        internal static object CurrentDirector { get { return _directorInstance; } }

        /// <summary>Sends one payload. Returns false if it could not be sent. Never throws.</summary>
        internal static bool Send(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return false;
            try
            {
                if (!Resolve()) return false;
                if (_sendDisabledThisSession) return false;

                object target = ResolveDirectorInstance();
                if (target == null)
                {
                    DevKitPlugin.Log.LogWarning("ParityService: no live " + DirectorTypeName
                        + " instance to send through; parity payload dropped.");
                    return false;
                }

                object[] args = BuildArguments(payload);
                if (args == null) return false;

                _sendMethod.Invoke(target, args);
                _sendFailuresThisSession = 0;
                DevKitPlugin.Verbose("ParityService: sent " + ParityPayloadCodec.PeekAction(payload)
                    + " (" + payload.Length + " bytes) via " + DescribeChannel() + ".");
                return true;
            }
            catch (Exception ex)
            {
                _sendFailuresThisSession++;
                bool giveUp = _sendFailuresThisSession >= MaxSendFailuresPerSession;
                if (giveUp) _sendDisabledThisSession = true;
                DevKitPlugin.Log.LogError("ParityService: send failed (" + _sendFailuresThisSession + "/"
                    + MaxSendFailuresPerSession + ") through " + DirectorTypeName + "." + SendMethodName
                    + (giveUp
                        ? " - outbound parity disabled FOR THIS SESSION; it is re-probed on the next session start."
                        : " - will retry on the next parity send this session.")
                    + " " + ex);
                return false;
            }
        }

        private static bool Resolve()
        {
            if (_unresolvable) return false;
            if (_sendMethod != null) return true;
            if (_resolveAttempted) return false;
            _resolveAttempted = true;

            Type directorType = AccessTools.TypeByName(DirectorTypeName);
            if (directorType == null) return Unresolvable(DirectorTypeName + " (type unresolved)");

            // Pick the root overload out of the 8 by its REAL ParameterInfo shape, then validate that
            // shape member by member (MP review B1: AccessTools.Method(type, name) with no argumentTypes
            // is an ambiguous lookup across AdventureDirector.cs:15100-15135 and is not a stable way to
            // reach any of them).
            //
            // Shape-matching rather than an exact Type[] is deliberate. The signature contains
            // `(int,int) pPosition`, and a ValueTuple that the game resolves out of a separate
            // System.ValueTuple assembly is NOT type-identical to this plugin's `typeof(ValueTuple<int,int>)`
            // from mscorlib; an exact-types lookup would then silently return null on a machine where
            // that split exists. Matching on arity + the anchors below cannot miss for that reason, and
            // every value we later pass is built from the ParameterInfo itself.
            MethodInfo method = FindRootSendOverload(directorType);
            if (method == null)
            {
                return Unresolvable(DirectorTypeName + "." + SendMethodName
                    + " (13-arg root overload, AdventureDirector.cs:15135)");
            }

            _sendMethod = method;
            _sendParameters = method.GetParameters();

            Type adventureActionsType = _sendParameters[0].ParameterType;
            Type encounterActionsType = _sendParameters[1].ParameterType;
            Type townServiceTypesType = _sendParameters[9].ParameterType;
            Type skillsType = _sendParameters[12].ParameterType;

            if (!ResolveChannelConstants(adventureActionsType, encounterActionsType, townServiceTypesType, skillsType))
            {
                _sendMethod = null;
                return false;
            }

            DevKitPlugin.Log.LogInfo("Target found: " + DirectorTypeName + "." + SendMethodName
                + " (" + DevKitPlugin.DescribeSignature(method) + "); channel=" + DescribeChannel());
            return true;
        }

        /// <summary>
        /// Finds the single 13-parameter <c>_trySendNetworkAction</c> and proves it is the root
        /// overload from <c>AdventureDirector.cs:15135</c> before we agree to call it. The anchors are
        /// the slots we actually depend on:
        ///  - #0 <c>eAdventureActions</c> and #1 <c>eEncounterActions</c> — enums, and they select the
        ///    <c>AdventureActionData.Create</c> case that carries our string;
        ///  - #4 <c>object pResultArgs</c> — the payload slot itself;
        ///  - #7 <c>(int,int) pPosition</c> — a 2-arg generic value type, the donor we build
        ///    <c>ValueTuple&lt;string,int&gt;</c> from;
        ///  - #9 <c>eTownServiceTypes</c> / #12 <c>eSkills</c> — enums;
        ///  - #11 <c>bool pIsActivePlayerOnly</c> — the flag that decides whether an off-turn peer may
        ///    broadcast at all.
        /// Anything that fails these is not the method we read in the decompile, so we refuse it and
        /// disable sending rather than invoke something we have not verified.
        /// </summary>
        private static MethodInfo FindRootSendOverload(Type directorType)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                MethodInfo best = null;
                Type cursor = directorType;
                while (cursor != null && best == null)
                {
                    MethodInfo[] methods = cursor.GetMethods(flags);
                    for (int i = 0; i < methods.Length; i++)
                    {
                        if (!string.Equals(methods[i].Name, SendMethodName, StringComparison.Ordinal)) continue;
                        if (methods[i].GetParameters().Length != 13) continue;
                        best = methods[i];
                        break;
                    }
                    cursor = cursor.BaseType;
                }
                if (best == null) return null;

                ParameterInfo[] p = best.GetParameters();
                bool shapeOk =
                    p[0].ParameterType.IsEnum &&
                    p[1].ParameterType.IsEnum &&
                    p[4].ParameterType == typeof(object) &&
                    p[5].ParameterType == typeof(bool) &&
                    p[6].ParameterType == typeof(bool) &&
                    p[7].ParameterType.IsValueType && p[7].ParameterType.IsGenericType
                        && p[7].ParameterType.GetGenericArguments().Length == 2 &&
                    p[8].ParameterType == typeof(int) &&
                    p[9].ParameterType.IsEnum &&
                    p[10].ParameterType == typeof(int) &&
                    p[11].ParameterType == typeof(bool) &&
                    p[12].ParameterType.IsEnum;
                if (!shapeOk)
                {
                    DevKitPlugin.Log.LogError("Target found but unusable: " + DirectorTypeName + "."
                        + SendMethodName + " has 13 parameters but not the expected shape - "
                        + DevKitPlugin.DescribeSignature(best));
                    return null;
                }
                return best;
            }
            catch (Exception ex)
            {
                DevKitPlugin.Log.LogError("ParityService: could not enumerate " + DirectorTypeName + "."
                    + SendMethodName + " overloads: " + ex);
                return null;
            }
        }

        /// <summary>
        /// Resolves the enum members and the payload boxing strategy for the configured channel.
        /// Both channels use the SAME method and the SAME <c>pResultArgs</c> slot; they differ only in
        /// which <c>AdventureActionData.Create</c> case receives the string.
        /// </summary>
        private static bool ResolveChannelConstants(Type adventureActionsType, Type encounterActionsType,
            Type townServiceTypesType, Type skillsType)
        {
            bool useEorChannel = DevKitPlugin.UseEorParityChannel();

            _townServiceNoneValue = ParseEnum(townServiceTypesType, "NONE");
            _skillNoneValue = ParseEnum(skillsType, "NONE");
            if (_townServiceNoneValue == null || _skillNoneValue == null)
                return Unresolvable("eTownServiceTypes.NONE / eSkills.NONE");

            if (useEorChannel)
            {
                // EOR-identical: AdventureActionData.cs:306 does `(string)pResultArgs`, so the slot
                // takes a bare string. Plugin.cs:12600-12617 is the proven invocation.
                _adventureActionValue = ParseEnum(adventureActionsType, "ENCOUNTER_ACTION");
                _encounterActionValue = ParseEnum(encounterActionsType, "TOWN_SERVICES");
                _payloadIsPlainString = true;
                if (_adventureActionValue == null || _encounterActionValue == null)
                    return Unresolvable("eAdventureActions.ENCOUNTER_ACTION / eEncounterActions.TOWN_SERVICES");
                return true;
            }

            // Default: AdventureActionData.cs:275 does `((string,int))pResultArgs`, so the slot needs a
            // boxed ValueTuple<string,int>. Build that generic type from the game's OWN ValueTuple<int,int>
            // parameter (pPosition, index 7) rather than from our typeof, so the runtime type identity is
            // guaranteed to be the one the game's cast expects even if a shim assembly is in play.
            _adventureActionValue = ParseEnum(adventureActionsType, "DEBUG_GET_SPECIFIC_THING");
            _encounterActionValue = ParseEnum(encounterActionsType, "NONE");
            _payloadIsPlainString = false;
            if (_adventureActionValue == null || _encounterActionValue == null)
                return Unresolvable("eAdventureActions.DEBUG_GET_SPECIFIC_THING / eEncounterActions.NONE");

            try
            {
                Type positionType = _sendParameters[7].ParameterType;
                if (!positionType.IsGenericType) return Unresolvable("pPosition is not a generic ValueTuple");
                _stringIntTupleType = positionType.GetGenericTypeDefinition()
                    .MakeGenericType(typeof(string), typeof(int));
            }
            catch (Exception ex)
            {
                return Unresolvable("ValueTuple<string,int> could not be constructed: " + ex.Message);
            }
            return true;
        }

        private static object[] BuildArguments(string payload)
        {
            object[] args = new object[_sendParameters.Length];
            args[0] = _adventureActionValue;
            args[1] = _encounterActionValue;
            args[2] = null;   // pPlayerEntity: _createAdventureAction maps null -> PlayerIndex -1 (:15156), no deref.
            args[3] = null;   // pEncounterEntity: null -> no display-name lookup (:15157).
            args[4] = BuildPayloadArgument(payload);
            args[5] = false;  // pMove
            args[6] = false;  // pTeamUp
            args[7] = Activator.CreateInstance(_sendParameters[7].ParameterType);   // pPosition = (0,0)
            args[8] = 0;      // pFocusUsed
            args[9] = _townServiceNoneValue;
            args[10] = -1;    // pThingIndex
            // pIsActivePlayerOnly: false. The guard at AdventureDirector.cs:15140 is
            //   (_activeCharacterEntity == null || !IsRemotePlayer || !pIsActivePlayerOnly)
            // so `true` would silently drop our broadcast on any peer whose active character is remote —
            // i.e. on every peer whose turn it is not, which is most of them at session start. `false` is
            // a vanilla-exercised value (AdventureDirector.cs:13556 sends MARKER with it).
            args[11] = false;
            args[12] = _skillNoneValue;
            if (args[4] == null) return null;
            return args;
        }

        private static object BuildPayloadArgument(string payload)
        {
            if (_payloadIsPlainString) return payload;
            try
            {
                return Activator.CreateInstance(_stringIntTupleType, new object[] { payload, 0 });
            }
            catch (Exception ex)
            {
                DevKitPlugin.Log.LogError("ParityService: could not box the payload as ValueTuple<string,int>: " + ex);
                return null;
            }
        }

        private static object ParseEnum(Type enumType, string memberName)
        {
            try
            {
                if (enumType == null || !enumType.IsEnum) return null;
                if (!Array.Exists(Enum.GetNames(enumType),
                        delegate (string n) { return string.Equals(n, memberName, StringComparison.Ordinal); }))
                {
                    return null;
                }
                return Enum.Parse(enumType, memberName, false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool Unresolvable(string what)
        {
            _unresolvable = true;
            DevKitPlugin.Log.LogError("Target NOT found: " + what
                + " - parity send disabled for this process (receive-only).");
            return false;
        }

        private static object ResolveDirectorInstance()
        {
            if (_directorInstance != null) return _directorInstance;

            Type directorType = _sendMethod == null ? null : _sendMethod.DeclaringType;
            if (directorType == null) return null;

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

        private static string DescribeChannel()
        {
            return _payloadIsPlainString
                ? "ENCOUNTER_ACTION/TOWN_SERVICES -> EncounterActionActionData.SkillEncounterConfigId (EOR-identical)"
                : "DEBUG_GET_SPECIFIC_THING -> DebugGetSpecificThingActionData.ThingData.Item1";
        }

        /// <summary>Diagnostics for <c>dk_dump_parity</c>: what the sender resolved to, if anything.</summary>
        internal static string DescribeState()
        {
            if (_unresolvable) return "send: DISABLED (target unresolved; receive-only for this process)";
            if (_sendDisabledThisSession) return "send: disabled for THIS SESSION after "
                + MaxSendFailuresPerSession + " failures (re-probed next session)";
            if (_sendMethod == null) return "send: not resolved yet";
            List<string> notes = new List<string>();
            notes.Add(DevKitPlugin.DescribeSignature(_sendMethod));
            notes.Add("channel=" + DescribeChannel());
            notes.Add("instance=" + (_directorInstance != null ? "cached" : "none"));
            return "send: " + string.Join(" | ", notes.ToArray());
        }
    }
}

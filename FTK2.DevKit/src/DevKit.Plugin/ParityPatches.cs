using System;
using System.Reflection;

namespace DevKit.Plugin
{
    /// <summary>
    /// Harmony patch bodies for the ParityService transport. Both are strictly observational.
    ///
    /// <b>Why the receive hook is a void Prefix.</b> A void prefix cannot return false, so it cannot
    /// skip the original method — the compiler enforces the property we care about. EOR's own
    /// <c>_handleNetworkAction</c> prefix returns <c>false</c> for its custom payloads and therefore
    /// swallows messages, which is exactly the hazard called out in
    /// docs/research/eor-0760-content-audit.md §3.4 (an unmodded peer's forged action then routes
    /// into the vanilla handler — undefined behavior). DevKit reads the payload and always lets the
    /// game's own handler run, unaltered, with its return value untouched.
    ///
    /// <b>What it reads.</b> <c>AdventureDirector._handleNetworkAction</c> takes exactly one
    /// argument, a ProtoBuf <c>GameAction</c> (<c>tools/out/decompile/FTK2/AdventureDirector.cs:15166</c>:
    /// <c>protected override async Task _handleNetworkAction(GameAction pGameAction)</c>). The
    /// previous implementation scanned <c>__args</c> for a <c>string</c>, which is always null and
    /// could never observe anything (MP review B2). The walk below mirrors, member for member, EOR's
    /// own extraction at
    /// <c>tools/out/decompile/EOR-0.7.0.60/FTK2/BanditKingPlayable/Plugin.cs:12978-12988</c>:
    ///
    /// <code>
    /// gameAction.ActionType == eActionType.AdventureAction
    ///   &amp;&amp; gameAction.Data is AdventureActionData a          // GameActionDataBase.cs:5 ProtoInclude(1)
    ///   &amp;&amp; a.Data is &lt;subtype&gt;                                // AdventureActionData.cs:8-24
    /// </code>
    ///
    /// Two subtypes are accepted, matching the two channels <see cref="ParityTransport"/> can send on:
    ///  - <c>DebugGetSpecificThingActionData.ThingData.Item1</c> — <c>AdventureActionData.cs:179</c>,
    ///    written from <c>pResultArgs</c> at <c>:275</c> (DevKit's default channel);
    ///  - <c>EncounterActionActionData.SkillEncounterConfigId</c> — <c>AdventureActionData.cs:165</c>,
    ///    written from <c>pResultArgs</c> at <c>:306</c> (the EOR-identical channel).
    ///
    /// Accepting both on receive is free and makes the channel knob a send-side-only decision, so two
    /// peers configured differently still complete the handshake.
    ///
    /// Everything is reflective and cached: no compile-time game types, and a renamed member degrades
    /// to "we never see a payload", never to a throw inside the game's network handler.
    /// </summary>
    internal static class ParityPatches
    {
        private const string DebugThingSubtype = "DebugGetSpecificThingActionData";
        private const string EncounterActionSubtype = "EncounterActionActionData";
        private const string ThingDataField = "ThingData";
        private const string SkillEncounterConfigIdField = "SkillEncounterConfigId";

        private static PropertyInfo _gameActionDataProperty;
        private static FieldInfo _gameActionDataField;
        private static FieldInfo _adventureActionDataField;
        private static PropertyInfo _adventureActionDataProperty;
        private static Type _lastGameActionType;
        private static Type _lastAdventureActionType;

        internal static void HandleNetworkActionPrefix(object __instance, object[] __args)
        {
            try
            {
                if (__instance != null) ParityTransport.RememberDirector(__instance);
                if (__args == null || __args.Length == 0) return;

                string payload = ExtractPayload(__args[0]);
                if (payload == null) return;
                ParityCoordinator.OnNetworkPayloadObserved(payload);
                // M-LG3: generic sibling-mod dispatch (TransportService.RegisterReceiver). Additive —
                // parity's own hardwired path above is untouched either way (see TransportService's
                // header for why the two can never collide).
                TransportService.DispatchReceived(payload);
            }
            catch (Exception ex)
            {
                // Never rethrow from a patch body: an exception here would propagate into the game's
                // network handler. Log and let vanilla continue untouched (fail-safe rule).
                SafeLog("DevKit parity receive hook error (ignored): " + ex);
            }
        }

        /// <summary>
        /// Walks <c>GameAction -&gt; Data (AdventureActionData) -&gt; Data (ActionBase subtype)</c> and
        /// returns the carried string, or null if this action is not one of ours. Pure read; touches
        /// nothing on the action.
        /// </summary>
        private static string ExtractPayload(object gameAction)
        {
            if (gameAction == null) return null;

            object adventureActionData = ReadDataMember(gameAction, ref _lastGameActionType,
                ref _gameActionDataProperty, ref _gameActionDataField);
            if (adventureActionData == null) return null;

            // We do not need to test GameAction.ActionType: reaching an AdventureActionData at all
            // implies eActionType.AdventureAction, and matching the leaf subtype below is stricter
            // than EOR's check. One fewer reflective read on a hot path.
            object leaf = ReadDataMember(adventureActionData, ref _lastAdventureActionType,
                ref _adventureActionDataProperty, ref _adventureActionDataField);
            if (leaf == null) return null;

            Type leafType = leaf.GetType();
            if (string.Equals(leafType.Name, DebugThingSubtype, StringComparison.Ordinal))
                return ReadThingDataItem1(leaf, leafType);
            if (string.Equals(leafType.Name, EncounterActionSubtype, StringComparison.Ordinal))
                return ReadStringField(leaf, leafType, SkillEncounterConfigIdField);
            return null;
        }

        /// <summary>
        /// Reads a member named "Data" off <paramref name="instance"/>, caching the accessor per
        /// declaring type. Both <c>GameAction.Data</c> and <c>AdventureActionData.Data</c> use that
        /// name; whether each is a field or a property is left to the runtime rather than assumed.
        /// </summary>
        private static object ReadDataMember(object instance, ref Type cachedType,
            ref PropertyInfo cachedProperty, ref FieldInfo cachedField)
        {
            Type type = instance.GetType();
            if (cachedType != type)
            {
                cachedProperty = null;
                cachedField = null;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo property = type.GetProperty("Data", flags);
                if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                {
                    cachedProperty = property;
                }
                else
                {
                    cachedField = type.GetField("Data", flags);
                }
                cachedType = type;
            }
            if (cachedProperty != null) return cachedProperty.GetValue(instance, null);
            if (cachedField != null) return cachedField.GetValue(instance);
            return null;
        }

        /// <summary>
        /// <c>DebugGetSpecificThingActionData.ThingData</c> is a <c>(string,int)</c> ValueTuple
        /// (AdventureActionData.cs:179); the payload is <c>Item1</c>.
        /// </summary>
        private static string ReadThingDataItem1(object leaf, Type leafType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo thingData = leafType.GetField(ThingDataField, flags);
            if (thingData == null) return null;
            object tuple = thingData.GetValue(leaf);
            if (tuple == null) return null;
            FieldInfo item1 = tuple.GetType().GetField("Item1", flags);
            if (item1 == null) return null;
            return item1.GetValue(tuple) as string;
        }

        private static string ReadStringField(object leaf, Type leafType, string fieldName)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo field = leafType.GetField(fieldName, flags);
            if (field == null) return null;
            return field.GetValue(leaf) as string;
        }

        internal static void AdventureDirectorInitializePostfix(object __instance)
        {
            try
            {
                if (__instance != null) ParityTransport.RememberDirector(__instance);
                ParityCoordinator.OnSessionStarted(__instance);
            }
            catch (Exception ex)
            {
                SafeLog("DevKit parity session-start hook error (ignored): " + ex);
            }
        }

        private static void SafeLog(string message)
        {
            try
            {
                if (DevKitPlugin.Log != null) DevKitPlugin.Log.LogWarning(message);
            }
            catch (Exception)
            {
            }
        }
    }
}

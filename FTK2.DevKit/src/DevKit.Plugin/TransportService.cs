using System;
using System.Collections.Generic;
using FTK2Mods.DevKit;

namespace DevKit.Plugin
{
    /// <summary>
    /// M-LG3 OQ-1 pilot decision (docs/superpowers/plans/2026-08-05-loot-grant-verb-spec.md §1.5/§9 OQ-1):
    /// the minimal PUBLIC transport surface DevKit exposes to sibling mods, resolved the same
    /// reflection-consumable way <c>FTK2Mods.DevKit.ParityService.Register</c> already is (DevKit SPEC
    /// §3). It adds zero wire mechanism — every payload still rides <see cref="ParityTransport"/>'s
    /// existing send path and the existing observe-only <c>_handleNetworkAction</c> receive hook
    /// (<see cref="ParityPatches"/>) — only a registration surface so a sibling can claim its own
    /// <c>"Action"</c> key without DevKit hardcoding it. This deliberately does NOT tip DevKit SPEC
    /// §11.9's contracts-DLL question (verb spec §1.5); it is the same soft-dependency convention as
    /// <c>ParityService.Register</c>, one assembly further out.
    ///
    /// <b>Reflection-friendly by design</b> (mirrors <c>ParityService</c>'s own doc comment): every
    /// entry point is <c>public static</c>, BCL-typed (<c>string</c>, <c>bool</c>,
    /// <c>Action&lt;string&gt;</c>), overload-free, so a sibling mod can resolve this type by
    /// assembly-qualified name and <c>GetMethod(name)</c> with no disambiguation:
    /// <code>
    /// var t = Type.GetType("DevKit.Plugin.TransportService, FTK2.DevKit");
    /// if (t != null) // DevKit not installed -> no-op, never a hard failure
    /// {
    ///     bool canSend = (bool)t.GetProperty("CanSend").GetValue(null, null);
    ///     t.GetMethod("RegisterReceiver").Invoke(null, new object[] {
    ///         "MY_ACTION_KEY", new Action&lt;string&gt;(OnMyPayload) });
    ///     t.GetMethod("Send").Invoke(null, new object[] { myPayloadJson });
    /// }
    /// </code>
    /// Note the assembly is <c>FTK2.DevKit</c> (DevKit.Plugin's own <c>AssemblyName</c>), NOT
    /// <c>ftk2mods.devkit</c> (that is DevKit.Core's — where <c>ParityService</c> lives). The two
    /// services live in different assemblies on purpose: DevKit.Core is host-agnostic (no BepInEx, no
    /// Unity), while <see cref="ParityTransport"/> — and therefore this class — needs the live game
    /// surface.
    ///
    /// <b>Everything here is fail-safe</b> (docs/CONVENTIONS.md): no public method throws. A sibling's
    /// handler that throws is caught and isolated in <see cref="DispatchReceived"/> so one bad mod's
    /// receiver can never abort another's dispatch, nor propagate into the game's network pump.
    /// </summary>
    public static class TransportService
    {
        /// <summary>True once the send path has resolved a usable target and is not disabled this
        /// session — delegates straight to <see cref="ParityTransport.CanSend"/>, the same flag
        /// parity's own sends are gated on.</summary>
        public static bool CanSend
        {
            get
            {
                try { return ParityTransport.CanSend; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>
        /// Sends one payload on DevKit's existing channel, boxed exactly as parity payloads are
        /// (<see cref="ParityTransport.Send"/> — the same <c>DEBUG_GET_SPECIFIC_THING</c>/
        /// <c>ENCOUNTER_ACTION</c> carrier per the <c>[Multiplayer] ParityChannel</c> knob). Returns
        /// false if it could not be sent. Never throws.
        /// </summary>
        public static bool Send(string payloadJson)
        {
            try
            {
                return ParityTransport.Send(payloadJson);
            }
            catch (Exception ex)
            {
                SafeLog("TransportService.Send failed (ignored): " + ex);
                return false;
            }
        }

        /// <summary>
        /// Registers (or replaces) the handler for one action key. Whenever a payload is decoded off
        /// <c>AdventureDirector._handleNetworkAction</c> (the same observe-only walk
        /// <see cref="ParityPatches"/> already performs for parity's own traffic) whose leading
        /// <c>"Action"</c> JSON member equals <paramref name="actionKey"/>, <paramref name="handler"/>
        /// is invoked with the raw payload string. Parity's own <c>FTK2MODS_PARITY_V1</c>/
        /// <c>FTK2MODS_PARITY_REQUEST_V1</c> payloads keep their existing hardwired path through
        /// <see cref="ParityCoordinator"/> untouched — this registry is consulted in addition to that
        /// path, never instead of it, and a sibling can never register those two reserved keys away
        /// from parity because <see cref="ParityCoordinator.OnNetworkPayloadObserved"/> is not routed
        /// through this dictionary at all. A null/empty <paramref name="actionKey"/> or null
        /// <paramref name="handler"/> is rejected (logged, no-op). Never throws.
        /// </summary>
        public static void RegisterReceiver(string actionKey, Action<string> handler)
        {
            try
            {
                if (string.IsNullOrEmpty(actionKey) || handler == null)
                {
                    SafeLog("TransportService.RegisterReceiver rejected: actionKey/handler missing.");
                    return;
                }
                lock (Sync) { Handlers[actionKey] = handler; }
                DevKitPlugin.Verbose("TransportService: registered receiver for action '" + actionKey + "'.");
            }
            catch (Exception ex)
            {
                SafeLog("TransportService.RegisterReceiver failed (ignored): " + ex);
            }
        }

        // ---------------------------------------------------------------- receive dispatch (internal)

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Action<string>> Handlers =
            new Dictionary<string, Action<string>>(StringComparer.Ordinal);

        /// <summary>
        /// Called from <see cref="ParityPatches.HandleNetworkActionPrefix"/> for every payload
        /// extracted off the network hook — including parity's own, which is harmless:
        /// <c>PeekAction</c> on a parity payload returns <c>FTK2MODS_PARITY_V1</c>/
        /// <c>FTK2MODS_PARITY_REQUEST_V1</c>, neither of which any sibling registers a handler for.
        /// The handler invocation is isolated in its own try/catch so a throwing sibling can never stop
        /// this dispatch, nor the parity handling that runs alongside it, nor escape into the game's
        /// network pump (fail-safe rule, docs/CONVENTIONS.md).
        /// </summary>
        internal static void DispatchReceived(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;

            string action;
            try
            {
                action = ParityPayloadCodec.PeekAction(payload);
            }
            catch (Exception ex)
            {
                SafeLog("TransportService: PeekAction failed (ignored): " + ex);
                return;
            }
            if (string.IsNullOrEmpty(action)) return;

            Action<string> handler;
            lock (Sync) { Handlers.TryGetValue(action, out handler); }
            if (handler == null) return;

            try
            {
                handler(payload);
            }
            catch (Exception ex)
            {
                SafeLog("TransportService: receiver for action '" + action + "' threw (isolated, ignored): " + ex);
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

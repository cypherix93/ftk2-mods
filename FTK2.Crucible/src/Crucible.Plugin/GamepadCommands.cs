using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using FTK2Mods.Crucible;
using HarmonyLib;
using UnityEngine;

namespace Crucible.Plugin
{
    /// <summary>
    /// Virtual-controller <c>crucible_pad_*</c> console commands (SPEC S3 controller-driving): the
    /// game is entirely controller-navigable, so injecting through Unity's new Input System — a
    /// virtual <c>Gamepad</c> device driven via <c>InputSystem.QueueStateEvent</c> — is the
    /// universal way to drive every screen, rather than reverse-engineering per-screen UIToolkit
    /// wiring the way <see cref="UiCommands"/> does. OS-level input (SendKeys, keybd_event) was
    /// verified NOT to work against this game 2026-08-23; UIToolkit NavigationMoveEvent sent
    /// straight to a document root also did nothing (the game drives focus itself). This drives the
    /// same Input System surface the game's own <c>InputController</c> reads from.
    ///
    /// No compile-time reference to Unity.InputSystem.dll: every Input System type/member is
    /// resolved reflectively via AccessTools/Type.GetMethods, matching <see cref="UiCommands"/>'s
    /// posture — a future Input System update degrades to a reported error, never a load failure.
    /// UnityEngine.Vector2 IS used directly (not reflectively): UnityEngine.CoreModule is already a
    /// compile-time reference for this project (see CaptureService/CruciblePlugin's `using
    /// UnityEngine;`), and GamepadState.leftStick/rightStick are exactly that type.
    ///
    /// Follows <see cref="UiCommands"/>'s shape: registration via <see cref="GameBridge.RegisterCommand"/>,
    /// results handed off through <see cref="LastResult"/> for RpcServer to pick up, never throw out
    /// of a handler. <b>Registration is deferred to <see cref="TryRegister"/></b>, polled from
    /// <see cref="MainThreadPump.OnTick"/> (wired in CruciblePlugin.PollHotkeys) for the same reason
    /// as UiCommands/ReflectionCommands: the game's command registry does not exist during Awake.
    ///
    /// <c>crucible_pad_pair</c> and <c>crucible_input_devices</c> (SPEC S3 pairing) were added after
    /// discovering, via a static probe of FTK2.dll's own <c>InputController</c>, that FTK2 does NOT
    /// use Unity's <c>UnityEngine.InputSystem.Users.InputUser</c> pairing system at all: it drives
    /// exactly one Unity <c>PlayerInput</c> (field <c>InputController._playerInput</c>) and layers
    /// its OWN local-co-op bookkeeping on top — <c>InputController._joinedPlayers</c>, a
    /// <c>List&lt;InputPlayer&gt;</c> where each <c>InputPlayer</c> owns a
    /// <c>List&lt;InputDevice&gt; PlayerDevices</c> and an <c>int AssignmentIndex</c>. A newly added
    /// device that isn't in any <c>InputPlayer.PlayerDevices</c> list either gets silently ignored or
    /// auto-joins as a NEW <c>InputPlayer</c> (observed as the party screen's spurious P2) — never
    /// routed to the existing P1. Pairing therefore means: find the target <c>InputPlayer</c> by
    /// <c>AssignmentIndex</c>, remove the virtual device from every other player's device list, add it
    /// to the target's, then best-effort call the private <c>InputController._activateDevice</c> and
    /// the public <c>ClearNonPrimaryInputPlayers()</c> (only when pairing to the default/primary
    /// player) to clean up any spurious extra player a prior injection already created.
    /// </summary>
    internal static class GamepadCommands
    {
        private static ManualLogSource _log;

        /// <summary>The most recent crucible_pad* result, rendered as text. Same handoff contract as UiCommands.LastResult.</summary>
        internal static string LastResult;

        private static readonly MethodInfo PadHandler = typeof(GamepadCommands).GetMethod("CruciblePad", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo PadHoldHandler = typeof(GamepadCommands).GetMethod("CruciblePadHold", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo PadStickHandler = typeof(GamepadCommands).GetMethod("CruciblePadStick", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo PadPairHandler = typeof(GamepadCommands).GetMethod("CruciblePadPair", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo InputDevicesHandler = typeof(GamepadCommands).GetMethod("CrucibleInputDevices", BindingFlags.Public | BindingFlags.Static);

        private static bool _padRegistered;
        private static bool _padHoldRegistered;
        private static bool _padStickRegistered;
        private static bool _padPairRegistered;
        private static bool _inputDevicesRegistered;
        private static bool _loggedWaiting;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>Called every tick from MainThreadPump.OnTick until every command is registered.</summary>
        internal static void TryRegister()
        {
            if (_padRegistered && _padHoldRegistered && _padStickRegistered && _padPairRegistered && _inputDevicesRegistered) return;

            if (!_padRegistered) _padRegistered = GameBridge.RegisterCommand("crucible_pad", PadHandler, new List<string> { "button" });
            if (!_padHoldRegistered) _padHoldRegistered = GameBridge.RegisterCommand("crucible_pad_hold", PadHoldHandler, new List<string> { "button", "milliseconds" });
            if (!_padStickRegistered) _padStickRegistered = GameBridge.RegisterCommand("crucible_pad_stick", PadStickHandler, new List<string> { "stick", "direction" });
            if (!_padPairRegistered) _padPairRegistered = GameBridge.RegisterCommand("crucible_pad_pair", PadPairHandler, new List<string> { "playerIndex" });
            if (!_inputDevicesRegistered) _inputDevicesRegistered = GameBridge.RegisterCommand("crucible_input_devices", InputDevicesHandler, new List<string>());

            if (_padRegistered && _padHoldRegistered && _padStickRegistered && _padPairRegistered && _inputDevicesRegistered)
            {
                if (_log != null) _log.LogInfo("GamepadCommands registered (crucible_pad/crucible_pad_hold/crucible_pad_stick/crucible_pad_pair/crucible_input_devices).");
            }
            else if (!_loggedWaiting)
            {
                _loggedWaiting = true;
                if (_log != null) _log.LogInfo("GamepadCommands registration incomplete; will retry each tick until the game's command registry is ready.");
            }
        }

        // ============================================================== crucible_pad

        /// <summary>crucible_pad &lt;button&gt; — taps a face/dpad/shoulder/start/select button (press then release).</summary>
        public static void CruciblePad(string button)
        {
            LastResult = null;
            try
            {
                string canonical, parseError;
                if (!GamepadButtonName.TryParse(button, out canonical, out parseError))
                {
                    LastResult = "error: " + parseError;
                    return;
                }

                object device; bool created; string deviceError;
                if (!EnsureGamepad(out device, out created, out deviceError))
                {
                    LastResult = "error: " + deviceError;
                    return;
                }

                string pressError, releaseError;
                bool sentPress, sentRelease;
                TapButton(device, canonical, out sentPress, out pressError, out sentRelease, out releaseError);

                StringBuilder sb = new StringBuilder();
                sb.Append("device=").Append(DescribeDevice(device, created));
                sb.Append(" button=").Append(canonical);
                sb.Append(" pressed=").Append(sentPress);
                if (pressError != null) sb.Append(" pressError=").Append(pressError);
                sb.Append(" released=").Append(sentRelease);
                if (releaseError != null) sb.Append(" releaseError=").Append(releaseError);
                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_pad threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_pad failed: " + ex.Message);
            }
        }

        // ============================================================== crucible_pad_hold

        /// <summary>crucible_pad_hold &lt;button&gt; &lt;milliseconds&gt; — press, wait, release. Clamped to PadHoldDuration.MaxMs.</summary>
        public static void CruciblePadHold(string button, string milliseconds)
        {
            LastResult = null;
            try
            {
                string canonical, parseError;
                if (!GamepadButtonName.TryParse(button, out canonical, out parseError))
                {
                    LastResult = "error: " + parseError;
                    return;
                }

                int ms; bool clamped; string msError;
                if (!PadHoldDuration.TryParse(milliseconds, out ms, out clamped, out msError))
                {
                    LastResult = "error: " + msError;
                    return;
                }

                object device; bool created; string deviceError;
                if (!EnsureGamepad(out device, out created, out deviceError))
                {
                    LastResult = "error: " + deviceError;
                    return;
                }

                string pressError, releaseError;
                bool sentPress, sentRelease;
                TapButton(device, canonical, ms, out sentPress, out pressError, out sentRelease, out releaseError);

                StringBuilder sb = new StringBuilder();
                sb.Append("device=").Append(DescribeDevice(device, created));
                sb.Append(" button=").Append(canonical);
                sb.Append(" holdMs=").Append(ms);
                if (clamped) sb.Append(" (clamped from '").Append(milliseconds).Append("')");
                sb.Append(" pressed=").Append(sentPress);
                if (pressError != null) sb.Append(" pressError=").Append(pressError);
                sb.Append(" released=").Append(sentRelease);
                if (releaseError != null) sb.Append(" releaseError=").Append(releaseError);
                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_pad_hold threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_pad_hold failed: " + ex.Message);
            }
        }

        // ============================================================== crucible_pad_stick

        /// <summary>crucible_pad_stick &lt;stick&gt; &lt;direction&gt; — left/right stick, up/down/left/right/center.</summary>
        public static void CruciblePadStick(string stick, string direction)
        {
            LastResult = null;
            try
            {
                string canonicalStick, stickError;
                if (!GamepadStick.TryParseSide(stick, out canonicalStick, out stickError))
                {
                    LastResult = "error: " + stickError;
                    return;
                }

                float x, y; string dirError;
                if (!GamepadStick.TryParseDirection(direction, out x, out y, out dirError))
                {
                    LastResult = "error: " + dirError;
                    return;
                }

                object device; bool created; string deviceError;
                if (!EnsureGamepad(out device, out created, out deviceError))
                {
                    LastResult = "error: " + deviceError;
                    return;
                }

                object state; string buildError;
                bool built = BuildStickState(canonicalStick, x, y, out state, out buildError);
                string sendError = null;
                bool sent = built && QueueAndUpdate(device, state, out sendError);

                StringBuilder sb = new StringBuilder();
                sb.Append("device=").Append(DescribeDevice(device, created));
                sb.Append(" stick=").Append(canonicalStick);
                sb.Append(" direction=").Append(direction);
                sb.Append(" vector=(").Append(x.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",").Append(y.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(")");
                sb.Append(" sent=").Append(sent);
                if (!built) sb.Append(" error=").Append(buildError);
                else if (!sent) sb.Append(" error=").Append(sendError);
                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_pad_stick threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_pad_stick failed: " + ex.Message);
            }
        }

        // ============================================================== crucible_pad_pair

        /// <summary>
        /// crucible_pad_pair &lt;playerIndex|-&gt; — pairs the virtual gamepad with a specific
        /// <c>InputController.InputPlayer</c> (FTK2's own player-bookkeeping, not Unity's
        /// InputUser — see the class doc comment) so injected <c>crucible_pad*</c> presses are
        /// routed to that player instead of being ignored or silently spawning a new player.
        /// "-" targets the default/primary player (AssignmentIndex 0); a non-negative integer
        /// targets an explicit AssignmentIndex.
        /// </summary>
        public static void CruciblePadPair(string playerIndex)
        {
            LastResult = null;
            try
            {
                int targetAssignmentIndex; bool isDefault; string parseError;
                if (!PadPairTarget.TryParse(playerIndex, out targetAssignmentIndex, out isDefault, out parseError))
                {
                    LastResult = "error: " + parseError;
                    return;
                }

                object device; bool created; string deviceError;
                if (!EnsureGamepad(out device, out created, out deviceError))
                {
                    LastResult = "error: " + deviceError;
                    return;
                }

                Type inputControllerType = AccessTools.TypeByName("InputController");
                if (inputControllerType == null) { LastResult = "error: InputController type not found"; return; }

                PropertyInfo instanceProp = AccessTools.Property(inputControllerType, "Instance");
                object instance = instanceProp == null ? null : instanceProp.GetValue(null, null);
                if (instance == null) { LastResult = "error: InputController.Instance is null (not reachable yet)"; return; }

                FieldInfo joinedPlayersField = AccessTools.Field(inputControllerType, "_joinedPlayers");
                if (joinedPlayersField == null) { LastResult = "error: InputController._joinedPlayers field not found -- API shape changed"; return; }

                IList joinedPlayers = joinedPlayersField.GetValue(instance) as IList;
                if (joinedPlayers == null || joinedPlayers.Count == 0)
                {
                    LastResult = "error: InputController._joinedPlayers is empty -- no InputPlayer joined yet";
                    return;
                }

                Type inputPlayerType = null;
                foreach (object p in joinedPlayers) { if (p != null) { inputPlayerType = p.GetType(); break; } }
                if (inputPlayerType == null) { LastResult = "error: could not determine InputPlayer type from _joinedPlayers"; return; }

                FieldInfo assignmentIndexField = AccessTools.Field(inputPlayerType, "AssignmentIndex");
                FieldInfo playerDevicesField = AccessTools.Field(inputPlayerType, "PlayerDevices");
                if (assignmentIndexField == null || playerDevicesField == null)
                {
                    LastResult = "error: InputPlayer.AssignmentIndex/PlayerDevices field not found -- API shape changed";
                    return;
                }

                object targetPlayer = null;
                List<int> availableIndexes = new List<int>();
                foreach (object p in joinedPlayers)
                {
                    if (p == null) continue;
                    int idx = (int)assignmentIndexField.GetValue(p);
                    availableIndexes.Add(idx);
                    if (idx == targetAssignmentIndex) targetPlayer = p;
                }
                if (targetPlayer == null && isDefault && joinedPlayers.Count > 0)
                {
                    // No InputPlayer carries AssignmentIndex 0 (e.g. it's 1-based) -- fall back to
                    // the first joined player, which by construction is the primary one.
                    targetPlayer = joinedPlayers[0];
                }
                if (targetPlayer == null)
                {
                    LastResult = "error: no InputPlayer with AssignmentIndex=" + targetAssignmentIndex
                        + "; available AssignmentIndex values: [" + string.Join(", ", availableIndexes.ConvertAll(i => i.ToString()).ToArray()) + "]";
                    return;
                }

                MethodInfo getPlayerByDeviceMethod = ResolveGetPlayerByDeviceMethod(inputControllerType, device);
                object beforePlayer = InvokeGetPlayerByDevice(getPlayerByDeviceMethod, instance, device);
                string beforeDescription = DescribeInputPlayer(beforePlayer, assignmentIndexField);

                int removedFromOthers = 0;
                foreach (object p in joinedPlayers)
                {
                    if (p == null || ReferenceEquals(p, targetPlayer)) continue;
                    IList otherDevices = playerDevicesField.GetValue(p) as IList;
                    if (otherDevices == null) continue;
                    for (int i = otherDevices.Count - 1; i >= 0; i--)
                    {
                        if (ReferenceEquals(otherDevices[i], device)) { otherDevices.RemoveAt(i); removedFromOthers++; }
                    }
                }

                IList targetDevices = playerDevicesField.GetValue(targetPlayer) as IList;
                if (targetDevices == null) { LastResult = "error: target InputPlayer.PlayerDevices is not a list"; return; }
                bool alreadyPresent = false;
                foreach (object d in targetDevices) { if (ReferenceEquals(d, device)) { alreadyPresent = true; break; } }
                if (!alreadyPresent) targetDevices.Add(device);

                bool activateAttempted = false, activateSucceeded = false; string activateError = null;
                MethodInfo activateDeviceMethod = AccessTools.Method(inputControllerType, "_activateDevice", new[] { device.GetType() });
                if (activateDeviceMethod == null)
                {
                    // fall back to the InputDevice base type -- AccessTools.Method needs an exact
                    // parameter-type match and the device's concrete runtime type may differ from
                    // the declared parameter type (e.g. a Gamepad subclass).
                    Type deviceBaseType = AccessTools.TypeByName("UnityEngine.InputSystem.InputDevice");
                    if (deviceBaseType != null) activateDeviceMethod = AccessTools.Method(inputControllerType, "_activateDevice", new[] { deviceBaseType });
                }
                if (activateDeviceMethod != null)
                {
                    activateAttempted = true;
                    try { activateDeviceMethod.Invoke(instance, new object[] { device }); activateSucceeded = true; }
                    catch (TargetInvocationException ex) { activateError = ex.InnerException != null ? ex.InnerException.Message : ex.Message; }
                    catch (Exception ex) { activateError = ex.Message; }
                }

                bool clearedNonPrimary = false; string clearError = null;
                if (isDefault)
                {
                    MethodInfo clearNonPrimaryMethod = AccessTools.Method(inputControllerType, "ClearNonPrimaryInputPlayers");
                    if (clearNonPrimaryMethod != null)
                    {
                        try { clearNonPrimaryMethod.Invoke(instance, null); clearedNonPrimary = true; }
                        catch (TargetInvocationException ex) { clearError = ex.InnerException != null ? ex.InnerException.Message : ex.Message; }
                        catch (Exception ex) { clearError = ex.Message; }
                    }
                }

                object afterPlayer = InvokeGetPlayerByDevice(getPlayerByDeviceMethod, instance, device);
                string afterDescription = DescribeInputPlayer(afterPlayer, assignmentIndexField);

                StringBuilder sb = new StringBuilder();
                sb.Append("device=").Append(DescribeDevice(device, created));
                sb.Append(" targetAssignmentIndex=").Append(targetAssignmentIndex).Append(isDefault ? " (default)" : "");
                sb.Append(" removedFromOtherPlayers=").Append(removedFromOthers);
                sb.Append(" alreadyPaired=").Append(alreadyPresent);
                sb.Append(" activateDeviceAttempted=").Append(activateAttempted).Append(" succeeded=").Append(activateSucceeded);
                if (activateError != null) sb.Append(" activateError=").Append(activateError);
                sb.Append(" clearedNonPrimaryPlayers=").Append(clearedNonPrimary);
                if (clearError != null) sb.Append(" clearError=").Append(clearError);
                sb.Append(" playerByDevice_before=").Append(beforeDescription);
                sb.Append(" playerByDevice_after=").Append(afterDescription);
                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_pad_pair threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_pad_pair failed: " + ex.Message);
            }
        }

        private static MethodInfo ResolveGetPlayerByDeviceMethod(Type inputControllerType, object device)
        {
            if (inputControllerType == null || device == null) return null;
            MethodInfo m = AccessTools.Method(inputControllerType, "GetPlayerByDevice", new[] { device.GetType() });
            if (m != null) return m;
            Type deviceBaseType = AccessTools.TypeByName("UnityEngine.InputSystem.InputDevice");
            return deviceBaseType == null ? null : AccessTools.Method(inputControllerType, "GetPlayerByDevice", new[] { deviceBaseType });
        }

        private static object InvokeGetPlayerByDevice(MethodInfo method, object instance, object device)
        {
            if (method == null || instance == null) return null;
            try { return method.Invoke(instance, new object[] { device }); }
            catch (Exception) { return null; }
        }

        private static string DescribeInputPlayer(object inputPlayer, FieldInfo assignmentIndexField)
        {
            if (inputPlayer == null) return "(none)";
            try
            {
                object idx = assignmentIndexField == null ? null : assignmentIndexField.GetValue(inputPlayer);
                return "InputPlayer(AssignmentIndex=" + (idx == null ? "?" : idx.ToString()) + ")";
            }
            catch (Exception ex)
            {
                return "InputPlayer(unreadable: " + ex.Message + ")";
            }
        }

        // ============================================================== crucible_input_devices

        /// <summary>
        /// crucible_input_devices — lists every <c>InputSystem.devices</c> entry (id, layout,
        /// enabled, added, whether it's the type's ".current" device) alongside FTK2's own
        /// per-device player assignment (<c>InputController.GetPlayerByDevice</c> /
        /// <c>IsDeviceActivated</c>), plus a count of Unity's <c>InputUser.all</c> pairings so a
        /// caller can tell "press rejected" apart from "device not paired" apart from "device
        /// disabled" -- exactly the diagnostic gap that made the pad-vs-mouse difference opaque.
        /// </summary>
        public static void CrucibleInputDevices()
        {
            LastResult = null;
            try
            {
                Type inputSystemType = AccessTools.TypeByName("UnityEngine.InputSystem.InputSystem");
                if (inputSystemType == null) { LastResult = "error: UnityEngine.InputSystem.InputSystem type not found"; return; }

                PropertyInfo devicesProp = AccessTools.Property(inputSystemType, "devices");
                if (devicesProp == null) { LastResult = "error: InputSystem.devices property not found"; return; }

                object readOnlyArray = devicesProp.GetValue(null, null);
                object[] devices = ToObjectArray(readOnlyArray);
                if (devices == null) { LastResult = "error: could not enumerate InputSystem.devices (ReadOnlyArray<T>.ToArray() not found)"; return; }

                Type inputControllerType = AccessTools.TypeByName("InputController");
                object controllerInstance = null;
                MethodInfo isDeviceActivatedMethod = null;
                MethodInfo getPlayerByDeviceMethod = null;
                FieldInfo assignmentIndexField = null;
                if (inputControllerType != null)
                {
                    PropertyInfo instanceProp = AccessTools.Property(inputControllerType, "Instance");
                    controllerInstance = instanceProp == null ? null : instanceProp.GetValue(null, null);
                    Type deviceBaseType = AccessTools.TypeByName("UnityEngine.InputSystem.InputDevice");
                    if (deviceBaseType != null)
                    {
                        isDeviceActivatedMethod = AccessTools.Method(inputControllerType, "IsDeviceActivated", new[] { deviceBaseType, typeof(bool) });
                        getPlayerByDeviceMethod = AccessTools.Method(inputControllerType, "GetPlayerByDevice", new[] { deviceBaseType });
                    }

                    if (controllerInstance != null)
                    {
                        object joinedPlayersObj = null;
                        FieldInfo joinedPlayersField = AccessTools.Field(inputControllerType, "_joinedPlayers");
                        if (joinedPlayersField != null) joinedPlayersObj = joinedPlayersField.GetValue(controllerInstance);
                        IList joinedPlayers = joinedPlayersObj as IList;
                        if (joinedPlayers != null)
                        {
                            foreach (object p in joinedPlayers) { if (p != null) { assignmentIndexField = AccessTools.Field(p.GetType(), "AssignmentIndex"); break; } }
                        }
                    }
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("count=").Append(devices.Length);
                for (int i = 0; i < devices.Length; i++)
                {
                    object d = devices[i];
                    sb.Append(" | ").Append(DescribeDeviceForList(d, controllerInstance, isDeviceActivatedMethod, getPlayerByDeviceMethod, assignmentIndexField));
                }

                sb.Append(" || inputUsers=").Append(DescribeInputUsers());
                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_input_devices threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_input_devices failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Reflectively calls ReadOnlyArray&lt;T&gt;.ToArray() and boxes the result into an
        /// object[] via <see cref="Array.GetValue(int)"/> rather than a direct cast: TValue can be
        /// a reference type (InputDevice -- array covariance would work) or a value type (InputUser
        /// is a struct -- a direct `(object[])` cast on a value-type array throws InvalidCastException),
        /// and this handles both uniformly.
        /// </summary>
        private static object[] ToObjectArray(object readOnlyArray)
        {
            if (readOnlyArray == null) return new object[0];
            MethodInfo toArray = AccessTools.Method(readOnlyArray.GetType(), "ToArray");
            if (toArray == null) return null;
            try
            {
                object arrObj = toArray.Invoke(readOnlyArray, null);
                Array arr = arrObj as Array;
                if (arr == null) return null;
                object[] result = new object[arr.Length];
                for (int i = 0; i < arr.Length; i++) result[i] = arr.GetValue(i);
                return result;
            }
            catch (Exception) { return null; }
        }

        private static string DescribeDeviceForList(object device, object controllerInstance, MethodInfo isDeviceActivatedMethod, MethodInfo getPlayerByDeviceMethod, FieldInfo assignmentIndexField)
        {
            if (device == null) return "(null)";

            string name = ReadStringProp(device, "name");
            string layout = ReadStringProp(device, "layout");
            object deviceId = ReadProp(device, "deviceId");
            object enabled = ReadProp(device, "enabled");
            object added = ReadProp(device, "added");
            bool isCurrent = IsCurrentDevice(device);

            string playerDescription = "(InputController unavailable)";
            if (controllerInstance != null && getPlayerByDeviceMethod != null)
            {
                object player = InvokeGetPlayerByDevice(getPlayerByDeviceMethod, controllerInstance, device);
                playerDescription = DescribeInputPlayer(player, assignmentIndexField);
            }

            string activatedNav = "(unavailable)", activatedNonNav = "(unavailable)";
            if (controllerInstance != null && isDeviceActivatedMethod != null)
            {
                try { activatedNav = isDeviceActivatedMethod.Invoke(controllerInstance, new object[] { device, true }).ToString(); }
                catch (Exception ex) { activatedNav = "(error: " + ex.Message + ")"; }
                try { activatedNonNav = isDeviceActivatedMethod.Invoke(controllerInstance, new object[] { device, false }).ToString(); }
                catch (Exception ex) { activatedNonNav = "(error: " + ex.Message + ")"; }
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(device.GetType().Name)
                .Append(" id=").Append(deviceId == null ? "?" : deviceId.ToString())
                .Append(" name='").Append(name ?? "(null)").Append("'")
                .Append(" layout='").Append(layout ?? "(null)").Append("'")
                .Append(" enabled=").Append(enabled == null ? "?" : enabled.ToString())
                .Append(" added=").Append(added == null ? "?" : added.ToString())
                .Append(" current=").Append(isCurrent)
                .Append(" activated[nav]=").Append(activatedNav)
                .Append(" activated[nonNav]=").Append(activatedNonNav)
                .Append(" player=").Append(playerDescription);
            return sb.ToString();
        }

        /// <summary>True if <c>device</c>'s own runtime type has a public static "current" property
        /// (e.g. Gamepad.current, Mouse.current, Keyboard.current) whose value is this device.</summary>
        private static bool IsCurrentDevice(object device)
        {
            if (device == null) return false;
            try
            {
                PropertyInfo currentProp = AccessTools.Property(device.GetType(), "current");
                if (currentProp == null) return false;
                object current = currentProp.GetValue(null, null);
                return ReferenceEquals(current, device);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Renders Unity's InputUser.all -- expected to settle whether FTK2 uses InputUser
        /// pairing at all (the doc comment's hypothesis is that it does not).</summary>
        private static string DescribeInputUsers()
        {
            try
            {
                Type inputUserType = AccessTools.TypeByName("UnityEngine.InputSystem.Users.InputUser");
                if (inputUserType == null) return "(InputUser type not found)";

                PropertyInfo allProp = AccessTools.Property(inputUserType, "all");
                if (allProp == null) return "(InputUser.all property not found)";

                object allValue = allProp.GetValue(null, null);
                object[] users = ToObjectArray(allValue);
                if (users == null) return "(InputUser.all not enumerable)";

                StringBuilder sb = new StringBuilder();
                sb.Append("count=").Append(users.Length);
                for (int i = 0; i < users.Length; i++)
                {
                    object user = users[i];
                    PropertyInfo pairedDevicesProp = AccessTools.Property(inputUserType, "pairedDevices");
                    object pairedDevicesValue = pairedDevicesProp == null ? null : pairedDevicesProp.GetValue(user, null);
                    object[] pairedDevices = pairedDevicesValue == null ? new object[0] : ToObjectArray(pairedDevicesValue);
                    sb.Append(" [user").Append(i).Append(" pairedDeviceCount=").Append(pairedDevices == null ? 0 : pairedDevices.Length).Append("]");
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "(error: " + ex.Message + ")";
            }
        }

        // ============================================================== shared tap helper (crucible_ui_press / crucible_dialogue_* reuse this)

        /// <summary>
        /// Ensures a gamepad device exists, then presses-and-releases <paramref name="canonicalButton"/>
        /// (a <c>GamepadButtonName</c> canonical name, e.g. "South" for A) via the same
        /// <see cref="EnsureGamepad"/>/<see cref="TapButton"/> path <see cref="CruciblePad"/> uses.
        /// Used by <c>UiCommands</c>'s activation ladder as the virtual-gamepad fallback when a
        /// UIToolkit <c>NavigationSubmitEvent</c> alone doesn't produce an observable change.
        /// </summary>
        internal static bool TryTap(string canonicalButton, out string detail)
        {
            StringBuilder sb = new StringBuilder();

            object device; bool created; string deviceError;
            if (!EnsureGamepad(out device, out created, out deviceError))
            {
                detail = "device error: " + deviceError;
                return false;
            }

            string pressError, releaseError;
            bool sentPress, sentRelease;
            TapButton(device, canonicalButton, out sentPress, out pressError, out sentRelease, out releaseError);

            sb.Append("device=").Append(DescribeDevice(device, created));
            sb.Append(" button=").Append(canonicalButton);
            sb.Append(" pressed=").Append(sentPress);
            if (pressError != null) sb.Append(" pressError=").Append(pressError);
            sb.Append(" released=").Append(sentRelease);
            if (releaseError != null) sb.Append(" releaseError=").Append(releaseError);
            detail = sb.ToString();
            return sentPress && sentRelease;
        }

        // ============================================================== press/release

        private static void TapButton(object device, string canonicalButton, out bool sentPress, out string pressError, out bool sentRelease, out string releaseError)
        {
            TapButton(device, canonicalButton, 33, out sentPress, out pressError, out sentRelease, out releaseError);
        }

        /// <summary>
        /// Press-then-release: queue a GamepadState with the button bit set, InputSystem.Update(),
        /// wait, then queue the cleared state and Update() again. The release is always attempted,
        /// even if the press failed — a press with no release would jam the virtual controller.
        /// </summary>
        private static void TapButton(object device, string canonicalButton, int waitMs, out bool sentPress, out string pressError, out bool sentRelease, out string releaseError)
        {
            pressError = null;
            releaseError = null;

            object pressedState;
            bool builtPressed = BuildButtonState(canonicalButton, true, out pressedState, out pressError);
            sentPress = builtPressed && QueueAndUpdate(device, pressedState, out pressError);

            Thread.Sleep(waitMs);

            object releasedState;
            bool builtReleased = BuildButtonState(canonicalButton, false, out releasedState, out releaseError);
            sentRelease = builtReleased && QueueAndUpdate(device, releasedState, out releaseError);
        }

        // ============================================================== device resolution

        /// <summary>
        /// Reuses UnityEngine.InputSystem.Gamepad.current if a gamepad is already present; only
        /// calls InputSystem.AddDevice&lt;Gamepad&gt;() when none exists. Logs which path was taken.
        /// </summary>
        private static bool EnsureGamepad(out object device, out bool created, out string error)
        {
            device = null;
            created = false;
            error = null;

            Type gamepadType = AccessTools.TypeByName("UnityEngine.InputSystem.Gamepad");
            if (gamepadType == null) { error = "UnityEngine.InputSystem.Gamepad type not found"; return false; }

            PropertyInfo currentProp = AccessTools.Property(gamepadType, "current");
            if (currentProp == null) { error = "Gamepad.current property not found"; return false; }

            object current;
            try { current = currentProp.GetValue(null, null); }
            catch (Exception ex) { error = "Gamepad.current threw: " + ex.Message; return false; }

            if (current != null)
            {
                device = current;
                created = false;
                if (_log != null) _log.LogInfo("Crucible gamepad: reusing existing Gamepad.current.");
                return true;
            }

            Type inputSystemType = AccessTools.TypeByName("UnityEngine.InputSystem.InputSystem");
            if (inputSystemType == null) { error = "UnityEngine.InputSystem.InputSystem type not found"; return false; }

            MethodInfo addDeviceGeneric = FindGenericStaticMethod(inputSystemType, "AddDevice", 1, new[] { typeof(string) });
            if (addDeviceGeneric == null) { error = "InputSystem.AddDevice<TDevice>(string) not found"; return false; }

            try
            {
                MethodInfo bound = addDeviceGeneric.MakeGenericMethod(gamepadType);
                object added = bound.Invoke(null, new object[] { null });
                if (added == null) { error = "InputSystem.AddDevice<Gamepad>() returned null"; return false; }
                device = added;
                created = true;
                if (_log != null) _log.LogInfo("Crucible gamepad: no Gamepad.current found; created a virtual Gamepad via InputSystem.AddDevice<Gamepad>().");
                return true;
            }
            catch (TargetInvocationException ex)
            {
                error = "AddDevice<Gamepad> threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = "AddDevice<Gamepad> failed: " + ex.Message;
                return false;
            }
        }

        /// <summary>Finds a public static generic method by name/arity/non-generic-parameter-types, ignoring the generic type parameter's own position in the signature.</summary>
        private static MethodInfo FindGenericStaticMethod(Type type, string name, int paramCount, Type[] expectedFixedParamTypes)
        {
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (!string.Equals(m.Name, name, StringComparison.Ordinal)) continue;
                if (!m.IsGenericMethodDefinition) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != paramCount) continue;

                bool matches = true;
                for (int i = 0; i < expectedFixedParamTypes.Length; i++)
                {
                    if (ps[i].ParameterType != expectedFixedParamTypes[i]) { matches = false; break; }
                }
                if (matches) return m;
            }
            return null;
        }

        // ============================================================== state construction

        private static bool BuildButtonState(string canonicalButton, bool pressed, out object state, out string error)
        {
            state = null;
            error = null;
            try
            {
                Type stateType = AccessTools.TypeByName("UnityEngine.InputSystem.LowLevel.GamepadState");
                if (stateType == null) { error = "UnityEngine.InputSystem.LowLevel.GamepadState type not found"; return false; }

                Type buttonEnumType = AccessTools.TypeByName("UnityEngine.InputSystem.LowLevel.GamepadButton");
                if (buttonEnumType == null) { error = "UnityEngine.InputSystem.LowLevel.GamepadButton type not found"; return false; }

                object buttonValue;
                try { buttonValue = Enum.Parse(buttonEnumType, canonicalButton, true); }
                catch (Exception)
                {
                    string[] names = Enum.GetNames(buttonEnumType);
                    error = "GamepadButton has no member '" + canonicalButton + "'; actual members: " + string.Join(", ", names);
                    return false;
                }

                object defaultState = Activator.CreateInstance(stateType);
                MethodInfo withButton = AccessTools.Method(stateType, "WithButton", new[] { buttonEnumType, typeof(bool) });
                if (withButton == null) { error = "GamepadState.WithButton(GamepadButton, bool) not found"; return false; }

                state = withButton.Invoke(defaultState, new object[] { buttonValue, pressed });
                if (state == null) { error = "WithButton returned null"; return false; }
                return true;
            }
            catch (TargetInvocationException ex)
            {
                error = "build threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = "build failed: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Builds a fresh, otherwise-zeroed GamepadState with just the given stick's Vector2 set.
        /// GamepadState fully replaces the device's low-level state on each QueueStateEvent, so this
        /// does not preserve buttons or the other stick from any state queued by an earlier call —
        /// acceptable for a discrete "set stick direction" command, but worth knowing if a caller
        /// ever needs to combine a held button with a stick direction in one frame.
        /// </summary>
        private static bool BuildStickState(string canonicalStick, float x, float y, out object state, out string error)
        {
            state = null;
            error = null;
            try
            {
                Type stateType = AccessTools.TypeByName("UnityEngine.InputSystem.LowLevel.GamepadState");
                if (stateType == null) { error = "UnityEngine.InputSystem.LowLevel.GamepadState type not found"; return false; }

                object boxed = Activator.CreateInstance(stateType);

                string fieldName = string.Equals(canonicalStick, "Left", StringComparison.Ordinal) ? "leftStick" : "rightStick";
                FieldInfo stickField = AccessTools.Field(stateType, fieldName);
                if (stickField == null) { error = "GamepadState." + fieldName + " field not found"; return false; }

                stickField.SetValue(boxed, new Vector2(x, y));

                state = boxed;
                return true;
            }
            catch (Exception ex)
            {
                error = "build failed: " + ex.Message;
                return false;
            }
        }

        // ============================================================== queue + update

        private static bool QueueAndUpdate(object device, object state, out string error)
        {
            error = null;
            if (device == null) { error = "no device"; return false; }
            if (state == null) { error = "no state to queue"; return false; }

            try
            {
                Type inputSystemType = AccessTools.TypeByName("UnityEngine.InputSystem.InputSystem");
                if (inputSystemType == null) { error = "UnityEngine.InputSystem.InputSystem type not found"; return false; }

                Type deviceType = AccessTools.TypeByName("UnityEngine.InputSystem.InputDevice");
                if (deviceType == null) { error = "UnityEngine.InputSystem.InputDevice type not found"; return false; }

                MethodInfo queueGeneric = FindGenericStaticMethod(inputSystemType, "QueueStateEvent", 3, new[] { deviceType });
                if (queueGeneric == null) { error = "InputSystem.QueueStateEvent<TState>(InputDevice, TState, double) not found"; return false; }

                MethodInfo bound = queueGeneric.MakeGenericMethod(state.GetType());
                bound.Invoke(null, new object[] { device, state, -1.0 });

                MethodInfo update = AccessTools.Method(inputSystemType, "Update", Type.EmptyTypes);
                if (update == null) { error = "InputSystem.Update() not found"; return false; }
                update.Invoke(null, null);

                return true;
            }
            catch (TargetInvocationException ex)
            {
                error = "queue/update threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = "queue/update failed: " + ex.Message;
                return false;
            }
        }

        // ============================================================== safe reflective reads

        private static string DescribeDevice(object device, bool created)
        {
            if (device == null) return "(none)";
            string name = ReadStringProp(device, "name");
            string layout = ReadStringProp(device, "layout");
            object deviceId = ReadProp(device, "deviceId");
            return device.GetType().Name
                + " name=" + (name == null ? "(null)" : "'" + name + "'")
                + " layout=" + (layout == null ? "(null)" : "'" + layout + "'")
                + " deviceId=" + (deviceId == null ? "?" : deviceId.ToString())
                + " origin=" + (created ? "created-by-crucible" : "pre-existing");
        }

        private static object ReadProp(object obj, string propName)
        {
            if (obj == null) return null;
            try
            {
                PropertyInfo p = AccessTools.Property(obj.GetType(), propName);
                return p == null ? null : p.GetValue(obj, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ReadStringProp(object obj, string propName)
        {
            object v = ReadProp(obj, propName);
            return v == null ? null : v.ToString();
        }
    }
}

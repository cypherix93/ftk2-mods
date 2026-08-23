using System;
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
    /// </summary>
    internal static class GamepadCommands
    {
        private static ManualLogSource _log;

        /// <summary>The most recent crucible_pad* result, rendered as text. Same handoff contract as UiCommands.LastResult.</summary>
        internal static string LastResult;

        private static readonly MethodInfo PadHandler = typeof(GamepadCommands).GetMethod("CruciblePad", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo PadHoldHandler = typeof(GamepadCommands).GetMethod("CruciblePadHold", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo PadStickHandler = typeof(GamepadCommands).GetMethod("CruciblePadStick", BindingFlags.Public | BindingFlags.Static);

        private static bool _padRegistered;
        private static bool _padHoldRegistered;
        private static bool _padStickRegistered;
        private static bool _loggedWaiting;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>Called every tick from MainThreadPump.OnTick until every command is registered.</summary>
        internal static void TryRegister()
        {
            if (_padRegistered && _padHoldRegistered && _padStickRegistered) return;

            if (!_padRegistered) _padRegistered = GameBridge.RegisterCommand("crucible_pad", PadHandler, new List<string> { "button" });
            if (!_padHoldRegistered) _padHoldRegistered = GameBridge.RegisterCommand("crucible_pad_hold", PadHoldHandler, new List<string> { "button", "milliseconds" });
            if (!_padStickRegistered) _padStickRegistered = GameBridge.RegisterCommand("crucible_pad_stick", PadStickHandler, new List<string> { "stick", "direction" });

            if (_padRegistered && _padHoldRegistered && _padStickRegistered)
            {
                if (_log != null) _log.LogInfo("GamepadCommands registered (crucible_pad/crucible_pad_hold/crucible_pad_stick).");
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

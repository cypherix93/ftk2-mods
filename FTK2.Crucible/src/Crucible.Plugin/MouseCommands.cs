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
    /// <c>crucible_mouse_click &lt;x&gt; &lt;y&gt;</c> — the owner asked for "clicks and controller
    /// and everything" (SPEC S3). Overworld hexes are 3D world objects the UI/gamepad commands
    /// cannot reach, so a real screen-space click is the only way to move the party. Verified live
    /// 2026-08-23 by reflecting the retail Unity.InputSystem.dll (same method GamepadCommands used
    /// for the gamepad): <c>UnityEngine.InputSystem.Mouse</c> exists,
    /// <c>UnityEngine.InputSystem.LowLevel.MouseState</c> has a <c>position</c> (Vector2) field and
    /// a <c>WithButton(MouseButton, bool)</c> builder, and <c>MouseButton.Left</c> exists — the exact
    /// same <c>InputSystem.QueueStateEvent&lt;TState&gt;</c> injection path
    /// <see cref="GamepadCommands"/> uses for the virtual gamepad works for a virtual mouse too.
    ///
    /// No compile-time reference to Unity.InputSystem.dll: resolved reflectively via
    /// AccessTools/Type.GetMethods, same posture as GamepadCommands. Registration deferred to
    /// <see cref="TryRegister"/>, polled from MainThreadPump.OnTick (wired in CruciblePlugin.PollHotkeys).
    /// </summary>
    internal static class MouseCommands
    {
        private static ManualLogSource _log;

        /// <summary>Most recent crucible_mouse_click result. Same handoff contract as GamepadCommands.LastResult.</summary>
        internal static string LastResult;

        private static readonly MethodInfo ClickHandler = typeof(MouseCommands).GetMethod("CrucibleMouseClick", BindingFlags.Public | BindingFlags.Static);
        private static bool _clickRegistered;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        internal static void TryRegister()
        {
            if (_clickRegistered) return;
            _clickRegistered = GameBridge.RegisterCommand("crucible_mouse_click", ClickHandler, new List<string> { "x", "y" });
            if (_clickRegistered && _log != null) _log.LogInfo("MouseCommands registered (crucible_mouse_click).");
        }

        // ============================================================== crucible_mouse_click

        /// <summary>crucible_mouse_click &lt;x&gt; &lt;y&gt; — moves the virtual pointer to a screen position and taps the left button.</summary>
        public static void CrucibleMouseClick(string x, string y)
        {
            LastResult = null;
            try
            {
                float px, py; string parseError;
                if (!ScreenPointParser.TryParse(x, y, out px, out py, out parseError))
                {
                    LastResult = "error: " + parseError;
                    return;
                }

                object device; bool created; string deviceError;
                if (!EnsureMouse(out device, out created, out deviceError))
                {
                    LastResult = "error: " + deviceError;
                    return;
                }

                string pressError, releaseError;
                bool sentPress, sentRelease;
                TapAt(device, px, py, out sentPress, out pressError, out sentRelease, out releaseError);

                StringBuilder sb = new StringBuilder();
                sb.Append("device=").Append(DescribeDevice(device, created));
                sb.Append(" position=(").Append(px.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",").Append(py.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(")");
                sb.Append(" pressed=").Append(sentPress);
                if (pressError != null) sb.Append(" pressError=").Append(pressError);
                sb.Append(" released=").Append(sentRelease);
                if (releaseError != null) sb.Append(" releaseError=").Append(releaseError);
                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_mouse_click threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_mouse_click failed: " + ex.Message);
            }
        }

        // ============================================================== press/release

        /// <summary>
        /// Moves the pointer and presses-then-releases the left button: queue a MouseState with
        /// position set and the button bit set, InputSystem.Update(), wait, then queue
        /// position-only (button cleared) and Update() again. The release is always attempted, even
        /// if the press failed -- a press with no release would jam the virtual pointer, same
        /// reasoning as GamepadCommands.TapButton.
        /// </summary>
        private static void TapAt(object device, float x, float y, out bool sentPress, out string pressError, out bool sentRelease, out string releaseError)
        {
            pressError = null;
            releaseError = null;

            object pressedState;
            bool builtPressed = BuildMouseState(x, y, true, out pressedState, out pressError);
            sentPress = builtPressed && QueueAndUpdate(device, pressedState, out pressError);

            Thread.Sleep(33);

            object releasedState;
            bool builtReleased = BuildMouseState(x, y, false, out releasedState, out releaseError);
            sentRelease = builtReleased && QueueAndUpdate(device, releasedState, out releaseError);
        }

        // ============================================================== device resolution

        /// <summary>Reuses UnityEngine.InputSystem.Mouse.current if present; only calls InputSystem.AddDevice&lt;Mouse&gt;() when none exists.</summary>
        private static bool EnsureMouse(out object device, out bool created, out string error)
        {
            device = null;
            created = false;
            error = null;

            Type mouseType = AccessTools.TypeByName("UnityEngine.InputSystem.Mouse");
            if (mouseType == null) { error = "UnityEngine.InputSystem.Mouse type not found"; return false; }

            PropertyInfo currentProp = AccessTools.Property(mouseType, "current");
            if (currentProp == null) { error = "Mouse.current property not found"; return false; }

            object current;
            try { current = currentProp.GetValue(null, null); }
            catch (Exception ex) { error = "Mouse.current threw: " + ex.Message; return false; }

            if (current != null)
            {
                device = current;
                created = false;
                if (_log != null) _log.LogInfo("Crucible mouse: reusing existing Mouse.current.");
                return true;
            }

            Type inputSystemType = AccessTools.TypeByName("UnityEngine.InputSystem.InputSystem");
            if (inputSystemType == null) { error = "UnityEngine.InputSystem.InputSystem type not found"; return false; }

            MethodInfo addDeviceGeneric = FindGenericStaticMethod(inputSystemType, "AddDevice", 1, new[] { typeof(string) });
            if (addDeviceGeneric == null) { error = "InputSystem.AddDevice<TDevice>(string) not found"; return false; }

            try
            {
                MethodInfo bound = addDeviceGeneric.MakeGenericMethod(mouseType);
                object added = bound.Invoke(null, new object[] { null });
                if (added == null) { error = "InputSystem.AddDevice<Mouse>() returned null"; return false; }
                device = added;
                created = true;
                if (_log != null) _log.LogInfo("Crucible mouse: no Mouse.current found; created a virtual Mouse via InputSystem.AddDevice<Mouse>().");
                return true;
            }
            catch (TargetInvocationException ex)
            {
                error = "AddDevice<Mouse> threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = "AddDevice<Mouse> failed: " + ex.Message;
                return false;
            }
        }

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

        private static bool BuildMouseState(float x, float y, bool leftPressed, out object state, out string error)
        {
            state = null;
            error = null;
            try
            {
                Type stateType = AccessTools.TypeByName("UnityEngine.InputSystem.LowLevel.MouseState");
                if (stateType == null) { error = "UnityEngine.InputSystem.LowLevel.MouseState type not found"; return false; }

                Type buttonEnumType = AccessTools.TypeByName("UnityEngine.InputSystem.LowLevel.MouseButton");
                if (buttonEnumType == null) { error = "UnityEngine.InputSystem.LowLevel.MouseButton type not found"; return false; }

                object leftValue;
                try { leftValue = Enum.Parse(buttonEnumType, "Left", true); }
                catch (Exception)
                {
                    string[] names = Enum.GetNames(buttonEnumType);
                    error = "MouseButton has no member 'Left'; actual members: " + string.Join(", ", names);
                    return false;
                }

                object boxed = Activator.CreateInstance(stateType);

                FieldInfo positionField = AccessTools.Field(stateType, "position");
                if (positionField == null) { error = "MouseState.position field not found"; return false; }
                positionField.SetValue(boxed, new Vector2(x, y));

                MethodInfo withButton = AccessTools.Method(stateType, "WithButton", new[] { buttonEnumType, typeof(bool) });
                if (withButton == null) { error = "MouseState.WithButton(MouseButton, bool) not found"; return false; }

                state = withButton.Invoke(boxed, new object[] { leftValue, leftPressed });
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

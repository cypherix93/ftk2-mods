using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Keyboard-driving <c>crucible_key</c> console command. This is the harness's primary input
    /// path: FTK2 is fully keyboard-navigable, and the keyboard is a REAL device that the game has
    /// already paired to its single <c>InputPlayer</c>.
    ///
    /// It never calls AddDevice. A created keyboard would not be paired and its input would be
    /// silently ignored, which is precisely the gamepad's unsolved problem: no physical pad is
    /// attached, so <see cref="GamepadCommands"/> must create one, and even after
    /// <c>crucible_pad_pair</c> folds it into FTK2's own <c>InputPlayer</c> the game still ignores
    /// it (`InputUser.pairedDeviceCount` stays at 2 — keyboard and mouse only). Measured live
    /// 2026-08-23: with the window focused and taps spanning frames, <c>crucible_key</c> moved menu
    /// focus and activated buttons while <c>crucible_pad</c> did neither.
    ///
    /// Taps span real frames — see <see cref="Tick"/> and <see cref="HoldFrames"/>. Pressing and
    /// releasing inside one handler call does nothing at all, because handlers run on the game
    /// thread and no frame renders with the key held.
    ///
    /// Reflection-only, like every sibling: no compile-time reference to Unity.InputSystem.dll.
    /// </summary>
    internal static class KeyboardCommands
    {
        internal static string LastResult;
        private static ManualLogSource _log;
        private static bool _keyRegistered;

        private static readonly MethodInfo KeyHandler =
            typeof(KeyboardCommands).GetMethod("CrucibleKey", BindingFlags.Public | BindingFlags.Static);

        /// <summary>Frames the key stays held before the scheduled release. See <see cref="HoldFrames"/>.</summary>
        private const int ReleaseDelayFrames = HoldFrames.Minimum;

        private static object _pendingRelease;
        private static object _pendingReleaseDevice;
        private static int _pendingReleaseFrames;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>
        /// Called every tick. Flushes a scheduled key release once enough frames have passed, so a
        /// tap spans real frames instead of collapsing inside one handler call. Never throws.
        /// </summary>
        internal static void Tick()
        {
            if (_pendingRelease == null) return;
            if (_pendingReleaseFrames > 0) { _pendingReleaseFrames--; return; }

            object state = _pendingRelease;
            object device = _pendingReleaseDevice;
            _pendingRelease = null;
            _pendingReleaseDevice = null;

            string error;
            if (!QueueAndUpdate(device, state, out error) && _log != null)
            {
                _log.LogWarning("crucible_key: scheduled release failed: " + error);
            }
        }

        /// <summary>Deferred registration: the game's command registry does not exist during Awake.</summary>
        internal static void TryRegister()
        {
            if (_keyRegistered) return;
            _keyRegistered = GameBridge.RegisterCommand("crucible_key", KeyHandler, new List<string> { "key" });
            if (_keyRegistered && _log != null) _log.LogInfo("KeyboardCommands registered (crucible_key).");
        }

        /// <summary>crucible_key &lt;key&gt; — press and release one key on the real Keyboard.current.</summary>
        public static void CrucibleKey(string key)
        {
            try
            {
                string canonical;
                string parseError;
                if (!KeyboardKeyName.TryParse(key, out canonical, out parseError))
                {
                    LastResult = "error: " + parseError;
                    return;
                }

                object device;
                string deviceError;
                if (!ResolveKeyboard(out device, out deviceError))
                {
                    LastResult = "error: " + deviceError;
                    return;
                }

                object pressedState, releasedState;
                string buildError;
                if (!BuildKeyState(canonical, true, out pressedState, out buildError)
                    || !BuildKeyState(canonical, false, out releasedState, out buildError))
                {
                    LastResult = "error: " + buildError;
                    return;
                }

                string pressError;
                bool sentPress = QueueAndUpdate(device, pressedState, out pressError);

                // The release is scheduled on a LATER FRAME, never in this call.
                //
                // Command handlers run on the game thread, inside the RouterMono.Update postfix that
                // drives MainThreadPump. A press/Thread.Sleep/release sequence therefore completes
                // entirely within ONE frame: the game never gets an Update where the key reads as
                // held, so its Input Actions never see a press->release transition and nothing
                // happens. Measured 2026-08-23: a same-call tap moved no menu focus even with the
                // window focused, while a synthetic NavigationMoveEvent on the same screen did.
                _pendingRelease = releasedState;
                _pendingReleaseDevice = device;
                _pendingReleaseFrames = ReleaseDelayFrames;

                LastResult = DescribeDevice(device)
                    + " key=" + canonical
                    + " pressed=" + sentPress
                    + " releaseScheduledInFrames=" + ReleaseDelayFrames
                    + (pressError == null ? "" : " pressError=" + pressError);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_key threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_key failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Returns the REAL Keyboard.current, never creating one. See the class doc: a created
        /// keyboard would be unpaired and therefore ignored, which is worse than a clear error.
        /// </summary>
        private static bool ResolveKeyboard(out object device, out string error)
        {
            device = null;
            error = null;

            Type keyboardType = AccessTools.TypeByName("UnityEngine.InputSystem.Keyboard");
            if (keyboardType == null) { error = "UnityEngine.InputSystem.Keyboard type not found"; return false; }

            PropertyInfo currentProp = AccessTools.Property(keyboardType, "current");
            if (currentProp == null) { error = "Keyboard.current property not found"; return false; }

            try { device = currentProp.GetValue(null, null); }
            catch (Exception ex) { error = "Keyboard.current threw: " + ex.Message; return false; }

            if (device == null)
            {
                error = "Keyboard.current is null; refusing to create one (a created device is not "
                      + "paired to FTK2's InputPlayer and its input would be silently ignored)";
                return false;
            }
            return true;
        }

        private static bool BuildKeyState(string canonicalKey, bool pressed, out object state, out string error)
        {
            state = null;
            error = null;
            try
            {
                Type stateType = AccessTools.TypeByName("UnityEngine.InputSystem.LowLevel.KeyboardState");
                if (stateType == null) { error = "UnityEngine.InputSystem.LowLevel.KeyboardState type not found"; return false; }

                Type keyEnumType = AccessTools.TypeByName("UnityEngine.InputSystem.Key");
                if (keyEnumType == null) { error = "UnityEngine.InputSystem.Key type not found"; return false; }

                object keyValue;
                try { keyValue = Enum.Parse(keyEnumType, canonicalKey, true); }
                catch (Exception)
                {
                    error = "Key has no member '" + canonicalKey + "'; actual members: "
                          + string.Join(", ", Enum.GetNames(keyEnumType));
                    return false;
                }

                // Boxed struct: Set mutates the box in place, so the box is what gets queued.
                object boxed = Activator.CreateInstance(stateType);
                MethodInfo set = AccessTools.Method(stateType, "Set", new[] { keyEnumType, typeof(bool) });
                if (set == null) { error = "KeyboardState.Set(Key, bool) not found"; return false; }
                set.Invoke(boxed, new object[] { keyValue, pressed });

                state = boxed;
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

                MethodInfo queueGeneric = null;
                foreach (MethodInfo m in inputSystemType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!string.Equals(m.Name, "QueueStateEvent", StringComparison.Ordinal)) continue;
                    if (!m.IsGenericMethodDefinition) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 3 && ps[0].ParameterType == deviceType) { queueGeneric = m; break; }
                }
                if (queueGeneric == null) { error = "InputSystem.QueueStateEvent<TState>(InputDevice, TState, double) not found"; return false; }

                queueGeneric.MakeGenericMethod(state.GetType()).Invoke(null, new object[] { device, state, -1.0 });

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

        private static string DescribeDevice(object device)
        {
            if (device == null) return "(none)";
            try
            {
                PropertyInfo nameProp = AccessTools.Property(device.GetType(), "name");
                PropertyInfo idProp = AccessTools.Property(device.GetType(), "deviceId");
                object name = nameProp == null ? null : nameProp.GetValue(device, null);
                object id = idProp == null ? null : idProp.GetValue(device, null);
                return device.GetType().Name
                    + " name=" + (name == null ? "(null)" : "'" + name + "'")
                    + " deviceId=" + (id == null ? "?" : id.ToString())
                    + " origin=pre-existing";
            }
            catch (Exception) { return device.GetType().Name; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// <c>crucible_input_background &lt;on|off&gt;</c> — the fix for the hard blocker measured live
    /// 2026-08-23: injected gamepad presses (<see cref="GamepadCommands"/>) are queued and silently
    /// ignored while the game window lacks OS focus, and Windows refuses to let a background
    /// process steal focus (SetForegroundWindow/ShowWindow/BringWindowToTop/AttachThreadInput all
    /// verified NOT to work here). <c>UnityEngine.Application.runInBackground = true</c> alone is
    /// NOT sufficient (also verified live) — Unity's Input System has its own, separate focus
    /// policy: <c>UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior</c>. This sets
    /// both.
    ///
    /// No compile-time reference to Unity.InputSystem.dll (same posture as GamepadCommands): every
    /// type/member is resolved reflectively via AccessTools, so a missing/renamed API degrades to a
    /// reported error rather than a load failure. The target enum member is never hardcoded by
    /// name — <see cref="InputBackgroundBehaviorPicker"/> (Crucible.Core) enumerates the real,
    /// live-reflected member names and picks by heuristic, and reports the actual members if its
    /// heuristic finds nothing.
    /// </summary>
    internal static class InputBackgroundCommands
    {
        private static ManualLogSource _log;
        private static readonly MethodInfo Handler = typeof(InputBackgroundCommands).GetMethod("CrucibleInputBackground", BindingFlags.Public | BindingFlags.Static);

        private static bool _registered;

        /// <summary>Most recent crucible_input_background result. Same handoff contract as GamepadCommands.LastResult.</summary>
        internal static string LastResult;

        // Captured the first time this successfully turns background input ON, so "off" can
        // restore the game's own original backgroundBehavior instead of guessing a "default".
        private static bool _haveCapturedOriginal;
        private static string _capturedOriginalBackgroundBehaviorName;
        private static bool _capturedOriginalRunInBackground;

        // Auto-apply-at-startup bookkeeping (see AutoApplyTick).
        private static bool _autoApplied;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        internal static void TryRegister()
        {
            if (_registered) return;
            _registered = GameBridge.RegisterCommand("crucible_input_background", Handler, new List<string> { "on|off" });
            if (_registered && _log != null) _log.LogInfo("InputBackgroundCommands registered (crucible_input_background).");
        }

        /// <summary>
        /// Called every tick from MainThreadPump.OnTick (mirrors SinglePlayerGuard.Tick's shape):
        /// once, while <c>[Safety] ForceSinglePlayer</c> is on — this is a test harness where that's
        /// the default — applies background input automatically as soon as the Input System is
        /// reachable, so an unattended run doesn't need an explicit crucible_input_background call.
        /// Re-arms if ForceSinglePlayer is toggled off then back on. Never throws.
        /// </summary>
        internal static void AutoApplyTick()
        {
            if (CruciblePlugin.Instance == null || !CruciblePlugin.Instance.CfgForceSinglePlayer.Value)
            {
                _autoApplied = false;
                return;
            }

            if (_autoApplied) return;

            string beforeBehavior, afterBehavior, error;
            bool beforeRunInBg, afterRunInBg;
            bool ok = Apply(true, out beforeBehavior, out afterBehavior, out beforeRunInBg, out afterRunInBg, out error);

            if (!ok)
            {
                // Not reachable yet (Input System not loaded) or a genuine failure — either way,
                // retry next tick rather than getting stuck; TryResolveInputSettings below returning
                // "not found" this early in boot is expected and not worth spamming as a warning.
                return;
            }

            _autoApplied = true;
            if (_log != null)
            {
                _log.LogInfo("ForceSinglePlayer auto-apply: crucible_input_background on -- "
                    + "backgroundBehavior " + beforeBehavior + " -> " + afterBehavior
                    + ", Application.runInBackground " + beforeRunInBg + " -> " + afterRunInBg);
            }
        }

        // ----------------------------------------------------------------- crucible_input_background

        public static void CrucibleInputBackground(string pOnOff)
        {
            LastResult = null;

            bool on;
            if (string.Equals(pOnOff, "on", StringComparison.OrdinalIgnoreCase)) on = true;
            else if (string.Equals(pOnOff, "off", StringComparison.OrdinalIgnoreCase)) on = false;
            else
            {
                LastResult = "error: usage: crucible_input_background <on|off> (got '" + pOnOff + "')";
                return;
            }

            string beforeBehavior, afterBehavior, error;
            bool beforeRunInBg, afterRunInBg;
            bool ok = Apply(on, out beforeBehavior, out afterBehavior, out beforeRunInBg, out afterRunInBg, out error);

            if (!ok)
            {
                LastResult = "error: " + error;
                if (_log != null) _log.LogWarning("crucible_input_background failed: " + error);
                return;
            }

            LastResult = "backgroundBehavior: " + beforeBehavior + " -> " + afterBehavior
                + " | Application.runInBackground: " + beforeRunInBg + " -> " + afterRunInBg;
            if (_log != null) _log.LogInfo("crucible_input_background " + pOnOff + ": " + LastResult);
        }

        /// <summary>
        /// Does the actual reflective work, shared by the explicit command and the auto-apply tick.
        /// Reads backgroundBehavior/runInBackground BEFORE, applies the requested state, reads both
        /// back AFTER, and never throws — every failure mode (type not found, member not found,
        /// heuristic found nothing) is reported through <paramref name="error"/>.
        /// </summary>
        private static bool Apply(bool on, out string beforeBehavior, out string afterBehavior,
            out bool beforeRunInBackground, out bool afterRunInBackground, out string error)
        {
            beforeBehavior = null; afterBehavior = null;
            beforeRunInBackground = false; afterRunInBackground = false;
            error = null;

            object settings; PropertyInfo backgroundBehaviorProp;
            if (!TryResolveInputSettings(out settings, out backgroundBehaviorProp, out error)) return false;

            Type enumType = backgroundBehaviorProp.PropertyType;
            if (!enumType.IsEnum)
            {
                error = "InputSettings.backgroundBehavior is not an enum (" + enumType.FullName + ") -- API shape changed";
                return false;
            }

            object beforeValue = backgroundBehaviorProp.GetValue(settings, null);
            beforeBehavior = beforeValue == null ? "null" : beforeValue.ToString();

            PropertyInfo runInBgProp;
            if (!TryResolveApplicationRunInBackground(out runInBgProp, out error)) return false;
            object beforeRunInBgObj = runInBgProp.GetValue(null, null);
            beforeRunInBackground = beforeRunInBgObj is bool && (bool)beforeRunInBgObj;

            string[] memberNames = Enum.GetNames(enumType);

            if (on)
            {
                if (!_haveCapturedOriginal)
                {
                    _capturedOriginalBackgroundBehaviorName = beforeBehavior;
                    _capturedOriginalRunInBackground = beforeRunInBackground;
                    _haveCapturedOriginal = true;
                }

                string picked; string pickError;
                if (!InputBackgroundBehaviorPicker.TryPickIgnoreFocusMember(memberNames, out picked, out pickError))
                {
                    // Application.runInBackground is still unambiguous and worth setting even if
                    // we can't identify the Input System member -- report both facts rather than
                    // failing the whole call silently.
                    runInBgProp.SetValue(null, true, null);
                    afterRunInBackground = true;
                    afterBehavior = beforeBehavior; // unchanged
                    error = "Application.runInBackground set to true, but backgroundBehavior left unchanged: " + pickError;
                    return false;
                }

                object targetValue = Enum.Parse(enumType, picked, false);
                backgroundBehaviorProp.SetValue(settings, targetValue, null);
                runInBgProp.SetValue(null, true, null);
            }
            else
            {
                object restoreValue;
                if (_haveCapturedOriginal && !string.IsNullOrEmpty(_capturedOriginalBackgroundBehaviorName)
                    && Array.IndexOf(memberNames, _capturedOriginalBackgroundBehaviorName) >= 0)
                {
                    restoreValue = Enum.Parse(enumType, _capturedOriginalBackgroundBehaviorName, false);
                }
                else
                {
                    string picked; string pickError;
                    if (!InputBackgroundBehaviorPicker.TryPickResetDefaultMember(memberNames, out picked, out pickError))
                    {
                        error = "no captured original to restore and no reset-default member found: " + pickError;
                        return false;
                    }
                    restoreValue = Enum.Parse(enumType, picked, false);
                }

                backgroundBehaviorProp.SetValue(settings, restoreValue, null);
                bool restoreRunInBg = _haveCapturedOriginal ? _capturedOriginalRunInBackground : false;
                runInBgProp.SetValue(null, restoreRunInBg, null);
            }

            object afterValue = backgroundBehaviorProp.GetValue(settings, null);
            afterBehavior = afterValue == null ? "null" : afterValue.ToString();
            object afterRunInBgObj = runInBgProp.GetValue(null, null);
            afterRunInBackground = afterRunInBgObj is bool && (bool)afterRunInBgObj;

            return true;
        }

        /// <summary>
        /// Resolves UnityEngine.InputSystem.InputSystem.settings (a static property) and, on the
        /// InputSettings instance it returns, the backgroundBehavior property. Never throws.
        /// </summary>
        private static bool TryResolveInputSettings(out object settings, out PropertyInfo backgroundBehaviorProp, out string error)
        {
            settings = null;
            backgroundBehaviorProp = null;
            error = null;

            Type inputSystemType;
            try { inputSystemType = AccessTools.TypeByName("UnityEngine.InputSystem.InputSystem"); }
            catch (Exception ex) { error = "UnityEngine.InputSystem.InputSystem lookup threw: " + ex.Message; return false; }
            if (inputSystemType == null)
            {
                error = "type not found: UnityEngine.InputSystem.InputSystem (Input System not loaded yet, or Unity.InputSystem.dll missing)";
                return false;
            }

            PropertyInfo settingsProp = AccessTools.Property(inputSystemType, "settings");
            if (settingsProp == null)
            {
                error = "InputSystem.settings property not found -- API shape changed";
                return false;
            }

            object settingsValue;
            try { settingsValue = settingsProp.GetValue(null, null); }
            catch (Exception ex) { error = "InputSystem.settings threw: " + ex.Message; return false; }
            if (settingsValue == null)
            {
                error = "InputSystem.settings returned null";
                return false;
            }

            PropertyInfo prop = AccessTools.Property(settingsValue.GetType(), "backgroundBehavior");
            if (prop == null)
            {
                error = "InputSettings.backgroundBehavior property not found on " + settingsValue.GetType().FullName + " -- API shape changed";
                return false;
            }

            settings = settingsValue;
            backgroundBehaviorProp = prop;
            return true;
        }

        private static bool TryResolveApplicationRunInBackground(out PropertyInfo runInBgProp, out string error)
        {
            runInBgProp = null;
            error = null;

            Type appType;
            try { appType = AccessTools.TypeByName("UnityEngine.Application"); }
            catch (Exception ex) { error = "UnityEngine.Application lookup threw: " + ex.Message; return false; }
            if (appType == null)
            {
                error = "type not found: UnityEngine.Application";
                return false;
            }

            PropertyInfo prop = AccessTools.Property(appType, "runInBackground");
            if (prop == null)
            {
                error = "Application.runInBackground property not found -- API shape changed";
                return false;
            }

            runInBgProp = prop;
            return true;
        }
    }
}

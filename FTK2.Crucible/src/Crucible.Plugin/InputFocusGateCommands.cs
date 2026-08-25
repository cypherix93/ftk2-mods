using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// <c>crucible_input_focus_gate &lt;on|off&gt;</c> — layer 3 of the background-input fix.
    /// Verified live 2026-08-23 in the game's own Player.log: with
    /// <c>UnityEngine.Application.runInBackground</c> and Unity's
    /// <c>InputSystem.settings.backgroundBehavior</c> (see <see cref="InputBackgroundCommands"/>)
    /// both already applied, an injected <c>crucible_pad</c> press on a focused party slot STILL did
    /// nothing while the window lacked OS focus — because FTK2's own <c>InputController</c> gates ALL
    /// input independently of Unity, via a reference-counted disable-reason set:
    /// <c>RequestDisable(eDisableRequest, string)</c> adds a reason,
    /// <c>ReleaseDisable(eDisableRequest, string)</c> removes it, and input stays off while the set
    /// is non-empty. The log showed 3 <c>RequestDisable</c> calls for <c>LOST_FOCUS</c> against 2
    /// <c>ReleaseDisable</c> calls — one outstanding disable holding input off.
    ///
    /// <c>eDisableRequest</c> is used for many reasons besides focus loss (e.g. <c>ROUTE_CHANGE</c>,
    /// <c>SYSTEM_DIALOG_TRANSITION</c>) — only <c>LOST_FOCUS</c> may be defeated. Suppressing the
    /// others would let input fire during real transitions and cause misbehaviour, so this patches
    /// narrowly: a Harmony prefix on <c>RequestDisable</c> skips the original ONLY when the reason
    /// (compared by its live-reflected member name, resolved via <see cref="InputDisableReasonPicker"/>
    /// — never a hardcoded guess) is the lost-focus one; every other reason runs unmodified.
    /// Belt-and-braces, mirroring <see cref="SinglePlayerGuard"/>'s two-layer shape: a per-tick check
    /// clears any LOST_FOCUS entry already sitting in the disable set (e.g. the outstanding disable
    /// from before the gate turned on), since the prefix only stops future adds.
    ///
    /// No compile-time reference to the game or Unity.InputSystem: <c>InputController</c> and its
    /// nested <c>eDisableRequest</c> enum are resolved reflectively via AccessTools/Type.GetNestedType,
    /// and the Harmony prefix declares its enum-typed parameter as <c>object</c> (Harmony auto-boxes
    /// value-type original parameters into an <c>object</c>-typed patch parameter) — same
    /// no-compile-time-reference posture as <see cref="TurnHooks"/>'s <c>object __instance</c>
    /// postfixes.
    /// </summary>
    internal static class InputFocusGateCommands
    {
        private static ManualLogSource _log;

        private static readonly MethodInfo GateHandler = typeof(InputFocusGateCommands).GetMethod("CrucibleInputFocusGate", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo StateHandler = typeof(InputFocusGateCommands).GetMethod("CrucibleInputState", BindingFlags.Public | BindingFlags.Static);

        private static bool _gateRegistered;
        private static bool _stateRegistered;

        /// <summary>Most recent crucible_input_focus_gate / crucible_input_state result. Same
        /// shared-field handoff shape as ChaosCommands.LastResult (one field, two handlers, only one
        /// command ever executes per RPC call).</summary>
        internal static string LastResult;

        // Resolved once in Initialize(); null means "the patch target/reason couldn't be resolved,
        // the feature is disabled" (reported loudly, never a silent no-op pretending success).
        private static Type _inputControllerType;
        private static Type _disableRequestEnumType;
        private static object _lostFocusValue;
        private static string _lostFocusMemberName;
        private static MethodInfo _requestDisableMethod;
        private static MethodInfo _releaseDisableMethod;
        private static FieldInfo _disableRequestersField;
        private static PropertyInfo _instanceProp;
        private static bool _patchInstalled;
        private static string _initError;

        /// <summary>Whether RequestDisable(LOST_FOCUS) is currently being suppressed. Off by default
        /// until AutoApplyTick or an explicit command turns it on.</summary>
        private static bool _gateEnabled;

        private static bool _autoApplied;
        private static bool _loggedSuppression;
        private static bool _loggedRegistration;
        private static bool _loggedAutoApplySkip;

        // Logged once: CommandLineHelper._commandRegistry is still null on the first several
        // MainThreadPump ticks (the game's own CommandLineHelper.Initialize() hasn't run yet), so
        // GameBridge.RegisterCommand throws/logs a NullReferenceException per attempt during that
        // window. TryRegister is polled every tick and does NOT cache failure, so it keeps retrying
        // and self-heals the moment the game's Initialize() runs -- verified live (crucible_input_focus_gate
        // / crucible_input_state show several failed attempts followed by a successful registration a
        // few ticks later). This note exists so that transient cascade isn't mistaken for a permanent bug.
        private static bool _loggedRetryNote;

        internal static void Initialize(Harmony harmony, ManualLogSource log)
        {
            _log = log;

            _inputControllerType = AccessTools.TypeByName("InputController");
            if (_inputControllerType == null)
            {
                _initError = "type not found: InputController";
                log.LogWarning("Target NOT found: InputController — crucible_input_focus_gate disabled: " + _initError);
                DevKitBridge.ReportTarget("InputController", null);
                return;
            }

            _disableRequestEnumType = _inputControllerType.GetNestedType("eDisableRequest", BindingFlags.Public | BindingFlags.NonPublic);
            if (_disableRequestEnumType == null || !_disableRequestEnumType.IsEnum)
            {
                _initError = "InputController.eDisableRequest nested enum not found -- API shape changed";
                log.LogWarning(_initError);
                return;
            }

            string[] memberNames = Enum.GetNames(_disableRequestEnumType);
            string picked; string pickError;
            if (!InputDisableReasonPicker.TryPickLostFocusMember(memberNames, out picked, out pickError))
            {
                _initError = "could not identify the lost-focus eDisableRequest member: " + pickError;
                log.LogWarning(_initError);
                return;
            }

            _lostFocusMemberName = picked;
            _lostFocusValue = Enum.Parse(_disableRequestEnumType, picked, false);

            _requestDisableMethod = AccessTools.Method(_inputControllerType, "RequestDisable", new[] { _disableRequestEnumType, typeof(string) });
            _releaseDisableMethod = AccessTools.Method(_inputControllerType, "ReleaseDisable", new[] { _disableRequestEnumType, typeof(string) });
            _disableRequestersField = AccessTools.Field(_inputControllerType, "_disableRequesters");
            _instanceProp = AccessTools.Property(_inputControllerType, "Instance");

            if (_requestDisableMethod == null)
            {
                _initError = "InputController.RequestDisable(eDisableRequest, string) not found -- API shape changed";
                log.LogWarning(_initError);
                DevKitBridge.ReportTarget("InputController.RequestDisable", null);
                return;
            }

            log.LogInfo("Target found: InputController.RequestDisable (lost-focus reason resolved to '" + _lostFocusMemberName + "')");
            DevKitBridge.ReportTarget("InputController.RequestDisable", _requestDisableMethod);

            try
            {
                harmony.Patch(_requestDisableMethod, new HarmonyMethod(AccessTools.Method(typeof(InputFocusGateCommands), "RequestDisablePrefix")));
                _patchInstalled = true;

                // Hold the gate from here, NOT from a later tick. This is a test harness whose whole
                // purpose is driving the game unattended, and an unfocused window is the normal case
                // rather than the exception. Relying on AutoApplyTick was measured to leave the gate
                // down at startup (_patchInstalled=True, CfgForceSinglePlayer=True, yet
                // _autoApplied stayed False), so an unattended run booted with input disabled and
                // looked frozen. Setting it at the one point we KNOW is reached removes the
                // dependency entirely; AutoApplyTick still re-asserts it every tick.
                _gateEnabled = true;
                _autoApplied = true;
                log.LogInfo("crucible_input_focus_gate: held from Initialize (LOST_FOCUS will be suppressed).");
            }
            catch (Exception ex)
            {
                _initError = "Failed to patch InputController.RequestDisable: " + ex.Message;
                log.LogWarning(_initError);
            }
        }

        internal static void TryRegister()
        {
            if (!_gateRegistered) _gateRegistered = GameBridge.RegisterCommand("crucible_input_focus_gate", GateHandler, new List<string> { "on|off" });
            if (!_stateRegistered) _stateRegistered = GameBridge.RegisterCommand("crucible_input_state", StateHandler, new List<string>());

            // Log ONCE. TryRegister is polled from the per-frame tick, so an unguarded log line here
            // printed every frame -- tens of thousands of identical lines that bury real errors.
            if (_gateRegistered && _stateRegistered && !_loggedRegistration && _log != null)
            {
                _loggedRegistration = true;
                _log.LogInfo("InputFocusGateCommands registered (crucible_input_focus_gate, crucible_input_state).");
            }
            else if (!(_gateRegistered && _stateRegistered) && !_loggedRetryNote && _log != null)
            {
                _loggedRetryNote = true;
                _log.LogInfo("InputFocusGateCommands: one or more crucible_input_* registrations failed this tick -- "
                    + "expected if CommandLineHelper isn't initialized by the game yet; retrying every tick "
                    + "and will self-heal (see the preceding RegisterCommand warning for which method/arity).");
            }
        }

        /// <summary>
        /// Harmony prefix on InputController.RequestDisable. <c>object pRequester</c> instead of the
        /// game's private <c>eDisableRequest</c>: no compile-time game reference (SPEC posture).
        /// Returning false skips the original, so LOST_FOCUS never enters <c>_disableRequesters</c>
        /// while the gate is on. Every other reason returns true and runs untouched.
        /// </summary>
        private static bool RequestDisablePrefix(object pRequester, string pLogMessage)
        {
            if (!_gateEnabled) return true; // run the original -- gate off, behave stock.
            if (_lostFocusMemberName == null) return true; // never resolved; nothing to suppress.

            if (pRequester != null && string.Equals(pRequester.ToString(), _lostFocusMemberName, StringComparison.Ordinal))
            {
                if (!_loggedSuppression)
                {
                    _loggedSuppression = true;
                    if (_log != null) _log.LogInfo("crucible_input_focus_gate: suppressed RequestDisable(" + _lostFocusMemberName + ") -- input stays enabled while the window is unfocused.");
                }
                return false; // skip the original -- do not add LOST_FOCUS to the disable set.
            }

            return true; // a different reason (ROUTE_CHANGE, SYSTEM_DIALOG_TRANSITION, ...) -- run normally.
        }

        /// <summary>
        /// Called every tick from MainThreadPump.OnTick. Belt-and-braces: the prefix only stops
        /// future RequestDisable(LOST_FOCUS) calls, so this also clears any LOST_FOCUS entry already
        /// sitting in the disable set (e.g. the outstanding disable measured live before the gate
        /// turned on). Never throws.
        /// </summary>
        private static void ReleaseAnyHeldLostFocus()
        {
            if (!_gateEnabled || _lostFocusMemberName == null || _releaseDisableMethod == null) return;

            try
            {
                object instance;
                bool held;
                if (!TryReadDisableState(out instance, out held) || !held) return;

                _releaseDisableMethod.Invoke(instance, new object[] { _lostFocusValue, "crucible_input_focus_gate" });
            }
            catch (Exception ex)
            {
                if (_log != null) _log.LogWarning("crucible_input_focus_gate: failed to release an already-held LOST_FOCUS disable: " + ex.Message);
            }
        }

        /// <summary>
        /// Called from CruciblePlugin.OnTick, gated on [Safety] ForceSinglePlayer -- on by default,
        /// same auto-apply shape as InputBackgroundCommands.AutoApplyTick / SinglePlayerGuard.Tick.
        /// Turns the gate on once reachable, and keeps releasing any held LOST_FOCUS entry every tick
        /// thereafter (cheap: one HashSet.Contains read).
        /// </summary>
        internal static void AutoApplyTick()
        {
            if (!CruciblePlugin.ForceSinglePlayerEnabled)
            {
                if (!_loggedAutoApplySkip && _log != null)
                {
                    _loggedAutoApplySkip = true;
                    _log.LogInfo("crucible_input_focus_gate auto-apply skipped: instance="
                        + (CruciblePlugin.Instance == null ? "null" : "present")
                        + " forceSinglePlayer="
                        + (CruciblePlugin.Instance == null ? "?" : CruciblePlugin.Instance.CfgForceSinglePlayer.Value.ToString()));
                }
                _autoApplied = false;
                return;
            }

            if (!_patchInstalled) return; // not reachable yet, or init failed -- nothing to auto-apply.

            if (!_autoApplied)
            {
                _gateEnabled = true;
                _autoApplied = true;
                if (_log != null) _log.LogInfo("ForceSinglePlayer auto-apply: crucible_input_focus_gate on.");
            }

            ReleaseAnyHeldLostFocus();
        }

        // ----------------------------------------------------------------- crucible_input_focus_gate

        public static void CrucibleInputFocusGate(string pOnOff)
        {
            LastResult = null;

            bool on;
            if (string.Equals(pOnOff, "on", StringComparison.OrdinalIgnoreCase)) on = true;
            else if (string.Equals(pOnOff, "off", StringComparison.OrdinalIgnoreCase)) on = false;
            else
            {
                LastResult = "error: usage: crucible_input_focus_gate <on|off> (got '" + pOnOff + "')";
                return;
            }

            if (!_patchInstalled)
            {
                LastResult = "error: " + (_initError ?? "InputController.RequestDisable patch not installed");
                return;
            }

            string before = DescribeDisableReasons();
            _gateEnabled = on;
            if (on) ReleaseAnyHeldLostFocus();
            string after = DescribeDisableReasons();

            LastResult = "gate: " + (on ? "on" : "off")
                + " | lostFocusReason='" + _lostFocusMemberName + "'"
                + " | disableReasons before: " + before
                + " | disableReasons after: " + after;
            if (_log != null) _log.LogInfo("crucible_input_focus_gate " + pOnOff + ": " + LastResult);
        }

        // ----------------------------------------------------------------- crucible_input_state

        public static void CrucibleInputState()
        {
            LastResult = null;

            if (_inputControllerType == null || _instanceProp == null || _disableRequestersField == null)
            {
                LastResult = "error: " + (_initError ?? "InputController introspection not available");
                return;
            }

            string reasons = DescribeDisableReasons();
            bool disabled = reasons != null && reasons != "[]";

            LastResult = "inputDisabled=" + disabled
                + " reasons=" + reasons
                + " gateHeld=" + _gateEnabled
                + " lostFocusReason='" + (_lostFocusMemberName ?? "(unresolved)") + "'";
        }

        // ----------------------------------------------------------------- reflective reads

        private static bool TryReadDisableState(out object instance, out bool lostFocusHeld)
        {
            instance = null;
            lostFocusHeld = false;

            if (_instanceProp == null || _disableRequestersField == null || _lostFocusValue == null) return false;

            instance = _instanceProp.GetValue(null, null);
            if (instance == null) return false; // not reachable yet -- try again next tick.

            object setObj = _disableRequestersField.GetValue(instance);
            IEnumerable set = setObj as IEnumerable;
            if (set == null) return false;

            foreach (object item in set)
            {
                if (item != null && string.Equals(item.ToString(), _lostFocusMemberName, StringComparison.Ordinal))
                {
                    lostFocusHeld = true;
                    break;
                }
            }
            return true;
        }

        /// <summary>Renders the live _disableRequesters set as "[REASON_A, REASON_B]", or "[]" / "(unavailable)".</summary>
        private static string DescribeDisableReasons()
        {
            if (_instanceProp == null || _disableRequestersField == null) return "(unavailable)";

            object instance;
            try { instance = _instanceProp.GetValue(null, null); }
            catch (Exception ex) { return "(unavailable: " + ex.Message + ")"; }
            if (instance == null) return "(unavailable: InputController.Instance is null)";

            object setObj;
            try { setObj = _disableRequestersField.GetValue(instance); }
            catch (Exception ex) { return "(unavailable: " + ex.Message + ")"; }
            IEnumerable set = setObj as IEnumerable;
            if (set == null) return "(unavailable: _disableRequesters not enumerable)";

            List<string> names = new List<string>();
            foreach (object item in set) names.Add(item == null ? "null" : item.ToString());
            names.Sort(StringComparer.Ordinal);

            StringBuilder sb = new StringBuilder();
            sb.Append("[");
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(names[i]);
            }
            sb.Append("]");
            return sb.ToString();
        }
    }
}

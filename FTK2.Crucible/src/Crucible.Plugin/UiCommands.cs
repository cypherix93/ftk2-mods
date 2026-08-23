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
    /// UI-driving <c>crucible_ui_*</c> console commands (SPEC S3 UI-driving): read what's on screen
    /// and press buttons/navigate — the Playwright model, driving the live UIToolkit tree instead of
    /// reverse-engineering each Director's private API. See docs/research/crucible-ui-driving.md for
    /// the verified reflective API surface this is built on.
    ///
    /// Follows <see cref="ReflectionCommands"/>'s shape: registration via
    /// <see cref="GameBridge.RegisterCommand"/>, results handed off through <see cref="LastResult"/>
    /// for RpcServer to pick up, never throw out of a handler.
    ///
    /// <b>Registration is deferred to <see cref="TryRegister"/>, polled from
    /// <see cref="MainThreadPump.OnTick"/></b> (wired in CruciblePlugin.PollHotkeys) rather than
    /// called once from Awake: the game's own command registry inside CommandLineHelper does not
    /// exist yet during Awake, and registering that early throws a NullReferenceException from
    /// inside CommandLineHelper. Each command is tracked and registered independently so a partial
    /// success on one tick does not re-register (and potentially double-register) a command that
    /// already succeeded on an earlier tick.
    ///
    /// <b>Discrete string parameters only.</b> CommandLineHelper marshals args into a handler's
    /// individual typed parameters (vocabulary: int/float/double/bool/string/Vector2/Vector3 only) —
    /// a handler taking <c>string[]</c> is rejected at registration. <c>crucible_ui_submit</c>/
    /// <c>crucible_ui_cancel</c> have no domain argument, but there is no live-game confirmation that
    /// a zero-parameter handler registers cleanly, and no way to get that confirmation before the
    /// orchestrator's next launch. Rather than risk a silent registration failure, both take one
    /// ignored string parameter.
    /// </summary>
    internal static class UiCommands
    {
        private static ManualLogSource _log;

        /// <summary>The most recent crucible_ui_* result, rendered as text. Same handoff contract as ReflectionCommands.LastResult.</summary>
        internal static string LastResult;

        private static readonly MethodInfo DumpHandler = typeof(UiCommands).GetMethod("CrucibleUiDump", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo ClickHandler = typeof(UiCommands).GetMethod("CrucibleUiClick", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo NavHandler = typeof(UiCommands).GetMethod("CrucibleUiNav", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo SubmitHandler = typeof(UiCommands).GetMethod("CrucibleUiSubmit", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo CancelHandler = typeof(UiCommands).GetMethod("CrucibleUiCancel", BindingFlags.Public | BindingFlags.Static);

        private static bool _dumpRegistered;
        private static bool _clickRegistered;
        private static bool _navRegistered;
        private static bool _submitRegistered;
        private static bool _cancelRegistered;
        private static bool _loggedWaiting;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>Called every tick from MainThreadPump.OnTick until every command is registered.</summary>
        internal static void TryRegister()
        {
            if (_dumpRegistered && _clickRegistered && _navRegistered && _submitRegistered && _cancelRegistered) return;

            if (!_dumpRegistered) _dumpRegistered = GameBridge.RegisterCommand("crucible_ui_dump", DumpHandler, new List<string> { "filter" });
            if (!_clickRegistered) _clickRegistered = GameBridge.RegisterCommand("crucible_ui_click", ClickHandler, new List<string> { "selector" });
            if (!_navRegistered) _navRegistered = GameBridge.RegisterCommand("crucible_ui_nav", NavHandler, new List<string> { "direction" });
            if (!_submitRegistered) _submitRegistered = GameBridge.RegisterCommand("crucible_ui_submit", SubmitHandler, new List<string> { "_unused" });
            if (!_cancelRegistered) _cancelRegistered = GameBridge.RegisterCommand("crucible_ui_cancel", CancelHandler, new List<string> { "_unused" });

            if (_dumpRegistered && _clickRegistered && _navRegistered && _submitRegistered && _cancelRegistered)
            {
                if (_log != null) _log.LogInfo("UiCommands registered (crucible_ui_dump/click/nav/submit/cancel).");
            }
            else if (!_loggedWaiting)
            {
                _loggedWaiting = true;
                if (_log != null) _log.LogInfo("UiCommands registration incomplete; will retry each tick until the game's command registry is ready.");
            }
        }

        // ============================================================== crucible_ui_dump

        /// <summary>crucible_ui_dump &lt;filter&gt; — snapshot the visible UI. "-" for everything.</summary>
        public static void CrucibleUiDump(string filter)
        {
            LastResult = null;
            try
            {
                List<UiElementInfo> elements;
                string error;
                if (!WalkAllElements(out elements, out error))
                {
                    LastResult = "error: " + error;
                    return;
                }

                int matched, skipped;
                bool truncated;
                string rendered = UiTreeRenderer.Render(elements, filter, UiTreeRenderer.DefaultCap, out matched, out skipped, out truncated);

                StringBuilder sb = new StringBuilder();
                sb.Append(rendered);
                sb.Append("\n(matchedVisible=").Append(matched)
                    .Append(" skippedInvisible=").Append(skipped)
                    .Append(" truncated=").Append(truncated).Append(")");
                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_ui_dump threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_ui_dump failed: " + ex.Message);
            }
        }

        // ============================================================== crucible_ui_click

        /// <summary>crucible_ui_click &lt;selector&gt; — clicks the first visible Button whose name/text contains selector.</summary>
        public static void CrucibleUiClick(string selector)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(selector))
                {
                    LastResult = "error: usage: crucible_ui_click <selector>";
                    return;
                }

                List<UiElementInfo> buttonInfos;
                List<object> buttonRefs;
                string error;
                if (!WalkButtons(out buttonInfos, out buttonRefs, out error))
                {
                    LastResult = "error: " + error;
                    return;
                }

                UiSelectorMatcher.Result match = UiSelectorMatcher.Find(buttonInfos, selector, out error);
                if (error != null)
                {
                    LastResult = "error: " + error;
                    return;
                }

                if (!match.Found)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("no visible button matches '").Append(selector).Append("'. Visible buttons:");
                    if (match.AllVisible.Count == 0)
                    {
                        sb.Append(" (none)");
                    }
                    else
                    {
                        foreach (UiElementInfo b in match.AllVisible)
                            sb.Append("\n  - ").Append(UiTreeRenderer.FormatLine(b));
                    }
                    LastResult = sb.ToString();
                    return;
                }

                int matchedIndex = buttonInfos.IndexOf(match.Match);
                object targetButton = (matchedIndex >= 0 && matchedIndex < buttonRefs.Count) ? buttonRefs[matchedIndex] : null;
                if (targetButton == null)
                {
                    LastResult = "error: internal: matched element had no live reference";
                    return;
                }

                string invokeError;
                bool invoked = InvokeButtonClick(targetButton, out invokeError);

                StringBuilder result = new StringBuilder();
                result.Append("matched: ").Append(UiTreeRenderer.FormatLine(match.Match));
                result.Append("\ncount=").Append(1 + match.OtherMatches.Count);
                if (match.OtherMatches.Count > 0)
                {
                    result.Append(" others:");
                    foreach (UiElementInfo o in match.OtherMatches)
                        result.Append("\n  - ").Append(UiTreeRenderer.FormatLine(o));
                }
                result.Append("\ninvoked=").Append(invoked);
                if (!invoked) result.Append(" error=").Append(invokeError);

                LastResult = result.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_ui_click threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_ui_click failed: " + ex.Message);
            }
        }

        // ============================================================== crucible_ui_nav / submit / cancel

        /// <summary>crucible_ui_nav &lt;direction&gt; — up/down/left/right. Sends a NavigationMoveEvent to the focused element (or root if nothing is focused).</summary>
        public static void CrucibleUiNav(string direction)
        {
            LastResult = null;
            try
            {
                string canonical, parseError;
                if (!UiDirection.TryParse(direction, out canonical, out parseError))
                {
                    LastResult = "error: " + parseError;
                    return;
                }

                object root, focusedBefore;
                string error;
                if (!TryGetFirstRootAndFocus(out root, out focusedBefore, out error))
                {
                    LastResult = "error: " + error;
                    return;
                }

                bool usedFallback = focusedBefore == null;
                object target = usedFallback ? root : focusedBefore;

                object evt;
                string buildError;
                if (!BuildNavigationMoveEvent(canonical, out evt, out buildError))
                {
                    LastResult = "error: " + buildError;
                    return;
                }

                string sendError;
                bool sent = SendEvent(target, evt, out sendError);
                object focusedAfter = GetFocusedElementFor(root);

                LastResult = FormatNavResult("direction=" + canonical, target, usedFallback, sent, sendError, focusedBefore, focusedAfter);
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_ui_nav threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_ui_nav failed: " + ex.Message);
            }
        }

        /// <summary>crucible_ui_submit &lt;_unused&gt; — sends a NavigationSubmitEvent to the focused element (the controller "A"/Enter).</summary>
        public static void CrucibleUiSubmit(string _unused)
        {
            LastResult = null;
            try
            {
                DispatchSimpleNavigationEvent("UnityEngine.UIElements.NavigationSubmitEvent", "submit");
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_ui_submit threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_ui_submit failed: " + ex.Message);
            }
        }

        /// <summary>crucible_ui_cancel &lt;_unused&gt; — sends a NavigationCancelEvent to the focused element (the controller "B"/Escape/Back).</summary>
        public static void CrucibleUiCancel(string _unused)
        {
            LastResult = null;
            try
            {
                DispatchSimpleNavigationEvent("UnityEngine.UIElements.NavigationCancelEvent", "cancel");
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_ui_cancel threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_ui_cancel failed: " + ex.Message);
            }
        }

        private static void DispatchSimpleNavigationEvent(string eventTypeName, string label)
        {
            object root, focusedBefore;
            string error;
            if (!TryGetFirstRootAndFocus(out root, out focusedBefore, out error))
            {
                LastResult = "error: " + error;
                return;
            }

            bool usedFallback = focusedBefore == null;
            object target = usedFallback ? root : focusedBefore;

            object evt;
            string buildError;
            if (!BuildSimpleNavigationEvent(eventTypeName, out evt, out buildError))
            {
                LastResult = "error: " + buildError;
                return;
            }

            string sendError;
            bool sent = SendEvent(target, evt, out sendError);
            object focusedAfter = GetFocusedElementFor(root);

            LastResult = FormatNavResult(label, target, usedFallback, sent, sendError, focusedBefore, focusedAfter);
        }

        private static string FormatNavResult(string header, object target, bool usedFallback, bool sent, string sendError, object focusedBefore, object focusedAfter)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(header);
            sb.Append("\ntarget=").Append(DescribeElement(target));
            if (usedFallback) sb.Append(" (fallback: root visual element of the first live UIDocument — nothing was focused)");
            sb.Append("\nsent=").Append(sent);
            if (!sent) sb.Append(" error=").Append(sendError);
            sb.Append("\nfocusBefore=").Append(DescribeElement(focusedBefore));
            sb.Append("\nfocusAfter=").Append(DescribeElement(focusedAfter));
            return sb.ToString();
        }

        // ============================================================== reflective tree walk

        private static bool WalkAllElements(out List<UiElementInfo> elements, out string error)
        {
            elements = new List<UiElementInfo>();
            error = null;

            List<object> docRoots;
            if (!FindDocumentRoots(out docRoots, out error)) return false;

            foreach (object root in docRoots)
            {
                object docFocused = GetFocusedElementFor(root);
                WalkElement(root, docFocused, elements);
            }
            return true;
        }

        private static bool WalkButtons(out List<UiElementInfo> infos, out List<object> refs, out string error)
        {
            infos = new List<UiElementInfo>();
            refs = new List<object>();
            error = null;

            Type buttonType = AccessTools.TypeByName("UnityEngine.UIElements.Button");
            if (buttonType == null) { error = "UnityEngine.UIElements.Button type not found"; return false; }

            List<object> docRoots;
            if (!FindDocumentRoots(out docRoots, out error)) return false;

            foreach (object root in docRoots)
            {
                object docFocused = GetFocusedElementFor(root);
                WalkForType(root, buttonType, docFocused, infos, refs);
            }
            return true;
        }

        /// <summary>Every live UIDocument's rootVisualElement this frame.</summary>
        private static bool FindDocumentRoots(out List<object> roots, out string error)
        {
            roots = new List<object>();
            error = null;

            Type unityObjectType = AccessTools.TypeByName("UnityEngine.Object");
            Type uiDocumentType = AccessTools.TypeByName("UnityEngine.UIElements.UIDocument");
            if (unityObjectType == null || uiDocumentType == null)
            {
                error = "UnityEngine.Object or UnityEngine.UIElements.UIDocument type not found";
                return false;
            }

            MethodInfo findObjectsOfType = AccessTools.Method(unityObjectType, "FindObjectsOfType", new[] { typeof(Type) });
            if (findObjectsOfType == null) { error = "UnityEngine.Object.FindObjectsOfType(Type) not found"; return false; }

            object docsObj;
            try { docsObj = findObjectsOfType.Invoke(null, new object[] { uiDocumentType }); }
            catch (Exception ex) { error = "FindObjectsOfType threw: " + ex.Message; return false; }

            IEnumerable docs = docsObj as IEnumerable;
            if (docs == null) { error = "FindObjectsOfType returned no enumerable result"; return false; }

            PropertyInfo rootProp = AccessTools.Property(uiDocumentType, "rootVisualElement");
            if (rootProp == null) { error = "UIDocument.rootVisualElement not found"; return false; }

            foreach (object doc in docs)
            {
                if (doc == null) continue;
                object root;
                try { root = rootProp.GetValue(doc, null); }
                catch (Exception) { continue; }
                if (root != null) roots.Add(root);
            }

            return true;
        }

        private static void WalkElement(object ve, object focusedElement, List<UiElementInfo> outList)
        {
            if (ve == null) return;

            Type t = ve.GetType();
            outList.Add(DescribeAsInfo(ve, t, focusedElement));

            IEnumerable children = GetChildren(ve, t);
            if (children == null) return;
            foreach (object child in children) WalkElement(child, focusedElement, outList);
        }

        private static void WalkForType(object ve, Type matchType, object focusedElement, List<UiElementInfo> infos, List<object> refs)
        {
            if (ve == null) return;

            Type t = ve.GetType();
            if (matchType.IsInstanceOfType(ve))
            {
                infos.Add(DescribeAsInfo(ve, t, focusedElement));
                refs.Add(ve);
            }

            IEnumerable children = GetChildren(ve, t);
            if (children == null) return;
            foreach (object child in children) WalkForType(child, matchType, focusedElement, infos, refs);
        }

        private static UiElementInfo DescribeAsInfo(object ve, Type t, object focusedElement)
        {
            string name = ReadStringProp(ve, "name");
            string text = ReadStringProp(ve, "text");
            bool visible = ReadBoolProp(ve, "visible", true);
            bool enabled = ReadBoolProp(ve, "enabledInHierarchy", true);
            bool focused = focusedElement != null && ReferenceEquals(ve, focusedElement);
            return new UiElementInfo(t.Name, name, text, visible, enabled, focused);
        }

        private static IEnumerable GetChildren(object ve, Type t)
        {
            try
            {
                MethodInfo childrenMethod = AccessTools.Method(t, "Children");
                if (childrenMethod == null) return null;
                return childrenMethod.Invoke(ve, null) as IEnumerable;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ============================================================== focus

        private static bool TryGetFirstRootAndFocus(out object root, out object focusedElement, out string error)
        {
            root = null;
            focusedElement = null;

            List<object> roots;
            if (!FindDocumentRoots(out roots, out error)) return false;
            if (roots.Count == 0) { error = "no live UIDocument with a root element"; return false; }

            root = roots[0];
            focusedElement = GetFocusedElementFor(root);
            return true;
        }

        private static object GetFocusedElementFor(object root)
        {
            object focusController = ReadProp(root, "focusController");
            if (focusController == null) return null;
            return ReadProp(focusController, "focusedElement");
        }

        // ============================================================== click (Option B: Clickable.clicked)

        /// <summary>
        /// Click mechanism: reads Button.clickable (a Clickable), then invokes the public Action field
        /// "clicked" the game itself wired up with `button.clicked += ...`. Falls back to
        /// "clickedWithEventInfo" (Action&lt;EventBase&gt;, invoked with null) if "clicked" is unset.
        /// This bypasses focus/panel/dispatch entirely and runs exactly the callback the game
        /// registered — see docs/research/crucible-ui-driving.md §4 Option B.
        /// </summary>
        private static bool InvokeButtonClick(object button, out string error)
        {
            error = null;
            try
            {
                object clickable = ReadProp(button, "clickable");
                if (clickable == null) { error = "button.clickable is null"; return false; }

                FieldInfo clickedField = AccessTools.Field(clickable.GetType(), "clicked");
                Delegate clicked = clickedField == null ? null : clickedField.GetValue(clickable) as Delegate;
                if (clicked != null)
                {
                    clicked.DynamicInvoke();
                    return true;
                }

                FieldInfo clickedWithInfoField = AccessTools.Field(clickable.GetType(), "clickedWithEventInfo");
                Delegate clickedWithInfo = clickedWithInfoField == null ? null : clickedWithInfoField.GetValue(clickable) as Delegate;
                if (clickedWithInfo != null)
                {
                    clickedWithInfo.DynamicInvoke(new object[] { null });
                    return true;
                }

                error = "clickable.clicked and clickable.clickedWithEventInfo are both null (no handler wired)";
                return false;
            }
            catch (TargetInvocationException ex)
            {
                error = "click callback threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = "invoke failed: " + ex.Message;
                return false;
            }
        }

        // ============================================================== nav events (Option A: SendEvent)

        private static bool BuildNavigationMoveEvent(string canonicalDirection, out object evt, out string error)
        {
            evt = null;
            error = null;
            try
            {
                Type moveEventType = AccessTools.TypeByName("UnityEngine.UIElements.NavigationMoveEvent");
                if (moveEventType == null) { error = "UnityEngine.UIElements.NavigationMoveEvent type not found"; return false; }

                Type directionType = moveEventType.GetNestedType("Direction", AccessTools.all);
                if (directionType == null) { error = "NavigationMoveEvent.Direction nested type not found"; return false; }

                object directionValue;
                try
                {
                    directionValue = Enum.Parse(directionType, canonicalDirection, true);
                }
                catch (Exception)
                {
                    string[] names = Enum.GetNames(directionType);
                    error = "NavigationMoveEvent.Direction has no member '" + canonicalDirection
                        + "'; actual members: " + string.Join(", ", names);
                    return false;
                }

                Type modifiersType = AccessTools.TypeByName("UnityEngine.EventModifiers");
                if (modifiersType == null) { error = "UnityEngine.EventModifiers type not found"; return false; }
                object modifiersNone = Enum.ToObject(modifiersType, 0);

                MethodInfo getPooled = AccessTools.Method(moveEventType, "GetPooled", new[] { directionType, modifiersType });
                if (getPooled == null) { error = "NavigationMoveEvent.GetPooled(Direction, EventModifiers) not found"; return false; }

                evt = getPooled.Invoke(null, new object[] { directionValue, modifiersNone });
                if (evt == null) { error = "GetPooled returned null"; return false; }
                return true;
            }
            catch (Exception ex)
            {
                error = "build threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
        }

        /// <summary>
        /// NavigationSubmitEvent/NavigationCancelEvent don't declare GetPooled directly — it's on the
        /// shared generic base NavigationEventBase&lt;T&gt;. Resolving via GetMethod with
        /// FlattenHierarchy against the concrete type finds the inherited generic method bound to it,
        /// per docs/research/crucible-ui-driving.md §4 Option A.
        /// </summary>
        private static bool BuildSimpleNavigationEvent(string eventTypeName, out object evt, out string error)
        {
            evt = null;
            error = null;
            try
            {
                Type eventType = AccessTools.TypeByName(eventTypeName);
                if (eventType == null) { error = eventTypeName + " type not found"; return false; }

                Type modifiersType = AccessTools.TypeByName("UnityEngine.EventModifiers");
                if (modifiersType == null) { error = "UnityEngine.EventModifiers type not found"; return false; }

                MethodInfo getPooled = eventType.GetMethod(
                    "GetPooled",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy,
                    null,
                    new[] { modifiersType },
                    null);
                if (getPooled == null) { error = eventTypeName + ".GetPooled(EventModifiers) not found (checked inherited members)"; return false; }

                object modifiersNone = Enum.ToObject(modifiersType, 0);
                evt = getPooled.Invoke(null, new object[] { modifiersNone });
                if (evt == null) { error = "GetPooled returned null"; return false; }
                return true;
            }
            catch (Exception ex)
            {
                error = "build threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
        }

        private static bool SendEvent(object target, object evt, out string error)
        {
            error = null;
            if (target == null) { error = "no target element"; return false; }
            if (evt == null) { error = "no event to send"; return false; }

            try
            {
                Type eventBaseType = AccessTools.TypeByName("UnityEngine.UIElements.EventBase");
                if (eventBaseType == null) { error = "UnityEngine.UIElements.EventBase type not found"; return false; }

                MethodInfo sendEvent = AccessTools.Method(target.GetType(), "SendEvent", new[] { eventBaseType });
                if (sendEvent == null) { error = "SendEvent(EventBase) not found on " + target.GetType().FullName; return false; }

                sendEvent.Invoke(target, new object[] { evt });
                return true;
            }
            catch (TargetInvocationException ex)
            {
                error = "SendEvent threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = "SendEvent failed: " + ex.Message;
                return false;
            }
        }

        // ============================================================== safe reflective reads

        private static string DescribeElement(object ve)
        {
            if (ve == null) return "(none)";
            Type t = ve.GetType();
            string name = ReadStringProp(ve, "name");
            string text = ReadStringProp(ve, "text");
            return t.Name + " name=" + (name == null ? "(null)" : "'" + name + "'")
                + " text=" + (text == null ? "(null)" : "'" + text + "'");
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

        private static bool ReadBoolProp(object obj, string propName, bool defaultValue)
        {
            object v = ReadProp(obj, propName);
            return v is bool ? (bool)v : defaultValue;
        }
    }
}

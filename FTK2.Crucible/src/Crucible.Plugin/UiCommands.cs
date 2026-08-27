using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
        private static readonly MethodInfo ClickNthHandler = typeof(UiCommands).GetMethod("CrucibleUiClickNth", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo MatchesHandler = typeof(UiCommands).GetMethod("CrucibleUiMatches", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo FocusHandler = typeof(UiCommands).GetMethod("CrucibleUiFocus", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo NavHandler = typeof(UiCommands).GetMethod("CrucibleUiNav", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo SubmitHandler = typeof(UiCommands).GetMethod("CrucibleUiSubmit", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo CancelHandler = typeof(UiCommands).GetMethod("CrucibleUiCancel", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo PressHandler = typeof(UiCommands).GetMethod("CrucibleUiPress", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo WhereHandler = typeof(UiCommands).GetMethod("CrucibleUiWhere", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo DialogueAdvanceHandler = typeof(UiCommands).GetMethod("CrucibleDialogueAdvance", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo DialogueChooseHandler = typeof(UiCommands).GetMethod("CrucibleDialogueChoose", BindingFlags.Public | BindingFlags.Static);

        private static bool _dumpRegistered;
        private static bool _clickRegistered;
        private static bool _clickNthRegistered;
        private static bool _matchesRegistered;
        private static bool _focusRegistered;
        private static bool _navRegistered;
        private static bool _submitRegistered;
        private static bool _cancelRegistered;
        private static bool _pressRegistered;
        private static bool _whereRegistered;
        private static bool _dialogueAdvanceRegistered;
        private static bool _dialogueChooseRegistered;
        private static bool _loggedWaiting;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>Called every tick from MainThreadPump.OnTick until every command is registered.</summary>
        internal static void TryRegister()
        {
            if (_dumpRegistered && _clickRegistered && _clickNthRegistered && _matchesRegistered && _focusRegistered && _navRegistered && _submitRegistered && _cancelRegistered
                && _pressRegistered && _whereRegistered && _dialogueAdvanceRegistered && _dialogueChooseRegistered) return;

            if (!_dumpRegistered) _dumpRegistered = GameBridge.RegisterCommand("crucible_ui_dump", DumpHandler, new List<string> { "filter", "kinds" });
            if (!_clickRegistered) _clickRegistered = GameBridge.RegisterCommand("crucible_ui_click", ClickHandler, new List<string> { "selector" });
            if (!_clickNthRegistered) _clickNthRegistered = GameBridge.RegisterCommand("crucible_ui_click_nth", ClickNthHandler, new List<string> { "selector", "index", "document" });
            if (!_matchesRegistered) _matchesRegistered = GameBridge.RegisterCommand("crucible_ui_matches", MatchesHandler, new List<string> { "selector", "document" });
            if (!_focusRegistered) _focusRegistered = GameBridge.RegisterCommand("crucible_ui_focus", FocusHandler, new List<string> { "selector" });
            if (!_navRegistered) _navRegistered = GameBridge.RegisterCommand("crucible_ui_nav", NavHandler, new List<string> { "direction" });
            if (!_submitRegistered) _submitRegistered = GameBridge.RegisterCommand("crucible_ui_submit", SubmitHandler, new List<string> { "_unused" });
            if (!_cancelRegistered) _cancelRegistered = GameBridge.RegisterCommand("crucible_ui_cancel", CancelHandler, new List<string> { "_unused" });
            if (!_pressRegistered) _pressRegistered = GameBridge.RegisterCommand("crucible_ui_press", PressHandler, new List<string> { "selector" });
            if (!_whereRegistered) _whereRegistered = GameBridge.RegisterCommand("crucible_ui_where", WhereHandler, new List<string> { "_unused" });
            if (!_dialogueAdvanceRegistered) _dialogueAdvanceRegistered = GameBridge.RegisterCommand("crucible_dialogue_advance", DialogueAdvanceHandler, new List<string> { "maxPresses" });
            if (!_dialogueChooseRegistered) _dialogueChooseRegistered = GameBridge.RegisterCommand("crucible_dialogue_choose", DialogueChooseHandler, new List<string> { "selector" });

            if (_dumpRegistered && _clickRegistered && _clickNthRegistered && _matchesRegistered && _focusRegistered && _navRegistered && _submitRegistered && _cancelRegistered
                && _pressRegistered && _whereRegistered && _dialogueAdvanceRegistered && _dialogueChooseRegistered)
            {
                if (_log != null) _log.LogInfo("UiCommands registered (crucible_ui_dump/click/click_nth/matches/focus/nav/submit/cancel/press/where, crucible_dialogue_advance/choose).");
            }
            else if (!_loggedWaiting)
            {
                _loggedWaiting = true;
                if (_log != null) _log.LogInfo("UiCommands registration incomplete; will retry each tick until the game's command registry is ready.");
            }
        }

        // ============================================================== crucible_ui_dump

        /// <summary>
        /// crucible_ui_dump &lt;filter&gt; &lt;kinds&gt; — snapshot the visible UI. "filter" is the
        /// existing name/text substring filter ("-" for everything); "kinds" is a comma-separated
        /// type filter (e.g. "Button,Label", "-" for every type) applied on top of it, so a busy
        /// screen full of noise (e.g. repeated TemplateContainers) can be narrowed to just the kinds
        /// worth reading.
        /// </summary>
        public static void CrucibleUiDump(string filter, string kinds)
        {
            LastResult = null;
            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();

                List<UiElementInfo> elements;
                int inactiveDocumentSkipCount;
                UiTreeWalker.Stats walkStats;
                string error;
                if (!WalkAllElements(out elements, out inactiveDocumentSkipCount, out walkStats, out error))
                {
                    LastResult = "error: " + error;
                    return;
                }

                int matched, skipped;
                bool truncated;
                OnScreenTest.SkipReasonCounts skipReasons;
                string rendered = UiTreeRenderer.Render(elements, filter, kinds, UiTreeRenderer.DefaultCap, out matched, out skipped, out truncated, out skipReasons);

                stopwatch.Stop();

                // Pruned subtrees never became individual UiElementInfo entries (SPEC S3 UI dump
                // perf — see WalkAllDocuments), so their counts are folded in here alongside the
                // pre-existing inactiveDocumentSkipCount rollup rather than coming from Render.
                StringBuilder sb = new StringBuilder();
                sb.Append(rendered);
                sb.Append("\n(matchedVisible=").Append(matched)
                    .Append(" skippedInvisible=").Append(skipped + inactiveDocumentSkipCount + walkStats.PrunedCounts.Total())
                    .Append(" truncated=").Append(truncated)
                    .Append(" budgetTruncated=").Append(walkStats.BudgetTruncated).Append(")");
                sb.Append("\nskipReasons: inactiveDocument=").Append(inactiveDocumentSkipCount + skipReasons.InactiveDocument + walkStats.PrunedCounts.InactiveDocument)
                    .Append(" hiddenAncestor=").Append(skipReasons.HiddenAncestor + walkStats.PrunedCounts.HiddenAncestor)
                    .Append(" displayNone=").Append(skipReasons.DisplayNone + walkStats.PrunedCounts.DisplayNone)
                    .Append(" zeroOpacity=").Append(skipReasons.ZeroOpacity + walkStats.PrunedCounts.ZeroOpacity)
                    .Append(" zeroSize=").Append(skipReasons.ZeroSize + walkStats.PrunedCounts.ZeroSize)
                    .Append(" unresolved=").Append(skipReasons.Unresolved + walkStats.PrunedCounts.Unresolved);
                sb.Append("\nperf: visited=").Append(walkStats.ElementsVisited)
                    .Append(" prunedSubtrees=").Append(walkStats.SubtreesPruned)
                    .Append(" ms=").Append(stopwatch.ElapsedMilliseconds);
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

                // Refuse forbidden elements BEFORE any activation strategy runs. crucible_ui_press
                // gets this via UiPressMatcher, but crucible_ui_click goes through
                // UiSelectorMatcher, which has no such check — so a selector like "Continue"
                // substring-matched 'continue-btn' and resumed the owner's live co-op campaign.
                // The Node layer's guard (mcp/server.js ftk2_pick) does not cover this path,
                // because ftk2_exec reaches the command directly.
                string forbiddenReason;
                if (UiForbiddenElements.IsForbidden(match.Match.Name, match.Match.DocumentName, out forbiddenReason))
                {
                    LastResult = "refused: " + forbiddenReason
                        + "\nmatched: " + UiTreeRenderer.FormatLine(match.Match)
                        + "\nNothing was activated.";
                    return;
                }

                int matchedIndex = buttonInfos.IndexOf(match.Match);
                object targetButton = (matchedIndex >= 0 && matchedIndex < buttonRefs.Count) ? buttonRefs[matchedIndex] : null;
                if (targetButton == null)
                {
                    LastResult = "error: internal: matched element had no live reference";
                    return;
                }

                StringBuilder result = new StringBuilder();
                result.Append("matched: ").Append(UiTreeRenderer.FormatLine(match.Match));
                result.Append("\ncount=").Append(1 + match.OtherMatches.Count);
                if (match.OtherMatches.Count > 0)
                {
                    result.Append(" others:");
                    foreach (UiElementInfo o in match.OtherMatches)
                        result.Append("\n  - ").Append(UiTreeRenderer.FormatLine(o));
                }

                RunActivationStrategyLadder(targetButton, result);

                LastResult = result.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_ui_click threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_ui_click failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The shared activation ladder behind crucible_ui_click and crucible_ui_click_nth: try each
        /// strategy in order, stop at the first success, but report every attempt made (up to and
        /// including the one that succeeded) so a failed click is fully diagnosable from this one
        /// call. Appends its report to <paramref name="result"/>.
        ///
        /// Order matters, and it is deliberate: the first two strategies report whether the element
        /// ACTED, while the last two only report that an event was DISPATCHED. Synthesising a
        /// NavigationSubmitEvent "succeeds" whether or not any handler is listening, so running it
        /// earlier masked a real failure -- a story dialogue reported invoked=True through
        /// focus+submit and did not advance, which stalled quest resolution and stopped the adventure
        /// from ever ending.
        /// </summary>
        private static bool RunActivationStrategyLadder(object targetButton, StringBuilder result)
        {
            bool succeeded = false;
            string succeededVia = null;

            string s1Error;
            bool s1 = InvokeButtonClick(targetButton, out s1Error);
            result.Append("\nstrategy1[clickable.clicked]: ").Append(s1 ? "SUCCEEDED" : ("failed: " + s1Error));
            if (s1) { succeeded = true; succeededVia = "clickable.clicked/clickedWithEventInfo"; }

            if (!succeeded)
            {
                string s4Detail;
                bool s4 = TryGameSubmit(targetButton, out s4Detail);
                result.Append("\nstrategy2[UIToolkitHelper.Submit]: ").Append(s4Detail);
                if (s4) { succeeded = true; succeededVia = "UIToolkitHelper.Submit"; }
            }

            if (!succeeded)
            {
                string s2Detail;
                bool s2 = TryFocusThenSubmit(targetButton, out s2Detail);
                result.Append("\nstrategy3[focus+submit]: ").Append(s2Detail);
                if (s2) { succeeded = true; succeededVia = "Focus()+NavigationSubmitEvent"; }
            }

            if (!succeeded)
            {
                string s3Detail;
                bool s3 = TryPointerSequence(targetButton, out s3Detail);
                result.Append("\nstrategy4[pointer]: ").Append(s3Detail);
                if (s3) { succeeded = true; succeededVia = "PointerDownEvent+PointerUpEvent+ClickEvent"; }
            }

            result.Append("\ninvoked=").Append(succeeded);
            if (succeeded) result.Append(" via=").Append(succeededVia);
            else result.Append(" (all strategies failed; see detail above)");

            return succeeded;
        }

        /// <summary>
        /// The game's OWN activation path: UIToolkitHelper.Submit(element, pickFilter,
        /// pIgnoreDisableCheck, pIsMouseAction).
        ///
        /// This exists because the first three strategies all reported success on a story dialogue
        /// and advanced nothing (measured 2026-08-24). Dialogue pages are wired with
        /// RegisterSingleSubmit and advanced by DialogueViewHelper._onSubmit, which calls exactly
        /// this method with a pick filter that returns false -- it deliberately ignores whether
        /// something else is painted on top. Synthesising a NavigationSubmitEvent is NOT
        /// equivalent: an orphaned LoadingUIDocument left over from a load sits above the dialogue
        /// and swallows the ordinary path, and that is precisely the state a quest-resolution
        /// dialogue is reached in.
        ///
        /// pIgnoreDisableCheck is true for the same reason: the harness has to activate elements
        /// the game has disabled for input while it waits on a Task.
        /// </summary>
        private static bool TryGameSubmit(object element, out string detail)
        {
            detail = null;
            try
            {
                Type helperType = AccessTools.TypeByName("UIToolkitHelper");
                if (helperType == null) { detail = "failed: UIToolkitHelper not found"; return false; }

                MethodInfo submit = null;
                foreach (MethodInfo m in helperType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!string.Equals(m.Name, "Submit", StringComparison.Ordinal)) continue;
                    if (m.GetParameters().Length == 4) { submit = m; break; }
                }
                if (submit == null) { detail = "failed: UIToolkitHelper.Submit(4 args) not found"; return false; }

                object returned = submit.Invoke(null, new object[] { element, null, true, false });
                bool ok = returned is bool && (bool)returned;
                detail = ok ? "SUCCEEDED (returned true)" : "failed: Submit returned false";
                return ok;
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex; while (root.InnerException != null) root = root.InnerException;
                detail = "failed: threw " + root.GetType().Name + ": " + root.Message;
                return false;
            }
            catch (Exception ex)
            {
                detail = "failed: " + ex.Message;
                return false;
            }
        }

        // ============================================================== crucible_ui_click_nth

        /// <summary>
        /// crucible_ui_click_nth &lt;selector&gt; &lt;index&gt; &lt;document&gt; - clicks the element at
        /// the given ZERO-BASED index among the on-screen Buttons matching selector, optionally
        /// scoped to one owning UIDocument ("-" for any).
        ///
        /// Exists because <c>crucible_ui_click</c> is first-match-wins, which cannot address one of
        /// several same-named, text-less elements: the nine <c>adventure-art-holder</c> Buttons on
        /// the adventure-selection carousel, and the empty-text <c>next-btn</c> difficulty stepper
        /// that shares a screen with a CONFIRM button also named <c>next-btn</c>.
        ///
        /// SAFETY: runs the SAME <see cref="UiForbiddenElements"/> check as
        /// <c>crucible_ui_click</c>/<c>crucible_ui_press</c>, before any activation strategy runs
        /// (see <see cref="UiIndexedMatcher"/>). An out-of-range index is a loud refusal and NEVER
        /// falls back to index 0. The match report - name, document, index, total match count and
        /// text - is emitted BEFORE anything is activated, so a caller verifies identity instead of
        /// hoping.
        /// </summary>
        public static void CrucibleUiClickNth(string selector, string index, string document)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(selector))
                {
                    LastResult = "error: usage: crucible_ui_click_nth <selector> <index> <document|->";
                    return;
                }

                int parsedIndex;
                string parseError;
                if (!UiIndexedMatcher.TryParseIndex(index, out parsedIndex, out parseError))
                {
                    LastResult = "error: " + parseError;
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

                UiIndexedMatcher.Result match = UiIndexedMatcher.Find(buttonInfos, selector, document, parsedIndex, out error);
                if (error != null)
                {
                    LastResult = "error: " + error;
                    return;
                }

                StringBuilder result = new StringBuilder();

                if (!match.Found)
                {
                    if (match.OutOfRange)
                    {
                        // Loud. A silent fallback to index 0 here would click the wrong element while
                        // reporting success - the exact silent-success trap this verb exists to close.
                        result.Append("error: index ").Append(parsedIndex)
                            .Append(" is out of range: '").Append(selector).Append("' matches ")
                            .Append(match.MatchCount).Append(" on-screen button(s)")
                            .Append(DescribeDocumentScope(document))
                            .Append(", valid indices 0..").Append(match.MatchCount - 1)
                            .Append(". Nothing was activated (no fallback to index 0).");
                        AppendMatchList(result, match.Matches);
                    }
                    else
                    {
                        result.Append("no on-screen button matches '").Append(selector).Append("'")
                            .Append(DescribeDocumentScope(document)).Append(". On-screen buttons in scope:");
                        if (match.AllVisible.Count == 0)
                        {
                            result.Append(" (none)");
                        }
                        else
                        {
                            foreach (UiElementInfo b in match.AllVisible)
                                result.Append("\n  - ").Append(UiTreeRenderer.FormatLine(b));
                        }
                    }
                    LastResult = result.ToString();
                    return;
                }

                // Report exactly what was matched BEFORE acting.
                AppendMatchReport(result, match, selector, document);

                if (match.Forbidden)
                {
                    result.Append("\nREFUSED: ").Append(match.ForbiddenReason).Append("\nNothing was activated.");
                    LastResult = result.ToString();
                    return;
                }

                int walkIndex = buttonInfos.IndexOf(match.Match);
                object targetButton = (walkIndex >= 0 && walkIndex < buttonRefs.Count) ? buttonRefs[walkIndex] : null;
                if (targetButton == null)
                {
                    result.Append("\nerror: internal: matched element had no live reference");
                    LastResult = result.ToString();
                    return;
                }

                RunActivationStrategyLadder(targetButton, result);

                LastResult = result.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_ui_click_nth threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_ui_click_nth failed: " + ex.Message);
            }
        }

        // ============================================================== crucible_ui_matches

        /// <summary>
        /// crucible_ui_matches &lt;selector&gt; &lt;document&gt; - READ-ONLY. Lists every on-screen
        /// Button matching selector with the zero-based index <c>crucible_ui_click_nth</c> takes,
        /// its owning document and its text, plus whether each one is forbidden. Activates nothing.
        ///
        /// <c>crucible_ui_dump</c> shows the same elements but numbers nothing, so a caller had to
        /// count lines by hand and hope the index space matched; this shares the one matcher with
        /// the click verb, so the printed index IS the index to pass.
        /// </summary>
        public static void CrucibleUiMatches(string selector, string document)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(selector))
                {
                    LastResult = "error: usage: crucible_ui_matches <selector> <document|->";
                    return;
                }

                List<UiElementInfo> buttonInfos;
                List<object> buttonRefsUnused;
                string error;
                if (!WalkButtons(out buttonInfos, out buttonRefsUnused, out error))
                {
                    LastResult = "error: " + error;
                    return;
                }

                // index 0 only satisfies Find's contract; nothing is activated and Found is not read.
                UiIndexedMatcher.Result match = UiIndexedMatcher.Find(buttonInfos, selector, document, 0, out error);
                if (error != null)
                {
                    LastResult = "error: " + error;
                    return;
                }

                StringBuilder result = new StringBuilder();
                result.Append("selector='").Append(selector).Append("'")
                    .Append(DescribeDocumentScope(document))
                    .Append(" count=").Append(match.MatchCount);
                if (match.MatchCount == 0)
                {
                    result.Append("\n(no on-screen button matches). On-screen buttons in scope:");
                    if (match.AllVisible.Count == 0) result.Append(" (none)");
                    foreach (UiElementInfo b in match.AllVisible)
                        result.Append("\n  - ").Append(UiTreeRenderer.FormatLine(b));
                }
                else
                {
                    AppendMatchList(result, match.Matches);
                }
                LastResult = result.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_ui_matches threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_ui_matches failed: " + ex.Message);
            }
        }

        /// <summary>The identity report emitted BEFORE any activation: what was matched, where, and how many alternatives existed.</summary>
        private static void AppendMatchReport(StringBuilder result, UiIndexedMatcher.Result match, string selector, string document)
        {
            result.Append("matched: ").Append(UiTreeRenderer.FormatLine(match.Match));
            result.Append("\nselector='").Append(selector).Append("'")
                .Append(DescribeDocumentScope(document))
                .Append(" index=").Append(match.Index)
                .Append(" count=").Append(match.MatchCount)
                .Append(" name=").Append(match.Match.Name == null ? "(null)" : "'" + match.Match.Name + "'")
                .Append(" doc=").Append(match.Match.DocumentName == null ? "(null)" : "'" + match.Match.DocumentName + "'")
                .Append(" text=").Append(match.Match.Text == null ? "(null)" : "'" + match.Match.Text + "'");
            AppendMatchList(result, match.Matches);
        }

        /// <summary>Numbered listing of every match, so the index space is visible in the same result that used it.</summary>
        private static void AppendMatchList(StringBuilder result, List<UiElementInfo> matches)
        {
            result.Append("\nmatches:");
            if (matches == null || matches.Count == 0)
            {
                result.Append(" (none)");
                return;
            }
            for (int i = 0; i < matches.Count; i++)
            {
                UiElementInfo e = matches[i];
                result.Append("\n  [").Append(i).Append("] ").Append(UiTreeRenderer.FormatLine(e));
                string forbiddenReason;
                if (UiForbiddenElements.IsForbidden(e.Name, e.DocumentName, out forbiddenReason))
                    result.Append("  <-- FORBIDDEN: ").Append(forbiddenReason);
            }
        }

        /// <summary>Renders the document scope for a report; "(any)" when unscoped.</summary>
        private static string DescribeDocumentScope(string document)
        {
            if (string.IsNullOrEmpty(document) || document == UiIndexedMatcher.AnyDocument) return " doc=(any)";
            return " doc='" + document + "'";
        }

        // ============================================================== crucible_ui_focus

        /// <summary>crucible_ui_focus &lt;selector&gt; — finds the first visible element whose name/text contains selector and calls Focusable.Focus() on it, reporting focus before/after.</summary>
        public static void CrucibleUiFocus(string selector)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(selector))
                {
                    LastResult = "error: usage: crucible_ui_focus <selector>";
                    return;
                }

                List<UiElementInfo> infos;
                List<object> refs;
                string error;
                if (!WalkAllElementsWithRefs(out infos, out refs, out error))
                {
                    LastResult = "error: " + error;
                    return;
                }

                UiSelectorMatcher.Result match = UiSelectorMatcher.Find(infos, selector, out error);
                if (error != null)
                {
                    LastResult = "error: " + error;
                    return;
                }

                if (!match.Found)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("no visible element matches '").Append(selector).Append("'. Visible elements:");
                    if (match.AllVisible.Count == 0)
                    {
                        sb.Append(" (none)");
                    }
                    else
                    {
                        foreach (UiElementInfo e in match.AllVisible)
                            sb.Append("\n  - ").Append(UiTreeRenderer.FormatLine(e));
                    }
                    LastResult = sb.ToString();
                    return;
                }

                int matchedIndex = infos.IndexOf(match.Match);
                object target = (matchedIndex >= 0 && matchedIndex < refs.Count) ? refs[matchedIndex] : null;
                if (target == null)
                {
                    LastResult = "error: internal: matched element had no live reference";
                    return;
                }

                object focusedBefore = GetFocusedElementFor(target);

                MethodInfo focusMethod = AccessTools.Method(target.GetType(), "Focus");
                bool focusInvoked = false;
                string focusError = null;
                if (focusMethod == null)
                {
                    focusError = "Focus() method not found on " + target.GetType().FullName;
                }
                else
                {
                    try { focusMethod.Invoke(target, null); focusInvoked = true; }
                    catch (TargetInvocationException ex) { focusError = "Focus() threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message); }
                    catch (Exception ex) { focusError = "Focus() threw: " + ex.Message; }
                }

                object focusedAfter = GetFocusedElementFor(target);
                bool focusMoved = focusedAfter != null && ReferenceEquals(focusedAfter, target);

                StringBuilder result = new StringBuilder();
                result.Append("matched: ").Append(UiTreeRenderer.FormatLine(match.Match));
                result.Append("\ncount=").Append(1 + match.OtherMatches.Count);
                if (match.OtherMatches.Count > 0)
                {
                    result.Append(" others:");
                    foreach (UiElementInfo o in match.OtherMatches)
                        result.Append("\n  - ").Append(UiTreeRenderer.FormatLine(o));
                }
                result.Append("\nfocusBefore=").Append(DescribeElement(focusedBefore));
                result.Append("\nfocusInvoked=").Append(focusInvoked);
                if (!focusInvoked) result.Append(" error=").Append(focusError);
                result.Append("\nfocusAfter=").Append(DescribeElement(focusedAfter));
                result.Append("\nfocusMoved=").Append(focusMoved);

                LastResult = result.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_ui_focus threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_ui_focus failed: " + ex.Message);
            }
        }

        // ============================================================== crucible_ui_press

        /// <summary>
        /// crucible_ui_press &lt;selector&gt; — the whole "press this thing" loop in one call:
        /// match (exact-name-beats-substring via <see cref="UiPressMatcher"/>, all matches reported,
        /// forbidden names refused outright), Focus() with a verified-landed check, an activation
        /// ladder (NavigationSubmitEvent, then a virtual gamepad A press — see
        /// <see cref="TryActivate"/>), and a screen-delta report so a silent no-op is never mistaken
        /// for a working press. Supersedes hand-chaining crucible_ui_click + crucible_pad a +
        /// crucible_ui_dump.
        /// </summary>
        public static void CrucibleUiPress(string selector)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(selector))
                {
                    LastResult = "error: usage: crucible_ui_press <selector>";
                    return;
                }

                List<UiElementInfo> infos;
                List<object> refs;
                string error;
                if (!WalkAllElementsWithRefs(out infos, out refs, out error))
                {
                    LastResult = "error: " + error;
                    return;
                }

                UiPressMatcher.Result match = UiPressMatcher.Find(infos, selector, out error);
                if (error != null)
                {
                    LastResult = "error: " + error;
                    return;
                }

                StringBuilder result = new StringBuilder();

                if (!match.Found)
                {
                    result.Append("no on-screen element matches '").Append(selector).Append("'. On-screen elements:");
                    if (match.AllVisible.Count == 0)
                    {
                        result.Append(" (none)");
                    }
                    else
                    {
                        foreach (UiElementInfo e in match.AllVisible)
                            result.Append("\n  - ").Append(UiTreeRenderer.FormatLine(e));
                    }
                    LastResult = result.ToString();
                    return;
                }

                result.Append("matched: ").Append(UiTreeRenderer.FormatLine(match.Match));
                result.Append("\ncount=").Append(1 + match.OtherMatches.Count);
                if (match.OtherMatches.Count > 0)
                {
                    result.Append(" others:");
                    foreach (UiElementInfo o in match.OtherMatches)
                        result.Append("\n  - ").Append(UiTreeRenderer.FormatLine(o));
                }

                if (match.Forbidden)
                {
                    result.Append("\nREFUSED: ").Append(match.ForbiddenReason);
                    LastResult = result.ToString();
                    return;
                }

                int matchedIndex = infos.IndexOf(match.Match);
                object target = (matchedIndex >= 0 && matchedIndex < refs.Count) ? refs[matchedIndex] : null;
                if (target == null)
                {
                    result.Append("\nerror: internal: matched element had no live reference");
                    LastResult = result.ToString();
                    return;
                }

                object focusBefore = GetFocusedElementFor(target);
                result.Append("\nfocusBefore=").Append(DescribeElement(focusBefore));

                bool focusMoved;
                string focusDetail;
                TryFocus(target, out focusMoved, out focusDetail);
                result.Append("\n").Append(focusDetail);

                object focusAfterFocusCall = GetFocusedElementFor(target);
                result.Append("\nfocusMoved=").Append(focusMoved);

                ScreenSnapshot before;
                string snapError;
                if (!CaptureScreenSnapshot(out before, out snapError))
                {
                    result.Append("\nerror capturing before-snapshot: ").Append(snapError);
                    LastResult = result.ToString();
                    return;
                }

                string activationLog;
                bool changed;
                string succeededVia;
                ScreenSnapshot after;
                TryActivate(target, before, out activationLog, out changed, out succeededVia, out after);
                result.Append("\nactivation: ").Append(activationLog);
                result.Append("\nactivationSucceeded=").Append(changed);
                if (succeededVia != null) result.Append(" via=").Append(succeededVia);

                object focusAfter = GetFocusedElementFor(target);
                result.Append("\nfocusAfter=").Append(DescribeElement(focusAfter));

                bool deltaChanged;
                string delta = DescribeScreenDelta(before, after, out deltaChanged);
                if (!deltaChanged)
                {
                    result.Append("\nno observable change");
                }
                else
                {
                    result.Append("\n").Append(delta);
                }

                LastResult = result.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_ui_press threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_ui_press failed: " + ex.Message);
            }
        }

        // ============================================================== crucible_ui_where

        /// <summary>
        /// crucible_ui_where &lt;_unused&gt; — a zero-argument read: what is focused right now, which
        /// documents are on screen, and the on-screen buttons. No filters, no dump cap — the constant
        /// "where am I" check that shouldn't require a full crucible_ui_dump.
        /// </summary>
        public static void CrucibleUiWhere(string _unused)
        {
            LastResult = null;
            try
            {
                List<DocRoot> docs;
                string error;
                if (!FindDocumentRootsWithMeta(out docs, out error))
                {
                    LastResult = "error: " + error;
                    return;
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("documents:");
                bool anyDoc = false;
                object focused = null;
                foreach (DocRoot d in docs)
                {
                    if (d.Active != true) continue;
                    anyDoc = true;
                    sb.Append("\n  - ").Append(d.Name == null ? "(unnamed)" : d.Name);
                    if (focused == null) focused = GetFocusedElementFor(d.Root);
                }
                if (!anyDoc) sb.Append(" (none active)");

                sb.Append("\nfocused=").Append(DescribeElement(focused));

                List<UiElementInfo> buttonInfos;
                List<object> buttonRefsUnused;
                string buttonError;
                if (WalkButtons(out buttonInfos, out buttonRefsUnused, out buttonError))
                {
                    sb.Append("\nbuttons:");
                    bool anyBtn = false;
                    foreach (UiElementInfo b in buttonInfos)
                    {
                        if (!b.OnScreen) continue;
                        anyBtn = true;
                        sb.Append("\n  - ").Append(UiTreeRenderer.FormatLine(b));
                    }
                    if (!anyBtn) sb.Append(" (none)");
                }
                else
                {
                    sb.Append("\nbuttons: error: ").Append(buttonError);
                }

                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_ui_where threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_ui_where failed: " + ex.Message);
            }
        }

        // ============================================================== crucible_dialogue_advance

        /// <summary>
        /// crucible_dialogue_advance &lt;maxPresses&gt; — clears a whole conversation in one call
        /// instead of one press per round trip. Loop: detect a continue prompt
        /// (<see cref="DialoguePromptDetector"/>), press it via the same activation ladder as
        /// crucible_ui_press, stop when no prompt remains, the cap is hit, or a press produces no
        /// observable change (a stuck prompt must surface as stuck, not burn the whole budget).
        /// </summary>
        public static void CrucibleDialogueAdvance(string maxPressesRaw)
        {
            LastResult = null;
            try
            {
                int maxPresses;
                bool clamped;
                string parseError;
                if (!DialogueAdvanceBudget.TryParse(maxPressesRaw, out maxPresses, out clamped, out parseError))
                {
                    LastResult = "error: " + parseError;
                    return;
                }

                StringBuilder log = new StringBuilder();
                List<string> linesEncountered = new List<string>();
                int presses = 0;
                string stopReason = null;

                for (int i = 0; i < maxPresses; i++)
                {
                    List<UiElementInfo> infos;
                    List<object> refs;
                    string error;
                    if (!WalkAllElementsWithRefs(out infos, out refs, out error))
                    {
                        stopReason = "error: " + error;
                        break;
                    }

                    UiElementInfo prompt;
                    if (!DialoguePromptDetector.TryFind(infos, out prompt))
                    {
                        stopReason = "no prompt remains";
                        break;
                    }

                    if (!string.IsNullOrEmpty(prompt.Text)) linesEncountered.Add(prompt.Text);

                    int idx = infos.IndexOf(prompt);
                    object target = (idx >= 0 && idx < refs.Count) ? refs[idx] : null;
                    if (target == null)
                    {
                        stopReason = "internal: matched prompt had no live reference";
                        break;
                    }

                    // A live check against this ladder proved that the activation ladder's own
                    // "sent"/"changed" verdict is NOT a reliable advance signal: repeated presses on
                    // the wrong target reported submit=SUCCEEDED with the dialogue never moving, and
                    // dialogue-container's own text can change while the same Button/document set
                    // stays on screen (missed by a buttons-only or docs-only delta). So this loop
                    // independently diffs the FULL on-screen element set (name+text+doc, every kind,
                    // not just buttons) captured from the very walk that found the prompt, before vs.
                    // after the press — that catches a dialogue-line text change even when no
                    // document/button appeared or disappeared.
                    HashSet<string> linesBefore = FormatOnScreenLines(infos);

                    ScreenSnapshot before;
                    string snapError;
                    if (!CaptureScreenSnapshot(out before, out snapError))
                    {
                        stopReason = "error capturing snapshot: " + snapError;
                        break;
                    }

                    string activationLog;
                    bool ladderChanged;
                    string succeededVia;
                    ScreenSnapshot after;
                    bool focusMovedUnused;
                    string focusDetailUnused;
                    TryFocus(target, out focusMovedUnused, out focusDetailUnused);
                    TryActivate(target, before, out activationLog, out ladderChanged, out succeededVia, out after);
                    presses++;

                    List<UiElementInfo> infosAfter;
                    List<object> refsAfterUnused;
                    string afterWalkError;
                    bool advanced = false;
                    if (WalkAllElementsWithRefs(out infosAfter, out refsAfterUnused, out afterWalkError))
                    {
                        HashSet<string> linesAfter = FormatOnScreenLines(infosAfter);
                        advanced = !linesBefore.SetEquals(linesAfter);
                    }

                    log.Append("\npress ").Append(presses).Append(": ").Append(UiTreeRenderer.FormatLine(prompt))
                        .Append(" -> ").Append(activationLog)
                        .Append(" | dialogueTextChanged=").Append(advanced);

                    if (!advanced)
                    {
                        stopReason = "no observable change after press " + presses + " (dialogue text/elements identical before and after; stopping rather than burning the budget)";
                        break;
                    }

                    System.Threading.Thread.Sleep(50);
                }

                if (stopReason == null) stopReason = "cap hit (" + maxPresses + ")";

                StringBuilder result = new StringBuilder();
                result.Append("presses=").Append(presses);
                result.Append(" stopReason=").Append(stopReason);
                if (clamped) result.Append(" (maxPresses clamped from '").Append(maxPressesRaw).Append("' to ").Append(DialogueAdvanceBudget.MaxPresses).Append(")");
                result.Append("\nlines encountered:");
                if (linesEncountered.Count == 0)
                {
                    result.Append(" (none)");
                }
                else
                {
                    foreach (string line in linesEncountered) result.Append("\n  - ").Append(line);
                }
                result.Append(log.ToString());

                LastResult = result.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_dialogue_advance threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_dialogue_advance failed: " + ex.Message);
            }
        }

        /// <summary>Every on-screen element (any kind) rendered as a FormatLine, as a set — used by crucible_dialogue_advance to diff actual dialogue-text/element changes rather than trust the activation ladder's own success report.</summary>
        private static HashSet<string> FormatOnScreenLines(List<UiElementInfo> infos)
        {
            HashSet<string> lines = new HashSet<string>(StringComparer.Ordinal);
            if (infos == null) return lines;
            foreach (UiElementInfo e in infos)
            {
                if (e != null && e.OnScreen) lines.Add(UiTreeRenderer.FormatLine(e));
            }
            return lines;
        }

        // ============================================================== crucible_dialogue_choose

        /// <summary>
        /// crucible_dialogue_choose &lt;selector|-&gt; — enumerates on-screen dialogue OPTIONS
        /// (<see cref="DialogueOptions"/>) and activates the one matching selector via
        /// <see cref="UiPressMatcher"/> (exact-name-beats-substring, forbidden refused). Passing "-"
        /// lists the options and presses nothing — a failed choose is diagnosable in one call, and
        /// enumeration doesn't require guessing a selector first.
        /// </summary>
        public static void CrucibleDialogueChoose(string selector)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(selector))
                {
                    LastResult = "error: usage: crucible_dialogue_choose <selector|->";
                    return;
                }

                List<UiElementInfo> infos;
                List<object> refs;
                string error;
                if (!WalkAllElementsWithRefs(out infos, out refs, out error))
                {
                    LastResult = "error: " + error;
                    return;
                }

                List<UiElementInfo> options = DialogueOptions.ListOptions(infos);

                StringBuilder result = new StringBuilder();
                result.Append("options:");
                if (options.Count == 0)
                {
                    result.Append(" (none)");
                }
                else
                {
                    foreach (UiElementInfo o in options) result.Append("\n  - ").Append(UiTreeRenderer.FormatLine(o));
                }

                if (selector == "-")
                {
                    LastResult = result.ToString();
                    return;
                }

                UiPressMatcher.Result match = UiPressMatcher.Find(options, selector, out error);
                if (error != null)
                {
                    LastResult = "error: " + error;
                    return;
                }

                if (!match.Found)
                {
                    result.Append("\nno option matches '").Append(selector).Append("'");
                    LastResult = result.ToString();
                    return;
                }

                result.Append("\nmatched: ").Append(UiTreeRenderer.FormatLine(match.Match));
                result.Append(" count=").Append(1 + match.OtherMatches.Count);
                if (match.OtherMatches.Count > 0)
                {
                    result.Append(" others:");
                    foreach (UiElementInfo o in match.OtherMatches) result.Append("\n  - ").Append(UiTreeRenderer.FormatLine(o));
                }

                if (match.Forbidden)
                {
                    result.Append("\nREFUSED: ").Append(match.ForbiddenReason);
                    LastResult = result.ToString();
                    return;
                }

                int idx = infos.IndexOf(match.Match);
                object target = (idx >= 0 && idx < refs.Count) ? refs[idx] : null;
                if (target == null)
                {
                    result.Append("\nerror: internal: matched element had no live reference");
                    LastResult = result.ToString();
                    return;
                }

                bool focusMoved;
                string focusDetail;
                TryFocus(target, out focusMoved, out focusDetail);
                result.Append("\n").Append(focusDetail);
                result.Append(" focusMoved=").Append(focusMoved);

                ScreenSnapshot before;
                string snapError;
                if (!CaptureScreenSnapshot(out before, out snapError))
                {
                    result.Append("\nerror capturing before-snapshot: ").Append(snapError);
                    LastResult = result.ToString();
                    return;
                }

                string activationLog;
                bool changed;
                string succeededVia;
                ScreenSnapshot after;
                TryActivate(target, before, out activationLog, out changed, out succeededVia, out after);
                result.Append("\nactivation: ").Append(activationLog);
                result.Append("\nactivationSucceeded=").Append(changed);
                if (succeededVia != null) result.Append(" via=").Append(succeededVia);

                LastResult = result.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_dialogue_choose threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_dialogue_choose failed: " + ex.Message);
            }
        }

        // ============================================================== shared: focus + activation ladder + screen delta

        /// <summary>Focus()es <paramref name="target"/> and reports whether focus actually landed on it — shared by crucible_ui_press/crucible_dialogue_choose/crucible_dialogue_advance.</summary>
        private static void TryFocus(object target, out bool focusMoved, out string detail)
        {
            StringBuilder sb = new StringBuilder();

            MethodInfo focusMethod = AccessTools.Method(target.GetType(), "Focus");
            bool focusInvoked = false;
            string focusError = null;
            if (focusMethod == null)
            {
                focusError = "Focus() method not found on " + target.GetType().FullName;
            }
            else
            {
                try { focusMethod.Invoke(target, null); focusInvoked = true; }
                catch (TargetInvocationException ex) { focusError = "Focus() threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message); }
                catch (Exception ex) { focusError = "Focus() threw: " + ex.Message; }
            }

            object focusedAfter = GetFocusedElementFor(target);
            focusMoved = focusedAfter != null && ReferenceEquals(focusedAfter, target);

            sb.Append("focusInvoked=").Append(focusInvoked);
            if (!focusInvoked) sb.Append(" error=").Append(focusError);
            detail = sb.ToString();
        }

        /// <summary>Cheap snapshot of what's on screen for before/after delta comparison: the set of active documents, and the on-screen buttons rendered as lines.</summary>
        private struct ScreenSnapshot
        {
            public HashSet<string> ActiveDocNames;
            public List<string> ButtonLines;
        }

        private static bool CaptureScreenSnapshot(out ScreenSnapshot snapshot, out string error)
        {
            snapshot = new ScreenSnapshot
            {
                ActiveDocNames = new HashSet<string>(StringComparer.Ordinal),
                ButtonLines = new List<string>()
            };

            List<DocRoot> docs;
            if (!FindDocumentRootsWithMeta(out docs, out error)) return false;
            foreach (DocRoot d in docs)
            {
                if (d.Active == true) snapshot.ActiveDocNames.Add(d.Name == null ? "(unnamed)" : d.Name);
            }

            List<UiElementInfo> buttonInfos;
            List<object> refsUnused;
            if (!WalkButtons(out buttonInfos, out refsUnused, out error)) return false;
            foreach (UiElementInfo b in buttonInfos)
            {
                if (b.OnScreen) snapshot.ButtonLines.Add(UiTreeRenderer.FormatLine(b));
            }

            return true;
        }

        private static bool ScreenChanged(ScreenSnapshot before, ScreenSnapshot after)
        {
            if (!before.ActiveDocNames.SetEquals(after.ActiveDocNames)) return true;
            if (before.ButtonLines.Count != after.ButtonLines.Count) return true;
            HashSet<string> beforeSet = new HashSet<string>(before.ButtonLines, StringComparer.Ordinal);
            HashSet<string> afterSet = new HashSet<string>(after.ButtonLines, StringComparer.Ordinal);
            return !beforeSet.SetEquals(afterSet);
        }

        /// <summary>Documents that appeared/disappeared plus the on-screen buttons after, for the crucible_ui_press screen-delta report.</summary>
        private static string DescribeScreenDelta(ScreenSnapshot before, ScreenSnapshot after, out bool changed)
        {
            List<string> appeared = new List<string>();
            foreach (string d in after.ActiveDocNames) if (!before.ActiveDocNames.Contains(d)) appeared.Add(d);
            List<string> disappeared = new List<string>();
            foreach (string d in before.ActiveDocNames) if (!after.ActiveDocNames.Contains(d)) disappeared.Add(d);

            changed = ScreenChanged(before, after);

            StringBuilder sb = new StringBuilder();
            sb.Append("documentsAppeared=").Append(appeared.Count == 0 ? "(none)" : string.Join(", ", appeared.ToArray()));
            sb.Append(" documentsDisappeared=").Append(disappeared.Count == 0 ? "(none)" : string.Join(", ", disappeared.ToArray()));
            sb.Append("\non-screen buttons after:");
            if (after.ButtonLines.Count == 0)
            {
                sb.Append(" (none)");
            }
            else
            {
                foreach (string b in after.ButtonLines) sb.Append("\n  - ").Append(b);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Activation ladder: send a UIToolkit NavigationSubmitEvent to <paramref name="target"/>
        /// (works for many buttons); if that produces no observable change vs. <paramref name="before"/>,
        /// fall back to a virtual gamepad A ("South") tap (needed for party slots, dialogue prompts,
        /// "Click to Continue" — <see cref="GamepadCommands.TryTap"/>). Reports which was tried and
        /// which — if either — actually changed the screen. <paramref name="finalSnapshot"/> is the
        /// last snapshot taken, reused by callers as the "after" state instead of walking again.
        /// </summary>
        private static void TryActivate(object target, ScreenSnapshot before, out string activationLog, out bool changed, out string succeededVia, out ScreenSnapshot finalSnapshot)
        {
            StringBuilder sb = new StringBuilder();
            succeededVia = null;

            object evt;
            string buildError;
            bool submitBuilt = BuildSimpleNavigationEvent("UnityEngine.UIElements.NavigationSubmitEvent", out evt, out buildError);
            bool submitSent = false;
            string submitSendError = null;
            if (submitBuilt) submitSent = SendEvent(target, evt, out submitSendError);
            else submitSendError = buildError;
            sb.Append("submit[NavigationSubmitEvent]: ").Append(submitSent ? "sent" : ("failed: " + submitSendError));

            ScreenSnapshot afterSubmit;
            string snapError1;
            bool haveAfterSubmit = CaptureScreenSnapshot(out afterSubmit, out snapError1);
            finalSnapshot = haveAfterSubmit ? afterSubmit : before;

            if (submitSent && haveAfterSubmit && ScreenChanged(before, afterSubmit))
            {
                changed = true;
                succeededVia = "NavigationSubmitEvent";
                sb.Append(" -> screen changed");
                activationLog = sb.ToString();
                return;
            }

            sb.Append(" -> no observable change yet, trying gamepad A");

            string padDetail;
            GamepadCommands.TryTap("South", out padDetail);
            sb.Append(" | pad[A/South]: ").Append(padDetail);

            ScreenSnapshot afterPad;
            string snapError2;
            bool haveAfterPad = CaptureScreenSnapshot(out afterPad, out snapError2);
            if (haveAfterPad) finalSnapshot = afterPad;

            if (haveAfterPad && ScreenChanged(before, afterPad))
            {
                changed = true;
                succeededVia = "gamepad A (South)";
                sb.Append(" -> screen changed");
            }
            else
            {
                changed = false;
                sb.Append(" -> no observable change");
            }

            activationLog = sb.ToString();
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
        //
        // SPEC S3 UI true-visibility: an element only counts as on screen if its owning UIDocument
        // is active+enabled AND the element and every ancestor up to the root are visible, not
        // display:none, and non-zero opacity AND the element's own worldBound has nonzero size.
        // VisualElement.visible/enabledInHierarchy are LOCAL properties that miss all of that — see
        // OnScreenTest.IsOnScreen (Crucible.Core) for the pure decision this feeds.

        /// <summary>One live UIDocument this frame: its root element, resolved active state (null = could not resolve, fails closed), and GameObject name.</summary>
        private struct DocRoot
        {
            public readonly object Root;
            public readonly bool? Active;
            public readonly string Name;
            public DocRoot(object root, bool? active, string name) { Root = root; Active = active; Name = name; }
        }

        /// <summary>
        /// Hard cap on elements visited by one crucible_ui_dump (or any crucible_ui_* walk) —
        /// SPEC S3 UI dump perf. A dump must never silently blow the 10s main-thread budget: past
        /// this many elements the walk stops and reports <see cref="UiTreeWalker.Stats.BudgetTruncated"/>
        /// rather than risk a timeout that returns no data at all.
        /// </summary>
        private const int MaxElementBudget = 50000;

        private static bool WalkAllElements(out List<UiElementInfo> elements, out int inactiveDocumentSkipCount, out UiTreeWalker.Stats stats, out string error)
        {
            List<object> refsUnused;
            return WalkAllDocuments(null, false, out elements, out refsUnused, out inactiveDocumentSkipCount, out stats, out error);
        }

        private static bool WalkButtons(out List<UiElementInfo> infos, out List<object> refs, out string error)
        {
            Type buttonType = AccessTools.TypeByName("UnityEngine.UIElements.Button");
            if (buttonType == null)
            {
                infos = new List<UiElementInfo>();
                refs = new List<object>();
                error = "UnityEngine.UIElements.Button type not found";
                return false;
            }

            int inactiveDocumentSkipCount;
            UiTreeWalker.Stats statsUnused;
            return WalkAllDocuments(buttonType, true, out infos, out refs, out inactiveDocumentSkipCount, out statsUnused, out error);
        }

        /// <summary>Like <see cref="WalkAllElements"/> but also keeps the live element reference alongside each <see cref="UiElementInfo"/> — needed by crucible_ui_focus, which (unlike crucible_ui_click) must be able to target any element kind, not just Button.</summary>
        private static bool WalkAllElementsWithRefs(out List<UiElementInfo> infos, out List<object> refs, out string error)
        {
            int inactiveDocumentSkipCount;
            UiTreeWalker.Stats statsUnused;
            return WalkAllDocuments(null, true, out infos, out refs, out inactiveDocumentSkipCount, out statsUnused, out error);
        }

        /// <summary>
        /// Shared walk driving every crucible_ui_* command: resolves every live UIDocument, checks
        /// each document's active+enabled state ONCE, and skips a whole inactive document's subtree
        /// wholesale (just a cheap Children()-only count, no per-node style/opacity reflection) —
        /// this is where the vast majority of a busy screen's off-screen elements come from
        /// (combat HUD, shop, quest log, inventory panels kept alive but inactive behind the current
        /// screen). Active documents are walked via <see cref="UiTreeWalker"/> (Crucible.Core, pure)
        /// bound to the reflective accessors below: it prunes at the root of any display:none/
        /// hidden/zero-opacity subtree (the OTHER big source of off-screen elements on a busy
        /// screen — see OnScreenTest.TryGetSubtreePruneReason) instead of reflecting into every
        /// descendant, and enforces <see cref="MaxElementBudget"/> across the whole dump.
        /// </summary>
        private static bool WalkAllDocuments(Type matchType, bool collectRefs, out List<UiElementInfo> infos, out List<object> refs, out int inactiveDocumentSkipCount, out UiTreeWalker.Stats stats, out string error)
        {
            List<UiElementInfo> collectedInfos = new List<UiElementInfo>();
            List<object> collectedRefs = collectRefs ? new List<object>() : null;
            infos = collectedInfos;
            refs = collectedRefs;
            inactiveDocumentSkipCount = 0;
            stats = new UiTreeWalker.Stats();
            error = null;

            List<DocRoot> docs;
            if (!FindDocumentRootsWithMeta(out docs, out error)) return false;

            foreach (DocRoot doc in docs)
            {
                if (stats.BudgetTruncated) break;

                if (doc.Active == false)
                {
                    inactiveDocumentSkipCount += CountSubtree(doc.Root);
                    continue;
                }

                int remainingBudget = MaxElementBudget - stats.ElementsVisited;
                if (remainingBudget <= 0) { stats.BudgetTruncated = true; break; }

                object docFocused = GetFocusedElementFor(doc.Root);
                DocRoot capturedDoc = doc;
                Action<object, bool, OnScreenTest.SkipReason> visit = delegate(object ve, bool onScreen, OnScreenTest.SkipReason reason)
                {
                    if (matchType == null || matchType.IsInstanceOfType(ve))
                    {
                        collectedInfos.Add(DescribeAsInfo(ve, ve.GetType(), docFocused, capturedDoc.Name, onScreen, reason));
                        if (collectedRefs != null) collectedRefs.Add(ve);
                    }
                };

                UiTreeWalker.Stats docStats = UiTreeWalker.Walk<object>(
                    doc.Root, doc.Active, remainingBudget,
                    BuildAncestorFrame, ReadSize, GetChildrenGeneric, CountSubtree, visit);

                stats.ElementsVisited += docStats.ElementsVisited;
                stats.SubtreesPruned += docStats.SubtreesPruned;
                if (docStats.BudgetTruncated) stats.BudgetTruncated = true;
                stats.PrunedCounts.Merge(docStats.PrunedCounts);
            }
            return true;
        }

        private static void ReadSize(object ve, out double? width, out double? height)
        {
            TryGetWorldBoundSize(ve, out width, out height);
        }

        private static IEnumerable<object> GetChildrenGeneric(object ve)
        {
            IEnumerable children = GetChildren(ve, ve.GetType());
            if (children == null) yield break;
            foreach (object child in children) yield return child;
        }

        /// <summary>Every live UIDocument's rootVisualElement, active state, and name this frame.</summary>
        private static bool FindDocumentRootsWithMeta(out List<DocRoot> docs, out string error)
        {
            docs = new List<DocRoot>();
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

            IEnumerable rawDocs = docsObj as IEnumerable;
            if (rawDocs == null) { error = "FindObjectsOfType returned no enumerable result"; return false; }

            PropertyInfo rootProp = AccessTools.Property(uiDocumentType, "rootVisualElement");
            if (rootProp == null) { error = "UIDocument.rootVisualElement not found"; return false; }

            foreach (object doc in rawDocs)
            {
                if (doc == null) continue;
                object root;
                try { root = rootProp.GetValue(doc, null); }
                catch (Exception) { continue; }
                if (root == null) continue;

                bool? active = ReadDocumentActive(doc);
                string name = ReadDocumentName(doc);
                docs.Add(new DocRoot(root, active, name));
            }

            return true;
        }

        /// <summary>UIDocument.enabled (Behaviour) AND its GameObject's activeInHierarchy. Null (unresolved) fails closed via OnScreenTest, never defaults to true.</summary>
        private static bool? ReadDocumentActive(object doc)
        {
            bool? behaviourEnabled = ReadBoolPropNullable(doc, "enabled");
            if (behaviourEnabled == null) return null;
            if (behaviourEnabled == false) return false;

            object gameObject = ReadProp(doc, "gameObject");
            if (gameObject == null) return null;

            bool? activeInHierarchy = ReadBoolPropNullable(gameObject, "activeInHierarchy");
            if (activeInHierarchy == null) return null;

            return activeInHierarchy == true;
        }

        private static string ReadDocumentName(object doc)
        {
            object gameObject = ReadProp(doc, "gameObject");
            return gameObject == null ? null : ReadStringProp(gameObject, "name");
        }

        /// <summary>Cheap count of a subtree via Children() only — used to tally an inactive document's skipped elements without doing full per-node style/opacity reflection.</summary>
        private static int CountSubtree(object ve)
        {
            if (ve == null) return 0;
            int count = 1;
            Type t = ve.GetType();
            IEnumerable children = GetChildren(ve, t);
            if (children != null)
            {
                foreach (object child in children) count += CountSubtree(child);
            }
            return count;
        }

        private static UiElementInfo DescribeAsInfo(object ve, Type t, object focusedElement, string docName, bool onScreen, OnScreenTest.SkipReason reason)
        {
            string name = ReadStringProp(ve, "name");
            string text = ReadStringProp(ve, "text");
            bool visible = ReadBoolProp(ve, "visible", true);
            bool enabled = ReadBoolProp(ve, "enabledInHierarchy", true);
            bool focused = focusedElement != null && ReferenceEquals(ve, focusedElement);
            return new UiElementInfo(t.Name, name, text, visible, enabled, focused, onScreen, reason, docName);
        }

        // ============================================================== on-screen decision inputs

        private static Type _displayStyleType;
        private static object _displayStyleNoneValue;
        private static bool _displayStyleResolveAttempted;
        private static string _displayStyleResolveError;

        /// <summary>
        /// Resolves UnityEngine.UIElements.DisplayStyle and its "None" member reflectively, ONCE
        /// (cached). If either can't be resolved (game update moved/renamed the enum), every
        /// element's display:none check fails closed via OnScreenTest.SkipReason.Unresolved — this
        /// logs that explicitly rather than silently skipping the check.
        /// </summary>
        private static bool TryGetDisplayStyleNone(out object noneValue, out string error)
        {
            if (!_displayStyleResolveAttempted)
            {
                _displayStyleResolveAttempted = true;
                _displayStyleType = AccessTools.TypeByName("UnityEngine.UIElements.DisplayStyle");
                if (_displayStyleType == null)
                {
                    _displayStyleResolveError = "UnityEngine.UIElements.DisplayStyle type not found";
                }
                else
                {
                    try { _displayStyleNoneValue = Enum.Parse(_displayStyleType, "None"); }
                    catch (Exception ex)
                    {
                        _displayStyleResolveError = "DisplayStyle.None could not be resolved reflectively: " + ex.Message;
                        _displayStyleNoneValue = null;
                    }
                }

                if (_displayStyleResolveError != null && _log != null)
                {
                    _log.LogWarning("crucible_ui: " + _displayStyleResolveError
                        + " — display:none can never be confirmed, so every element fails the on-screen check closed (Unresolved) until this is fixed.");
                }
            }

            noneValue = _displayStyleNoneValue;
            error = _displayStyleResolveError;
            return noneValue != null;
        }

        /// <summary>
        /// Reads a property that VisualElement implements as an EXPLICIT interface member.
        ///
        /// resolvedStyle returns the element itself typed as IResolvedStyle, and display/opacity are
        /// explicit implementations — so obj.GetType().GetProperty("display") finds nothing and every
        /// element reports Unresolved. Verified against the game's own
        /// UnityEngine.UIElementsModule.dll on 2026-08-23: VisualElement has no public "display" or
        /// "opacity" property, and its interface map points at
        /// UnityEngine.UIElements.IResolvedStyle.get_display. The value is therefore reachable only
        /// through the interface type's property.
        /// </summary>
        private static readonly Dictionary<string, PropertyInfo> _ifacePropCache =
            new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

        private static object ReadInterfaceProp(object instance, string interfaceName, string propName)
        {
            if (instance == null) return null;
            try
            {
                // Resolved once per (interface, property) and cached: a UI dump walks tens of
                // thousands of elements, and doing TypeByName + GetProperty per element costs more
                // than the whole rest of the walk — enough to blow the main-thread budget.
                string key = interfaceName + "." + propName;
                PropertyInfo prop;
                if (!_ifacePropCache.TryGetValue(key, out prop))
                {
                    Type iface = AccessTools.TypeByName("UnityEngine.UIElements." + interfaceName);
                    prop = iface == null
                        ? null
                        : iface.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                    _ifacePropCache[key] = prop;
                }
                if (prop == null) return null;
                return prop.GetValue(instance, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static OnScreenTest.AncestorFrame BuildAncestorFrame(object ve)
        {
            bool? visible = ReadBoolPropNullable(ve, "visible");
            bool? displayNone = ReadDisplayNone(ve);
            double? opacity = ReadOpacity(ve);
            return new OnScreenTest.AncestorFrame(visible, displayNone, opacity);
        }

        private static bool? ReadDisplayNone(object ve)
        {
            object resolvedStyle = ReadProp(ve, "resolvedStyle");
            if (resolvedStyle == null) return null;

            object displayValue = ReadInterfaceProp(resolvedStyle, "IResolvedStyle", "display");
            if (displayValue == null) return null;

            object noneValue;
            string error;
            if (!TryGetDisplayStyleNone(out noneValue, out error)) return null;

            return displayValue.Equals(noneValue);
        }

        private static double? ReadOpacity(object ve)
        {
            object resolvedStyle = ReadProp(ve, "resolvedStyle");
            if (resolvedStyle == null) return null;

            object opacityValue = ReadInterfaceProp(resolvedStyle, "IResolvedStyle", "opacity");
            if (opacityValue is float) return (double)(float)opacityValue;
            if (opacityValue is double) return (double)opacityValue;
            return null;
        }

        private static void TryGetWorldBoundSize(object ve, out double? width, out double? height)
        {
            width = null;
            height = null;

            object rect = ReadProp(ve, "worldBound");
            if (rect == null) return;

            object w = ReadProp(rect, "width");
            object h = ReadProp(rect, "height");
            if (w is float) width = (double)(float)w;
            else if (w is double) width = (double)w;
            if (h is float) height = (double)(float)h;
            else if (h is double) height = (double)h;
        }

        /// <summary>Children() MethodInfo cached per concrete VisualElement type, ONCE — a dump walks tens of thousands of elements, and re-resolving it per element (like <see cref="_ifacePropCache"/> next to it) costs more than the rest of the walk.</summary>
        private static readonly Dictionary<Type, MethodInfo> _childrenMethodCache = new Dictionary<Type, MethodInfo>();

        private static IEnumerable GetChildren(object ve, Type t)
        {
            try
            {
                MethodInfo childrenMethod;
                if (!_childrenMethodCache.TryGetValue(t, out childrenMethod))
                {
                    childrenMethod = AccessTools.Method(t, "Children");
                    _childrenMethodCache[t] = childrenMethod;
                }
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

            List<DocRoot> docs;
            if (!FindDocumentRootsWithMeta(out docs, out error)) return false;
            if (docs.Count == 0) { error = "no live UIDocument with a root element"; return false; }

            root = docs[0].Root;
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

        // ============================================================== click strategy 2: focus + submit

        /// <summary>
        /// Strategy 2 of crucible_ui_click's ladder: Focus() the target, verify focus actually moved
        /// by re-reading focusController.focusedElement, then SendEvent a NavigationSubmitEvent to it
        /// — the same event a controller's "A"/keyboard Enter would produce on a focused button. Only
        /// attempts the submit if focus is confirmed to have moved onto the target (a submit sent to
        /// something else would be meaningless and misleading to report as "succeeded").
        /// </summary>
        private static bool TryFocusThenSubmit(object target, out string detail)
        {
            StringBuilder sb = new StringBuilder();

            object focusedBefore = GetFocusedElementFor(target);
            sb.Append("focusBefore=").Append(DescribeElement(focusedBefore));

            MethodInfo focusMethod = AccessTools.Method(target.GetType(), "Focus");
            if (focusMethod == null)
            {
                sb.Append(" focus=FAILED(Focus() method not found on ").Append(target.GetType().FullName).Append(")");
                detail = sb.ToString();
                return false;
            }

            try
            {
                focusMethod.Invoke(target, null);
            }
            catch (TargetInvocationException ex)
            {
                sb.Append(" focus=FAILED(Focus() threw: ").Append(ex.InnerException != null ? ex.InnerException.Message : ex.Message).Append(")");
                detail = sb.ToString();
                return false;
            }
            catch (Exception ex)
            {
                sb.Append(" focus=FAILED(Focus() threw: ").Append(ex.Message).Append(")");
                detail = sb.ToString();
                return false;
            }

            object focusedAfter = GetFocusedElementFor(target);
            sb.Append(" focusAfter=").Append(DescribeElement(focusedAfter));
            bool focusMoved = focusedAfter != null && ReferenceEquals(focusedAfter, target);
            sb.Append(" focusMoved=").Append(focusMoved);

            if (!focusMoved)
            {
                sb.Append(" -> submit not attempted (focus did not move onto the target)");
                detail = sb.ToString();
                return false;
            }

            object evt;
            string buildError;
            if (!BuildSimpleNavigationEvent("UnityEngine.UIElements.NavigationSubmitEvent", out evt, out buildError))
            {
                sb.Append(" submit=FAILED(build: ").Append(buildError).Append(")");
                detail = sb.ToString();
                return false;
            }

            string sendError;
            bool sent = SendEvent(target, evt, out sendError);
            sb.Append(" submit=").Append(sent ? "SUCCEEDED" : ("FAILED(" + sendError + ")"));
            detail = sb.ToString();
            return sent;
        }

        // ============================================================== click strategy 3: synthetic pointer sequence

        /// <summary>
        /// Strategy 3 of crucible_ui_click's ladder: build and send a PointerDownEvent, then a
        /// PointerUpEvent, then a ClickEvent built from that PointerUpEvent — the same sequence a
        /// real mouse click produces, since UIToolkit's own Clickable manipulator (which Button uses
        /// internally) listens for pointer-down/up, and ClickEvent is what a &lt;ClickEvent&gt;
        /// callback registered via RegisterCallback would receive.
        ///
        /// PointerDownEvent/PointerUpEvent don't declare their own parameterless GetPooled directly
        /// (per docs/research/crucible-ui-driving.md §4, this was ASSUMED-not-confirmed at doc time);
        /// resolved here reflectively via FlattenHierarchy against EventBase&lt;T&gt;'s base
        /// GetPooled() — the same pattern UiCommands already uses for NavigationSubmitEvent/
        /// NavigationCancelEvent. If that lookup fails, this strategy is reported as skipped with the
        /// exact reason rather than silently doing nothing.
        /// </summary>
        private static bool TryPointerSequence(object target, out string detail)
        {
            StringBuilder sb = new StringBuilder();

            Type downType = AccessTools.TypeByName("UnityEngine.UIElements.PointerDownEvent");
            Type upType = AccessTools.TypeByName("UnityEngine.UIElements.PointerUpEvent");
            if (downType == null || upType == null)
            {
                detail = "PointerDownEvent/PointerUpEvent types not found";
                return false;
            }

            object downEvt;
            string downBuildError;
            bool downBuilt = BuildParameterlessEvent(downType, out downEvt, out downBuildError);
            sb.Append("PointerDownEvent: ").Append(downBuilt ? "built" : ("build FAILED(" + downBuildError + ")"));
            bool downSent = false;
            if (downBuilt)
            {
                string sendError;
                downSent = SendEvent(target, downEvt, out sendError);
                sb.Append(downSent ? ", sent" : (", send FAILED(" + sendError + ")"));
            }

            object upEvt;
            string upBuildError;
            bool upBuilt = BuildParameterlessEvent(upType, out upEvt, out upBuildError);
            sb.Append(" | PointerUpEvent: ").Append(upBuilt ? "built" : ("build FAILED(" + upBuildError + ")"));
            bool upSent = false;
            if (upBuilt)
            {
                string sendError;
                upSent = SendEvent(target, upEvt, out sendError);
                sb.Append(upSent ? ", sent" : (", send FAILED(" + sendError + ")"));
            }

            bool clickSent = false;
            if (upBuilt)
            {
                object clickEvt;
                string clickBuildError;
                bool clickBuilt = BuildClickEvent(upEvt, out clickEvt, out clickBuildError);
                sb.Append(" | ClickEvent: ").Append(clickBuilt ? "built" : ("build FAILED(" + clickBuildError + ")"));
                if (clickBuilt)
                {
                    string sendError;
                    clickSent = SendEvent(target, clickEvt, out sendError);
                    sb.Append(clickSent ? ", sent" : (", send FAILED(" + sendError + ")"));
                }
            }
            else
            {
                sb.Append(" | ClickEvent: skipped (needs a built PointerUpEvent)");
            }

            detail = sb.ToString();
            return downSent && upSent && clickSent;
        }

        /// <summary>
        /// Resolves and invokes a zero-argument GetPooled() on <paramref name="eventType"/> —
        /// declared on the shared EventBase&lt;T&gt; base every UIToolkit event ultimately derives
        /// from, found via FlattenHierarchy the same way BuildSimpleNavigationEvent resolves
        /// NavigationSubmitEvent/NavigationCancelEvent's inherited GetPooled(EventModifiers).
        /// </summary>
        private static bool BuildParameterlessEvent(Type eventType, out object evt, out string error)
        {
            evt = null;
            error = null;
            try
            {
                MethodInfo getPooled = eventType.GetMethod(
                    "GetPooled",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy,
                    null,
                    Type.EmptyTypes,
                    null);
                if (getPooled == null) { error = eventType.Name + ".GetPooled() (parameterless) not found (checked inherited members)"; return false; }

                evt = getPooled.Invoke(null, null);
                if (evt == null) { error = "GetPooled() returned null"; return false; }
                return true;
            }
            catch (TargetInvocationException ex)
            {
                error = "build threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = "build threw: " + ex.Message;
                return false;
            }
        }

        /// <summary>ClickEvent's only confirmed factory: GetPooled(PointerUpEvent, Int32 clickCount) (docs/research/crucible-ui-driving.md §4 Option A).</summary>
        private static bool BuildClickEvent(object pointerUpEvent, out object evt, out string error)
        {
            evt = null;
            error = null;
            try
            {
                Type clickEventType = AccessTools.TypeByName("UnityEngine.UIElements.ClickEvent");
                if (clickEventType == null) { error = "UnityEngine.UIElements.ClickEvent type not found"; return false; }

                Type pointerUpType = AccessTools.TypeByName("UnityEngine.UIElements.PointerUpEvent");
                if (pointerUpType == null) { error = "UnityEngine.UIElements.PointerUpEvent type not found"; return false; }

                MethodInfo getPooled = AccessTools.Method(clickEventType, "GetPooled", new[] { pointerUpType, typeof(int) });
                if (getPooled == null) { error = "ClickEvent.GetPooled(PointerUpEvent, Int32) not found"; return false; }

                evt = getPooled.Invoke(null, new object[] { pointerUpEvent, 1 });
                if (evt == null) { error = "GetPooled returned null"; return false; }
                return true;
            }
            catch (TargetInvocationException ex)
            {
                error = "build threw: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = "build threw: " + ex.Message;
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

        /// <summary>
        /// PropertyInfo cache for <see cref="ReadProp"/>, keyed by concrete type + property name —
        /// covers every property this file reads reflectively off a live element (visible,
        /// resolvedStyle, worldBound, enabledInHierarchy, name, text, gameObject, ...). Same reason
        /// as <see cref="_ifacePropCache"/>/<see cref="_childrenMethodCache"/>: resolved once, not
        /// once per element across a 24k-element walk.
        /// </summary>
        private static readonly Dictionary<string, PropertyInfo> _typePropCache =
            new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

        private static object ReadProp(object obj, string propName)
        {
            if (obj == null) return null;
            try
            {
                Type t = obj.GetType();
                string key = t.FullName + "." + propName;
                PropertyInfo p;
                if (!_typePropCache.TryGetValue(key, out p))
                {
                    p = AccessTools.Property(t, propName);
                    _typePropCache[key] = p;
                }
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

        /// <summary>Unlike <see cref="ReadBoolProp"/>, returns null (unresolved) instead of a default when the property is missing or not a bool — callers that feed OnScreenTest must be able to fail closed rather than silently assume true.</summary>
        private static bool? ReadBoolPropNullable(object obj, string propName)
        {
            object v = ReadProp(obj, propName);
            return v is bool ? (bool?)v : null;
        }
    }
}

using System;

namespace FTK2Mods.Crucible.Tests
{
    internal static class InputDisableReasonPickerTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("InputDisableReasonPicker");

            TestHarness.Run("picks the real LOST_FOCUS member out of the full live enum", delegate
            {
                // Verified live 2026-08-23 via TypeProbe/SigProbe over the retail FTK2.dll:
                // InputController.eDisableRequest's real member list.
                string[] members = {
                    "ROUTE_CHANGE", "LOST_FOCUS", "ENCYCLOPEDIA_TOPIC_CHANGE", "SETTINGS_RESOLUTION_CHANGE",
                    "UI_TRANSITION_WAIT", "SETTINGS_MENU_SHOW", "REST_PHASE_DELAY", "CHOICE_MENU_DELAY",
                    "BUG_REPORT_DELAY", "MP_PROMPT_DELAY", "ADVENTURE_END_TURN", "RANDOMIZE_CHARACTER",
                    "STEAM_OVERLAY", "TEXT_EDITING", "STEAM_FLOATING_KEYBOARD", "CONTEXT_MENU_NAV_SET",
                    "ENCYCLOPEDIA_SEARCH", "DIALOG_DISMISS", "PLAYER_KICK_REPORT", "BLOCK_LIST_VIEW_RENDER",
                    "BLOCK_LIST_VIEW_PROFILE", "SYSTEM_DIALOG_TRANSITION", "PLEASE_WAIT_SPINNER",
                    "GAMESAVE_DELETE_FILE", "GAMESAVE_LIST_RENDER", "LEADERBOARD_SHOW_DELAY",
                    "CUSTOMIZATION_DELAY", "ADVENTURE_SUMMARY_RENDER", "CONNECTION_CHECK", "LOAD_CHAR_PRESET"
                };
                string picked; string error;
                bool ok = InputDisableReasonPicker.TryPickLostFocusMember(members, out picked, out error);
                TestHarness.True(ok, "found");
                TestHarness.Equal("LOST_FOCUS", picked, "picked member");
            });

            // Negative control: the other reasons observed live (ROUTE_CHANGE, SYSTEM_DIALOG_TRANSITION)
            // must NOT be matched — only LOST_FOCUS should ever be suppressed. If the heuristic were
            // wrong (too broad), this would catch it.
            TestHarness.Run("does not match ROUTE_CHANGE or SYSTEM_DIALOG_TRANSITION when LOST_FOCUS is absent", delegate
            {
                string[] members = { "ROUTE_CHANGE", "SYSTEM_DIALOG_TRANSITION", "STEAM_FLOATING_KEYBOARD" };
                string picked; string error;
                bool ok = InputDisableReasonPicker.TryPickLostFocusMember(members, out picked, out error);
                TestHarness.True(!ok, "refused");
                TestHarness.True(picked == null, "no guess");
                TestHarness.True(error.IndexOf("ROUTE_CHANGE", StringComparison.Ordinal) >= 0, "names actual member 1");
                TestHarness.True(error.IndexOf("SYSTEM_DIALOG_TRANSITION", StringComparison.Ordinal) >= 0, "names actual member 2");
            });

            TestHarness.Run("finds a renamed member by heuristic, not exact string", delegate
            {
                string[] members = { "RouteChange", "WindowLostFocusEvent" };
                string picked; string error;
                bool ok = InputDisableReasonPicker.TryPickLostFocusMember(members, out picked, out error);
                TestHarness.True(ok, "found");
                TestHarness.Equal("WindowLostFocusEvent", picked, "picked the renamed member");
            });

            TestHarness.Run("empty member list is a clean error, not a crash", delegate
            {
                string picked; string error;
                bool ok = InputDisableReasonPicker.TryPickLostFocusMember(new string[0], out picked, out error);
                TestHarness.True(!ok, "refused");
                TestHarness.True(error != null, "has a message");
            });
        }
    }
}

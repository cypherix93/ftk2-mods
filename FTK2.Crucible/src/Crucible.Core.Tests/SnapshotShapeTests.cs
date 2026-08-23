using System.Collections.Generic;

namespace FTK2Mods.Crucible.Tests
{
    internal static class SnapshotShapeTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("SnapshotShape — shape");

            TestHarness.Run("schema is crucible.state.v2", delegate
            {
                Dictionary<string, object> snap = Build(Combat());
                TestHarness.Equal("crucible.state.v2", snap["schema"] as string, "schema");
            });

            // Negative control: v2 must not be mistakable for v1.
            TestHarness.Run("schema is not the v1 string", delegate
            {
                Dictionary<string, object> snap = Build(Combat());
                TestHarness.NotEqual("crucible.state.v1", snap["schema"] as string, "distinct");
            });

            TestHarness.Run("synthesized marker names turn and phase", delegate
            {
                List<object> marker = (List<object>)Combatless(Build(Combat()))["synthesized"];
                TestHarness.Equal(2, marker.Count, "two synthesized fields");
                TestHarness.Equal("turn", marker[0] as string, "turn");
                TestHarness.Equal("phase", marker[1] as string, "phase");
            });

            TestHarness.Run("statuses are sorted by id", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].StatusesAvailable = true;
                c.Combatants[0].Statuses.Add(Status("STATUS_ATTACKUP_01", 3));
                c.Combatants[0].Statuses.Add(Status("STATUS_ATTACKUP_00", 2));
                c.Combatants[0].Statuses.Add(Status("STATUS_CF_ENCMOD_CURSED", 1));

                List<object> statuses = (List<object>)FirstCombatant(Build(c))["statuses"];
                TestHarness.Equal("STATUS_ATTACKUP_00", Str(statuses[0], "id"), "0");
                TestHarness.Equal("STATUS_ATTACKUP_01", Str(statuses[1], "id"), "1");
                TestHarness.Equal("STATUS_CF_ENCMOD_CURSED", Str(statuses[2], "id"), "2");
            });

            TestHarness.Run("a combatant with no statuses serializes an empty array", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].StatusesAvailable = true;
                List<object> statuses = (List<object>)FirstCombatant(Build(c))["statuses"];
                TestHarness.Equal(0, statuses.Count, "empty, not null");
            });

            // Negative control for rule 1: unreadable must NOT look like empty.
            TestHarness.Run("unreadable statuses serialize as null, not an empty array", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].StatusesAvailable = false;
                TestHarness.True(FirstCombatant(Build(c))["statuses"] == null, "null");
            });

            TestHarness.Run("available stats serialize as an object", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].StatsAvailable = true;
                c.Combatants[0].Stats["STR"] = 4;
                Dictionary<string, object> stats =
                    (Dictionary<string, object>)FirstCombatant(Build(c))["stats"];
                TestHarness.Equal(4, (int)stats["STR"], "value");
            });

            TestHarness.Run("unavailable stats serialize as null, not an empty object", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].StatsAvailable = false;
                TestHarness.True(FirstCombatant(Build(c))["stats"] == null, "null");
            });

            TestHarness.Run("unreadable combatants serialize as null, not an empty array", delegate
            {
                CombatView c = Combat();
                c.CombatantsAvailable = false;
                TestHarness.True(Combatless(Build(c))["combatants"] == null, "null");
            });

            TestHarness.Run("a null combat view still yields an inactive combat object", delegate
            {
                Dictionary<string, object> combat = Combatless(Build(null));
                TestHarness.Equal(false, (bool)combat["active"], "inactive");
                TestHarness.True(combat["combatants"] == null, "no combatants");
            });

            TestHarness.Run("warnings are carried into the snapshot", delegate
            {
                WarningSink w = new WarningSink();
                w.MemberMissing("CharacterComponent", "DisplayName");
                Dictionary<string, object> snap = SnapshotShape.BuildV2(
                    "p1", "COMBAT", Run(), Network(), Combat(), false, w);
                List<object> warnings = (List<object>)snap["warnings"];
                TestHarness.Equal(1, warnings.Count, "one warning");
                TestHarness.Equal("member_missing: CharacterComponent.DisplayName",
                    warnings[0] as string, "text");
            });

            TestHarness.Run("the v2 snapshot round-trips through MiniJson", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].StatusesAvailable = true;
                c.Combatants[0].Statuses.Add(Status("STATUS_ATTACKUP_00", 2));
                c.Combatants[0].StatsAvailable = true;
                c.Combatants[0].Stats["STR"] = 4;

                object parsed; string error;
                TestHarness.True(MiniJson.TryParse(MiniJson.Write(Build(c)), out parsed, out error),
                    "parses: " + error);
                TestHarness.True(MiniJson.AsObject(parsed) != null, "object at the root");
            });

            TestHarness.Section("SnapshotShape — v2 digest");

            TestHarness.Run("entity ids do not affect the v2 digest", delegate
            {
                CombatView a = Combat();
                CombatView b = Combat();
                b.Combatants[0].Id = "a-totally-different-guid";
                b.ActiveEntityId = "a-totally-different-guid";
                b.Episode = "deadbeef";
                TestHarness.Equal(Digest(a), Digest(b), "runtime ids redacted");
            });

            // Negative control: real state changes MUST move the digest.
            TestHarness.Run("an hp change does affect the v2 digest", delegate
            {
                CombatView a = Combat();
                CombatView b = Combat();
                b.Combatants[0].Hp = 12;
                TestHarness.NotEqual(Digest(a), Digest(b), "hp is in the digest");
            });

            TestHarness.Run("warnings do not affect the v2 digest", delegate
            {
                WarningSink noisy = new WarningSink();
                noisy.MemberMissing("CharacterComponent", "DisplayName");
                string quiet = StateDigest.Compute(
                    SnapshotShape.BuildV2("p1", "COMBAT", Run(), Network(), Combat(), false, new WarningSink()),
                    Redactions.V2);
                string loud = StateDigest.Compute(
                    SnapshotShape.BuildV2("p2", "COMBAT", Run(), Network(), Combat(), false, noisy),
                    Redactions.V2);
                TestHarness.Equal(quiet, loud, "warnings and instance redacted");
            });

            TestHarness.Run("status order does not affect the v2 digest", delegate
            {
                CombatView a = Combat();
                a.Combatants[0].StatusesAvailable = true;
                a.Combatants[0].Statuses.Add(Status("STATUS_ATTACKUP_00", 2));
                a.Combatants[0].Statuses.Add(Status("STATUS_CF_ENCMOD_CURSED", 1));

                CombatView b = Combat();
                b.Combatants[0].StatusesAvailable = true;
                b.Combatants[0].Statuses.Add(Status("STATUS_CF_ENCMOD_CURSED", 1));
                b.Combatants[0].Statuses.Add(Status("STATUS_ATTACKUP_00", 2));

                TestHarness.Equal(Digest(a), Digest(b), "sorted before hashing");
            });

            // Negative control for the sort: a different status set must still differ.
            TestHarness.Run("a different status set does affect the v2 digest", delegate
            {
                CombatView a = Combat();
                a.Combatants[0].StatusesAvailable = true;
                a.Combatants[0].Statuses.Add(Status("STATUS_ATTACKUP_00", 2));

                CombatView b = Combat();
                b.Combatants[0].StatusesAvailable = true;
                b.Combatants[0].Statuses.Add(Status("STATUS_ATTACKUP_01", 2));

                TestHarness.NotEqual(Digest(a), Digest(b), "different ids, different digest");
            });
        }

        // ---- builders ----

        private static Dictionary<string, object> Build(CombatView combat)
        {
            return SnapshotShape.BuildV2("p1", "COMBAT", Run(), Network(), combat, false, new WarningSink());
        }

        private static string Digest(CombatView combat)
        {
            return StateDigest.Compute(Build(combat), Redactions.V2);
        }

        private static RunView Run()
        {
            RunView r = new RunView();
            r.Present = true;
            r.Seed = 12345;
            r.Day = 3;
            r.Gold = 120;
            r.Chapter = "CHAPTER_1";
            return r;
        }

        private static NetworkView Network()
        {
            NetworkView n = new NetworkView();
            n.Online = false;
            n.IsHost = true;
            n.PlayerCount = 1;
            return n;
        }

        private static CombatView Combat()
        {
            CombatantView c = new CombatantView();
            c.Id = "e-1";
            c.Name = "Bard";
            c.ClassId = "CF_EOR_BARD";
            c.IsPlayer = true;
            c.Hp = 34;
            c.MaxHp = 40;
            c.Alive = true;

            CombatView v = new CombatView();
            v.Active = true;
            v.Round = 2;
            v.Wave = 0;
            v.Turn = 1;
            v.Phase = TurnTracker.PhasePlayer;
            v.ActiveEntityId = "e-1";
            v.Episode = "0000002a";
            v.CombatantsAvailable = true;
            v.Combatants.Add(c);
            return v;
        }

        private static StatusView Status(string id, int duration)
        {
            StatusView s = new StatusView();
            s.Id = id;
            s.Duration = duration;
            s.InitialDuration = duration;
            s.TickDuration = 0;
            s.OriginEntityId = "e-9";
            return s;
        }

        // ---- accessors ----

        private static Dictionary<string, object> Combatless(Dictionary<string, object> snap)
        {
            return (Dictionary<string, object>)snap["combat"];
        }

        private static Dictionary<string, object> FirstCombatant(Dictionary<string, object> snap)
        {
            List<object> combatants = (List<object>)Combatless(snap)["combatants"];
            return (Dictionary<string, object>)combatants[0];
        }

        private static string Str(object node, string key)
        {
            return ((Dictionary<string, object>)node)[key] as string;
        }
    }
}

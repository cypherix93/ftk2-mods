using System.Collections.Generic;

namespace FTK2Mods.Crucible.Tests
{
    // ---- fakes standing in for the game's board types (no game, no Unity) ----
    //
    // Shaped to match the decompile exactly, because these tests are asserting that the reader's
    // reflection idiom works against THESE shapes:
    //   VenueComponent.TilePosition  -> public (int x, int y)      (VenueComponent.cs:5)
    //   VenueTileComponent           -> GroupIndex / RowPositionsType / AuraStatuses
    //   CharacterComponent.Properties-> List<eActorProperties>     (CharacterComponent.cs:56)

    internal enum FakeActorProperties { NONE, SUMMON, COMPANION }

    internal sealed class FakeVenueComponent
    {
        public (int x, int y) TilePosition;
        public List<(int x, int y)> OccupiedTiles;
    }

    internal sealed class FakeVenueTileComponent
    {
        public int GroupIndex;
        public string RowPositionsType = "FRONT";
        public List<string> AuraStatuses;
    }

    internal sealed class FakeCharacterWithProperties
    {
        public List<FakeActorProperties> Properties;
        public Dictionary<string, string> CustomData;
    }

    internal sealed class FakeCharacterWithoutCustomData
    {
        public int GroupIndex = 1;
    }

    /// <summary>
    /// Covers the two halves of the position/custom-state surface separately:
    ///  - the REFLECTION idiom <c>CombatReader</c> uses (Crucible.Plugin cannot be referenced from a
    ///    game-free test, but every member it touches is read through <c>MemberResolver</c>, which can);
    ///  - the SHAPE <c>SnapshotShape</c> emits, including the ordering rules the digest depends on.
    /// </summary>
    internal static class CombatPositionTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("Board reads — reflection idiom");

            TestHarness.Run("a ValueTuple TilePosition reads as Item1/Item2", delegate
            {
                WarningSink w = new WarningSink();
                FakeVenueComponent v = new FakeVenueComponent();
                v.TilePosition = (3, 1);

                object pos = MemberResolver.GetMember(v, "TilePosition", w);
                TestHarness.Equal(3, MemberResolver.AsInt(MemberResolver.GetMember(pos, "Item1", w)).Value,
                    "Item1 is x, the ROW/DEPTH axis");
                TestHarness.Equal(1, MemberResolver.AsInt(MemberResolver.GetMember(pos, "Item2", w)).Value,
                    "Item2 is y, the LATERAL axis");
                TestHarness.Equal(0, w.Count, "no warnings");
            });

            // The axis has been documented backwards once. This pins which one is which.
            TestHarness.Run("NEGATIVE: the tuple's named fields are not real members", delegate
            {
                FakeVenueComponent v = new FakeVenueComponent();
                v.TilePosition = (3, 1);
                object pos = MemberResolver.GetMember(v, "TilePosition", null);
                TestHarness.True(MemberResolver.GetMember(pos, "x", null) == null,
                    "'x' is compiler metadata, not a field — reading it by name would silently null");
            });

            TestHarness.Run("OccupiedTiles entries read as tuples too", delegate
            {
                FakeVenueComponent v = new FakeVenueComponent();
                v.OccupiedTiles = new List<(int x, int y)> { (0, 0), (0, 1) };
                System.Collections.IList cells =
                    (System.Collections.IList)MemberResolver.GetMember(v, "OccupiedTiles", null);
                TestHarness.Equal(2, cells.Count, "two covered tiles");
                TestHarness.Equal(1, MemberResolver.AsInt(MemberResolver.GetMember(cells[1], "Item2", null)).Value,
                    "second cell's lateral axis");
            });

            TestHarness.Run("SUMMON is a name match against the Properties list", delegate
            {
                FakeCharacterWithProperties c = new FakeCharacterWithProperties();
                c.Properties = new List<FakeActorProperties> { FakeActorProperties.SUMMON };
                System.Collections.IList props =
                    (System.Collections.IList)MemberResolver.GetMember(c, "Properties", null);
                TestHarness.Equal("SUMMON", props[0].ToString(), "enum stringifies to its name");
            });

            TestHarness.Run("a null Properties list is 'no properties', not a read failure", delegate
            {
                WarningSink w = new WarningSink();
                FakeCharacterWithProperties c = new FakeCharacterWithProperties();
                TestHarness.True(MemberResolver.GetMember(c, "Properties", w) == null, "null value");
                TestHarness.Equal(0, w.Count, "declared member, so no member_missing");
            });

            TestHarness.Run("an ABSENT CustomData member is loud, a null one is not", delegate
            {
                WarningSink declared = new WarningSink();
                MemberResolver.GetMember(new FakeCharacterWithProperties(), "CustomData", declared);
                TestHarness.Equal(0, declared.Count, "declared-but-null is a real answer");

                WarningSink absent = new WarningSink();
                MemberResolver.GetMember(new FakeCharacterWithoutCustomData(), "CustomData", absent);
                TestHarness.Equal(1, absent.Count, "an absent member is loud");
            });

            TestHarness.Run("AuraStatuses reads as a list of status ids", delegate
            {
                FakeVenueTileComponent t = new FakeVenueTileComponent();
                t.AuraStatuses = new List<string> { "STATUS_FIRE_00" };
                System.Collections.IList auras =
                    (System.Collections.IList)MemberResolver.GetMember(t, "AuraStatuses", null);
                TestHarness.Equal("STATUS_FIRE_00", auras[0].ToString(), "tile effect id");
            });

            TestHarness.Run("VenueTileComponent does not match a VenueComponent lookup", delegate
            {
                Dictionary<string, object> components = new Dictionary<string, object>();
                components["a"] = new FakeVenueTileComponent();
                TestHarness.True(
                    MemberResolver.FindComponentByTypeName(components, "VenueComponent", null) == null,
                    "prefix must not match — a tile would otherwise read as an actor's position");
            });

            TestHarness.Section("SnapshotShape — position and identity");

            TestHarness.Run("a combatant carries named x/y, not a tuple", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].TileX = 3;
                c.Combatants[0].TileY = 1;
                Dictionary<string, object> tile =
                    (Dictionary<string, object>)FirstCombatant(Build(c))["tile"];
                TestHarness.Equal(3, (int)tile["x"], "x is row/depth");
                TestHarness.Equal(1, (int)tile["y"], "y is lateral");
            });

            TestHarness.Run("an off-board combatant has a null tile, not a zeroed one", delegate
            {
                TestHarness.True(FirstCombatant(Build(Combat()))["tile"] == null, "null");
            });

            TestHarness.Run("groupIndex, ordinal, isSummon and isTile are emitted", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].GroupIndex = 1;
                c.Combatants[0].RosterOrdinal = 7;
                c.Combatants[0].IsSummon = true;
                c.Combatants[0].IsTile = false;

                Dictionary<string, object> d = FirstCombatant(Build(c));
                TestHarness.Equal(1, (int)d["groupIndex"], "enemy side");
                TestHarness.Equal(7, (int)d["ordinal"], "roster index");
                TestHarness.Equal(true, (bool)d["isSummon"], "summon");
                TestHarness.Equal(false, (bool)d["isTile"], "actor, not a tile");
            });

            TestHarness.Run("the not-found ordinal sentinel serializes as null", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].RosterOrdinal = CombatantView.NoOrdinal;
                TestHarness.True(FirstCombatant(Build(c))["ordinal"] == null,
                    "int.MaxValue must never be served as a real index");
            });

            TestHarness.Section("SnapshotShape — custom state");

            TestHarness.Run("CF_ keys are emitted verbatim, not allow-listed", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].CustomDataAvailable = true;
                c.Combatants[0].CustomData["CF_COUNTER_RAGE"] = "3";
                c.Combatants[0].CustomData["CF_TRAINER_STARTER"] = "GRASS";
                c.Combatants[0].CustomData["A_KEY_NO_ONE_ALLOW_LISTED"] = "x";

                Dictionary<string, object> data =
                    (Dictionary<string, object>)FirstCombatant(Build(c))["customData"];
                TestHarness.Equal("3", data["CF_COUNTER_RAGE"] as string, "counter");
                TestHarness.Equal("GRASS", data["CF_TRAINER_STARTER"] as string, "charm progression");
                TestHarness.Equal("x", data["A_KEY_NO_ONE_ALLOW_LISTED"] as string,
                    "a future feature's key appears with no harness change");
            });

            TestHarness.Run("unreadable custom data is null, an empty map is an empty object", delegate
            {
                CombatView unreadable = Combat();
                unreadable.Combatants[0].CustomDataAvailable = false;
                TestHarness.True(FirstCombatant(Build(unreadable))["customData"] == null, "null");

                CombatView empty = Combat();
                empty.Combatants[0].CustomDataAvailable = true;
                Dictionary<string, object> d =
                    (Dictionary<string, object>)FirstCombatant(Build(empty))["customData"];
                TestHarness.Equal(0, d.Count, "empty, not null");
            });

            TestHarness.Run("a partner ball's CF_POKE_* record rides on things[]", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].ThingsAvailable = true;
                c.Combatants[0].Things.Add(Ball("t-1", "ARM_ORIG_TRAINER_BALL_GRASS", "8", "0"));

                List<object> things = (List<object>)FirstCombatant(Build(c))["things"];
                Dictionary<string, object> ball = (Dictionary<string, object>)things[0];
                Dictionary<string, object> data = (Dictionary<string, object>)ball["customData"];
                TestHarness.Equal("ARM_ORIG_TRAINER_BALL_GRASS", ball["configName"] as string, "config");
                TestHarness.Equal("8", data["CF_POKE_HP"] as string, "persisted partner hp");
                TestHarness.Equal("0", data["CF_POKE_DOWNED"] as string, "downed mirror");
            });

            TestHarness.Run("things are ordered by content, never by their local guid", delegate
            {
                CombatView a = Combat();
                a.Combatants[0].ThingsAvailable = true;
                a.Combatants[0].Things.Add(Ball("zzz-guid", "ARM_A", "1", "0"));
                a.Combatants[0].Things.Add(Ball("aaa-guid", "ARM_B", "2", "0"));

                CombatView b = Combat();
                b.Combatants[0].ThingsAvailable = true;
                b.Combatants[0].Things.Add(Ball("111-guid", "ARM_B", "2", "0"));
                b.Combatants[0].Things.Add(Ball("999-guid", "ARM_A", "1", "0"));

                TestHarness.Equal(Digest(a), Digest(b),
                    "same state, different local ids and insertion order -> same digest");
            });

            TestHarness.Section("SnapshotShape — tiles");

            TestHarness.Run("AuraStatuses is readable in state", delegate
            {
                CombatView c = Combat();
                c.TilesAvailable = true;
                c.Tiles.Add(Tile(2, 1, 1, "STATUS_FIRE_00"));

                List<object> tiles = (List<object>)Combatless(Build(c))["tiles"];
                Dictionary<string, object> t = (Dictionary<string, object>)tiles[0];
                List<object> auras = (List<object>)t["auraStatuses"];
                TestHarness.Equal("STATUS_FIRE_00", auras[0] as string,
                    "the hazard tile is no longer screenshot-only");
                TestHarness.Equal(2, (int)t["x"], "row/depth");
                TestHarness.Equal(1, (int)t["y"], "lateral");
                TestHarness.Equal(1, (int)t["groupIndex"], "enemy side of the board");
                TestHarness.Equal("FRONT", t["rowPositionsType"] as string, "row band");
            });

            TestHarness.Run("a clean tile is an empty array; an unreadable one is null", delegate
            {
                CombatView clean = Combat();
                clean.TilesAvailable = true;
                clean.Tiles.Add(Tile(0, 0, 0));
                List<object> auras = (List<object>)FirstTile(Build(clean))["auraStatuses"];
                TestHarness.Equal(0, auras.Count, "no aura, but readable");

                CombatView broken = Combat();
                broken.TilesAvailable = true;
                TileView t = Tile(0, 0, 0);
                t.AuraStatusesAvailable = false;
                broken.Tiles.Add(t);
                TestHarness.True(FirstTile(Build(broken))["auraStatuses"] == null,
                    "a renamed field must not read as a clean tile");
            });

            TestHarness.Run("a tile names its occupant by id and ordinal", delegate
            {
                CombatView c = Combat();
                c.TilesAvailable = true;
                TileView t = Tile(1, 0, 0);
                t.RosterOrdinal = 4;
                t.OccupantId = "e-1";
                t.OccupantOrdinal = 9;
                c.Tiles.Add(t);

                Dictionary<string, object> d = FirstTile(Build(c));
                TestHarness.Equal("e-1", d["occupantId"] as string, "occupant");
                TestHarness.Equal(9, (int)d["occupantOrdinal"], "occupant ordinal");
                TestHarness.Equal(4, (int)d["ordinal"], "the tile's own roster index");
            });

            TestHarness.Run("an unreadable tile list is null, not an empty array", delegate
            {
                CombatView c = Combat();
                c.TilesAvailable = false;
                TestHarness.True(Combatless(Build(c))["tiles"] == null, "null");
            });

            TestHarness.Section("SnapshotShape — v2 digest, board fields");

            TestHarness.Run("tile order does not affect the v2 digest", delegate
            {
                CombatView a = Combat();
                a.TilesAvailable = true;
                a.Tiles.Add(Tile(0, 0, 0));
                a.Tiles.Add(Tile(1, 1, 0, "STATUS_FIRE_00"));

                CombatView b = Combat();
                b.TilesAvailable = true;
                b.Tiles.Add(Tile(1, 1, 0, "STATUS_FIRE_00"));
                b.Tiles.Add(Tile(0, 0, 0));

                TestHarness.Equal(Digest(a), Digest(b), "sorted by (x, y, groupIndex) before hashing");
            });

            TestHarness.Run("aura order does not affect the v2 digest", delegate
            {
                CombatView a = Combat();
                a.TilesAvailable = true;
                a.Tiles.Add(Tile(0, 0, 0, "STATUS_FIRE_00", "STATUS_ACID_00"));

                CombatView b = Combat();
                b.TilesAvailable = true;
                b.Tiles.Add(Tile(0, 0, 0, "STATUS_ACID_00", "STATUS_FIRE_00"));

                TestHarness.Equal(Digest(a), Digest(b), "sorted ordinally before hashing");
            });

            // Negative control: the whole point is that a real board change moves the digest.
            TestHarness.Run("a hazard aura DOES affect the v2 digest", delegate
            {
                CombatView a = Combat();
                a.TilesAvailable = true;
                a.Tiles.Add(Tile(0, 0, 0));

                CombatView b = Combat();
                b.TilesAvailable = true;
                b.Tiles.Add(Tile(0, 0, 0, "STATUS_FIRE_00"));

                TestHarness.NotEqual(Digest(a), Digest(b), "tile effects are now in the oracle");
            });

            TestHarness.Run("a move DOES affect the v2 digest", delegate
            {
                CombatView a = Combat();
                a.Combatants[0].TileX = 0;
                a.Combatants[0].TileY = 0;

                CombatView b = Combat();
                b.Combatants[0].TileX = 1;
                b.Combatants[0].TileY = 0;

                TestHarness.NotEqual(Digest(a), Digest(b), "position is in the oracle");
            });

            TestHarness.Run("a counter change DOES affect the v2 digest", delegate
            {
                CombatView a = Combat();
                a.Combatants[0].CustomDataAvailable = true;
                a.Combatants[0].CustomData["CF_COUNTER_RAGE"] = "1";

                CombatView b = Combat();
                b.Combatants[0].CustomDataAvailable = true;
                b.Combatants[0].CustomData["CF_COUNTER_RAGE"] = "2";

                TestHarness.NotEqual(Digest(a), Digest(b), "class-feature state is in the oracle");
            });

            // SUMMONED_BY holds an ENTITY GUID, which is LOCAL (EntityKey.cs) — two healthy peers
            // disagree on it by construction, so it must not read as a desync.
            TestHarness.Run("SUMMONED_BY does not affect the v2 digest", delegate
            {
                CombatView a = Combat();
                a.Combatants[0].CustomDataAvailable = true;
                a.Combatants[0].CustomData["SUMMONED_BY"] = "guid-on-peer-a";

                CombatView b = Combat();
                b.Combatants[0].CustomDataAvailable = true;
                b.Combatants[0].CustomData["SUMMONED_BY"] = "guid-on-peer-b";

                TestHarness.Equal(Digest(a), Digest(b), "redacted: it is a local guid");
            });

            TestHarness.Run("a tile's occupantId does not affect the v2 digest", delegate
            {
                CombatView a = Combat();
                a.TilesAvailable = true;
                TileView ta = Tile(0, 0, 0);
                ta.OccupantId = "guid-on-peer-a";
                ta.OccupantOrdinal = 5;
                a.Tiles.Add(ta);

                CombatView b = Combat();
                b.TilesAvailable = true;
                TileView tb = Tile(0, 0, 0);
                tb.OccupantId = "guid-on-peer-b";
                tb.OccupantOrdinal = 5;
                b.Tiles.Add(tb);

                TestHarness.Equal(Digest(a), Digest(b), "guid redacted, ordinal retained");
            });

            TestHarness.Run("the board fields round-trip through MiniJson", delegate
            {
                CombatView c = Combat();
                c.Combatants[0].TileX = 1;
                c.Combatants[0].TileY = 0;
                c.Combatants[0].CustomDataAvailable = true;
                c.Combatants[0].CustomData["CF_POKE_NICKNAME"] = "Sparky";
                c.Combatants[0].ThingsAvailable = true;
                c.Combatants[0].Things.Add(Ball("t-1", "ARM_ORIG_TRAINER_BALL_GRASS", "8", "0"));
                c.TilesAvailable = true;
                c.Tiles.Add(Tile(1, 0, 0, "STATUS_FIRE_00"));

                object parsed; string error;
                TestHarness.True(MiniJson.TryParse(MiniJson.Write(Build(c)), out parsed, out error),
                    "parses: " + error);
                TestHarness.True(MiniJson.AsObject(parsed) != null, "object at the root");
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
            c.Name = "Pokemon Trainer";
            c.ClassId = "CF_ORIG_TRAINER";
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

        private static ThingView Ball(string id, string configName, string hp, string downed)
        {
            ThingView t = new ThingView();
            t.Id = id;
            t.ConfigName = configName;
            t.CustomData["CF_POKE_HP"] = hp;
            t.CustomData["CF_POKE_DOWNED"] = downed;
            return t;
        }

        private static TileView Tile(int x, int y, int groupIndex, params string[] auras)
        {
            TileView t = new TileView();
            t.X = x;
            t.Y = y;
            t.GroupIndex = groupIndex;
            t.RowPositionsType = "FRONT";
            t.AuraStatusesAvailable = true;
            for (int i = 0; i < auras.Length; i++) t.AuraStatuses.Add(auras[i]);
            return t;
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

        private static Dictionary<string, object> FirstTile(Dictionary<string, object> snap)
        {
            List<object> tiles = (List<object>)Combatless(snap)["tiles"];
            return (Dictionary<string, object>)tiles[0];
        }
    }
}

namespace FTK2Mods.Crucible.Tests
{
    internal static class FixtureManifestTests
    {
        private static FixtureManifest ValidManifest()
        {
            FixtureManifest m = new FixtureManifest();
            m.FixtureName = "four-classes-basic";
            m.PartyClassIds = new[] { "CF_EOR_THIEF", "CF_EOR_TRICKSHOT", "CF_EOR_CORSAIR", "CF_EOR_BLADEDANCER" };
            m.RunId = "0778a3d1-5bf7-449a-acae-263102cf2235";
            m.GameVersion = "1.2.3";
            m.ClassForgeDataHash = "abc123";
            m.TimestampUtc = "2026-08-23T00:00:00Z";
            m.RecipeSteps = new[] { "new-run-four-classes" };
            m.ItemsGranted = new[] { new FixtureManifest.ItemGrant("SCROLL_TELEPORT_01", 99) };
            return m;
        }

        internal static void RunAll()
        {
            TestHarness.Section("FixtureManifestShape.TryValidate");

            TestHarness.Run("a fully-populated manifest validates", delegate
            {
                string error;
                TestHarness.True(FixtureManifestShape.TryValidate(ValidManifest(), out error), "should validate: " + error);
            });

            TestHarness.Run("ClassForgeDataHash may be null (optional field)", delegate
            {
                FixtureManifest m = ValidManifest();
                m.ClassForgeDataHash = null;
                string error;
                TestHarness.True(FixtureManifestShape.TryValidate(m, out error), "should still validate: " + error);
            });

            TestHarness.Run("ItemsGranted may be an empty array (no items granted)", delegate
            {
                FixtureManifest m = ValidManifest();
                m.ItemsGranted = new FixtureManifest.ItemGrant[0];
                string error;
                TestHarness.True(FixtureManifestShape.TryValidate(m, out error), "should still validate: " + error);
            });

            TestHarness.Run("NEGATIVE: null manifest is refused", delegate
            {
                string error;
                TestHarness.True(!FixtureManifestShape.TryValidate(null, out error), "should refuse a null manifest");
            });

            TestHarness.Run("NEGATIVE: missing FixtureName is refused", delegate
            {
                FixtureManifest m = ValidManifest();
                m.FixtureName = null;
                string error;
                TestHarness.True(!FixtureManifestShape.TryValidate(m, out error), "should refuse");
            });

            TestHarness.Run("NEGATIVE: a party of three is refused (party is always four)", delegate
            {
                FixtureManifest m = ValidManifest();
                m.PartyClassIds = new[] { "CF_EOR_THIEF", "CF_EOR_TRICKSHOT", "CF_EOR_CORSAIR" };
                string error;
                TestHarness.True(!FixtureManifestShape.TryValidate(m, out error), "should refuse");
            });

            TestHarness.Run("NEGATIVE: a blank slot in the party is refused", delegate
            {
                FixtureManifest m = ValidManifest();
                m.PartyClassIds = new[] { "CF_EOR_THIEF", "", "CF_EOR_CORSAIR", "CF_EOR_BLADEDANCER" };
                string error;
                TestHarness.True(!FixtureManifestShape.TryValidate(m, out error), "should refuse");
            });

            // NEGATIVE CONTROL: same "never default an implicit run id" property crucible_load_run
            // enforces -- a manifest without a real run id must not validate either.
            TestHarness.Run("NEGATIVE: an empty RunId is refused", delegate
            {
                FixtureManifest m = ValidManifest();
                m.RunId = "";
                string error;
                TestHarness.True(!FixtureManifestShape.TryValidate(m, out error), "should refuse");
            });

            TestHarness.Run("NEGATIVE: missing GameVersion is refused", delegate
            {
                FixtureManifest m = ValidManifest();
                m.GameVersion = null;
                string error;
                TestHarness.True(!FixtureManifestShape.TryValidate(m, out error), "should refuse");
            });

            TestHarness.Run("NEGATIVE: missing TimestampUtc is refused", delegate
            {
                FixtureManifest m = ValidManifest();
                m.TimestampUtc = "";
                string error;
                TestHarness.True(!FixtureManifestShape.TryValidate(m, out error), "should refuse");
            });

            TestHarness.Run("NEGATIVE: null RecipeSteps is refused (use an empty array, not null)", delegate
            {
                FixtureManifest m = ValidManifest();
                m.RecipeSteps = null;
                string error;
                TestHarness.True(!FixtureManifestShape.TryValidate(m, out error), "should refuse");
            });

            TestHarness.Run("NEGATIVE: null ItemsGranted is refused (use an empty array, not null)", delegate
            {
                FixtureManifest m = ValidManifest();
                m.ItemsGranted = null;
                string error;
                TestHarness.True(!FixtureManifestShape.TryValidate(m, out error), "should refuse");
            });

            TestHarness.Run("NEGATIVE: an item grant with zero quantity is refused", delegate
            {
                FixtureManifest m = ValidManifest();
                m.ItemsGranted = new[] { new FixtureManifest.ItemGrant("SCROLL_TELEPORT_01", 0) };
                string error;
                TestHarness.True(!FixtureManifestShape.TryValidate(m, out error), "should refuse");
            });

            TestHarness.Run("NEGATIVE: an item grant with no ConfigId is refused", delegate
            {
                FixtureManifest m = ValidManifest();
                m.ItemsGranted = new[] { new FixtureManifest.ItemGrant(null, 99) };
                string error;
                TestHarness.True(!FixtureManifestShape.TryValidate(m, out error), "should refuse");
            });

            TestHarness.Section("FixtureManifestShape.ToJson");

            TestHarness.Run("round-trips through MiniJson and carries every field", delegate
            {
                string json = FixtureManifestShape.ToJson(ValidManifest());
                object parsed; string parseError;
                TestHarness.True(MiniJson.TryParse(json, out parsed, out parseError), "parses: " + parseError);

                System.Collections.Generic.Dictionary<string, object> obj = MiniJson.AsObject(parsed);
                TestHarness.True(obj != null, "top-level value is an object");
                TestHarness.Equal("four-classes-basic", MiniJson.AsString(obj["fixtureName"]), "fixtureName");
                TestHarness.Equal("0778a3d1-5bf7-449a-acae-263102cf2235", MiniJson.AsString(obj["runId"]), "runId");

                System.Collections.Generic.List<object> classIds = MiniJson.AsArray(obj["partyClassIds"]);
                TestHarness.Equal(4, classIds.Count, "partyClassIds count");

                System.Collections.Generic.List<object> grants = MiniJson.AsArray(obj["itemsGranted"]);
                TestHarness.Equal(1, grants.Count, "itemsGranted count");
                System.Collections.Generic.Dictionary<string, object> grant0 = MiniJson.AsObject(grants[0]);
                TestHarness.Equal("SCROLL_TELEPORT_01", MiniJson.AsString(grant0["configId"]), "itemsGranted[0].configId");
            });

            TestHarness.Run("a null ClassForgeDataHash serializes as JSON null, not the string 'null'", delegate
            {
                FixtureManifest m = ValidManifest();
                m.ClassForgeDataHash = null;
                string json = FixtureManifestShape.ToJson(m);
                object parsed; string parseError;
                TestHarness.True(MiniJson.TryParse(json, out parsed, out parseError), "parses: " + parseError);
                System.Collections.Generic.Dictionary<string, object> obj = MiniJson.AsObject(parsed);
                TestHarness.True(obj["classForgeDataHash"] == null, "classForgeDataHash should be null");
            });
        }
    }
}

using System;
using System.Collections.Generic;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// The <c>fixture.json</c> shape written beside a captured <c>.ftk2</c> save by
    /// <c>tools/make-fixture.ps1</c>. Declared here, not just in the PowerShell script, so the shape
    /// has one source of truth that unit-tests can hold to a contract without a running game --
    /// the script is expected to produce JSON matching <see cref="FixtureManifestShape.ToJson"/>.
    ///
    /// A fixture's mobility (which consumable items were pre-granted, e.g. teleport scrolls) is part
    /// of its declared state via <see cref="ItemsGranted"/>, not a hidden side effect of the recipe
    /// that produced it.
    /// </summary>
    public sealed class FixtureManifest
    {
        public string FixtureName;
        public string[] PartyClassIds;
        public string RunId;
        public string GameVersion;
        /// <summary>ClassForge's own content hash, when the recipe was able to read one. Null is a
        /// legitimate value -- "if readable" in the task, not a required field.</summary>
        public string ClassForgeDataHash;
        /// <summary>ISO-8601 UTC timestamp of capture.</summary>
        public string TimestampUtc;
        public string[] RecipeSteps;
        public ItemGrant[] ItemsGranted;

        public sealed class ItemGrant
        {
            public string ConfigId;
            public int Quantity;

            public ItemGrant() { }

            public ItemGrant(string configId, int quantity)
            {
                ConfigId = configId;
                Quantity = quantity;
            }
        }
    }

    /// <summary>
    /// Validation and JSON shaping for <see cref="FixtureManifest"/>. Pure -- no game reference, no
    /// file I/O -- so it unit-tests without a running game or a real fixture on disk.
    /// </summary>
    public static class FixtureManifestShape
    {
        /// <summary>
        /// Required: FixtureName, exactly four non-empty PartyClassIds (a party is always four
        /// characters), a RunId that passes the same <see cref="LoadRunGuard"/> the load path
        /// enforces, GameVersion, and TimestampUtc. ClassForgeDataHash is optional (nullable).
        /// RecipeSteps and ItemsGranted may be empty but not null -- an omitted array reads as "was
        /// never recorded", which is a different fact than "recorded as zero".
        /// </summary>
        public static bool TryValidate(FixtureManifest manifest, out string error)
        {
            error = null;
            if (manifest == null) { error = "manifest is null"; return false; }

            if (string.IsNullOrWhiteSpace(manifest.FixtureName)) { error = "FixtureName is required"; return false; }

            if (manifest.PartyClassIds == null || manifest.PartyClassIds.Length != 4)
            {
                error = "PartyClassIds must have exactly 4 entries (got "
                    + (manifest.PartyClassIds == null ? "null" : manifest.PartyClassIds.Length.ToString()) + ")";
                return false;
            }
            for (int i = 0; i < manifest.PartyClassIds.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(manifest.PartyClassIds[i]))
                {
                    error = "PartyClassIds[" + i + "] is empty";
                    return false;
                }
            }

            string runIdError;
            if (!LoadRunGuard.TryValidateRunId(manifest.RunId, out runIdError))
            {
                error = "RunId: " + runIdError;
                return false;
            }

            if (string.IsNullOrWhiteSpace(manifest.GameVersion)) { error = "GameVersion is required"; return false; }
            if (string.IsNullOrWhiteSpace(manifest.TimestampUtc)) { error = "TimestampUtc is required"; return false; }

            if (manifest.RecipeSteps == null) { error = "RecipeSteps must not be null (use an empty array)"; return false; }
            if (manifest.ItemsGranted == null) { error = "ItemsGranted must not be null (use an empty array)"; return false; }

            for (int i = 0; i < manifest.ItemsGranted.Length; i++)
            {
                FixtureManifest.ItemGrant grant = manifest.ItemsGranted[i];
                if (grant == null || string.IsNullOrWhiteSpace(grant.ConfigId))
                {
                    error = "ItemsGranted[" + i + "] has no ConfigId";
                    return false;
                }
                if (grant.Quantity <= 0)
                {
                    error = "ItemsGranted[" + i + "] (" + grant.ConfigId + ") quantity must be positive, got " + grant.Quantity;
                    return false;
                }
            }

            return true;
        }

        internal static Dictionary<string, object> ToJsonObject(FixtureManifest manifest)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["fixtureName"] = manifest.FixtureName;

            List<object> classIds = new List<object>();
            if (manifest.PartyClassIds != null)
                foreach (string id in manifest.PartyClassIds) classIds.Add(id);
            d["partyClassIds"] = classIds;

            d["runId"] = manifest.RunId;
            d["gameVersion"] = manifest.GameVersion;
            d["classForgeDataHash"] = manifest.ClassForgeDataHash;
            d["timestampUtc"] = manifest.TimestampUtc;

            List<object> steps = new List<object>();
            if (manifest.RecipeSteps != null)
                foreach (string s in manifest.RecipeSteps) steps.Add(s);
            d["recipeSteps"] = steps;

            List<object> grants = new List<object>();
            if (manifest.ItemsGranted != null)
            {
                foreach (FixtureManifest.ItemGrant grant in manifest.ItemsGranted)
                {
                    Dictionary<string, object> g = new Dictionary<string, object>();
                    g["configId"] = grant.ConfigId;
                    g["quantity"] = grant.Quantity;
                    grants.Add(g);
                }
            }
            d["itemsGranted"] = grants;

            return d;
        }

        /// <summary>Renders the manifest as the JSON that belongs at data/Fixtures/&lt;name&gt;/fixture.json.</summary>
        public static string ToJson(FixtureManifest manifest)
        {
            return MiniJson.Write(ToJsonObject(manifest));
        }
    }
}

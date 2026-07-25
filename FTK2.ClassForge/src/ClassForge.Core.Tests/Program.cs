using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassForge.Core;
using ClassForge.Core.IO;
using ClassForge.Core.Json;
using ClassForge.Core.Tests;

int passed = 0, failed = 0;

void Test(string name, Action body)
{
    try
    {
        body();
        Console.WriteLine($"PASS: {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL: {name} -- {ex.Message}");
        failed++;
    }
}

void Assert(bool cond, string message)
{
    if (!cond) throw new Exception(message);
}

void AssertEqual<T>(T expected, T actual, string message)
{
    if (!Equals(expected, actual))
        throw new Exception($"{message} (expected '{expected}', actual '{actual}')");
}

string MakePackJson(string id, int loadOrder = 0, string[]? dependencies = null, bool enabled = true) =>
    "{" +
    $"\"id\":\"{id}\"," +
    $"\"name\":\"{id}\"," +
    "\"version\":\"1.0.0\"," +
    $"\"loadOrder\":{loadOrder}," +
    $"\"dependencies\":[{string.Join(",", (dependencies ?? Array.Empty<string>()).Select(d => $"\"{d}\""))}]," +
    $"\"enabled\":{(enabled ? "true" : "false")}" +
    "}";

// ---------------------------------------------------------------------
// 1. Discovery determinism: shuffled directory-listing order -> identical resolved order.
// ---------------------------------------------------------------------
Test("Discovery determinism: shuffled directory order yields identical sorted result", () =>
{
    var ids = new[] { "CF_PACK_ALPHA", "CF_PACK_BETA", "CF_PACK_GAMMA" };

    var fsInsertionOrder = new InMemoryFileSource();
    foreach (var id in ids)
        fsInsertionOrder.AddFile($"root/{id}/pack.json", MakePackJson(id));

    var fsShuffled = new InMemoryFileSource();
    foreach (var id in ids)
        fsShuffled.AddFile($"root/{id}/pack.json", MakePackJson(id));
    fsShuffled.ShuffleDirectoryOrder(new[] { "root/CF_PACK_GAMMA", "root/CF_PACK_ALPHA", "root/CF_PACK_BETA" });

    var findingsA = new List<Finding>();
    var findingsB = new List<Finding>();
    var a = PackDiscovery.Discover(fsInsertionOrder, new[] { "root" }, findingsA).Select(p => p.Manifest.Id).ToList();
    var b = PackDiscovery.Discover(fsShuffled, new[] { "root" }, findingsB).Select(p => p.Manifest.Id).ToList();

    Assert(a.SequenceEqual(b), $"Discovery order differed between insertion-order and shuffled file sources: [{string.Join(",", a)}] vs [{string.Join(",", b)}]");
    Assert(a.SequenceEqual(ids.OrderBy(x => x, StringComparer.Ordinal)), "Discovery result was not alphabetically sorted by pack id.");
});

// ---------------------------------------------------------------------
// 2. Topo sort / cycle / missing-dep cases.
// ---------------------------------------------------------------------
Test("Ordering: dependencies force order regardless of discovery order", () =>
{
    var packs = new List<DiscoveredPack>
    {
        MakeDiscovered("CF_PACK_A", loadOrder: 0, deps: new[] { "CF_PACK_B" }),
        MakeDiscovered("CF_PACK_B", loadOrder: 0, deps: new[] { "CF_PACK_C" }),
        MakeDiscovered("CF_PACK_C", loadOrder: 0),
    };
    var findings = new List<Finding>();
    var ordered = PackOrderer.Order(packs, findings).Select(p => p.Manifest.Id).ToList();
    Assert(ordered.SequenceEqual(new[] { "CF_PACK_C", "CF_PACK_B", "CF_PACK_A" }), $"Expected C,B,A got {string.Join(",", ordered)}");
});

Test("Ordering: loadOrder ties break alphabetically by id", () =>
{
    var packs = new List<DiscoveredPack>
    {
        MakeDiscovered("CF_PACK_E", loadOrder: 5),
        MakeDiscovered("CF_PACK_D", loadOrder: 5),
    };
    var findings = new List<Finding>();
    var ordered = PackOrderer.Order(packs, findings).Select(p => p.Manifest.Id).ToList();
    Assert(ordered.SequenceEqual(new[] { "CF_PACK_D", "CF_PACK_E" }), $"Expected D,E got {string.Join(",", ordered)}");
});

Test("Ordering: missing dependency skips the dependent pack and logs a Finding", () =>
{
    var packs = new List<DiscoveredPack>
    {
        MakeDiscovered("CF_PACK_F", loadOrder: 0, deps: new[] { "CF_PACK_MISSING" }),
        MakeDiscovered("CF_PACK_OK", loadOrder: 0),
    };
    var findings = new List<Finding>();
    var ordered = PackOrderer.Order(packs, findings).Select(p => p.Manifest.Id).ToList();
    Assert(ordered.SequenceEqual(new[] { "CF_PACK_OK" }), $"Expected only CF_PACK_OK got {string.Join(",", ordered)}");
    Assert(findings.Any(f => f.Code == "CF_MISSING_DEP" && f.PackId == "CF_PACK_F"), "Expected a CF_MISSING_DEP Finding for CF_PACK_F.");
});

Test("Ordering: dependency cycle skips every pack in the cycle and logs Findings", () =>
{
    var packs = new List<DiscoveredPack>
    {
        MakeDiscovered("CF_PACK_G", loadOrder: 0, deps: new[] { "CF_PACK_H" }),
        MakeDiscovered("CF_PACK_H", loadOrder: 0, deps: new[] { "CF_PACK_G" }),
        MakeDiscovered("CF_PACK_OK2", loadOrder: 0),
    };
    var findings = new List<Finding>();
    var ordered = PackOrderer.Order(packs, findings).Select(p => p.Manifest.Id).ToList();
    Assert(ordered.SequenceEqual(new[] { "CF_PACK_OK2" }), $"Expected only CF_PACK_OK2 got {string.Join(",", ordered)}");
    Assert(findings.Count(f => f.Code == "CF_CYCLE") == 2, $"Expected 2 CF_CYCLE findings, got {findings.Count(f => f.Code == "CF_CYCLE")}");
});

// ---------------------------------------------------------------------
// 3. CF_PACK_BALDURS end-to-end parse + merge plan against the real fixture on disk.
// ---------------------------------------------------------------------
var classPacksDir = FindClassPacksDir();
var baldursDir = Path.Combine(classPacksDir, "CF_PACK_BALDURS");
Console.WriteLine($"Fixture root: {baldursDir}");

PackLoadResult? baldursResult = null;

Test("CF_PACK_BALDURS: loads cleanly with zero Error findings", () =>
{
    var fs = new FileSystemFileSource();
    var loader = new PackLoader();
    // Scan the real data/ClassPacks dir (so this is still a true end-to-end read of the shipped
    // BALDURS fixture), but restrict "enabled" to just CF_PACK_BALDURS via the isPackEnabled knob
    // (same mechanism ClassForge.PackCheck uses). This keeps the assertion independent of how many
    // other packs happen to be enabled in the repo (e.g. CF_PACK_EOR_CLASSES) -- we assert on
    // CF_PACK_BALDURS's own presence/shape, not on a repo-wide enabled-pack count.
    baldursResult = loader.Load(fs, new[] { classPacksDir }, id => string.Equals(id, "CF_PACK_BALDURS", StringComparison.Ordinal));

    var errors = baldursResult.Findings.Where(f => f.Severity == FindingSeverity.Error).ToList();
    Assert(errors.Count == 0, "Unexpected Error findings: " + string.Join(" | ", errors.Select(e => e.ToString())));
    Assert(baldursResult.EnabledOrderedPacks.Count == 1, $"Expected 1 enabled pack, got {baldursResult.EnabledOrderedPacks.Count}");
    AssertEqual("CF_PACK_BALDURS", baldursResult.EnabledOrderedPacks[0].Id, "Enabled pack id mismatch");
});

Test("CF_PACK_BALDURS: merge plan counts match independently re-parsed raw JSON", () =>
{
    var result = baldursResult!;
    int RawObjectCount(string fileName)
    {
        var text = File.ReadAllText(Path.Combine(baldursDir, fileName));
        return JsonParser.Parse(text).AsObjectMembers.Count;
    }

    var classCount = RawObjectCount("classes.json");
    var traitCount = RawObjectCount("traits.json");
    var abilityCount = RawObjectCount("abilities.json");
    var itemCount = RawObjectCount("items.json");
    var locCount = RawObjectCount(Path.Combine("localization", "en.json"));

    Console.WriteLine($"  classes={classCount} traits={traitCount} abilities={abilityCount} items={itemCount} localization={locCount}");

    AssertEqual(classCount, result.MergePlan.Characters.Count, "Characters count");
    AssertEqual(traitCount + itemCount, result.MergePlan.Things.Count, "Things (traits+items) count");
    AssertEqual(abilityCount, result.MergePlan.Abilities.Count, "Abilities count");
    AssertEqual(locCount, result.MergePlan.Localization.Count, "Localization key count");
    AssertEqual(traitCount, result.MergePlan.TraitIds.Count, "TraitIds count (every traits.json entry has Class:\"TRAIT\")");

    var iconFiles = Directory.GetFiles(Path.Combine(baldursDir, "icons"), "*.png").Length;
    var portraitFiles = Directory.GetFiles(Path.Combine(baldursDir, "portraits"), "*.png").Length;
    AssertEqual(iconFiles, result.MergePlan.Icons.Count, "Icons count");
    AssertEqual(portraitFiles, result.MergePlan.Portraits.Count, "Portraits count");

    Console.WriteLine($"  BALDURS merge plan: classes={result.MergePlan.Characters.Count} things={result.MergePlan.Things.Count} " +
                       $"abilities={result.MergePlan.Abilities.Count} traitIds={result.MergePlan.TraitIds.Count} " +
                       $"loc={result.MergePlan.Localization.Count} icons={result.MergePlan.Icons.Count} portraits={result.MergePlan.Portraits.Count}");
});

Test("CF_PACK_BALDURS: TRAIT_-prefixed trait ids produce zero CF_TRAIT_PREFIX warnings", () =>
{
    // The fixture's 6 trait ids were renamed CF_TRAIT_* -> TRAIT_CF_BALDURS_* so the native trait
    // substrate (CharacterHelper.GiveTrait/InventoryHelper.GetTraits, which keys on a literal
    // "TRAIT_" ConfigName prefix) can actually inject them. That should make the loader's
    // CF_TRAIT_PREFIX warning disappear for this pack entirely.
    var result = baldursResult!;
    var warnings = result.Findings.Where(f => f.Code == "CF_TRAIT_PREFIX").ToList();
    Assert(warnings.Count == 0, $"Expected 0 CF_TRAIT_PREFIX warnings (fixture trait ids are TRAIT_-prefixed), got {warnings.Count}: " + string.Join(" | ", warnings.Select(w => w.ToString())));
});

Test("CF_PACK_BALDURS: dataHash is sha256:-prefixed, stable 64-char hex (MP review B0)", () =>
{
    var result = baldursResult!;
    Assert(!string.IsNullOrEmpty(result.DataHash), "DataHash was empty.");
    Assert(result.DataHash.StartsWith("sha256:", StringComparison.Ordinal),
        "DataHash must be sha256:-prefixed so DevKit's ParityComparer treats it as well-formed (MP review B0), was: " + result.DataHash);
    var hex = result.DataHash.Substring("sha256:".Length);
    Assert(hex.Length == 64, $"DataHash hex portion should be 64 chars, was {hex.Length}.");
    Assert(hex.All(c => "0123456789abcdef".Contains(c)), "DataHash hex portion contained non-hex characters.");
});

// ---------------------------------------------------------------------
// 4. Collision: id defined by two packs -> last (later load order) pack wins, both ids logged.
// ---------------------------------------------------------------------
Test("Merge: id collision across packs resolves last-pack-wins and logs both pack ids", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_ONE/pack.json", MakePackJson("CF_PACK_ONE", loadOrder: 0));
    fs.AddFile("root/CF_PACK_ONE/classes.json", "{\"CF_X\":{\"Stats\":{},\"Value\":1}}");
    fs.AddFile("root/CF_PACK_TWO/pack.json", MakePackJson("CF_PACK_TWO", loadOrder: 10));
    fs.AddFile("root/CF_PACK_TWO/classes.json", "{\"CF_X\":{\"Stats\":{},\"Value\":2}}");

    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { "root" });

    AssertEqual(1, result.MergePlan.Characters.Count, "Expected exactly one merged CF_X entry");
    var op = result.MergePlan.Characters.Single();
    AssertEqual("CF_PACK_TWO", op.SourcePackId, "Later-loadOrder pack should win the collision");
    AssertEqual(2.0, op.Value.GetNumber("Value"), "Winning value should be CF_PACK_TWO's Value");

    var collisionFindings = result.Findings.Where(f => f.Code == "CF_ID_COLLISION").ToList();
    Assert(collisionFindings.Count == 1, $"Expected 1 CF_ID_COLLISION finding, got {collisionFindings.Count}");
    Assert(collisionFindings[0].Message.Contains("CF_PACK_ONE") && collisionFindings[0].Message.Contains("CF_PACK_TWO"),
        "Collision finding should name both pack ids: " + collisionFindings[0].Message);
});

// ---------------------------------------------------------------------
// 5. dataHash stability: CRLF/LF invariance, localization exclusion, sensitivity to real changes.
// ---------------------------------------------------------------------
Test("DataHasher: CRLF vs LF line endings produce the same hash", () =>
{
    var fsLf = new InMemoryFileSource();
    fsLf.AddFile("root/CF_PACK_X/pack.json", MakePackJson("CF_PACK_X"));
    fsLf.AddFile("root/CF_PACK_X/classes.json", "{\n  \"A\":1\n}");

    var fsCrlf = new InMemoryFileSource();
    fsCrlf.AddFile("root/CF_PACK_X/pack.json", MakePackJson("CF_PACK_X"));
    fsCrlf.AddFile("root/CF_PACK_X/classes.json", "{\r\n  \"A\":1\r\n}");

    var hashLf = DataHasher.ComputeHash(fsLf, new[] { new PackForHash("CF_PACK_X", "root/CF_PACK_X") });
    var hashCrlf = DataHasher.ComputeHash(fsCrlf, new[] { new PackForHash("CF_PACK_X", "root/CF_PACK_X") });

    AssertEqual(hashLf, hashCrlf, "CRLF and LF variants of the same content should hash identically");
});

Test("DataHasher: localization/** is excluded from the hash", () =>
{
    var fsWithoutLoc = new InMemoryFileSource();
    fsWithoutLoc.AddFile("root/CF_PACK_Y/pack.json", MakePackJson("CF_PACK_Y"));
    fsWithoutLoc.AddFile("root/CF_PACK_Y/classes.json", "{\"A\":1}");

    var fsWithLoc = new InMemoryFileSource();
    fsWithLoc.AddFile("root/CF_PACK_Y/pack.json", MakePackJson("CF_PACK_Y"));
    fsWithLoc.AddFile("root/CF_PACK_Y/classes.json", "{\"A\":1}");
    fsWithLoc.AddFile("root/CF_PACK_Y/localization/en.json", "{\"A\":\"anything, doesn't matter\"}");

    var hashWithout = DataHasher.ComputeHash(fsWithoutLoc, new[] { new PackForHash("CF_PACK_Y", "root/CF_PACK_Y") });
    var hashWith = DataHasher.ComputeHash(fsWithLoc, new[] { new PackForHash("CF_PACK_Y", "root/CF_PACK_Y") });

    AssertEqual(hashWithout, hashWith, "Adding a localization file should not change the dataHash");
});

Test("DataHasher: a real (non-localization) content change does change the hash", () =>
{
    var fs1 = new InMemoryFileSource();
    fs1.AddFile("root/CF_PACK_Z/pack.json", MakePackJson("CF_PACK_Z"));
    fs1.AddFile("root/CF_PACK_Z/classes.json", "{\"A\":1}");

    var fs2 = new InMemoryFileSource();
    fs2.AddFile("root/CF_PACK_Z/pack.json", MakePackJson("CF_PACK_Z"));
    fs2.AddFile("root/CF_PACK_Z/classes.json", "{\"A\":2}");

    var hash1 = DataHasher.ComputeHash(fs1, new[] { new PackForHash("CF_PACK_Z", "root/CF_PACK_Z") });
    var hash2 = DataHasher.ComputeHash(fs2, new[] { new PackForHash("CF_PACK_Z", "root/CF_PACK_Z") });

    Assert(hash1 != hash2, "Changing classes.json content should change the dataHash");
});

Test("DataHasher: provenance.json is excluded (metadata, not gameplay data -- MP review M5)", () =>
{
    var fsWithout = new InMemoryFileSource();
    fsWithout.AddFile("root/CF_PACK_PROV/pack.json", MakePackJson("CF_PACK_PROV"));
    fsWithout.AddFile("root/CF_PACK_PROV/classes.json", "{\"A\":1}");

    var fsWith = new InMemoryFileSource();
    fsWith.AddFile("root/CF_PACK_PROV/pack.json", MakePackJson("CF_PACK_PROV"));
    fsWith.AddFile("root/CF_PACK_PROV/classes.json", "{\"A\":1}");
    fsWith.AddFile("root/CF_PACK_PROV/provenance.json", "{\"PackageVersion\":\"anything, doesn't matter\"}");

    var hashWithout = DataHasher.ComputeHash(fsWithout, new[] { new PackForHash("CF_PACK_PROV", "root/CF_PACK_PROV") });
    var hashWith = DataHasher.ComputeHash(fsWith, new[] { new PackForHash("CF_PACK_PROV", "root/CF_PACK_PROV") });

    AssertEqual(hashWithout, hashWith, "provenance.json is import/regeneration bookkeeping, not gameplay data, and must not affect the dataHash");
});

Test("DataHasher: PNG (binary, non-UTF8) bytes are hashed directly and stably (MP review M5)", () =>
{
    // Real PNG signature bytes, deliberately including 0x0D 0x0A (which a naive text-based CRLF
    // normalizer would corrupt) and 0xFF/0x89 (invalid as a UTF-8 lead byte on its own -- a
    // File.ReadAllText(..., Encoding.UTF8) decode would replace these with U+FFFD).
    var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0xFE, 0x01, 0x02, 0x03 };

    var fs1 = new InMemoryFileSource();
    fs1.AddFile("root/CF_PACK_PNG/pack.json", MakePackJson("CF_PACK_PNG"));
    fs1.AddBinaryFile("root/CF_PACK_PNG/icons/CF_X.png", pngBytes);

    var fs2 = new InMemoryFileSource();
    fs2.AddFile("root/CF_PACK_PNG/pack.json", MakePackJson("CF_PACK_PNG"));
    fs2.AddBinaryFile("root/CF_PACK_PNG/icons/CF_X.png", (byte[])pngBytes.Clone());

    var hash1 = DataHasher.ComputeHash(fs1, new[] { new PackForHash("CF_PACK_PNG", "root/CF_PACK_PNG") });
    var hash2 = DataHasher.ComputeHash(fs2, new[] { new PackForHash("CF_PACK_PNG", "root/CF_PACK_PNG") });
    AssertEqual(hash1, hash2, "Identical PNG bytes must hash identically across independent runs (byte-stable, no lossy text decode).");

    var changedBytes = (byte[])pngBytes.Clone();
    changedBytes[changedBytes.Length - 1] = 0x99;
    var fsChanged = new InMemoryFileSource();
    fsChanged.AddFile("root/CF_PACK_PNG/pack.json", MakePackJson("CF_PACK_PNG"));
    fsChanged.AddBinaryFile("root/CF_PACK_PNG/icons/CF_X.png", changedBytes);
    var hashChanged = DataHasher.ComputeHash(fsChanged, new[] { new PackForHash("CF_PACK_PNG", "root/CF_PACK_PNG") });
    Assert(hash1 != hashChanged, "Changing a single PNG byte must change the dataHash.");
});

Test("DataHasher: line-ending normalization is NOT applied to non-text (.png) extensions", () =>
{
    // If normalization incorrectly applied to .png, these two fixtures (one with a real CRLF pair, one
    // with it pre-collapsed to a single LF) would hash identically. They must NOT: a PNG's bytes are
    // opaque and 0x0D/0x0A are ordinary content bytes, not line endings.
    var withCrlf = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    var withLfOnly = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0A, 0x1A, 0x0A };

    var fsCrlf = new InMemoryFileSource();
    fsCrlf.AddFile("root/CF_PACK_BIN/pack.json", MakePackJson("CF_PACK_BIN"));
    fsCrlf.AddBinaryFile("root/CF_PACK_BIN/icons/x.png", withCrlf);

    var fsLf = new InMemoryFileSource();
    fsLf.AddFile("root/CF_PACK_BIN/pack.json", MakePackJson("CF_PACK_BIN"));
    fsLf.AddBinaryFile("root/CF_PACK_BIN/icons/x.png", withLfOnly);

    var hashCrlf = DataHasher.ComputeHash(fsCrlf, new[] { new PackForHash("CF_PACK_BIN", "root/CF_PACK_BIN") });
    var hashLf = DataHasher.ComputeHash(fsLf, new[] { new PackForHash("CF_PACK_BIN", "root/CF_PACK_BIN") });

    Assert(hashCrlf != hashLf,
        "A .png file's bytes must be hashed verbatim -- CRLF normalization must be scoped to the known-text extension set (.json/.md/.txt) only.");
});

// ---------------------------------------------------------------------
// 6. Parity-registration payload shape.
// ---------------------------------------------------------------------
Test("ParityRegistrationBuilder: payload includes pack ids + gameplay feature knobs (MP review B4)", () =>
{
    var result = baldursResult!;
    var payload = ParityRegistrationBuilder.Build(result, "ftk2mods.classforge", "0.1.0",
        enableRecipeEngine: true, enableTraitLoadoutInjection: false);

    AssertEqual("ftk2mods.classforge", payload.Guid, "guid");
    AssertEqual("0.1.0", payload.Version, "version");
    AssertEqual(result.DataHash, payload.DataHash, "dataHash");
    Assert(payload.EnabledFeatures.SequenceEqual(new[]
    {
        "CF_PACK_BALDURS",
        "feature:EnableRecipeEngine=true",
        "feature:EnableTraitLoadoutInjection=false"
    }), $"enabledFeatures should include the pack id and both gameplay feature knobs, got [{string.Join(",", payload.EnabledFeatures)}]");
    Assert(payload.EnabledFeatures.SequenceEqual(payload.EnabledFeatures.OrderBy(x => x, StringComparer.Ordinal)), "enabledFeatures must be sorted ordinally");
});

Test("ParityRegistrationBuilder: feature knob values flip the encoded string (MP review B4)", () =>
{
    var result = baldursResult!;
    var payload = ParityRegistrationBuilder.Build(result, "ftk2mods.classforge", "0.1.0",
        enableRecipeEngine: false, enableTraitLoadoutInjection: true);

    Assert(payload.EnabledFeatures.Contains("feature:EnableRecipeEngine=false"), "Expected feature:EnableRecipeEngine=false in " + string.Join(",", payload.EnabledFeatures));
    Assert(payload.EnabledFeatures.Contains("feature:EnableTraitLoadoutInjection=true"), "Expected feature:EnableTraitLoadoutInjection=true in " + string.Join(",", payload.EnabledFeatures));
    Assert(!payload.EnabledFeatures.Any(f => f.Contains("EnableClassSelectInjection")), "EnableClassSelectInjection is presentation-only and must NOT appear in enabledFeatures.");
    Assert(!payload.EnabledFeatures.Any(f => f.Contains("EnableIconFallback")), "EnableIconFallback is presentation-only and must NOT appear in enabledFeatures.");
});

// ---------------------------------------------------------------------
// 7. Merge-time adds-only enforcement against live (pre-existing) ids (MP review M0).
// ---------------------------------------------------------------------
Test("Merge: a pack entry colliding with a LIVE id is refused, not merged (MP review M0)", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_LIVE/pack.json", MakePackJson("CF_PACK_LIVE"));
    fs.AddFile("root/CF_PACK_LIVE/classes.json",
        "{\"KNIGHT\":{\"Stats\":{},\"Value\":1},\"CF_NEW_CLASS\":{\"Stats\":{},\"Value\":2}}");

    var loader = new PackLoader();
    var liveIds = new LiveIdSets(new[] { "KNIGHT" }, null, null);
    var result = loader.Load(fs, new[] { "root" }, null, liveIds);

    AssertEqual(1, result.MergePlan.Characters.Count, "Only the non-colliding class should merge");
    AssertEqual("CF_NEW_CLASS", result.MergePlan.Characters.Single().Id, "KNIGHT must not appear in the merge plan");

    var refusals = result.Findings.Where(f => f.Code == "CF_LIVE_ID_COLLISION").ToList();
    Assert(refusals.Count == 1, $"Expected 1 CF_LIVE_ID_COLLISION finding, got {refusals.Count}");
    Assert(refusals[0].Severity == FindingSeverity.Error, "A live-id collision must be an Error (loud), not a Warning");
    Assert(refusals[0].Message.Contains("KNIGHT"), "Finding should name the refused id: " + refusals[0].Message);
});

Test("Merge: pack-vs-pack collisions still resolve last-pack-wins alongside an unrelated live-id set (MP review M0)", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_ONE/pack.json", MakePackJson("CF_PACK_ONE", loadOrder: 0));
    fs.AddFile("root/CF_PACK_ONE/classes.json", "{\"CF_Y\":{\"Stats\":{},\"Value\":1}}");
    fs.AddFile("root/CF_PACK_TWO/pack.json", MakePackJson("CF_PACK_TWO", loadOrder: 10));
    fs.AddFile("root/CF_PACK_TWO/classes.json", "{\"CF_Y\":{\"Stats\":{},\"Value\":2}}");

    var loader = new PackLoader();
    var liveIds = new LiveIdSets(new[] { "SOME_OTHER_LIVE_ID" }, null, null);
    var result = loader.Load(fs, new[] { "root" }, null, liveIds);

    AssertEqual(1, result.MergePlan.Characters.Count, "Expected exactly one merged CF_Y entry");
    AssertEqual("CF_PACK_TWO", result.MergePlan.Characters.Single().SourcePackId, "Last-pack-wins must be unaffected by an unrelated live-id set");
    Assert(result.Findings.Any(f => f.Code == "CF_ID_COLLISION"), "The existing pack-vs-pack collision Finding must still fire");
    Assert(!result.Findings.Any(f => f.Code == "CF_LIVE_ID_COLLISION"), "No live-id collision should fire when neither id is live");
});

Test("Merge: a null live-id set (e.g. ClassForge.PackCheck) refuses nothing (MP review M0)", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_NOLIVE/pack.json", MakePackJson("CF_PACK_NOLIVE"));
    fs.AddFile("root/CF_PACK_NOLIVE/classes.json", "{\"KNIGHT\":{\"Stats\":{},\"Value\":1}}");

    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { "root" }); // liveIds omitted entirely

    AssertEqual(1, result.MergePlan.Characters.Count, "With no live-id set supplied, nothing is refused");
    Assert(!result.Findings.Any(f => f.Code == "CF_LIVE_ID_COLLISION"), "No live-id collision Finding should appear when liveIds is null");
});

// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

// ---------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------
static DiscoveredPack MakeDiscovered(string id, int loadOrder = 0, string[]? deps = null)
{
    var manifest = new PackManifest
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        LoadOrder = loadOrder,
        Dependencies = (deps ?? Array.Empty<string>()).ToList(),
        Enabled = true,
        RootDir = $"root/{id}"
    };
    return new DiscoveredPack(manifest, manifest.RootDir);
}

static string FindClassPacksDir()
{
    var dir = AppContext.BaseDirectory;
    for (int i = 0; i < 12; i++)
    {
        var candidate = Path.Combine(dir, "FTK2.ClassForge", "data", "ClassPacks");
        if (Directory.Exists(candidate)) return candidate;
        var parent = Directory.GetParent(dir);
        if (parent == null) break;
        dir = parent.FullName;
    }
    throw new DirectoryNotFoundException($"Could not locate FTK2.ClassForge/data/ClassPacks by walking up from {AppContext.BaseDirectory}");
}

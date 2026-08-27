using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassForge.Core;
using ClassForge.Core.IO;
using ClassForge.Core.Json;
using ClassForge.Core.Rng;
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
// 6. Parity-registration payload shape (P0.5: opt-OUT knob coverage).
//
// The two tests that used to live here encoded the OLD contract: "the payload contains the three
// hand-named bools". That contract is exactly the bug -- it asserted the presence of three knobs and
// said nothing about the other twenty, so it stayed green while [Combat] VenueGridPreset (which changes
// the arena tile count, and therefore AIHelper's ShuffleList draw count on the shared GameRandom stream)
// was invisible to parity. The replacements below assert the MECHANISM instead: every Gameplay knob is
// emitted, no Presentation knob is, the order is ordinal, the formatting is culture-invariant, and no
// bind site anywhere in the Plugin can bypass the registry.
// ---------------------------------------------------------------------

// The P0.5 classification of record. A knob added to the Plugin without a matching entry here fails
// "CFConfig: every knob's classification matches the P0.5 audit" below, in BOTH directions.
var expectedGameplay = new[]
{
    "Combat.RotateDioramas", "Combat.RotateDioramasInDungeons", "Combat.VenueGridPreset",
    "General.Enabled",
    // AiTargetingDrawNeutrality decides how many draws AIHelper.ForceAiDecision takes from the SHARED
    // stream, so two peers disagreeing about it desync on the first AI turn. Gameplay, unambiguously.
    "Multiplayer.AiTargetingDrawNeutrality",
    "Multiplayer.OnParityMismatch", "Multiplayer.RequireParityService",
    "Packs.AdditionalRoots",
    "Skills.DebugEncounterModifierChance", "Skills.EnableLootGrants", "Skills.EnableRecipeEngine",
    "Skills.EnableStatModifiers",
    "Traits.EnableTraitLoadoutInjection",
    // All 19 [Trainer] knobs.
    "Trainer.CapPartnerMaxHp", "Trainer.CaptureRejectStunKits", "Trainer.CharmUnlockOrder",
    "Trainer.DefaultPartnerNicknames", "Trainer.EnableCapture", "Trainer.EnableCharmProgression",
    "Trainer.EnableFocusFireOrders", "Trainer.EnablePartnerAutonomy",
    "Trainer.EnablePartnerCommandTendency", "Trainer.EnablePartnerNicknames",
    "Trainer.EnablePartnerPanel", "Trainer.EnablePartnerPersistence", "Trainer.FocusFireRequireMarker",
    "Trainer.PartnerMaxHpByStage", "Trainer.RevivePartnersInTown", "Trainer.SecondCharmLevel",
    "Trainer.ShapePartnerTendency", "Trainer.StarterCharmLine", "Trainer.ThirdCharmLevel",
};

var expectedPresentation = new[]
{
    "Combat.ClearFoliageOverGrid", "Combat.VenueCameraRig", "Combat.VenueCameraZoomOut",
    "Combat.VenueTileBorderOpacity",
    "General.VerboseLogging",
    "Skills.DebugLogCombatRandomDraws",
    "UI.EnableClassSelectInjection", "UI.EnableIconFallback", "UI.EnableSkillDisplay",
};

Test("ParityRegistrationBuilder: EVERY Gameplay knob reaches the payload, no Presentation knob does (P0.5)", () =>
{
    ParityKnobRegistry.Reset();
    foreach (var name in expectedGameplay)
    {
        var dot = name.IndexOf('.');
        ParityKnobRegistry.Declare(name.Substring(0, dot), name.Substring(dot + 1), ParityClass.Gameplay, () => "v");
    }
    foreach (var name in expectedPresentation)
    {
        var dot = name.IndexOf('.');
        ParityKnobRegistry.Declare(name.Substring(0, dot), name.Substring(dot + 1), ParityClass.Presentation, () => "v");
    }

    var payload = ParityRegistrationBuilder.Build(baldursResult!, "ftk2mods.classforge", "0.1.0");

    // Wire format is UNCHANGED -- still feature:<Name>=<value> -- so DevKit.ParityComparer needs no work.
    foreach (var name in expectedGameplay)
        Assert(payload.EnabledFeatures.Contains("feature:" + name + "=v"),
            $"Gameplay knob '{name}' is missing from the parity payload -- a peer could differ on it and still compare as Match.");

    foreach (var name in expectedPresentation)
        Assert(!payload.EnabledFeatures.Any(f => f.StartsWith("feature:" + name + "=", StringComparison.Ordinal)),
            $"Presentation knob '{name}' leaked into the parity payload -- a cosmetic preference must never refuse a join.");

    Assert(payload.EnabledFeatures.Contains("CF_PACK_BALDURS"), "the enabled pack ids must still be in the payload");
    AssertEqual(expectedGameplay.Length + 1, payload.EnabledFeatures.Length,
        "payload should be exactly the pack id plus one entry per Gameplay knob");
    AssertEqual(baldursResult!.DataHash, payload.DataHash, "dataHash");
    ParityKnobRegistry.Reset();
});

Test("ParityRegistrationBuilder: payload is ORDINAL-sorted, not culture-sorted (P0.5)", () =>
{
    ParityKnobRegistry.Reset();
    // '_' (0x5F) sorts BEFORE 'a' (0x61) ordinally; most culture-aware comparers ignore or reorder
    // leading punctuation, so a culture-sorted payload puts these the other way round. Two peers under
    // different locales would then hand DevKit two differently-ordered arrays for identical settings.
    ParityKnobRegistry.Declare("Z", "aKnob", ParityClass.Gameplay, () => "1");
    ParityKnobRegistry.Declare("Z", "_Knob", ParityClass.Gameplay, () => "1");
    ParityKnobRegistry.Declare("Z", "BKnob", ParityClass.Gameplay, () => "1");

    var payload = ParityRegistrationBuilder.Build(baldursResult!, "g", "v");
    var knobEntries = payload.EnabledFeatures.Where(f => f.StartsWith("feature:Z.", StringComparison.Ordinal)).ToArray();

    Assert(knobEntries.SequenceEqual(new[] { "feature:Z.BKnob=1", "feature:Z._Knob=1", "feature:Z.aKnob=1" }),
        "expected ordinal order [B, _, a], got [" + string.Join(", ", knobEntries) + "]");
    Assert(payload.EnabledFeatures.SequenceEqual(payload.EnabledFeatures.OrderBy(x => x, StringComparer.Ordinal)),
        "the whole payload must be ordinal-sorted");
    ParityKnobRegistry.Reset();
});

Test("ParityValue: knob values are culture-INVARIANT (P0.5)", () =>
{
    var previous = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
        // de-DE renders a decimal point as a comma. A peer on this locale must still emit "0.5", or two
        // identically-configured peers diverge on nothing but their operating-system language.
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

        AssertEqual("0.5", ParityValue.Format(0.5f), "float must be invariant + round-trippable");
        AssertEqual("0.5", ParityValue.Format(0.5d), "double must be invariant");
        AssertEqual("-1", ParityValue.Format(-1), "int must be invariant");
        AssertEqual("true", ParityValue.Format(true), "bool must be lower-case 'true' (unchanged wire text)");
        AssertEqual("false", ParityValue.Format(false), "bool must be lower-case 'false'");
        AssertEqual("", ParityValue.Format(null), "null must be empty, never a crash");

        // Commas would make the payload ambiguous when DevKit joins it for display; both peers apply the
        // same substitution, so this can never manufacture or hide a divergence.
        AssertEqual("C:/a;C:/b", ParityValue.Format("C:/a,C:/b"), "commas must be escaped deterministically");
        AssertEqual("a b", ParityValue.Format("a\nb"), "newlines must be flattened deterministically");

        // And the same value formatted under an invariant culture must be byte-identical.
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        AssertEqual("0.5", ParityValue.Format(0.5f), "the same float under InvariantCulture must match de-DE's output");
    }
    finally
    {
        System.Globalization.CultureInfo.CurrentCulture = previous;
    }
});

Test("ParityRegistrationBuilder: a knob whose reader throws is emitted as <unreadable>, never dropped (P0.5)", () =>
{
    ParityKnobRegistry.Reset();
    ParityKnobRegistry.Declare("Skills", "Broken", ParityClass.Gameplay, () => throw new InvalidOperationException("boom"));
    var payload = ParityRegistrationBuilder.Build(baldursResult!, "g", "v");
    // Silently dropping it would restore the exact "invisible knob" failure P0.5 exists to remove.
    Assert(payload.EnabledFeatures.Contains("feature:Skills.Broken=<unreadable>"),
        "a throwing value-reader must still occupy its slot in the payload, got [" + string.Join(", ", payload.EnabledFeatures) + "]");
    ParityKnobRegistry.Reset();
});

// ---------------------------------------------------------------------
// 6b. Source scan: no knob can bypass the registry.
//
// The wrapper only covers bind sites that call it, so a test -- not a convention -- is what keeps the
// next knob from being added the way the first twenty were. Four TrainerPartner*.cs files are owned by
// another workstream and still call config.Bind directly; they are classified declaratively in
// CFConfig.ExternalClassifications, and that table is the ONLY sanctioned bypass.
// ---------------------------------------------------------------------

Test("CFConfig: no raw Config.Bind anywhere in the Plugin escapes the registry (P0.5)", () =>
{
    var plugin = FindPluginSrcDir();
    var external = ReadExternalClassifications(plugin);
    var offenders = new List<string>();

    foreach (var file in Directory.GetFiles(plugin, "*.cs", SearchOption.AllDirectories))
    {
        if (Path.GetFileName(file) == "CFConfig.cs") continue;                 // the wrapper itself
        if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;

        var text = File.ReadAllText(file);
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(text,
                     // The (?<!\w) look-behind excludes `CFConfig.Bind(` -- the wrapper -- while still
                     // matching a qualified `Instance.Config.Bind(`, since '.' is not a word character.
                     @"(?<!\w)[Cc]onfig\.Bind\(\s*""([^""]+)""\s*,\s*""([^""]+)"""))
        {
            var name = m.Groups[1].Value + "|" + m.Groups[2].Value;
            if (!external.ContainsKey(name))
                offenders.Add($"{Path.GetFileName(file)}: [{m.Groups[1].Value}] {m.Groups[2].Value}");
        }
    }

    Assert(offenders.Count == 0,
        "These bind sites bypass CFConfig AND are not in CFConfig.ExternalClassifications, so their knobs " +
        "would be invisible to the parity handshake. Route them through CFConfig.Bind with an explicit " +
        "ParityClass, or classify them in the table:\n  " + string.Join("\n  ", offenders));
});

Test("CFConfig: every knob's classification matches the P0.5 audit (P0.5)", () =>
{
    var plugin = FindPluginSrcDir();
    var actual = ReadAllClassifications(plugin);

    var expected = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var n in expectedGameplay) expected[n] = "Gameplay";
    foreach (var n in expectedPresentation) expected[n] = "Presentation";

    var problems = new List<string>();
    foreach (var pair in actual)
    {
        if (!expected.TryGetValue(pair.Key, out var want))
            problems.Add($"{pair.Key} is classified {pair.Value} in the Plugin but is not in the P0.5 audit list " +
                         "(add it there, and decide deliberately -- over-inclusion costs a false refusal, " +
                         "under-inclusion costs a desync)");
        else if (want != pair.Value)
            problems.Add($"{pair.Key}: audit says {want}, the Plugin says {pair.Value}");
    }
    foreach (var pair in expected)
        if (!actual.ContainsKey(pair.Key))
            problems.Add($"{pair.Key} is in the P0.5 audit ({pair.Value}) but no bind site or table entry declares it");

    Assert(problems.Count == 0, string.Join("\n  ", problems));
});

Test("CFConfig: the desync-potent knobs the old three-bool payload missed are Gameplay (P0.5)", () =>
{
    var actual = ReadAllClassifications(FindPluginSrcDir());

    // The headline case: VenueGridPreset substitutes the combat arena map (8/12/24 tiles a side). Tile
    // count drives list.Count in AIHelper, which drives ShuffleList's draw count -- GameRandom.ShuffleList
    // takes exactly _list.Count draws from the SHARED stream. A preset mismatch desyncs on the first AI
    // turn, and the pre-P0.5 payload reported "Match".
    AssertEqual("Gameplay", actual["Combat.VenueGridPreset"], "[Combat] VenueGridPreset");

    // Same argument one step removed: the diorama selection decides WHICH arena is built, and
    // VenueGridPatches' own measured note records that two of the three shipped defaults "came up 8 tiles
    // a side" contrary to the asset inventory -- i.e. diorama choice and tile count are NOT independent.
    AssertEqual("Gameplay", actual["Combat.RotateDioramas"], "[Combat] RotateDioramas");
    AssertEqual("Gameplay", actual["Combat.RotateDioramasInDungeons"], "[Combat] RotateDioramasInDungeons");

    // A peer loading an extra pack root has different content behind an identical DataHash over the
    // standard roots.
    AssertEqual("Gameplay", actual["Packs.AdditionalRoots"], "[Packs] AdditionalRoots");

    // Fail-closed enforcement is itself parity-relevant: one peer shutting features off while the other
    // does not IS the asymmetric execution parity exists to prevent.
    AssertEqual("Gameplay", actual["Multiplayer.RequireParityService"], "[Multiplayer] RequireParityService");
    AssertEqual("Gameplay", actual["Multiplayer.OnParityMismatch"], "[Multiplayer] OnParityMismatch");
});

Test("CFConfig: the per-pack [Packs] <id>.Enabled knob is registered as Gameplay (P0.5)", () =>
{
    var text = File.ReadAllText(Path.Combine(FindPluginSrcDir(), "ClassForgePlugin.cs"));
    var i = text.IndexOf(".Enabled\", true,", StringComparison.Ordinal);
    Assert(i > 0, "could not find the per-pack knob bind site in ClassForgePlugin.cs");
    var window = text.Substring(Math.Max(0, i - 400), Math.Min(700, text.Length - Math.Max(0, i - 400)));
    // Interpolated, so the source scan above cannot read its key -- assert its shape directly instead.
    Assert(window.Contains("CFConfig.Bind("), "the per-pack knob must be bound through CFConfig");
    Assert(window.Contains("ParityClass.Gameplay"),
        "a pack switched off on ONE peer means that peer simulates from different merged Configs");
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
// 8. Encounter Modifiers (M-EM1): statuses.json + modifiers.json loader/validation/merge-plan (spec §12.5).
// ---------------------------------------------------------------------

Test("Encounter Modifiers: statuses.json + modifiers.json parse and merge cleanly (authored order preserved)", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TESTMOD/pack.json", MakePackJson("CF_PACK_TESTMOD"));
    fs.AddFile("root/CF_PACK_TESTMOD/statuses.json", """
    {
      "STATUS_CF_TM_ONE": { "Type": "BUFF", "Duration": -1, "TickFrequency": 1,
        "TickOverworld": false, "TickCombat": false, "TickExpire": false,
        "TileSync": false, "GroupSync": false, "Passives": [], "AddProperties": [],
        "Stats": { "DEF": 1 }, "CustomStats": {} },
      "STATUS_CF_TM_TWO": { "Type": "DEBUFF", "Duration": -1, "TickFrequency": 1,
        "TickOverworld": false, "TickCombat": false, "TickExpire": false,
        "TileSync": false, "GroupSync": false, "Passives": [], "AddProperties": [],
        "Stats": { "DEF": -1 }, "CustomStats": {} }
    }
    """);
    fs.AddFile("root/CF_PACK_TESTMOD/modifiers.json", """
    {
      "SchemaVersion": "1.0",
      "Selection": { "Recipe": "SKILL_CF_TM_SELECT" },
      "Modifiers": [
        { "Id": "ZETA", "Weight": 3, "Status": "STATUS_CF_TM_TWO" },
        { "Id": "ALPHA", "Weight": 7, "Status": "STATUS_CF_TM_ONE" }
      ]
    }
    """);

    var result = new PackLoader().Load(fs, new[] { "root" });

    var errors = result.Findings.Where(f => f.Severity == FindingSeverity.Error).ToList();
    Assert(errors.Count == 0, "Unexpected Error findings: " + string.Join(" | ", errors.Select(e => e.ToString())));

    AssertEqual(2, result.MergePlan.StatusEffects.Count, "StatusEffects count");
    Assert(result.MergePlan.StatusEffects.Any(m => m.Id == "STATUS_CF_TM_ONE"), "STATUS_CF_TM_ONE should be merged");
    Assert(result.MergePlan.StatusEffects.Any(m => m.Id == "STATUS_CF_TM_TWO"), "STATUS_CF_TM_TWO should be merged");

    AssertEqual(1, result.MergePlan.ModifierTables.Count, "One modifiers.json table expected");
    var table = result.MergePlan.ModifierTables[0];
    AssertEqual("SKILL_CF_TM_SELECT", table.SelectionRecipe, "Selection.Recipe");
    AssertEqual("1.0", table.SchemaVersion, "SchemaVersion");
    AssertEqual(2, table.Modifiers.Count, "Modifiers count");
    // Authored array order is load-bearing (spec §6.3 weighted-pick walk order) -- ZETA before ALPHA,
    // NOT re-sorted alphabetically or by weight.
    Assert(table.Modifiers.Select(m => m.Id).SequenceEqual(new[] { "ZETA", "ALPHA" }),
        "Modifiers authored order must be preserved, got: " + string.Join(",", table.Modifiers.Select(m => m.Id)));
});

Test("Encounter Modifiers: a non-STATUS_CF_-prefixed status id produces a CF_STATUS_ID_PREFIX warning but still merges", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TM_PREFIX/pack.json", MakePackJson("CF_PACK_TM_PREFIX"));
    fs.AddFile("root/CF_PACK_TM_PREFIX/statuses.json", """
    { "MYSTATUS_BAD": { "Type": "BUFF", "Duration": -1, "TickFrequency": 1,
        "TickOverworld": false, "TickCombat": false, "TickExpire": false,
        "TileSync": false, "GroupSync": false, "Passives": [], "AddProperties": [],
        "Stats": {}, "CustomStats": {} } }
    """);

    var result = new PackLoader().Load(fs, new[] { "root" });

    var warnings = result.Findings.Where(f => f.Code == "CF_STATUS_ID_PREFIX").ToList();
    Assert(warnings.Count == 1, $"Expected 1 CF_STATUS_ID_PREFIX warning, got {warnings.Count}");
    Assert(warnings[0].Severity == FindingSeverity.Warning, "Id-convention mismatch must be a Warning, not an Error (mirrors CF_TRAIT_PREFIX)");
    Assert(result.MergePlan.StatusEffects.Any(m => m.Id == "MYSTATUS_BAD"), "The status must still merge despite the naming warning");
});

Test("Encounter Modifiers: an unknown Type is an Error and the whole status entry is dropped", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TM_BADTYPE/pack.json", MakePackJson("CF_PACK_TM_BADTYPE"));
    fs.AddFile("root/CF_PACK_TM_BADTYPE/statuses.json", """
    { "STATUS_CF_BAD_TYPE": { "Type": "NOT_A_REAL_TYPE", "Duration": -1, "TickFrequency": 1,
        "TickOverworld": false, "TickCombat": false, "TickExpire": false,
        "TileSync": false, "GroupSync": false, "Passives": [], "AddProperties": [],
        "Stats": {}, "CustomStats": {} } }
    """);

    var result = new PackLoader().Load(fs, new[] { "root" });

    var errors = result.Findings.Where(f => f.Code == "CF_STATUS_TYPE_UNKNOWN").ToList();
    Assert(errors.Count == 1, $"Expected 1 CF_STATUS_TYPE_UNKNOWN error, got {errors.Count}");
    Assert(errors[0].Severity == FindingSeverity.Error, "Unresolvable Type must be an Error");
    Assert(!result.MergePlan.StatusEffects.Any(m => m.Id == "STATUS_CF_BAD_TYPE"), "A status with an unresolvable Type must not merge");
});

Test("Encounter Modifiers: a Passives entry that resolves to neither a same-pack recipe nor SKILL_ is dropped, the status entry survives", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TM_PASSIVE/pack.json", MakePackJson("CF_PACK_TM_PASSIVE"));
    fs.AddFile("root/CF_PACK_TM_PASSIVE/statuses.json", """
    { "STATUS_CF_TM_PASSIVE": { "Type": "BUFF", "Duration": -1, "TickFrequency": 1,
        "TickOverworld": false, "TickCombat": false, "TickExpire": false,
        "TileSync": false, "GroupSync": false, "Passives": ["BOGUS_NOT_A_SKILL"], "AddProperties": [],
        "Stats": {}, "CustomStats": {} } }
    """);

    var result = new PackLoader().Load(fs, new[] { "root" });

    var errors = result.Findings.Where(f => f.Code == "CF_STATUS_PASSIVE_UNRESOLVED").ToList();
    Assert(errors.Count == 1, $"Expected 1 CF_STATUS_PASSIVE_UNRESOLVED error, got {errors.Count}");
    var op = result.MergePlan.StatusEffects.SingleOrDefault(m => m.Id == "STATUS_CF_TM_PASSIVE");
    Assert(op != null, "The status entry itself must survive (only the bad Passives entry is dropped)");
    AssertEqual(0, op!.Value.GetStringArray("Passives").Count, "The unresolved Passives entry must be dropped from the array");
});

Test("Encounter Modifiers: M0 -- a statuses.json entry colliding with a live StatusEffects id is refused", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TM_LIVE/pack.json", MakePackJson("CF_PACK_TM_LIVE"));
    fs.AddFile("root/CF_PACK_TM_LIVE/statuses.json", """
    { "STATUS_CF_LIVE_TEST": { "Type": "BUFF", "Duration": -1, "TickFrequency": 1,
        "TickOverworld": false, "TickCombat": false, "TickExpire": false,
        "TileSync": false, "GroupSync": false, "Passives": [], "AddProperties": [],
        "Stats": {}, "CustomStats": {} } }
    """);

    var liveIds = new LiveIdSets(null, null, null, new[] { "STATUS_CF_LIVE_TEST" });
    var result = new PackLoader().Load(fs, new[] { "root" }, null, liveIds);

    AssertEqual(0, result.MergePlan.StatusEffects.Count, "The live-colliding status must be refused, not merged");
    var refusals = result.Findings.Where(f => f.Code == "CF_LIVE_ID_COLLISION").ToList();
    Assert(refusals.Count == 1, $"Expected 1 CF_LIVE_ID_COLLISION finding, got {refusals.Count}");
    Assert(refusals[0].Severity == FindingSeverity.Error, "A live-id collision must be an Error");
    Assert(refusals[0].Message.Contains("StatusEffect"), "Finding should label the category: " + refusals[0].Message);
});

Test("Encounter Modifiers: a modifiers.json Status ref that resolves nowhere is a dangling-ref Error and the entry is dropped", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TM_DANGLE/pack.json", MakePackJson("CF_PACK_TM_DANGLE"));
    fs.AddFile("root/CF_PACK_TM_DANGLE/statuses.json", "{}");
    fs.AddFile("root/CF_PACK_TM_DANGLE/modifiers.json", """
    { "SchemaVersion": "1.0", "Selection": { "Recipe": "SKILL_CF_TM_SELECT" },
      "Modifiers": [ { "Id": "GHOST", "Weight": 1, "Status": "STATUS_CF_DOES_NOT_EXIST_ANYWHERE" } ] }
    """);

    var result = new PackLoader().Load(fs, new[] { "root" });

    var errors = result.Findings.Where(f => f.Code == "CF_MODIFIER_STATUS_DANGLING").ToList();
    Assert(errors.Count == 1, $"Expected 1 CF_MODIFIER_STATUS_DANGLING error, got {errors.Count}");
    Assert(errors[0].Severity == FindingSeverity.Error, "A dangling Status ref must be an Error");
    var table = result.MergePlan.ModifierTables.Single();
    Assert(!table.Modifiers.Any(m => m.Id == "GHOST"), "A modifier with a dangling Status ref must be dropped");
});

Test("Encounter Modifiers: a modifiers.json Status ref matching a plausible vanilla status name is deferred (Info), not an Error", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TM_VANILLA/pack.json", MakePackJson("CF_PACK_TM_VANILLA"));
    fs.AddFile("root/CF_PACK_TM_VANILLA/statuses.json", "{}");
    fs.AddFile("root/CF_PACK_TM_VANILLA/modifiers.json", """
    { "SchemaVersion": "1.0", "Selection": { "Recipe": "SKILL_CF_TM_SELECT" },
      "Modifiers": [ { "Id": "VANILLA_CURSE", "Weight": 1, "Status": "CURSE" } ] }
    """);

    var result = new PackLoader().Load(fs, new[] { "root" });

    Assert(!result.Findings.Any(f => f.Code == "CF_MODIFIER_STATUS_DANGLING"), "A vanilla-plausible ref must not be flagged dangling");
    var deferred = result.Findings.Where(f => f.Code == "CF_MODIFIER_STATUS_VANILLA_DEFERRED").ToList();
    Assert(deferred.Count == 1, $"Expected 1 CF_MODIFIER_STATUS_VANILLA_DEFERRED note, got {deferred.Count}");
    Assert(deferred[0].Severity == FindingSeverity.Info, "The deferred vanilla-id check is Info-level, not blocking");
    var table = result.MergePlan.ModifierTables.Single();
    Assert(table.Modifiers.Any(m => m.Id == "VANILLA_CURSE"), "The modifier still parses through (resolution just deferred to the Plugin, M-EM2)");
});

Test("Encounter Modifiers: duplicate Modifier Id is an Error and only the first occurrence is kept", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TM_DUPE/pack.json", MakePackJson("CF_PACK_TM_DUPE"));
    fs.AddFile("root/CF_PACK_TM_DUPE/statuses.json", """
    { "STATUS_CF_TM_DUPE": { "Type": "BUFF", "Duration": -1, "TickFrequency": 1,
        "TickOverworld": false, "TickCombat": false, "TickExpire": false,
        "TileSync": false, "GroupSync": false, "Passives": [], "AddProperties": [],
        "Stats": {}, "CustomStats": {} } }
    """);
    fs.AddFile("root/CF_PACK_TM_DUPE/modifiers.json", """
    { "SchemaVersion": "1.0", "Selection": { "Recipe": "SKILL_CF_TM_SELECT" },
      "Modifiers": [
        { "Id": "DUPE", "Weight": 1, "Status": "STATUS_CF_TM_DUPE" },
        { "Id": "DUPE", "Weight": 2, "Status": "STATUS_CF_TM_DUPE" }
      ] }
    """);

    var result = new PackLoader().Load(fs, new[] { "root" });

    var errors = result.Findings.Where(f => f.Code == "CF_MODIFIER_DUP_ID").ToList();
    Assert(errors.Count == 1, $"Expected 1 CF_MODIFIER_DUP_ID error, got {errors.Count}");
    Assert(errors[0].Severity == FindingSeverity.Error, "Duplicate Modifier Id must be an Error");
    var table = result.MergePlan.ModifierTables.Single();
    AssertEqual(1, table.Modifiers.Count(m => m.Id == "DUPE"), "Only the first occurrence of a duplicate Id should be kept");
    AssertEqual(1, table.Modifiers.Single(m => m.Id == "DUPE").Weight, "The kept occurrence should be the first one authored (Weight 1)");
});

Test("Encounter Modifiers: Weight < 1 is an Error and the entry is dropped", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_TM_WEIGHT/pack.json", MakePackJson("CF_PACK_TM_WEIGHT"));
    fs.AddFile("root/CF_PACK_TM_WEIGHT/statuses.json", """
    { "STATUS_CF_TM_WEIGHT": { "Type": "BUFF", "Duration": -1, "TickFrequency": 1,
        "TickOverworld": false, "TickCombat": false, "TickExpire": false,
        "TileSync": false, "GroupSync": false, "Passives": [], "AddProperties": [],
        "Stats": {}, "CustomStats": {} } }
    """);
    fs.AddFile("root/CF_PACK_TM_WEIGHT/modifiers.json", """
    { "SchemaVersion": "1.0", "Selection": { "Recipe": "SKILL_CF_TM_SELECT" },
      "Modifiers": [
        { "Id": "ZEROWEIGHT", "Weight": 0, "Status": "STATUS_CF_TM_WEIGHT" },
        { "Id": "NEGWEIGHT", "Weight": -3, "Status": "STATUS_CF_TM_WEIGHT" },
        { "Id": "OKWEIGHT", "Weight": 1, "Status": "STATUS_CF_TM_WEIGHT" }
      ] }
    """);

    var result = new PackLoader().Load(fs, new[] { "root" });

    var errors = result.Findings.Where(f => f.Code == "CF_MODIFIER_WEIGHT_RANGE").ToList();
    Assert(errors.Count == 2, $"Expected 2 CF_MODIFIER_WEIGHT_RANGE errors (0 and -3), got {errors.Count}");
    Assert(errors.All(e => e.Severity == FindingSeverity.Error), "Weight < 1 must be an Error");
    var table = result.MergePlan.ModifierTables.Single();
    AssertEqual(1, table.Modifiers.Count, "Only the Weight>=1 entry should survive");
    AssertEqual("OKWEIGHT", table.Modifiers.Single().Id, "The surviving entry should be OKWEIGHT");
});

Test("Encounter Modifiers: dataHash changes when a modifiers.json Weight changes (and when statuses.json changes)", () =>
{
    var fsBase = new InMemoryFileSource();
    fsBase.AddFile("root/CF_PACK_TM_HASH/pack.json", MakePackJson("CF_PACK_TM_HASH"));
    fsBase.AddFile("root/CF_PACK_TM_HASH/statuses.json",
        "{\"STATUS_CF_TM_HASH\":{\"Type\":\"BUFF\",\"Stats\":{}}}");
    fsBase.AddFile("root/CF_PACK_TM_HASH/modifiers.json",
        "{\"SchemaVersion\":\"1.0\",\"Selection\":{\"Recipe\":\"SKILL_CF_TM_SELECT\"}," +
        "\"Modifiers\":[{\"Id\":\"ONE\",\"Weight\":5,\"Status\":\"STATUS_CF_TM_HASH\"}]}");

    var fsWeightChanged = new InMemoryFileSource();
    fsWeightChanged.AddFile("root/CF_PACK_TM_HASH/pack.json", MakePackJson("CF_PACK_TM_HASH"));
    fsWeightChanged.AddFile("root/CF_PACK_TM_HASH/statuses.json",
        "{\"STATUS_CF_TM_HASH\":{\"Type\":\"BUFF\",\"Stats\":{}}}");
    fsWeightChanged.AddFile("root/CF_PACK_TM_HASH/modifiers.json",
        "{\"SchemaVersion\":\"1.0\",\"Selection\":{\"Recipe\":\"SKILL_CF_TM_SELECT\"}," +
        "\"Modifiers\":[{\"Id\":\"ONE\",\"Weight\":6,\"Status\":\"STATUS_CF_TM_HASH\"}]}"); // only the Weight differs

    var fsStatusChanged = new InMemoryFileSource();
    fsStatusChanged.AddFile("root/CF_PACK_TM_HASH/pack.json", MakePackJson("CF_PACK_TM_HASH"));
    fsStatusChanged.AddFile("root/CF_PACK_TM_HASH/statuses.json",
        "{\"STATUS_CF_TM_HASH\":{\"Type\":\"BUFF\",\"Stats\":{\"DEF\":1}}}"); // statuses.json content differs
    fsStatusChanged.AddFile("root/CF_PACK_TM_HASH/modifiers.json",
        "{\"SchemaVersion\":\"1.0\",\"Selection\":{\"Recipe\":\"SKILL_CF_TM_SELECT\"}," +
        "\"Modifiers\":[{\"Id\":\"ONE\",\"Weight\":5,\"Status\":\"STATUS_CF_TM_HASH\"}]}");

    var baseResult = new PackLoader().Load(fsBase, new[] { "root" });
    var weightChangedResult = new PackLoader().Load(fsWeightChanged, new[] { "root" });
    var statusChangedResult = new PackLoader().Load(fsStatusChanged, new[] { "root" });

    Assert(!string.IsNullOrEmpty(baseResult.DataHash), "Base dataHash must not be empty");
    Assert(baseResult.DataHash != weightChangedResult.DataHash,
        "Changing a modifiers.json Weight must change the pack dataHash (both files fold into the existing hash-every-file rule automatically)");
    Assert(baseResult.DataHash != statusChangedResult.DataHash,
        "Changing statuses.json content must change the pack dataHash");
});

// ---------------------------------------------------------------------
// 9. CF_PACK_ENCOUNTER_MODIFIERS end-to-end fixture (real pack on disk), mirrors the CF_PACK_BALDURS test.
// ---------------------------------------------------------------------
var encModDir = Path.Combine(classPacksDir, "CF_PACK_ENCOUNTER_MODIFIERS");

Test("CF_PACK_ENCOUNTER_MODIFIERS: loads cleanly with zero Error findings", () =>
{
    var fs = new FileSystemFileSource();
    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { classPacksDir }, id => string.Equals(id, "CF_PACK_ENCOUNTER_MODIFIERS", StringComparison.Ordinal));

    var errors = result.Findings.Where(f => f.Severity == FindingSeverity.Error).ToList();
    Assert(errors.Count == 0, "Unexpected Error findings: " + string.Join(" | ", errors.Select(e => e.ToString())));
    AssertEqual(1, result.EnabledOrderedPacks.Count, $"Expected 1 enabled pack, got {result.EnabledOrderedPacks.Count}");
    AssertEqual("CF_PACK_ENCOUNTER_MODIFIERS", result.EnabledOrderedPacks[0].Id, "Enabled pack id mismatch");

    AssertEqual(10, result.MergePlan.StatusEffects.Count, "Expected all 10 shipped statuses to merge");
    AssertEqual(1, result.MergePlan.ModifierTables.Count, "Expected exactly one modifiers.json table");
    var table = result.MergePlan.ModifierTables[0];
    AssertEqual(10, table.Modifiers.Count, "Expected all 10 shipped modifier rows");
    AssertEqual(85, table.Modifiers.Sum(m => m.Weight), "Shipped weights must sum to 85 (spec §1.1)");

    // Authored order (spec §3.2, load-bearing for the weighted-pick walk, §6.3).
    var expectedOrder = new[]
    {
        "ARMORED", "RESISTANT", "FRENZIED", "SWIFT", "VETERAN", "WEALTHY", "CURSED",
        "REGENERATING", "GLASSCANNON", "TREASUREGUARDED"
    };
    Assert(table.Modifiers.Select(m => m.Id).SequenceEqual(expectedOrder),
        "Modifiers authored order must match spec §3.2 exactly, got: " + string.Join(",", table.Modifiers.Select(m => m.Id)));

    Assert(!result.Findings.Any(f => f.Code == "CF_STATUS_ID_PREFIX"), "All shipped status ids should be STATUS_CF_-prefixed");
    Assert(!result.Findings.Any(f => f.Code == "CF_MODIFIER_STATUS_DANGLING"), "Every shipped Status ref should resolve within the pack");
    Assert(!result.Findings.Any(f => f.Code == "CF_STATUS_PASSIVE_UNRESOLVED"), "CURSED/REGENERATING Passives must resolve against the pack's own skillrecipes.json");
});

// ---------------------------------------------------------------------
// 10. SkillDisplay.SelectCustomSkillRows — which class Passives get a custom UI row in the
//     character-customization panel (the game itself only renders eSkills-parseable passives).
// ---------------------------------------------------------------------
Test("SkillDisplay: selects non-vanilla passives with display names, preserving Passives order", () =>
{
    var passives = new[] { "SKILL_BLACKHOLE", "SKILL_CF_WARRIOR_RAGE_BUILD", "SKILL_REFOCUS", "SKILL_CF_WARRIOR_RAGE_CONSUME" };
    var vanilla = new HashSet<string>(new[] { "SKILL_BLACKHOLE", "SKILL_REFOCUS" }, StringComparer.Ordinal);

    var rows = SkillDisplay.SelectCustomSkillRows(passives, vanilla.Contains, _ => true);

    Assert(rows.SequenceEqual(new[] { "SKILL_CF_WARRIOR_RAGE_BUILD", "SKILL_CF_WARRIOR_RAGE_CONSUME" }),
        "Expected the two custom passives in authored order, got: " + string.Join(",", rows));
});

Test("SkillDisplay: excludes vanilla passives even when they have display names", () =>
{
    var rows = SkillDisplay.SelectCustomSkillRows(
        new[] { "SKILL_BLACKHOLE" }, _ => true, _ => true);
    AssertEqual(0, rows.Count, "A vanilla-renderable passive must never get a duplicate custom row");
});

Test("SkillDisplay: excludes custom passives that have no display name (nothing legible to render)", () =>
{
    var rows = SkillDisplay.SelectCustomSkillRows(
        new[] { "SKILL_CF_NO_LOC" }, _ => false, _ => false);
    AssertEqual(0, rows.Count, "A custom passive without a name key would render as a raw id — must be skipped");
});

Test("SkillDisplay: null or empty passives yield an empty list", () =>
{
    AssertEqual(0, SkillDisplay.SelectCustomSkillRows(null, _ => false, _ => true).Count, "null passives");
    AssertEqual(0, SkillDisplay.SelectCustomSkillRows(Array.Empty<string>(), _ => false, _ => true).Count, "empty passives");
});

Test("SkillDisplay: duplicate passive ids produce a single row", () =>
{
    var rows = SkillDisplay.SelectCustomSkillRows(
        new[] { "SKILL_CF_X", "SKILL_CF_X" }, _ => false, _ => true);
    AssertEqual(1, rows.Count, "Duplicate passive entries must not produce duplicate UI rows");
});

Test("SkillDisplay: real CF_PACK_EOR_CLASSES data — every class's custom passives are selectable", () =>
{
    var fs = new FileSystemFileSource();
    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { classPacksDir }, id => string.Equals(id, "CF_PACK_EOR_CLASSES", StringComparison.Ordinal));

    var loc = result.MergePlan.Localization;
    int classesWithRows = 0;
    foreach (var op in result.MergePlan.Characters)
    {
        var passives = op.Value.Get("Passives").AsArray.Select(n => n.AsString).ToList();
        var rows = SkillDisplay.SelectCustomSkillRows(passives, p => !p.StartsWith("SKILL_CF_", StringComparison.Ordinal), loc.ContainsKey);
        if (rows.Count > 0) classesWithRows++;
        foreach (var row in rows)
            Assert(loc.ContainsKey("UI_ENCYCLOPEDIA_" + row), $"{op.Id}: row {row} has no UI_ENCYCLOPEDIA_ description key");
    }
    AssertEqual(28, classesWithRows, "28 of 31 EOR classes ship a custom signature skill (DUELIST/PALADIN/THIEF are parked)");
});

// ---------------------------------------------------------------------
// 10b. The same rendered-row assertions for CF_PACK_ORIGINALS.
//
// The test above hardcoded CF_PACK_EOR_CLASSES, so CF_PACK_ORIGINALS never had its rendered rows
// checked at all -- which is exactly why missing UI_ENCYCLOPEDIA_ copy survived there. These tests
// are pack-id-agnostic in shape: every rendered row must have a title AND a description, and every
// ARM_ item must have a description, or the pack ships content that renders blank in game.
// ---------------------------------------------------------------------
Test("CF_PACK_ORIGINALS: every rendered skill row has a UI_ENCYCLOPEDIA_ description key", () =>
{
    var fs = new FileSystemFileSource();
    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { classPacksDir }, id => string.Equals(id, "CF_PACK_ORIGINALS", StringComparison.Ordinal));

    var loc = result.MergePlan.Localization;
    var missing = new List<string>();
    int classesWithRows = 0;

    foreach (var op in result.MergePlan.Characters)
    {
        var passivesNode = op.Value.Get("Passives");
        if (passivesNode == null) continue;
        var passives = passivesNode.AsArray.Select(n => n.AsString).ToList();
        var rows = SkillDisplay.SelectCustomSkillRows(
            passives, pp => !pp.StartsWith("SKILL_CF_", StringComparison.Ordinal), loc.ContainsKey);
        if (rows.Count > 0) classesWithRows++;
        foreach (var row in rows)
            if (!loc.ContainsKey("UI_ENCYCLOPEDIA_" + row))
                missing.Add(op.Id + " -> " + row);
    }

    Assert(classesWithRows > 0, "CF_PACK_ORIGINALS must render at least one custom skill row");
    Assert(missing.Count == 0,
        missing.Count + " rendered row(s) would show an empty tooltip/encyclopedia body -- add "
        + "UI_ENCYCLOPEDIA_<SKILL> to the pack's localization for: "
        + string.Join(" | ", missing.Distinct().OrderBy(x => x, StringComparer.Ordinal)));
});

Test("CF_PACK_ORIGINALS: the Trainer needs far more skill rows than the templates' vanilla-era budget", () =>
{
    var fs = new FileSystemFileSource();
    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { classPacksDir }, id => string.Equals(id, "CF_PACK_ORIGINALS", StringComparison.Ordinal));

    var loc = result.MergePlan.Localization;
    var trainer = result.MergePlan.Characters.FirstOrDefault(op => op.Id == "CF_ORIG_TRAINER");
    Assert(trainer != null, "CF_ORIG_TRAINER must be present in CF_PACK_ORIGINALS");

    var passives = trainer!.Value.Get("Passives").AsArray.Select(n => n.AsString).ToList();
    var rows = SkillDisplay.SelectCustomSkillRows(
        passives, pp => !pp.StartsWith("SKILL_CF_", StringComparison.Ordinal), loc.ContainsKey);

    // The regression this locks in: SkillDisplayPatches used to assume "our worst case is 4 rows",
    // walk the spare rows and DROP everything past them with one generic warning. The Trainer alone
    // needs far more, so any code that budgets a fixed number of rows is wrong by construction.
    Assert(rows.Count > 4,
        "CF_ORIG_TRAINER renders " + rows.Count + " custom skill rows; the display path must never "
        + "assume a fixed row budget (see SkillDisplayPatches overflow handling)");
});

Test("CF_PACK_ORIGINALS: every ARM_ item has a <ITEM>_DESCRIPTION localization key", () =>
{
    var fs = new FileSystemFileSource();
    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { classPacksDir }, id => string.Equals(id, "CF_PACK_ORIGINALS", StringComparison.Ordinal));

    var loc = result.MergePlan.Localization;
    var armIds = result.MergePlan.Things
        .Select(op => op.Id)
        .Where(id => id.StartsWith("ARM_", StringComparison.Ordinal))
        .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

    Assert(armIds.Count > 0, "CF_PACK_ORIGINALS must ship at least one ARM_ item");

    var missingName = armIds.Where(id => !loc.ContainsKey(id)).ToList();
    var missingDesc = armIds.Where(id => !loc.ContainsKey(id + "_DESCRIPTION")).ToList();

    Assert(missingName.Count == 0,
        missingName.Count + " ARM_ item(s) have no title key and would show a raw id on the item card: "
        + string.Join(", ", missingName));
    Assert(missingDesc.Count == 0,
        missingDesc.Count + " of " + armIds.Count + " ARM_ item(s) have no <ITEM>_DESCRIPTION key and "
        + "would show a blank item card body: " + string.Join(", ", missingDesc));
});

// ---------------------------------------------------------------------
// 11. visualfallbacks.json — pack item id -> donor Thing id, the equipment-visual remap surface
//     (task #8: pack items without dEquipmentPrefab records NRE CharacterVisualHelper.VisualReEquip).
// ---------------------------------------------------------------------
Test("VisualFallbacks: parse + merge round-trip; non-string values are an Error and dropped", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_VF/pack.json", MakePackJson("CF_PACK_VF"));
    fs.AddFile("root/CF_PACK_VF/items.json", "{\"CF_ITEM_A\":{\"Class\":\"WHIP\"}}");
    fs.AddFile("root/CF_PACK_VF/visualfallbacks.json",
        "{\"CF_ITEM_A\":\"WHIP_MILITIA_BASIC_00\",\"CF_ITEM_BAD\":42}");

    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { "root" }, id => true);

    AssertEqual("WHIP_MILITIA_BASIC_00", result.MergePlan.VisualFallbacks["CF_ITEM_A"], "fallback merged");
    Assert(!result.MergePlan.VisualFallbacks.ContainsKey("CF_ITEM_BAD"), "non-string value dropped");
    Assert(result.Findings.Any(f => f.Code == "CF_VISUALFALLBACK_SHAPE" && f.Severity == FindingSeverity.Error),
        "non-string value produced an Error finding");
});

Test("VisualFallbacks: last pack wins across packs", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_VF1/pack.json", MakePackJson("CF_PACK_VF1", loadOrder: 0));
    fs.AddFile("root/CF_PACK_VF1/visualfallbacks.json", "{\"CF_ITEM_A\":\"DONOR_ONE\"}");
    fs.AddFile("root/CF_PACK_VF2/pack.json", MakePackJson("CF_PACK_VF2", loadOrder: 1));
    fs.AddFile("root/CF_PACK_VF2/visualfallbacks.json", "{\"CF_ITEM_A\":\"DONOR_TWO\"}");

    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { "root" }, id => true);
    AssertEqual("DONOR_TWO", result.MergePlan.VisualFallbacks["CF_ITEM_A"], "later pack wins");
});

Test("VisualFallbacks: donor resolvable via live ids or merged pack Things — else Warning", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_VF/pack.json", MakePackJson("CF_PACK_VF"));
    fs.AddFile("root/CF_PACK_VF/items.json", "{\"CF_ITEM_A\":{\"Class\":\"WHIP\"},\"CF_ITEM_B\":{\"Class\":\"AXE\"},\"CF_ITEM_C\":{\"Class\":\"BOW\"}}");
    fs.AddFile("root/CF_PACK_VF/visualfallbacks.json",
        "{\"CF_ITEM_A\":\"LIVE_DONOR\",\"CF_ITEM_B\":\"CF_ITEM_A\",\"CF_ITEM_C\":\"NO_SUCH_DONOR\"}");

    var live = new LiveIdSets(null, new HashSet<string>(StringComparer.Ordinal) { "LIVE_DONOR" }, null, null);
    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { "root" }, id => true, live);

    var dangling = result.Findings.Where(f => f.Code == "CF_VISUALFALLBACK_DANGLING").ToList();
    AssertEqual(1, dangling.Count, "exactly the unresolvable donor warns");
    Assert(dangling[0].Message.Contains("NO_SUCH_DONOR"), "warning names the missing donor");
    Assert(dangling.All(f => f.Severity == FindingSeverity.Warning), "dangling donor is a Warning, not an Error");
});

Test("CF_PACK_EOR_CLASSES: visualfallbacks cover exactly the 31 starter items", () =>
{
    var fs = new FileSystemFileSource();
    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { classPacksDir }, id => string.Equals(id, "CF_PACK_EOR_CLASSES", StringComparison.Ordinal));

    var itemsPath = Path.Combine(classPacksDir, "CF_PACK_EOR_CLASSES", "items.json");
    var itemIds = JsonParser.Parse(File.ReadAllText(itemsPath)).AsObjectMembers.Select(m => m.Key)
        .OrderBy(x => x, StringComparer.Ordinal).ToList();

    var fallbackKeys = result.MergePlan.VisualFallbacks.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
    AssertEqual(31, fallbackKeys.Count, "31 starter fallbacks");
    Assert(fallbackKeys.SequenceEqual(itemIds), "fallback keys must be exactly the pack's items.json ids");
    Assert(result.MergePlan.VisualFallbacks.Values.All(v => !string.IsNullOrEmpty(v)), "every donor non-empty");
    Assert(!result.Findings.Any(f => f.Code == "CF_VISUALFALLBACK_DANGLING"),
        "in-pack run: donors are live-game ids, but with no live set supplied they must not warn either");
});

Test("VisualFallbacks: a key may be a pack Thing OR a live Things id — anything else is a Warning", () =>
{
    var fs = new InMemoryFileSource();
    fs.AddFile("root/CF_PACK_VF/pack.json", MakePackJson("CF_PACK_VF"));
    fs.AddFile("root/CF_PACK_VF/items.json", "{\"CF_ITEM_A\":{\"Class\":\"WHIP\"}}");
    fs.AddFile("root/CF_PACK_VF/visualfallbacks.json",
        "{\"CF_ITEM_A\":\"LIVE_DONOR\",\"ARM_LIVE_ITEM\":\"LIVE_DONOR\",\"NO_SUCH_KEY\":\"LIVE_DONOR\"}");

    var live = new LiveIdSets(null,
        new HashSet<string>(StringComparer.Ordinal) { "LIVE_DONOR", "ARM_LIVE_ITEM" }, null, null);
    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { "root" }, id => true, live);

    var foreignKeys = result.Findings.Where(f => f.Code == "CF_VISUALFALLBACK_KEY_DANGLING").ToList();
    AssertEqual(1, foreignKeys.Count, "only the key that is neither pack Thing nor live id warns");
    Assert(foreignKeys[0].Message.Contains("NO_SUCH_KEY"), "warning names the dangling key");
    Assert(foreignKeys.All(f => f.Severity == FindingSeverity.Warning), "dangling key is a Warning");

    // Offline gate: with no live set the check must stay silent (PackCheck/tests have no game).
    var offline = loader.Load(fs, new[] { "root" }, id => true);
    Assert(!offline.Findings.Any(f => f.Code == "CF_VISUALFALLBACK_KEY_DANGLING"),
        "no live set supplied: key check is skipped, not spammed");
});

Test("CF_PACK_ARMORY_VISUALS: fallback-only pack covers the shipped Armory catalog", () =>
{
    var fs = new FileSystemFileSource();
    var loader = new PackLoader();
    var result = loader.Load(fs, new[] { classPacksDir }, id => string.Equals(id, "CF_PACK_ARMORY_VISUALS", StringComparison.Ordinal));

    AssertEqual(1, result.EnabledOrderedPacks.Count, "pack discovered and enabled");
    AssertEqual(0, result.MergePlan.Things.Count, "ships no Things of its own");
    AssertEqual(502, result.MergePlan.VisualFallbacks.Count, "one fallback per shipped Armory item");
    Assert(result.MergePlan.VisualFallbacks.Keys.All(k => k.StartsWith("ARM_", StringComparison.Ordinal)),
        "every key is an ARM_ id");
    Assert(!result.MergePlan.VisualFallbacks.Keys.Any(k => k.Contains("EOR_STARTER")),
        "starter fallbacks live in CF_PACK_EOR_CLASSES, not here");
    Assert(result.MergePlan.VisualFallbacks.Values.All(v => !string.IsNullOrEmpty(v)), "every donor non-empty");
});

// ---------------------------------------------------------------------

const ulong CFRNG_KNOWN_ENTITYKEY_HASH        = 0xE56783A52FBCEDA5UL; // FNV-1a 64 of EntityKey(3) under domain tag "EntityKey/2"

// =====================================================================
// Cross-peer identity core (ClassForge.Core.Rng).
//
// This folder used to also hold a bespoke PRNG side-stream (CFRandom / CFSeedInputs / SplitMix64). It was
// REMOVED 2026-08-26 and its tests with it: the engine already rolls from the replicated stream, so a
// second generator was a mechanism this codebase does not use and must not grow. What survives is the part
// that is still load-bearing -- EntityKey, the ordinal-based peer-stable identity that replaces Entity.Guid
// everywhere a value is ordered, hashed or seeded. See EntityKey's own remarks.
// =====================================================================

// ---------------------------------------------------------------------
// EntityKey -- the cross-peer stable identity.
// ---------------------------------------------------------------------

Test("EntityKey: StableValue of a known key is a hardcoded constant (guards against string.GetHashCode)", () =>
{
    // string.GetHashCode() is salted per process on .NET Core, so if it ever creeps into this path the
    // constant below stops matching between two runs of this very test binary.
    var key = new EntityKey(3);
    AssertEqual(CFRNG_KNOWN_ENTITYKEY_HASH, key.StableValue, "EntityKey.StableValue drifted from its pinned constant");
    AssertEqual("E3", key.ToString(), "EntityKey.ToString() shape changed");
    AssertEqual(key.StableValue, EntityKey.FromCombatRosterIndex(3).StableValue, "FromCombatRosterIndex disagreed with the ctor");
});

Test("EntityKey: StableValue is identical across repeated construction in-process", () =>
{
    var a = new EntityKey(3);
    var b = EntityKey.FromCombatRosterIndex(3);
    AssertEqual(a.StableValue, b.StableValue, "Two equal keys built via different entry points hashed differently");
    AssertEqual(a.GetHashCode(), b.GetHashCode(), "GetHashCode differed for equal keys");
    Assert(a == b, "operator== said two equal keys differ");
});

Test("EntityKey: negative control -- keys differing only in Ordinal do not collide", () =>
{
    // Ordinal is now the WHOLE key, so this is the only negative control there is to run -- and it is the
    // one that matters: two roster slots must never share a stream.
    var a = new EntityKey(3);
    var b = new EntityKey(4);
    Assert(a.StableValue != b.StableValue, "Ordinals 3 and 4 collided in StableValue");
    Assert(!a.Equals(b), "Ordinals 3 and 4 compared equal");
    Assert(a.ToString() != b.ToString(), "Ordinals 3 and 4 produced the same ToString()");
    Assert(a != b, "operator!= said two different keys are equal");
});

Test("EntityKey: every ordinal over a realistic roster range maps to a distinct StableValue", () =>
{
    // A venue roster is at most a few dozen characters plus up to 32 tiles; negatives are included because
    // an unfound index (List.IndexOf -> -1) must not alias slot 0 or any other slot.
    var seen = new Dictionary<ulong, int>();
    for (int i = -8; i <= 128; i++)
    {
        ulong v = new EntityKey(i).StableValue;
        Assert(!seen.ContainsKey(v), $"ordinals {seen.GetValueOrDefault(v)} and {i} collided in StableValue");
        seen[v] = i;
    }
    // The domain tag must actually namespace the value: a bare FNV-1a of the ordinal bytes must not match.
    Assert(new EntityKey(3).StableValue != StableHash.AbsorbInt32(StableHash.Fnv1aOffsetBasis, 3),
        "EntityKey.StableValue is an untagged FNV of the ordinal -- the domain tag is not being absorbed");
});

// ---------------------------------------------------------------------
// AI targeting draw counts -- the invariant that keeps a co-op session alive.
//
// FTK2 co-op is deterministic lockstep with AI recomputed on every peer, so what kills a session is two
// peers taking a different NUMBER of draws from the shared CombatState.Random -- not different values.
// AIHelper.cs:589's gate forks that count on the AI tendency, and AIHelper.cs:516-551 skips the gate
// outright when AIComponent.PriorityTargets (what TrainerFocusFire writes into) already supplied a
// position. ClassForge moves BOTH of those inputs, so it owes the game a constant.
// ---------------------------------------------------------------------

Test("AI draws: the UNPATCHED gate really does fork on tendency (the bug being fixed exists)", () =>
{
    // Guard against a vacuous suite: if this ever stops failing to be constant, the fix below is
    // asserting nothing.
    AssertEqual(0, AiTargetingDraws.VanillaGateDraws(true, false), "NONE must cost zero draws");
    AssertEqual(0, AiTargetingDraws.VanillaGateDraws(false, true), "a strict tendency must cost zero draws");
    AssertEqual(1, AiTargetingDraws.VanillaGateDraws(false, false), "an ordinary tendency must cost one draw");
    Assert(AiTargetingDraws.VanillaGateDraws(true, false) != AiTargetingDraws.VanillaGateDraws(false, false),
        "vanilla draw count does NOT depend on tendency -- then there is nothing to fix and the model is wrong");
});

Test("AI draws: a Focus Fire order really does skip the gate in the UNPATCHED game", () =>
{
    // PriorityTargets hit => GetPreferredTarget never called => the whole gate costs nothing.
    AssertEqual(0, AiTargetingDraws.VanillaTargetingDraws(false, false, false),
        "an ordered ally must cost zero draws before the fix");
    AssertEqual(1, AiTargetingDraws.VanillaTargetingDraws(true, false, false),
        "the same ally unordered must cost one -- that difference IS the desync");
});

Test("AI draws: NEUTRALISED targeting costs exactly one draw for EVERY input combination", () =>
{
    // Exhaustive over the whole input space: 2 x 2 x 2. This is the acceptance invariant --
    // same operation, same draw count, regardless of tendency, of strictness, and of whether an
    // order was issued.
    foreach (bool reached in new[] { true, false })
        foreach (bool isNone in new[] { true, false })
            foreach (bool isStrict in new[] { true, false })
                AssertEqual(1, AiTargetingDraws.NeutralisedTargetingDraws(reached, isNone, isStrict),
                    $"reached={reached} none={isNone} strict={isStrict} did not cost exactly one draw");
});

Test("AI draws: the compensation is exactly the complement of vanilla, never a second draw", () =>
{
    // A compensation that fired on top of a gate that already drew would be just as fatal as one that
    // never fired -- 2 draws is as wrong as 0.
    foreach (bool isNone in new[] { true, false })
        foreach (bool isStrict in new[] { true, false })
        {
            int vanilla = AiTargetingDraws.VanillaGateDraws(isNone, isStrict);
            int comp = AiTargetingDraws.GateCompensationDraws(isNone, isStrict);
            Assert(comp == 0 || comp == 1, $"compensation must be 0 or 1, saw {comp}");
            AssertEqual(1, vanilla + comp, $"none={isNone} strict={isStrict}: vanilla+compensation must be 1");
        }

    AssertEqual(0, AiTargetingDraws.SkippedGateCompensationDraws(true),
        "no compensation when the gate actually ran -- that would double-draw");
    AssertEqual(1, AiTargetingDraws.SkippedGateCompensationDraws(false),
        "one compensation when a PriorityTargets hit skipped the gate");
});

Test("AI draws: an ORDERED actor and an UNORDERED actor consume the same count, at every tendency", () =>
{
    // The Focus Fire acceptance case stated directly: issuing an order must be invisible to the stream.
    foreach (bool isNone in new[] { true, false })
        foreach (bool isStrict in new[] { true, false })
            AssertEqual(
                AiTargetingDraws.NeutralisedTargetingDraws(true, isNone, isStrict),
                AiTargetingDraws.NeutralisedTargetingDraws(false, isNone, isStrict),
                $"none={isNone} strict={isStrict}: ordering an ally changed its draw count");
});


// ---------------------------------------------------------------------
// W1-A / W1-F: encounter identity must be a function of REPLICATED state and of nothing else.
//
// The bug: VenueGridPatches picked the battlefield with `names[_dioramaTurn++ % names.Count]`, a
// process-lifetime static. It was never reset at run start, session start or join, and the overworld and
// dungeon paths shared it. Two peers whose process-local fight counts differed -- anyone who played solo
// first, rejoined after a crash, or joined mid-session -- loaded a DIFFERENT diorama, hence a different
// VenueGrid, hence a different tile count, hence a different GetTargetableTiles().Count, hence a different
// ShuffleList draw count off the SHARED stream on the first AI turn. RotateDioramas ships ON with three
// venues, so this was live and default-on.
// ---------------------------------------------------------------------

Test("Encounter identity: the same replicated inputs give the same answer, however many times it is called", () =>
{
    // This is the whole difference from the counter it replaces, stated executably: no hidden state.
    ulong first = EncounterIdentity.Hash("P", 12345, 7, "MAP_1", "FOREST", null);
    for (int i = 0; i < 200; i++)
        AssertEqual(first.ToString(), EncounterIdentity.Hash("P", 12345, 7, "MAP_1", "FOREST", null).ToString(),
            $"call #{i} disagreed with call #0 -- something in the derivation is stateful");

    string firstText = EncounterIdentity.Text(first);
    for (int i = 0; i < 200; i++)
        AssertEqual(firstText, EncounterIdentity.Text(EncounterIdentity.Hash("P", 12345, 7, "MAP_1", "FOREST", null)),
            "the ENCOUNTER_GUID token text is not stable across repeated resolution");
});

Test("Encounter identity: two peers with DIFFERENT local fight histories pick the same battlefield", () =>
{
    // The reachable case, simulated. Peer A has fought 41 times in this process (solo session earlier,
    // then a rejoin); peer B has just started and is at 0. Under the old counter they index a 3-name
    // rotation at 41 % 3 = 2 and 0 % 3 = 0 -- two different arenas, two different tile counts.
    string[] names = { "CASTLETHRONE", "HARAZUEL_ROOF", "OMUS_CASTLE_BOSS" };

    int oldPeerA = 41 % names.Length;
    int oldPeerB = 0 % names.Length;
    Assert(oldPeerA != oldPeerB, "the counter model must actually diverge, or this test proves nothing");

    // The replicated model: identical inputs (both peers are in the same encounter), so identical pick,
    // whatever either process did before.
    ulong h = EncounterIdentity.Hash("CF_BATTLEFIELD_ROTATION_V1", 987654, 12, "MAP_1", "SWAMP", null);
    int newPeerA = EncounterIdentity.IndexOf(h, names.Length, "GRASSLAND_A", -1);
    int newPeerB = EncounterIdentity.IndexOf(h, names.Length, "GRASSLAND_A", -1);
    AssertEqual(newPeerA, newPeerB, "two peers in the same encounter picked different battlefields");
    Assert(newPeerA >= 0 && newPeerA < names.Length, $"index {newPeerA} is outside the rotation");
});

Test("Encounter identity: the pick does move -- across encounters, across rooms, and across purposes", () =>
{
    // A constant answer would also be peer-stable, and useless. Assert the discriminators are live.
    const int count = 3;
    ulong roundA = EncounterIdentity.Hash("CF_BATTLEFIELD_ROTATION_V1", 11, 1, "MAP_1", "FOREST", null);
    ulong roundB = EncounterIdentity.Hash("CF_BATTLEFIELD_ROTATION_V1", 11, 2, "MAP_1", "FOREST", null);
    Assert(roundA != roundB, "the overworld round count does not reach the hash");

    Assert(EncounterIdentity.Hash("CF_BATTLEFIELD_ROTATION_V1", 11, 1, "MAP_1", "FOREST", null)
        != EncounterIdentity.Hash("CF_BATTLEFIELD_ROTATION_V1", 12, 1, "MAP_1", "FOREST", null),
        "MapGenSeed does not reach the hash");
    Assert(EncounterIdentity.Hash("CF_BATTLEFIELD_ROTATION_V1", 11, 1, "MAP_1", "FOREST", null)
        != EncounterIdentity.Hash("CF_BATTLEFIELD_ROTATION_V1", 11, 1, "MAP_2", "FOREST", null),
        "ActiveMapID does not reach the hash");
    Assert(EncounterIdentity.Hash("CF_BATTLEFIELD_ROTATION_V1", 11, 1, "MAP_1", "FOREST", null)
        != EncounterIdentity.Hash("CF_BATTLEFIELD_ROTATION_V1", 11, 1, "MAP_1", "SWAMP", null),
        "BiomeName does not reach the hash");
    Assert(EncounterIdentity.Hash("CF_BATTLEFIELD_ROTATION_V1", 11, 1, "MAP_1", "FOREST", null)
        != EncounterIdentity.Hash("CF_BATTLEFIELD_ROTATION_V1", 11, 1, "MAP_1", "FOREST", "CRYPT"),
        "DungeonName does not reach the hash");

    // Two rooms on one dungeon floor share every field above; the room's index in the SERIALIZED
    // OngoingVenues list is what separates them, and it must actually do so.
    var byRoom = new HashSet<int>();
    for (int room = 0; room < 8; room++) byRoom.Add(EncounterIdentity.IndexOf(roundA, count, "DUNGEON_ROOM", room));
    Assert(byRoom.Count > 1, "every room on a floor got the same battlefield -- roomIndex is not reaching the pick");

    // Different purposes must not alias onto one another (the battlefield pick and the ENCOUNTER_GUID
    // token read the same five fields).
    Assert(EncounterIdentity.Hash("CF_BATTLEFIELD_ROTATION_V1", 11, 1, "MAP_1", "FOREST", null)
        != EncounterIdentity.Hash("CF_ENCOUNTER_IDENTITY_V1", 11, 1, "MAP_1", "FOREST", null),
        "the purpose tag does not reach the hash");
});

Test("Encounter identity: the whole rotation is reachable, and no index is ever out of range", () =>
{
    for (int count = 1; count <= 5; count++)
    {
        var seen = new HashSet<int>();
        for (int round = 0; round < 300; round++)
        {
            ulong h = EncounterIdentity.Hash("CF_BATTLEFIELD_ROTATION_V1", 4242, round, "MAP_1", "FOREST", null);
            int i = EncounterIdentity.IndexOf(h, count, "GRASSLAND_A", -1);
            Assert(i >= 0 && i < count, $"count={count} round={round}: index {i} is out of range");
            seen.Add(i);
        }
        AssertEqual(count, seen.Count, $"count={count}: some configured battlefield can never be chosen");
    }
    AssertEqual(0, EncounterIdentity.IndexOf(1234UL, 0, "x", 0), "an empty choice list must yield 0, not throw");
    AssertEqual(0, EncounterIdentity.IndexOf(1234UL, -3, "x", 0), "a negative count must yield 0, not throw");
});

Test("Encounter identity: string inputs are length-prefixed, so field boundaries cannot collide", () =>
{
    // ("AB","C") vs ("A","BC") absorbed without a length prefix produce the same accumulator, i.e. two
    // different encounters sharing one battlefield pick. StableHash.AbsorbString prefixes; prove it holds
    // through this composition too.
    Assert(EncounterIdentity.Hash("P", 1, 1, "AB", "C", null) != EncounterIdentity.Hash("P", 1, 1, "A", "BC", null),
        "adjacent string fields collide -- the length prefix is not being applied");
    Assert(EncounterIdentity.Hash("P", 1, 1, null, "X", null) != EncounterIdentity.Hash("P", 1, 1, "", "X", null),
        "a null field and an empty field must not hash alike");
});

// ---------------------------------------------------------------------
// W1-B / W1-C: source-scan guards on AiDrawNeutrality's gate.
//
// These are source scans for the same reason the CFConfig knob audit above is: ClassForge.Plugin
// references FTK2/BepInEx/Unity and cannot be instantiated in a test, but the property being guarded is
// a one-line edit away from silently regressing, and it is a property of the SOURCE.
// ---------------------------------------------------------------------

Test("AI draws: the neutrality gate does not read a per-peer parity latch (W1-B)", () =>
{
    var text = File.ReadAllText(Path.Combine(FindPluginSrcDir(), "AiDrawNeutrality.cs"));
    int open = text.IndexOf("private static bool Active", StringComparison.Ordinal);
    Assert(open > 0, "could not find AiDrawNeutrality.Active");
    int end = text.IndexOf("internal static bool PreferredTargetHookInstalled", open, StringComparison.Ordinal);
    Assert(end > open, "could not delimit AiDrawNeutrality.Active");
    var body = text.Substring(open, end - open);

    Assert(!body.Contains("FeaturesActive"),
        "AiDrawNeutrality.Active reads ClassForgePlugin.FeaturesActive again. That is " +
        "Enabled && !ParityBridge.Blocked && !ParityBridge.SafeMode, and BOTH latches are decided from " +
        "LOCAL evidence -- the reachable case is one peer alone lacking FTK2.DevKit, which SafeModes that " +
        "peer only. Gating the compensating draw on a per-peer latch makes the DRAW COUNT a function of a " +
        "per-peer latch, which is the exact failure this file exists to remove. Gate on the " +
        "[Multiplayer] AiTargetingDrawNeutrality knob (ParityClass.Gameplay, so it is IN the parity " +
        "payload and a divergence refuses the join) plus the master Enabled switch, and nothing else.");
    Assert(body.Contains("_hardDisabled"),
        "the hard-disable latch must gate Active, or a half-resolved feature keeps compensating");
});

Test("AI draws: every fallback path hard-disables instead of skipping one decision (W1-C)", () =>
{
    var text = File.ReadAllText(Path.Combine(FindPluginSrcDir(), "AiDrawNeutrality.cs"));

    // One draw is all it takes: after it the two shared streams are permanently offset, every later roll
    // in the session disagrees, and because CombatState is [JsonIgnore] on GameRunData nothing reports it.
    // A per-decision silent revert is therefore the single worst available behaviour -- both wrong AND
    // invisible. A consistent, announced local disable is strictly better.
    foreach (var site in new[]
    {
        "AIHelper._strictTendencies could not be read",
        "GetPreferredTarget prefix is not installed",
        "the ForceAiDecision postfix threw",
        "the GetPreferredTarget prefix threw",
    })
        Assert(text.Contains(site), $"the '{site}' fallback site is gone or reworded -- re-check it still latches");

    int calls = 0;
    for (int i = text.IndexOf("HardDisable(", StringComparison.Ordinal); i >= 0;
         i = text.IndexOf("HardDisable(", i + 1, StringComparison.Ordinal)) calls++;
    Assert(calls >= 5, $"expected the definition plus 4 fallback call sites of HardDisable, found {calls}");

    Assert(text.Contains("ParityBridge.AnnounceUnilateralDegrade"),
        "a hard disable in an ONLINE session must be announced in-game: it is a one-sided change to this " +
        "peer's draw counts that no detector can see, so silence is the failure mode being fixed");
});

Test("Parity: a unilateral latch is announced as a divergence, not as graceful degradation (W1-B)", () =>
{
    var text = File.ReadAllText(Path.Combine(FindPluginSrcDir(), "ParityBridge.cs"));
    int def = text.IndexOf("internal static void AnnounceUnilateralDegrade", StringComparison.Ordinal);
    Assert(def > 0, "could not find AnnounceUnilateralDegrade");

    int calls = 0;
    for (int i = text.IndexOf("AnnounceUnilateralDegrade(", StringComparison.Ordinal); i >= 0;
         i = text.IndexOf("AnnounceUnilateralDegrade(", i + 1, StringComparison.Ordinal)) calls++;
    Assert(calls >= 3, $"expected the definition plus at least 2 latch call sites, found {calls}");

    var body = text.Substring(def);
    Assert(body.Contains("IsOnlineMultiplayer"),
        "the announcement must be gated on the session actually being online -- offline, a latch really " +
        "IS graceful degradation and must stay quiet");
});

// ---------------------------------------------------------------------
// W1-A, second half: the battlefield picker itself must not have grown a counter back.
// ---------------------------------------------------------------------

Test("Battlefield rotation: the pick reads replicated identity, never a process-local counter (W1-A)", () =>
{
    var raw = File.ReadAllText(Path.Combine(FindPluginSrcDir(), "VenueGridPatches.cs"));
    // Doc comments quote the OLD expression verbatim so the hazard stays on record; scan code only.
    var text = string.Join("\n", raw.Split('\n').Where(l => !l.TrimStart().StartsWith("///")));

    Assert(!text.Contains("names[_dioramaTurn"),
        "the battlefield is being indexed by a process-lifetime counter again. Two peers whose local " +
        "fight counts differ then load different dioramas -> different VenueGrid -> different tile count " +
        "-> different ShuffleList draw count off the SHARED stream on the first AI turn. Resetting the " +
        "counter is NOT a fix: a peer joining mid-session still starts at 0 while the host is at 7.");

    Assert(text.Contains("ReplicatedEncounterKey.IndexOf"),
        "the battlefield pick must come from ReplicatedEncounterKey");

    // Both rotation paths -- overworld VenueDirector.Initialize and dungeon _loadNextDioramas -- must go
    // through the one picker. They used to share the static counter, which is how a dungeon fight could
    // shift the overworld rotation.
    int picks = 0;
    for (int i = text.IndexOf("ChooseBattlefield(", StringComparison.Ordinal); i >= 0;
         i = text.IndexOf("ChooseBattlefield(", i + 1, StringComparison.Ordinal)) picks++;
    Assert(picks >= 3, $"expected the definition plus the overworld and dungeon call sites, found {picks}");
});

Test("STATE_HASH_CHANCE: the ENCOUNTER_GUID token no longer resolves to a raw guid (W1-F)", () =>
{
    var text = File.ReadAllText(Path.Combine(FindPluginSrcDir(), "Recipes", "GameAdapters.cs"));
    int prop = text.IndexOf("public string EncounterGuid", StringComparison.Ordinal);
    Assert(prop > 0, "could not find CombatContextAdapter.EncounterGuid");
    var body = text.Substring(prop, Math.Min(600, text.Length - prop));

    Assert(!body.Contains("adv.EncounterGUID"),
        "ENCOUNTER_GUID resolves to AdventureState.EncounterGUID again. That field holds an Entity.Guid, " +
        "minted per peer by Guid.NewGuid() (the map is regenerated from a replicated SEED, not shipped " +
        "entity-by-entity), so it differs on every peer for the same encounter. STATE_HASH_CHANCE takes " +
        "ZERO draws, so the divergence never perturbs the shared stream and the vendor's " +
        "GameRandomNextInt probe cannot see it; the vendor's desync MD5 rewrites guids to occurrence " +
        "ordinals before hashing, so it cannot see it either. NO detector of any kind would catch this.");
    Assert(body.Contains("ReplicatedEncounterKey.Text"),
        "ENCOUNTER_GUID must resolve to the peer-stable encounter key, as the other three *_GUID tokens " +
        "resolve to PeerOrder.KeyOf");
});

// ---------------------------------------------------------------------
// Status icon donors: the table must never fall silently behind statuses.json.
//
// The freeze this guards against (live, 2026-08-26): StatusVisualPatches.Donors carried ONE entry
// while CF_PACK_ORIGINALS/statuses.json had grown to five. The four unmapped ids resolved to null in
// dObjectHelper.Index.dStatusEffect, and CombatTimelineViewHelper2._refreshPortraitVisuals
// dereferences that record unguarded -- the NRE escaped the scheduled _progressRound callback, so the
// round never finished progressing and it was retried every frame (~60 NREs/sec, Player.log +94 KB/s).
// CombatVisualNullGuards.AddStatusIcon_Prefix now makes that merely iconless; this test is what makes
// "merely iconless" a DELIBERATE choice rather than an unnoticed regression.
// ---------------------------------------------------------------------
Test("Status donors: every pack status has a donor or is recorded as intentionally iconless", () =>
{
    var (donors, iconless) = ReadStatusDonorTables(FindPluginSrcDir());
    var declared = new HashSet<string>(donors.Keys, StringComparer.OrdinalIgnoreCase);
    declared.UnionWith(iconless);

    var authored = ReadAuthoredStatusIds(classPacksDir);
    Assert(authored.Count > 0, "found no statuses.json ids at all -- the scan is broken, not the data");
    Console.WriteLine($"  authored statuses={authored.Count} donors={donors.Count} iconless={iconless.Count}");

    var undeclared = authored.Where(id => !declared.Contains(id)).OrderBy(x => x, StringComparer.Ordinal).ToList();
    Assert(undeclared.Count == 0,
        "pack statuses with neither a donor nor an intentionally-iconless record: " +
        string.Join(", ", undeclared) + ". Add each to StatusVisualPatches.Donors (mapping onto a vanilla " +
        "status of the same Type, which must resolve in dObjectHelper.Index.dStatusEffect -- being present " +
        "in StatusEffects.json is NOT sufficient) or, if it is meant to render no icon, to " +
        "StatusVisualPatches.IntentionallyIconless.");

    // The reverse direction: a stale entry means the table is describing content that no longer exists,
    // which is how it stops being a trustworthy record of what is covered.
    var stale = declared.Where(id => !authored.Contains(id)).OrderBy(x => x, StringComparer.Ordinal).ToList();
    Assert(stale.Count == 0,
        "StatusVisualPatches names statuses that no statuses.json authors any more: " + string.Join(", ", stale));

    // A status cannot be both mapped and deliberately invisible.
    var both = donors.Keys.Where(iconless.Contains).OrderBy(x => x, StringComparer.Ordinal).ToList();
    Assert(both.Count == 0, "status listed as BOTH donored and intentionally iconless: " + string.Join(", ", both));

    // A donor that is empty, or that points at another pack status, cannot resolve to a vanilla asset.
    foreach (var pair in donors)
    {
        Assert(pair.Value.Length > 0, $"empty donor for {pair.Key}");
        Assert(!authored.Contains(pair.Value),
            $"{pair.Key}'s donor '{pair.Value}' is itself a pack status, so it has no baked dStatusEffect " +
            "asset either -- donors must be vanilla ids");
    }
});

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

/// StatusVisualPatches.cs's two coverage tables, read from source. Source-scanning (rather than
/// referencing the type) because ClassForge.Plugin targets net472 and links FTK2/Unity, which this
/// net10 test host cannot load -- the same reason ReadExternalClassifications scrapes CFConfig.cs.
static (Dictionary<string, string> Donors, HashSet<string> Iconless) ReadStatusDonorTables(string pluginSrcDir)
{
    var text = File.ReadAllText(Path.Combine(pluginSrcDir, "StatusVisualPatches.cs"));

    static string Initializer(string source, string declaration)
    {
        int at = source.IndexOf(declaration, StringComparison.Ordinal);
        if (at < 0) throw new Exception($"could not find '{declaration}' in StatusVisualPatches.cs");
        int open = source.IndexOf('{', at);
        int close = source.IndexOf("};", open, StringComparison.Ordinal);
        if (open < 0 || close < 0) throw new Exception($"could not delimit the initializer for '{declaration}'");
        return source.Substring(open, close - open);
    }

    var donors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                 Initializer(text, "Dictionary<string, string> Donors"),
                 "\\{\\s*\"([^\"]+)\"\\s*,\\s*\"([^\"]*)\"\\s*\\}"))
        donors[m.Groups[1].Value] = m.Groups[2].Value;

    var iconless = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                 Initializer(text, "HashSet<string> IntentionallyIconless"), "\"([^\"]+)\""))
        iconless.Add(m.Groups[1].Value);

    return (donors, iconless);
}

/// Every status id authored by any pack on disk, from the statuses.json files themselves -- the data
/// the donor table has to keep up with.
static HashSet<string> ReadAuthoredStatusIds(string classPacksDir)
{
    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var file in Directory.GetFiles(classPacksDir, "statuses.json", SearchOption.AllDirectories))
        foreach (var member in JsonParser.Parse(File.ReadAllText(file)).AsObjectMembers)
            ids.Add(member.Key);
    return ids;
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

static string FindPluginSrcDir()
{
    var dir = AppContext.BaseDirectory;
    for (int i = 0; i < 12; i++)
    {
        var candidate = Path.Combine(dir, "FTK2.ClassForge", "src", "ClassForge.Plugin");
        if (Directory.Exists(candidate)) return candidate;
        var parent = Directory.GetParent(dir);
        if (parent == null) break;
        dir = parent.FullName;
    }
    throw new DirectoryNotFoundException($"Could not locate FTK2.ClassForge/src/ClassForge.Plugin by walking up from {AppContext.BaseDirectory}");
}

/// The one sanctioned bypass: CFConfig.ExternalClassifications, as `{ "Section|Key", ParityClass.X }`.
static Dictionary<string, string> ReadExternalClassifications(string pluginSrcDir)
{
    var text = File.ReadAllText(Path.Combine(pluginSrcDir, "CFConfig.cs"));
    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                 text, @"\{\s*""([^""|]+)\|([^""]+)""\s*,\s*ParityClass\.(\w+)"))
        map[m.Groups[1].Value + "|" + m.Groups[2].Value] = m.Groups[3].Value;
    return map;
}

/// Every knob's classification, from both sources: CFConfig.Bind call sites (keyed "Section.Key") and
/// the external table. Interpolated keys (the per-pack `$"{id}.Enabled"` knob) are skipped -- they have
/// their own test.
static Dictionary<string, string> ReadAllClassifications(string pluginSrcDir)
{
    var map = new Dictionary<string, string>(StringComparer.Ordinal);

    foreach (var pair in ReadExternalClassifications(pluginSrcDir))
        map[pair.Key.Replace("|", ".")] = pair.Value;

    foreach (var file in Directory.GetFiles(pluginSrcDir, "*.cs", SearchOption.AllDirectories))
    {
        if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
        var text = File.ReadAllText(file);
        // Both wrapper shapes: Bind(...) and BindWithParityValue(...). The latter differs ONLY in what
        // the knob reports to parity ([Packs] AdditionalRoots reports its discovered pack SET, not its
        // filesystem paths -- W1-H); its ParityClass argument is still required and still positional, so
        // it must be scanned exactly like Bind or the opt-OUT guarantee this test enforces has a hole.
        var calls = System.Text.RegularExpressions.Regex.Matches(
            text, @"CFConfig\.Bind(?:WithParityValue)?\(\s*\w+\s*,\s*""([^""{]+)""\s*,\s*""([^""{]+)""");
        for (int i = 0; i < calls.Count; i++)
        {
            var m = calls[i];
            // The ParityClass argument is last, so scan forward only as far as the NEXT CFConfig bind.
            int from = m.Index + m.Length;
            var after = System.Text.RegularExpressions.Regex.Match(
                text.Substring(from), @"CFConfig\.Bind(?:WithParityValue)?\(");
            var window = after.Success ? text.Substring(from, after.Index) : text.Substring(from);
            var cls = System.Text.RegularExpressions.Regex.Match(window, @"ParityClass\.(\w+)");
            if (!cls.Success) throw new Exception($"CFConfig bind for [{m.Groups[1].Value}] {m.Groups[2].Value} has no ParityClass argument");
            map[m.Groups[1].Value + "." + m.Groups[2].Value] = cls.Groups[1].Value;
        }
    }
    return map;
}

// --- ClassForge.Core.Rng test helpers -------------------------------------------------------------

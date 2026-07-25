using System;
using System.Collections.Generic;

namespace FTK2Mods.DevKit.Tests
{
    internal static class CodecTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("Registration model");

            TestHarness.Run("registration normalizes nulls to empty strings", delegate
            {
                ParityRegistration r = new ParityRegistration(null, null, null, null);
                TestHarness.Equal("", r.Guid, "guid");
                TestHarness.Equal("", r.Version, "version");
                TestHarness.Equal("", r.DataHash, "dataHash");
                TestHarness.Equal(0, r.EnabledFeatures.Length, "features");
                TestHarness.False(r.HasWellFormedDataHash, "empty hash must not be well-formed");
            });

            TestHarness.Run("registration sorts, trims and de-duplicates enabled features", delegate
            {
                ParityRegistration r = new ParityRegistration("ftk2mods.x", "1.0.0", Sha(1),
                    new string[] { " Zebra ", "Alpha", "", null, "Alpha", "Mid" });
                string[] f = r.EnabledFeatures;
                TestHarness.Equal(3, f.Length, "feature count after dedupe/drop-empty");
                TestHarness.Equal("Alpha", f[0], "features[0]");
                TestHarness.Equal("Mid", f[1], "features[1]");
                TestHarness.Equal("Zebra", f[2], "features[2]");
            });

            TestHarness.Run("features compare as a set, not a sequence (SPEC 8 edge case)", delegate
            {
                ParityRegistration a = new ParityRegistration("m", "1", Sha(1), new string[] { "B", "A" });
                ParityRegistration b = new ParityRegistration("m", "1", Sha(1), new string[] { "A", "B" });
                TestHarness.True(a.FeaturesEqual(b), "out-of-order feature arrays must compare equal");
            });

            TestHarness.Section("Payload codec (FTK2MODS_PARITY_V1)");

            TestHarness.Run("snapshot round-trips guid/version/hash/features", delegate
            {
                ParityRegistration[] regs = new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.warbrain", "1.0.0", Sha(0xab), new string[] { "TacticalScoring", "AIDecisionLogging" }),
                    new ParityRegistration("ftk2mods.devkit", "0.1.0", Sha(0x1b), new string[0]),
                };
                string payload = ParityPayloadCodec.EncodeSnapshot("host", regs);
                ParitySnapshot decoded;
                string error;
                TestHarness.True(ParityPayloadCodec.TryDecodeSnapshot(payload, out decoded, out error), "decode failed: " + error);
                TestHarness.Equal("host", decoded.SenderPeerId, "sender peer id");
                TestHarness.Equal(2, decoded.Registrations.Length, "registration count");
                TestHarness.Equal("ftk2mods.devkit", decoded.Registrations[0].Guid, "sorted registrations[0]");
                TestHarness.Equal("ftk2mods.warbrain", decoded.Registrations[1].Guid, "sorted registrations[1]");
                TestHarness.Equal(Sha(0xab), decoded.Registrations[1].DataHash, "warbrain hash");
                TestHarness.Equal("[AIDecisionLogging, TacticalScoring]", decoded.Registrations[1].FeaturesToString(), "features");
                TestHarness.Equal(payload, ParityPayloadCodec.EncodeSnapshot("host", decoded.Registrations), "re-encode must be byte-identical");
            });

            TestHarness.Run("snapshot encoding is deterministic regardless of input order", delegate
            {
                ParityRegistration a = new ParityRegistration("ftk2mods.aaa", "1", Sha(1), new string[] { "F2", "F1" });
                ParityRegistration b = new ParityRegistration("ftk2mods.bbb", "2", Sha(2), new string[] { "Z" });
                ParityRegistration c = new ParityRegistration("ftk2mods.ccc", "3", Sha(3), null);
                string one = ParityPayloadCodec.EncodeSnapshot("p", new ParityRegistration[] { a, b, c });
                string two = ParityPayloadCodec.EncodeSnapshot("p", new ParityRegistration[] { c, a, b });
                TestHarness.Equal(one, two, "shuffled registration order must produce identical bytes");
            });

            TestHarness.Run("snapshot matches the SPEC 4 wire shape", delegate
            {
                string payload = ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.devkit", "1.0.0", Sha(0), new string[0]),
                });
                TestHarness.Equal(
                    "{\"Action\":\"FTK2MODS_PARITY_V1\",\"SenderPeerId\":\"host\",\"Registrations\":["
                    + "{\"Guid\":\"ftk2mods.devkit\",\"Version\":\"1.0.0\",\"DataHash\":\"" + Sha(0) + "\",\"EnabledFeatures\":[]}]}",
                    payload, "exact wire bytes");
            });

            TestHarness.Run("request payload round-trips", delegate
            {
                string payload = ParityPayloadCodec.EncodeRequest("client-2");
                TestHarness.Equal("{\"Action\":\"FTK2MODS_PARITY_REQUEST_V1\",\"RequestingPeerId\":\"client-2\"}",
                    payload, "exact wire bytes");
                string peer;
                string error;
                TestHarness.True(ParityPayloadCodec.TryDecodeRequest(payload, out peer, out error), "decode failed: " + error);
                TestHarness.Equal("client-2", peer, "requesting peer id");
            });

            TestHarness.Run("PeekAction routes both action keys and tolerates junk", delegate
            {
                TestHarness.Equal(ParityPayloadCodec.ParityActionKey,
                    ParityPayloadCodec.PeekAction(ParityPayloadCodec.EncodeSnapshot("h", new ParityRegistration[0])), "snapshot action");
                TestHarness.Equal(ParityPayloadCodec.ParityRequestActionKey,
                    ParityPayloadCodec.PeekAction(ParityPayloadCodec.EncodeRequest("c")), "request action");
                TestHarness.True(ParityPayloadCodec.PeekAction("not json at all") == null, "junk must peek as null");
                TestHarness.True(ParityPayloadCodec.PeekAction(null) == null, "null must peek as null");
                TestHarness.True(ParityPayloadCodec.PeekAction("{\"Action\":123}") == null, "non-string action must peek as null");
            });

            TestHarness.Run("codec escapes quotes, backslashes, newlines and non-ASCII", delegate
            {
                string nasty = "quo\"te \\back\\slash\nnew\tline é中";
                ParityRegistration r = new ParityRegistration("ftk2mods.x", "1.0", Sha(7), new string[] { nasty });
                string payload = ParityPayloadCodec.EncodeSnapshot(nasty, new ParityRegistration[] { r });
                for (int i = 0; i < payload.Length; i++)
                {
                    if (payload[i] > '~') throw new Exception("payload must be pure ASCII, found U+" + ((int)payload[i]).ToString("X4"));
                }
                ParitySnapshot decoded;
                string error;
                TestHarness.True(ParityPayloadCodec.TryDecodeSnapshot(payload, out decoded, out error), "decode failed: " + error);
                // Trailing/leading whitespace is trimmed by the model; compare the trimmed original.
                TestHarness.Equal(nasty.Trim(), decoded.SenderPeerId, "sender peer id round-trip");
                TestHarness.Equal(nasty.Trim(), decoded.Registrations[0].EnabledFeatures[0], "feature round-trip");
            });

            TestHarness.Run("malformed payloads fail closed without throwing", delegate
            {
                string[] junk = new string[]
                {
                    null,
                    "",
                    "{",
                    "{\"Action\":\"FTK2MODS_PARITY_V1\"",
                    "{\"Action\":\"FTK2MODS_PARITY_V1\",\"Registrations\":\"nope\"}",
                    "{\"Action\":\"SOMETHING_ELSE\"}",
                    "[]",
                    "{\"Action\":\"FTK2MODS_PARITY_V1\"} trailing",
                };
                for (int i = 0; i < junk.Length; i++)
                {
                    ParitySnapshot snapshot;
                    string error;
                    bool ok = ParityPayloadCodec.TryDecodeSnapshot(junk[i], out snapshot, out error);
                    TestHarness.False(ok, "junk payload #" + i + " must not decode");
                    TestHarness.True(!string.IsNullOrEmpty(error), "junk payload #" + i + " must report an error");
                }
            });

            TestHarness.Run("missing Registrations member decodes as an empty snapshot", delegate
            {
                ParitySnapshot snapshot;
                string error;
                TestHarness.True(ParityPayloadCodec.TryDecodeSnapshot(
                    "{\"Action\":\"FTK2MODS_PARITY_V1\",\"SenderPeerId\":\"p\"}", out snapshot, out error), "decode failed: " + error);
                TestHarness.Equal(0, snapshot.Registrations.Length, "empty registration list");
            });

            TestHarness.Run("LooksLikeParityPayload pre-filter rejects foreign network actions", delegate
            {
                TestHarness.False(ParityPayloadCodec.LooksLikeParityPayload("EOR_SYNC_TOWN_SNAPSHOT_V1|payload"), "EOR action");
                TestHarness.False(ParityPayloadCodec.LooksLikeParityPayload(null), "null");
                TestHarness.True(ParityPayloadCodec.LooksLikeParityPayload(ParityPayloadCodec.EncodeRequest("c")), "our request");
            });

            // ---- culture stress -------------------------------------------------------------
            // A Turkish-I locale is the classic breaker for ToLower()/ToUpper()-based logic, and a
            // comma-decimal locale (de-DE) breaks any culture-sensitive number formatting. Both
            // must produce byte-identical payloads to the invariant run, or two peers on different
            // OS locales would disagree about identical data.
            string invariantPayload = ParityPayloadCodec.EncodeSnapshot("HOST-ID", new ParityRegistration[]
            {
                new ParityRegistration("FTK2MODS.INDEXER", "1.0.0-I", Sha(0x1a), new string[] { "Idle", "INDEX", "ıslak" }),
            });

            TestHarness.RunInCulture("codec output is byte-identical under a Turkish-I culture", "tr-TR", delegate
            {
                string payload = ParityPayloadCodec.EncodeSnapshot("HOST-ID", new ParityRegistration[]
                {
                    new ParityRegistration("FTK2MODS.INDEXER", "1.0.0-I", Sha(0x1a), new string[] { "Idle", "INDEX", "ıslak" }),
                });
                TestHarness.Equal(invariantPayload, payload, "Turkish culture must not change the payload");
                ParitySnapshot decoded;
                string error;
                TestHarness.True(ParityPayloadCodec.TryDecodeSnapshot(payload, out decoded, out error), "decode failed: " + error);
                TestHarness.Equal("FTK2MODS.INDEXER", decoded.Registrations[0].Guid, "guid survives Turkish round-trip");
                TestHarness.Equal("[INDEX, Idle, ıslak]", decoded.Registrations[0].FeaturesToString(), "feature order is ordinal, not culture-collated");
            });

            TestHarness.RunInCulture("codec output is byte-identical under a comma-decimal culture", "de-DE", delegate
            {
                string payload = ParityPayloadCodec.EncodeSnapshot("HOST-ID", new ParityRegistration[]
                {
                    new ParityRegistration("FTK2MODS.INDEXER", "1.0.0-I", Sha(0x1a), new string[] { "Idle", "INDEX", "ıslak" }),
                });
                TestHarness.Equal(invariantPayload, payload, "German culture must not change the payload");
            });
        }

        internal static string Sha(int seed)
        {
            char[] hex = new char[64];
            for (int i = 0; i < 64; i++)
            {
                int v = (seed + i) & 0xF;
                hex[i] = "0123456789abcdef"[v];
            }
            return DataHasher.HashPrefix + new string(hex);
        }
    }
}

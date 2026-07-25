using System;
using System.Collections.Generic;

namespace FTK2Mods.DevKit.Tests
{
    internal static class HandshakeTests
    {
        private const string HashA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string HashB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        internal static void RunAll()
        {
            TestHarness.Section("FTK2MODS_PARITY_V1 handshake flow");

            TestHarness.Run("matching peers stay silent - no banner, no SafeMode, no callback", delegate
            {
                List<string[]> fired = new List<string[]>();
                ParityService.SetLocalPeerId("client-1");
                ParityService.RegisterWithCallback("ftk2mods.warbrain", "1.0.0", HashA, new string[] { "Tactics" },
                    delegate (string[] args) { fired.Add(args); });

                string hostPayload = ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.warbrain", "1.0.0", HashA, new string[] { "Tactics" }),
                });
                ParityService.HandleIncomingPayload(hostPayload, "host");

                TestHarness.Equal(0, fired.Count, "no callback on a healthy handshake");
                TestHarness.Equal("", ParityService.TakePendingBanner(), "no banner on a healthy handshake");
                TestHarness.Equal("", ParityService.GetLastMismatchSummary(), "no mismatch summary");
                TestHarness.Equal(0, ParityService.GetSafeModeGuids().Length, "no SafeMode");
                TestHarness.False(ParityService.IsSessionBlocked(), "session not blocked");
            });

            TestHarness.Run("a data divergence fires the callback, banner and SafeMode (default policy)", delegate
            {
                List<string[]> fired = new List<string[]>();
                ParityService.SetLocalPeerId("client-1");
                ParityService.RegisterWithCallback("ftk2mods.forge", "1.0.0", HashA, null,
                    delegate (string[] args) { fired.Add(args); });

                ParityService.HandleIncomingPayload(ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.forge", "1.0.0", HashB, null),
                }), "host");

                TestHarness.Equal(1, fired.Count, "callback fired once");
                TestHarness.Equal("DataMismatch", fired[0][1], "callback kind");
                TestHarness.Equal("host", fired[0][4], "callback peer id");
                string banner = ParityService.TakePendingBanner();
                TestHarness.True(banner.Contains("ftk2mods.forge"), "banner names the mod");
                TestHarness.True(banner.Contains("data diverged"), "banner names the divergence");
                TestHarness.Equal("", ParityService.TakePendingBanner(), "banner is consumed once");
                TestHarness.True(ParityService.IsInSafeMode("ftk2mods.forge"), "SafeMode engaged");
                TestHarness.True(ParityService.GetLastMismatchSummary().Contains("ftk2mods.forge"), "sticky mismatch summary");
            });

            TestHarness.Run("WarnOnly fires callbacks and the banner but no SafeMode", delegate
            {
                List<string[]> fired = new List<string[]>();
                ParityService.SetPolicy("WarnOnly");
                ParityService.SetLocalPeerId("client-1");
                ParityService.RegisterWithCallback("ftk2mods.forge", "1.0.0", HashA, null,
                    delegate (string[] args) { fired.Add(args); });
                ParityService.HandleIncomingPayload(ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.forge", "1.0.0", HashB, null),
                }), "host");
                TestHarness.Equal(1, fired.Count, "callback still fires under WarnOnly");
                TestHarness.False(ParityService.IsInSafeMode("ftk2mods.forge"), "no SafeMode under WarnOnly");
                TestHarness.False(ParityService.IsSessionBlocked(), "no block under WarnOnly");
                TestHarness.True(ParityService.TakePendingBanner().Length > 0, "banner shown");
            });

            TestHarness.Run("Block marks the session blocked", delegate
            {
                ParityService.SetPolicy("Block");
                ParityService.SetLocalPeerId("client-1");
                ParityService.Register("ftk2mods.forge", "1.0.0", HashA, null);
                ParityService.HandleIncomingPayload(ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.forge", "1.0.0", HashB, null),
                }), "host");
                TestHarness.True(ParityService.IsSessionBlocked(), "session blocked");
                TestHarness.False(ParityService.IsInSafeMode("ftk2mods.forge"), "Block does not also engage SafeMode");
            });

            TestHarness.Run("one mod throwing from its callback never stops the others", delegate
            {
                List<string> called = new List<string>();
                List<string> errors = new List<string>();
                ParityService.SetLogger(delegate (string level, string message)
                {
                    if (level == "Error") errors.Add(message);
                });
                ParityService.SetLocalPeerId("client-1");
                ParityService.RegisterWithCallback("ftk2mods.aaa", "1.0.0", HashA, null,
                    delegate (string[] args) { called.Add("aaa"); throw new InvalidOperationException("boom"); });
                ParityService.RegisterWithCallback("ftk2mods.bbb", "1.0.0", HashA, null,
                    delegate (string[] args) { called.Add("bbb"); });

                ParityService.HandleIncomingPayload(ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.aaa", "1.0.0", HashB, null),
                    new ParityRegistration("ftk2mods.bbb", "1.0.0", HashB, null),
                }), "host");

                TestHarness.Equal(2, called.Count, "both callbacks invoked");
                TestHarness.Equal(1, errors.Count, "the throwing mod is logged");
                TestHarness.True(errors[0].Contains("ftk2mods.aaa"), "the error names the offending mod");
                TestHarness.True(errors[0].Contains("isolated"), "the error states it was isolated");
                TestHarness.True(ParityService.IsInSafeMode("ftk2mods.bbb"), "the well-behaved mod still got SafeMode");
            });

            TestHarness.Run("MissingLocal cannot call a local callback but is still reported", delegate
            {
                ParityService.SetLocalPeerId("client-1");
                ParityService.Register("ftk2mods.devkit", "0.1.0", HashA, null);
                ParityService.HandleIncomingPayload(ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.devkit", "0.1.0", HashA, null),
                    new ParityRegistration("ftk2mods.summoner", "1.0.0", HashA, null),
                }), "host");
                string summary = ParityService.GetLastMismatchSummary();
                TestHarness.True(summary.Contains("ftk2mods.summoner"), "mismatch summary names the peer-only mod");
                TestHarness.True(summary.Contains("NOT installed locally"), "summary explains the divergence");
                TestHarness.False(summary.Contains("ftk2mods.devkit"), "the matching mod is not reported");
            });

            TestHarness.Run("the host replies to a peer's snapshot exactly once (no ping-pong)", delegate
            {
                ParityService.SetLocalPeerId("host");
                ParityService.SetIsHost(true);
                ParityService.Register("ftk2mods.devkit", "0.1.0", HashA, null);

                string clientPayload = ParityPayloadCodec.EncodeSnapshot("client-2", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.devkit", "0.1.0", HashA, null),
                });
                string firstReply = ParityService.HandleIncomingPayload(clientPayload, "client-2");
                TestHarness.True(firstReply != null, "host replies to the first snapshot");
                TestHarness.Equal(ParityPayloadCodec.ParityActionKey, ParityPayloadCodec.PeekAction(firstReply), "reply is a snapshot");
                string secondReply = ParityService.HandleIncomingPayload(clientPayload, "client-2");
                TestHarness.True(secondReply == null, "host does not reply twice to the same peer");
            });

            TestHarness.Run("a peer's own echoed snapshot is ignored", delegate
            {
                ParityService.SetLocalPeerId("host");
                ParityService.SetIsHost(true);
                ParityService.Register("ftk2mods.devkit", "0.1.0", HashA, null);
                string own = ParityService.BuildSnapshotPayload();
                TestHarness.True(ParityService.HandleIncomingPayload(own, "host") == null, "no reply to our own payload");
                TestHarness.Equal("", ParityService.GetLastMismatchSummary(), "our own payload is not compared");
            });

            TestHarness.Run("late join: the host answers FTK2MODS_PARITY_REQUEST_V1 with a FRESH snapshot", delegate
            {
                ParityService.SetLocalPeerId("host");
                ParityService.SetIsHost(true);
                ParityService.Register("ftk2mods.forge", "1.0.0", HashA, null);

                string request = ParityPayloadCodec.EncodeRequest("client-3");
                string reply = ParityService.HandleIncomingPayload(request, "client-3");
                TestHarness.True(reply != null, "host answers the request");
                TestHarness.True(reply.Contains(HashA), "reply carries the current hash");

                // Mutate state (a hot-reload) and re-request: the answer must reflect the NEW state,
                // never a cached copy from session start.
                ParityService.UpdateDataHash("ftk2mods.forge", HashB);
                string secondReply = ParityService.HandleIncomingPayload(ParityPayloadCodec.EncodeRequest("client-4"), "client-4");
                TestHarness.True(secondReply != null, "host answers the second request");
                TestHarness.True(secondReply.Contains(HashB), "reply reflects the post-hot-reload hash");
                TestHarness.False(secondReply.Contains(HashA), "reply is not a cached pre-reload snapshot");
            });

            TestHarness.Run("a non-host peer ignores a parity request", delegate
            {
                ParityService.SetLocalPeerId("client-1");
                ParityService.SetIsHost(false);
                ParityService.Register("ftk2mods.forge", "1.0.0", HashA, null);
                TestHarness.True(ParityService.HandleIncomingPayload(ParityPayloadCodec.EncodeRequest("client-3"), "client-3") == null,
                    "non-host must not answer");
            });

            TestHarness.Run("foreign and malformed network payloads are ignored without throwing", delegate
            {
                ParityService.SetLocalPeerId("client-1");
                ParityService.Register("ftk2mods.forge", "1.0.0", HashA, null);
                string[] foreign = new string[]
                {
                    null,
                    "",
                    "EOR_SYNC_TOWN_SNAPSHOT_V1|{}",
                    "TOWN_SERVICES",
                    "{\"Action\":\"FTK2MODS_PARITY_V1\",",           // our key, but broken JSON
                    "{\"Action\":\"FTK2MODS_PARITY_REQUEST_V1\"",     // our key, but broken JSON
                };
                for (int i = 0; i < foreign.Length; i++)
                {
                    TestHarness.True(ParityService.HandleIncomingPayload(foreign[i], "host") == null,
                        "payload #" + i + " must be ignored");
                }
                TestHarness.Equal("", ParityService.GetLastMismatchSummary(), "no state change from foreign traffic");
            });

            TestHarness.Run("hot-reload re-handshake: an updated hash flips a healthy session to a mismatch", delegate
            {
                ParityService.SetLocalPeerId("client-1");
                ParityService.Register("ftk2mods.forge", "1.0.0", HashA, null);
                string hostSnapshot = ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.forge", "1.0.0", HashA, null),
                });
                ParityService.HandleIncomingPayload(hostSnapshot, "host");
                TestHarness.Equal("", ParityService.GetLastMismatchSummary(), "healthy before the reload");

                // Only this peer hot-reloads -> its hash changes -> the re-handshake must diverge.
                ParityService.UpdateDataHash("ftk2mods.forge", HashB);
                ParityService.HandleIncomingPayload(hostSnapshot, "host");
                TestHarness.True(ParityService.GetLastMismatchSummary().Contains("data diverged"),
                    "an uncoordinated hot-reload must surface as a data mismatch");
                TestHarness.True(ParityService.IsInSafeMode("ftk2mods.forge"), "SafeMode engaged after the re-handshake");
            });

            TestHarness.Run("ResetSession clears peers/SafeMode/block but keeps registrations", delegate
            {
                ParityService.SetLocalPeerId("client-1");
                ParityService.Register("ftk2mods.forge", "1.0.0", HashA, null);
                ParityService.HandleIncomingPayload(ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.forge", "1.0.0", HashB, null),
                }), "host");
                TestHarness.True(ParityService.IsInSafeMode("ftk2mods.forge"), "SafeMode engaged before reset");

                ParityService.ResetSession();
                TestHarness.False(ParityService.IsInSafeMode("ftk2mods.forge"), "SafeMode cleared");
                TestHarness.Equal("", ParityService.GetLastMismatchSummary(), "mismatch summary cleared");
                TestHarness.Equal(1, ParityService.GetRegisteredGuids().Length, "registrations survive a session reset");
            });

            TestHarness.Run("GetLastVerdictRows exposes every verdict as flat string rows", delegate
            {
                ParityService.SetLocalPeerId("client-1");
                ParityService.Register("ftk2mods.forge", "1.0.0", HashA, null);
                ParityService.Register("ftk2mods.warbrain", "1.0.0", HashA, null);
                ParityService.HandleIncomingPayload(ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.forge", "1.0.0", HashB, null),
                    new ParityRegistration("ftk2mods.warbrain", "1.0.0", HashA, null),
                }), "host");
                string[][] rows = ParityService.GetLastVerdictRows();
                TestHarness.Equal(2, rows.Length, "one row per compared mod");
                TestHarness.Equal(6, rows[0].Length, "row width");
                TestHarness.Equal("ftk2mods.forge", rows[0][0], "rows are guid-ordered");
                TestHarness.Equal("DataMismatch", rows[0][1], "diverged mod kind");
                TestHarness.Equal("Match", rows[1][1], "matching mod kind");
            });

            TestHarness.RunInCulture("a full handshake behaves identically under a Turkish-I culture", "tr-TR", delegate
            {
                List<string[]> fired = new List<string[]>();
                ParityService.SetLocalPeerId("client-1");
                ParityService.RegisterWithCallback("ftk2mods.indexer", "1.0.0-I", HashA, new string[] { "Idle", "INDEX" },
                    delegate (string[] args) { fired.Add(args); });

                // Identical registration -> must match even where I/i casing differs from the invariant fold.
                ParityService.HandleIncomingPayload(ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.indexer", "1.0.0-I", HashA, new string[] { "INDEX", "Idle" }),
                }), "host");
                TestHarness.Equal(0, fired.Count, "no mismatch for identical registrations under tr-TR");

                // Case-only difference -> must still diverge under tr-TR.
                ParityService.HandleIncomingPayload(ParityPayloadCodec.EncodeSnapshot("host2", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.indexer", "1.0.0-i", HashA, new string[] { "INDEX", "Idle" }),
                }), "host2");
                TestHarness.Equal(1, fired.Count, "case-only version difference must diverge under tr-TR");
                TestHarness.Equal("VersionMismatch", fired[0][1], "kind");
            });
        }
    }
}

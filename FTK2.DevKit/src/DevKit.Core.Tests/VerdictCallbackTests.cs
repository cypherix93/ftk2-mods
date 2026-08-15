using System;
using System.Collections.Generic;

namespace FTK2Mods.DevKit.Tests
{
    /// <summary>
    /// <c>ParityService.RegisterVerdictCallback</c> — the verdict-ARRIVAL surface (task #11).
    /// <see cref="ParityService.RegisterWithCallback"/> only notifies on mismatch; a sibling mod that
    /// fails closed until a VERIFIED MATCH (ClassForge's MP trait-loadout gate) needs to hear about
    /// the Match outcome too, without polling. Args contract: <c>[remotePeerId, "Match"|"Mismatch"]</c>,
    /// fired only on a verdict TRANSITION for that peer (same m13 dedupe as the failure callback).
    /// </summary>
    internal static class VerdictCallbackTests
    {
        private const string HashA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string HashB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        internal static void RunAll()
        {
            TestHarness.Section("verdict-arrival callbacks (RegisterVerdictCallback)");

            TestHarness.Run("a healthy handshake fires the verdict callback with Match", delegate
            {
                List<string[]> fired = new List<string[]>();
                ParityService.SetLocalPeerId("client-1");
                ParityService.Register("ftk2mods.classforge", "1.0.0", HashA, null);
                TestHarness.True(
                    ParityService.RegisterVerdictCallback("ftk2mods.classforge",
                        delegate (string[] args) { fired.Add(args); }),
                    "registration accepted");

                ParityService.HandleIncomingPayload(ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.classforge", "1.0.0", HashA, null),
                }), "host");

                TestHarness.Equal(1, fired.Count, "verdict callback fired once");
                TestHarness.Equal("host", fired[0][0], "arg 0 = remote peer id");
                TestHarness.Equal("Match", fired[0][1], "arg 1 = Match on a healthy handshake");
            });

            TestHarness.Run("a mismatch fires the verdict callback with Mismatch (alongside ParityFailed)", delegate
            {
                List<string[]> verdicts = new List<string[]>();
                List<string[]> failures = new List<string[]>();
                ParityService.SetLocalPeerId("client-1");
                ParityService.RegisterWithCallback("ftk2mods.classforge", "1.0.0", HashA, null,
                    delegate (string[] args) { failures.Add(args); });
                ParityService.RegisterVerdictCallback("ftk2mods.classforge",
                    delegate (string[] args) { verdicts.Add(args); });

                ParityService.HandleIncomingPayload(ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.classforge", "1.0.0", HashB, null),
                }), "host");

                TestHarness.Equal(1, verdicts.Count, "verdict callback fired once");
                TestHarness.Equal("Mismatch", verdicts[0][1], "verdict arg says Mismatch");
                TestHarness.Equal(1, failures.Count, "ParityFailed still fired too");
            });

            TestHarness.Run("a duplicate snapshot does not re-fire the verdict callback (m13 dedupe)", delegate
            {
                List<string[]> fired = new List<string[]>();
                ParityService.SetLocalPeerId("client-1");
                ParityService.Register("ftk2mods.classforge", "1.0.0", HashA, null);
                ParityService.RegisterVerdictCallback("ftk2mods.classforge",
                    delegate (string[] args) { fired.Add(args); });

                string payload = ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.classforge", "1.0.0", HashA, null),
                });
                ParityService.HandleIncomingPayload(payload, "host");
                ParityService.HandleIncomingPayload(payload, "host");

                TestHarness.Equal(1, fired.Count, "second identical snapshot did not re-notify");
            });

            TestHarness.Run("a throwing verdict callback is isolated from other subscribers and the handshake", delegate
            {
                List<string[]> fired = new List<string[]>();
                ParityService.SetLocalPeerId("client-1");
                ParityService.Register("ftk2mods.devkit", "1.0.0", HashA, null);
                ParityService.Register("ftk2mods.classforge", "1.0.0", HashA, null);
                ParityService.RegisterVerdictCallback("ftk2mods.devkit",
                    delegate { throw new InvalidOperationException("boom"); });
                ParityService.RegisterVerdictCallback("ftk2mods.classforge",
                    delegate (string[] args) { fired.Add(args); });

                ParityService.HandleIncomingPayload(ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.devkit", "1.0.0", HashA, null),
                    new ParityRegistration("ftk2mods.classforge", "1.0.0", HashA, null),
                }), "host");

                TestHarness.Equal(1, fired.Count, "second subscriber notified despite the first throwing");
            });

            TestHarness.Run("RegisterVerdictCallback before Register still binds (order-independent)", delegate
            {
                List<string[]> fired = new List<string[]>();
                ParityService.SetLocalPeerId("client-1");
                ParityService.RegisterVerdictCallback("ftk2mods.classforge",
                    delegate (string[] args) { fired.Add(args); });
                ParityService.Register("ftk2mods.classforge", "1.0.0", HashA, null);

                ParityService.HandleIncomingPayload(ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.classforge", "1.0.0", HashA, null),
                }), "host");

                TestHarness.Equal(1, fired.Count, "callback registered pre-Register still fires");
            });
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;

namespace FTK2Mods.DevKit.Tests
{
    internal static class RegistryTests
    {
        private const string HashA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string HashB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        internal static void RunAll()
        {
            TestHarness.Section("ParityService registry");

            TestHarness.Run("register / re-register / unregister", delegate
            {
                TestHarness.True(ParityService.Register("ftk2mods.warbrain", "1.0.0", HashA, new string[] { "Tactics" }), "register");
                TestHarness.True(ParityService.Register("ftk2mods.devkit", "0.1.0", HashA, null), "register second");
                TestHarness.Equal(2, ParityService.GetRegisteredGuids().Length, "registered count");
                TestHarness.Equal("ftk2mods.devkit", ParityService.GetRegisteredGuids()[0], "guids are ordinal-sorted");

                // Re-registering replaces rather than duplicating.
                TestHarness.True(ParityService.Register("ftk2mods.warbrain", "2.0.0", HashB, null), "re-register");
                TestHarness.Equal(2, ParityService.GetRegisteredGuids().Length, "no duplicate entry");
                string[] fields = ParityService.GetRegistrationFields("ftk2mods.warbrain");
                TestHarness.Equal("2.0.0", fields[1], "version replaced");
                TestHarness.Equal(HashB, fields[2], "hash replaced");

                TestHarness.True(ParityService.Unregister("ftk2mods.warbrain"), "unregister");
                TestHarness.Equal(1, ParityService.GetRegisteredGuids().Length, "count after unregister");
                TestHarness.False(ParityService.Unregister("ftk2mods.nope"), "unregister unknown returns false");
            });

            TestHarness.Run("register rejects an empty guid and never throws on junk", delegate
            {
                TestHarness.False(ParityService.Register(null, "1", HashA, null), "null guid");
                TestHarness.False(ParityService.Register("   ", "1", HashA, null), "whitespace guid");
                TestHarness.True(ParityService.Register("ftk2mods.x", null, null, null), "null version/hash still registers");
                TestHarness.Equal(1, ParityService.GetRegisteredGuids().Length, "only the valid one registered");
            });

            TestHarness.Run("GetRegistrationFields returns [guid, version, hash, features...]", delegate
            {
                ParityService.Register("ftk2mods.x", "1.2.3", HashA, new string[] { "B", "A" });
                string[] fields = ParityService.GetRegistrationFields("ftk2mods.x");
                TestHarness.Equal(5, fields.Length, "field count");
                TestHarness.Equal("ftk2mods.x", fields[0], "guid");
                TestHarness.Equal("1.2.3", fields[1], "version");
                TestHarness.Equal(HashA, fields[2], "hash");
                TestHarness.Equal("A", fields[3], "features sorted [0]");
                TestHarness.Equal("B", fields[4], "features sorted [1]");
                TestHarness.Equal(0, ParityService.GetRegistrationFields("ftk2mods.unknown").Length, "unknown guid -> empty");
            });

            TestHarness.Run("UpdateDataHash / UpdateFeatures mutate only their own field (hot-reload path)", delegate
            {
                ParityService.Register("ftk2mods.x", "1.2.3", HashA, new string[] { "A" });
                TestHarness.True(ParityService.UpdateDataHash("ftk2mods.x", HashB), "UpdateDataHash");
                string[] fields = ParityService.GetRegistrationFields("ftk2mods.x");
                TestHarness.Equal("1.2.3", fields[1], "version preserved");
                TestHarness.Equal(HashB, fields[2], "hash updated");
                TestHarness.Equal("A", fields[3], "features preserved");

                TestHarness.True(ParityService.UpdateFeatures("ftk2mods.x", new string[] { "C", "A" }), "UpdateFeatures");
                fields = ParityService.GetRegistrationFields("ftk2mods.x");
                TestHarness.Equal(HashB, fields[2], "hash preserved");
                TestHarness.Equal("A", fields[3], "features[0]");
                TestHarness.Equal("C", fields[4], "features[1]");

                TestHarness.False(ParityService.UpdateDataHash("ftk2mods.unknown", HashA), "unknown guid -> false");
            });

            TestHarness.Run("a missing/malformed dataHash is logged loudly at registration time", delegate
            {
                List<string> warnings = new List<string>();
                ParityService.SetLogger(delegate (string level, string message)
                {
                    if (level == "Warning") warnings.Add(message);
                });
                ParityService.Register("ftk2mods.x", "1.0.0", "", null);
                TestHarness.Equal(1, warnings.Count, "one warning expected");
                TestHarness.True(warnings[0].Contains("GUARANTEED mismatch"), "warning explains the consequence");
            });

            TestHarness.Run("DumpRegistrations reports guid/version/hash/features and peers (dk_dump_parity)", delegate
            {
                ParityService.SetLocalPeerId("host");
                ParityService.SetIsHost(true);
                ParityService.Register("ftk2mods.warbrain", "1.0.0", HashA, new string[] { "Tactics" });
                string dump = ParityService.DumpRegistrations();
                TestHarness.True(dump.Contains("ftk2mods.warbrain"), "dump names the mod");
                TestHarness.True(dump.Contains(HashA), "dump prints the hash");
                TestHarness.True(dump.Contains("Tactics"), "dump prints the features");
                TestHarness.True(dump.Contains("isHost=true"), "dump prints session role");
                TestHarness.True(dump.Contains("WarnAndSafeMode"), "dump prints the policy");
                TestHarness.True(dump.Contains("(none - all peers matched)"), "dump prints the last mismatch result");
            });

            TestHarness.Run("SetPolicy accepts known names, rejects junk and keeps the previous value", delegate
            {
                TestHarness.Equal("WarnAndSafeMode", ParityService.GetPolicy(), "default policy");
                TestHarness.True(ParityService.SetPolicy("Block"), "set Block");
                TestHarness.Equal("Block", ParityService.GetPolicy(), "policy after set");
                TestHarness.False(ParityService.SetPolicy("Nonsense"), "junk rejected");
                TestHarness.Equal("Block", ParityService.GetPolicy(), "policy unchanged after junk");
            });

            TestHarness.Section("ParityService reflection surface (the way a sibling mod calls it)");

            TestHarness.Run("a client mod can drive the whole API through reflection with BCL types only", delegate
            {
                // Exactly what a sibling plugin does: no compile-time reference, resolve by name.
                Type service = Type.GetType("FTK2Mods.DevKit.ParityService, ftk2mods.devkit");
                TestHarness.True(service != null, "Type.GetType(\"FTK2Mods.DevKit.ParityService, ftk2mods.devkit\") must resolve");

                MethodInfo computeFolder = service.GetMethod("ComputeDataHashForFolder", BindingFlags.Public | BindingFlags.Static);
                MethodInfo registerWithCallback = service.GetMethod("RegisterWithCallback", BindingFlags.Public | BindingFlags.Static);
                MethodInfo register = service.GetMethod("Register", BindingFlags.Public | BindingFlags.Static);
                MethodInfo isInSafeMode = service.GetMethod("IsInSafeMode", BindingFlags.Public | BindingFlags.Static);
                MethodInfo getRegisteredGuids = service.GetMethod("GetRegisteredGuids", BindingFlags.Public | BindingFlags.Static);
                MethodInfo buildSnapshot = service.GetMethod("BuildSnapshotPayload", BindingFlags.Public | BindingFlags.Static);
                MethodInfo handleIncoming = service.GetMethod("HandleIncomingPayload", BindingFlags.Public | BindingFlags.Static);
                MethodInfo setLocalPeerId = service.GetMethod("SetLocalPeerId", BindingFlags.Public | BindingFlags.Static);

                TestHarness.True(computeFolder != null, "ComputeDataHashForFolder resolvable");
                TestHarness.True(registerWithCallback != null, "RegisterWithCallback resolvable");
                TestHarness.True(register != null, "Register resolvable");
                TestHarness.True(isInSafeMode != null, "IsInSafeMode resolvable");
                TestHarness.True(getRegisteredGuids != null, "GetRegisteredGuids resolvable");
                TestHarness.True(buildSnapshot != null, "BuildSnapshotPayload resolvable");
                TestHarness.True(handleIncoming != null, "HandleIncomingPayload resolvable");
                TestHarness.True(setLocalPeerId != null, "SetLocalPeerId resolvable");

                // No overloads anywhere on the public surface: a reflection caller must be able to
                // do a plain GetMethod(name) without disambiguating parameter types.
                MethodInfo[] all = service.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                Dictionary<string, int> byName = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < all.Length; i++)
                {
                    int n;
                    byName.TryGetValue(all[i].Name, out n);
                    byName[all[i].Name] = n + 1;
                }
                foreach (KeyValuePair<string, int> kv in byName)
                {
                    if (kv.Value > 1) throw new Exception("ParityService." + kv.Key + " is overloaded (" + kv.Value + " overloads) - breaks plain reflection lookup");
                }

                // Every parameter and return type must be a simple BCL type a caller already has.
                for (int i = 0; i < all.Length; i++)
                {
                    ParameterInfo[] ps = all[i].GetParameters();
                    for (int j = 0; j < ps.Length; j++)
                    {
                        if (!IsSimpleType(ps[j].ParameterType))
                            throw new Exception("ParityService." + all[i].Name + " parameter '" + ps[j].Name
                                + "' is " + ps[j].ParameterType.FullName + " - not reflection-friendly");
                    }
                    if (!IsSimpleType(all[i].ReturnType) && all[i].ReturnType != typeof(void))
                        throw new Exception("ParityService." + all[i].Name + " returns " + all[i].ReturnType.FullName + " - not reflection-friendly");
                }

                // Now actually drive it the way a client mod would.
                setLocalPeerId.Invoke(null, new object[] { "client-1" });
                List<string[]> received = new List<string[]>();
                Action<string[]> onParityFailed = delegate (string[] args) { received.Add(args); };
                object registered = registerWithCallback.Invoke(null, new object[]
                {
                    "ftk2mods.warbrain", "1.0.0", HashA, new string[] { "TacticalScoring" }, onParityFailed,
                });
                TestHarness.True((bool)registered, "RegisterWithCallback via reflection");
                string[] guids = (string[])getRegisteredGuids.Invoke(null, new object[0]);
                TestHarness.Equal(1, guids.Length, "registered via reflection");

                // The host's snapshot arrives with a diverged data hash for the same mod.
                string hostPayload = ParityPayloadCodec.EncodeSnapshot("host", new ParityRegistration[]
                {
                    new ParityRegistration("ftk2mods.warbrain", "1.0.0", HashB, new string[] { "TacticalScoring" }),
                });
                object reply = handleIncoming.Invoke(null, new object[] { hostPayload, "host" });
                TestHarness.True(reply == null, "a non-host peer must not reply to a snapshot");
                TestHarness.Equal(1, received.Count, "the ParityFailed callback fired exactly once");
                TestHarness.Equal("ftk2mods.warbrain", received[0][0], "callback arg guid");
                TestHarness.Equal("DataMismatch", received[0][1], "callback arg kind");
                TestHarness.True((bool)isInSafeMode.Invoke(null, new object[] { "ftk2mods.warbrain" }),
                    "SafeMode engaged for the diverged mod under the default policy");

                // ComputeDataHashForFolder is callable with a null glob array (= defaults).
                string missingFolderHash = (string)computeFolder.Invoke(null, new object[] { "definitely-not-a-folder", null });
                TestHarness.Equal("", missingFolderHash, "missing folder hashes to empty via reflection");
            });
        }

        private static bool IsSimpleType(Type t)
        {
            if (t == typeof(string) || t == typeof(string[]) || t == typeof(string[][])) return true;
            if (t == typeof(bool) || t == typeof(int)) return true;
            if (t == typeof(Action<string[]>) || t == typeof(Action<string, string>)) return true;
            return false;
        }
    }
}

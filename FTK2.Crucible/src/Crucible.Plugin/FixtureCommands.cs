using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Fixture generation: read and mutate the live run, then persist it as a NEW save.
    ///
    /// This is what makes testing 31 authored classes tractable. Building a party through character
    /// creation takes ~20 driven UI steps per run; mutating a loaded run takes one command. The
    /// intended workflow is load one base fixture, swap what the test needs, save under a fresh id.
    ///
    /// Verified API surface (TypeProbe --signatures, 2026-08-23):
    ///   SaveGameHelper.WriteSaveData(String pGameRunId, GameRunData pGameRun, UserData pUser) static
    ///   Env.GameRun : GameRunData · Env.User : UserData · Env.GameRuns : List(String)
    ///   GameRunData.Entities : List(Entity) · CharacterComponent.ConfigName : String  <- THE CLASS
    ///
    /// <b>Safety:</b> a save is ALWAYS written under a freshly generated run id. This folder holds
    /// the owner's live co-op campaign, and overwriting an existing run id would destroy a real save.
    /// There is deliberately no "overwrite" argument — the only way to reuse an id is to not offer it.
    ///
    /// Components are matched by type NAME over <c>Entity.Components</c> rather than through
    /// <c>Entity.Get&lt;T&gt;()</c>: the latter is generic and would need MakeGenericMethod against a
    /// game type resolved at runtime, which buys nothing here and fails less clearly.
    /// </summary>
    internal static class FixtureCommands
    {
        internal static string LastResult;
        private static ManualLogSource _log;
        private static bool _partyListRegistered;
        private static bool _setClassRegistered;
        private static bool _saveRegistered;
        private static bool _listRegistered;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        internal static void TryRegister()
        {
            if (!_partyListRegistered)
                _partyListRegistered = GameBridge.RegisterCommand("crucible_party_list",
                    typeof(FixtureCommands).GetMethod("CruciblePartyList", BindingFlags.Public | BindingFlags.Static),
                    new List<string>());

            if (!_setClassRegistered)
                _setClassRegistered = GameBridge.RegisterCommand("crucible_party_set_class",
                    typeof(FixtureCommands).GetMethod("CruciblePartySetClass", BindingFlags.Public | BindingFlags.Static),
                    new List<string> { "slot", "classConfigName" });

            if (!_saveRegistered)
                _saveRegistered = GameBridge.RegisterCommand("crucible_fixture_save",
                    typeof(FixtureCommands).GetMethod("CrucibleFixtureSave", BindingFlags.Public | BindingFlags.Static),
                    new List<string> { "label" });

            if (!_listRegistered)
                _listRegistered = GameBridge.RegisterCommand("crucible_fixture_list",
                    typeof(FixtureCommands).GetMethod("CrucibleFixtureList", BindingFlags.Public | BindingFlags.Static),
                    new List<string>());
        }

        // ============================================================== crucible_party_list

        /// <summary>crucible_party_list — one line per party member: slot, class id, name, hp.</summary>
        public static void CruciblePartyList()
        {
            LastResult = null;
            try
            {
                List<object> party;
                string error;
                if (!TryGetParty(out party, out error)) { LastResult = "error: " + error; return; }

                StringBuilder sb = new StringBuilder();
                sb.Append("party count=").Append(party.Count);
                for (int i = 0; i < party.Count; i++)
                {
                    object character = FindComponent(party[i], "CharacterComponent");
                    sb.Append("\n[").Append(i).Append("] ")
                      .Append("classId=").Append(StringOf(ReadMember(character, "ConfigName")))
                      .Append(" name=").Append(StringOf(ReadMember(character, "DisplayName")))
                      .Append(" hp=").Append(StringOf(ReadMember(character, "CurrentHealth")))
                      .Append(" level=").Append(StringOf(ReadMember(character, "ExtraLevel")))
                      .Append(" guid=").Append(StringOf(ReadMember(party[i], "Guid")));
                }
                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_party_list threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_party_set_class

        /// <summary>
        /// crucible_party_set_class &lt;slot&gt; &lt;classConfigName&gt; — rewrites
        /// <c>CharacterComponent.ConfigName</c>, which is where a character's class lives.
        ///
        /// Reports the value before and after and whether it actually changed, because a reflective
        /// write that silently no-ops is the failure this harness exists to catch. This changes the
        /// class ID only; equipment, things and stat modifiers are whatever the base fixture had.
        /// </summary>
        public static void CruciblePartySetClass(string slot, string classConfigName)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(classConfigName))
                {
                    LastResult = "error: usage: crucible_party_set_class <slot> <classConfigName>";
                    return;
                }

                int index;
                if (!int.TryParse((slot ?? "").Trim(), out index))
                {
                    LastResult = "error: slot must be an integer, got '" + slot + "'";
                    return;
                }

                List<object> party;
                string error;
                if (!TryGetParty(out party, out error)) { LastResult = "error: " + error; return; }

                if (index < 0 || index >= party.Count)
                {
                    LastResult = "error: slot " + index + " out of range (party count=" + party.Count + ")";
                    return;
                }

                object character = FindComponent(party[index], "CharacterComponent");
                if (character == null) { LastResult = "error: slot " + index + " has no CharacterComponent"; return; }

                object before = ReadMember(character, "ConfigName");

                FieldInfo field = AccessTools.Field(character.GetType(), "ConfigName");
                if (field == null) { LastResult = "error: CharacterComponent.ConfigName field not found"; return; }
                field.SetValue(character, classConfigName.Trim());

                object after = ReadMember(character, "ConfigName");
                bool changed = !string.Equals(StringOf(before), StringOf(after), StringComparison.Ordinal);

                LastResult = "slot=" + index
                    + " classBefore=" + StringOf(before)
                    + " classAfter=" + StringOf(after)
                    + " changed=" + changed;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_party_set_class threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_fixture_save

        /// <summary>
        /// crucible_fixture_save &lt;label&gt; — persists the live run under a NEWLY GENERATED run id.
        ///
        /// Never overwrites: the id is always a fresh Guid, because this save folder also holds the
        /// owner's live co-op campaign. WriteSaveData returns a Task which is deliberately NOT
        /// awaited (blocking the game thread would deadlock the pump); the reported id is what a
        /// following crucible_fixture_list should show once the write lands.
        /// </summary>
        public static void CrucibleFixtureSave(string label)
        {
            LastResult = null;
            try
            {
                object env = GameBridge.GetEnv();
                if (env == null) { LastResult = "error: Env unavailable (no run loaded?)"; return; }

                object gameRun = ReadMember(env, "GameRun");
                if (gameRun == null) { LastResult = "error: Env.GameRun is null — no run is loaded, nothing to save"; return; }

                object user = ReadMember(env, "User");
                if (user == null) { LastResult = "error: Env.User is null"; return; }

                Type saveHelper = AccessTools.TypeByName("SaveGameHelper");
                if (saveHelper == null) { LastResult = "error: SaveGameHelper type not found"; return; }

                MethodInfo write = AccessTools.Method(saveHelper, "WriteSaveData");
                if (write == null) { LastResult = "error: SaveGameHelper.WriteSaveData not found"; return; }

                string newRunId = Guid.NewGuid().ToString();

                int runsBefore = CountOf(ReadMember(env, "GameRuns"));
                object task = write.Invoke(null, new object[] { newRunId, gameRun, user });

                LastResult = "wrote fixture"
                    + " label=" + (string.IsNullOrEmpty(label) ? "(none)" : label)
                    + " newRunId=" + newRunId
                    + " runsBefore=" + runsBefore
                    + " taskStarted=" + (task != null)
                    + " NOTE: the write is async and not awaited; confirm with crucible_fixture_list.";

                if (_log != null) _log.LogInfo("crucible_fixture_save: " + LastResult);
            }
            catch (TargetInvocationException ex)
            {
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                LastResult = "error: WriteSaveData threw: " + root.GetType().Name + ": " + root.Message;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_fixture_save threw: " + ex.Message;
            }
        }

        // ============================================================== crucible_fixture_list

        /// <summary>crucible_fixture_list — every run id the game knows about.</summary>
        public static void CrucibleFixtureList()
        {
            LastResult = null;
            try
            {
                object env = GameBridge.GetEnv();
                if (env == null) { LastResult = "error: Env unavailable"; return; }

                object runs = ReadMember(env, "GameRuns");
                IEnumerable list = runs as IEnumerable;
                if (list == null) { LastResult = "error: Env.GameRuns is not enumerable"; return; }

                StringBuilder sb = new StringBuilder();
                int n = 0;
                foreach (object item in list)
                {
                    sb.Append("\n[").Append(n).Append("] ").Append(item == null ? "(null)" : item.ToString());
                    n++;
                }
                LastResult = "gameRuns count=" + n + sb;
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_fixture_list threw: " + ex.Message;
            }
        }

        // ============================================================== helpers

        private static bool TryGetParty(out List<object> party, out string error)
        {
            return PartyAccess.TryGetParty(out party, out error);
        }

        private static object FindComponent(object entity, string componentTypeName)
        {
            return PartyAccess.FindComponent(entity, componentTypeName);
        }

        private static object ReadMember(object instance, string name)
        {
            return PartyAccess.ReadMember(instance, name);
        }

        private static string StringOf(object value)
        {
            return value == null ? "(null)" : value.ToString();
        }

        private static int CountOf(object collection)
        {
            ICollection c = collection as ICollection;
            return c == null ? -1 : c.Count;
        }
    }
}

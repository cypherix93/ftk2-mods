using System;
using System.Collections.Generic;
using System.Text;

namespace FTK2Mods.Crucible
{
    /// <summary>A parsed console-style command line: a name plus already-split string arguments.</summary>
    public sealed class CommandRequest
    {
        public string Name;
        public string[] Args;
        public string CorrelationId;

        /// <summary>
        /// Splits a console-style line into name + args. Double quotes group an argument that
        /// contains spaces; an unterminated quote is an error rather than a silent truncation
        /// (a truncated arg would execute a *different* command than the caller asked for).
        /// </summary>
        public static bool TryParse(string commandLine, out CommandRequest request, out string error)
        {
            request = null;
            error = null;
            if (string.IsNullOrEmpty(commandLine) || commandLine.Trim().Length == 0)
            {
                error = "empty command";
                return false;
            }

            List<string> tokens = new List<string>();
            StringBuilder current = new StringBuilder();
            bool inQuotes = false;
            bool hasToken = false;

            for (int i = 0; i < commandLine.Length; i++)
            {
                char c = commandLine[i];
                if (c == '"') { inQuotes = !inQuotes; hasToken = true; continue; }
                if (!inQuotes && (c == ' ' || c == '\t'))
                {
                    if (hasToken) { tokens.Add(current.ToString()); current.Length = 0; hasToken = false; }
                    continue;
                }
                current.Append(c);
                hasToken = true;
            }

            if (inQuotes) { error = "unterminated quote"; return false; }
            if (hasToken) tokens.Add(current.ToString());
            if (tokens.Count == 0) { error = "empty command"; return false; }

            CommandRequest result = new CommandRequest();
            result.Name = tokens[0];
            result.Args = tokens.Count > 1 ? tokens.GetRange(1, tokens.Count - 1).ToArray() : new string[0];
            request = result;
            return true;
        }
    }

    public enum GateVerdict
    {
        Allow,
        DeniedNotAllowlisted,
        DeniedMultiplayer
    }

    /// <summary>
    /// Safety policy for command execution (SPEC §9, MULTIPLAYER.md R5). In an online session every
    /// command that is not demonstrably read-only is refused unless the operator explicitly opted in.
    ///
    /// The read-only list is an allowlist, not a denylist, on purpose: an unrecognized command —
    /// including one a future game patch adds — is treated as a mutation. Guessing the other way
    /// would let an unknown verb desync a live session.
    /// </summary>
    public static class CommandGate
    {
        private static readonly string[] ReadOnlyCommands = new string[]
        {
            "toggleui", "togglevenuegrid", "toggleplayerhuds",
            "printdungeonconfighash", "printdialogueconfighash",
            "enablegamerandomstacktracerecording"
        };

        public static bool IsReadOnly(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lowered = name.ToLowerInvariant();
            for (int i = 0; i < ReadOnlyCommands.Length; i++)
                if (string.Equals(ReadOnlyCommands[i], lowered, StringComparison.Ordinal)) return true;
            return false;
        }

        public static GateVerdict Evaluate(string name, bool inOnlineSession, bool allowMutationsInMp, string[] allowlist)
        {
            if (allowlist != null && allowlist.Length > 0)
            {
                bool listed = false;
                string lowered = name == null ? string.Empty : name.ToLowerInvariant();
                for (int i = 0; i < allowlist.Length; i++)
                {
                    string entry = allowlist[i] == null ? string.Empty : allowlist[i].Trim().ToLowerInvariant();
                    if (entry.Length > 0 && string.Equals(entry, lowered, StringComparison.Ordinal)) { listed = true; break; }
                }
                if (!listed) return GateVerdict.DeniedNotAllowlisted;
            }

            if (inOnlineSession && !allowMutationsInMp && !IsReadOnly(name))
                return GateVerdict.DeniedMultiplayer;

            return GateVerdict.Allow;
        }
    }
}

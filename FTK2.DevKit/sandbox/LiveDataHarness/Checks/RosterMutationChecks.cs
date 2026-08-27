using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace LiveDataHarness.Checks
{
    /// <summary>
    /// CF_ROSTER_MUTATION — a SOURCE scan, not a data check.
    ///
    /// <para><b>The rule.</b> No file under <c>FTK2.ClassForge/src/ClassForge.Plugin</c> may call
    /// <c>.Remove(</c> on <c>CombatState.Entities</c>, <c>CombatState.RoundEntities</c> or
    /// <c>GameRun.Entities</c>, unless the call carries an explicit <c>// CF-ROSTER-OK: &lt;reason&gt;</c>
    /// line comment. Exempted call sites are not silently tolerated: this check prints the reason each
    /// one gave into the report, so the exemption list stays a reviewed artefact rather than a
    /// loophole.</para>
    ///
    /// <para><b>Why a source scan is the right instrument.</b> The failure it guards has no data
    /// signature at all — it is a rendering path deleting a combatant, and it only shows up as
    /// divergence between two live peers, which is precisely what this harness cannot run. What it CAN
    /// do is make the shape of the code unrepresentable, offline and in milliseconds, the same way
    /// <c>ClassForge.PackCheck</c>'s <c>CF_PACK_ABILITY_ON_EQUIPMENT</c> makes "pack-authored ability id
    /// on an EQUIPMENT Thing" unrepresentable rather than waiting for the crash in play.</para>
    ///
    /// <para><b>The failure it encodes (P0 Fix 5, 2026-08-25).</b>
    /// <c>SummonLeakPatches.ClearTileRenderState_Prefix</c> used to delete any combatant whose 3D actor
    /// could not be built. <c>SummonVisuals.TryBuildActor</c> fails on LOCAL art and timing conditions
    /// — <c>_canvas3D == null</c>, a prefab that has not streamed in — so two peers trivially disagreed
    /// about the roster. <c>AIHelper.ForceAiDecision</c> (AIHelper.cs:508-511), which every AI path
    /// funnels through, then does
    /// <c>list = VenueHelper.GetTargetableTiles(...); if (list.Count &gt; 0) …Random.ShuffleList(list);</c>,
    /// and <c>GameRandom.ShuffleList</c> (GameRandom.cs:222-236) takes EXACTLY <c>list.Count</c> draws
    /// from the SHARED stream. A different roster is therefore a different number of draws on the very
    /// next AI turn, i.e. permanent divergence — not a cosmetic difference.</para>
    ///
    /// <para>The scanner is deliberately blunt in the safe direction: it treats a local whose
    /// initializer mentions <c>Entities</c> as a roster alias too, because the original defect's worst
    /// line was <c>entities.Remove(e)</c> against <c>var entities = combatState?.Entities;</c> and a
    /// scanner that only matched literal member chains would have missed it.</para>
    /// </summary>
    public static class RosterMutationChecks
    {
        /// <summary>The marker a call site must carry to be allowed through.</summary>
        public const string ExemptionMarker = "CF-ROSTER-OK";

        /// <summary>Member names that ARE the replicated combat roster.</summary>
        private static readonly string[] RosterMembers = { "Entities", "RoundEntities" };

        public static void Register(CheckRunner runner)
        {
            runner.Section("Roster mutation (source scan of ClassForge.Plugin)");

            string repo = PackRoots.RepoRoot();
            string pluginDir = repo == null
                ? null
                : Path.Combine(repo, "FTK2.ClassForge", "src", "ClassForge.Plugin");

            List<string> offenders = new List<string>();
            List<string> exemptions = new List<string>();
            int filesScanned = 0;
            int callsMatched = 0;

            if (pluginDir != null && Directory.Exists(pluginDir))
            {
                foreach (string file in SourceFiles(pluginDir))
                {
                    filesScanned++;
                    string label = file.Substring(repo.Length).TrimStart('\\', '/').Replace('\\', '/');
                    callsMatched += Scan(label, File.ReadAllLines(file), offenders, exemptions);
                }
            }

            // Exemptions are RECORDED, never hidden. Warnings never fail a run (repo rule), so this is
            // the visible ledger of every deliberate roster mutation in the plugin.
            runner.Warn("CF_ROSTER_MUTATION exempted call sites", exemptions);

            runner.Case("no ClassForge.Plugin source removes an entity from a replicated roster", delegate
            {
                Check.True(pluginDir != null && Directory.Exists(pluginDir),
                    "ClassForge.Plugin source directory found (scan is vacuous without it): " +
                    (pluginDir ?? "<repo root not resolvable>"));
                Check.AtLeast(20, filesScanned, "ClassForge.Plugin .cs files scanned");

                // Non-vacuity: the scanner must be matching REAL call sites, not silently matching
                // nothing. SummonLeakPatches.Purge legitimately banishes SUMMON-tagged creatures at
                // combat entry/exit and carries CF-ROSTER-OK, so a zero here means the matcher broke.
                Check.AtLeast(1, callsMatched,
                    "roster .Remove( call sites matched in ClassForge.Plugin (0 means the matcher stopped working)");

                Check.Empty(offenders, "unexempted roster mutations in ClassForge.Plugin");
            });

            runner.Case("NEGATIVE control: the scanner flags removals and honours exemptions", delegate
            {
                // Without this, "0 offenders" could equally mean "the scanner never matches anything".
                // Each snippet below is a real shape the plugin has contained at some point.

                List<string> bad = new List<string>();
                List<string> badEx = new List<string>();
                int n = Scan("synthetic", new string[]
                {
                    "var entities = combatState?.Entities;",          // the alias the real defect used
                    "entities.Remove(e);",
                    "combatState.RoundEntities?.Remove(e);",
                    "RouterHelper.Env?.GameRun?.Entities?.Remove(e);",
                }, bad, badEx);
                Check.Exactly(3, n, "synthetic roster .Remove( call sites matched");
                Check.Exactly(3, bad.Count, "synthetic unexempted removals flagged");
                Check.Empty(badEx, "synthetic exemptions (there are none in this snippet)");
                Check.True(bad.Any(delegate (string s) { return s.Contains("entities.Remove(e)"); }),
                    "the local-alias removal 'entities.Remove(e)' must be flagged — it is the exact line the " +
                    "P0 Fix 5 defect shipped as, and a member-chain-only matcher would miss it");

                List<string> okOffenders = new List<string>();
                List<string> okEx = new List<string>();
                int m = Scan("synthetic-exempt", new string[]
                {
                    "// CF-ROSTER-OK: deterministic combat-entry purge, keyed on replicated state only.",
                    "gameRun.Entities?.Remove(e);",
                    "gameRun.CombatState?.Entities?.Remove(e);",
                    "gameRun.CombatState?.RoundEntities?.Remove(e);",
                    "",
                    "DoSomethingElse();",
                    "combatState.Entities.Remove(e); // CF-ROSTER-OK: trailing form.",
                    "combatState.Entities.Remove(z);",
                }, okOffenders, okEx);
                Check.Exactly(5, m, "synthetic exempted-block call sites matched");
                Check.Exactly(4, okEx.Count,
                    "one heading comment must cover the whole contiguous removal trio, plus the trailing form");
                Check.True(okEx.All(delegate (string s) { return s.Contains("deterministic") || s.Contains("trailing form"); }),
                    "the recorded exemption must carry the reason text, otherwise the marker is a rubber stamp");
                Check.Exactly(1, okOffenders.Count,
                    "the heading comment must NOT leak past an unrelated statement — the last removal is unexempted");
                Check.True(okOffenders[0].Contains("Remove(z)"),
                    "the unexempted removal below the unrelated statement is the one that must be flagged");

                // A comment mentioning .Remove() must never be mistaken for a call (RecipeEngineHost.cs:25
                // contains exactly that in an XML doc), and non-roster collections are none of our business.
                List<string> quiet = new List<string>();
                List<string> quietEx = new List<string>();
                int k = Scan("synthetic-quiet", new string[]
                {
                    "/// <c>.Remove()</c> — TM §6 hazard 3.)</para>",
                    "// combatState.Entities.Remove(e); // commented out",
                    "cc.Things.Remove(shippedBall);",
                    "if (e.Has<CombatComponent>()) e.Remove<CombatComponent>();",
                }, quiet, quietEx);
                Check.Exactly(0, k, "comments, commented-out code and non-roster collections must not match");
                Check.Empty(quiet, "false positives");
            });
        }

        /// <summary>Every .cs file under <paramref name="dir"/> except build output. Ordinal-sorted so
        /// two runs report identically (AC5).</summary>
        private static IEnumerable<string> SourceFiles(string dir)
        {
            List<string> files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(delegate (string f)
                {
                    string n = f.Replace('\\', '/');
                    return n.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) < 0
                        && n.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase) < 0;
                })
                .ToList();
            files.Sort(StringComparer.Ordinal);
            return files;
        }

        /// <summary>
        /// Scans one file's lines. Appends an offender string for every unexempted roster removal and an
        /// exemption string for every exempted one; returns the total number of roster removals matched,
        /// which is what proves the matcher is alive.
        /// </summary>
        public static int Scan(string label, string[] lines, List<string> offenders, List<string> exemptions)
        {
            HashSet<string> roster = new HashSet<string>(RosterMembers, StringComparer.Ordinal);

            // Locals that ALIAS a roster. `var entities = combatState?.Entities;` is how the P0 Fix 5
            // defect actually wrote its worst line, so an alias is treated as the roster itself.
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (IsComment(t)) continue;
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                    t, @"^(?:var|List<Entity>)\s+([A-Za-z_]\w*)\s*=\s*([^;]*Entities[^;]*);");
                if (m.Success) roster.Add(m.Groups[1].Value);
            }

            int matched = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                if (IsComment(raw.Trim())) continue;

                int from = 0;
                while (true)
                {
                    int at = raw.IndexOf(".Remove(", from, StringComparison.Ordinal);
                    if (at < 0) break;
                    from = at + 8;

                    // A ".Remove(" inside a trailing comment on an otherwise-real line is still a comment.
                    int comment = raw.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0 && comment < at) continue;

                    string receiver = TerminalIdentifier(raw.Substring(0, at));
                    if (receiver == null || !roster.Contains(receiver)) continue;

                    matched++;
                    string where = label + ":" + (i + 1).ToString(CultureInfo.InvariantCulture);
                    string reason = ExemptionReason(lines, i);
                    if (reason != null)
                        exemptions.Add(where + " — " + raw.Trim() + " — reason: " + reason);
                    else
                        offenders.Add(where + ": " + raw.Trim() +
                            " — removes an entity from the replicated combat roster ('" + receiver + "'). " +
                            "A roster that differs between peers changes GetTargetableTiles(...).Count and " +
                            "therefore how many draws ShuffleList takes from the shared RNG on the next AI " +
                            "turn (AIHelper.cs:508-511, GameRandom.cs:222-236) — a permanent desync. Make the " +
                            "combatant invisible, not absent; or justify it with a '// " + ExemptionMarker +
                            ": <reason>' comment on this line or in the comment block directly above it.");
                }
            }
            return matched;
        }

        private static bool IsComment(string trimmed)
        {
            return trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal);
        }

        /// <summary>
        /// The last identifier of the receiver expression immediately preceding a <c>.Remove(</c>.
        /// <c>gameRun.CombatState?.Entities?</c> -&gt; <c>Entities</c>; <c>entities</c> -&gt;
        /// <c>entities</c>. Null when the receiver is not a plain identifier chain (an indexer, a call
        /// result), which this check deliberately does not try to reason about.
        /// </summary>
        private static string TerminalIdentifier(string before)
        {
            int end = before.Length;
            int i = end - 1;
            while (i >= 0)
            {
                char c = before[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '?') { i--; continue; }
                break;
            }
            string chain = before.Substring(i + 1);
            string[] parts = chain.Split('.', '?');
            for (int p = parts.Length - 1; p >= 0; p--)
                if (parts[p].Length > 0) return parts[p];
            return null;
        }

        /// <summary>
        /// The reason text of a <c>CF-ROSTER-OK</c> marker covering line <paramref name="index"/>, or
        /// null. The marker counts on the line itself, or in the comment block that HEADS the
        /// removal statement it belongs to — the walk upward passes over blank lines and over sibling
        /// roster-removal lines (the three-line
        /// <c>Entities / CombatState.Entities / CombatState.RoundEntities</c> trio is one decision, not
        /// three, and repeating the justification on each would make it wallpaper), and stops at the
        /// first unrelated statement.
        /// </summary>
        private static string ExemptionReason(string[] lines, int index)
        {
            string self = Reason(lines[index]);
            if (self != null) return self;

            for (int i = index - 1; i >= 0; i--)
            {
                string t = lines[i].Trim();
                if (t.Length == 0) continue;                                        // blank line
                if (t.IndexOf(".Remove(", StringComparison.Ordinal) >= 0) continue; // sibling removal
                if (!IsComment(t)) break;                                           // unrelated statement
                string r = Reason(t);
                if (r != null) return r;
            }
            return null;
        }

        private static string Reason(string line)
        {
            int at = line.IndexOf(ExemptionMarker, StringComparison.Ordinal);
            if (at < 0) return null;
            string rest = line.Substring(at + ExemptionMarker.Length).TrimStart(':', ' ', '\t').Trim();
            return rest.Length == 0 ? "<no reason given>" : rest;
        }
    }
}

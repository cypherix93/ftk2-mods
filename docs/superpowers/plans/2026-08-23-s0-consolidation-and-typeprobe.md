# S0 Consolidation + TypeProbe Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Collapse the repo's three divergent branches onto a single `ben` branch, then build `TypeProbe` — the reflection instrument every later plan depends on to verify game member names before writing code against them.

**Architecture:** Consolidation is a pair of near-trivial merges: both feature branches are strictly ahead of `engine/eor-rehost` with zero commits behind, and they touch disjoint file sets, so no content conflict is expected. `TypeProbe` is a `net10.0` console under `FTK2.DevKit/sandbox/TypeProbe/` that loads the retail `FTK2.dll` out-of-process via `Assembly.LoadFrom` plus an `AssemblyResolve` fallback — no compile-time game reference, so it builds on a machine with no game installed. Its member-describing logic is a pure function over `IEnumerable<Type>`, which makes it unit-testable against BCL types with no game and no Unity.

**Tech Stack:** C# · `net10.0` · `System.Reflection` · BCL only, no NuGet · repo console-runner test pattern (`FTK2.Crucible/src/Crucible.Core.Tests/TestHarness.cs`)

**Spec:** `docs/superpowers/specs/2026-08-23-crucible-autopilot-design.md` (§2 grounding rule, §9 TypeProbe, §11 build order)

## Global Constraints

- **No NuGet `PackageReference` anywhere.** BCL + `ProjectReference` only. Repo build rule stated in `FTK2.DevKit/src/DevKit.Plugin/DevKit.Plugin.csproj:31`.
- **No compile-time reference to `FTK2.dll`, `UnityEngine*.dll`, or `BepInEx.dll`** in any project this plan touches. All game access is reflective.
- **No NuGet test framework.** Tests are a console runner returning exit code 0 on success, per `Crucible.Core.Tests` / `DevKit.Core.Tests`.
- **Target frameworks:** `net10.0` for consoles and tests, `LangVersion 7.3`, `ImplicitUsings disable`, `Nullable disable` — matching `Crucible.Core.Tests.csproj` exactly.
- **Sandbox naming:** root namespace matches assembly name (`TypeProbe`), per `docs/research/build-template-notes.md` §7 and the `sandbox/<X>Sim` convention.
- **Every check has a negative control.** A test that cannot fail is a plan failure. Bar set by `FTK2.DevKit/sandbox/DeterminismHarness.Tests` (6 negative controls).
- **The game directory is read-only.** No code in this plan writes outside the repo.
- **Grounding rule (spec §2):** no game member name may be written into code until dumped from the retail assembly. TypeProbe is the instrument that makes this enforceable; it must therefore not itself depend on any game member name.
- **Verified game path:** `C:\Program Files (x86)\Steam\steamapps\common\For The King II`. Managed dir: `<game>\For The King II_Data\Managed`. The README's `E:\…` path is stale.

## File Structure

| File | Responsibility |
|---|---|
| `FTK2.DevKit/sandbox/TypeProbe/TypeProbe.csproj` | `net10.0` console project, no package refs |
| `FTK2.DevKit/sandbox/TypeProbe/Probe.cs` | **Pure** — describes a type's members from `IEnumerable<Type>`. No file I/O, no game. Unit-testable. |
| `FTK2.DevKit/sandbox/TypeProbe/GameAssembly.cs` | Loads `FTK2.dll` reflectively with `AssemblyResolve` fallback; degrades to a clear error |
| `FTK2.DevKit/sandbox/TypeProbe/Report.cs` | **Pure** — renders a `ProbeResult` as the markdown field-map table the grounding rule requires |
| `FTK2.DevKit/sandbox/TypeProbe/Program.cs` | CLI: arg parsing, exit codes, output |
| `FTK2.DevKit/sandbox/TypeProbe/README.md` | One command, exit codes, what it cannot do |
| `FTK2.DevKit/sandbox/TypeProbe.Tests/TypeProbe.Tests.csproj` | `net10.0` console test runner |
| `FTK2.DevKit/sandbox/TypeProbe.Tests/TestHarness.cs` | Copy of the repo's minimal harness |
| `FTK2.DevKit/sandbox/TypeProbe.Tests/ProbeTests.cs` | Probe + Report tests, including negative controls |
| `FTK2.DevKit/sandbox/TypeProbe.Tests/Program.cs` | Test entry point |

The split matters: `Probe.cs` and `Report.cs` are pure so they can be driven with deliberately wrong inputs, which is what makes the negative controls possible. `GameAssembly.cs` is the only file that touches the disk, and it is deliberately thin.

---

### Task 1: Consolidate everything onto the `ben` branch

**Files:**
- Modify: git refs only. No source changes expected.
- Delete (after verification): the stale untracked working copy at `C:\Users\ben\repos\ftk2-mods-ben\FTK2.Crucible\`

**Interfaces:**
- Consumes: nothing.
- Produces: branch `ben` containing `engine/eor-rehost` + `feat/crucible-mcp` + `feat/desync-detector`. **Every later task, plan, and worktree is created from `ben`.**

**Grounding already done (2026-08-23) — do not re-derive:**

| Fact | Value |
|---|---|
| `feat/crucible-mcp` vs `engine/eor-rehost` | 0 behind, 1 ahead |
| `feat/desync-detector` vs `engine/eor-rehost` | 0 behind, 3 ahead |
| `engine/mp-tripwire` vs `engine/eor-rehost` | 0 ahead — fully contained, nothing unique |
| `eor-rehost/ben` vs `engine/eor-rehost` | 0/0 — identical commit `9836968` |
| `master` vs `engine/eor-rehost` | 29 behind |
| File overlap between the two feature branches | **none** (`FTK2.Crucible/*` + `tools/deploy.ps1` vs `FTK2.DevKit/*` + `tools/run-determinism.ps1`) |

**The `ftk2-mods-ben` dirty state is stale and safe to discard — verified 2026-08-23:**

| Dirty item | Verdict |
|---|---|
| untracked `FTK2.Crucible/` | **Superseded.** Written 13:55–14:09 Aug 15; the committed copy is 15:20 and larger (`RpcServer.cs` 377 vs 336 lines — the extra lines are commit 2d25e72's three startup fixes). `data/Scenarios/` is an empty directory. |
| untracked `docs/superpowers/plans/2026-08-15-crucible-m1-m3.md` | **Byte-identical** to the copy committed on `feat/crucible-mcp`. |
| modified `tools/deploy.ps1` | **Content-identical** to the committed version; differs only by line-ending normalization. |

- [ ] **Step 1: Confirm the working tree is clean and capture the starting point**

```bash
cd /c/Users/ben/repos/ftk2-mods-crucible
git status --porcelain
git rev-parse engine/eor-rehost feat/crucible-mcp feat/desync-detector
```

Expected: `git status --porcelain` prints nothing. If it prints anything, stop and resolve before merging — the `StateReader` `PlayerList` fix from 2026-08-23 may still be uncommitted; commit it on `feat/crucible-mcp` first so the merge carries it.

- [ ] **Step 2: Create the `ben` branch from `engine/eor-rehost`**

```bash
git checkout engine/eor-rehost
git checkout -b ben
```

`eor-rehost/ben` already exists at the identical commit and is now redundant; leave it alone for now — Step 7 removes it only if git agrees it is fully merged.

- [ ] **Step 3: Merge the Crucible branch**

```bash
git merge --no-ff feat/crucible-mcp -m "Merge feat/crucible-mcp into ben"
```

Expected: clean merge. If a conflict appears, stop — the grounding above says none should exist, so a conflict means the branches moved since 2026-08-23 and this plan's assumptions need rechecking.

- [ ] **Step 4: Merge the desync branch**

```bash
git merge --no-ff feat/desync-detector -m "Merge feat/desync-detector into ben"
```

Expected: clean merge, for the same reason.

- [ ] **Step 5: Verify every test suite still passes on the merged branch**

```bash
dotnet run --project FTK2.Crucible/src/Crucible.Core.Tests -c Release
dotnet run --project FTK2.DevKit/src/DevKit.Core.Tests -c Release
dotnet run --project FTK2.DevKit/sandbox/DeterminismHarness.Tests -c Release
```

Expected: all three exit 0. Record the test counts; the Crucible suite was 36 tests passing on 2026-08-23.

- [ ] **Step 6: Retire the stale `ftk2-mods-ben` working copy**

The table above establishes that nothing there is unique. Confirm once more, then discard:

```bash
cd /c/Users/ben/repos/ftk2-mods-ben
git status --porcelain
diff -r FTK2.Crucible /c/Users/ben/repos/ftk2-mods-crucible/FTK2.Crucible \
  -x bin -x obj -x '*.dll' -x '*.pdb' | head
```

If — and only if — the diff shows nothing beyond the known staleness above, remove the untracked copies and restore the tracked file:

```bash
rm -rf FTK2.Crucible docs/superpowers/plans/2026-08-15-crucible-m1-m3.md
git checkout -- tools/deploy.ps1
git status --porcelain    # expect empty
```

If the diff shows anything unexpected, **stop and report it** rather than deleting. This is the one destructive step in the plan.

- [ ] **Step 7: Delete the fully-contained branches**

```bash
git branch -d engine/mp-tripwire
git branch -d eor-rehost/ben
```

Expected: both succeed without `-D`. Git refuses `-d` on an unmerged branch, so success here *is* the proof they were fully contained. If git refuses either, do not force — investigate what is unique on it and report.

- [ ] **Step 8: Create the worktree root for later plans**

Subsequent plans (S6, S1, S3, …) each get an isolated worktree branched from `ben`:

```bash
cd /c/Users/ben/repos/ftk2-mods-crucible
git worktree add ../ftk2-wt-s6 -b s6/live-data-harness ben
git worktree list
```

Expected: `git worktree list` shows the new worktree. This one is for the next plan (S6); create further worktrees the same way, one per plan, always branching from `ben`.

- [ ] **Step 9: Report state**

No extra commit needed — the merges created their own. Report to the reviewer: the merged test counts, that `git status` is clean in both checkouts, and the output of `git branch -a`.

---

### Task 2: Probe — pure member description with negative controls

**Files:**
- Create: `FTK2.DevKit/sandbox/TypeProbe/TypeProbe.csproj`
- Create: `FTK2.DevKit/sandbox/TypeProbe/Probe.cs`
- Create: `FTK2.DevKit/sandbox/TypeProbe.Tests/TypeProbe.Tests.csproj`
- Create: `FTK2.DevKit/sandbox/TypeProbe.Tests/TestHarness.cs`
- Create: `FTK2.DevKit/sandbox/TypeProbe.Tests/ProbeTests.cs`
- Create: `FTK2.DevKit/sandbox/TypeProbe.Tests/Program.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `struct TypeProbe.MemberEntry { string Kind; string TypeName; string Name; }`
  - `sealed class TypeProbe.ProbeResult { string RequestedName; bool Found; string FullName; List<MemberEntry> Members; }`
  - `static ProbeResult TypeProbe.Probe.Describe(IEnumerable<Type> types, string simpleName, bool includeMethods)`

- [ ] **Step 1: Create the console project**

`FTK2.DevKit/sandbox/TypeProbe/TypeProbe.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>TypeProbe</RootNamespace>
    <AssemblyName>TypeProbe</AssemblyName>
    <LangVersion>7.3</LangVersion>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <!-- No PackageReference: repo build rules bar NuGet. No game reference: all access is
         reflective, so this builds on a machine with no game installed. -->
  </PropertyGroup>

</Project>
```

- [ ] **Step 2: Create the test project and harness**

`FTK2.DevKit/sandbox/TypeProbe.Tests/TypeProbe.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>TypeProbe.Tests</RootNamespace>
    <AssemblyName>TypeProbe.Tests</AssemblyName>
    <LangVersion>7.3</LangVersion>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\TypeProbe\TypeProbe.csproj" />
  </ItemGroup>

</Project>
```

`FTK2.DevKit/sandbox/TypeProbe.Tests/TestHarness.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace TypeProbe.Tests
{
    /// <summary>
    /// Minimal console test harness (no NuGet test framework — the repo build rules bar package
    /// references). Every test name is printed; exit code 0 means all passed.
    /// Ported from FTK2.Crucible/src/Crucible.Core.Tests/TestHarness.cs.
    /// </summary>
    internal static class TestHarness
    {
        private static int _passed;
        private static readonly List<string> Failures = new List<string>();

        internal static void Run(string name, Action body)
        {
            try
            {
                body();
                _passed++;
                Console.WriteLine("  PASS  " + name);
            }
            catch (Exception ex)
            {
                Failures.Add(name + ": " + ex.Message);
                Console.WriteLine("  FAIL  " + name);
                Console.WriteLine("        " + ex.Message);
            }
        }

        internal static void RunInCulture(string name, string cultureName, Action body)
        {
            Run(name + " [culture=" + cultureName + "]", delegate
            {
                CultureInfo previous = Thread.CurrentThread.CurrentCulture;
                try
                {
                    Thread.CurrentThread.CurrentCulture = new CultureInfo(cultureName);
                    body();
                }
                finally
                {
                    Thread.CurrentThread.CurrentCulture = previous;
                }
            });
        }

        internal static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title);
        }

        internal static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        internal static int Report()
        {
            Console.WriteLine();
            Console.WriteLine("---------------------------------------------");
            Console.WriteLine("Tests run: " + (_passed + Failures.Count)
                              + "   passed: " + _passed + "   failed: " + Failures.Count);
            if (Failures.Count == 0)
            {
                Console.WriteLine("ALL TESTS PASSED");
                return 0;
            }
            foreach (string f in Failures) Console.WriteLine("  " + f);
            return 1;
        }
    }
}
```

- [ ] **Step 3: Write the failing tests**

`FTK2.DevKit/sandbox/TypeProbe.Tests/ProbeTests.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace TypeProbe.Tests
{
    internal static class ProbeTests
    {
        private static Type[] Sample()
        {
            return new Type[] { typeof(string), typeof(Uri), typeof(Version) };
        }

        internal static void RunAll()
        {
            TestHarness.Section("Probe");

            TestHarness.Run("finds a type by simple name", delegate
            {
                ProbeResult r = Probe.Describe(Sample(), "Uri", false);
                TestHarness.Assert(r.Found, "expected Uri to be found");
                TestHarness.Assert(r.FullName == "System.Uri", "expected full name System.Uri, got " + r.FullName);
            });

            TestHarness.Run("reports known members of a known type", delegate
            {
                ProbeResult r = Probe.Describe(Sample(), "Uri", false);
                bool hasHost = false;
                foreach (MemberEntry m in r.Members) if (m.Name == "Host") hasHost = true;
                TestHarness.Assert(hasHost, "expected Uri.Host among members");
            });

            // NEGATIVE CONTROL: a missing type must report not-found, never a vacuous pass.
            TestHarness.Run("NEGATIVE: missing type reports not found", delegate
            {
                ProbeResult r = Probe.Describe(Sample(), "NoSuchTypeAnywhere", false);
                TestHarness.Assert(!r.Found, "a missing type must not report Found");
                TestHarness.Assert(r.Members.Count == 0, "a missing type must report no members");
            });

            // NEGATIVE CONTROL: output must be ordinal-sorted, not merely stable. An unsorted
            // reflection order is what a machine-dependent member enumeration looks like.
            TestHarness.Run("NEGATIVE: members are ordinal-sorted regardless of input order", delegate
            {
                ProbeResult a = Probe.Describe(Sample(), "Uri", true);
                List<Type> reversed = new List<Type>(Sample());
                reversed.Reverse();
                ProbeResult b = Probe.Describe(reversed, "Uri", true);
                TestHarness.Assert(a.Members.Count == b.Members.Count, "member counts differ across input orders");
                for (int i = 0; i < a.Members.Count; i++)
                {
                    TestHarness.Assert(a.Members[i].Name == b.Members[i].Name,
                        "member order differs at " + i + ": " + a.Members[i].Name + " vs " + b.Members[i].Name);
                }
                for (int i = 1; i < a.Members.Count; i++)
                {
                    string prev = a.Members[i - 1].Kind + "\u0000" + a.Members[i - 1].Name;
                    string cur = a.Members[i].Kind + "\u0000" + a.Members[i].Name;
                    TestHarness.Assert(string.CompareOrdinal(prev, cur) <= 0,
                        "members are not ordinal-sorted at index " + i);
                }
            });

            // NEGATIVE CONTROL: the methods flag must actually change the result.
            TestHarness.Run("NEGATIVE: includeMethods changes the member set", delegate
            {
                ProbeResult without = Probe.Describe(Sample(), "Uri", false);
                ProbeResult with = Probe.Describe(Sample(), "Uri", true);
                TestHarness.Assert(with.Members.Count > without.Members.Count,
                    "includeMethods=true must add members; got " + with.Members.Count + " vs " + without.Members.Count);
                foreach (MemberEntry m in without.Members)
                    TestHarness.Assert(m.Kind != "method", "includeMethods=false leaked a method: " + m.Name);
            });

            // NEGATIVE CONTROL: empty and null inputs must fail, not pass vacuously.
            TestHarness.Run("NEGATIVE: null and empty type sets report not found", delegate
            {
                TestHarness.Assert(!Probe.Describe(null, "Uri", false).Found, "null type set must not report Found");
                TestHarness.Assert(!Probe.Describe(new Type[0], "Uri", false).Found, "empty type set must not report Found");
            });

            TestHarness.Run("null or empty type name reports not found", delegate
            {
                TestHarness.Assert(!Probe.Describe(Sample(), null, false).Found, "null name must not report Found");
                TestHarness.Assert(!Probe.Describe(Sample(), "", false).Found, "empty name must not report Found");
            });

            // Ordinal comparison must not follow Turkish casing rules for the 'I' in "Uri".
            TestHarness.RunInCulture("type lookup is culture-invariant", "tr-TR", delegate
            {
                TestHarness.Assert(Probe.Describe(Sample(), "Uri", false).Found, "Uri must resolve under tr-TR");
            });
        }
    }
}
```

`FTK2.DevKit/sandbox/TypeProbe.Tests/Program.cs`:

```csharp
using System;

namespace TypeProbe.Tests
{
    /// <summary>
    ///   dotnet run --project FTK2.DevKit/sandbox/TypeProbe.Tests -c Release
    /// Exit code 0 = every test passed.
    /// </summary>
    internal static class Program
    {
        internal static int Main(string[] args)
        {
            Console.WriteLine("TypeProbe — tests");
            Console.WriteLine("=================");

            ProbeTests.RunAll();
            ReportTests.RunAll();

            return TestHarness.Report();
        }
    }
}
```

> Note: `ReportTests` is created in Task 3. Until then Step 3 will not compile — that is expected and is resolved within this task at Step 6. If running Task 2 standalone, comment out the `ReportTests.RunAll();` line and restore it in Task 3.

- [ ] **Step 4: Run the tests to verify they fail**

```bash
dotnet run --project FTK2.DevKit/sandbox/TypeProbe.Tests -c Release
```

Expected: build FAILS with "The type or namespace name 'Probe' could not be found".

- [ ] **Step 5: Write the minimal implementation**

`FTK2.DevKit/sandbox/TypeProbe/Probe.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;

namespace TypeProbe
{
    /// <summary>One described member of a game type.</summary>
    public struct MemberEntry
    {
        public string Kind;      // "field" | "prop" | "method"
        public string TypeName;  // field/property type, or method return type
        public string Name;
    }

    /// <summary>The result of describing one type. Never null; check <see cref="Found"/>.</summary>
    public sealed class ProbeResult
    {
        public string RequestedName;
        public bool Found;
        public string FullName;
        public List<MemberEntry> Members = new List<MemberEntry>();
    }

    /// <summary>
    /// Pure member description over a set of types. No file I/O and no game dependency, so it can
    /// be driven with deliberately wrong inputs — which is what makes the negative controls
    /// meaningful. Output is ordinal-sorted so two runs on two machines produce identical field
    /// maps; a stability-only guarantee would wave through a machine-dependent enumeration order.
    /// </summary>
    public static class Probe
    {
        private const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static ProbeResult Describe(IEnumerable<Type> types, string simpleName, bool includeMethods)
        {
            ProbeResult result = new ProbeResult();
            result.RequestedName = simpleName;
            result.Found = false;

            if (types == null || string.IsNullOrEmpty(simpleName)) return result;

            Type target = null;
            foreach (Type t in types)
            {
                if (t == null) continue;
                if (string.Equals(t.Name, simpleName, StringComparison.Ordinal)) { target = t; break; }
            }
            if (target == null) return result;

            result.Found = true;
            result.FullName = target.FullName;

            foreach (FieldInfo f in target.GetFields(Flags))
                result.Members.Add(Entry("field", SafeTypeName(f.FieldType), f.Name));

            foreach (PropertyInfo p in target.GetProperties(Flags))
                result.Members.Add(Entry("prop", SafeTypeName(p.PropertyType), p.Name));

            if (includeMethods)
            {
                foreach (MethodInfo m in target.GetMethods(Flags))
                {
                    if (m.IsSpecialName) continue; // property accessors are already reported as props
                    result.Members.Add(Entry("method", SafeTypeName(m.ReturnType), m.Name));
                }
            }

            result.Members.Sort(delegate (MemberEntry a, MemberEntry b)
            {
                int byKind = string.CompareOrdinal(a.Kind, b.Kind);
                if (byKind != 0) return byKind;
                int byName = string.CompareOrdinal(a.Name, b.Name);
                if (byName != 0) return byName;
                return string.CompareOrdinal(a.TypeName, b.TypeName);
            });

            return result;
        }

        private static MemberEntry Entry(string kind, string typeName, string name)
        {
            MemberEntry e = new MemberEntry();
            e.Kind = kind;
            e.TypeName = typeName;
            e.Name = name;
            return e;
        }

        /// <summary>A member whose type failed to load must not take the whole dump down.</summary>
        private static string SafeTypeName(Type t)
        {
            try { return t == null ? "?" : t.Name; }
            catch (Exception) { return "?"; }
        }
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet run --project FTK2.DevKit/sandbox/TypeProbe.Tests -c Release
```

Expected: PASS for all 8 Probe tests, exit code 0. (`ReportTests` arrives in Task 3.)

- [ ] **Step 7: Commit**

```bash
git add FTK2.DevKit/sandbox/TypeProbe FTK2.DevKit/sandbox/TypeProbe.Tests
git commit -m "TypeProbe: pure member description with negative controls"
```

---

### Task 3: Field-map rendering, game-assembly loading, and CLI

**Files:**
- Create: `FTK2.DevKit/sandbox/TypeProbe/Report.cs`
- Create: `FTK2.DevKit/sandbox/TypeProbe/GameAssembly.cs`
- Create: `FTK2.DevKit/sandbox/TypeProbe/Program.cs`
- Create: `FTK2.DevKit/sandbox/TypeProbe/README.md`
- Create: `FTK2.DevKit/sandbox/TypeProbe.Tests/ReportTests.cs`
- Modify: `FTK2.DevKit/sandbox/TypeProbe.Tests/Program.cs` (restore the `ReportTests.RunAll();` call)

**Interfaces:**
- Consumes: `Probe.Describe`, `ProbeResult`, `MemberEntry` from Task 2.
- Produces:
  - `static string TypeProbe.Report.ToMarkdown(ProbeResult result)`
  - `static bool TypeProbe.GameAssembly.TryLoad(string managedDir, out Type[] types, out string error)`
  - CLI exit codes: `0` found · `1` at least one requested type not found · `2` game assembly not loadable

- [ ] **Step 1: Write the failing Report tests**

`FTK2.DevKit/sandbox/TypeProbe.Tests/ReportTests.cs`:

```csharp
using System;

namespace TypeProbe.Tests
{
    internal static class ReportTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("Report");

            TestHarness.Run("renders a markdown table for a found type", delegate
            {
                ProbeResult r = Probe.Describe(new Type[] { typeof(Uri) }, "Uri", false);
                string md = Report.ToMarkdown(r);
                TestHarness.Assert(md.Contains("System.Uri"), "expected the full type name in the report");
                TestHarness.Assert(md.Contains("| Host |") || md.Contains("Host"), "expected Uri.Host in the report");
                TestHarness.Assert(md.Contains("|"), "expected a markdown table");
            });

            // NEGATIVE CONTROL: a not-found result must render as an explicit failure, never as an
            // empty-but-plausible table that a reader would mistake for "this type has no members".
            TestHarness.Run("NEGATIVE: not-found renders as NOT FOUND, not an empty table", delegate
            {
                ProbeResult r = Probe.Describe(new Type[] { typeof(Uri) }, "NoSuchType", false);
                string md = Report.ToMarkdown(r);
                TestHarness.Assert(md.Contains("NOT FOUND"), "expected an explicit NOT FOUND marker, got: " + md);
            });

            TestHarness.Run("report is deterministic across repeated calls", delegate
            {
                ProbeResult r = Probe.Describe(new Type[] { typeof(Uri) }, "Uri", true);
                TestHarness.Assert(Report.ToMarkdown(r) == Report.ToMarkdown(r), "report must be deterministic");
            });

            TestHarness.Run("null result does not throw", delegate
            {
                string md = Report.ToMarkdown(null);
                TestHarness.Assert(md != null && md.Contains("NOT FOUND"), "null result must render NOT FOUND");
            });
        }
    }
}
```

- [ ] **Step 2: Restore the test entry point**

In `FTK2.DevKit/sandbox/TypeProbe.Tests/Program.cs`, ensure the line `ReportTests.RunAll();` is present and uncommented, directly after `ProbeTests.RunAll();`.

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet run --project FTK2.DevKit/sandbox/TypeProbe.Tests -c Release
```

Expected: build FAILS with "The type or namespace name 'Report' could not be found".

- [ ] **Step 4: Implement Report**

`FTK2.DevKit/sandbox/TypeProbe/Report.cs`:

```csharp
using System;
using System.Text;

namespace TypeProbe
{
    /// <summary>
    /// Renders a <see cref="ProbeResult"/> as the markdown field-map table the grounding rule
    /// (spec §2) requires be committed to docs/research before code names a game member.
    /// A not-found result renders an explicit NOT FOUND marker: an empty table would read as
    /// "this type has no members", which is exactly the silent-failure class this tool exists
    /// to eliminate.
    /// </summary>
    public static class Report
    {
        public static string ToMarkdown(ProbeResult result)
        {
            StringBuilder sb = new StringBuilder();

            if (result == null || !result.Found)
            {
                string requested = result == null ? "(null)" : result.RequestedName;
                sb.AppendLine("## " + requested + " — **NOT FOUND**");
                sb.AppendLine();
                sb.AppendLine("The type was not present in the loaded assembly. Do not write code against it.");
                return sb.ToString();
            }

            sb.AppendLine("## " + result.FullName);
            sb.AppendLine();
            sb.AppendLine("| Kind | Type | Name |");
            sb.AppendLine("|---|---|---|");
            foreach (MemberEntry m in result.Members)
                sb.AppendLine("| " + m.Kind + " | `" + m.TypeName + "` | `" + m.Name + "` |");

            return sb.ToString();
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet run --project FTK2.DevKit/sandbox/TypeProbe.Tests -c Release
```

Expected: 12 tests pass (8 Probe + 4 Report), exit code 0.

- [ ] **Step 6: Implement the game-assembly loader**

`FTK2.DevKit/sandbox/TypeProbe/GameAssembly.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace TypeProbe
{
    /// <summary>
    /// Loads the retail FTK2 assembly out-of-process. There is no compile-time game reference, so
    /// this project builds on a machine with no game installed and degrades to a clear message at
    /// runtime. Verified 2026-08-23: Assembly.LoadFrom plus an AssemblyResolve fallback over the
    /// Managed folder loads 5389 types in a plain net10 process with no Unity and no Harmony.
    /// </summary>
    public static class GameAssembly
    {
        public static bool TryLoad(string managedDir, out Type[] types, out string error)
        {
            types = new Type[0];
            error = null;

            if (string.IsNullOrEmpty(managedDir) || !Directory.Exists(managedDir))
            {
                error = "Managed directory not found: " + managedDir;
                return false;
            }

            string ftk2 = Path.Combine(managedDir, "FTK2.dll");
            if (!File.Exists(ftk2))
            {
                error = "FTK2.dll not found in: " + managedDir;
                return false;
            }

            // Sibling assemblies (UnityEngine, Photon, …) are resolved on demand from the same folder.
            ResolveEventHandler handler = delegate (object sender, ResolveEventArgs args)
            {
                string name = new AssemblyName(args.Name).Name;
                string candidate = Path.Combine(managedDir, name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += handler;

            try
            {
                Assembly asm = Assembly.LoadFrom(ftk2);
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Partial load is expected and useful: Unity types that cannot resolve outside
                    // the player still leave the game's plain data types describable.
                    List<Type> loaded = new List<Type>();
                    foreach (Type t in ex.Types) if (t != null) loaded.Add(t);
                    types = loaded.ToArray();
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Failed to load FTK2.dll: " + ex.Message;
                return false;
            }
        }
    }
}
```

- [ ] **Step 7: Implement the CLI**

`FTK2.DevKit/sandbox/TypeProbe/Program.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;

namespace TypeProbe
{
    /// <summary>
    /// Dumps verified member lists for game types, with no game running.
    ///
    ///   dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- NetworkData CombatState
    ///
    /// Exit codes: 0 = every requested type found · 1 = at least one not found · 2 = assembly not
    /// loadable (skipped, not failed).
    /// </summary>
    internal static class Program
    {
        private const string DefaultGameDir =
            @"C:\Program Files (x86)\Steam\steamapps\common\For The King II";

        internal static int Main(string[] args)
        {
            string gameDir = DefaultGameDir;
            bool includeMethods = false;
            List<string> typeNames = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--game" && i + 1 < args.Length) { gameDir = args[++i]; continue; }
                if (args[i] == "--methods") { includeMethods = true; continue; }
                typeNames.Add(args[i]);
            }

            if (typeNames.Count == 0)
            {
                Console.Error.WriteLine("usage: TypeProbe [--game <dir>] [--methods] <TypeName> [TypeName...]");
                return 2;
            }

            string managed = Path.Combine(gameDir, "For The King II_Data", "Managed");

            Type[] types;
            string error;
            if (!GameAssembly.TryLoad(managed, out types, out error))
            {
                Console.Error.WriteLine("SKIP: " + error);
                return 2;
            }

            Console.WriteLine("<!-- TypeProbe: " + types.Length + " types loaded from " + managed + " -->");
            Console.WriteLine();

            bool allFound = true;
            foreach (string name in typeNames)
            {
                ProbeResult result = Probe.Describe(types, name, includeMethods);
                if (!result.Found) allFound = false;
                Console.WriteLine(Report.ToMarkdown(result));
            }

            return allFound ? 0 : 1;
        }
    }
}
```

- [ ] **Step 8: Verify against the real assembly — the acceptance test**

```bash
dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- NetworkData
```

Expected: exit 0, and the table contains `field` `List\`1` `PlayerList` and `field` `Boolean` `IsHost`, and contains **no** `PlayerCount`. This reproduces the 2026-08-23 finding that motivated the tool.

- [ ] **Step 9: Verify the not-found and skip paths**

```bash
dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- NoSuchTypeAnywhere
echo "exit=$?"   # expect 1, output contains NOT FOUND

dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- --game "Z:\nope" NetworkData
echo "exit=$?"   # expect 2, output starts with SKIP:
```

- [ ] **Step 10: Write the README**

`FTK2.DevKit/sandbox/TypeProbe/README.md`:

```markdown
# TypeProbe

Dumps verified member lists for game types with **no game running**, so the grounding rule
(spec §2) can be enforced: no game member name enters code until it has been read out of the
retail assembly.

## One command

    dotnet run --project FTK2.DevKit/sandbox/TypeProbe -c Release -- NetworkData CombatState

Options: `--game <dir>` (defaults to the Steam install), `--methods` (include methods).

Output is a markdown table, ready to paste into a `docs/research/*.md` field map.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | every requested type was found |
| 1 | at least one type was not found |
| 2 | the game assembly could not be loaded (skipped, not failed) |

## What it cannot do

- It does **not** run the game, so it cannot tell you whether a member is *populated* at runtime,
  only that it exists. `MultiplayerDemoQuickCombat` is registered and callable and still throws
  `NotImplementedException`; only running it revealed that.
- It reports a partial type list when Unity types fail to resolve outside the player. This is
  expected and does not affect plain data types.
- It reads only. It never writes to the game directory.
```

- [ ] **Step 11: Commit**

```bash
git add FTK2.DevKit/sandbox/TypeProbe FTK2.DevKit/sandbox/TypeProbe.Tests
git commit -m "TypeProbe: field-map rendering, reflective game loading, CLI"
```

---

## Self-Review

**Spec coverage.** §2 grounding rule → Tasks 2–3 (TypeProbe is the instrument; the `NOT FOUND` marker and the `PlayerList`/`PlayerCount` acceptance test in Task 3 Step 8 enforce it). §9 TypeProbe promotion → Tasks 2–3. §11 build order step S0 → Task 1. Spec §10 testing standard (negative controls) → Task 2 Steps 3/5 and Task 3 Step 1, six negative controls total.

**Deliberately out of scope for this plan:** S6 LiveDataHarness, S1 observation, S3 verbs, S4 runner, S5 MP-safety, S2 mod-internals, S8 overnight runner. Each gets its own plan, per the writing-plans scope rule that every plan must produce working, testable software on its own. This plan's deliverable is a merged `ben` branch plus a working, tested grounding tool.

**Placeholder scan.** No `TBD`/`TODO`/"implement later"/"add error handling" strings. Every code step contains complete, runnable content. The one forward reference — `ReportTests` in Task 2's `Program.cs` — is called out explicitly at the point of use with the workaround, rather than left to surprise the implementer.

**Type consistency.** `Probe.Describe(IEnumerable<Type>, string, bool)` returns `ProbeResult` in Tasks 2 and 3 identically. `MemberEntry` fields `Kind`/`TypeName`/`Name` are consistent across `Probe.cs`, `Report.cs`, and both test files. `Report.ToMarkdown(ProbeResult)` matches its Task 3 test usage. `GameAssembly.TryLoad(string, out Type[], out string)` matches its `Program.cs` call site. Exit codes 0/1/2 are stated identically in the interfaces block, `Program.cs`, the README, and the Step 9 verification.

**Known risk.** Task 1 Step 1 may find the uncommitted `StateReader` `PlayerList` fix from 2026-08-23 in the working tree; the step instructs committing it to `feat/crucible-mcp` before merging rather than carrying it across the merge.

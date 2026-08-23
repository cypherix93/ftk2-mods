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
            bool includeSignatures = false;
            List<string> typeNames = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--game" && i + 1 < args.Length) { gameDir = args[++i]; continue; }
                if (args[i] == "--methods") { includeMethods = true; continue; }
                // --signatures implies --methods: a signature is a property of a method, and
                // asking for one without the other is always a mistake rather than a request.
                if (args[i] == "--signatures") { includeMethods = true; includeSignatures = true; continue; }
                typeNames.Add(args[i]);
            }

            if (typeNames.Count == 0)
            {
                Console.Error.WriteLine("usage: TypeProbe [--game <dir>] [--methods] [--signatures] <TypeName> [TypeName...]");
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
                ProbeResult result = Probe.Describe(types, name, includeMethods, includeSignatures);
                if (!result.Found) allFound = false;
                Console.WriteLine(Report.ToMarkdown(result));
            }

            return allFound ? 0 : 1;
        }
    }
}

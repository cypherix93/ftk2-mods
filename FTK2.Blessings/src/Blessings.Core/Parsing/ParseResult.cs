using System.Collections.Generic;
using Blessings.Core.Diagnostics;
using Blessings.Core.Model;

namespace Blessings.Core.Parsing
{
    /// <summary>Result of <see cref="BlessingsRegistryParser.Parse"/>. <see cref="Registry"/> is null
    /// iff a fatal (Error-severity, unrecoverable) problem prevented building it -- check
    /// <see cref="Success"/> before using it.</summary>
    public sealed class ParseResult
    {
        public BlessingsRegistry Registry { get; internal set; }
        public List<Finding> Findings { get; private set; } = new List<Finding>();
        public bool Success => Registry != null;

        public bool HasErrors
        {
            get
            {
                for (int i = 0; i < Findings.Count; i++)
                    if (Findings[i].Severity == FindingSeverity.Error) return true;
                return false;
            }
        }
    }
}

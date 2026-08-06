using System.Collections.Generic;

namespace Blessings.Core.Model
{
    /// <summary>The parsed <c>blessings.json</c> registry. <see cref="Blessings"/> preserves the file's
    /// AUTHORED order -- load-bearing for the §3.4 weighted-walk resolution.</summary>
    public sealed class BlessingsRegistry
    {
        public string SchemaVersion { get; private set; }

        public IReadOnlyList<BlessingEntry> Blessings { get; private set; }

        public BlessingsRegistry(string schemaVersion, IReadOnlyList<BlessingEntry> blessings)
        {
            SchemaVersion = schemaVersion;
            Blessings = blessings ?? new List<BlessingEntry>();
        }

        /// <summary>First entry whose <see cref="BlessingEntry.Id"/> matches (ordinal), or null.</summary>
        public BlessingEntry FindById(string id)
        {
            if (id == null) return null;
            for (int i = 0; i < Blessings.Count; i++)
                if (string.Equals(Blessings[i].Id, id, System.StringComparison.Ordinal))
                    return Blessings[i];
            return null;
        }
    }
}

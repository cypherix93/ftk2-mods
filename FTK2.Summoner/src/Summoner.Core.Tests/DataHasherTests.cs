using System.Collections.Generic;
using System.Text;
using Summoner.Core.Parity;

namespace Summoner.Core.Tests
{
    public static class DataHasherTests
    {
        public static void IsStableAcrossFileOrder_AndExcludesLocalization()
        {
            byte[] B(string s) => Encoding.UTF8.GetBytes(s);

            var filesOrderA = new List<(string RelPath, byte[] Bytes)>
            {
                ("pack.json", B("{\"id\":\"SMN_PACK_HASH\"}")),
                ("followers.json", B("{\"SMN_FOL_X\":{}}")),
                ("provenance.json", B("{\"SMN_FOL_X\":{\"Source\":\"eor-import\"}}"))
            };
            var filesOrderB = new List<(string RelPath, byte[] Bytes)>
            {
                ("provenance.json", B("{\"SMN_FOL_X\":{\"Source\":\"eor-import\"}}")),
                ("pack.json", B("{\"id\":\"SMN_PACK_HASH\"}")),
                ("followers.json", B("{\"SMN_FOL_X\":{}}"))
            };

            var packsA = new List<(string, IReadOnlyList<(string, byte[])>)> { ("SMN_PACK_HASH", filesOrderA) };
            var packsB = new List<(string, IReadOnlyList<(string, byte[])>)> { ("SMN_PACK_HASH", filesOrderB) };

            var hashA = DataHasher.ComputeHash(packsA);
            var hashB = DataHasher.ComputeHash(packsB);
            Check.Equal(hashA, hashB, "hash must be stable regardless of the order files are supplied in");

            // Add a localization/** file with arbitrary content -- must NOT change the hash (R1: localization is parity-exempt).
            var filesWithLoc = new List<(string RelPath, byte[] Bytes)>(filesOrderA)
            {
                ("localization/en.json", B("{\"SMN_FOL_X\":\"anything, even garbage\"}"))
            };
            var packsWithLoc = new List<(string, IReadOnlyList<(string, byte[])>)> { ("SMN_PACK_HASH", filesWithLoc) };
            var hashWithLoc = DataHasher.ComputeHash(packsWithLoc);
            Check.Equal(hashA, hashWithLoc, "adding/changing a localization/** file must not change the dataHash");

            // Sanity: changing a non-localization file's content DOES change the hash.
            var filesChanged = new List<(string RelPath, byte[] Bytes)>
            {
                ("pack.json", B("{\"id\":\"SMN_PACK_HASH\"}")),
                ("followers.json", B("{\"SMN_FOL_X\":{\"ContractRounds\":999}}")), // changed
                ("provenance.json", B("{\"SMN_FOL_X\":{\"Source\":\"eor-import\"}}"))
            };
            var packsChanged = new List<(string, IReadOnlyList<(string, byte[])>)> { ("SMN_PACK_HASH", filesChanged) };
            var hashChanged = DataHasher.ComputeHash(packsChanged);
            Check.True(hashA != hashChanged, "changing a non-localization file's bytes must change the hash");
        }
    }
}

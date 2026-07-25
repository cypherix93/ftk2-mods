using System;
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

        /// <summary>MP review B0: a bare-hex hash fails DevKit's ParityRegistration.HasWellFormedDataHash /
        /// ParityComparer well-formedness check, forcing a guaranteed mismatch even between identical peers.
        /// The Summoner hash must carry the same "sha256:" + 64-lowercase-hex shape as FTK2.DevKit's own
        /// DataHasher.HashPrefix/IsWellFormedHash.</summary>
        public static void EmitsSha256PrefixedHash_AndIsWellFormed()
        {
            byte[] B(string s) => Encoding.UTF8.GetBytes(s);

            var files = new List<(string RelPath, byte[] Bytes)>
            {
                ("pack.json", B("{\"id\":\"SMN_PACK_HASH\"}")),
            };
            var packs = new List<(string, IReadOnlyList<(string, byte[])>)> { ("SMN_PACK_HASH", files) };

            var hash = DataHasher.ComputeHash(packs);

            Check.True(hash.StartsWith("sha256:", StringComparison.Ordinal), "hash must start with the 'sha256:' prefix (matches DevKit.DataHasher.HashPrefix)");
            Check.Equal("sha256:".Length + 64, hash.Length, "hash must be 'sha256:' + exactly 64 hex chars");
            Check.True(DataHasher.IsWellFormedHash(hash), "a hash this class emits must satisfy its own IsWellFormedHash (same shape DevKit's ParityRegistration.HasWellFormedDataHash checks)");
            Check.False(DataHasher.IsWellFormedHash(hash.Substring("sha256:".Length)), "a bare-hex hash (no prefix) must NOT be considered well-formed -- this is exactly the B0 regression");
        }

        /// <summary>MP review B0/M5: only known-text extensions (.json, the only kind FollowerPacks ship)
        /// are newline-normalized before hashing; any other extension is hashed as raw, unmodified bytes,
        /// so a hypothetical binary asset never gets mangled the way ClassForge's text-decode-based hasher did.</summary>
        public static void NonTextExtension_HashedAsRawBytes_NotNewlineNormalized()
        {
            byte[] jsonCrlf = Encoding.UTF8.GetBytes("{\"id\":\"SMN_PACK_HASH\"}\r\n");
            byte[] jsonLf = Encoding.UTF8.GetBytes("{\"id\":\"SMN_PACK_HASH\"}\n");

            var packsJsonCrlf = new List<(string, IReadOnlyList<(string, byte[])>)>
            {
                ("SMN_PACK_HASH", new List<(string, byte[])> { ("pack.json", jsonCrlf) } as IReadOnlyList<(string, byte[])>)
            };
            var packsJsonLf = new List<(string, IReadOnlyList<(string, byte[])>)>
            {
                ("SMN_PACK_HASH", new List<(string, byte[])> { ("pack.json", jsonLf) } as IReadOnlyList<(string, byte[])>)
            };
            Check.Equal(DataHasher.ComputeHash(packsJsonCrlf), DataHasher.ComputeHash(packsJsonLf),
                ".json content must be newline-normalized -- CRLF and LF variants of the same text hash identically");

            // Same CRLF/LF byte difference, but under a non-text extension: must NOT normalize, so the two must differ.
            byte[] binCrlf = jsonCrlf; // arbitrary bytes; extension is what matters, not actual content
            byte[] binLf = jsonLf;
            var packsBinCrlf = new List<(string, IReadOnlyList<(string, byte[])>)>
            {
                ("SMN_PACK_HASH", new List<(string, byte[])> { ("data.bin", binCrlf) } as IReadOnlyList<(string, byte[])>)
            };
            var packsBinLf = new List<(string, IReadOnlyList<(string, byte[])>)>
            {
                ("SMN_PACK_HASH", new List<(string, byte[])> { ("data.bin", binLf) } as IReadOnlyList<(string, byte[])>)
            };
            Check.True(DataHasher.ComputeHash(packsBinCrlf) != DataHasher.ComputeHash(packsBinLf),
                "a non-text extension must be hashed as raw bytes -- CRLF/LF difference must NOT be normalized away");
        }
    }
}

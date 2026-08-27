using System;
using System.Globalization;
using System.Text;

namespace ClassForge.Core.Rng
{
    /// <summary>
    /// FNV-1a 64-bit, used everywhere ClassForge needs a hash that must be identical on every peer.
    ///
    /// <para><b>Why not <c>string.GetHashCode()</c>.</b> On .NET Core / .NET 5+ (and on Mono with hash
    /// randomization enabled) <c>string.GetHashCode()</c> is salted with a per-process random seed. It differs
    /// between two runs of the SAME binary on the SAME machine, never mind between peers. Any seed derivation
    /// or dictionary ordering that touched it would silently reintroduce exactly the desync class this
    /// subsystem exists to eliminate, and it would only show up in a live multi-player session. FNV-1a is a
    /// fixed, fully specified byte algorithm with no salt and no implementation freedom.</para>
    ///
    /// <para>Integers are absorbed as their raw little-endian bytes rather than via <c>ToString()</c>, and the
    /// string helpers force <see cref="CultureInfo.InvariantCulture"/> at every formatting site -- a peer
    /// running a locale with non-ASCII digit shapes would otherwise format <c>GroupIndex</c> differently.</para>
    /// </summary>
    public static class StableHash
    {
        /// <summary>FNV-1a 64-bit offset basis.</summary>
        public const ulong Fnv1aOffsetBasis = 14695981039346656037UL;

        /// <summary>FNV-1a 64-bit prime.</summary>
        public const ulong Fnv1aPrime = 1099511628211UL;

        /// <summary>UTF-8, no BOM, no exceptions -- fixed so the byte encoding cannot vary by host settings.</summary>
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        /// <summary>Absorbs one byte into an FNV-1a accumulator.</summary>
        public static ulong AbsorbByte(ulong hash, byte value)
        {
            unchecked { return (hash ^ value) * Fnv1aPrime; }
        }

        /// <summary>Absorbs a byte range into an FNV-1a accumulator.</summary>
        public static ulong AbsorbBytes(ulong hash, byte[] bytes)
        {
            if (bytes == null) return hash;
            unchecked
            {
                for (int i = 0; i < bytes.Length; i++)
                    hash = (hash ^ bytes[i]) * Fnv1aPrime;
                return hash;
            }
        }

        /// <summary>Absorbs a 64-bit value as 8 little-endian bytes.</summary>
        public static ulong AbsorbUInt64(ulong hash, ulong value)
        {
            unchecked
            {
                for (int i = 0; i < 8; i++)
                {
                    hash = (hash ^ (byte)(value & 0xFF)) * Fnv1aPrime;
                    value >>= 8;
                }
                return hash;
            }
        }

        /// <summary>Absorbs a 32-bit signed value via its two's-complement little-endian bytes.</summary>
        public static ulong AbsorbInt32(ulong hash, int value)
        {
            unchecked
            {
                uint u = (uint)value;
                for (int i = 0; i < 4; i++)
                {
                    hash = (hash ^ (byte)(u & 0xFF)) * Fnv1aPrime;
                    u >>= 8;
                }
                return hash;
            }
        }

        /// <summary>
        /// Absorbs a string as: a null marker byte, then its UTF-8 byte length, then its UTF-8 bytes.
        ///
        /// <para>The length prefix is not decoration. Without it, absorbing the fields <c>("AB", "C")</c> and
        /// <c>("A", "BC")</c> in sequence produces the same accumulator, so two different seed contexts would
        /// collide onto one stream. Length-prefixing makes the absorb order unambiguous and removes any need
        /// for a delimiter character that a caller-supplied <c>purpose</c> tag could smuggle in.</para>
        /// </summary>
        public static ulong AbsorbString(ulong hash, string value)
        {
            if (value == null)
                return AbsorbByte(hash, 0x00);

            hash = AbsorbByte(hash, 0x01);
            byte[] bytes = Utf8.GetBytes(value);
            hash = AbsorbInt32(hash, bytes.Length);
            return AbsorbBytes(hash, bytes);
        }

        /// <summary>Formats an <see cref="int"/> with invariant culture. Use this at every peer-visible
        /// formatting site instead of <c>value.ToString()</c>.</summary>
        public static string Format(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClassForge.Recipes.Json;

namespace ClassForge.Recipes.Loot
{
    /// <summary><c>Mode</c> — verb spec §3.1/§2.</summary>
    public enum LootGrantMode
    {
        /// <summary>v1, ships. Every peer computes and applies the identical delta; the payload is the
        /// authoritative audit digest.</summary>
        MIRROR,
        /// <summary>Reserved for M-LG4. Decodes successfully but is never applied-from-wire in v1 — see
        /// <see cref="LootGrantCodec.IsApplyModeSupported"/>.</summary>
        HOST_PUSH
    }

    /// <summary>The decoded/to-be-encoded <c>CF_SYNC_LOOT_GRANT_V1</c> payload — verb spec §3.1.</summary>
    public sealed class LootGrantPayload
    {
        public int SchemaVersion = LootGrantCodec.CurrentSchemaVersion;
        public LootGrantMode Mode = LootGrantMode.MIRROR;
        public string GrantKey = string.Empty;
        public int CombatSeed;
        public string ListDigest = string.Empty;

        /// <summary>Null means the digest-only degraded form (§3.1 over-cap path): <c>GrantKey</c>/
        /// <c>OpsHash</c> are kept, <c>Ops</c> omitted.</summary>
        public List<LootOp> Ops;

        public string OpsHash = string.Empty;

        public bool IsDigestOnly { get { return Ops == null; } }
    }

    /// <summary>
    /// Wire codec for <c>CF_SYNC_LOOT_GRANT_V1</c> — verb spec §3.1. JSON with a leading <c>"Action"</c>
    /// key, riding the same DevKit codec convention <c>FTK2MODS_PARITY_V1</c> uses, but with zero
    /// project reference to DevKit (this assembly stays reference-free by design).
    /// <para><c>OpsHash</c> follows the same shape rules as <c>ParityService.ComputeDataHash</c>
    /// (FTK2.DevKit/src/DevKit.Core/DataHasher.cs): SHA-256, UTF-8 bytes, invariant culture,
    /// lowercase hex, <c>"sha256:"</c> prefix — over authored-order, <c>"\n"</c>-joined
    /// <c>"Op|field=value|..."</c> rows (<see cref="LootOp.ToCanonicalRow"/>).</para>
    /// </summary>
    public static class LootGrantCodec
    {
        public const string ActionKey = "CF_SYNC_LOOT_GRANT_V1";
        public const int CurrentSchemaVersion = 1;

        /// <summary>Mirrors DevKit's <c>MaxParityPayloadBytes</c> cap (FTK2.DevKit SPEC §5); duplicated
        /// here rather than referenced, to keep this assembly zero-cross-project-reference.</summary>
        public const int DefaultMaxPayloadBytes = 8192;

        /// <summary>Only <see cref="LootGrantMode.MIRROR"/> is ever applied-from-wire in v1 — Mode M
        /// never applies from wire either (every peer computes its own delta); Mode H is reserved for
        /// M-LG4 and this always returns false for it until then.</summary>
        public static bool IsApplyModeSupported(LootGrantMode mode)
        {
            return mode == LootGrantMode.MIRROR;
        }

        /// <summary>
        /// <c>OpsHash = "sha256:" + 64 hex</c> over <paramref name="ops"/>'s canonical serialization:
        /// authored order, invariant culture, <c>"\n"</c>-joined rows. A null/empty list hashes the
        /// empty string (still a well-formed, comparable digest).
        /// </summary>
        public static string ComputeOpsHash(IReadOnlyList<LootOp> ops)
        {
            StringBuilder sb = new StringBuilder();
            if (ops != null)
                for (int i = 0; i < ops.Count; i++)
                {
                    if (i > 0) sb.Append('\n');
                    sb.Append(ops[i] != null ? ops[i].ToCanonicalRow() : string.Empty);
                }
            return LootHash.Sha256HexPrefixed(sb.ToString());
        }

        public static string Encode(LootGrantPayload payload)
        {
            if (payload == null) payload = new LootGrantPayload();
            JsonValue root = JsonValue.NewObject();
            root.Set("Action", JsonValue.NewString(ActionKey));
            root.Set("SchemaVersion", JsonValue.NewNumber(payload.SchemaVersion));
            root.Set("Mode", JsonValue.NewString(payload.Mode.ToString()));
            root.Set("GrantKey", JsonValue.NewString(payload.GrantKey ?? string.Empty));
            root.Set("CombatSeed", JsonValue.NewNumber(payload.CombatSeed));
            root.Set("ListDigest", JsonValue.NewString(payload.ListDigest ?? string.Empty));
            if (payload.Ops != null)
            {
                JsonValue arr = JsonValue.NewArray();
                for (int i = 0; i < payload.Ops.Count; i++) arr.Add(EncodeOp(payload.Ops[i]));
                root.Set("Ops", arr);
            }
            root.Set("OpsHash", JsonValue.NewString(payload.OpsHash ?? string.Empty));
            return root.ToString();
        }

        /// <summary>
        /// Encodes at the given cap, degrading to the digest-only form (<c>Ops</c> omitted,
        /// <c>GrantKey</c>/<c>OpsHash</c> kept) when the full encoding exceeds it — verb spec §3.1
        /// "Size", mirroring the DevKit truncated-snapshot precedent. Pass 0 or less for
        /// <see cref="DefaultMaxPayloadBytes"/>.
        /// </summary>
        public static string EncodeCapped(LootGrantPayload payload, int maxBytes)
        {
            if (maxBytes <= 0) maxBytes = DefaultMaxPayloadBytes;
            string full = Encode(payload);
            if (full.Length <= maxBytes) return full;

            LootGrantPayload digestOnly = new LootGrantPayload
            {
                SchemaVersion = payload.SchemaVersion,
                Mode = payload.Mode,
                GrantKey = payload.GrantKey,
                CombatSeed = payload.CombatSeed,
                ListDigest = payload.ListDigest,
                Ops = null,
                OpsHash = payload.OpsHash
            };
            return Encode(digestOnly);
        }

        private static JsonValue EncodeOp(LootOp op)
        {
            JsonValue o = JsonValue.NewObject();
            if (op == null) { o.Set("Op", JsonValue.NewString("")); return o; }
            o.Set("Op", JsonValue.NewString(op.Kind.ToString()));
            switch (op.Kind)
            {
                case LootOpKind.ADD_GOLD:
                    o.Set("Amount", JsonValue.NewNumber(op.Amount));
                    break;
                case LootOpKind.ADD_ITEM:
                    o.Set("ConfigName", JsonValue.NewString(op.ConfigName ?? ""));
                    o.Set("Stack", JsonValue.NewNumber(op.Stack));
                    o.Set("ThingId", JsonValue.NewString(op.ThingId ?? ""));
                    break;
                case LootOpKind.SCALE_STACK:
                    o.Set("ConfigName", JsonValue.NewString(op.ConfigName ?? ""));
                    o.Set("Percent", JsonValue.NewNumber(op.Percent));
                    break;
                case LootOpKind.REPLACE_ITEM:
                    o.Set("ThingId", JsonValue.NewString(op.ThingId ?? ""));
                    o.Set("NewConfigName", JsonValue.NewString(op.NewConfigName ?? ""));
                    break;
            }
            o.Set("Source", JsonValue.NewString(op.Source ?? ""));
            return o;
        }

        /// <summary>
        /// Decodes a payload. Never throws. Fails (returns false + a human-readable
        /// <paramref name="error"/>) on: malformed JSON, wrong shapes, an unrecognized <c>Action</c>,
        /// an unsupported <c>SchemaVersion</c>, an unknown <c>Mode</c>, or a missing <c>GrantKey</c> —
        /// the verb spec §7 failure matrix's "malformed / unknown SchemaVersion" row.
        /// </summary>
        public static bool TryDecode(string json, out LootGrantPayload payload, out string error)
        {
            payload = null;
            error = null;

            JsonValue root;
            string parseError;
            if (!JsonParser.TryParse(json, out root, out parseError))
            {
                error = "not valid JSON: " + parseError;
                return false;
            }
            if (root.Kind != JsonKind.Object)
            {
                error = "top-level value must be an object";
                return false;
            }

            string action = StrField(root, "Action");
            if (!string.IsNullOrEmpty(action) && !string.Equals(action, ActionKey, StringComparison.Ordinal))
            {
                error = "unexpected Action '" + action + "'";
                return false;
            }

            JsonValue svNode = root.Get("SchemaVersion");
            int schemaVersion = (svNode != null && svNode.Kind == JsonKind.Number) ? (int)svNode.NumberValue : -1;
            if (schemaVersion != CurrentSchemaVersion)
            {
                error = "unsupported SchemaVersion " + schemaVersion.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            string modeTok = StrField(root, "Mode");
            LootGrantMode mode = LootGrantMode.MIRROR;
            if (!string.IsNullOrEmpty(modeTok) && !TryParseMode(modeTok, out mode))
            {
                error = "unknown Mode '" + modeTok + "'";
                return false;
            }

            LootGrantPayload p = new LootGrantPayload();
            p.SchemaVersion = schemaVersion;
            p.Mode = mode;
            p.GrantKey = StrField(root, "GrantKey") ?? string.Empty;
            JsonValue csNode = root.Get("CombatSeed");
            p.CombatSeed = (csNode != null && csNode.Kind == JsonKind.Number) ? (int)csNode.NumberValue : 0;
            p.ListDigest = StrField(root, "ListDigest") ?? string.Empty;
            p.OpsHash = StrField(root, "OpsHash") ?? string.Empty;

            JsonValue opsNode = root.Get("Ops");
            if (opsNode != null)
            {
                if (opsNode.Kind != JsonKind.Array)
                {
                    error = "Ops must be an array";
                    return false;
                }
                List<LootOp> list = new List<LootOp>();
                for (int i = 0; i < opsNode.Items.Count; i++)
                {
                    LootOp op;
                    string opError;
                    if (!TryDecodeOp(opsNode.Items[i], out op, out opError))
                    {
                        error = "Ops[" + i.ToString(CultureInfo.InvariantCulture) + "]: " + opError;
                        return false;
                    }
                    list.Add(op);
                }
                p.Ops = list;
            }

            if (string.IsNullOrEmpty(p.GrantKey))
            {
                error = "GrantKey is required";
                return false;
            }

            payload = p;
            return true;
        }

        private static bool TryDecodeOp(JsonValue node, out LootOp op, out string error)
        {
            op = null;
            error = null;
            if (node == null || node.Kind != JsonKind.Object) { error = "op must be an object"; return false; }

            string kindTok = StrField(node, "Op");
            LootOpKind kind;
            if (string.IsNullOrEmpty(kindTok) || !TryParseOpKind(kindTok, out kind))
            {
                error = "unknown Op '" + kindTok + "'";
                return false;
            }

            LootOp o = new LootOp { Kind = kind };
            o.Source = StrField(node, "Source") ?? string.Empty;
            switch (kind)
            {
                case LootOpKind.ADD_GOLD:
                    o.Amount = IntField(node, "Amount");
                    break;
                case LootOpKind.ADD_ITEM:
                    o.ConfigName = StrField(node, "ConfigName") ?? string.Empty;
                    o.Stack = IntFieldOr(node, "Stack", 1);
                    o.ThingId = StrField(node, "ThingId") ?? string.Empty;
                    break;
                case LootOpKind.SCALE_STACK:
                    o.ConfigName = StrField(node, "ConfigName") ?? string.Empty;
                    o.Percent = IntField(node, "Percent");
                    break;
                case LootOpKind.REPLACE_ITEM:
                    o.ThingId = StrField(node, "ThingId") ?? string.Empty;
                    o.NewConfigName = StrField(node, "NewConfigName") ?? string.Empty;
                    break;
            }
            op = o;
            return true;
        }

        private static bool TryParseMode(string token, out LootGrantMode mode)
        {
            return TryParseEnum(token, out mode);
        }

        private static bool TryParseOpKind(string token, out LootOpKind kind)
        {
            return TryParseEnum(token, out kind);
        }

        private static bool TryParseEnum<T>(string token, out T value) where T : struct
        {
            value = default(T);
            if (string.IsNullOrEmpty(token)) return false;
            foreach (string name in Enum.GetNames(typeof(T)))
            {
                if (string.Equals(name, token, StringComparison.Ordinal))
                {
                    value = (T)Enum.Parse(typeof(T), name);
                    return true;
                }
            }
            return false;
        }

        private static string StrField(JsonValue node, string key)
        {
            JsonValue v = node.Get(key);
            return (v != null && v.Kind == JsonKind.String) ? v.StringValue : null;
        }

        private static int IntField(JsonValue node, string key)
        {
            JsonValue v = node.Get(key);
            return (v != null && v.Kind == JsonKind.Number) ? (int)v.NumberValue : 0;
        }

        private static int IntFieldOr(JsonValue node, string key, int fallback)
        {
            JsonValue v = node.Get(key);
            return (v != null && v.Kind == JsonKind.Number) ? (int)v.NumberValue : fallback;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Collects reflection failures for the snapshot's <c>warnings</c> array.
    ///
    /// This exists because <c>StateReader</c> read <c>NetworkData.PlayerCount</c> — a plausible,
    /// wrong name — and returned null while the log filled with resolution failures, for months,
    /// undetected (SPEC §2). Warnings ride inside the snapshot so a member that vanishes in a game
    /// update surfaces in the next assertion, not in a log nobody reads.
    ///
    /// Deduplicating: a snapshot walks every combatant, so one renamed field on
    /// <c>CharacterComponent</c> would otherwise emit one identical line per entity.
    /// </summary>
    public sealed class WarningSink
    {
        private readonly List<string> _items = new List<string>();
        private readonly object _lock = new object();

        public int Count
        {
            get { lock (_lock) { return _items.Count; } }
        }

        public void Add(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            lock (_lock)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (string.Equals(_items[i], message, StringComparison.Ordinal)) return;
                }
                _items.Add(message);
            }
        }

        public void MemberMissing(string typeName, string memberName)
        {
            Add("member_missing: " + typeName + "." + memberName);
        }

        public void MemberThrew(string typeName, string memberName, string detail)
        {
            Add("member_threw: " + typeName + "." + memberName + ": " + detail);
        }

        public void Ambiguous(string typeName, string memberName, int candidates, string argTypeName)
        {
            Add("member_ambiguous: " + typeName + "." + memberName + " has "
                + candidates.ToString(CultureInfo.InvariantCulture)
                + " unary overloads accepting " + argTypeName);
        }

        public void TypeMissing(string typeName)
        {
            Add("type_missing: " + typeName);
        }

        public void Note(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            Add("note: " + message);
        }

        public string[] ToArray()
        {
            lock (_lock) { return _items.ToArray(); }
        }

        /// <summary>Snapshot-ready form: <c>MiniJson</c> serializes <c>List&lt;object&gt;</c> only.</summary>
        public List<object> ToJsonList()
        {
            List<object> result = new List<object>();
            lock (_lock)
            {
                for (int i = 0; i < _items.Count; i++) result.Add(_items[i]);
            }
            return result;
        }
    }
}

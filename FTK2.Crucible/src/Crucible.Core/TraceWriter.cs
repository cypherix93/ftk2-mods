using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Append-only JSONL session trace plus an in-memory ring buffer for cheap tailing over RPC.
    ///
    /// Every entry carries a caller-supplied correlation id, so an action taken by an agent can be
    /// matched to what the game actually did — that pairing is the whole point of the trace, and it
    /// is what makes "give it a bug and let it drive" debuggable after the fact rather than a
    /// guessing game.
    /// </summary>
    public sealed class TraceWriter
    {
        private readonly string _filePath;
        private readonly int _maxEntries;
        private readonly Queue<string> _buffer = new Queue<string>();
        private readonly object _lock = new object();
        private readonly StringBuilder _pending = new StringBuilder();

        /// <summary>Injected so tests can pin timestamps; defaults to UTC now in ISO-8601.</summary>
        public Func<string> TimestampProvider;

        public TraceWriter(string folder, string sessionId, int maxEntries)
        {
            _maxEntries = maxEntries < 1 ? 1 : maxEntries;
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "crucible-" + sessionId + ".jsonl");
            TimestampProvider = delegate { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); };
        }

        public string FilePath { get { return _filePath; } }

        public void Write(string kind, string correlationId, Dictionary<string, object> fields)
        {
            Dictionary<string, object> entry = new Dictionary<string, object>();
            if (fields != null)
            {
                foreach (KeyValuePair<string, object> kv in fields) entry[kv.Key] = kv.Value;
            }

            // Reserved keys are written last so a caller-supplied field can never forge them.
            entry["ts"] = TimestampProvider();
            entry["kind"] = kind;
            entry["correlationId"] = correlationId;

            string line = MiniJson.Write(entry);
            lock (_lock)
            {
                _buffer.Enqueue(line);
                while (_buffer.Count > _maxEntries) _buffer.Dequeue();
                _pending.Append(line).Append('\n');
                if (_pending.Length > 8192) FlushLocked();
            }
        }

        /// <summary>The most recent <paramref name="count"/> buffered entries, oldest first.</summary>
        public string[] Tail(int count)
        {
            lock (_lock)
            {
                string[] all = _buffer.ToArray();
                if (count >= all.Length) return all;
                string[] result = new string[count];
                Array.Copy(all, all.Length - count, result, 0, count);
                return result;
            }
        }

        public void Flush()
        {
            lock (_lock) { FlushLocked(); }
        }

        private void FlushLocked()
        {
            if (_pending.Length == 0) return;
            try
            {
                File.AppendAllText(_filePath, _pending.ToString(), Encoding.UTF8);
            }
            catch (IOException)
            {
                // Losing trace lines must never take down the game or an RPC call.
            }
            _pending.Length = 0;
        }
    }
}

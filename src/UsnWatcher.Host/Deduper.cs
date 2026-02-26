using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UsnWatcher.Core;

namespace UsnWatcher.Host
{
    /// <summary>
    /// Debounces/merges rapid events for the same FileReferenceNumber.
    /// Collects events into a combined UsnRecord after a short quiet window.
    /// </summary>
    public sealed class Deduper : IDisposable
    {
        private readonly ConcurrentDictionary<ulong, Pending> _pending = new();
        private readonly int _debounceMs;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        private sealed class Pending
        {
            public object Lock = new();
            public long Usn;
            public DateTime Timestamp;
            public ulong Parent;
            public string FileName = string.Empty;
            public string? FullPath;
            public uint ReasonRaw;
            public HashSet<string> Reasons = new();
            public bool IsDirectory;
            public uint Attributes;
            public string? OldPath;
            public string? NewPath;
            public DateTime LastSeen;
        }

        public Deduper(int debounceMs = 50)
        {
            _debounceMs = Math.Max(10, debounceMs);
        }

        public void Add(UsnRecord r)
        {
            var p = _pending.GetOrAdd(r.FileReferenceNumber, _ => new Pending());
            lock (p.Lock)
            {
                // Merge semantics: union of reasons, OR of raw mask, latest values for name/path/usn
                p.Usn = Math.Max(p.Usn, r.Usn);
                if (r.Timestamp > p.Timestamp) p.Timestamp = r.Timestamp;
                p.Parent = r.ParentFileReferenceNumber;
                if (!string.IsNullOrEmpty(r.FileName)) p.FileName = r.FileName;
                if (!string.IsNullOrEmpty(r.FullPath)) p.FullPath = r.FullPath;
                p.ReasonRaw |= r.ReasonRaw;
                if (r.Reasons != null)
                {
                    foreach (var s in r.Reasons) p.Reasons.Add(s);
                }
                p.IsDirectory |= r.IsDirectory;
                p.Attributes |= r.FileAttributes;
                if (!string.IsNullOrEmpty(r.OldPath) && string.IsNullOrEmpty(p.OldPath)) p.OldPath = r.OldPath;
                if (!string.IsNullOrEmpty(r.NewPath)) p.NewPath = r.NewPath;
                p.LastSeen = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Start the background flusher. The provided callback will be invoked for each
        /// synthesized record that has been quiet for the debounce window.
        /// </summary>
        public Task StartAsync(Action<UsnRecord> onEmit, CancellationToken externalToken)
        {
            if (onEmit == null) throw new ArgumentNullException(nameof(onEmit));
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            var ct = _cts.Token;

            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var now = DateTime.UtcNow;
                        var toFlush = new List<KeyValuePair<ulong, Pending>>();
                        foreach (var kv in _pending)
                        {
                            var p = kv.Value;
                            lock (p.Lock)
                            {
                                if ((now - p.LastSeen).TotalMilliseconds >= _debounceMs)
                                {
                                    toFlush.Add(kv);
                                }
                            }
                        }

                        foreach (var kv in toFlush)
                        {
                            if (_pending.TryRemove(kv.Key, out var p))
                            {
                                // Build merged UsnRecord
                                UsnRecord merged;
                                lock (p.Lock)
                                {
                                    // Use the flush time as the consolidated timestamp so consumers
                                    // can observe the coalesced event time (debounce end), not the
                                    // time of the first event.
                                    merged = new UsnRecord
                                    {
                                        Usn = p.Usn,
                                        Timestamp = DateTime.UtcNow,
                                        FileReferenceNumber = kv.Key,
                                        ParentFileReferenceNumber = p.Parent,
                                        FileName = p.FileName,
                                        FullPath = p.FullPath,
                                        Reasons = p.Reasons.ToArray(),
                                        ReasonRaw = p.ReasonRaw,
                                        IsDirectory = p.IsDirectory,
                                        FileAttributes = p.Attributes,
                                        OldPath = p.OldPath,
                                        NewPath = p.NewPath
                                    };
                                }

                                try
                                {
                                    onEmit(merged);
                                }
                                catch
                                {
                                    // Swallow emission exceptions — host will log where needed
                                }
                            }
                        }

                        await Task.Delay(_debounceMs, ct).ContinueWith(_ => { });
                    }
                }
                catch (OperationCanceledException) { }
            }, ct);

            return _loop;
        }

        public void FlushAll(Action<UsnRecord> onEmit)
        {
            var keys = _pending.Keys.ToArray();
            foreach (var k in keys)
            {
                if (_pending.TryRemove(k, out var p))
                {
                    UsnRecord merged;
                    lock (p.Lock)
                    {
                        merged = new UsnRecord
                        {
                            Usn = p.Usn,
                            Timestamp = DateTime.UtcNow,
                            FileReferenceNumber = k,
                            ParentFileReferenceNumber = p.Parent,
                            FileName = p.FileName,
                            FullPath = p.FullPath,
                            Reasons = p.Reasons.ToArray(),
                            ReasonRaw = p.ReasonRaw,
                            IsDirectory = p.IsDirectory,
                            FileAttributes = p.Attributes,
                            OldPath = p.OldPath,
                            NewPath = p.NewPath
                        };
                    }
                    try { onEmit(merged); } catch { }
                }
            }
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _loop?.Wait(500); } catch { }
        }
    }
}

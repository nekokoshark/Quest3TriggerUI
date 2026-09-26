using System;
using System.Collections.Generic;
using System.Threading;

namespace Quest3TriggerUI
{
    // No Unity APIs and no main-thread disk reads/writes. Coalesce queued
    // observations per content identity rather than enqueueing every callback.
    internal sealed class ResourceHistoryWorker
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, ResourceHistoryStore.Record> _pending = new Dictionary<string, ResourceHistoryStore.Record>(StringComparer.OrdinalIgnoreCase);
        private readonly string _directory;
        private readonly Thread _thread;
        private bool _stop;
        internal volatile int Count, Errors, Deleted, Hits, Invalidated;
        internal volatile bool Ready;
        internal volatile string LastError = "";
        internal ResourceHistoryWorker(string directory)
        {
            _directory = directory; _thread = new Thread(Run); _thread.IsBackground = true;
            _thread.Name = "Q3 resource history metadata"; _thread.Start();
        }
        internal void Queue(ResourceHistoryStore.Record record)
        { lock (_sync) { if (_stop) return; _pending[ResourceHistoryStore.Key(record.Kind, record.Source)] = record; Monitor.Pulse(_sync); } }
        internal void Stop() { lock (_sync) { _stop = true; Monitor.Pulse(_sync); } }
        private void Run()
        {
            // Hot-reload generations serialize persistence without blocking Unity.
            using (var mutex = new Mutex(false, "Local\\Q3ResourceHistory-" + System.Diagnostics.Process.GetCurrentProcess().Id))
            {
                bool held = false;
                try
                {
                    try { mutex.WaitOne(); held = true; } catch (AbandonedMutexException) { held = true; }
                    var store = new ResourceHistoryStore(_directory); store.Load(); Ready = true;
                    DateTime audit = DateTime.MinValue;
                    while (true)
                    {
                        List<ResourceHistoryStore.Record> work;
                        lock (_sync)
                        {
                            if (!_stop && _pending.Count == 0) Monitor.Wait(_sync, 2000);
                            if (_stop && _pending.Count == 0) break;
                            work = new List<ResourceHistoryStore.Record>(_pending.Values); _pending.Clear();
                        }
                        foreach (var record in work)
                        {
                            try { store.Observe(record); }
                            catch (Exception e) { Errors++; LastError = e.GetType().Name + ":" + e.Message; } // Keep the previous atomically saved manifest.
                        }
                        if (DateTime.UtcNow >= audit)
                        {
                            store.Audit(16, DateTime.UtcNow); audit = DateTime.UtcNow.AddSeconds(2);
                        }
                        Count = store.Records.Count; Deleted = store.Deleted; Hits = store.Hits; Invalidated = store.Invalidated;
                        if (store.Errors > 0) { Errors += store.Errors; store.Errors = 0; }
                    }
                }
                catch (Exception e) { Errors++; LastError = e.GetType().Name + ":" + e.Message; }
                finally { Ready = false; if (held) mutex.ReleaseMutex(); }
            }
        }
    }
}

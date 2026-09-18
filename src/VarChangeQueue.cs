using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Quest3TriggerUI
{
    // Background work is limited to filesystem discovery; VaM APIs stay on the Unity thread.
    internal sealed class VarChangeQueue : IDisposable
    {
        private readonly object _gate = new object();
        private readonly HashSet<string> _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly HashSet<string> _directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _watched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private volatile bool _disposed;
        private bool _ready;
        private string _repair;
        internal bool Ready { get { lock (_gate) return _ready; } }
        internal string RepairReason { get { lock (_gate) return _repair; } }
        internal VarChangeQueue(string root, IList<string> registered)
        {
            foreach (string path in registered) _known[Path.GetFullPath(path)] = null;
            try { Watch(Path.GetFullPath(root)); }
            catch (Exception e) { Repair("文件监听启动失败：" + e.Message); }
            ThreadPool.QueueUserWorkItem(delegate {
                try
                {
                    foreach (string path in registered)
                    {
                        if (_disposed) return;
                        string full = Path.GetFullPath(path);
                        string stamp = Stamp(full);
                        if (stamp == null) Repair("已注册VAR在磁盘上缺失");
                        lock (_gate) _known[full] = stamp;
                    }
                    Scan(Path.GetFullPath(root));
                }
                catch (Exception e) { Repair("初始文件快照失败：" + e.Message); }
                finally { lock (_gate) _ready = true; }
            });
        }
        private void Watch(string root)
        {
            lock (_gate) if (_disposed || !_watched.Add(Path.GetFullPath(root))) return;
            FileSystemWatcher watcher = new FileSystemWatcher(root);
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
            watcher.Created += Changed; watcher.Changed += Changed; watcher.Deleted += Changed;
            watcher.Renamed += delegate(object sender, RenamedEventArgs e) {
                Enqueue(e.OldFullPath);
                lock (_gate) if (_directories.Contains(e.OldFullPath)) Repair("VAR目录已移动");
                Changed(sender, e);
            };
            watcher.Error += delegate(object sender, ErrorEventArgs e) { Repair("文件监听漏报，请完整扫描：" + e.GetException().Message); };
            lock (_gate)
            {
                if (_disposed) { watcher.Dispose(); return; }
                _watchers.Add(watcher);
                watcher.EnableRaisingEvents = true;
            }
        }
        private void Changed(object sender, FileSystemEventArgs e)
        {
            if (_disposed) return;
            if (e.FullPath.EndsWith(".var", StringComparison.OrdinalIgnoreCase)) Enqueue(e.FullPath);
            else if (e.ChangeType == WatcherChangeTypes.Created || e.ChangeType == WatcherChangeTypes.Renamed)
                ThreadPool.QueueUserWorkItem(delegate {
                    try { if (Directory.Exists(e.FullPath)) Scan(e.FullPath); }
                    catch (Exception ex) { Repair("目录变化扫描失败：" + ex.Message); }
                });
            else if (e.ChangeType == WatcherChangeTypes.Deleted)
            { lock (_gate) if (_directories.Contains(e.FullPath)) Repair("目录被删除，需要完整扫描"); }
        }
        private void Scan(string root)
        {
            if (_disposed) return;
            lock (_gate) _directories.Add(Path.GetFullPath(root));
            foreach (string file in Directory.GetFiles(root, "*.var"))
            {
                lock (_gate) if (!_known.ContainsKey(Path.GetFullPath(file))) _pending.Add(Path.GetFullPath(file));
            }
            foreach (string dir in Directory.GetDirectories(root))
            {
                if (_disposed) return;
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                {
                    // VaM libraries often use junctions: watch their root explicitly.
                    Watch(dir);
                    // Junction trees may hold the whole library; skip files the
                    // snapshot already registered instead of queueing them all.
                    foreach (string file in Directory.GetFiles(dir, "*.var", SearchOption.AllDirectories))
                    {
                        lock (_gate) if (!_known.ContainsKey(Path.GetFullPath(file))) _pending.Add(Path.GetFullPath(file));
                    }
                }
                else Scan(dir);
            }
        }
        internal void Repair(string reason) { lock (_gate) if (!_disposed) _repair = reason; }
        internal void Enqueue(string path)
        {
            if (!path.EndsWith(".var", StringComparison.OrdinalIgnoreCase)) return;
            lock (_gate) if (!_disposed) _pending.Add(Path.GetFullPath(path));
        }
        internal string[] Drain()
        {
            lock (_gate)
            {
                string[] paths = new string[_pending.Count]; _pending.CopyTo(paths); _pending.Clear(); return paths;
            }
        }
        internal bool Known(string path) { lock (_gate) return _known.ContainsKey(Path.GetFullPath(path)); }
        internal bool Unchanged(string path, string stamp)
        {
            lock (_gate) { string previous; return _known.TryGetValue(Path.GetFullPath(path), out previous) && previous == stamp; }
        }
        internal void Accept(string path, string stamp) { lock (_gate) _known[Path.GetFullPath(path)] = stamp; }
        internal static string Stamp(string path)
        {
            FileInfo info = new FileInfo(path);
            return info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : null;
        }
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                foreach (FileSystemWatcher watcher in _watchers) watcher.Dispose();
                _watchers.Clear(); _pending.Clear();
            }
        }
    }
}

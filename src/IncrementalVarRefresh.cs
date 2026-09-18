using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using MVR.FileManagement;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal sealed class IncrementalVarRefresh : IDisposable
    {
        private readonly MonoBehaviour _host;
        private readonly Action _notify;
        private VarChangeQueue _queue;
        private readonly BrowserAssistIncrementalBridge _browserAssist = new BrowserAssistIncrementalBridge();
        private bool _busy, _disposed;
        internal IncrementalVarRefresh(MonoBehaviour host, Action notify)
        {
            _host = host; _notify = notify; ResetQueue();
        }
        private void ResetQueue()
        {
            if (_queue != null) _queue.Dispose();
            List<string> paths = new List<string>();
            foreach (VarPackage package in FileManager.GetPackages())
                if (package != null && !VarRegistration.Incomplete(package) && !string.IsNullOrEmpty(package.FullPath)) paths.Add(package.FullPath);
            _queue = new VarChangeQueue(FileManager.PackageFolder, paths);
        }
        internal void Begin(bool full, Action<string> completed)
        {
            if (_busy) { Report(completed, "VAR刷新正在进行，请稍候。"); return; }
            _busy = true;
            _host.StartCoroutine(Run(full, completed));
        }
        private IEnumerator Run(bool full, Action<string> completed)
        {
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                if (full)
                {
                    Report(completed, "正在完整扫描VAR，此操作可能较慢。");
                    yield return null;
                    string error = null;
                    try
                    {
                        // Bypass the local snapshot shortcut, including same-name overwrites.
                        foreach (VarPackage package in FileManager.GetPackages()) package.forceRefresh = true;
                        FileManager.Refresh();
                        ResetQueue();
                    }
                    catch (Exception e) { error = e.Message; }
                    Report(completed, error == null ? "VAR完整扫描返回（" + timer.ElapsedMilliseconds + " ms）。" : "完整扫描失败：" + error);
                    yield break;
                }
                if (!_queue.Ready)
                {
                    Report(completed, "VAR初始快照仍在后台建立；完成后再次点按增量刷新。");
                    yield break;
                }
                string[] batch = _queue.Drain();
                if (batch.Length == 0)
                {
                    Report(completed, "VAR增量刷新：没有新增文件（" + timer.ElapsedMilliseconds + " ms）。" + RepairHint() + _browserAssist.Sync(new VarPackage[0]));
                    yield break;
                }
                Dictionary<string, string> stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in batch)
                {
                    try { stamps[path] = VarChangeQueue.Stamp(path); }
                    catch (IOException) { stamps[path] = null; }
                    catch (UnauthorizedAccessException) { stamps[path] = null; }
                }
                // Unscaled, so standby/timeScale=0 cannot leave this coroutine stuck.
                float until = Time.realtimeSinceStartup + 0.75f;
                while (Time.realtimeSinceStartup < until) yield return null;
                List<VarPackage> added = new List<VarPackage>();
                int deferred = 0, failed = 0, duplicates = 0;
                try
                {
                    foreach (string path in batch)
                    {
                        try
                        {
                            string stamp = VarChangeQueue.Stamp(path);
                            if (_queue.Known(path))
                            {
                                if (!_queue.Unchanged(path, stamp)) _queue.Repair("已注册VAR被覆盖或删除");
                                continue;
                            }
                            if (stamp == null && !File.Exists(path)) continue;
                            if (stamp == null || stamp != stamps[path] || new FileInfo(path).Length == 0)
                            { _queue.Enqueue(path); deferred++; continue; }
                            // Allow existing archive readers; still reject a copier holding write access.
                            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                            bool changed;
                            VarPackage package = VarRegistration.RegisterFile(path, out changed);
                            if (changed) added.Add(package);
                            else if (!string.Equals(Path.GetFullPath(package.FullPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) duplicates++;
                            _queue.Accept(path, stamp);
                        }
                        catch (IOException) { _queue.Enqueue(path); deferred++; }
                        catch (Exception e)
                        {
                            failed++; _queue.Enqueue(path); _queue.Repair("包注册失败，请检查文件或完整扫描");
                            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogError("[VAR incremental] " + path + ": " + e);
                        }
                        yield return null;
                    }
                }
                finally
                {
                    // Even if a later entry fails, publish the successfully registered entries once.
                    if (added.Count > 0)
                    {
                        try { RefreshMetadata(added); }
                        catch (Exception e) { _queue.Repair("元数据更新失败：" + e.Message); UnityEngine.Debug.LogError(e); }
                        _notify();
                    }
                }
                Report(completed, "VAR增量刷新完成（" + timer.ElapsedMilliseconds + " ms）：新增 " + added.Count +
                    "，忽略已存在的同ID副本 " + duplicates + "，等待复制完成 " + deferred + "，失败 " + failed + "。" + RepairHint() + _browserAssist.Sync(added));
            }
            finally { _busy = false; }
        }
        private static void RefreshMetadata(List<VarPackage> added)
        {
            HashSet<string> groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (VarPackage package in added)
            {
                if (groups.Add(package.GroupName)) package.Group.Init();
            }
            // All new packages are registered before dependency resolution.
            foreach (VarPackage package in added) package.LoadMetaData();
            Dictionary<string, List<VarPackage>> dependents = new Dictionary<string, List<VarPackage>>(StringComparer.OrdinalIgnoreCase);
            foreach (VarPackage package in FileManager.GetPackages())
                if (package.PackageDependencies != null)
                    foreach (string dependency in package.PackageDependencies)
                    {
                        int separator = dependency == null ? -1 : dependency.LastIndexOf('.');
                        if (separator <= 0) continue;
                        string group = dependency.Substring(0, separator);
                        List<VarPackage> list;
                        if (!dependents.TryGetValue(group, out list)) dependents[group] = list = new List<VarPackage>();
                        list.Add(package);
                    }
            Queue<string> changed = new Queue<string>(groups);
            HashSet<VarPackage> affected = new HashSet<VarPackage>(added);
            while (changed.Count > 0)
            {
                List<VarPackage> list;
                if (!dependents.TryGetValue(changed.Dequeue(), out list)) continue;
                foreach (VarPackage package in list)
                    if (affected.Add(package))
                    {
                        package.LoadMetaData();
                        if (groups.Add(package.GroupName)) changed.Enqueue(package.GroupName);
                    }
            }
        }
        private string RepairHint()
        {
            return _queue.RepairReason == null ? "" : " 检测到：" + _queue.RepairReason + "；请选 刷新VAR → 完整扫描。";
        }
        private static void Report(Action<string> completed, string text)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo(text);
            if (completed != null) completed(text);
        }
        public void Dispose() { if (_disposed) return; _disposed = true; if (_queue != null) _queue.Dispose(); }
    }
}



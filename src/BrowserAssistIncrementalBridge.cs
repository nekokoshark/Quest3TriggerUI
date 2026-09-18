using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MVR.FileManagement;
using UnityEngine;

namespace Quest3TriggerUI
{
    // No assembly reference to a particular BrowserAssist VAR version.
    internal sealed class BrowserAssistIncrementalBridge
    {
        private readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private UnityEngine.Object owner;
        private Type manifest, entry, globals;
        private bool reconciled, refreshPending;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static object Read(object value, string name)
        {
            PropertyInfo property = value.GetType().GetProperty(name);
            return property != null ? property.GetValue(value, null) : value.GetType().GetField(name).GetValue(value);
        }
        internal static string RelativePath(string path)
        {
            string root = Path.GetFullPath("AddonPackages").TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            return "AddonPackages\\" + full.Substring(root.Length).Replace('/', '\\');
        }
        private bool Resolve()
        {
            if (owner != null) return (bool)Read(owner, "isEnabledAndActive");
            Assembly assembly = AssemblyCatalog.FindByType("JayJayWon.Globals");
            if (assembly == null) return false;
            Type candidate = assembly.GetType("JayJayWon.Globals");
            FieldInfo field = candidate.GetField("mvrScript", Static);
            UnityEngine.Object script = field == null ? null : field.GetValue(null) as UnityEngine.Object;
            if (script == null || !(bool)Read(script, "isEnabledAndActive")) return false;
            owner = script; globals = candidate;
            manifest = assembly.GetType("JayJayWon.VARPackageManifest", true);
            entry = assembly.GetType("JayJayWon.VARPackageManifestEntry", true);
            reconciled = false;
            return true;
        }
        private bool Indexed(object item)
        {
            if (item == null) return false;
            object group = Read(item, "vpvge");
            if (group == null) return false;
            IDictionary licenses = Read(group, "varVersionLicenses") as IDictionary;
            return licenses != null && licenses.Contains(Read(item, "varVersion"));
        }
        internal string Sync(IEnumerable<VarPackage> added)
        {
            foreach (VarPackage package in added)
                if (package != null && package.Enabled && !package.invalid && !VarRegistration.Incomplete(package) && !string.IsNullOrEmpty(package.FullPath)) pending.Add(package.FullPath);
            try
            {
                if (!Resolve()) return " BrowserAssist未就绪，待同步 " + pending.Count + "。";
                if (!reconciled)
                {
                    IDictionary known = (IDictionary)manifest.GetProperty("varPackagesByKey", Static).GetValue(null, null);
                    // One in-memory reconciliation per live BA instance; never enumerate disk.
                    foreach (VarPackage package in FileManager.GetPackages())
                    {
                        if (package == null || !package.Enabled || package.invalid || VarRegistration.Incomplete(package) || string.IsNullOrEmpty(package.FullPath)) continue;
                        string key = Path.GetFileNameWithoutExtension(package.FullPath);
                        if (!known.Contains(key) || !Indexed(known[key]) || (bool)Read(known[key], "metaFileLoadFailed")) pending.Add(package.FullPath);
                    }
                    reconciled = true;
                }
                IDictionary batch = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), entry));
                Dictionary<string, object> items = new Dictionary<string, object>();
                List<object> rescan = new List<object>();
                foreach (string path in pending)
                {
                    VarPackage current = FileManager.GetPackage(Path.GetFileNameWithoutExtension(path));
                    if (current == null || !current.Enabled || current.invalid || VarRegistration.Incomplete(current)) continue;
                    string relative = RelativePath(current.FullPath);
                    if (relative == null) continue;
                    object item = manifest.GetMethod("GetVPMEFromPath", Static, null, new Type[] { typeof(string), typeof(bool) }, null)
                        .Invoke(null, new object[] { relative, false });
                    if (!(bool)Read(item, "isBAAvailableVAR") || (bool)Read(item, "isDisabledOrOffloadedVAR")) continue;
                    if (Indexed(item)) rescan.Add(item);
                    batch[(string)Read(item, "vpmeKey")] = item;
                    items[path] = item;
                }
                if (batch.Count > 0)
                {
                    refreshPending = true;
                    manifest.GetMethod("RefreshManifestWithNewVARs", Static).Invoke(null, new object[] { batch, DateTime.Now, new HashSet<int>() });
                    // A previously failed native registration can leave a BA version with zero resources.
                    // Reconcile resources of only these queued packages, never all VARs.
                    foreach (object item in rescan)
                        manifest.GetMethod("RescanVARResources", Static).Invoke(null, new object[] { Read(item, "vpvge"), Read(item, "varVersion"), new HashSet<int>() });
                    // Reads only each new package's meta.json; no thumbnail cache creation or ruleset.
                    Type pc = globals.Assembly.GetType("JayJayWon.PC", true);
                    MethodInfo dependencies = pc.GetMethod("ProcessMetaFileDependencies", Static, null, new Type[] { entry }, null);
                    foreach (object item in batch.Values)
                        if (Indexed(item)) dependencies.Invoke(null, new object[] { item });
                }
                if (refreshPending)
                {
                    manifest.GetMethod("PostRescanRefresh", Static).Invoke(null, null);
                    refreshPending = false;
                }
                int synced = 0;
                foreach (KeyValuePair<string, object> pair in items)
                    if (Indexed(pair.Value) && !(bool)Read(pair.Value, "metaFileLoadFailed"))
                    { pending.Remove(pair.Key); synced++; }
                return " BrowserAssist同步 " + synced + "，待重试 " + pending.Count + "。";
            }
            catch (Exception e)
            {
                Exception detail = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                UnityEngine.Debug.LogError("[BA incremental] " + detail);
                return " BrowserAssist同步未完成，待重试 " + pending.Count + "：" + detail.Message;
            }
        }
    }
}




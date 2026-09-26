using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using HarmonyLib;

namespace Quest3TriggerUI
{
    internal static class TextureMetadataReuse
    {
        internal static ConfigEntry<bool> Enabled;
        private static Harmony _harmony;
        private static long _hits;
        private static readonly object Sync = new object();
        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly Regex Format = new Regex("\"format\"\\s*:\\s*\"([A-Za-z0-9]+)\"", RegexOptions.CultureInvariant);
        private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private sealed class Entry
        {
            internal WeakReference owner;
            internal string path, source, text;
            internal long metaStamp, dataStamp, dataLength, expires;
            internal int width, height;
            internal UnityEngine.TextureFormat format;
            internal bool used;
        }
        private static bool Active() { return Enabled == null || Enabled.Value; }
        private static void Remember(ImageLoaderThreaded.QueuedImage q, string path, string text, long metaStamp, long dataStamp, long length)
        {
            if (!Active()) return;
            int w, h;
            if (!TextureCacheEstimate.TryDimensions(text, out w, out h)) return;
            var match = Format.Match(text);
            if (!match.Success || !Enum.IsDefined(typeof(UnityEngine.TextureFormat), match.Groups[1].Value)) return;
            var e = new Entry { owner = new WeakReference(q), source = q.imgPath, path = path, text = text,
                metaStamp = metaStamp, dataStamp = dataStamp, dataLength = length, width = w, height = h,
                format = (UnityEngine.TextureFormat)Enum.Parse(typeof(UnityEngine.TextureFormat), match.Groups[1].Value),
                expires = DateTime.UtcNow.Ticks + TimeSpan.TicksPerSecond * 10 };
            lock (Sync) { Prune(q); if (Entries.Count < 128) Entries.Add(e); }
        }
        private static void Prune(ImageLoaderThreaded.QueuedImage owner)
        {
            long now = DateTime.UtcNow.Ticks;
            for (int i = Entries.Count - 1; i >= 0; i--)
                if (!Entries[i].owner.IsAlive || Entries[i].expires < now || ReferenceEquals(Entries[i].owner.Target, owner)) Entries.RemoveAt(i);
        }
        private static Entry Find(ImageLoaderThreaded.QueuedImage q)
        { foreach (var e in Entries) if (ReferenceEquals(e.owner.Target, q)) return e; return null; }
        private static bool Current(Entry e, string metaPath, ImageLoaderThreaded.QueuedImage q)
        {
            if (e == null || e.used || e.expires < DateTime.UtcNow.Ticks || q.imgPath != e.source ||
                !string.Equals(Path.GetFullPath(metaPath), e.path + "meta", StringComparison.OrdinalIgnoreCase)) return false;
            var data = new FileInfo(e.path); var meta = new FileInfo(metaPath);
            return data.Exists && meta.Exists && data.Length == e.dataLength &&
                data.LastWriteTimeUtc.Ticks == e.dataStamp && meta.LastWriteTimeUtc.Ticks == e.metaStamp;
        }
        private static string ReadText(string path, bool restrictPath, ImageLoaderThreaded.QueuedImage q)
        {
            Entry e; lock (Sync) { e = Find(q); }
            if (Active() && e != null)
            {
                try { if (Current(e, path, q)) { e.used = true; return e.text; } }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            lock (Sync) { Prune(q); }
            return MVR.FileManagement.FileManager.ReadAllText(path, restrictPath);
        }
        private static bool ReadMeta(ImageLoaderThreaded.QueuedImage __instance, string __0)
        {
            Entry e; lock (Sync) { e = Find(__instance); Prune(__instance); }
            if (!Active() || e == null || !e.used || !string.Equals(__0, e.text, StringComparison.Ordinal)) return true;
            __instance.width = e.width; __instance.height = e.height; __instance.textureFormat = e.format;
            if (System.Threading.Interlocked.Increment(ref _hits) == 1)
                Quest3TriggerUIPlugin.Log.LogInfo("[texture-metadata] first same-request read/parse reuse");
            return false;
        }
        private static Exception Done(ImageLoaderThreaded.QueuedImage __instance, Exception __exception)
        { lock (Sync) { Prune(__instance); } return __exception; }
        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions); int matches = 0;
            for (int i = 0; i < code.Count; i++)
            {
                var m = code[i].operand as MethodInfo;
                if (m == null || m.DeclaringType != typeof(MVR.FileManagement.FileManager) || m.Name != "ReadAllText" ||
                    m.GetParameters().Length != 2 || m.GetParameters()[0].ParameterType != typeof(string)) continue;
                if (code[i].blocks.Count != 0) throw new InvalidOperationException("metadata exception boundary changed");
                var load = new CodeInstruction(OpCodes.Ldarg_0); load.labels.AddRange(code[i].labels); code[i].labels.Clear();
                code[i].operand = typeof(TextureMetadataReuse).GetMethod("ReadText", All);
                code.Insert(i++, load); matches++;
            }
            if (matches != 2) throw new InvalidOperationException("metadata read anchors: " + matches);
            return code;
        }
        internal static void Install()
        {
            if (_harmony != null) return;
            try
            {
                _harmony = new Harmony("Quest3TriggerUI.texture-metadata-reuse");
                _harmony.Patch(typeof(ImageLoaderThreaded.QueuedImage).GetMethod("Process", All),
                    transpiler: new HarmonyMethod(typeof(TextureMetadataReuse).GetMethod("Transpile", All)),
                    finalizer: new HarmonyMethod(typeof(TextureMetadataReuse).GetMethod("Done", All)));
                _harmony.Patch(typeof(ImageLoaderThreaded.QueuedImage).GetMethod("ReadMetaJson", All),
                    prefix: new HarmonyMethod(typeof(TextureMetadataReuse).GetMethod("ReadMeta", All)));
                TextureCacheEstimate.MetadataObserved = Remember;
                Quest3TriggerUIPlugin.Log.LogInfo("[texture-metadata] installed; same request, checked cache stamps; weak max128/10s; native fallback");
            }
            catch (Exception e) { Shutdown(); Quest3TriggerUIPlugin.Log.LogInfo("[texture-metadata] not installed: " + e.Message); }
        }
        internal static void Shutdown()
        {
            TextureCacheEstimate.MetadataObserved = null;
            if (_harmony != null) _harmony.UnpatchAll(_harmony.Id);
            _harmony = null; lock (Sync) Entries.Clear();
        }
    }
}

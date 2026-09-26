using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MVR.FileManagement;
using SimpleJSON;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    // Person-preset texture preheat. Walks the 人物 preset-dock tab, parses
    // each .vap, and feeds its skin textures through VaM's own
    // ImageLoaderThreaded preload path so the .vamcache disk files already
    // exist when the preset is first loaded — the texture byte decode+DXT
    // compress is what makes cold presets stall.
    //
    // Cache-key fidelity (verified against ImageLoaderThreaded IL):
    //   memory signature = imgPath + ":C/:L/:N/:A/:BNs/:I"
    //   disk filename    = {cacheDir}/{basename '.'->'_'}_{size}_{fileTime}_{sig}.vamcache
    // Only the `textures` storable is preheated — its flags are fixed per
    // texture type in DAZCharacterTextureControl.StartSyncImage:
    //   Diffuse/Decal  -> compress only               (:C)
    //   Specular/Gloss -> compress+linear             (:C:L)
    //   Normal/Detail  -> compress+linear+normal map  (:C:L:N)
    // customTexture_* params are NOT covered: their flags come from
    // MaterialOptions customTextureNIs* slots whose prop->slot order depends
    // on each material's texture dictionary — guessing writes dead cache
    // entries and burns the decode anyway.
    //
    // Preload items use skipCache=true so decoded textures never enter
    // textureCache (VRAM isn't warmed — only disk files); Finish still
    // writes the .vamcache. The decoded q.tex is destroyed on completion
    // so nothing leaks.
    internal static class PresetPreheat
    {
        private const int FLinear = 1;
        private const int FNormal = 2;
        private const int FCompress = 4;
        private const int FAlphaGray = 8;

        // Enqueue pace — the loader decodes serially on its worker thread,
        // this just keeps the queue shallow and the parser amortized.
        private const int EnqueuePerTick = 2;
        private const int MaxInFlight = 8;

        private sealed class Job
        {
            internal string ImgPath;
            internal int Flags;
            internal ImageLoaderThreaded.QueuedImage Q;
        }

        private static readonly FieldInfo RealQueued =
            typeof(ImageLoaderThreaded).GetField("numRealQueuedImages",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static List<string> _presets;
        private static int _presetIdx;
        private static List<Job> _jobs;
        private static int _jobIdx;
        private static readonly List<Job> _inFlight = new List<Job>();
        private static readonly HashSet<string> _seen =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static int _cached, _enqueued, _missing, _skipPresets,
            _error, _decoded;
        private static string _status = "";
        private static float _runningSince;

        internal static Text Label;
        internal static bool Running;
        internal static string LastResult = "";

        internal static string StatusText
        {
            get { return Running ? _status : "预 热"; }
        }

        internal static void Toggle(List<string> personPresets)
        {
            if (Running) { Stop("已取消"); return; }
            Start(personPresets);
        }

        internal static void Start(List<string> personPresets)
        {
            Stop(null);
            _presets = personPresets != null
                ? new List<string>(personPresets) : new List<string>();
            _presetIdx = 0;
            _jobs = null;
            _jobIdx = 0;
            _cached = _enqueued = _missing = _skipPresets = _error =
                _decoded = 0;
            _seen.Clear();
            if (_presets.Count == 0)
            {
                LastResult = "预热：人物栏为空";
                Info(LastResult);
                return;
            }
            Running = true;
            _runningSince = Time.unscaledTime;
            LastResult = "";
            Info(
                "preset preheat start: " + _presets.Count + " presets");
        }

        internal static void Stop(string reason)
        {
            foreach (Job job in _inFlight)
                if (job.Q != null) job.Q.cancel = true;
            _inFlight.Clear();
            _jobs = null;
            if (Running && reason != null)
            {
                LastResult = reason;
                Info(
                    "preset preheat " + reason);
            }
            Running = false;
        }

        internal static void Tick()
        {
            try { TickInner(); }
            catch (Exception e) { Error("preset preheat tick", e); }
            if (Label != null)
            {
                string s = StatusText;
                if (Label.text != s) Label.text = s;
            }
        }

        private static void TickInner()
        {
            if (!Running) return;
            ImageLoaderThreaded il = ImageLoaderThreaded.singleton;
            if (il == null) return;

            // Drain finished preloads: destroy the decoded texture so the
            // only residue is the .vamcache file on disk.
            for (int i = _inFlight.Count - 1; i >= 0; i--)
            {
                Job job = _inFlight[i];
                ImageLoaderThreaded.QueuedImage q = job.Q;
                if (q == null || (!q.finished && !q.cancel)) continue;
                if (q.hadError) _error++;
                else if (!q.cancel) _decoded++;
                if (q.tex != null)
                {
                    UnityEngine.Object.Destroy(q.tex);
                    q.tex = null;
                }
                _inFlight.RemoveAt(i);
            }

            // Yield while VaM is loading real images — preheat stays
            // behind user-facing requests.
            if (RealQueuedCount() > 0)
            {
                _status = "预热 等待加载…";
                return;
            }

            if (_jobs == null || _jobIdx >= _jobs.Count)
            {
                if (!NextPreset()) return;
            }

            int budget = EnqueuePerTick;
            while (_jobIdx < _jobs.Count && _inFlight.Count < MaxInFlight &&
                   budget-- > 0)
            {
                Enqueue(il, _jobs[_jobIdx++]);
            }

            _status = "预热 " + Mathf.Min(_presetIdx, _presets.Count) + "/" +
                _presets.Count;
        }

        // Parse one preset per call; returns false when the whole list is
        // done.
        private static bool NextPreset()
        {
            while (_presetIdx < _presets.Count)
            {
                string path = _presets[_presetIdx++];
                _jobs = BuildJobs(path);
                _jobIdx = 0;
                if (_jobs.Count == 0)
                {
                    _skipPresets++;
                    continue;   // fully cached or no textures
                }
                return true;
            }
            Finish();
            return false;
        }

        private static void Finish()
        {
            int total = _presets.Count;
            Running = false;
            _jobs = null;
            LastResult = "预热完成 " + total + " 个预设（跳过 " +
                _skipPresets + " 个已缓存）";
            Info(
                "preset preheat done: presets=" + total +
                " skipped=" + _skipPresets + " enqueued=" + _enqueued +
                " cached=" + _cached + " missing=" + _missing +
                " decoded=" + _decoded + " errors=" + _error +
                " in " + (Time.unscaledTime - _runningSince).ToString("F1") +
                "s");
        }

        private static List<Job> BuildJobs(string presetPath)
        {
            List<Job> jobs = new List<Job>();
            try
            {
                string text = FileManager.ReadAllText(presetPath, false);
                JSONClass root = JSON.Parse(text).AsObject;
                JSONArray storables = root == null
                    ? null : root["storables"].AsArray;
                if (storables == null) return jobs;
                for (int i = 0; i < storables.Count; i++)
                {
                    JSONClass storable = storables[i].AsObject;
                    if (storable == null || storable["id"].Value != "textures")
                        continue;
                    foreach (KeyValuePair<string, JSONNode> kv in storable)
                    {
                        int flags = SkinFlags(kv.Key);
                        if (flags == 0) continue;
                        string url = kv.Value.Value;
                        AddJob(jobs, url, flags);
                    }
                }
            }
            catch (Exception e)
            {
                Error("preset preheat scan " + presetPath, e);
            }
            return jobs;
        }

        // Region{face|torso|limbs|genitals} + type suffix, matching
        // DAZCharacterTextureControl.StartSyncImage's TextureType flags.
        private static int SkinFlags(string key)
        {
            if (!key.EndsWith("Url", StringComparison.Ordinal)) return 0;
            if (key.EndsWith("DiffuseUrl", StringComparison.Ordinal) ||
                key.EndsWith("DecalUrl", StringComparison.Ordinal))
                return FCompress;
            if (key.EndsWith("SpecularUrl", StringComparison.Ordinal) ||
                key.EndsWith("GlossUrl", StringComparison.Ordinal))
                return FCompress | FLinear;
            if (key.EndsWith("NormalUrl", StringComparison.Ordinal) ||
                key.EndsWith("DetailUrl", StringComparison.Ordinal))
                return FCompress | FLinear | FNormal;
            return 0;
        }

        private static void AddJob(List<Job> jobs, string url, int flags)
        {
            if (string.IsNullOrEmpty(url) || url == "NULL") return;
            url = url.Trim();
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return;
            string ext = Path.GetExtension(url);
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" &&
                !ext.Equals(".tga", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".tif", StringComparison.OrdinalIgnoreCase))
                return;
            if (!_seen.Add(url + "|" + flags)) return;   // shared texture
            jobs.Add(new Job { ImgPath = url, Flags = flags });
        }

        private static void Enqueue(ImageLoaderThreaded il, Job job)
        {
            var q = new ImageLoaderThreaded.QueuedImage();
            q.imgPath = job.ImgPath;
            q.createMipMaps = true;
            q.compress = (job.Flags & FCompress) != 0;
            q.linear = (job.Flags & FLinear) != 0;
            q.isNormalMap = (job.Flags & FNormal) != 0;
            q.createAlphaFromGrayscale = (job.Flags & FAlphaGray) != 0;
            // Disk-write only: skipCache keeps the decoded texture out of
            // textureCache (the file is written by Finish regardless).
            q.skipCache = true;
            if (il.IsTextureCached(q.cacheSignature) ||
                DiskCacheExists(job))
            {
                _cached++;
                return;
            }
            il.PreloadImage(q);
            job.Q = q;
            _inFlight.Add(job);
            _enqueued++;
        }

        // Replicates QueuedImage.GetDiskCachePath:
        //   {dir}/{basename '.'->'_'}_{size}_{fileTime}_{diskSig}.vamcache
        private static bool DiskCacheExists(Job job)
        {
            string dir;
            try { dir = CacheManager.GetTextureCacheDir(); }
            catch { return false; }
            if (dir == null) return false;
            FileEntry fe;
            try { fe = FileManager.GetFileEntry(job.ImgPath, false); }
            catch { fe = null; }
            if (fe == null)
            {
                _missing++;
                return true;    // nothing to decode anyway — drop the job
            }
            string sig = "";
            if ((job.Flags & FCompress) != 0) sig += "_C";
            if ((job.Flags & FLinear) != 0) sig += "_L";
            if ((job.Flags & FNormal) != 0) sig += "_N";
            if ((job.Flags & FAlphaGray) != 0) sig += "_A";
            string path = dir + "/" +
                Path.GetFileName(job.ImgPath).Replace('.', '_') + "_" +
                fe.Size + "_" + fe.LastWriteTime.ToFileTime() + "_" +
                sig + ".vamcache";
            try { return File.Exists(path); }
            catch { return false; }
        }

        private static int RealQueuedCount()
        {
            try
            {
                ImageLoaderThreaded il = ImageLoaderThreaded.singleton;
                return il == null || RealQueued == null
                    ? 0 : (int)RealQueued.GetValue(il);
            }
            catch { return 0; }
        }

        private static void Info(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo(message);
        }

        private static void Error(string context, Exception e)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogWarning(
                    context + ": " + e.Message);
        }
    }
}

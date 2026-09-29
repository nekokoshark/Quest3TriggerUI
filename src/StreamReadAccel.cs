using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using MVR.FileManagement;
using UnityEngine;

namespace Quest3TriggerUI
{
    // 换人窗口里每个 item 都要把 .vab 从 .var 里读一遍：DAZDynamic::Load 走的是
    // FileManager.OpenStream -> FileEntryStream.Stream -> new BinaryReader(stream)。
    //
    // .var 里的 stored（未压缩）条目流是 SharpZipLib 的 ZipFile/PartialInputStream，
    // 它没有自己的缓冲：Read 的 IL 就是 Monitor.Enter(baseStream) ->
    // baseStream.Seek(readPos_) -> baseStream.Read(调用者要的几个字节) ->
    // Monitor.Exit（见 工作区\liveset_20260928\il_sharpzip.txt 的
    // PartialInputStream::Read）。BinaryReader 的 ReadInt32 / ReadSingle /
    // ReadString 每次只要 4 个字节，于是每一个数都要付一次 Seek + Read。
    //
    // 实测印证（BepInEx\LogOutput.log L37310-37350，金瓶儿趟的 [item-build]）：
    //   4.46MiB -> 3211ms, 2.52MiB -> 1752ms, 1.37MiB -> 959ms,
    //   1.29MiB -> 883ms,  1.28MiB -> 889ms,  0.64MiB -> 445ms
    // 六个 .vab 合计 11.4MiB / 8.4s 约等于 1.43 MB/s，而 DAZMesh 与
    // ClothGeometryData 这两个完全不同的解析器落在同一条直线上——瓶颈不是解析
    // 算法，而是这条按调用计费的读路径（每 4 个字节约 1.4 微秒）。
    //
    // 注意：这里**不用 System.IO.BufferedStream**——游戏内 Mono 的 BufferedStream
    // 字节）：原生 4 字节读 1598ms / 原生 64KiB 读 1.2ms / 套 64KiB BufferedStream
    // 之后的 4 字节读 10.4ms —— 读法与内容不变，只是把"每 4 个字节一次底层
    // Seek+Read"压成"每 64KiB 一次"。
    //
    // 挂点是两个具体 FileEntryStream 的构造函数（不是 get_Stream 属性访问器：
    // 属性访问器可能被 Mono 内联，构造函数不会），所以每个打开的文件流恰好包一次。
    // Deflated 条目走 InflaterInputStream，本来就按块读，包不包都无害。
    //
    // 这里同时给出验收账：每趟 burst 的 seen/wrapped/bytes，以及 ItemBuildProbe
    // 逐 item 读到的 WrappedCount / WrappedBytes 差值——用来证明某个 item 的
    // 慢读确实经过了这条被包裹的路径，而不是被漏掉了。
    internal static class StreamReadAccel
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ProbeOnce;

        // ItemBuildProbe 逐 item 对账：这一个 item 的 Load 里包了几个流、多少字节。
        internal static int WrappedCount
        {
            get { return _wrapped; }
        }

        internal static long WrappedBytes
        {
            get { return _wrappedBytes; }
        }

        private const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const int BufferBytes = 65536;
        private const string ZipEntryStreamType =
            "ICSharpCode.SharpZipLib.Zip.ZipFile+PartialInputStream";
        private const int ProbeBytes = 262144;
        private const int ProbeChunk = 65536;
        private const int ProbeRead = 4;
        private const int ProbeOpens = 50;
        private const float BurstGap = 4f;
        private const int WrapLogFirst = 6;

        private static Harmony _harmony;
        private static bool _tried;
        private static bool _probeArmed;
        private static MethodInfo _setter;
        private static FieldInfo _storeName;
        private static MethodInfo _storeFolder;
        private static int _wrapped;
        private static int _failed;
        private static int _other;
        private static long _wrappedBytes;
        private static float _lastSeen;
        private static int _burstSeen;
        private static int _burstWrapped;
        private static int _burstFailed;
        private static string _burstTypes = "";
        private static long _burstBytes;
        private static readonly object _sourceLock = new object();
        private static readonly string[] _sourceNames = new string[8];
        private static readonly int[] _sourceCounts = new int[8];
        private static bool _probeDone;
        private static bool _probeSuspend;

        internal static void Install()
        {
            if (_tried) return;
            _tried = true;
            if (Enabled != null && !Enabled.Value)
            {
                Log("off: file entry streams stay on the native read path");
                return;
            }
            try
            {
                Assembly asm = typeof(SuperController).Assembly;
                Type entryStream = asm.GetType("MVR.FileManagement.FileEntryStream");
                if (entryStream == null) throw new MissingMemberException("MVR.FileManagement.FileEntryStream");
                PropertyInfo property = entryStream.GetProperty("Stream", All);
                if (property == null) throw new MissingMemberException("FileEntryStream.Stream");
                _setter = property.GetSetMethod(true);
                if (_setter == null) throw new MissingMemberException("FileEntryStream.Stream setter");

                _harmony = new Harmony("Quest3TriggerUI.stream-read-accel");
                HarmonyMethod after = new HarmonyMethod(typeof(StreamReadAccel).GetMethod(
                    "AfterEntryStreamCreated", BindingFlags.NonPublic | BindingFlags.Static));
                int patched = 0;
                foreach (string name in new string[]
                {
                    "MVR.FileManagement.SystemFileEntryStream",
                    "MVR.FileManagement.VarFileEntryStream"
                })
                {
                    Type t = asm.GetType(name);
                    ConstructorInfo ctor = t == null ? null : SoleConstructor(t);
                    if (ctor == null) continue;
                    _harmony.Patch(ctor, postfix: after);
                    patched++;
                }
                if (patched == 0) throw new MissingMemberException("FileEntryStream constructors");

                string probe = "off";
                _probeArmed = ProbeOnce != null && ProbeOnce.Value;
                if (_probeArmed)
                {
                    Type dynamic = asm.GetType("MeshVR.DAZDynamic");
                    Type preset = asm.GetType("MeshVR.PresetManager");
                    MethodInfo load = dynamic == null ? null : dynamic.GetMethod(
                        "Load", All, null, new Type[] { typeof(bool) }, null);
                    if (preset != null)
                    {
                        _storeName = preset.GetField("storeName", All);
                        _storeFolder = preset.GetMethod(
                            "GetStoreFolderPath", All, null, new Type[] { typeof(bool) }, null);
                    }
                    if (load != null && _storeName != null && _storeFolder != null)
                    {
                        _harmony.Patch(load, prefix: new HarmonyMethod(typeof(StreamReadAccel).GetMethod(
                            "BeforeDynamicLoad", BindingFlags.NonPublic | BindingFlags.Static)));
                        probe = "armed";
                    }
                    else
                    {
                        _probeArmed = false;
                        probe = "unavailable";
                    }
                }
                Log("installed; bufferKiB=" + (BufferBytes / 1024) + " constructors=" + patched +
                    " probe=" + probe);
            }
            catch (Exception e)
            {
                Log("not installed: " + e.Message);
            }
        }

        private static ConstructorInfo SoleConstructor(Type type)
        {
            ConstructorInfo[] ctors = type.GetConstructors(All);
            if (ctors.Length != 1) return null;
            if (ctors[0].GetParameters().Length != 1) return null;
            return ctors[0];
        }

        private static void AfterEntryStreamCreated(object __instance)
        {
            Note();
            try
            {
                if (_probeSuspend) return;
                if (Enabled != null && !Enabled.Value) return;
                FileEntryStream file = __instance as FileEntryStream;
                if (file == null) return;
                Stream raw = file.Stream;
                if (raw == null || raw is EntryStreamBuffer) return;
                CountSource(raw.GetType().Name);
                if (!WantsBuffer(raw))
                {
                    _other++;
                    _burstTypes = AddType(_burstTypes, raw.GetType().Name);
                    return;
                }
                long length = -1;
                try { length = raw.Length; }
                catch (Exception) { }
                _setter.Invoke(file, new object[] { new EntryStreamBuffer(raw, BufferBytes) });
                _wrapped++;
                _burstWrapped++;
                if (length > 0) { _wrappedBytes += length; _burstBytes += length; }
                if (_wrapped <= WrapLogFirst || _wrapped % 200 == 0)
                    Log("wrapped=" + _wrapped + " source=" + raw.GetType().Name +
                        " bytes=" + length + " bufferKiB=" + (BufferBytes / 1024));
            }
            catch (Exception e)
            {
                _failed++;
                _burstFailed++;
                if (_failed <= 3) Log("wrap skipped: " + e.GetType().Name + " " + e.Message);
            }
        }

        // 一趟换人里的所有 FileEntryStream 是连着构造的；空转 4 秒就把这一卷账打出来。
        private static void Note()
        {
            float now = Time.realtimeSinceStartup;
            if (_burstSeen > 0 && now - _lastSeen > BurstGap) FlushBurst();
            _lastSeen = now;
            _burstSeen++;
        }

        private static void FlushBurst()
        {
            Log("burst seen=" + _burstSeen + " wrapped=" + _burstWrapped +
                " bytes=" + (_wrappedBytes / 1048576.0).ToString("0.0") + "MiB" +
                " burstBytes=" + (_burstBytes / 1048576.0).ToString("0.0") + "MiB" +
                " skippedType=" + _burstFailed +
                " other=" + (_burstTypes.Length == 0 ? "-" : _burstTypes) +
                " types=" + DescribeSources());
            _burstSeen = 0;
            _burstWrapped = 0;
            _burstFailed = 0;
            _burstBytes = 0;
            _burstTypes = "";
        }

        // burst 里按来源类型计数：PartialInputStream / FileStream 会被包缓冲，
        // InflaterInputStream 跳过。场景冷载到底吃到没吃到这套，看这一行就知道。
        private static void CountSource(string name)
        {
            if (name == null || name.Length == 0) return;
            lock (_sourceLock)
            {
                for (int i = 0; i < _sourceNames.Length; i++)
                {
                    if (_sourceNames[i] == name) { _sourceCounts[i]++; return; }
                    if (_sourceNames[i] == null)
                    {
                        _sourceNames[i] = name;
                        _sourceCounts[i] = 1;
                        return;
                    }
                }
            }
        }

        private static string DescribeSources()
        {
            string text = "";
            lock (_sourceLock)
            {
                for (int i = 0; i < _sourceNames.Length; i++)
                {
                    if (_sourceNames[i] == null) continue;
                    if (text.Length > 0) text += ",";
                    text += _sourceNames[i] + ":" + _sourceCounts[i];
                    _sourceNames[i] = null;
                    _sourceCounts[i] = 0;
                }
            }
            return text.Length == 0 ? "-" : text;
        }

        private static string AddType(string list, string name)
        {
            if (name == null || name.Length == 0) return list;
            if (list.Length == 0) return name;
            if (list.IndexOf(name, StringComparison.Ordinal) >= 0) return list;
            if (list.Length > 64) return list;
            return list + "," + name;
        }

        private static bool WantsBuffer(Stream stream)
        {
            Type t = stream.GetType();
            if (t == typeof(FileStream)) return true;
            return string.Equals(t.FullName, ZipEntryStreamType, StringComparison.Ordinal);
        }

        private sealed class ProbeArm
        {
            internal long Ms;
            internal int Total;
            internal long Length = -1;
            internal string Type = "-";
            internal string Error;
        }

        private static void BeforeDynamicLoad(object __instance)
        {
            if (_probeDone || !_probeArmed) return;
            try
            {
                string store = _storeName == null ? null : _storeName.GetValue(__instance) as string;
                string folder = _storeFolder == null ? null
                    : _storeFolder.Invoke(__instance, new object[] { true }) as string;
                if (string.IsNullOrEmpty(store) || string.IsNullOrEmpty(folder)) return;
                _probeDone = true;
                string path = folder + store + ".vab";
                ProbeArm nativeSmall = TimeRead(path, ProbeBytes, ProbeRead, true);
                ProbeArm nativeBulk = TimeRead(path, ProbeBytes, ProbeChunk, true);
                ProbeArm wrappedSmall = TimeRead(path, ProbeBytes, ProbeRead, false);
                long open = TimeOpens(path);
                Log("probe path=" + path
                    + " | native4B " + Describe(nativeSmall) + " of " + (ProbeBytes / ProbeRead) + " reads"
                    + " | native64K " + Describe(nativeBulk) + " of " + (ProbeBytes / ProbeChunk) + " reads"
                    + " | wrapped4B " + Describe(wrappedSmall)
                    + " | open" + ProbeOpens + "=" + open + "ms(" + (open * 1000L / ProbeOpens) + "us each)"
                    + " | wraps=" + _wrapped + " bytes=" + _wrappedBytes + " skipped=" + _failed);
            }
            catch (Exception e)
            {
                _probeSuspend = false;
                _probeDone = true;
                Log("probe skipped: " + e.GetType().Name + " " + e.Message);
            }
        }

        private static string Describe(ProbeArm arm)
        {
            return "type=" + arm.Type + " len=" + arm.Length + " total=" + arm.Total +
                " ms=" + arm.Ms + (arm.Error == null ? "" : " err=" + arm.Error);
        }

        private static ProbeArm TimeRead(string path, int bytes, int chunk, bool suspend)
        {
            ProbeArm arm = new ProbeArm();
            bool old = _probeSuspend;
            _probeSuspend = suspend;
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                using (FileEntryStream file = FileManager.OpenStream(path, true))
                {
                    Stream stream = file.Stream;
                    arm.Type = stream.GetType().Name;
                    try { arm.Length = stream.Length; }
                    catch (Exception) { }
                    byte[] buffer = new byte[chunk];
                    int total = 0;
                    while (total < bytes)
                    {
                        int read = stream.Read(buffer, 0, chunk);
                        if (read <= 0) break;
                        total += read;
                    }
                    arm.Total = total;
                }
            }
            catch (Exception e)
            {
                arm.Error = e.GetType().Name + ":" + e.Message;
            }
            finally
            {
                watch.Stop();
                _probeSuspend = old;
            }
            arm.Ms = watch.ElapsedMilliseconds;
            return arm;
        }

        private static long TimeOpens(string path)
        {
            Stopwatch watch = Stopwatch.StartNew();
            for (int i = 0; i < ProbeOpens; i++)
            {
                try
                {
                    using (FileEntryStream file = FileManager.OpenStream(path, true))
                    {
                        file.Stream.ReadByte();
                    }
                }
                catch (Exception) { }
            }
            watch.Stop();
            return watch.ElapsedMilliseconds;
        }

        internal static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("[stream-accel] " + message);
        }
    }
}

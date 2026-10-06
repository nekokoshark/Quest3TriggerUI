using System;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using HelloMeow;
using Un4seen.Bass;
using UnityEngine;

namespace Quest3TriggerUI
{
    // The channel and encoded input belong to an operation, not to the mutable
    // importer.handle field. Only the unpublished clip belongs to this patch.
    internal static class AudioPcmChunks
    {
        internal const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const int ChunkFloats = 131072;
        private static readonly MethodInfo PointerTarget = typeof(BassImporter).GetMethod("SetData", new[] { typeof(IntPtr), typeof(long) });
        private static readonly MethodInfo BytesTarget = typeof(BassImporter).GetMethod("SetData", new[] { typeof(byte[]) });
        private static readonly MethodInfo LoadTarget = typeof(DecoderImporter).GetMethod("Load", All);
        private static readonly MethodInfo StreamingTarget = typeof(DecoderImporter).GetMethod("LoadStreaming", All);
        private static readonly MethodInfo ResetTarget = typeof(AudioImporter).GetMethod("Cleanup", All);
        private static readonly MethodInfo CleanupTarget = typeof(BassImporter).GetMethod("Cleanup", All);
        private static readonly FieldInfo Handle = typeof(BassImporter).GetField("handle", All);
        private static readonly PropertyInfo Info = typeof(DecoderImporter).GetProperty("info", All);
        private static readonly Type InfoType = typeof(DecoderImporter).GetNestedType("AudioInfo", All);
        private delegate void LoadedCall(AudioImporter owner, AudioClip clip);
        private delegate void ProgressCall(AudioImporter owner, float progress);
        private delegate void ErrorCall(AudioImporter owner, string error);
        private static readonly LoadedCall Loaded = (LoadedCall)Delegate.CreateDelegate(typeof(LoadedCall), typeof(AudioImporter).GetMethod("OnLoaded", All));
        private static readonly ProgressCall Progress = (ProgressCall)Delegate.CreateDelegate(typeof(ProgressCall), typeof(AudioImporter).GetMethod("OnProgress", All));
        private static readonly ErrorCall Error = (ErrorCall)Delegate.CreateDelegate(typeof(ErrorCall), typeof(AudioImporter).GetMethod("OnError", All));
        private static Harmony harmony;
        private static bool accepting;
        private static int active;

        private static bool Selected()
        {
            var patches = Harmony.GetPatchInfo(PointerTarget);
            MethodInfo selected = null;
            if (patches != null) foreach (var patch in patches.Postfixes)
            {
                var method = patch.PatchMethod;
                if (method.Name == "AfterPointer" && method.DeclaringType != null && method.DeclaringType.Name == "AudioPcmChunks") selected = method;
            }
            return accepting && selected == typeof(AudioPcmChunks).GetMethod("AfterPointer", All);
        }

        private static bool ExactBass(object owner) { return owner != null && owner.GetType() == typeof(BassImporter); }
        private static void AfterPointer(BassImporter __instance, IntPtr __0, long __1, ref IEnumerator __result)
        { if (Selected() && ExactBass(__instance)) __result = new Entry(__instance, __result, __0, __1, null, null, -1); }
        private static void AfterBytes(BassImporter __instance, byte[] __0, ref IEnumerator __result)
        { if (Selected() && ExactBass(__instance) && __0 != null) __result = new Entry(__instance, __result, IntPtr.Zero, 0, __0, null, -1); }
        private static void AfterLoad(DecoderImporter __instance, string __0, ref IEnumerator __result)
        { if (Selected() && ExactBass(__instance) && __0 != null) __result = new Entry((BassImporter)__instance, __result, IntPtr.Zero, 0, null, __0, -1); }
        private static void AfterStreaming(DecoderImporter __instance, string __0, int __1, ref IEnumerator __result)
        { if (Selected() && ExactBass(__instance) && __0 != null) __result = new Entry((BassImporter)__instance, __result, IntPtr.Zero, 0, null, __0, __1); }

        // The reset is managed; StopAllCoroutines itself is an internal call.
        // A per-operation frame heartbeat also detects native coroutine stops
        // without assuming that Unity invokes an iterator's Dispose/finally.
        private static void BeforeReset(AudioImporter __instance) { CancelOwner(__instance as BassImporter); }
        private static void BeforeCleanup(BassImporter __instance) { CancelOwner(__instance); }
        private static void CancelOwner(BassImporter owner)
        {
            if (ReferenceEquals(owner, null)) return;
            foreach (var component in owner.GetComponents(typeof(AudioPcmLifetime)))
                ((AudioPcmLifetime)component).Cancel();
        }
        private static void CancelGenerations(BassImporter owner)
        {
            var patches = Harmony.GetPatchInfo(ResetTarget);
            if (patches == null) return;
            foreach (var patch in patches.Prefixes)
            {
                var method = patch.PatchMethod;
                if (method.Name == "BeforeReset" && method.DeclaringType != null && method.DeclaringType.Name == "AudioPcmChunks")
                    method.Invoke(null, new object[] { owner });
            }
        }

        private sealed class Entry : IEnumerator, IDisposable
        {
            private BassImporter owner;
            private IEnumerator fallback;
            private IntPtr pointer;
            private long length;
            private byte[] bytes;
            private string uri;
            private int initialLength;
            private bool started, completed;
            private AudioPcmLifetime lifetime;
            private object current;
            internal Entry(BassImporter owner, IEnumerator fallback, IntPtr pointer, long length, byte[] bytes, string uri, int initialLength)
            { this.owner = owner; this.fallback = fallback; this.pointer = pointer; this.length = length; this.bytes = bytes; this.uri = uri; this.initialLength = initialLength; }
            public object Current { get { return current; } }
            public void Reset() { throw new NotSupportedException(); }
            public bool MoveNext()
            {
                if (completed) return false;
                if (!started)
                {
                    started = true;
                    // A not-yet-started old-generation iterator may outlive its
                    // entry hooks. Re-enter the current factory, not old code.
                    if (!accepting)
                    {
                        fallback = uri != null ? (IEnumerator)(initialLength < 0 ? LoadTarget.Invoke(owner, new object[] { uri })
                            : StreamingTarget.Invoke(owner, new object[] { uri, initialLength }))
                            : bytes != null ? owner.SetData(bytes) : owner.SetData(pointer, length);
                    }
                    else
                    {
                        CancelGenerations(owner);
                        int existing = (int)Handle.GetValue(owner);
                        // Never commandeer an original decoder already running
                        // at hot installation, or a derived decoder's contract.
                        if (existing == -1 || existing == 0 || Bass.BASS_ChannelGetInfo(existing) == null)
                        {
                            lifetime = (AudioPcmLifetime)owner.gameObject.AddComponent(typeof(AudioPcmLifetime));
                            Interlocked.Increment(ref active);
                            var session = new Session(pointer, length, bytes, uri, initialLength);
                            bytes = null; pointer = IntPtr.Zero;
                            lifetime.Begin(owner, session);
                            fallback = null;
                            try { current = owner.StartCoroutine(lifetime.Routine); }
                            catch { lifetime.Cancel(); throw; }
                            return true;
                        }
                    }
                }
                if (fallback != null)
                {
                    bool more = fallback.MoveNext(); current = more ? fallback.Current : null;
                    if (more) return true;
                }
                completed = true; owner = null; fallback = null; bytes = null; current = null; lifetime = null;
                return false;
            }
            public void Dispose()
            {
                if (completed) return;
                completed = true;
                if (!ReferenceEquals(lifetime, null)) lifetime.Cancel();
                var disposable = fallback as IDisposable;
                if (disposable != null) disposable.Dispose();
                owner = null; fallback = null; bytes = null; current = null;
                // Before first MoveNext no input ownership was taken, matching
                // the original lazy iterator's ownership boundary.
            }
        }

        internal sealed class Session
        {
            internal readonly object Gate = new object();
            internal int Channel = -1, BoundChannel = -1, LengthSamples, Rate, Channels, Requested, ChunkSize, Offset;
            internal float[] Buffer;
            internal volatile bool OpenDone;
            internal bool Ready, DecodeDone, Cancelled, Published, Ended;
            internal Exception Failure;
            internal AudioClip Clip;
            internal int LastFrame;
            private IntPtr input;
            private long length;
            private byte[] bytes;
            internal readonly string Uri;
            internal readonly int InitialLength;
            private Thread worker;

            internal Session(IntPtr input, long length, byte[] bytes, string uri, int initialLength)
            { this.input = input; this.length = length; this.bytes = bytes; Uri = uri; InitialLength = initialLength; }

            private void ReadInfo()
            {
                var info = Bass.BASS_ChannelGetInfo(Channel);
                if (info == null) throw new InvalidOperationException("BASS channel info missing");
                LengthSamples = (int)Bass.BASS_ChannelGetLength(Channel) / 4;
                Rate = info.freq; Channels = info.chans;
                if (Channels <= 0 || Rate <= 0 || LengthSamples < 0) throw new InvalidOperationException("Invalid BASS audio shape");
            }
            internal void Open()
            {
                if (Uri != null)
                {
                    worker = new Thread(OpenFile); worker.IsBackground = true; worker.Start();
                    return;
                }
                try
                {
                    if (bytes != null)
                    {
                        input = Marshal.AllocHGlobal(bytes.Length); length = bytes.Length;
                        Marshal.Copy(bytes, 0, input, bytes.Length); bytes = null;
                    }
                    Channel = Bass.BASS_StreamCreateFile(input, 0L, length, BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_DECODE);
                    if (Channel != 0) ReadInfo();
                }
                catch { Release(); throw; }
                finally { OpenDone = true; }
            }
            private void OpenFile()
            {
                try
                {
                    int channel = Uri.StartsWith("file://")
                        ? Bass.BASS_StreamCreateFile(Uri.Substring(7), 0L, 0L, BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_DECODE)
                        : Bass.BASS_StreamCreateURL(Uri, 0, BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_DECODE, null, IntPtr.Zero);
                    lock (Gate) { Channel = channel; if (!Cancelled && channel != 0) ReadInfo(); }
                }
                catch (Exception ex) { lock (Gate) Failure = ex; }
                finally { lock (Gate) { OpenDone = true; if (Cancelled || Failure != null) ReleaseLocked(); Monitor.PulseAll(Gate); } }
            }
            internal void StartDecode()
            {
                Requested = InitialLength < 0 ? LengthSamples : Mathf.Clamp(unchecked(InitialLength * Rate * Channels), 44100, LengthSamples);
                // Chunk starts are whole frames. The native reads retain the
                // original <=4096-sample maximum; final SetData is exact-size.
                int divisor = Channels, other = 4096;
                while (other != 0) { int next = divisor % other; divisor = other; other = next; }
                int aligned = checked(4096 / divisor * Channels);
                ChunkSize = aligned <= ChunkFloats ? ChunkFloats / aligned * aligned : ChunkFloats / Channels * Channels;
                if (ChunkSize == 0) throw new InvalidOperationException("BASS channel count exceeds PCM chunk capacity");
                worker = new Thread(Decode); worker.IsBackground = true; worker.Start();
            }
            private void Decode()
            {
                try
                {
                    var scratch = new float[4096];
                    var reusable = new float[Math.Min(ChunkSize, Requested)];
                    bool eof = false;
                    int position = 0;
                    do
                    {
                        int count = Math.Min(ChunkSize, Requested - position);
                        var chunk = count == reusable.Length ? reusable : new float[count];
                        Array.Clear(chunk, 0, chunk.Length);
                        int written = 0;
                        while (!eof && written < count)
                        {
                            int request = Math.Min(4096, count - written), read;
                            lock (Gate)
                            {
                                if (Cancelled) return;
                                // No mutable importer fields are read by the worker.
                                int raw = Bass.BASS_ChannelGetData(Channel, written == 0 ? chunk : scratch, request * 4);
                                read = raw / 4;
                                if (raw == -1 || read == 0) { eof = true; break; }
                                if (read < 0 || read > request) throw new InvalidOperationException("Invalid BASS sample count");
                                if (written != 0) Array.Copy(scratch, 0, chunk, written, read);
                            }
                            written += read;
                        }
                        lock (Gate)
                        {
                            if (Cancelled) return;
                            Buffer = chunk; Offset = position; Ready = true;
                            position += count; DecodeDone = position == Requested;
                            while (Ready && !Cancelled) Monitor.Wait(Gate);
                            if (Cancelled) return;
                        }
                    } while (position < Requested);
                }
                catch (Exception ex) { lock (Gate) { Failure = ex; DecodeDone = true; } }
                finally { lock (Gate) { if (Cancelled || Failure != null) ReleaseLocked(); Monitor.PulseAll(Gate); } }
            }
            internal float ProgressValue()
            { lock (Gate) return (float)Bass.BASS_ChannelGetPosition(Channel, BASSMode.BASS_POS_BYTE) / 4f / (float)LengthSamples; }
            internal void Consume() { lock (Gate) { Buffer = null; Ready = false; Monitor.PulseAll(Gate); } }
            internal void Cancel()
            {
                lock (Gate)
                {
                    Cancelled = true; Buffer = null; Ready = false; bytes = null; Monitor.PulseAll(Gate);
                    // A file open may still be producing a channel. Its worker
                    // adopts and frees the late result before exiting.
                    if (Uri == null || OpenDone) ReleaseLocked();
                }
            }
            internal void Release() { lock (Gate) ReleaseLocked(); }
            private void ReleaseLocked()
            {
                // BASS uses unsigned native handle bits in its signed int API;
                // a valid channel can be negative. Only 0 and our -1 are empty.
                if (Channel != 0 && Channel != -1) { Bass.BASS_StreamFree(Channel); Channel = -1; }
                if (input != IntPtr.Zero) { Marshal.FreeHGlobal(input); input = IntPtr.Zero; }
                bytes = null;
            }
            internal int ReadStreaming(float[] buffer, int count)
            { lock (Gate) return Cancelled ? 0 : Bass.BASS_ChannelGetData(Channel, buffer, count * 4) / 4; }
        }

        internal static IEnumerator Run(AudioPcmLifetime lifetime, BassImporter owner, Session session)
        {
            try
            {
                session.Open();
                while (!session.OpenDone) { if (session.Cancelled) yield break; yield return null; }
                if (session.Cancelled) yield break;
                if (session.Failure != null) throw session.Failure;
                if (session.Channel == 0)
                {
                    Error(owner, session.Uri != null ? "Could not open: " + session.Uri : "Could not decode mp3 bytes");
                    yield break;
                }
                session.BoundChannel = session.Channel;
                Handle.SetValue(owner, session.Channel);
                Info.SetValue(owner, Activator.CreateInstance(InfoType, All, null,
                    new object[] { session.LengthSamples, session.Rate, session.Channels }, null), null);
                session.Clip = AudioClip.Create(string.Empty, session.LengthSamples / session.Channels, session.Channels, session.Rate, false);
                session.StartDecode();
                bool finished = false;
                while (!finished)
                {
                    if (session.Cancelled) yield break;
                    float[] chunk = null; int offset = 0;
                    lock (session.Gate)
                    {
                        if (session.Failure != null) throw session.Failure;
                        if (session.Ready) { chunk = session.Buffer; offset = session.Offset; finished = session.DecodeDone; }
                    }
                    if (chunk != null)
                    {
                        session.Clip.SetData(chunk, offset / session.Channels);
                        chunk = null;
                        session.Consume();
                    }
                    if (session.Cancelled) yield break;
                    if (!finished)
                    {
                        Progress(owner, session.ProgressValue());
                        if (session.Cancelled) yield break;
                        yield return null;
                    }
                }
                if (session.Cancelled) yield break;
                if (session.InitialLength < 0) session.Release();
                // From the moment OnLoaded is entered the clip may have escaped
                // to a shared consumer, even if a callback throws/re-enters.
                session.Published = true;
                Loaded(owner, session.Clip);
                if (session.Cancelled) yield break;
                if (session.InitialLength >= 0)
                {
                    // Preserve the original 0.1s continuation, including its
                    // partial-read behavior; only the initial bulk stage changes.
                    int size = session.Rate * session.Channels / 10;
                    var buffer = new float[size];
                    int index = session.Requested;
                    while (index < session.LengthSamples)
                    {
                        if (session.Cancelled) yield break;
                        int samples = session.ReadStreaming(buffer, size);
                        if (samples == -1 || samples == 0) break;
                        session.Clip.SetData(buffer, index / session.Channels);
                        index += samples;
                        Progress(owner, session.ProgressValue());
                        if (session.Cancelled) yield break;
                        yield return null;
                    }
                }
                // SetData originally does not issue the final progress callback.
                if (session.Uri != null) Progress(owner, 1f);
            }
            finally { lifetime.Finish(); }
        }

        internal static void Detach(BassImporter owner, Session session)
        {
            // The lifecycle detaches before a new operation can publish its
            // field. Old native release never touches the new channel's field.
            if (!ReferenceEquals(owner, null) && (int)Handle.GetValue(owner) == session.BoundChannel) Handle.SetValue(owner, -1);
        }
        internal static void End()
        {
            Interlocked.Decrement(ref active);
            if (!accepting && active == 0 && harmony != null) { harmony.UnpatchAll(harmony.Id); harmony = null; }
        }
        internal static void Install()
        {
            if (harmony != null) return;
            if (Handle == null || Handle.FieldType != typeof(int) || Info == null || InfoType == null) throw new MissingMemberException("BASS importer shape changed");
            harmony = new Harmony("quest3triggerui.audiopcmchunks." + typeof(AudioPcmChunks).Namespace);
            try
            {
                ValidateAudioIO();
                harmony.Patch(PointerTarget, postfix: new HarmonyMethod(typeof(AudioPcmChunks), "AfterPointer"));
                harmony.Patch(BytesTarget, postfix: new HarmonyMethod(typeof(AudioPcmChunks), "AfterBytes"));
                harmony.Patch(LoadTarget, postfix: new HarmonyMethod(typeof(AudioPcmChunks), "AfterLoad"));
                harmony.Patch(StreamingTarget, postfix: new HarmonyMethod(typeof(AudioPcmChunks), "AfterStreaming"));
                harmony.Patch(ResetTarget, prefix: new HarmonyMethod(typeof(AudioPcmChunks), "BeforeReset"));
                harmony.Patch(CleanupTarget, prefix: new HarmonyMethod(typeof(AudioPcmChunks), "BeforeCleanup"));
                accepting = true;
                var probe = new GameObject("Q3PcmHeartbeatSelftest");
                ((AudioPcmHeartbeatProbe)probe.AddComponent(typeof(AudioPcmHeartbeatProbe))).Begin();
                if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[audio-pcm-chunks] installed chunkFloats=" + ChunkFloats
                    + " wholePCMArray=False nativeClip=fullLength frameHeartbeat=True audioIO=PASS");
            }
            catch { harmony.UnpatchAll(harmony.Id); harmony = null; throw; }
        }
        internal static void Shutdown()
        {
            accepting = false;
            if (harmony == null) return;
            foreach (var target in new[] { PointerTarget, BytesTarget, LoadTarget, StreamingTarget }) harmony.Unpatch(target, HarmonyPatchType.Postfix, harmony.Id);
            // In-flight sessions keep only their own reset/cleanup hooks until
            // retirement. Hot shutdown never frees a live decoder's address.
            if (active == 0) { harmony.UnpatchAll(harmony.Id); harmony = null; }
        }
        internal static void HeartbeatResult(bool passed, bool disposed)
        {
            if (!passed) { Shutdown(); throw new InvalidOperationException("PCM native coroutine heartbeat selftest failed"); }
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo("[audio-pcm-chunks] native heartbeat selftest PASS stoppedCoroutine=True continuingCoroutine=True stoppedIteratorDisposed=" + disposed);
        }
        private static void ValidateAudioIO()
        {
            AudioClip clip = null;
            try
            {
                clip = AudioClip.Create("Q3PcmBitSelftest", 8, 2, 44100, false);
                var zero = new float[16]; clip.SetData(zero, 0);
                var middle = new[] { 0.125f, -0.25f, 0.5f, -0.75f };
                var tail = new[] { 0.875f, -0.875f };
                if (!clip.SetData(middle, 2) || !clip.SetData(tail, 7) || !clip.GetData(zero, 0)) throw new InvalidOperationException("PCM audio IO failed");
                for (int i = 0; i < zero.Length; i++)
                {
                    float expected = i >= 4 && i < 8 ? middle[i - 4] : i >= 14 ? tail[i - 14] : 0f;
                    if (BitConverter.ToInt32(BitConverter.GetBytes(zero[i]), 0) != BitConverter.ToInt32(BitConverter.GetBytes(expected), 0))
                        throw new InvalidOperationException("PCM frame-offset/bit selftest failed");
                }
            }
            finally { if (!ReferenceEquals(clip, null)) UnityEngine.Object.Destroy(clip); }
        }
    }

    // Uses only tiny scheduling state: no importer, sound playback, BASS input,
    // scene load or collection. The component/object retire after four frames.
    public sealed class AudioPcmHeartbeatProbe : MonoBehaviour
    {
        private int started, stoppedFrame, runningFrame;
        private bool disposed, done;
        internal void Begin()
        {
            started = Time.frameCount;
            StartCoroutine(Clock(false)); StopAllCoroutines(); StartCoroutine(Clock(true));
        }
        private IEnumerator Clock(bool running)
        {
            try
            {
                while (true)
                {
                    if (running) runningFrame = Time.frameCount; else stoppedFrame = Time.frameCount;
                    yield return null;
                }
            }
            finally { if (!running) disposed = true; }
        }
        private void Update()
        {
            int frame = Time.frameCount;
            if (done || unchecked(frame - started) < 4) return;
            done = true; StopAllCoroutines();
            bool passed = unchecked(frame - stoppedFrame) > 2 && unchecked(frame - runningFrame) <= 2;
            UnityEngine.Object.Destroy(gameObject);
            AudioPcmChunks.HeartbeatResult(passed, disposed);
        }
    }

    // Exists only while this importer has an active operation. It does not
    // scan a scene or keep a static collection of importers/clips/PCM buffers.
    public sealed class AudioPcmLifetime : MonoBehaviour
    {
        private BassImporter owner;
        private AudioPcmChunks.Session session;
        private bool finished;
        internal IEnumerator Routine;
        internal void Begin(BassImporter importer, AudioPcmChunks.Session operation)
        { owner = importer; session = operation; session.LastFrame = Time.frameCount; Routine = new Driver(this, AudioPcmChunks.Run(this, owner, session)); }
        private sealed class Driver : IEnumerator, IDisposable
        {
            private readonly AudioPcmLifetime lifetime;
            private IEnumerator body;
            internal Driver(AudioPcmLifetime lifetime, IEnumerator body) { this.lifetime = lifetime; this.body = body; }
            public object Current { get { return body == null ? null : body.Current; } }
            public void Reset() { throw new NotSupportedException(); }
            public bool MoveNext()
            {
                if (body == null || lifetime.finished) return false;
                lifetime.session.LastFrame = Time.frameCount;
                var executing = body;
                try { if (executing.MoveNext() && !lifetime.finished) return true; }
                catch { Dispose(); throw; }
                Dispose(); return false;
            }
            public void Dispose()
            {
                if (body == null) return;
                var disposable = body as IDisposable; body = null;
                try { if (disposable != null) disposable.Dispose(); }
                finally { lifetime.Finish(); }
            }
            internal void Forget() { body = null; }
        }
        private void Update()
        {
            if (finished) return;
            if (owner == null || !gameObject.activeInHierarchy || unchecked(Time.frameCount - session.LastFrame) > 2) Cancel();
        }
        private void OnDisable() { if (!gameObject.activeInHierarchy) Cancel(); }
        private void OnDestroy() { Cancel(); }
        internal void Cancel() { Finish(); }
        internal void Finish()
        {
            if (finished) return;
            finished = true;
            AudioPcmChunks.Detach(owner, session);
            session.Cancel();
            if (!session.Published && !ReferenceEquals(session.Clip, null)) UnityEngine.Object.Destroy(session.Clip);
            session.Clip = null; session.Ended = true;
            var driver = Routine as Driver;
            if (driver != null) driver.Forget();
            owner = null; Routine = null;
            AudioPcmChunks.End();
            UnityEngine.Object.Destroy(this);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace Quest3TriggerUI
{
    internal sealed class VrVideoRecorder : IDisposable
    {
        internal static VrVideoRecorder Current;
        // User-facing paths, bound to the [Recording] config section in the
        // plugin Awake.  A relative OutputDirectory resolves under the VaM
        // install folder; FfmpegPath may be a full path or a bare exe name
        // resolved through PATH.
        internal static string OutputDirectory = "VR录制";
        internal static string FfmpegPath = "ffmpeg.exe";
        internal static bool PauseOnStall = true;
        private const double StallGap = 0.5;
        private static string ResolvedDirectory()
        {
            return Path.IsPathRooted(OutputDirectory)
                ? OutputDirectory
                : Path.Combine(Directory.GetCurrentDirectory(), OutputDirectory);
        }
        internal static readonly float[] Scales = { .5f, .7f, .9f, 1f, 1.2f, 1.5f };
        internal static readonly int[] FrameRates = { 24, 30, 45, 60 };
        internal static readonly string[] Codecs = { "h264_nvenc", "hevc_nvenc", "av1_nvenc" };
        internal int ScaleIndex = 1, Bitrate = 30, CodecIndex, FrameRateIndex = 1;
        internal int Fps { get { return FrameRates[Math.Max(0, Math.Min(FrameRateIndex, FrameRates.Length - 1))]; } }
        internal bool Active { get; private set; }
        internal bool Busy { get { return Active || _pending || (_writer != null && _writer.IsAlive) || (_muxer != null && !_muxer.HasExited); } }
        internal string Status = "就绪 · 可选帧率 · 单眼视频（含音轨）";
        private RenderTexture _target;
        private AsyncGPUReadbackRequest _request;
        private bool _pending, _finishing;
        private int _width, _height, _frameIndex, _capturedFrame = -1;
        private double _nextCapture, _pendingSince;
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly Queue<byte[]> _free = new Queue<byte[]>();
        private readonly Queue<Frame> _queue = new Queue<Frame>();
        private readonly object _gate = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private Thread _writer;
        private volatile bool _finish;
        private volatile string _error, _finishedMessage;
        private int _endFrame;
        private Process _encoder, _muxer;
        private string _videoPath, _audioPath, _finalPath;
        private VrAudioTap _audioTap;
        private FileStream _audioStream;
        private long _audioBytes, _audioBytesAtFrame;
        private double _shift, _lastRaw = -1, _stallSkipped;
        private int _audioChannels = 2;
        private int _audioSampleRate = 48000;
        private float _nextFind;
        private bool _dlssFrame;
        internal int _tapFireCount, _manualFireCount;
        private struct Frame { internal byte[] Bytes; internal int Index; }

        internal static int Dimension(int eye, float scale) { return Math.Max(2, (int)Math.Round(eye * scale / 2f) * 2); }
        internal static string VideoArguments(int w, int h, int fps, int bitrate, string codec, string path)
        {
            return "-hide_banner -loglevel error -y -f rawvideo -pixel_format rgba -video_size " + w + "x" + h +
                " -framerate " + fps + " -i pipe:0 -an -c:v " + codec + " -preset p1 -b:v " + bitrate + "M -pix_fmt yuv420p \"" + path + "\"";
        }
        internal static string MuxArguments(string video, string wav, string output)
        {
            return "-hide_banner -loglevel error -y -i \"" + video + "\" -i \"" + wav + "\" -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 192k -shortest \"" + output + "\"";
        }
        private RenderTexture _scene;
        private Camera _captureCamera, _sourceCamera;
        private GameObject _captureGo;
        private Transform _headAnchor;
        private static System.Reflection.MethodInfo _replayDraws;
        private void InitManualCapture()
        {
            Camera eye = null;
            Transform head = null;
            foreach (Camera c in Camera.allCameras)
            {
                if (c == null) continue;
                int cbCount = 0;
                foreach (CameraEvent evt in Enum.GetValues(typeof(CameraEvent))) { try { cbCount += c.GetCommandBuffers(evt).Length; } catch { } }
                UnityEngine.Debug.Log("[VR Recording] cam " + c.name + " stereo=" + c.stereoEnabled + " enabled=" + c.enabled + " depth=" + c.depth + " cbs=" + cbCount + " mask=" + c.cullingMask);
                if (c.name == "Camera (eye)") eye = c;
                if (c.name == "CenterEye" || c.name == "CenterEyeAnchor") head = c.transform;
            }
            if (eye == null) { foreach (Camera c in Camera.allCameras) if (c != null && c.stereoEnabled && (eye == null || c.depth < eye.depth)) eye = c; }
            if (eye == null) throw new InvalidOperationException("找不到VR眼相机");
            _sourceCamera = eye;
            if (head == null) head = eye.transform;
            _headAnchor = head;
            _scene = new RenderTexture(_width, _height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            _scene.useMipMap = false;
            if (!_scene.Create()) throw new InvalidOperationException("录制场景纹理创建失败");
            _captureGo = new GameObject("VrRecCapture");
            _captureGo.hideFlags = HideFlags.HideAndDontSave;
            _captureCamera = _captureGo.AddComponent<Camera>();
            _captureCamera.CopyFrom(eye);
            _captureCamera.enabled = false;
            _captureCamera.stereoTargetEye = StereoTargetEyeMask.None;
            _captureCamera.RemoveAllCommandBuffers();
            _captureCamera.cullingMask = -1;
            _captureCamera.targetTexture = _scene;
            _captureCamera.aspect = (float)_width / _height;
            _captureCamera.ResetProjectionMatrix();
            _captureCamera.fieldOfView = EyeVerticalFov(eye);
            try { DiorBearDrawMeshHelper.RegisterExternalCamera(_captureCamera); } catch (Exception e) { UnityEngine.Debug.Log("[VR Recording] register external camera failed: " + e.Message); }
            try { if (_replayDraws == null) _replayDraws = typeof(DiorBearDrawMeshHelper).GetMethod("ReplayCacheToCamera", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic); }
            catch (Exception e) { UnityEngine.Debug.Log("[VR Recording] replay method lookup failed: " + e.Message); }
            Camera.onPostRender += OnSourcePostRender;
            UnityEngine.Debug.Log("[VR Recording] manual capture settings=" + eye.name + " head=" + head.name + " fov=" + _captureCamera.fieldOfView + " replay=" + (_replayDraws != null));
        }
        private static float EyeVerticalFov(Camera eye)
        {
            try
            {
                Matrix4x4 p = eye.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
                if (p.m11 > 0.001f) return Mathf.Clamp(2f * Mathf.Atan(1f / p.m11) * Mathf.Rad2Deg, 30f, 140f);
            }
            catch { }
            return 60f;
        }
        private void ReleaseManualCapture()
        {
            Camera.onPostRender -= OnSourcePostRender;
            _sourceCamera = null;
            if (_captureCamera != null) { try { DiorBearDrawMeshHelper.UnregisterExternalCamera(_captureCamera); } catch { } }
            _captureCamera = null;
            if (_captureGo != null) { UnityEngine.Object.Destroy(_captureGo); _captureGo = null; }
            if (_scene != null) { _scene.Release(); UnityEngine.Object.Destroy(_scene); _scene = null; }
            _headAnchor = null;
        }
        internal void Start()
        {
            if (Busy) return;
            if (!XRSettings.enabled || XRSettings.eyeTextureWidth < 2) throw new InvalidOperationException("请在VR模式启动录制");
            if (!SystemInfo.supportsAsyncGPUReadback) throw new InvalidOperationException("当前图形接口不支持异步GPU读回");
            _width = Dimension(XRSettings.eyeTextureWidth, Scales[ScaleIndex]);
            _height = Dimension(XRSettings.eyeTextureHeight, Scales[ScaleIndex]);
            if (_width > 8192 || _height > 8192) throw new InvalidOperationException("编码尺寸超过8192，请降低倍率");
            string directory = ResolvedDirectory();
            Directory.CreateDirectory(directory);
            string stem = Path.Combine(directory, "VaM_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            _finalPath = stem + ".mkv"; _videoPath = stem + ".video.mkv"; _audioPath = stem + ".audio.wav";
            _error = null; _finishedMessage = null; _finish = false; _finishing = false; _endFrame = 0;
            _capturedFrame = -1; _nextCapture = 0; _pending = false; _pendingSince = 0; _nextFind = 0;
            _shift = 0; _lastRaw = -1; _stallSkipped = 0; _audioBytesAtFrame = 0;
            try { foreach (string f in Directory.GetFiles(directory, "VaM_*.video.mkv")) File.Delete(f); foreach (string f in Directory.GetFiles(directory, "VaM_*.audio.wav")) File.Delete(f); } catch { }
            _target = new RenderTexture(_width, _height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            _target.useMipMap = false;
            if (!_target.Create()) throw new InvalidOperationException("录制缩放纹理创建失败");
            try { InitManualCapture(); } catch { ReleaseTarget(); ReleaseManualCapture(); throw; }
            try
            {
                lock (_gate) { _free.Clear(); _queue.Clear(); for (int i = 0; i < 4; i++) _free.Enqueue(new byte[checked(_width * _height * 4)]); }
                _encoder = new Process();
                _encoder.StartInfo = new ProcessStartInfo(FfmpegPath, VideoArguments(_width, _height, Fps, Bitrate, Codecs[CodecIndex], _videoPath));
                _encoder.StartInfo.UseShellExecute = false; _encoder.StartInfo.CreateNoWindow = true; _encoder.StartInfo.RedirectStandardInput = true; _encoder.StartInfo.RedirectStandardError = true;
                _encoder.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (!String.IsNullOrEmpty(e.Data)) _error = e.Data; };
                _encoder.Start(); _encoder.BeginErrorReadLine();
                _audioBytes = 0;
                _audioChannels = 2;
                _audioSampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
                _audioStream = new FileStream(_audioPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read); WriteWavHeader(_audioStream, 0, _audioChannels, _audioSampleRate);
                foreach (AudioListener listener in UnityEngine.Object.FindObjectsOfType<AudioListener>()) { _audioTap = listener.gameObject.AddComponent<VrAudioTap>(); break; }
                _clock.Reset(); _clock.Start(); Active = true; Current = this;
                _writer = new Thread(delegate() { WriteVideo(); }); _writer.IsBackground = true; _writer.Name = "VaM VR NVENC writer"; _writer.Start();
                Status = "录制中 " + _width + "×" + _height + " · " + Fps + " FPS · " + Bitrate + " Mbps";
            }
            catch { Active = false; RemoveAudioTap(); ReleaseManualCapture(); ReleaseTarget(); if (_encoder != null) { try { _encoder.Kill(); } catch { } _encoder.Dispose(); _encoder = null; } throw; }
        }
        internal void Stop()
        {
            if (!Active || _finishing) return;
            _endFrame = Math.Max(0, (int)((_clock.Elapsed.TotalSeconds - _shift) * Fps)); Active = false; _finishing = true; Status = "正在封装视频…";
            RemoveTap(); RemoveAudioTap(); ReleaseManualCapture(); _finish = true; _wake.Set();
            if (!_pending) ReleaseTarget();
        }
        internal void Tick()
        {
            if (_pending && (_request.done || Time.unscaledTime - _pendingSince > 5f))
            {
                bool timedOut = !_request.done; _pending = false;
                if (timedOut) UnityEngine.Debug.Log("[VR Recording] GPU读回超时，丢弃该帧（游戏卡顿中）");
                else if (_request.hasError) UnityEngine.Debug.Log("[VR Recording] GPU读回失败，丢弃该帧");
                else
                {
                    byte[] bytes = null; lock (_gate) { if (_free.Count > 0) bytes = _free.Dequeue(); }
                    if (bytes != null) { _request.GetData<byte>(0).CopyTo(bytes); lock (_gate) _queue.Enqueue(new Frame { Bytes = bytes, Index = _frameIndex }); _wake.Set(); }
                }
                if (!Active) ReleaseTarget();
            }
            if (Active && _error != null) { string e = _error; Stop(); Status = "录制失败：" + e; }
            if (_finishedMessage != null) { Status = _finishedMessage; _finishedMessage = null; }
            if (!Active) return;
            if (_encoder != null && _encoder.HasExited) { string we = _error ?? "编码进程退出"; Stop(); Status = "录制失败：" + we; return; }
            if (Time.unscaledTime >= _nextFind) { _nextFind = Time.unscaledTime + 2f; UnityEngine.Debug.Log("[VR Recording] eye frames=" + _manualFireCount); }
        }
        private void OnSourcePostRender(Camera cam)
        {
            if (cam != _sourceCamera || !Active || _finishing) return;
            CaptureManual();
        }
        // Wall-clock timestamp that also refreshes capture liveness. A raw gap
        // longer than StallGap is a main-thread stall — its whole span is cut
        // from the media timeline (video stays contiguous, audio rewinds to
        // the last frame boundary) so dead time never reaches the output.
        private double RawNow()
        {
            double raw = _clock.Elapsed.TotalSeconds;
            double gap = _lastRaw < 0 ? 0 : raw - _lastRaw;
            _lastRaw = raw;
            if (PauseOnStall && gap > StallGap) OnStall(gap);
            return raw;
        }
        private void OnStall(double gap)
        {
            _shift += gap; _stallSkipped += gap;
            FileStream s = _audioStream;
            if (s != null)
                lock (s)
                {
                    if (_audioStream == s && s.Length > _audioBytesAtFrame)
                    {
                        s.SetLength(_audioBytesAtFrame); s.Position = _audioBytesAtFrame;
                        _audioBytes = _audioBytesAtFrame;
                    }
                }
            UnityEngine.Debug.Log("[VR Recording] 卡顿跳过 " + gap.ToString("0.0") + "s（累计 " + _stallSkipped.ToString("0.0") + "s）");
            Status = "录制中 " + _width + "×" + _height + " · " + Fps + " FPS · " + Bitrate + " Mbps · 已跳过卡顿 " + _stallSkipped.ToString("0.0") + "s";
        }
        private void CaptureManual()
        {
            double raw = RawNow();
            if (_pending || _dlssFrame || _capturedFrame == Time.frameCount || _target == null || _scene == null || _captureCamera == null) return;
            double now = raw - _shift; if (now < _nextCapture) return;
            lock (_gate) { if (_free.Count == 0) return; }
            if (_headAnchor != null) { _captureCamera.transform.position = _headAnchor.position; _captureCamera.transform.rotation = _headAnchor.rotation; }
            try { if (_replayDraws != null) _replayDraws.Invoke(null, new object[] { _captureCamera }); } catch { }
            _captureCamera.Render();
            Graphics.Blit(_scene, _target, new Vector2(1f, -1f), new Vector2(0f, 1f));
            _request = AsyncGPUReadback.Request(_target, 0, TextureFormat.RGBA32);
            _frameIndex = Math.Max(0, (int)(now * Fps)); _audioBytesAtFrame = _audioBytes; _pending = true; _pendingSince = Time.unscaledTime;
            _capturedFrame = Time.frameCount; _nextCapture = now + 1.0 / Fps;
            _manualFireCount++;
        }
        internal void Capture(RenderTexture source, bool flip, bool packed, bool dlss)
        {
            _tapFireCount++;
            double raw = RawNow();
            if (!Active || _finishing || source == null || _target == null || _pending || _capturedFrame == Time.frameCount) return;
            double now = raw - _shift; if (now < _nextCapture) return;
            if (!dlss && _dlssFrame) return; lock (_gate) { if (_free.Count == 0) return; }
            Graphics.Blit(source, _target, new Vector2(packed ? .5f : 1f, flip ? -1f : 1f), new Vector2(0, flip ? 1f : 0));
            _request = AsyncGPUReadback.Request(_target, 0, TextureFormat.RGBA32); _frameIndex = Math.Max(0, (int)(now * Fps)); _audioBytesAtFrame = _audioBytes; _pending = true; _pendingSince = Time.unscaledTime; _capturedFrame = Time.frameCount; _nextCapture = now + 1.0 / Fps;
        }
        internal void SetDlss(bool active) { _dlssFrame = active; }
        private void FinalizeRecording()
        {
            try
            {
                while (!_finish) _wake.WaitOne(200);
                try { _encoder.StandardInput.Write('q'); _encoder.StandardInput.Flush(); } catch { }
                if (!_encoder.WaitForExit(25000)) { try { _encoder.Kill(); } catch { } throw new IOException("视频编码超时"); }
                if (_encoder.ExitCode != 0) throw new IOException(_error ?? "窗口采集失败");
                FinalizeAudio();
                UnityEngine.Debug.Log("[VR Recording] mux start video=" + _videoPath + " audio=" + _audioPath);
                _muxer = new Process(); _muxer.StartInfo = new ProcessStartInfo(FfmpegPath, MuxArguments(_videoPath, _audioPath, _finalPath)); _muxer.StartInfo.UseShellExecute = false; _muxer.StartInfo.CreateNoWindow = true; _muxer.StartInfo.RedirectStandardError = true; _muxer.Start();
                if (!_muxer.WaitForExit(20000)) { try { _muxer.Kill(); } catch { } throw new IOException("音视频封装超时"); }
                string muxError = _muxer.StandardError.ReadToEnd();
                if (_muxer.ExitCode != 0) throw new IOException(String.IsNullOrEmpty(muxError) ? "音视频封装失败" : muxError.Trim());
                if (!File.Exists(_finalPath) || new FileInfo(_finalPath).Length < 1024) throw new IOException("音视频封装未生成有效文件");
                UnityEngine.Debug.Log("[VR Recording] mux exit=" + _muxer.ExitCode + " output=" + _finalPath);
                try { File.Delete(_videoPath); File.Delete(_audioPath); } catch { }
                _finishedMessage = "已保存：" + _finalPath;
            }
            catch (Exception e) { _error = e.Message; _finishedMessage = "录制失败：" + e.Message; }
            finally { try { if (_encoder != null && !_encoder.HasExited) _encoder.Kill(); } catch { } if (_encoder != null) _encoder.Dispose(); }
        }
        private void WriteVideo()
        {
            byte[] last = null; int written = 0;
            try
            {
                Stream output = _encoder.StandardInput.BaseStream;
                while (true)
                {
                    Frame frame = new Frame(); bool found = false; lock (_gate) { if (_queue.Count > 0) { frame = _queue.Dequeue(); found = true; } }
                    if (!found) { if (_finish) break; _wake.WaitOne(100); continue; }
                    while (written < frame.Index) { byte[] repeat = last ?? frame.Bytes; output.Write(repeat, 0, repeat.Length); written++; }
                    output.Write(frame.Bytes, 0, frame.Bytes.Length); written++;
                    if (last != null) lock (_gate) _free.Enqueue(last); last = frame.Bytes;
                }
                while (last != null && written < _endFrame) { output.Write(last, 0, last.Length); written++; }
                output.Close(); if (!_encoder.WaitForExit(15000)) { try { _encoder.Kill(); } catch { } throw new IOException("视频编码超时"); }
                if (_encoder.ExitCode != 0 || written == 0) throw new IOException(_error ?? "没有收到VR眼图");
                FinalizeAudio();
                UnityEngine.Debug.Log("[VR Recording] mux start video=" + _videoPath + " audio=" + _audioPath);
                _muxer = new Process(); _muxer.StartInfo = new ProcessStartInfo(FfmpegPath, MuxArguments(_videoPath, _audioPath, _finalPath)); _muxer.StartInfo.UseShellExecute = false; _muxer.StartInfo.CreateNoWindow = true; _muxer.StartInfo.RedirectStandardError = true; _muxer.Start();
                if (!_muxer.WaitForExit(20000)) { try { _muxer.Kill(); } catch { } throw new IOException("音视频封装超时"); }
                string muxError = _muxer.StandardError.ReadToEnd();
                if (_muxer.ExitCode != 0) throw new IOException(String.IsNullOrEmpty(muxError) ? "音视频封装失败" : muxError.Trim());
                if (!File.Exists(_finalPath) || new FileInfo(_finalPath).Length < 1024) throw new IOException("音视频封装未生成有效文件");
                UnityEngine.Debug.Log("[VR Recording] mux exit=" + _muxer.ExitCode + " output=" + _finalPath);
                try { File.Delete(_videoPath); File.Delete(_audioPath); } catch { }
                _finishedMessage = "已保存：" + _finalPath;
            }
            catch (Exception e) { _error = e.Message; _finishedMessage = "录制失败：" + e.Message; }
            finally { try { if (_encoder != null && !_encoder.HasExited) _encoder.Kill(); } catch { } if (_encoder != null) _encoder.Dispose(); }
        }
        private void FinalizeAudio()
        {
            FileStream stream = _audioStream;
            if (stream == null) return;
            lock (stream)
            {
                if (_audioStream != stream) return;
                WriteWavHeader(stream, _audioBytes, _audioChannels, _audioSampleRate);
                stream.Flush();
                stream.Dispose();
                _audioStream = null;
            }
        }
        internal static void WriteWavHeader(Stream s, long bytes) { WriteWavHeader(s, bytes, 2, 48000); }
        internal static void WriteWavHeader(Stream s, long bytes, int channels, int sampleRate)
        {
            channels = Math.Max(1, Math.Min(8, channels));
            sampleRate = Math.Max(8000, sampleRate);
            int blockAlign = channels * 2;
            int byteRate = sampleRate * blockAlign;
            byte[] h = new byte[44];
            Array.Copy(System.Text.Encoding.ASCII.GetBytes("RIFF"), 0, h, 0, 4);
            BitConverter.GetBytes((int)Math.Min(int.MaxValue, 36L + bytes)).CopyTo(h, 4);
            Array.Copy(System.Text.Encoding.ASCII.GetBytes("WAVE"), 0, h, 8, 4);
            Array.Copy(System.Text.Encoding.ASCII.GetBytes("fmt "), 0, h, 12, 4);
            BitConverter.GetBytes(16).CopyTo(h, 16);
            BitConverter.GetBytes((short)1).CopyTo(h, 20);
            BitConverter.GetBytes((short)channels).CopyTo(h, 22);
            BitConverter.GetBytes(sampleRate).CopyTo(h, 24);
            BitConverter.GetBytes(byteRate).CopyTo(h, 28);
            BitConverter.GetBytes((short)blockAlign).CopyTo(h, 32);
            BitConverter.GetBytes((short)16).CopyTo(h, 34);
            Array.Copy(System.Text.Encoding.ASCII.GetBytes("data"), 0, h, 36, 4);
            BitConverter.GetBytes((int)Math.Min(int.MaxValue, bytes)).CopyTo(h, 40);
            s.Position = 0; s.Write(h, 0, h.Length); s.Position = 44;
        }
        private void RemoveTap() { }
        private void RemoveAudioTap() { if (_audioTap != null) UnityEngine.Object.Destroy(_audioTap); _audioTap = null; FinalizeAudio(); }
        private void ReleaseTarget() { if (_target != null) { _target.Release(); UnityEngine.Object.Destroy(_target); _target = null; } }
        public void Dispose() { Stop(); if (_pending) { _request.WaitForCompletion(); _pending = false; ReleaseTarget(); } if (Current == this) Current = null; ReleaseManualCapture(); RemoveAudioTap(); }
        internal void AddAudio(float[] data, int channels)
        {
            FileStream stream = _audioStream;
            if (stream == null || data == null || data.Length == 0) return;
            byte[] pcm = new byte[data.Length * 2];
            for (int i = 0; i < data.Length; i++) { float x = Math.Max(-1f, Math.Min(1f, data[i])); short v = (short)(x * 32767f); pcm[i * 2] = (byte)v; pcm[i * 2 + 1] = (byte)(v >> 8); }
            lock (stream)
            {
                if (_audioStream != stream) return;
                if (_audioBytes == 0 && channels > 0) _audioChannels = channels;
                stream.Write(pcm, 0, pcm.Length); _audioBytes += pcm.Length;
            }
        }
    }
    internal sealed class VrAudioTap : MonoBehaviour { void OnAudioFilterRead(float[] data, int channels) { VrVideoRecorder r = VrVideoRecorder.Current; if (r != null && r.Active) r.AddAudio(data, channels); } }
    internal sealed class VrVideoTap : MonoBehaviour { void OnRenderImage(RenderTexture source, RenderTexture destination) { try { VrVideoRecorder r = VrVideoRecorder.Current; if (r != null) r.Capture(source, true, source.width >= XRSettings.eyeTextureWidth * 2, false); } catch (Exception e) { UnityEngine.Debug.LogError("[VR Recording] " + e); if (VrVideoRecorder.Current != null) VrVideoRecorder.Current.Stop(); } finally { Graphics.Blit(source, destination); } } }
}

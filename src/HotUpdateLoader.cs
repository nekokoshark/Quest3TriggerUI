using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using UnityEngine;

namespace Quest3TriggerUI.HotLoader
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class HotUpdateLoaderPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "local.vam.quest3-trigger-ui.hot-loader";
        public const string PluginName = "Quest 3 Trigger UI Hot Loader";
        public const string PluginVersion = "1.0.0";
        private const string PayloadFileName = "Quest3TriggerUI.payload.dll.disabled";
        private const string RuntimeTypeName = "Quest3TriggerUI.Quest3TriggerUIPlugin";
        private const float PollSeconds = 0.50f;

        private string _payloadPath;
        private MonoBehaviour _runtime;
        private Type _runtimeType;
        private string _loadedHash;
        private long _observedLength = -1;
        private long _observedWriteTicks = -1;
        private int _stableObservations;
        private float _nextPoll;
        private float _nextSweep;
        private int _censusLogsLeft;
        private int _sweepsLeft;

        private void Awake()
        {
            string directory = Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location);
            _payloadPath = Path.Combine(directory, PayloadFileName);
            TryLoadPayload(true);
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextPoll)
                return;
            _nextPoll = Time.unscaledTime + PollSeconds;

            FileInfo file = new FileInfo(_payloadPath);
            if (!file.Exists)
                return;

            if (file.Length != _observedLength ||
                file.LastWriteTimeUtc.Ticks != _observedWriteTicks)
            {
                _observedLength = file.Length;
                _observedWriteTicks = file.LastWriteTimeUtc.Ticks;
                _stableObservations = 1;
                return;
            }

            _stableObservations++;
            if (_stableObservations >= 2)
                TryLoadPayload(false);

            // Extermination sweeps: run a dozen times after each payload load,
            // then stop — FindObjectsOfTypeAll is too heavy to run forever.
            // Any payload-generation runtime component that is not the tracked
            // one is a zombie left behind by a byte-loaded assembly sweep that
            // could not see it; one bad survivor drives input behind the live
            // build.
            if (_sweepsLeft > 0 && Time.unscaledTime >= _nextSweep)
            {
                _nextSweep = Time.unscaledTime + 2f;
                _sweepsLeft--;
                SweepStaleRuntimes();
            }
        }

        private void SweepStaleRuntimes()
        {
            try
            {
                MonoBehaviour[] all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
                int zombies = 0;
                var payloadTypes = new System.Collections.Generic.Dictionary<string, int>();
                for (int i = 0; i < all.Length; i++)
                {
                    MonoBehaviour b = all[i];
                    if (b == null) continue;
                    Type t = b.GetType();
                    string loc;
                    try { loc = t.Assembly.Location; } catch { loc = "?"; }
                    if (!string.IsNullOrEmpty(loc)) continue;
                    string fn = t.FullName ?? "?";
                    int n;
                    payloadTypes[fn] = payloadTypes.TryGetValue(fn, out n) ? n + 1 : 1;
                    if (fn == RuntimeTypeName && !ReferenceEquals(b, _runtime))
                    {
                        Logger.LogWarning("[Hot Loader] destroying stale runtime asm=" +
                            t.Assembly.GetHashCode() + " on '" +
                            (b.gameObject != null ? b.gameObject.name : "?") + "'");
                        UnityEngine.Object.DestroyImmediate(b);
                        zombies++;
                    }
                }
                if (payloadTypes.Count > 0 && (_censusLogsLeft > 0 || zombies > 0))
                {
                    if (_censusLogsLeft > 0) _censusLogsLeft--;
                    var sb = new System.Text.StringBuilder();
                    foreach (var kv in payloadTypes)
                        sb.Append(kv.Key).Append('x').Append(kv.Value).Append("; ");
                    Logger.LogInfo("[Hot Loader] payload-component census (killed " +
                        zombies + "): " + sb);
                }
            }
            catch (Exception e)
            {
                Logger.LogError("[Hot Loader] stale sweep failed: " + e);
            }
        }

        private void TryLoadPayload(bool initialLoad)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(_payloadPath);
                string hash = ComputeHash(bytes);
                if (String.Equals(hash, _loadedHash, StringComparison.Ordinal))
                    return;

                Assembly assembly = Assembly.Load(bytes);
                Type nextType = assembly.GetType(RuntimeTypeName, true);
                if (!typeof(MonoBehaviour).IsAssignableFrom(nextType))
                    throw new InvalidOperationException(RuntimeTypeName + " is not a MonoBehaviour.");

                Type previousType = _runtimeType;
                if (_runtime != null)
                {
                    UnityEngine.Object.DestroyImmediate(_runtime);
                    _runtime = null;
                }

                try
                {
                    _runtime = gameObject.AddComponent(nextType) as MonoBehaviour;
                    if (_runtime == null)
                        throw new InvalidOperationException("Payload component creation returned null.");
                    // Unity binds MonoBehaviour script classes by full name —
                    // the first byte-loaded assembly wins and later payloads
                    // instantiate the stale class while logging a successful
                    // reload. Detect it instead of silently running old code.
                    if (_runtime.GetType() != nextType)
                        Logger.LogError("[Hot Loader] STALE BINDING: payload SHA256=" +
                            hash + " loaded, but the component resolved to the " +
                            "earlier assembly " + _runtime.GetType().Assembly.FullName +
                            " — restart VaM to run this payload.");
                    PropertyInfo readyProperty = nextType.GetProperty(
                        "RuntimeReady", BindingFlags.Public | BindingFlags.Static);
                    if (readyProperty != null &&
                        !Convert.ToBoolean(readyProperty.GetValue(null, null)))
                        throw new InvalidOperationException("Payload Awake did not complete.");
                }
                catch
                {
                    if (_runtime != null)
                    {
                        UnityEngine.Object.DestroyImmediate(_runtime);
                        _runtime = null;
                    }
                    if (previousType != null)
                        _runtime = gameObject.AddComponent(previousType) as MonoBehaviour;
                    throw;
                }

                _runtimeType = _runtime.GetType();
                _loadedHash = hash;
                _stableObservations = 0;
                _censusLogsLeft = 6;
                _sweepsLeft = 12;
                SweepStaleRuntimes();
                FileInfo file = new FileInfo(_payloadPath);
                _observedLength = file.Length;
                _observedWriteTicks = file.LastWriteTimeUtc.Ticks;
                Logger.LogInfo((initialLoad ? "Loaded" : "Hot-reloaded") +
                    " Quest3TriggerUI payload SHA256=" + hash + ".");
            }
            catch (Exception exception)
            {
                Logger.LogError("Quest3TriggerUI payload update failed; previous runtime restored: " + exception);
            }
        }

        private static string ComputeHash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(bytes);
                return BitConverter.ToString(digest).Replace("-", String.Empty);
            }
        }

        private void OnDestroy()
        {
            if (_runtime != null)
                UnityEngine.Object.DestroyImmediate(_runtime);
            _runtime = null;
            _runtimeType = null;
        }
    }
}

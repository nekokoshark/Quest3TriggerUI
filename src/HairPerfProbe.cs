using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using GPUTools.Hair.Scripts.Runtime.Physics;
using GPUTools.Hair.Scripts;

namespace Quest3TriggerUI
{
    /// <summary>
    /// Read-only sampling probe for the hair / cloth collision phases.
    ///
    /// It never touches the physics path: it only reads counters VaM already
    /// maintains (public static on GPUCollidersManager and
    /// SpatialHashCollisionManager) plus a few private constants, capacities
    /// and per-sim counters through reflection, and prints per-second deltas
    /// into the BepInEx log. Frame time is sampled in the same pass so a later
    /// change can be compared against the same run shape.
    ///
    /// Off by default: with no trigger file the probe costs one File.Exists
    /// every two seconds. Drop "hair_perf_probe.txt" into the plugin folder
    /// with a number of seconds as its content to sample; rewriting the file
    /// starts a new run.
    /// </summary>
    internal static class HairPerfProbe
    {
        private const string TriggerFileName = "hair_perf_probe.txt";
        private const float CheckInterval = 2f;
        private const float SampleInterval = 1f;
        private const int DefaultSeconds = 90;
        private const int SimRefreshSamples = 30;
        private const BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static string _triggerPath;
        private static bool _armed;
        private static int _armedHealthy;
        private static int _armedSeconds;
        private static float _nextCheck;
        private static float _nextSample;
        private static float _startedAt;
        private static float _stopAt;
        private static bool _running;
        private static bool _abMode;
        private static GPHairPhysics[] _gphairComps;
        private static HairSettings[] _hsComps;
        private static bool[] _origEnabled;
        private static bool[] _hsOrigEnabled;
        private static int _phaseIndex;
        private static string _phaseLabel = "";

        private static int _sampled;
        private static int _frames;
        private static float _frameMsSum;
        private static float _frameMsMax;

        // HairPhysicsWorld is a plain managed object (PrimitiveBase), owned by
        // a GPHairPhysics component; keep the handles as object.
        private static object[] _simWorlds = new object[0];
        private static FieldInfo _simFrameField;
        private static int _simIterations = -1;
        private static int _simOuterIterations = -1;
        private static float _simWeight = -1f;

        private static int _fCountBase;
        private static int _sDispBase, _lDispBase, _sCntBase, _lCntBase, _sCollBase, _lCollBase;
        private static float _fMsBase, _ccMsBase, _grabMsBase, _editMsBase;
        private static float _sHashBase, _lHashBase, _sPullBase, _lPullBase;
        private static float _sBuildBase, _lBuildBase, _sBindBase, _lBindBase, _hbTimeBase;
        private static int _hbCountBase, _buildSCallBase, _readyCheckBase, _simFrameBase;

        internal static void Tick()
        {
            try
            {
                float now = Time.unscaledTime;
                if (_running)
                {
                    CountFrame();
                    if (now >= _nextSample)
                    {
                        _nextSample = now + SampleInterval;
                        Sample(now);
                    }
                    if (now >= _stopAt)
                    {
                        _running = false;
                        FrameBudgetProbe.End();
                        RestoreSims();
                        Emit("[HairProbe] 采样结束（" + _sampled + " 个样本），头发模拟已恢复原状。" +
                             "改写或删除 " + TriggerFileName + " 可再次采样；探针本体保持只读待机。");
                    }
                    return;
                }

                if (_armed)
                {
                    CountFrame();
                    if (now >= _nextSample)
                    {
                        _nextSample = now + SampleInterval;
                        bool healthy = _frames >= 18 && _frameMsSum > 0f;
                        _armedHealthy = healthy ? _armedHealthy + 1 : 0;
                        _frames = 0;
                        _frameMsSum = 0f;
                        _frameMsMax = 0f;
                        if (_armedHealthy >= 3)
                        {
                            _armed = false;
                            StartRun(now, _armedSeconds);
                        }
                    }
                    return;
                }

                if (now < _nextCheck) return;
                _nextCheck = now + CheckInterval;
                TryBegin(now);
            }
            catch (Exception e)
            {
                _running = false;
                FrameBudgetProbe.End();
                RestoreSims();
                Emit("[HairProbe] 采样异常，已停用本次运行: " + e.GetType().Name + " " + e.Message);
            }
        }

        private static void TryBegin(float now)
        {
            if (_triggerPath == null)
            {
                string install = Path.GetDirectoryName(Application.dataPath);
                _triggerPath = Path.Combine(
                    Path.Combine(Path.Combine(install, "BepInEx"), "plugins"),
                    Path.Combine("Quest3TriggerUI", TriggerFileName));
            }
            if (!File.Exists(_triggerPath)) return;

            string content = File.ReadAllText(_triggerPath).Trim();
            // 触发文件读完即删：热载会重置静态状态，靠时间戳会被旧戳位卡住。
            try { File.Delete(_triggerPath); } catch { }

            int seconds;
            _abMode = content.StartsWith("ab", StringComparison.OrdinalIgnoreCase);
            if (_abMode)
            {
                // A/B 模式：阶段1 头发开 20s -> 阶段2 头发停 20s -> 阶段3 头发开 20s。
                // 无头显/未呈现时帧率极低，此时不开始，等画面恢复到 >=18fps 自动跑。
                _armed = true;
                _armedHealthy = 0;
                _armedSeconds = 60;
                _frames = 0;
                _frameMsSum = 0f;
                _frameMsMax = 0f;
                _nextSample = now + SampleInterval;
                Emit("[HairProbe] A/B 已就绪：画面稳定在 >=18fps 持续 3 秒后自动开始（约 60 秒）。");
                return;
            }
            if (!int.TryParse(content, out seconds) || seconds < 5 || seconds > 900)
                seconds = DefaultSeconds;

            StartRun(now, seconds);
        }

        private static void StartRun(float now, int seconds)
        {
            _origEnabled = null;
            _hsOrigEnabled = null;
            _phaseIndex = 0;
            _phaseLabel = "";
            RefreshSims();
            if (_abMode) ApplyPhase(0);
            _startedAt = now;
            _stopAt = now + seconds;
            _nextSample = now + 0.2f;
            _sampled = 0;
            _frames = 0;
            _frameMsSum = 0f;
            _frameMsMax = 0f;
            _running = true;
            FrameBudgetProbe.BeginRun();

            LogHeader(seconds);
            Rebase();
        }

        private static void CountFrame()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            if (ms <= 0f || ms > 1000f) return;
            _frames++;
            _frameMsSum += ms;
            if (ms > _frameMsMax) _frameMsMax = ms;
        }

        private static void Rebase()
        {
            _fCountBase = GPUCollidersManager.perfCount_FixedUpdate;
            _fMsBase = GPUCollidersManager.perfAccum_FixedUpdate;
            _ccMsBase = GPUCollidersManager.perfAccum_ComputeColliders;
            _grabMsBase = GPUCollidersManager.perfAccum_ComputeGrabSpheres;
            _editMsBase = GPUCollidersManager.perfAccum_ComputeEditCapsules;
            _sHashBase = GPUCollidersManager.perfAccum_sphereHashBuild;
            _lHashBase = GPUCollidersManager.perfAccum_lineHashBuild;
            _sPullBase = GPUCollidersManager.perfAccum_spherePullData;
            _lPullBase = GPUCollidersManager.perfAccum_linePullData;
            _sDispBase = GPUCollidersManager.perfAccum_sphereDispatchCount;
            _lDispBase = GPUCollidersManager.perfAccum_lineSphereDispatchCount;
            _sCntBase = GPUCollidersManager.perfAccum_sphereCount;
            _lCntBase = GPUCollidersManager.perfAccum_lineSphereCount;
            _sBuildBase = SpatialHashCollisionManager.perfAccum_sphereBuild;
            _lBuildBase = SpatialHashCollisionManager.perfAccum_lsBuild;
            _sBindBase = SpatialHashCollisionManager.perfAccum_sphereBind;
            _lBindBase = SpatialHashCollisionManager.perfAccum_lsBind;
            _hbTimeBase = SpatialHashCollisionManager.perfAccum_HashBuildTime;
            _hbCountBase = SpatialHashCollisionManager.perfAccum_HashBuildCount;
            _sCollBase = SpatialHashCollisionManager.perfCount_sphereColliders;
            _lCollBase = SpatialHashCollisionManager.perfCount_lsColliders;
            _buildSCallBase = SpatialHashCollisionManager.diagBuildSCallCount;
            _readyCheckBase = SpatialHashCollisionManager.diagIsReadyCheckCount;
            _simFrameBase = SumSimFrames();
        }

        private static void Sample(float now)
        {
            _sampled++;

            int fixedCount = GPUCollidersManager.perfCount_FixedUpdate - _fCountBase;
            float frameAvg = _frames > 0 ? _frameMsSum / _frames : 0f;
            float fps = frameAvg > 0.0001f ? 1000f / frameAvg : 0f;

            var sb = new StringBuilder(400);
            sb.Append("[HairProbe] t=+").Append((now - _startedAt).ToString("0")).Append("s");
            if (_abMode) sb.Append(" [").Append(_phaseLabel).Append("]");
            sb.Append(" | 帧=").Append(fps.ToString("0.0")).Append("fps ")
              .Append(frameAvg.ToString("0.00")).Append("ms(峰值").Append(_frameMsMax.ToString("0.0")).Append("ms)");
            sb.Append(" | fixed=").Append(fixedCount);
            sb.Append(" | GPU碰撞: FixedUpdate=").Append((GPUCollidersManager.perfAccum_FixedUpdate - _fMsBase).ToString("0.00"))
              .Append("ms ComputeColliders=").Append((GPUCollidersManager.perfAccum_ComputeColliders - _ccMsBase).ToString("0.00"))
              .Append("ms 抓取=").Append((GPUCollidersManager.perfAccum_ComputeGrabSpheres - _grabMsBase).ToString("0.00"))
              .Append("ms 编辑胶囊=").Append((GPUCollidersManager.perfAccum_ComputeEditCapsules - _editMsBase).ToString("0.00")).Append("ms");
            sb.Append(" | 哈希: 球=").Append((GPUCollidersManager.perfAccum_sphereHashBuild - _sHashBase).ToString("0.00"))
              .Append("ms 线球=").Append((GPUCollidersManager.perfAccum_lineHashBuild - _lHashBase).ToString("0.00"))
              .Append("ms 球拉取=").Append((GPUCollidersManager.perfAccum_spherePullData - _sPullBase).ToString("0.00"))
              .Append("ms 线球拉取=").Append((GPUCollidersManager.perfAccum_linePullData - _lPullBase).ToString("0.00")).Append("ms");
            sb.Append(" | dispatch: 球=").Append(GPUCollidersManager.perfAccum_sphereDispatchCount - _sDispBase)
              .Append(" 线球=").Append(GPUCollidersManager.perfAccum_lineSphereDispatchCount - _lDispBase)
              .Append(" 碰撞体=球").Append(GPUCollidersManager.perfAccum_sphereCount - _sCntBase)
              .Append("/线球").Append(GPUCollidersManager.perfAccum_lineSphereCount - _lCntBase);

            int hbCount = SpatialHashCollisionManager.perfAccum_HashBuildCount - _hbCountBase;
            float hbTime = SpatialHashCollisionManager.perfAccum_HashBuildTime - _hbTimeBase;
            sb.Append(" | 内部: 球构建=").Append((SpatialHashCollisionManager.perfAccum_sphereBuild - _sBuildBase).ToString("0.00"))
              .Append("ms 线球构建=").Append((SpatialHashCollisionManager.perfAccum_lsBuild - _lBuildBase).ToString("0.00"))
              .Append("ms 绑定=").Append((SpatialHashCollisionManager.perfAccum_sphereBind - _sBindBase).ToString("0.00"))
              .Append("ms/线球").Append((SpatialHashCollisionManager.perfAccum_lsBind - _lBindBase).ToString("0.00"))
              .Append("ms 合计=").Append(hbTime.ToString("0.00")).Append("ms/").Append(hbCount).Append("次");
            if (hbCount > 0)
                sb.Append(" 单次=").Append((hbTime / hbCount).ToString("0.000")).Append("ms");

            sb.Append(" | 诊断: BuildS调用=").Append(SpatialHashCollisionManager.diagBuildSCallCount - _buildSCallBase)
              .Append(" 就绪检查=").Append(SpatialHashCollisionManager.diagIsReadyCheckCount - _readyCheckBase)
              .Append(" 就绪=").Append(SpatialHashCollisionManager.diagLastIsReady);

            int simFrames = SumSimFrames() - _simFrameBase;
            sb.Append(" | 头发: 实例=").Append(_simWorlds.Length)
              .Append(" 求解步=").Append(simFrames)
              .Append(" 迭代=").Append(_simIterations).Append('/').Append(_simOuterIterations)
              .Append(" 权重=").Append(_simWeight.ToString("0.00"));

            sb.Append(FrameBudgetProbe.SampleLine(_frames));
            Emit(sb.ToString());
            _frames = 0;
            _frameMsSum = 0f;
            _frameMsMax = 0f;
            Rebase();

            if (_abMode)
            {
                int elapsed = (int)(now - _startedAt);
                int want = elapsed < 20 ? 0 : (elapsed < 40 ? 1 : 2);
                if (want != _phaseIndex) ApplyPhase(want);
            }

            if (_sampled % SimRefreshSamples == 0)
            {
                RefreshSims();
                _simFrameBase = SumSimFrames();
            }
        }

        private static void LogHeader(int seconds)
        {
            var sb = new StringBuilder(400);
            sb.Append("[HairProbe] 开始采样 ").Append(seconds)
              .Append(" 秒；帧率上限=").Append(Application.targetFrameRate)
              .Append(" vSync=").Append(QualitySettings.vSyncCount)
              .Append("；常量: 哈希表=")
              .Append(StaticValue("SpatialHashCollisionManager", "HASH_TABLE_SIZE"))
              .Append(" cell=").Append(StaticValue("SpatialHashCollisionManager", "CELL_SIZE"))
              .Append(" 每cell上限=").Append(StaticValue("SpatialHashCollisionManager", "MAX_COLLIDERS_PER_CELL"))
              .Append(" 球索引初值=").Append(StaticValue("SpatialHashCollisionManager", "SPHERE_INDICES_INIT_SIZE"))
              .Append(" 线球索引初值=").Append(StaticValue("SpatialHashCollisionManager", "LS_INDICES_INIT_SIZE"));
            Emit(sb.ToString());

            sb = new StringBuilder(400);
            sb.Append("[HairProbe] 运行时状态: enableSpatialHash=").Append(GPUCollidersManager.enableSpatialHash)
              .Append(" disableComputeColliders=").Append(GPUCollidersManager.disableComputeColliders)
              .Append(" 消费者=").Append(GPUCollidersManager.perfAccum_consumerCount)
              .Append(" 初始化状态=").Append(SpatialHashCollisionManager.diagInitStatus)
              .Append(" 替换内核=").Append(SpatialHashCollisionManager.diagReplacedKernelCount)
              .Append(" 哈希就绪=").Append(SpatialHashCollisionManager.diagLastIsReady)
              .Append(" BuildS调用=").Append(SpatialHashCollisionManager.diagBuildSCallCount)
              .Append(" BuildLS调用=").Append(SpatialHashCollisionManager.diagBuildLSCallCount)
              .Append(" BuildLS跳过=").Append(SpatialHashCollisionManager.diagBuildLSSkipCount);
            Emit(sb.ToString());

            Emit("[HairProbe] 索引缓冲: " + CapacityLine());
        }

        private static string CapacityLine()
        {
            object inst = ManagerInstance();
            if (inst == null) return "SpatialHashCollisionManager.Instance=null";
            Type t = inst.GetType();
            var sb = new StringBuilder(200);
            sb.Append("球容量=").Append(InstanceField(inst, t, "_sphereIndicesCapacity"));
            sb.Append(" 线球容量=").Append(InstanceField(inst, t, "_lsIndicesCapacity"));
            sb.Append(" 球索引缓冲=").Append(BufferCount(inst, t, "_sphereCellIndicesBuffer"));
            sb.Append(" 线球索引缓冲=").Append(BufferCount(inst, t, "_lsCellIndicesBuffer"));
            sb.Append(" 球格点缓冲=").Append(BufferCount(inst, t, "_sphereHashGridBuffer"));
            sb.Append(" 线球格点缓冲=").Append(BufferCount(inst, t, "_lsHashGridBuffer"));
            return sb.ToString();
        }

        private static object ManagerInstance()
        {
            try
            {
                PropertyInfo p = typeof(SpatialHashCollisionManager).GetProperty("Instance", AnyStatic);
                return p != null ? p.GetValue(null, null) : null;
            }
            catch { return null; }
        }

        private static string InstanceField(object inst, Type t, string name)
        {
            try
            {
                FieldInfo f = t.GetField(name, AnyInstance);
                if (f == null) return "?";
                object v = f.GetValue(inst);
                return v == null ? "null" : v.ToString();
            }
            catch { return "?"; }
        }

        private static string BufferCount(object inst, Type t, string name)
        {
            try
            {
                FieldInfo f = t.GetField(name, AnyInstance);
                if (f == null) return "?";
                var buf = f.GetValue(inst) as ComputeBuffer;
                return buf == null ? "null" : buf.count.ToString();
            }
            catch { return "?"; }
        }

        private static string StaticValue(string typeName, string fieldName)
        {
            try
            {
                Type t = typeof(SpatialHashCollisionManager).Assembly.GetType(typeName, false);
                if (t == null) return "?";
                FieldInfo f = t.GetField(fieldName, AnyStatic);
                if (f == null) return "?";
                object v = f.GetValue(null);
                return v == null ? "null" : v.ToString();
            }
            catch { return "?"; }
        }

        private static void RefreshSims()
        {
            try
            {
                var found = new List<object>(4);
                var compsTyped = new List<GPHairPhysics>(4);
                UnityEngine.Object[] comps = Resources.FindObjectsOfTypeAll(typeof(GPHairPhysics));
                for (int i = 0; i < comps.Length; i++)
                {
                    var comp = comps[i] as GPHairPhysics;
                    if (comp == null) continue;
                    compsTyped.Add(comp);
                    FieldInfo wf = comp.GetType().GetField("world", AnyInstance);
                    if (wf == null) continue;
                    object world = wf.GetValue(comp);
                    if (world != null) found.Add(world);
                }
                _simWorlds = found.ToArray();
                _gphairComps = compsTyped.ToArray();
                var hsList = new List<HairSettings>(4);
                UnityEngine.Object[] hsAll = Resources.FindObjectsOfTypeAll(typeof(HairSettings));
                for (int j = 0; j < hsAll.Length; j++)
                {
                    var hs = hsAll[j] as HairSettings;
                    if (hs != null) hsList.Add(hs);
                }
                _hsComps = hsList.ToArray();
                if (_origEnabled == null && _gphairComps.Length > 0)
                {
                    _origEnabled = new bool[_gphairComps.Length];
                    for (int k = 0; k < _gphairComps.Length; k++)
                        _origEnabled[k] = _gphairComps[k] != null && _gphairComps[k].enabled;
                }
                if (_hsOrigEnabled == null && _hsComps.Length > 0)
                {
                    _hsOrigEnabled = new bool[_hsComps.Length];
                    for (int k = 0; k < _hsComps.Length; k++)
                        _hsOrigEnabled[k] = _hsComps[k] != null && _hsComps[k].enabled;
                }
                if (_simWorlds.Length > 0)
                {
                    Type wt = _simWorlds[0].GetType();
                    if (_simFrameField == null) _simFrameField = wt.GetField("frame", AnyInstance);
                    _simIterations = IntField(wt, _simWorlds[0], "iterations", _simIterations);
                    _simOuterIterations = IntField(wt, _simWorlds[0], "outerIterations", _simOuterIterations);
                    object wv = null;
                    PropertyInfo wp = wt.GetProperty("Weight", AnyInstance);
                    if (wp != null) wv = wp.GetValue(_simWorlds[0], null);
                    if (wv == null)
                    {
                        FieldInfo wf2 = wt.GetField("<Weight>k__BackingField", AnyInstance);
                        if (wf2 != null) wv = wf2.GetValue(_simWorlds[0]);
                    }
                    if (wv != null)
                    {
                        object inner = null;
                        PropertyInfo vp = wv.GetType().GetProperty("Value", AnyInstance);
                        if (vp != null) inner = vp.GetValue(wv, null);
                        if (inner == null)
                        {
                            FieldInfo vf = wv.GetType().GetField("Value", AnyInstance);
                            if (vf != null) inner = vf.GetValue(wv);
                        }
                        if (inner != null) _simWeight = Convert.ToSingle(inner);
                    }
                }
            }
            catch { }
        }

        private static void ApplyPhase(int index)
        {
            _phaseIndex = index;
            _phaseLabel = index == 0 ? "阶段1 头发开" : (index == 1 ? "阶段2 头发停" : "阶段3 头发开");
            bool enable = index != 1;
            int touched = 0;
            GPHairPhysics[] comps = _gphairComps;
            if (comps != null)
            {
                for (int i = 0; i < comps.Length; i++)
                {
                    GPHairPhysics c = comps[i];
                    if (c == null) continue;
                    if (c.enabled != enable) { c.enabled = enable; touched++; }
                }
            }
            HairSettings[] hs = _hsComps;
            if (hs != null)
            {
                for (int i = 0; i < hs.Length; i++)
                {
                    HairSettings s = hs[i];
                    if (s == null) continue;
                    if (s.enabled != enable) { s.enabled = enable; touched++; }
                }
            }
            Emit("[HairProbe] " + _phaseLabel + "（切换 " + touched + " 个组件：GPHairPhysics=" +
                 (comps != null ? comps.Length : 0) + " HairSettings=" + (hs != null ? hs.Length : 0) + "）");
        }

        private static void RestoreSims()
        {
            int restored = 0;
            GPHairPhysics[] comps = _gphairComps;
            bool[] orig = _origEnabled;
            if (comps != null && orig != null)
            {
                for (int i = 0; i < comps.Length && i < orig.Length; i++)
                {
                    GPHairPhysics c = comps[i];
                    if (c == null) continue;
                    if (c.enabled != orig[i]) { c.enabled = orig[i]; restored++; }
                }
            }
            HairSettings[] hs = _hsComps;
            bool[] hsOrig = _hsOrigEnabled;
            if (hs != null && hsOrig != null)
            {
                for (int i = 0; i < hs.Length && i < hsOrig.Length; i++)
                {
                    HairSettings s = hs[i];
                    if (s == null) continue;
                    if (s.enabled != hsOrig[i]) { s.enabled = hsOrig[i]; restored++; }
                }
            }
            if (restored > 0) Emit("[HairProbe] 已恢复 " + restored + " 个头发模拟组件的原启用状态。");
        }

        private static int SumSimFrames()
        {
            object[] worlds = _simWorlds;
            if (worlds == null || worlds.Length == 0 || _simFrameField == null) return 0;
            int total = 0;
            for (int i = 0; i < worlds.Length; i++)
            {
                if (worlds[i] == null) continue;
                try { total += Convert.ToInt32(_simFrameField.GetValue(worlds[i])); }
                catch { }
            }
            return total;
        }

        private static int IntField(Type t, object inst, string name, int fallback)
        {
            try
            {
                FieldInfo f = t.GetField(name, AnyInstance);
                if (f == null) return fallback;
                return Convert.ToInt32(f.GetValue(inst));
            }
            catch { return fallback; }
        }

        private static float FloatField(Type t, object inst, string name, float fallback)
        {
            try
            {
                FieldInfo f = t.GetField(name, AnyInstance);
                if (f == null) return fallback;
                object v = f.GetValue(inst);
                return v == null ? fallback : Convert.ToSingle(v);
            }
            catch { return fallback; }
        }

        private static void Emit(string line)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo(line);
            else Debug.Log(line);
        }
    }
}
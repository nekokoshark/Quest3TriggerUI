using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    // 「运镜」— bound camera shots. A small translucent panel on the user's
    // right offers 记录/上一镜/下一镜/隐藏 plus a horizontal thumbnail strip
    // of every shot recorded in this scene. Record mode captures two data
    // points with live status: ① a body point picked with the right-hand ray
    // (stored in the bone's local space, so the anchor follows the actor),
    // ② the user's head pose relative to that point. Entering a shot is a
    // one-shot teleport of the navigation rig — the view is then released.
    internal static class VrShotCameras
    {
        private const float PanelScale = 0.00055f;
        private const float ThumbW = 168f;
        private const float ThumbH = 94f;

        private sealed class Shot
        {
            internal string AtomUid, BoneName, ThumbFile;
            internal Vector3 PointLocal, CamLocalPos;
            internal Quaternion CamLocalRot;
            internal Texture2D ThumbTex;
        }

        private static readonly List<Shot> _shots = new List<Shot>();
        private static int _current = -1;   // 镜头0 = not yet entered
        private static int _tickFrame = -1;
        private static Shot _pendingEnter, _pendingThumb;
        private static string _pendingThumbPath;
        private static float _thumbDeadline;
        private static bool _panelVisible;
        private static bool _pinned;
        private static bool _dragging;
        private static Vector3 _dragHand;
        private static bool _recordMode;
        private static bool _loaded;
        private static string _sceneKey = "";

        private static GameObject _canvasGo;
        private static Canvas _canvas;
        private static GameObject _expanderGo;
        private static Canvas _expanderCanvas;
        private static Font _font;
        private static Text _title, _status, _shotInfo;
        private static RectTransform _mainArea, _recordArea, _thumbRow;
        private static Text _recLine1, _recLine2;
        private static Button _commitBtn, _pinBtn;

        private static string _recAtomUid, _recBoneName;
        private static Transform _recBone;
        private static Vector3 _recPointLocal;
        private static bool _recPointSet;
        private static GameObject _marker;
        private static MeshRenderer _markerRend;
        private static LineRenderer _rayLine;
        private static readonly Color MarkIdle = new Color(1f, .85f, .2f, .8f);
        private static readonly Color MarkSet = new Color(.2f, .95f, .5f, .9f);
        private static readonly Color RayIdle = new Color(.3f, .8f, 1f, .7f);
        private static readonly Color RayHit = new Color(.3f, 1f, .5f, .9f);
        private const float RayLength = 30f;

        internal static bool PointerInside
        {
            get
            {
                if (_canvasGo == null && _expanderGo == null) return false;
                GameObject r = VrPointerPresentation.CurrentLookTarget(true);
                GameObject l = VrPointerPresentation.CurrentLookTarget(false);
                return Inside(r) || Inside(l);
            }
        }
        private static bool Inside(GameObject go)
        {
            if (go == null) return false;
            return (_canvasGo != null && go.transform.IsChildOf(_canvasGo.transform)) ||
                   (_expanderGo != null && go.transform.IsChildOf(_expanderGo.transform));
        }

        internal static bool PanelVisible { get { return _panelVisible; } }

        internal static void TogglePanel()
        {
            // Radial action toggles the whole feature: reopen when hidden,
            // fully exit when visible (no expander left behind).
            if (_panelVisible) HidePanel(false);
            else ShowPanel();
        }
        internal static void ToggleRecord()
        {
            if (!_panelVisible) ShowPanel();
            if (_recordMode) ExitRecord(false);
            else EnterRecord();
        }
        internal static void Next() { Step(+1); }
        internal static void Prev() { Step(-1); }

        private static void Step(int dir)
        {
            // Shortcut-driven switching must not resurrect a hidden panel.
            if (_shots.Count == 0) { SetStatus("还没有镜头——先点「记录」"); return; }
            _current = (_current + dir) % _shots.Count;
            if (_current < 0) _current += _shots.Count;
            Enter(_current);
        }

        // ---------------- record mode ----------------

        private static void EnterRecord()
        {
            _recordMode = true;
            _recPointSet = false; _recBone = null; _recAtomUid = _recBoneName = null;
            EnsureMarker();
            if (_mainArea != null) _mainArea.gameObject.SetActive(false);
            if (_recordArea != null) _recordArea.gameObject.SetActive(true);
            SetRecText();
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("Q3 shots: record mode ON");
        }

        private static void ExitRecord(bool saved)
        {
            _recordMode = false;
            _recPointSet = false; _recBone = null;
            if (_marker != null) _marker.SetActive(false);
            if (_rayLine != null) _rayLine.gameObject.SetActive(false);
            if (_mainArea != null) _mainArea.gameObject.SetActive(true);
            if (_recordArea != null) _recordArea.gameObject.SetActive(false);
            if (!saved) SetStatus("");
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("Q3 shots: record mode OFF saved=" + saved);
        }

        private static void SetRecText()
        {
            if (_recLine1 != null)
                _recLine1.text = _recPointSet
                    ? "① 部位点 ✓  " + _recAtomUid + " / " + _recBoneName
                    : "① 部位点：射线指向人物身体，扣扳机记录";
            if (_recLine2 != null)
                _recLine2.text = _recPointSet
                    ? "② 镜头位姿：移动到取景位置，点「确认记录」"
                    : "② 镜头位姿：等待部位点";
            if (_commitBtn != null) _commitBtn.interactable = _recPointSet;
        }

        // ---------------- per-frame ----------------

        internal static void Tick(TriggerStateMachine index)
        {
            if (_tickFrame == Time.frameCount) return;
            _tickFrame = Time.frameCount;
            SuperController sc = SuperController.singleton;
            if (sc == null || sc.centerCameraTarget == null) return;
            if (_pendingThumb != null && Time.unscaledTime > _thumbDeadline)
                CancelThumb();
            // Per-scene persistence: reload when the scene identity changes.
            string key = sc.LoadedSceneName;
            if (string.IsNullOrEmpty(key)) key = "unsaved";
            if (key != _sceneKey) { CancelThumb(); _pendingEnter = null; _sceneKey = key; _loaded = false; }
            if (!_loaded) { _loaded = true; Load(); }

            if (_canvas != null && _canvas.worldCamera == null)
                _canvas.worldCamera = VrPointerPresentation.ReferenceCamera();
            if (_expanderCanvas != null && _expanderCanvas.worldCamera == null)
                _expanderCanvas.worldCamera = VrPointerPresentation.ReferenceCamera();

            Transform headT = sc.centerCameraTarget.transform;
            if (_panelVisible && _canvasGo != null && !_pinned)
                FollowView(_canvasGo.transform, headT, 0.20f, 0.62f, -0.06f);
            if (_expanderGo != null && _expanderGo.activeSelf)
                FollowView(_expanderGo.transform, headT, -0.16f, 0.55f, -0.14f);

            // Pinned panel drag: press and hold the trigger while pointing at
            // the panel background, then move the hand — like tile dragging.
            Transform dragHand = VrPointerPresentation.MotionController(sc, true);
            bool overBg = dragHand != null && _panelVisible && _pinned &&
                _canvasGo != null &&
                VrPointerPresentation.CurrentLookTarget(true) == _canvasGo;
            if (overBg && index != null &&
                index.PressedDownFrame == Time.frameCount)
            {
                _dragging = true;
                _dragHand = dragHand.position;
            }
            else if (_dragging)
            {
                if (index != null && index.Pressed)
                {
                    _canvasGo.transform.position += dragHand.position - _dragHand;
                    _dragHand = dragHand.position;
                }
                else _dragging = false;
            }

            if (!_recordMode) return;
            // Tick runs after SyncCursor, with the same visual pitch used by UI.
            // ProcessUI reassigns referenceCamera to the monitor; use the right
            // hand camera directly, never that shared mutable reference.
            Camera pointerCamera = sc.rightControllerCamera;
            bool haveRay = pointerCamera != null && pointerCamera.gameObject.activeInHierarchy;
            Ray ray = haveRay ? pointerCamera.ViewportPointToRay(new Vector3(.5f, .5f, 0f)) : new Ray();
            Atom hitAtom = null; Transform hitT = null; Vector3 hitP = Vector3.zero;
            bool hit = haveRay && !PointerInside &&
                RaycastPerson(ray, out hitAtom, out hitT, out hitP);
            Ray used = ray;
            UpdateRayLine(haveRay && !PointerInside, ray, hit);
            if (haveRay && index != null && index.PressedDownFrame == Time.frameCount
                && !hit && !PointerInside)
            {
                if (_recLine1 != null)
                    _recLine1.text = "① 部位点：未命中人物——把射线指到身体上再按";
                if (Quest3TriggerUIPlugin.Log != null)
                {
                    RaycastHit dh;
                    string what = Physics.Raycast(used, out dh, RayLength,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide)
                        ? dh.collider.name + " atom=" + (dh.collider.GetComponentInParent<Atom>() == null
                            ? "none" : dh.collider.GetComponentInParent<Atom>().type)
                        : "nothing";
                    Quest3TriggerUIPlugin.Log.LogInfo("Q3 shots: press miss | " + what);
                }
            }
            if (_marker != null)
            {
                if (hit && !_recPointSet)
                {
                    _marker.SetActive(true);
                    _marker.transform.position = hitP;
                    SetMarker(MarkIdle);
                    if (_recLine1 != null)
                        _recLine1.text = "① 部位点：" + hitAtom.uid + " / " +
                            hitT.name + " — 扣扳机记录";
                }
                else if (_recPointSet && _recBone != null)
                {
                    _marker.SetActive(true);
                    _marker.transform.position = _recBone.TransformPoint(_recPointLocal);
                    SetMarker(MarkSet);
                }
                else _marker.SetActive(false);
            }
            if (hit && index != null && index.PressedDownFrame == Time.frameCount)
            {
                _recPointSet = true;
                Transform bone = hitT;
                RaycastHit rh;
                if (Physics.Raycast(used, out rh, RayLength,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide)
                    && rh.rigidbody != null)
                    bone = rh.rigidbody.transform;
                _recBone = bone;
                _recBoneName = bone.name;
                _recAtomUid = hitAtom.uid;
                _recPointLocal = bone.InverseTransformPoint(hitP);
                SetRecText();
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogInfo("Q3 shots: point1 " +
                        _recAtomUid + "/" + _recBoneName);
            }
        }

        private static bool RaycastPerson(Ray ray, out Atom atom, out Transform t, out Vector3 p)
        {
            atom = null; t = null; p = Vector3.zero;
            RaycastHit hit;
            if (!Physics.Raycast(ray, out hit, RayLength,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
                return false;
            Atom a = hit.collider != null ? hit.collider.GetComponentInParent<Atom>() : null;
            if (a == null || a.type != "Person") return false;
            atom = a; t = hit.transform; p = hit.point;
            return true;
        }

        // Commit = record point2 + thumbnail + save + auto-exit.
        private static void Commit()
        {
            SuperController sc = SuperController.singleton;
            if (!_recPointSet || _recBone == null || sc == null) return;
            Transform head = sc.lookCamera != null ? sc.lookCamera.transform : sc.centerCameraTarget.transform;
            Shot s = new Shot();
            s.AtomUid = _recAtomUid; s.BoneName = _recBoneName;
            s.PointLocal = _recPointLocal;
            s.CamLocalPos = _recBone.InverseTransformPoint(head.position);
            s.CamLocalRot = Quaternion.Inverse(_recBone.rotation) * head.rotation;
            Vector3 dir = head.position - _recBone.TransformPoint(_recPointLocal);
            float dist = dir.magnitude;
            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Asin(Mathf.Clamp(dir.y / Mathf.Max(dist, 1e-4f), -1f, 1f)) * Mathf.Rad2Deg;
            s.ThumbFile = "shot_" + DateTime.Now.ToString("HHmmss") + "_" + _shots.Count + ".png";
            QueueThumb(s);
            _shots.Add(s);
            Save(); RebuildThumbs();
            ExitRecord(true);
            SetStatus(string.Format(CultureInfo.InvariantCulture,
                "镜头{0} 已记录：距离 {1:F2}m  偏航 {2:F0}°  俯仰 {3:F0}°",
                _shots.Count, dist, yaw, pitch));
        }

        // ---------------- entering shots ----------------

        private static void Enter(int i)
        {
            if (i < 0 || i >= _shots.Count) return;
            Shot s = _shots[i];
            Transform bone = ResolveBone(s);
            if (bone == null) { SetStatus("镜头" + (i + 1) + "：找不到 " + s.AtomUid + "/" + s.BoneName); return; }
            // UI clicks occur inside ProcessUI. Apply at the next controller
            // preparation boundary, never between the two eye/UI passes.
            _pendingEnter = s;
        }

        internal static void ApplyPendingShot(SuperController sc, GlobalVrPitchController pitch)
        {
            Shot s = _pendingEnter;
            if (s == null || sc == null || sc.navigationRig == null || sc.lookCamera == null || pitch == null) return;
            _pendingEnter = null;
            Transform bone = ResolveBone(s);
            if (bone == null || !_shots.Contains(s)) return;
            Vector3 wantPos = bone.TransformPoint(s.CamLocalPos);
            Quaternion wantRot = bone.rotation * s.CamLocalRot;
            pitch.SuspendForExternalRigControl(sc);
            Transform head = sc.lookCamera.transform;
            Transform rig = sc.navigationRig;
            // Remove residual base tilt left by the old direct RotateAround
            // path while preserving the current eye position and rig yaw.
            Vector3 eyePosition = head.position;
            rig.rotation = Quaternion.Euler(0f, rig.eulerAngles.y, 0f);
            rig.position += eyePosition - head.position;
            Vector3 f0 = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            Vector3 f1 = Vector3.ProjectOnPlane(wantRot * Vector3.forward, Vector3.up);
            float yaw = (f0.sqrMagnitude < .001f || f1.sqrMagnitude < .001f)
                ? 0f : Vector3.SignedAngle(f0, f1, Vector3.up);
            rig.RotateAround(head.position, Vector3.up, yaw);
            float pNow = Mathf.Asin(Mathf.Clamp(head.forward.y, -1f, 1f));
            float pWant = Mathf.Asin(Mathf.Clamp((wantRot * Vector3.forward).y, -1f, 1f));
            // Positive Unity X rotation looks down, hence now - wanted.
            pitch.RestorePitch((pNow - pWant) * Mathf.Rad2Deg);
            rig.position += wantPos - head.position;
            _current = _shots.IndexOf(s);
            RebuildThumbs();
            VrHaptics.Press();
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("Q3 shots: enter " + (_current + 1) + " managed pitch=" + pitch.PitchDegrees);
        }

        private static Transform ResolveBone(Shot s)
        {
            SuperController sc = SuperController.singleton;
            Atom a = sc == null ? null : sc.GetAtomByUid(s.AtomUid);
            if (a == null) return null;
            if (a.rigidbodies != null)
                for (int i = 0; i < a.rigidbodies.Length; i++)
                    if (a.rigidbodies[i] != null && a.rigidbodies[i].name == s.BoneName)
                        return a.rigidbodies[i].transform;
            Transform t = a.transform.Find(s.BoneName);
            if (t != null) return t;
            var q = new Queue<Transform>(); q.Enqueue(a.transform);
            while (q.Count > 0)
            {
                Transform c = q.Dequeue();
                for (int i = 0; i < c.childCount; i++)
                {
                    Transform k = c.GetChild(i);
                    if (k.name == s.BoneName) return k;
                    q.Enqueue(k);
                }
            }
            return null;
        }

        private static void Delete(int i)
        {
            if (i < 0 || i >= _shots.Count) return;
            Shot s = _shots[i];
            if (_pendingThumb == s) CancelThumb();
            if (_pendingEnter == s) _pendingEnter = null;
            try { string p = Path.Combine(SceneDir(), s.ThumbFile); if (File.Exists(p)) File.Delete(p); }
            catch (Exception) { }
            if (s.ThumbTex != null) UnityEngine.Object.Destroy(s.ThumbTex);
            _shots.RemoveAt(i);
            if (_current >= _shots.Count) _current = _shots.Count - 1;
            Save(); RebuildThumbs(); UpdateShotInfo();
        }

        // ---------------- persistence ----------------

        private static string SceneDir()
        {
            string safe = _sceneKey;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(Path.Combine(BepInEx.Paths.ConfigPath, "Quest3TriggerUI.shots"), safe);
        }
        private static string DataFile() { return Path.Combine(SceneDir(), "shots.txt"); }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(SceneDir());
                var lines = new List<string>();
                var inv = CultureInfo.InvariantCulture;
                for (int i = 0; i < _shots.Count; i++)
                {
                    Shot s = _shots[i];
                    lines.Add(string.Format(inv, "{0}|{1}|{2:R},{3:R},{4:R}|{5:R},{6:R},{7:R}|{8:R},{9:R},{10:R},{11:R}|{12}",
                        s.AtomUid, s.BoneName,
                        s.PointLocal.x, s.PointLocal.y, s.PointLocal.z,
                        s.CamLocalPos.x, s.CamLocalPos.y, s.CamLocalPos.z,
                        s.CamLocalRot.x, s.CamLocalRot.y, s.CamLocalRot.z, s.CamLocalRot.w,
                        s.ThumbFile));
                }
                File.WriteAllLines(DataFile(), lines.ToArray());
            }
            catch (Exception e)
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogWarning("Q3 shots save failed: " + e.Message);
            }
        }

        private static void Load()
        {
            for (int i = 0; i < _shots.Count; i++)
                if (_shots[i].ThumbTex != null) UnityEngine.Object.Destroy(_shots[i].ThumbTex);
            _shots.Clear(); _current = -1;
            try
            {
                string f = DataFile();
                if (File.Exists(f))
                {
                    var inv = CultureInfo.InvariantCulture;
                    foreach (string line in File.ReadAllLines(f))
                    {
                        string[] x = line.Split('|');
                        if (x.Length != 6) continue;
                        float[] v = new float[10];
                        string[] nums = (x[2] + "," + x[3] + "," + x[4]).Split(',');
                        if (nums.Length != 10) continue;
                        bool ok = true;
                        for (int i = 0; i < 10; i++)
                            if (!float.TryParse(nums[i], NumberStyles.Float, inv, out v[i])) ok = false;
                        if (!ok) continue;
                        Shot s = new Shot
                        {
                            AtomUid = x[0], BoneName = x[1], ThumbFile = x[5],
                            PointLocal = new Vector3(v[0], v[1], v[2]),
                            CamLocalPos = new Vector3(v[3], v[4], v[5]),
                            CamLocalRot = new Quaternion(v[6], v[7], v[8], v[9])
                        };
                        string png = Path.Combine(SceneDir(), s.ThumbFile);
                        if (File.Exists(png))
                        {
                            var t = new Texture2D(2, 2);
                            if (t.LoadImage(File.ReadAllBytes(png))) s.ThumbTex = t;
                            else UnityEngine.Object.Destroy(t);
                        }
                        _shots.Add(s);
                    }
                }
            }
            catch (Exception e)
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogWarning("Q3 shots load failed: " + e.Message);
            }
            RebuildThumbs(); UpdateShotInfo();
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("Q3 shots: loaded " + _shots.Count + " for " + _sceneKey);
        }

        // ---------------- screenshot ----------------

        private static void QueueThumb(Shot shot)
        {
            CancelThumb();
            _pendingThumb = shot;
            _pendingThumbPath = Path.Combine(SceneDir(), shot.ThumbFile);
            _thumbDeadline = Time.unscaledTime + 3f;
            Camera.onPostRender += CaptureRenderedThumb;
        }

        private static void CancelThumb()
        {
            Camera.onPostRender -= CaptureRenderedThumb;
            _pendingThumb = null;
            _pendingThumbPath = null;
        }

        private static void CaptureRenderedThumb(Camera camera)
        {
            SuperController sc = SuperController.singleton;
            if (_pendingThumb == null || sc == null || camera == null || camera != sc.lookCamera) return;
            Shot shot = _pendingThumb;
            string path = _pendingThumbPath;
            CancelThumb();
            RenderTexture previous = RenderTexture.active;
            RenderTexture small = null;
            Texture2D pixels = null, thumb = null;
            try
            {
                // Copy a frame already rendered by the eye. Never redirect or
                // manually Render the live camera: its DLSS/PostMagic callbacks
                // own the stereo targets and must run only on their normal pass.
                small = RenderTexture.GetTemporary(336, 189, 0);
                if (previous != null) Graphics.Blit(previous, small);
                else
                {
                    Rect rect = camera.pixelRect;
                    int w = Mathf.RoundToInt(rect.width), h = Mathf.RoundToInt(rect.height);
                    if (w < 1 || h < 1) return;
                    pixels = new Texture2D(w, h, TextureFormat.RGB24, false);
                    pixels.ReadPixels(rect, 0, 0);
                    pixels.Apply();
                    Graphics.Blit(pixels, small);
                }
                RenderTexture.active = small;
                thumb = new Texture2D(336, 189, TextureFormat.RGB24, false);
                thumb.ReadPixels(new Rect(0, 0, 336, 189), 0, 0);
                thumb.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, thumb.EncodeToPNG());
                shot.ThumbTex = thumb; thumb = null;
                RebuildThumbs();
            }
            catch (Exception e)
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogWarning("Q3 shots thumb failed: " + e.Message);
            }
            finally
            {
                RenderTexture.active = previous;
                if (small != null) RenderTexture.ReleaseTemporary(small);
                if (pixels != null) UnityEngine.Object.Destroy(pixels);
                if (thumb != null) UnityEngine.Object.Destroy(thumb);
            }
        }

        // ---------------- panel UI ----------------

        private static void ShowPanel()
        {
            EnsurePanel();
            SuperController sc = SuperController.singleton;
            Transform head = sc.centerCameraTarget.transform;
            Vector3 fwd = head.forward;
            if (fwd.sqrMagnitude < .01f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            if (right.sqrMagnitude < .01f) right = Vector3.right;
            right.Normalize();
            Vector3 up = Vector3.Cross(fwd, right);
            Vector3 pos = head.position + fwd * 0.62f + right * 0.20f - up * 0.06f;
            _canvasGo.transform.position = pos;
            _canvasGo.transform.rotation = Quaternion.LookRotation(pos - head.position, Vector3.up);
            _canvasGo.SetActive(true);
            _panelVisible = true;
            if (_expanderGo != null) _expanderGo.SetActive(false);
            UpdateShotInfo();
        }

        private static void TogglePin()
        {
            _pinned = !_pinned;
            _dragging = false;
            if (_pinBtn != null)
            {
                Text t = _pinBtn.GetComponentInChildren<Text>();
                if (t != null) t.text = _pinned ? "跟随" : "固定";
            }
        }

        private static void HidePanel(bool expander)
        {
            _panelVisible = false;
            _pinned = false;
            _dragging = false;
            if (_pinBtn != null)
            {
                Text t = _pinBtn.GetComponentInChildren<Text>();
                if (t != null) t.text = "固定";
            }
            if (_recordMode) ExitRecord(false);
            if (_canvasGo != null) _canvasGo.SetActive(false);
            if (expander) ShowExpander();
        }

        private static void ShowExpander()
        {
            EnsureExpander();
            if (_canvasGo != null)
            {
                _expanderGo.transform.position =
                    _canvasGo.transform.position + _canvasGo.transform.right * -0.31f;
                _expanderGo.transform.rotation = _canvasGo.transform.rotation;
            }
            _expanderGo.SetActive(true);
        }

        private static void SetStatus(string s)
        {
            if (_status != null) _status.text = s;
        }
        private static void UpdateShotInfo()
        {
            if (_shotInfo != null)
                _shotInfo.text = _shots.Count == 0 ? "无镜头"
                    : _current < 0 ? "镜头0/" + _shots.Count + "（未启动）"
                    : "镜头" + (_current + 1) + "/" + _shots.Count;
        }

        // Hot-reload generations can leave orphaned world objects behind;
        // destroy any stale copy before creating our own.
        private static void KillNamed(string name, GameObject keep)
        {
            int killed = 0;
            foreach (GameObject g in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (g == null || g == keep || g.name != name) continue;
                if (g.hideFlags != HideFlags.None) continue;
                UnityEngine.Object.Destroy(g);
                killed++;
            }
            if (killed > 0 && Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo("Q3 shots: killed " + killed +
                    " stale " + name);
        }

        private static void EnsurePanel()
        {
            if (_canvasGo != null) return;
            KillNamed("Q3 Shot Panel", null);
            _font = (Font)Resources.GetBuiltinResource(typeof(Font), "Arial.ttf");
            _canvasGo = new GameObject("Q3 Shot Panel");
            UnityEngine.Object.DontDestroyOnLoad(_canvasGo);
            _canvas = _canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = 32750;
            _canvasGo.AddComponent<GraphicRaycaster>();
            RectTransform root = _canvasGo.GetComponent<RectTransform>();
            root.sizeDelta = new Vector2(960f, 560f);
            _canvasGo.transform.localScale = Vector3.one * PanelScale;
            Image bg = _canvasGo.AddComponent<Image>();
            bg.color = new Color(.08f, .09f, .12f, .55f);
            SetLayer(_canvasGo);

            _title = Text(root, "运镜", 20, -12, 300, 40, 30, TextAnchor.MiddleLeft,
                new Color(.95f, .8f, .35f));
            // Stamp the payload generation into the title so a stale-gen clone
            // is identifiable at a glance.
            string asm = typeof(VrShotCameras).Assembly.GetName().Name;
            _title.text = "运镜 ·" + asm.Substring(Mathf.Max(0, asm.Length - 6));
            _shotInfo = Text(root, "", 660, -12, 280, 40, 24, TextAnchor.MiddleRight,
                new Color(.7f, .9f, 1f));

            _mainArea = new GameObject("Main", typeof(RectTransform)).GetComponent<RectTransform>();
            _mainArea.SetParent(root, false);
            Stretch(_mainArea);
            Btn(_mainArea, "记录", 20, -62, 120, 56, ToggleRecord);
            Btn(_mainArea, "上一镜", 150, -62, 120, 56, Prev);
            Btn(_mainArea, "下一镜", 280, -62, 120, 56, Next);
            _pinBtn = Btn(_mainArea, "固定", 410, -62, 110, 56, TogglePin);
            Btn(_mainArea, "隐藏", 530, -62, 110, 56, delegate { HidePanel(false); });
            Btn(_mainArea, "退出", 650, -62, 110, 56, delegate { HidePanel(false); });
            _status = Text(_mainArea, "", 20, -128, 920, 34, 21, TextAnchor.MiddleLeft,
                new Color(.75f, .85f, 1f));
            _thumbRow = new GameObject("Thumbs", typeof(RectTransform)).GetComponent<RectTransform>();
            _thumbRow.SetParent(_mainArea, false);
            _thumbRow.anchorMin = new Vector2(0f, 0f);
            _thumbRow.anchorMax = new Vector2(1f, 0f);
            _thumbRow.pivot = new Vector2(.5f, 0f);
            _thumbRow.anchoredPosition = new Vector2(0f, 12f);
            _thumbRow.sizeDelta = new Vector2(-20f, 130f);

            _recordArea = new GameObject("Record", typeof(RectTransform)).GetComponent<RectTransform>();
            _recordArea.SetParent(root, false);
            Stretch(_recordArea);
            _recLine1 = Text(_recordArea, "", 20, -80, 920, 40, 24, TextAnchor.MiddleLeft, Color.white);
            _recLine2 = Text(_recordArea, "", 20, -128, 920, 40, 24, TextAnchor.MiddleLeft,
                new Color(.7f, .85f, 1f));
            _commitBtn = Btn(_recordArea, "确认记录", 20, -190, 160, 60, Commit);
            Btn(_recordArea, "取消", 200, -190, 120, 60, delegate { ExitRecord(false); });
            Text(_recordArea,
                "提示：部位点记录在骨骼局部坐标——角色移动，镜头锚点跟随。\n进入镜头是瞬间跳转，之后视角交还给你。",
                20, -280, 920, 80, 19, TextAnchor.UpperLeft, new Color(.6f, .65f, .7f));
            _recordArea.gameObject.SetActive(false);

            _canvasGo.SetActive(false);
            // Registered for clicks (VaM input needs allCanvases membership);
            // IgnoreCanvas keeps our sorting layer from being rewritten.
            _canvasGo.AddComponent<IgnoreCanvas>();
            if (SuperController.singleton != null)
                SuperController.singleton.AddCanvas(_canvas);
            _canvas.worldCamera = VrPointerPresentation.ReferenceCamera();
        }

        private static void EnsureExpander()
        {
            if (_expanderGo != null) return;
            KillNamed("Q3 Shot Expander", null);
            _expanderGo = new GameObject("Q3 Shot Expander");
            UnityEngine.Object.DontDestroyOnLoad(_expanderGo);
            _expanderCanvas = _expanderGo.AddComponent<Canvas>();
            _expanderCanvas.renderMode = RenderMode.WorldSpace;
            _expanderCanvas.overrideSorting = true;
            _expanderCanvas.sortingOrder = 32750;
            _expanderGo.AddComponent<GraphicRaycaster>();
            RectTransform r = _expanderGo.GetComponent<RectTransform>();
            r.sizeDelta = new Vector2(70f, 70f);
            _expanderGo.transform.localScale = Vector3.one * 0.0009f;
            Button b = Btn(r, "▶", 0, 0, 70, 70, ShowPanel);
            b.targetGraphic.color = new Color(.15f, .18f, .22f, .6f);
            SetLayer(_expanderGo);
            _expanderGo.SetActive(false);
            _expanderGo.AddComponent<IgnoreCanvas>();
            if (SuperController.singleton != null)
                SuperController.singleton.AddCanvas(_expanderCanvas);
            _expanderCanvas.worldCamera = VrPointerPresentation.ReferenceCamera();
        }

        private static void RebuildThumbs()
        {
            if (_thumbRow == null) return;
            for (int i = _thumbRow.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_thumbRow.GetChild(i).gameObject);
            for (int i = 0; i < _shots.Count; i++)
            {
                Shot s = _shots[i];
                int idx = i;
                RectTransform item = new GameObject("shot" + i, typeof(RectTransform)).GetComponent<RectTransform>();
                item.SetParent(_thumbRow, false);
                item.anchorMin = item.anchorMax = new Vector2(0f, .5f);
                item.pivot = new Vector2(0f, .5f);
                item.anchoredPosition = new Vector2(10f + i * (ThumbW + 12f), 0f);
                item.sizeDelta = new Vector2(ThumbW, ThumbH + 22f);
                Image frame = item.gameObject.AddComponent<Image>();
                frame.color = (i == _current) ? new Color(.2f, .5f, .9f, .9f) : new Color(.2f, .22f, .26f, .9f);
                RectTransform imgRt = new GameObject("img", typeof(RectTransform)).GetComponent<RectTransform>();
                imgRt.SetParent(item, false);
                imgRt.anchorMin = imgRt.anchorMax = new Vector2(.5f, .5f);
                imgRt.anchoredPosition = new Vector2(0f, -9f);
                imgRt.sizeDelta = new Vector2(ThumbW - 8f, ThumbH);
                RawImage ri = imgRt.gameObject.AddComponent<RawImage>();
                ri.texture = s.ThumbTex;
                ri.color = s.ThumbTex != null ? Color.white : new Color(.3f, .3f, .3f);
                Button open = imgRt.gameObject.AddComponent<Button>();
                int openIdx = idx;
                open.onClick.AddListener(delegate { Enter(openIdx); });
                Text(item, "镜头" + (i + 1), 0, 0, 0, 0, 18, TextAnchor.UpperLeft, Color.white)
                    .rectTransform.anchoredPosition = new Vector2(4f, -2f);
                RectTransform x = new GameObject("x", typeof(RectTransform)).GetComponent<RectTransform>();
                x.SetParent(item, false);
                x.anchorMin = x.anchorMax = new Vector2(1f, 1f);
                x.pivot = new Vector2(1f, 1f);
                x.anchoredPosition = Vector2.zero;
                x.sizeDelta = new Vector2(24f, 24f);
                Image xi = x.gameObject.AddComponent<Image>();
                xi.color = new Color(.6f, .1f, .1f, .95f);
                Text xtt = Text(x, "×", 0, 0, 0, 0, 20, TextAnchor.MiddleCenter, Color.white);
                Stretch(xtt.rectTransform);
                Button xb = x.gameObject.AddComponent<Button>();
                int delIdx = idx;
                xb.onClick.AddListener(delegate { Delete(delIdx); });
            }
            UpdateShotInfo();
        }

        private static void EnsureMarker()
        {
            if (_marker != null) return;
            KillNamed("Q3 Shot Marker", null);
            _marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _marker.name = "Q3 Shot Marker";
            UnityEngine.Object.Destroy(_marker.GetComponent<Collider>());
            _marker.transform.localScale = Vector3.one * 0.028f;
            _markerRend = _marker.GetComponent<MeshRenderer>();
            _markerRend.material = new Material(Shader.Find("Sprites/Default"));
            _marker.SetActive(false);
        }

        private static void FollowView(Transform t, Transform head,
            float right, float fwd, float up)
        {
            // Full pitch-following basis: forward from the head, right from
            // world-up cross forward (keeps the panel roll-stable).
            Vector3 f = head.forward;
            if (f.sqrMagnitude < .01f) f = Vector3.forward;
            f.Normalize();
            Vector3 r = Vector3.Cross(Vector3.up, f);
            if (r.sqrMagnitude < .01f) r = Vector3.right;
            r.Normalize();
            Vector3 u = Vector3.Cross(f, r);
            Vector3 target = head.position + f * fwd + r * right + u * up;
            Quaternion rot = Quaternion.LookRotation(target - head.position, Vector3.up);
            float k = 1f - Mathf.Exp(-4f * Time.unscaledDeltaTime);
            t.position = Vector3.Lerp(t.position, target, k);
            t.rotation = Quaternion.Slerp(t.rotation, rot, k);
        }

        private static void UpdateRayLine(bool have, Ray ray, bool hit)
        {
            EnsureRayLine();
            if (_rayLine == null) return;
            if (!have) { _rayLine.gameObject.SetActive(false); return; }
            _rayLine.gameObject.SetActive(true);
            _rayLine.SetPosition(0, ray.origin);
            _rayLine.SetPosition(1, ray.origin + ray.direction * RayLength);
            Color c = hit ? RayHit : RayIdle;
            _rayLine.startColor = c; _rayLine.endColor = c;
        }

        private static void EnsureRayLine()
        {
            if (_rayLine != null) return;
            KillNamed("Q3 Shot Ray", null);
            var go = new GameObject("Q3 Shot Ray");
            _rayLine = go.AddComponent<LineRenderer>();
            _rayLine.useWorldSpace = true;
            _rayLine.positionCount = 2;
            _rayLine.startWidth = _rayLine.endWidth = 0.003f;
            _rayLine.material = new Material(Shader.Find("Sprites/Default"));
            go.SetActive(false);
        }
        private static void SetMarker(Color c)
        {
            if (_markerRend != null) _markerRend.material.color = c;
        }

        // ---------------- UI helpers ----------------

        private static Button Btn(RectTransform parent, string label, float x, float y,
            float w, float h, UnityEngine.Events.UnityAction fn)
        {
            GameObject go = new GameObject("B_" + label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform r = go.GetComponent<RectTransform>();
            r.anchorMin = r.anchorMax = new Vector2(0f, 1f);
            r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = new Vector2(x, y);
            r.sizeDelta = new Vector2(w, h);
            Image img = go.AddComponent<Image>();
            img.color = new Color(.18f, .34f, .55f, .95f);
            Text t = Text(r, label, 0, 0, 0, 0, 24, TextAnchor.MiddleCenter, Color.white);
            Stretch(t.rectTransform);
            Button b = go.AddComponent<Button>();
            b.targetGraphic = img;
            b.onClick.AddListener(fn);
            return b;
        }

        private static Text Text(RectTransform parent, string s, float x, float y,
            float w, float h, float size, TextAnchor anchor, Color color)
        {
            GameObject go = new GameObject("T", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Text t = go.AddComponent<Text>();
            t.font = _font; t.text = s;
            t.fontSize = Mathf.RoundToInt(size);
            t.alignment = anchor; t.color = color; t.raycastTarget = false;
            RectTransform r = t.rectTransform;
            r.anchorMin = r.anchorMax = new Vector2(0f, 1f);
            r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = new Vector2(x, y);
            r.sizeDelta = new Vector2(w, h);
            return t;
        }

        private static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = r.offsetMax = Vector2.zero;
        }

        private static void SetLayer(GameObject g)
        {
            // Preserve the existing world-space panel layer.
            int l = 0;
            g.layer = l;
            for (int i = 0; i < g.transform.childCount; i++)
                SetLayer(g.transform.GetChild(i).gameObject);
        }

        internal static void Shutdown()
        {
            CancelThumb(); _pendingEnter = null;
            _recordMode = false; _panelVisible = false;
            if (_canvas != null && SuperController.singleton != null)
            { try { SuperController.singleton.RemoveCanvas(_canvas); } catch (Exception) { } }
            if (_expanderCanvas != null && SuperController.singleton != null)
            { try { SuperController.singleton.RemoveCanvas(_expanderCanvas); } catch (Exception) { } }
            if (_canvasGo != null) UnityEngine.Object.Destroy(_canvasGo);
            if (_expanderGo != null) UnityEngine.Object.Destroy(_expanderGo);
            if (_marker != null) UnityEngine.Object.Destroy(_marker);
            if (_rayLine != null) UnityEngine.Object.Destroy(_rayLine.gameObject);
            _canvasGo = null; _canvas = null; _expanderGo = null; _expanderCanvas = null;
            _marker = null; _markerRend = null; _rayLine = null;
        }
    }
    [HarmonyLib.HarmonyPatch(typeof(SuperController), "SyncCursor")]
    internal static class ShotTickAfterPointerFinalizationPatch
    {
        private static void Postfix()
        {
            if (Quest3TriggerUIPlugin.InputRuntimeActive)
                VrShotCameras.Tick(Quest3TriggerUIPlugin.Trigger);
        }
    }

}

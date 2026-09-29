using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SimpleJSON;
using UnityEngine;
using UnityEngine.UI;
using MVR.FileManagement;

namespace Quest3TriggerUI
{
    // Scene dock save session — mirrors the preset dock's save model but
    // never opens the file browser: 「保存」toggles a session where a cell's
    // thumbnail offers 拍照覆盖/仅覆盖/缩略图/另存, and the name strip
    // edits the favorite's display alias (the scene file is untouched).
    // 「存新」on the strip saves the live scene under a typed name and
    // appends the new file to the current list.
    internal static partial class UiAssistHudLink
    {
        private static bool _sdSaveBrowsing;
        private static GameObject _sdSaveOverlay;
        private static SdSlotTag _sdSaveTag;

        // ---------- save session ----------

        private static void EnterSdSaveSession()
        {
            if (_dockDeleteMode)
            {
                _dockDeleteMode = false;
                PaintDockDeleteMode();
            }
            if (_sdSaveOverlay != null) _sdSaveOverlay.SetActive(false);
            _sdSaveTag = null;
            _sdSaveBrowsing = true;
            RefreshSdTagRows();
            Log("场景保存模式：点缩略图覆盖/换缩略图，点名字改收藏别名。再点「结束保存」退出。");
        }

        private static void ExitSdSaveSession()
        {
            if (!_sdSaveBrowsing) return;
            _sdSaveBrowsing = false;
            if (_sdSaveOverlay != null) _sdSaveOverlay.SetActive(false);
            _sdSaveTag = null;
            CancelSdRename();
            RefreshSdTagRows();
        }

        // Returns true while save mode owns the click: the cell shows the
        // overwrite overlay instead of loading the scene.
        private static bool SaveSceneOnClick(SdSlotTag tag, RectTransform cell)
        {
            if (!_sdSaveBrowsing || tag == null || cell == null) return false;
            ToggleSdSaveOverlay(tag, cell);
            return true;
        }

        private static void ToggleSdSaveOverlay(SdSlotTag tag,
            RectTransform cell)
        {
            if (_sdSaveOverlay != null && _sdSaveOverlay.activeSelf &&
                _sdSaveOverlay.transform.parent == cell && _sdSaveTag == tag)
            {
                _sdSaveOverlay.SetActive(false);
                _sdSaveTag = null;
                return;
            }
            EnsureSdSaveOverlay();
            CancelSdRename();
            _sdSaveTag = tag;
            RectTransform rt = (RectTransform)_sdSaveOverlay.transform;
            rt.SetParent(cell, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(-4f, -4f);
            rt.offsetMax = new Vector2(4f, 4f);
            rt.SetAsLastSibling();
            _sdSaveOverlay.SetActive(true);
            VrHaptics.Press();
        }

        private static void EnsureSdSaveOverlay()
        {
            if (_sdSaveOverlay != null) return;
            GameObject go = new GameObject("SdSaveOverlay",
                typeof(RectTransform));
            go.AddComponent<SdOverlayTag>();
            go.AddComponent<SdDockTag>();
            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.78f);
            bg.raycastTarget = true;
            RectTransform rt = (RectTransform)go.transform;
            // 2x2: json+screenshot overwrite / json-only overwrite on top,
            // thumbnail-only reshoot / save-as below.
            CreateSdOverlayButton(rt, "拍 照", new Vector2(-21f, 14f),
                new Vector2(40f, 30f), new Color(0.14f, 0.42f, 0.22f, 1f),
                delegate { SdOverwriteClicked(0); });
            CreateSdOverlayButton(rt, "覆 盖", new Vector2(21f, 14f),
                new Vector2(40f, 30f), new Color(0.55f, 0.32f, 0.10f, 1f),
                delegate { SdOverwriteClicked(1); });
            CreateSdOverlayButton(rt, "缩 图", new Vector2(-21f, -20f),
                new Vector2(40f, 22f), new Color(0.16f, 0.30f, 0.42f, 1f),
                delegate { SdOverwriteClicked(2); });
            CreateSdOverlayButton(rt, "另 存", new Vector2(21f, -20f),
                new Vector2(40f, 22f), new Color(0.13f, 0.24f, 0.32f, 1f),
                delegate
                {
                    SdSlotTag host = _sdSaveTag;
                    if (_sdSaveOverlay != null)
                        _sdSaveOverlay.SetActive(false);
                    _sdSaveTag = null;
                    BeginSdSaveAs(host);
                });
            _sdSaveOverlay = go;
            go.SetActive(false);
        }

        private static void CreateSdOverlayButton(RectTransform parent,
            string label, Vector2 center, Vector2 size, Color color,
            UnityEngine.Events.UnityAction action)
        {
            GameObject go = new GameObject("SdSave " + label,
                typeof(RectTransform));
            go.AddComponent<SdOverlayTag>();
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = center;
            rect.sizeDelta = size;
            Image bg = go.AddComponent<Image>();
            bg.color = color;
            bg.raycastTarget = true;
            Button button = go.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                if (Quest3TriggerUIPlugin.ClothingDragActive ||
                    Time.unscaledTime < _favoriteClickAfter) return;
                VrHaptics.Press();
                action();
            });
            Text text = new GameObject("Label", typeof(RectTransform))
                .AddComponent<Text>();
            RectTransform tr = (RectTransform)text.transform;
            tr.SetParent(rect, false);
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
            text.text = label;
            text.alignment = TextAnchor.MiddleCenter;
            text.fontSize = 13;
            text.color = Color.white;
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.raycastTarget = false;
        }

        // 0 = json + fresh thumbnail, 1 = json only, 2 = thumbnail only.
        private static void SdOverwriteClicked(int mode)
        {
            SdSlotTag tag = _sdSaveTag;
            if (_sdSaveOverlay != null) _sdSaveOverlay.SetActive(false);
            _sdSaveTag = null;
            if (tag == null || string.IsNullOrEmpty(tag.Path)) return;
            SuperController sc = SuperController.singleton;
            if (sc == null) return;
            string path = tag.Path;
            try
            {
                if (mode == 0)
                {
                    string savedPath = path;
                    sc.Save(path, null, true, true,
                        new SuperController.ScreenShotCallback(
                            delegate(string img)
                            { InvalidateSdThumb(savedPath); }),
                        true);
                    Log("覆盖保存场景：" + path + "（json+缩略图，按提示瞄准取景）");
                }
                else if (mode == 1)
                {
                    StoreSceneJsonOnly(path);
                    Log("已覆盖场景 json：" + path + "（缩略图未动）");
                }
                else
                {
                    string shotPath = path;
                    sc.DoSaveScreenshot(path,
                        new SuperController.ScreenShotCallback(
                            delegate(string img)
                            { InvalidateSdThumb(shotPath); }));
                    Log("重拍场景缩略图：" + path + "（按提示瞄准取景）");
                }
            }
            catch (Exception e) { Error(e); }
        }

        // SaveInternalFinish minus the aim-and-select screenshot pass:
        // handlers + loadedName bookkeeping + JSON write.
        private static void StoreSceneJsonOnly(string path)
        {
            SuperController sc = SuperController.singleton;
            string saveName = sc.NormalizeSavePath(path);
            try
            {
                if (sc.onBeforeSceneSaveHandlers != null)
                    sc.onBeforeSceneSaveHandlers();
                string dir = saveName;
                int slash = dir.LastIndexOf('/');
                if (slash >= 0) dir = dir.Substring(0, slash);
                else dir = "";
                if (dir.Length > 0) FileManager.CreateDirectory(dir);
                FieldInfo ln = typeof(SuperController).GetField("loadedName",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (ln != null) ln.SetValue(sc, saveName);
                JSONClass jc = sc.GetSaveJSON(null, true, true);
                MethodInfo write = typeof(SuperController).GetMethod(
                    "SaveJSONInternal",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (write != null)
                    write.Invoke(sc, new object[] { jc, saveName });
                else
                    sc.SaveJSON(jc, saveName);
                if (sc.onSceneSavedHandlers != null)
                    sc.onSceneSavedHandlers();
            }
            catch (Exception e)
            {
                Log("场景覆盖保存失败：" + e.Message);
            }
        }

        // ---------- name overlay: alias rename / save-as ----------

        private static GameObject _sdRenameOverlay;
        private static InputField _sdRenameInput;
        private static string _sdRenamePath;
        // 0 = rename the favorite alias (entry[1]); 1 = save current scene
        // under the typed name and append to the current list.
        private static int _sdRenameMode;
        private static RectTransform _sdRenameHost;

        private static void BeginSdRename(SdSlotTag tag)
        {
            if (tag == null || string.IsNullOrEmpty(tag.Path)) return;
            if (_dockDeleteMode) return;
            EnsureSdRenameOverlay();
            if (_sdSaveOverlay != null) _sdSaveOverlay.SetActive(false);
            _sdSaveTag = null;
            _sdRenameMode = 0;
            _sdRenamePath = tag.Path;
            AttachSdNameOverlay((RectTransform)tag.transform);
            int at = FindSdIndex(VisibleSdFavorites, tag.Path);
            _sdRenameInput.text = at >= 0
                ? SdDisplayName(VisibleSdFavorites[at])
                : SdDisplayName(new string[] { tag.Path, "", "" });
            _sdRenameOverlay.SetActive(true);
            _sdRenameInput.ActivateInputField();
            VrTextInputBridge.Select(_sdRenameInput);
            VrHaptics.Press();
        }

        private static void BeginSdSaveAs(SdSlotTag host)
        {
            EnsureSdRenameOverlay();
            if (_sdSaveOverlay != null) _sdSaveOverlay.SetActive(false);
            _sdSaveTag = null;
            _sdRenameMode = 1;
            _sdRenamePath = null;
            AttachSdNameOverlay(host != null
                ? (RectTransform)host.transform
                : _sdTagStrip);
            SuperController sc = SuperController.singleton;
            string current = sc == null ? "" : sc.LoadedSceneName;
            _sdRenameInput.text = string.IsNullOrEmpty(current)
                ? "Scene" : SdLeafName(current);
            _sdRenameOverlay.SetActive(true);
            _sdRenameInput.ActivateInputField();
            VrTextInputBridge.Select(_sdRenameInput);
            VrHaptics.Press();
        }

        private static void AttachSdNameOverlay(RectTransform host)
        {
            _sdRenameHost = host;
            RectTransform rt = (RectTransform)_sdRenameOverlay.transform;
            rt.SetParent(host, false);
            if (host == _sdTagStrip)
            {
                // Saving a brand-new scene has no cell to attach to —
                // float the input over the strip's top rows instead.
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.offsetMin = new Vector2(0f, -FavTagRowH - FavTagGap);
                rt.offsetMax = new Vector2(0f, -2f);
            }
            else
            {
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.offsetMin = new Vector2(-6f, -6f);
                rt.offsetMax = new Vector2(6f, 6f);
            }
            rt.SetAsLastSibling();
        }

        private static void EnsureSdRenameOverlay()
        {
            if (_sdRenameOverlay != null) return;
            GameObject go = new GameObject("SdRenameOverlay",
                typeof(RectTransform));
            go.AddComponent<SdOverlayTag>();
            go.AddComponent<SdDockTag>();
            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.82f);
            bg.raycastTarget = true;
            RectTransform rt = (RectTransform)go.transform;

            GameObject inputGo = new GameObject("Input",
                typeof(RectTransform));
            RectTransform ir = (RectTransform)inputGo.transform;
            ir.SetParent(rt, false);
            ir.anchorMin = new Vector2(0f, 0.5f);
            ir.anchorMax = new Vector2(1f, 1f);
            ir.offsetMin = new Vector2(2f, -4f);
            ir.offsetMax = new Vector2(-2f, -2f);
            Image inputBg = inputGo.AddComponent<Image>();
            inputBg.color = new Color(0.17f, 0.19f, 0.24f, 1f);
            Text inputText = new GameObject("Text", typeof(RectTransform))
                .AddComponent<Text>();
            RectTransform itr = (RectTransform)inputText.transform;
            itr.SetParent(ir, false);
            itr.anchorMin = Vector2.zero;
            itr.anchorMax = Vector2.one;
            itr.offsetMin = new Vector2(3f, 0f);
            itr.offsetMax = new Vector2(-3f, 0f);
            inputText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            inputText.fontSize = 13;
            inputText.alignment = TextAnchor.MiddleLeft;
            inputText.color = Color.white;
            inputText.verticalOverflow = VerticalWrapMode.Overflow;
            inputText.raycastTarget = false;
            InputField input = inputGo.AddComponent<InputField>();
            input.targetGraphic = inputBg;
            input.textComponent = inputText;
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = 60;
            _sdRenameInput = input;

            CreateSdOverlayButton(rt, "✓", new Vector2(-20f, -14f),
                new Vector2(38f, 22f), new Color(0.14f, 0.45f, 0.20f, 1f),
                CommitSdRename);
            CreateSdOverlayButton(rt, "✗", new Vector2(20f, -14f),
                new Vector2(38f, 22f), new Color(0.45f, 0.16f, 0.14f, 1f),
                CancelSdRename);
            _sdRenameOverlay = go;
            go.SetActive(false);
        }

        private static void CommitSdRename()
        {
            int mode = _sdRenameMode;
            string path = _sdRenamePath;
            string typed = _sdRenameInput == null ? "" : _sdRenameInput.text;
            if (_sdRenameOverlay != null) _sdRenameOverlay.SetActive(false);
            _sdRenamePath = null;
            _sdRenameHost = null;
            VrTextInputBridge.Clear();
            if (mode == 0)
            {
                if (string.IsNullOrEmpty(path)) return;
                string alias = (typed ?? "").Trim();
                if (alias.Length > 24) alias = alias.Substring(0, 24);
                List<string[]> items = VisibleSdFavorites;
                int at = FindSdIndex(items, path);
                if (at < 0) return;
                items[at][1] = alias;
                SaveSdStores();
                for (int i = 0; i < _sdVisibleCells.Count; i++)
                {
                    SdSlotTag cell = _sdVisibleCells[i];
                    if (cell != null && cell.Path == path &&
                        cell.Label != null)
                        cell.Label.text = SdDisplayName(items[at]);
                }
                VrHaptics.Confirm();
                Log("场景收藏别名已改为：" + (alias.Length > 0
                    ? alias : SdDisplayName(items[at])));
                return;
            }
            SaveSceneAs(typed);
        }

        private static void CancelSdRename()
        {
            if (_sdRenameOverlay != null) _sdRenameOverlay.SetActive(false);
            _sdRenamePath = null;
            _sdRenameHost = null;
            if (_sdRenameInput != null) VrTextInputBridge.Clear();
        }

        // Scene file name rules: strip characters that cannot appear in a
        // filename, append .json. The save path is always Saves/scene/<n>.
        private static string SdFileName(string typed)
        {
            string s = (typed ?? "").Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            s = s.Replace('/', '_').Replace('\\', '_').Trim();
            if (s.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 5).Trim();
            return s.Length == 0 ? "" : s + ".json";
        }

        private static string SdLeafName(string path)
        {
            string leaf = path ?? "";
            int slash = leaf.LastIndexOf('/');
            if (slash >= 0) leaf = leaf.Substring(slash + 1);
            if (leaf.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                leaf = leaf.Substring(0, leaf.Length - 5);
            return leaf;
        }

        private static void SaveSceneAs(string typed)
        {
            SuperController sc = SuperController.singleton;
            if (sc == null) return;
            string leaf = SdFileName(typed);
            if (leaf.Length == 0) { Log("场景保存取消：文件名为空。"); return; }
            string path = "Saves/scene/" + leaf;
            string stored = path;
            try
            {
                sc.Save(path, null, true, true,
                    new SuperController.ScreenShotCallback(
                        delegate(string img)
                        { InvalidateSdThumb(stored); }),
                    false);
                stored = sc.NormalizePath(path);
                List<string[]> items = VisibleSdFavorites;
                if (FindSdIndex(items, stored) < 0)
                {
                    items.Add(new string[] {
                        stored, SdLeafName(stored), "" });
                    SaveSdStores();
                    _sdScrollY = float.MaxValue;
                    _sdDirty = true;
                }
                VrHaptics.Confirm();
                Log("已另存当前场景并收藏：" + stored);
            }
            catch (Exception e)
            {
                Log("场景另存失败：" + e.Message);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using MVR.FileManagement;

namespace Quest3TriggerUI
{
    // Dock save interactions are scoped to the save browser being open:
    // 保存 opens the dialog as before; while it is up, clicking a cell's
    // thumbnail shows a 拍照覆盖/仅覆盖/另存为 overlay (overwrite the .vap
    // that cell points at), and clicking its name strip opens an inline
    // rename field driven by the VR keyboard.
    internal static partial class UiAssistHudLink
    {
        // True for the lifetime of the dock-initiated save dialog.
        private static bool _pdSaveBrowsing;

        private static void PaintDockSaveMode()
        {
            for (int i = 0; i < _pdVisibleCells.Count; i++)
            {
                PdSlotTag tag = _pdVisibleCells[i];
                if (tag != null && tag.NameHit != null)
                    tag.NameHit.raycastTarget = _pdSaveBrowsing;
            }
        }

        // ---------- thumbnail zone: overwrite save ----------

        private static GameObject _pdSaveOverlay;
        private static PdSlotTag _pdSaveTag;

        // Returns true while save mode owns the click: the cell shows the
        // overwrite overlay instead of applying/person choices.
        private static bool SavePresetOnClick(PdSlotTag tag, RectTransform cell)
        {
            if (!_pdSaveBrowsing || tag == null || cell == null) return false;
            TogglePdSaveOverlay(tag, cell);
            return true;
        }

        private static void TogglePdSaveOverlay(PdSlotTag tag, RectTransform cell)
        {
            if (_pdSaveOverlay != null && _pdSaveOverlay.activeSelf &&
                _pdSaveOverlay.transform.parent == cell && _pdSaveTag == tag)
            {
                _pdSaveOverlay.SetActive(false);
                _pdSaveTag = null;
                return;
            }
            EnsurePdSaveOverlay();
            if (_pdPersonOverlay != null) _pdPersonOverlay.SetActive(false);
            CancelDockRename();
            _pdSaveTag = tag;
            RectTransform rt = (RectTransform)_pdSaveOverlay.transform;
            rt.SetParent(cell, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(-4f, -4f);
            rt.offsetMax = new Vector2(4f, 4f);
            rt.SetAsLastSibling();
            _pdSaveOverlay.SetActive(true);
            VrHaptics.Press();
        }

        private static void EnsurePdSaveOverlay()
        {
            if (_pdSaveOverlay != null) return;
            GameObject go = new GameObject("PdSaveOverlay", typeof(RectTransform));
            go.AddComponent<PdOverlayTag>();
            go.AddComponent<PdDockTag>();
            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.78f);
            bg.raycastTarget = true;
            RectTransform rt = (RectTransform)go.transform;
            CreatePdSaveButton(rt, "拍 照", new Vector2(-21f, 14f),
                new Vector2(40f, 30f), new Color(0.14f, 0.42f, 0.22f, 1f),
                delegate { DockOverwriteClicked(true); });
            CreatePdSaveButton(rt, "覆 盖", new Vector2(21f, 14f),
                new Vector2(40f, 30f), new Color(0.55f, 0.32f, 0.10f, 1f),
                delegate { DockOverwriteClicked(false); });
            CreatePdSaveButton(rt, "另 存", new Vector2(0f, -20f),
                new Vector2(84f, 22f), new Color(0.13f, 0.24f, 0.32f, 1f),
                delegate
                {
                    if (_pdSaveOverlay != null) _pdSaveOverlay.SetActive(false);
                    _pdSaveTag = null;
                    // The save dialog is already what opened this window —
                    // re-point it at a fresh-name save for this tab.
                    OpenDockPresetSaver();
                });
            _pdSaveOverlay = go;
            go.SetActive(false);
        }

        private static void CreatePdSaveButton(RectTransform parent,
            string label, Vector2 center, Vector2 size, Color color,
            UnityEngine.Events.UnityAction action)
        {
            GameObject go = new GameObject("PdSave " + label,
                typeof(RectTransform));
            go.AddComponent<PdOverlayTag>();
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

        private static void DockOverwriteClicked(bool photo)
        {
            PdSlotTag tag = _pdSaveTag;
            if (_pdSaveOverlay != null) _pdSaveOverlay.SetActive(false);
            _pdSaveTag = null;
            if (tag == null) return;
            SuperController sc = SuperController.singleton;
            Snapshot state = FindEditor(sc);
            if ((state == null || state.Target == null) && _presetBrowsing)
                state = _presetState;
            if (state == null || state.Target == null)
            { Log("保存预设失败：未找到编辑中的角色。"); return; }
            try
            {
                if (_pdQuick == null)
                    _pdQuick = new SceneQuickActions(
                        Quest3TriggerUIPlugin.Instance);
                if (!_pdQuick.DockStorePreset(state.Target, _pdTab, tag.Path))
                    return;
                if (photo)
                    PresetThumbCapture.Queue(tag.Path, delegate
                    {
                        // The sidecar mtime moved — evict the cached decode
                        // so the cell re-reads the fresh thumbnail.
                        _pdThumbStamp.Remove(tag.Path);
                        Texture2D old;
                        if (_pdThumbs.TryGetValue(tag.Path, out old))
                        {
                            _pdThumbs.Remove(tag.Path);
                            if (old != null) UnityEngine.Object.Destroy(old);
                        }
                        if (tag.Thumb != null) { tag.Thumb.texture = null; }
                        ApplyPdThumb(tag);
                    });
                VrHaptics.Confirm();
            }
            catch (Exception e) { Error(e); }
        }

        // ---------- name zone: inline rename ----------

        private static GameObject _pdRenameOverlay;
        private static InputField _pdRenameInput;
        private static string _pdRenamePath;

        private static void BeginDockRename(PdSlotTag tag)
        {
            if (!_pdSaveBrowsing || tag == null || string.IsNullOrEmpty(tag.Path))
                return;
            if (FileManager.IsPackagePath(tag.Path))
            { Log("VAR 包内预设不能改名：" + tag.Path); return; }
            EnsurePdRenameOverlay();
            if (_pdPersonOverlay != null) _pdPersonOverlay.SetActive(false);
            if (_pdSaveOverlay != null) _pdSaveOverlay.SetActive(false);
            _pdSaveTag = null;
            _pdRenamePath = tag.Path;
            RectTransform rt = (RectTransform)_pdRenameOverlay.transform;
            rt.SetParent(tag.transform, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            // Slightly oversize the cell so the input stays readable.
            rt.offsetMin = new Vector2(-6f, -6f);
            rt.offsetMax = new Vector2(6f, 6f);
            rt.SetAsLastSibling();
            string leaf = tag.Path;
            int slash = leaf.LastIndexOf('/');
            if (slash >= 0) leaf = leaf.Substring(slash + 1);
            _pdRenameInput.text = PresetFilenameRules.EditName(leaf);
            _pdRenameOverlay.SetActive(true);
            _pdRenameInput.ActivateInputField();
            VrTextInputBridge.Select(_pdRenameInput);
            VrHaptics.Press();
        }

        private static void EnsurePdRenameOverlay()
        {
            if (_pdRenameOverlay != null) return;
            GameObject go = new GameObject("PdRenameOverlay",
                typeof(RectTransform));
            go.AddComponent<PdOverlayTag>();
            go.AddComponent<PdDockTag>();
            Image bg = go.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.82f);
            bg.raycastTarget = true;
            RectTransform rt = (RectTransform)go.transform;

            GameObject inputGo = new GameObject("Input", typeof(RectTransform));
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
            _pdRenameInput = input;

            CreatePdSaveButton(rt, "✓", new Vector2(-20f, -14f),
                new Vector2(38f, 22f), new Color(0.14f, 0.45f, 0.20f, 1f),
                CommitDockRename);
            CreatePdSaveButton(rt, "✗", new Vector2(20f, -14f),
                new Vector2(38f, 22f), new Color(0.45f, 0.16f, 0.14f, 1f),
                CancelDockRename);
            _pdRenameOverlay = go;
            go.SetActive(false);
        }

        private static void CommitDockRename()
        {
            string path = _pdRenamePath;
            string typed = _pdRenameInput == null ? "" : _pdRenameInput.text;
            if (_pdRenameOverlay != null) _pdRenameOverlay.SetActive(false);
            _pdRenamePath = null;
            VrTextInputBridge.Clear();
            if (string.IsNullOrEmpty(path)) return;
            string leaf = PresetFilenameRules.FileName(typed);
            if (leaf.Length == 0) return;
            string dir = path;
            int slash = path.LastIndexOf('/');
            if (slash >= 0) dir = path.Substring(0, slash);
            string dst = dir + "/" + leaf;
            if (string.Equals(dst, path, StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                FileManager.MoveFile(path, dst, false);
                string srcJpg = path.Substring(0, path.Length - ".vap".Length) + ".jpg";
                string dstJpg = dst.Substring(0, dst.Length - ".vap".Length) + ".jpg";
                try
                {
                    if (FileManager.FileExists(srcJpg, false, false))
                        FileManager.MoveFile(srcJpg, dstJpg, false);
                }
                catch { }
                NotifyDockPresetMoved(path, dst, false);
                VrHaptics.Confirm();
            }
            catch (Exception e)
            {
                Log("预设改名失败：" + e.Message);
            }
        }

        private static void CancelDockRename()
        {
            if (_pdRenameOverlay != null) _pdRenameOverlay.SetActive(false);
            _pdRenamePath = null;
            if (_pdRenameInput != null) VrTextInputBridge.Clear();
        }
    }

    // Sidecar .jpg capture for preset saves — replaces VaM's aim-and-select
    // screenshot mode (its first shot was being eaten as a skip). Grabs the
    // frame the eye camera already rendered; the stereo targets stay on
    // their normal pass.
    internal static class PresetThumbCapture
    {
        private sealed class Job
        {
            internal string JpgPath;
            internal float Deadline;
            internal Action<string> Done;
        }

        private static readonly List<Job> _jobs = new List<Job>();
        private static bool _hooked;

        internal static void Queue(string vapPath, Action<string> done)
        {
            if (string.IsNullOrEmpty(vapPath)) return;
            string jpg;
            try { jpg = FileManager.GetFullPath(vapPath); }
            catch { return; }
            if (string.IsNullOrEmpty(jpg)) return;
            if (jpg.EndsWith(".vap", StringComparison.OrdinalIgnoreCase))
                jpg = jpg.Substring(0, jpg.Length - 4) + ".jpg";
            else return;
            _jobs.Add(new Job
            {
                JpgPath = jpg, Deadline = Time.unscaledTime + 3f, Done = done
            });
            if (!_hooked)
            {
                Camera.onPostRender += OnPostRender;
                _hooked = true;
            }
        }

        private static void OnPostRender(Camera camera)
        {
            SuperController sc = SuperController.singleton;
            if (camera == null || sc == null || camera != sc.lookCamera) return;
            float now = Time.unscaledTime;
            for (int i = _jobs.Count - 1; i >= 0; i--)
                if (now > _jobs[i].Deadline) _jobs.RemoveAt(i);
            if (_jobs.Count == 0) { Camera.onPostRender -= OnPostRender; _hooked = false; return; }
            List<Job> batch = new List<Job>(_jobs);
            _jobs.Clear();
            if (batch.Count == 0) { Camera.onPostRender -= OnPostRender; _hooked = false; return; }
            RenderTexture previous = RenderTexture.active;
            RenderTexture small = null;
            Texture2D pixels = null, thumb = null;
            byte[] jpg = null;
            try
            {
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
                jpg = ImageConversion.EncodeToJPG(thumb, 85);
                if (jpg == null || jpg.Length == 0) return;
                foreach (Job job in batch)
                {
                    try
                    {
                        FileManager.WriteAllBytes(job.JpgPath, jpg);
                        if (sc.fileBrowserUI != null)
                            sc.fileBrowserUI.ClearCacheImage(job.JpgPath);
                        if (job.Done != null) job.Done(job.JpgPath);
                    }
                    catch { }
                }
            }
            catch (Exception e)
            {
                if (Quest3TriggerUIPlugin.Log != null)
                    Quest3TriggerUIPlugin.Log.LogWarning("preset thumb capture failed: " + e.Message);
            }
            finally
            {
                RenderTexture.active = previous;
                if (small != null) RenderTexture.ReleaseTemporary(small);
                if (pixels != null) UnityEngine.Object.Destroy(pixels);
                if (thumb != null) UnityEngine.Object.Destroy(thumb);
            }
        }
    }
}

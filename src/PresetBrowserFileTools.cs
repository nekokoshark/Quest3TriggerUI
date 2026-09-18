using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using MVR.FileManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using uFileBrowser;

namespace Quest3TriggerUI
{
    internal sealed class PresetBrowserFileTools
    {
        private static PresetBrowserFileTools _active;
        private FileBrowser _browser;
        private GameObject _root;
        private InputField _folderName;
        private Button _moveButton;
        private string _pendingMoveSource;
        private bool _browserWasVisible;
        private bool _choosingMoveFile;
        private bool _previousSelectOnClick;
        private Coroutine _refreshCoroutine;

        internal PresetBrowserFileTools()
        {
            _active = this;
        }

        internal static void AttachShownBrowser(FileBrowser browser)
        {
            if (_active != null && IsPresetBrowser(browser) &&
                SuperController.singleton != null)
                _active.Build(browser);
        }

        internal void Tick()
        {
            FileBrowser browser = IsBrowserVisible(_browser) ? _browser : ChooseBrowser(SuperController.singleton);
            if (browser == null)
            {
                if (_browserWasVisible)
                    CancelMove();
                _browserWasVisible = false;
                if (_root != null)
                    _root.SetActive(false);
                return;
            }

            if (_browser != browser || _root == null)
                Build(browser);
            if (_root != null)
                _root.SetActive(true);

            bool visible = browser.window != null && browser.window.activeInHierarchy;
            if (_browserWasVisible && !visible)
                CancelMove();
            _browserWasVisible = visible;
        }

        private static FileBrowser ChooseBrowser(SuperController controller)
        {
            if (controller == null)
                return null;

            // JSONStorableUrl.FileBrowse -> GetMediaPathDialog uses this
            // instance for Person/Clothing presets, not the scene/start browser.
            FileBrowser media = controller.mediaFileBrowserUI;
            return IsBrowserVisible(media) && IsPresetBrowser(media) ? media : null;
        }

        private static bool IsPresetBrowser(FileBrowser browser)
        {
            return browser != null;
        }

        private static bool IsBrowserVisible(FileBrowser browser)
        {
            return browser != null && browser.window != null &&
                   browser.window.activeInHierarchy;
        }

        internal void Dispose()
        {
            if (_refreshCoroutine != null && Quest3TriggerUIPlugin.Instance != null)
                Quest3TriggerUIPlugin.Instance.StopCoroutine(_refreshCoroutine);
            _refreshCoroutine = null;
            CancelMove();
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            _root = null;
            _browser = null;
            if (_active == this)
                _active = null;
        }

        private void Build(FileBrowser browser)
        {
            CancelMove();
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
            _root = null;
            _folderName = null;
            _moveButton = null;

            _browser = browser;
            if (browser.window == null)
                return;

            // Own the toolbar under the popup itself. Parenting to an ancestor
            // Canvas leaves it visible on the start screen after the popup hides.
            Canvas parentCanvas = browser.window.GetComponentInParent<Canvas>();
            Transform uiParent = browser.window.transform;
            Font font = ResolveFont(browser);
            _root = new GameObject(
                "Quest3PresetBrowserFileTools", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image),
                typeof(LayoutElement));
            RectTransform rootRect = _root.GetComponent<RectTransform>();
            rootRect.SetParent(uiParent, false);
            rootRect.anchorMin = new Vector2(0.5f, 1f);
            rootRect.anchorMax = new Vector2(0.5f, 1f);
            rootRect.pivot = new Vector2(0.5f, 1f);
            // Keep the row inside the popup's visible rectangle.  The previous
            // bottom-pivot/+4 placement put the entire row above the window, so
            // the preset browser's clipping hierarchy could hide it completely.
            rootRect.anchoredPosition = new Vector2(0f, -8f);
            rootRect.sizeDelta = new Vector2(1220f, 92f);
            rootRect.localScale = Vector3.one;
            rootRect.localRotation = Quaternion.identity;
            _root.GetComponent<LayoutElement>().ignoreLayout = true;
            Image background = _root.GetComponent<Image>();
            background.color = new Color(0.06f, 0.07f, 0.09f, 0.92f);

            _folderName = CreateInput(rootRect, font, "新文件夹名称");
            SetRect(_folderName.GetComponent<RectTransform>(), 12f, -12f, 470f, 68f);

            Button createButton = CreateButton(rootRect, font, "新建文件夹");
            SetRect(createButton.GetComponent<RectTransform>(), 494f, -12f, 300f, 68f);
            createButton.onClick.AddListener(CreateFolder);

            _moveButton = CreateButton(rootRect, font, "移动文件");
            SetRect(_moveButton.GetComponent<RectTransform>(), 806f, -12f, 402f, 68f);
            _moveButton.onClick.AddListener(ToggleMove);

            rootRect.SetAsLastSibling();
            _root.SetActive(true);

            RectTransform windowRect = browser.window.GetComponent<RectTransform>();
            LogInfo(
                "Preset browser tools attached: browser=" + browser.name +
                ", window=" + browser.window.name +
                ", windowSize=" +
                (windowRect == null ? "unknown" : windowRect.rect.size.ToString()) +
                ", uiParent=" + uiParent.name +
                ", canvas=" + (parentCanvas == null ? "none" : parentCanvas.name) +
                ", renderLayer=inherited, sibling=" + rootRect.GetSiblingIndex() +
                ", visible=" + browser.window.activeInHierarchy + ".");
        }

        private static Font ResolveFont(FileBrowser browser)
        {
            Text text = browser.titleText;
            if (text == null && browser.statusField != null)
                text = browser.statusField;
            if (text == null)
                text = browser.window.GetComponentInChildren<Text>(true);
            return text != null && text.font != null
                ? text.font
                : Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        private static InputField CreateInput(Transform parent, Font font, string placeholderText)
        {
            GameObject inputObject = new GameObject(
                "Quest3NewFolderName", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image), typeof(InputField));
            inputObject.transform.SetParent(parent, false);
            Image background = inputObject.GetComponent<Image>();
            background.color = new Color(0.17f, 0.19f, 0.24f, 0.98f);

            Text text = CreateText(inputObject.transform, font, string.Empty,
                TextAnchor.MiddleLeft, Color.white);
            Text placeholder = CreateText(inputObject.transform, font, placeholderText,
                TextAnchor.MiddleLeft, new Color(0.75f, 0.78f, 0.84f, 0.85f));
            SetTextInsets(text.rectTransform, 16f, 12f);
            SetTextInsets(placeholder.rectTransform, 16f, 12f);

            InputField input = inputObject.GetComponent<InputField>();
            input.targetGraphic = background;
            input.textComponent = text;
            input.placeholder = placeholder;
            input.lineType = InputField.LineType.SingleLine;
            input.characterLimit = 80;
            input.text = string.Empty;
            return input;
        }

        private static Button CreateButton(Transform parent, Font font, string label)
        {
            GameObject buttonObject = new GameObject(
                "Quest3" + label, typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);
            Image image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.12f, 0.35f, 0.57f, 0.98f);
            Button button = buttonObject.GetComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 0.72f, 0.55f, 1f);
            colors.pressedColor = new Color(1f, 0.48f, 0.30f, 1f);
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.7f);
            colors.colorMultiplier = 1f;
            button.colors = colors;
            CreateText(buttonObject.transform, font, label,
                TextAnchor.MiddleCenter, Color.white);
            return button;
        }

        private static Text CreateText(
            Transform parent, Font font, string value,
            TextAnchor alignment, Color color)
        {
            GameObject textObject = new GameObject(
                "Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            textObject.transform.SetParent(parent, false);
            RectTransform rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            Text text = textObject.GetComponent<Text>();
            text.font = font;
            text.fontSize = 28;
            text.alignment = alignment;
            text.color = color;
            text.text = value;
            text.raycastTarget = false;
            return text;
        }

        private static void SetTextInsets(RectTransform rect, float horizontal, float vertical)
        {
            rect.offsetMin = new Vector2(horizontal, vertical);
            rect.offsetMax = new Vector2(-horizontal, -vertical);
        }

        private static void SetRect(
            RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);
            rect.localScale = Vector3.one;
        }

        private static void SetButtonLabel(Button button, string label)
        {
            Text text = button.GetComponentInChildren<Text>(true);
            if (text != null)
                text.text = label;
        }

        private void CreateFolder()
        {
            if (_browser == null || _folderName == null)
                return;

            string current = _browser.CurrentPath;
            string name = (_folderName.text ?? string.Empty).Trim();
            LogInfo("Preset browser create-folder requested: path=" + current +
                    ", name=" + name + ".");
            if (!CanCreateDirectory(current))
            {
                SetStatus("当前目录位于 VAR 包内，不能新建文件夹。");
                return;
            }
            if (!IsValidFolderName(name))
            {
                SetStatus("请输入不含路径符号的有效文件夹名称。");
                return;
            }

            string path = CombineBrowserPath(current, name);
            if (FileManager.DirectoryExists(path, false, false))
            {
                SetStatus("文件夹已经存在：" + name);
                return;
            }

            try
            {
                Directory.CreateDirectory(FileManager.GetFullPath(path));
                _folderName.text = string.Empty;
                RefreshCurrentDirectory("已新建文件夹：" + name);
                LogInfo("Preset browser create-folder succeeded: " + path + ".");
            }
            catch (Exception exception)
            {
                SetStatus("新建文件夹失败：" + exception.Message);
                LogError("Preset browser create-folder exception: " + exception);
            }
        }

        private void ToggleMove()
        {
            if (_browser == null)
                return;

            if (string.IsNullOrEmpty(_pendingMoveSource))
            {
                if (!_choosingMoveFile)
                {
                    _previousSelectOnClick = _browser.selectOnClick;
                    _browser.selectOnClick = false;
                    _choosingMoveFile = true;
                    SetButtonLabel(_moveButton, "选文件后点此");
                    SetStatus("移动模式：点选预设不会加载；选好后再次点此按钮。");
                    return;
                }
                string selected = _browser.SelectedPath;
                if (string.IsNullOrEmpty(selected) || !LocalFileExists(selected))
                {
                    SetStatus("请先点选一个本地文件，再按“移动文件”。");
                    return;
                }
                if (FileManager.IsFileInPackage(selected))
                {
                    SetStatus("VAR 包内的预设是只读的，不能移动。");
                    return;
                }
                if (!CanCreateDirectory(Path.GetDirectoryName(FileManager.GetFullPath(selected))))
                {
                    SetStatus("请选择现有本地目录中的文件。");
                    return;
                }

                _pendingMoveSource = selected;
                SetButtonLabel(_moveButton, "移动到这里");
                SetStatus("已选择 " + Path.GetFileName(selected) +
                          "；进入目标文件夹后按“移动到这里”。");
                return;
            }

            MovePendingFile();
        }

        private void MovePendingFile()
        {
            string current = _browser.CurrentPath;
            if (!CanCreateDirectory(current))
            {
                SetStatus("目标目录位于 VAR 包内，不能移动到这里。");
                return;
            }

            List<KeyValuePair<string, string>> moves = BuildMoveList(
                _pendingMoveSource, current);
            if (moves.Count == 0)
            {
                SetStatus("找不到待移动的预设文件。");
                CancelMove();
                return;
            }

            for (int i = 0; i < moves.Count; i++)
            {
                if (PathsEqual(moves[i].Key, moves[i].Value))
                {
                    SetStatus("该预设已经位于当前文件夹。");
                    return;
                }
                if (LocalFileExists(moves[i].Value))
                {
                    SetStatus("目标文件夹已有同名文件：" +
                              Path.GetFileName(moves[i].Value));
                    return;
                }
            }

            int completed = 0;
            try
            {
                for (; completed < moves.Count; completed++)
                    MoveLocalFile(moves[completed].Key, moves[completed].Value);

                string movedName = Path.GetFileName(_pendingMoveSource);
                CancelMove();
                RefreshCurrentDirectory("已移动预设及其缩略图：" + movedName);
            }
            catch (Exception exception)
            {
                RollBackMoves(moves, completed);
                SetStatus("移动失败，已恢复原位置：" + exception.Message);
                LogError("Preset browser move exception: " + exception);
            }
        }

        private static List<KeyValuePair<string, string>> BuildMoveList(
            string source, string destinationDirectory)
        {
            List<KeyValuePair<string, string>> result =
                new List<KeyValuePair<string, string>>();
            AddMoveIfPresent(result, source, destinationDirectory);
            AddMoveIfPresent(result, Path.ChangeExtension(source, ".jpg"), destinationDirectory);
            AddMoveIfPresent(result, Path.ChangeExtension(source, ".png"), destinationDirectory);
            AddMoveIfPresent(result, source + ".hide", destinationDirectory);
            return result;
        }

        private static void AddMoveIfPresent(
            List<KeyValuePair<string, string>> moves,
            string source,
            string destinationDirectory)
        {
            if (!LocalFileExists(source))
                return;
            moves.Add(new KeyValuePair<string, string>(
                source,
                CombineBrowserPath(destinationDirectory, Path.GetFileName(source))));
        }

        private static void RollBackMoves(
            List<KeyValuePair<string, string>> moves, int completed)
        {
            for (int i = completed - 1; i >= 0; i--)
            {
                try
                {
                    if (LocalFileExists(moves[i].Value) && !LocalFileExists(moves[i].Key))
                        MoveLocalFile(moves[i].Value, moves[i].Key);
                }
                catch (Exception rollbackException)
                {
                    LogError("Preset browser move rollback exception: " + rollbackException);
                }
            }
        }

        private void RefreshCurrentDirectory(string completionStatus)
        {
            if (_browser == null || string.IsNullOrEmpty(_browser.CurrentPath) ||
                Quest3TriggerUIPlugin.Instance == null)
                return;
            if (_refreshCoroutine != null)
                Quest3TriggerUIPlugin.Instance.StopCoroutine(_refreshCoroutine);
            _refreshCoroutine = Quest3TriggerUIPlugin.Instance.StartCoroutine(
                RefreshCurrentDirectoryNextFrame(
                    _browser, _browser.CurrentPath, completionStatus));
        }

        private IEnumerator RefreshCurrentDirectoryNextFrame(
            FileBrowser browser, string path, string completionStatus)
        {
            // Let the VR EventSystem finish the current pointer-up/click dispatch
            // before the browser rebuilds its file buttons. Rebuilding inline can
            // leave the pointer attached to stale UI after the first operation.
            yield return null;
            _refreshCoroutine = null;
            if (browser == null || browser != _browser ||
                !string.Equals(browser.CurrentPath, path,
                    StringComparison.OrdinalIgnoreCase))
                yield break;

            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);
            browser.GotoDirectory(path, string.Empty, false, true);
            if (_root != null)
            {
                _root.SetActive(true);
                _root.transform.SetAsLastSibling();
            }
            SetStatus(completionStatus);
        }

        private void CancelMove()
        {
            if (_choosingMoveFile && _browser != null)
                _browser.selectOnClick = _previousSelectOnClick;
            _choosingMoveFile = false;
            _pendingMoveSource = null;
            if (_moveButton != null)
                SetButtonLabel(_moveButton, "移动文件");
        }

        private void SetStatus(string message)
        {
            if (_browser != null && _browser.statusField != null)
                _browser.statusField.text = message;
        }

        private static bool IsValidFolderName(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "." || name == "..")
                return false;
            return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                   name.IndexOf('/') < 0 && name.IndexOf('\\') < 0;
        }

        private static bool CanCreateDirectory(string path)
        {
            return !string.IsNullOrEmpty(path) && !FileManager.IsPackagePath(path) &&
                !FileManager.IsDirectoryInPackage(path) && Directory.Exists(FileManager.GetFullPath(path));
        }

        private static bool CanModifyDirectory(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   !FileManager.IsDirectoryInPackage(path) &&
                   IsInsideManagedPresetRoot(path);
        }

        private static bool LocalFileExists(string path)
        {
            if (string.IsNullOrEmpty(path) || FileManager.IsFileInPackage(path))
                return false;
            return File.Exists(FileManager.GetFullPath(path));
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                FileManager.GetFullPath(left), FileManager.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInsideManagedPresetRoot(string path)
        {
            return IsInsidePresetRoot(path, PluginPaths.AppearancePresetDir) ||
                   IsInsidePresetRoot(path, PluginPaths.ClothingPresetDir) ||
                   IsInsidePresetRoot(path, PluginPaths.SkinPresetDir);
        }

        private static bool IsInsidePresetRoot(string path, string presetRoot)
        {
            if (string.IsNullOrEmpty(path) || FileManager.IsPackagePath(path))
                return false;

            string root = Path.GetFullPath(FileManager.GetFullPath(presetRoot));
            string candidate = Path.GetFullPath(FileManager.GetFullPath(path));
            return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase) ||
                   candidate.StartsWith(
                       root.TrimEnd(Path.DirectorySeparatorChar) +
                       Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static void MoveLocalFile(string source, string destination)
        {
            File.Move(
                FileManager.GetFullPath(source),
                FileManager.GetFullPath(destination));
        }

        private static string CombineBrowserPath(string directory, string name)
        {
            return directory.TrimEnd('/', '\\') + "/" + name;
        }

        private static void LogError(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogError(message);
            else
                SuperController.LogError(message);
        }

        private static void LogInfo(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUIPlugin.Log.LogInfo(message);
        }
    }

    [HarmonyPatch(typeof(FileBrowser), "ShowInternal")]
    internal static class PresetBrowserShowInternalPatch
    {
        [HarmonyPostfix]
        private static void Postfix(FileBrowser __instance)
        {
            PresetBrowserFileTools.AttachShownBrowser(__instance);
        }
    }
}



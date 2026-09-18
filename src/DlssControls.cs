using System;
using System.Reflection;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    // Uses the same ConfigEntry instances as the F10 panel, not a second config file.
    internal sealed class DlssControlBridge
    {
        internal ConfigEntry<bool> Sr, Nr;
        internal readonly ConfigEntry<float>[] Parameters = new ConfigEntry<float>[3];
        private MethodInfo _save;
        private float _nextResolve;
        internal bool Resolve()
        {
            if (Sr != null && Nr != null) return true;
            if (Time.unscaledTime < _nextResolve) return false;
            _nextResolve = Time.unscaledTime + 1f;
            Type type = AssemblyCatalog.FindType("VamDlssNr.VamDlssNrPlugin");
            if (type == null) return false;
            ConfigEntry<bool> sr = Read<bool>(type, "CfgSrEnabled");
            ConfigEntry<bool> nr = Read<bool>(type, "CfgEnabled");
            ConfigEntry<float> strength = Read<float>(type, "CfgIntensity");
            ConfigEntry<float> tone = Read<float>(type, "CfgLocalTone");
            ConfigEntry<float> structure = Read<float>(type, "CfgLocalStructure");
            if (sr == null || nr == null || strength == null || tone == null || structure == null)
                return false;
            Parameters[0] = strength; Parameters[1] = tone; Parameters[2] = structure;
            _save = type.GetMethod("SaveNow", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Sr = sr; Nr = nr;
            return true;
        }
        private static ConfigEntry<T> Read<T>(Type type, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return field == null ? null : field.GetValue(null) as ConfigEntry<T>;
        }
        internal bool SrActive { get { return Resolve() && Sr.Value; } }
        internal bool NrActive { get { return Resolve() && Nr.Value; } }
        internal void Toggle(bool nr)
        {
            if (!Resolve()) throw new InvalidOperationException("DLSS 插件尚未加载或接口不匹配");
            ConfigEntry<bool> entry = nr ? Nr : Sr;
            entry.Value = !entry.Value;
            if (_save != null) _save.Invoke(null, null);
        }
        internal static void Range(ConfigEntry<float> entry, out float min, out float max)
        {
            // Identical to VamDlssNrPanel.RangeOf.
            AcceptableValueRange<float> range = entry.Description == null ? null :
                entry.Description.AcceptableValues as AcceptableValueRange<float>;
            min = range == null ? 0f : range.MinValue;
            max = range == null ? 1f : range.MaxValue;
        }
    }

    internal sealed partial class VrKeyboardOverlay
    {
        private readonly DlssControlBridge _dlss = new DlssControlBridge();
        private GameObject _dlssMenu, _dlssStrengthPanel;
        private ShortcutActionButton _dlssRootButton;
        private readonly Slider[] _dlssSliders = new Slider[3];
        private readonly Text[] _dlssValues = new Text[3];
        private readonly ShortcutActionButton[] _dlssButtons = new ShortcutActionButton[2];
        private readonly string[] _dlssLabels = { "强度", "Local Tone", "Local structure" };
        private bool _syncingDlss;

        private QuickActionDefinition BuildDlssAction()
        {
            return new QuickActionDefinition("dlss", "DLSS", ToggleDlssMenu,
                delegate { return _dlss.SrActive || _dlss.NrActive; },
                new List<QuickActionDefinition> {
                    new QuickActionDefinition("dlss.sr", "超采样", delegate { ToggleDlss(false); },
                        delegate { return _dlss.SrActive; }),
                    new QuickActionDefinition("dlss.nr", "AI滤镜", delegate { ToggleDlss(true); },
                        delegate { return _dlss.NrActive; })
                });
        }
        private void ToggleDlss(bool nr)
        {
            try
            {
                _dlss.Toggle(nr);
                UpdateHelpText((nr ? "AI滤镜 (DLSS NR)" : "DLSS 超采样") +
                    ((nr ? _dlss.NrActive : _dlss.SrActive) ? " 已开启" : " 已关闭"));
                SyncDlssControls();
            }
            catch (Exception exception)
            {
                UpdateHelpText("DLSS 控制失败：" + exception.Message);
                UnityEngine.Debug.LogError("[Quest3TriggerUI DLSS] " + exception);
            }
        }
        private void ToggleDlssMenu()
        {
            if (_canvas == null) Build();
            if (!Visible) Toggle();
            if (_dlssMenu == null)
            {
                _dlssMenu = CreateUiObject("DLSS controls", _canvas.GetComponent<RectTransform>());
                RectTransform rect = _dlssMenu.GetComponent<RectTransform>();
                SetTopLeft(rect, 10f, -100f, 560f, 95f);
                _dlssMenu.AddComponent<Image>().color = new Color(0.04f, 0.06f, 0.09f, 0.98f);
                _dlssButtons[0] = CreateBindableActionButton(rect, "超采样", 10f, 10f, 265f, 75f,
                    delegate { ToggleDlss(false); });
                _dlssButtons[1] = CreateBindableActionButton(rect, "AI滤镜", 285f, 10f, 265f, 75f,
                    delegate { ToggleDlss(true); });
                SetLayerRecursively(_dlssMenu, ResolveUiLayer());
            }
            else _dlssMenu.SetActive(!_dlssMenu.activeSelf);
            SyncDlssControls();
        }
        private void SyncDlssControls()
        {
            if (_canvas == null) return;
            bool nr = _dlss.NrActive;
            if (_dlssRootButton != null)
                _dlssRootButton.SetNormalColor((_dlss.SrActive || nr)
                    ? new Color(0.08f, 0.68f, 0.36f, 1f) : new Color(0.11f, 0.38f, 0.48f, 1f));
            for (int i = 0; i < 2; i++)
                if (_dlssButtons[i] != null)
                    _dlssButtons[i].SetNormalColor((i == 0 ? _dlss.SrActive : nr)
                        ? new Color(0.08f, 0.68f, 0.36f, 1f) : new Color(0.11f, 0.38f, 0.48f, 1f));
            if (nr && _dlssStrengthPanel == null) CreateDlssStrengthPanel();
            if (_dlssStrengthPanel == null) return;
            _dlssStrengthPanel.SetActive(nr);
            if (!nr) return;
            _syncingDlss = true;
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    _dlssSliders[i].value = _dlss.Parameters[i].Value;
                    _dlssValues[i].text = _dlssLabels[i] + "  " + _dlss.Parameters[i].Value.ToString("0.00");
                }
            }
            finally { _syncingDlss = false; }
        }
        private void CreateDlssStrengthPanel()
        {
            _dlssStrengthPanel = CreateUiObject("DLSS5 Strength", _canvas.GetComponent<RectTransform>());
            RectTransform panel = _dlssStrengthPanel.GetComponent<RectTransform>();
            // Above the right column, outside eye-gap, bindings and existing popup panels.
            SetTopLeft(panel, 1840f, -410f, 400f, 410f);
            _dlssStrengthPanel.AddComponent<Image>().color = new Color(0.04f, 0.06f, 0.09f, 0.98f);
            GameObject title = CreateUiObject("Title", panel);
            SetTopLeft(title.GetComponent<RectTransform>(), 10f, 5f, 380f, 40f);
            AddText(title.transform, "DLSS5 Strength", 28, TextAnchor.MiddleCenter, Color.white, 4f);
            for (int i = 0; i < 3; i++)
            {
                int index = i;
                GameObject label = CreateUiObject(_dlssLabels[i], panel);
                SetTopLeft(label.GetComponent<RectTransform>(), 10f, 50f + i * 115f, 380f, 40f);
                _dlssValues[i] = AddText(label.transform, _dlssLabels[i], 26, TextAnchor.MiddleCenter, Color.white, 4f);
                GameObject track = CreateUiObject("Slider " + i, panel);
                RectTransform rect = track.GetComponent<RectTransform>();
                SetTopLeft(rect, 20f, 95f + i * 115f, 360f, 55f);
                track.AddComponent<Image>().color = new Color(0.10f, 0.25f, 0.32f, 1f);
                GameObject area = CreateUiObject("Handle area", rect);
                RectTransform areaRect = area.GetComponent<RectTransform>();
                Stretch(areaRect);
                areaRect.offsetMin = new Vector2(20f, 0f);
                areaRect.offsetMax = new Vector2(-20f, 0f);
                GameObject handle = CreateUiObject("Handle", areaRect);
                RectTransform handleRect = handle.GetComponent<RectTransform>();
                handleRect.sizeDelta = new Vector2(40f, 60f);
                Image graphic = handle.AddComponent<Image>();
                graphic.color = Color.white;
                Slider slider = track.AddComponent<Slider>();
                slider.direction = Slider.Direction.LeftToRight;
                float min, max;
                DlssControlBridge.Range(_dlss.Parameters[i], out min, out max);
                slider.minValue = min; slider.maxValue = max;
                slider.handleRect = handleRect; slider.targetGraphic = graphic;
                slider.value = _dlss.Parameters[i].Value;
                slider.onValueChanged.AddListener(delegate(float value) {
                    if (_syncingDlss) return;
                    _dlss.Parameters[index].Value = value;
                    _dlssValues[index].text = _dlssLabels[index] + "  " + _dlss.Parameters[index].Value.ToString("0.00");
                });
                _dlssSliders[i] = slider;
            }
            SetLayerRecursively(_dlssStrengthPanel, ResolveUiLayer());
        }
    }
}

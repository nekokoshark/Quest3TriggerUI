using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    internal static partial class UiAssistHudLink
    {
        private static bool _dockDeleteMode;
        private static Image _dockDeleteButton;
        private static Text _dockDeleteLabel;

        private static void ToggleDockDeleteMode()
        {
            _dockDeleteMode = !_dockDeleteMode;
            if (_pdPersonOverlay != null) _pdPersonOverlay.SetActive(false);
            PaintDockDeleteMode();
        }

        private static void PaintDockDeleteMode()
        {
            if (_dockDeleteButton != null)
                _dockDeleteButton.color = _dockDeleteMode
                    ? new Color(0.72f, 0.12f, 0.10f, 1f)
                    : new Color(0.28f, 0.15f, 0.15f, 1f);
            if (_dockDeleteLabel != null)
                _dockDeleteLabel.text = _dockDeleteMode ? "结束删除" : "删 除";
        }

        private static bool DeleteFavoriteOnClick(string uid)
        {
            if (!_dockDeleteMode) return false;
            if (RemoveFavorite(uid, true)) VrHaptics.Confirm();
            return true; // Never fall through to wearing clothing in this mode.
        }

        private static bool DeletePresetOnClick(string path)
        {
            if (!_dockDeleteMode) return false;
            if (RemoveDockSlot(_pdTab, path)) VrHaptics.Confirm();
            return true; // Removes this tab's reference, not the preset file.
        }
    }
}

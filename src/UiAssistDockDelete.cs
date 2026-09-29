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
            // While a save session owns dock clicks, deletion stays off.
            if (!_dockDeleteMode && (_pdSaveBrowsing || _sdSaveBrowsing))
                return;
            _dockDeleteMode = !_dockDeleteMode;
            if (_pdPersonOverlay != null) _pdPersonOverlay.SetActive(false);
            CancelDockRename();
            if (_pdSaveOverlay != null) _pdSaveOverlay.SetActive(false);
            _pdSaveTag = null;
            if (_favTagEditing != null) CommitTagRename();
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
            // The clothing strip rebuilds its rows per view switch — the
            // fav-side button is a second painted handle on the same mode.
            if (_favDeleteButton != null)
                _favDeleteButton.color = _dockDeleteMode
                    ? new Color(0.72f, 0.12f, 0.10f, 1f)
                    : new Color(0.28f, 0.15f, 0.15f, 1f);
            if (_favDeleteLabel != null)
                _favDeleteLabel.text = _dockDeleteMode ? "结束删除" : "删 除";
            // The scene dock's strip paints the same shared mode.
            if (_sdDeleteButton != null)
                _sdDeleteButton.color = _dockDeleteMode
                    ? new Color(0.72f, 0.12f, 0.10f, 1f)
                    : new Color(0.28f, 0.15f, 0.15f, 1f);
            if (_sdDeleteLabel != null)
                _sdDeleteLabel.text = _dockDeleteMode ? "结束删除" : "删 除";
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

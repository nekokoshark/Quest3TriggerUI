using System.Collections.Generic;
using UnityEngine;

namespace Quest3TriggerUI
{
    internal static partial class UiAssistHudLink
    {
        // Retain only the current page. A reorder moves existing objects;
        // it must not rebuild 56 buttons or re-read 56 thumbnail files.
        private static readonly List<AceFavSlotTag> _favVisibleCells = new List<AceFavSlotTag>();
        private static readonly HashSet<AceFavSlotTag> _favKeptCells = new HashSet<AceFavSlotTag>();
        private static readonly List<PdSlotTag> _pdVisibleCells = new List<PdSlotTag>();
        private static readonly HashSet<PdSlotTag> _pdKeptCells = new HashSet<PdSlotTag>();
        private static int _favCellPosition, _pdCellPosition;
        private static GameObject _favHintCell, _pdHintCell;
        private static bool _favPreviewDirty, _pdPreviewDirty;

        private static bool ReuseFavoriteCell(string uid)
        {
            for (int i = 0; i < _favVisibleCells.Count; i++)
            {
                AceFavSlotTag tag = _favVisibleCells[i];
                if (tag == null || tag.Uid != uid || _favKeptCells.Contains(tag)) continue;
                PlaceFavoriteCell(tag);
                if (_favDirty) QueueFavoriteVisual(tag);
                return true;
            }
            return false;
        }

        private static void PlaceFavoriteCell(AceFavSlotTag tag)
        {
            _favKeptCells.Add(tag);
            if (tag.transform.GetSiblingIndex() != _favCellPosition)
                tag.transform.SetSiblingIndex(_favCellPosition);
            _favCellPosition++;
        }

        private static void FinishFavoriteCells()
        {
            for (int i = _favVisibleCells.Count - 1; i >= 0; i--)
            {
                AceFavSlotTag tag = _favVisibleCells[i];
                if (tag != null && _favKeptCells.Contains(tag)) continue;
                if (tag != null)
                {
                    tag.gameObject.SetActive(false);
                    Object.Destroy(tag.gameObject);
                }
                _favVisibleCells.RemoveAt(i);
            }
            if (_favKeptCells.Count > 0 && _favHintCell != null)
            {
                _favHintCell.SetActive(false);
                Object.Destroy(_favHintCell);
                _favHintCell = null;
            }
            _favPreviewDirty = false;
            int pending = _favVisualQueue.Count;
            while (pending-- > 0)
            {
                AceFavSlotTag tag = _favVisualQueue.Dequeue();
                if (tag != null && _favKeptCells.Contains(tag)) _favVisualQueue.Enqueue(tag);
                else _favVisualQueued.Remove(tag);
            }
        }

        private static bool ReusePdCell(string path)
        {
            for (int i = 0; i < _pdVisibleCells.Count; i++)
            {
                PdSlotTag tag = _pdVisibleCells[i];
                if (tag == null || tag.Path != path || _pdKeptCells.Contains(tag)) continue;
                PlacePdCell(tag);
                if (_pdDirty) ApplyPdThumb(tag);
                return true;
            }
            return false;
        }

        private static void PlacePdCell(PdSlotTag tag)
        {
            _pdKeptCells.Add(tag);
            if (tag.transform.GetSiblingIndex() != _pdCellPosition)
                tag.transform.SetSiblingIndex(_pdCellPosition);
            _pdCellPosition++;
        }

        private static void FinishPdCells()
        {
            for (int i = _pdVisibleCells.Count - 1; i >= 0; i--)
            {
                PdSlotTag tag = _pdVisibleCells[i];
                if (tag != null && _pdKeptCells.Contains(tag)) continue;
                if (tag != null)
                {
                    tag.gameObject.SetActive(false);
                    Object.Destroy(tag.gameObject);
                }
                _pdVisibleCells.RemoveAt(i);
            }
            if (_pdKeptCells.Count > 0 && _pdHintCell != null)
            {
                _pdHintCell.SetActive(false);
                Object.Destroy(_pdHintCell);
                _pdHintCell = null;
            }
            _pdPreviewDirty = false;
            // Keep pending work bounded to the current page even when users
            // flip faster than thumbnails can be decoded.
            int pending = _pdThumbQueue.Count;
            while (pending-- > 0)
            {
                PdSlotTag tag = _pdThumbQueue.Dequeue();
                if (tag != null && _pdKeptCells.Contains(tag)) _pdThumbQueue.Enqueue(tag);
                else _pdThumbQueued.Remove(tag);
            }
        }

        private static readonly Queue<AceFavSlotTag> _favVisualQueue = new Queue<AceFavSlotTag>();
        private static readonly HashSet<AceFavSlotTag> _favVisualQueued = new HashSet<AceFavSlotTag>();

        private static void QueueFavoriteVisual(AceFavSlotTag tag)
        {
            Texture2D cached;
            if (_favThumbs.TryGetValue(tag.Uid, out cached) && cached != null)
            {
                tag.Thumb.texture = cached;
                tag.Thumb.color = Color.white;
            }
            // Cells outside the masked viewport stay unqueued — scrolling
            // pumps them in via PumpVisibleFavVisuals, so a long list only
            // ever decodes the rows the user can actually see.
            if (!DockCellVisible(tag.transform.GetSiblingIndex(),
                FavCellH, _favScrollY, FavGridH)) return;
            if (_favVisualQueued.Add(tag)) _favVisualQueue.Enqueue(tag);
        }

        private static void TickFavoriteVisuals()
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int work = 0;
            while (_favVisualQueue.Count > 0 && work < 2)
            {
                AceFavSlotTag tag = _favVisualQueue.Dequeue();
                _favVisualQueued.Remove(tag);
                work++;
                if (tag == null || !_favKeptCells.Contains(tag)) continue;
                RefreshSlotVisual(tag);
                if ((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency >= 2.0) break;
            }
        }

        private static readonly Queue<PdSlotTag> _pdThumbQueue = new Queue<PdSlotTag>();
        private static readonly HashSet<PdSlotTag> _pdThumbQueued = new HashSet<PdSlotTag>();

        private static void ApplyPdThumb(PdSlotTag tag)
        {
            Texture2D cached;
            _pdThumbs.TryGetValue(tag.Path, out cached);
            tag.Thumb.texture = cached;
            tag.Thumb.color = cached != null ? Color.white : new Color(0.25f, 0.25f, 0.3f, 1f);
            if (!DockCellVisible(tag.transform.GetSiblingIndex(),
                PdCellH, _pdScrollY, PdGridH)) return;
            if (_pdThumbQueued.Add(tag)) _pdThumbQueue.Enqueue(tag);
        }

        private static void TickPdThumbnails()
        {
            // Unity texture upload stays on the main thread. At most two
            // files per frame, stop after ~2ms; a single decode is indivisible.
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            int work = 0;
            while (_pdThumbQueue.Count > 0 && work < 2)
            {
                PdSlotTag tag = _pdThumbQueue.Dequeue();
                _pdThumbQueued.Remove(tag);
                work++;
                if (tag == null || !_pdKeptCells.Contains(tag)) continue;
                LoadPdThumb(tag);
                if ((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency >= 2.0) break;
            }
        }
    }
}

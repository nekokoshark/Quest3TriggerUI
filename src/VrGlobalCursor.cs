using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Quest3TriggerUI
{
    // Global UI-ray cursor: a generated glowing ring pinned to VaM's own
    // per-hand cursor transforms (LookInputModule cursorRight/cursor), which
    // already land on the hit point of whatever canvas the UI ray touches.
    // The native cursor graphic is faded out once so the ring replaces it.
    internal sealed class VrGlobalCursor : IDisposable
    {
        private const float CursorSize = 36f; // canvas-local px; inherits hovered canvas's world scale

        private Sprite _sprite;
        private RectTransform _left;
        private RectTransform _right;
        private readonly Vector2[] _lastLocal = { new Vector2(float.MinValue, 0f), new Vector2(float.MinValue, 0f) };
        private readonly HashSet<int> _muted = new HashSet<int>();

        private readonly bool _enabled;

        internal VrGlobalCursor(bool enabled)
        {
            _enabled = enabled;
        }

        internal void Tick()
        {
            SuperController controller = SuperController.singleton;
            if (!_enabled || controller == null)
            {
                SetVisible(true, false);
                SetVisible(false, false);
                return;
            }
            UpdateHand(true, controller);
            UpdateHand(false, controller);
        }

        private void UpdateHand(bool right, SuperController controller)
        {
            GameObject target = VrPointerPresentation.CurrentLookTarget(right);
            if (target == null)
            {
                SetVisible(right, false);
                return;
            }

            Canvas canvas = TargetCanvas(right, target);
            Transform hand = VrPointerPresentation.MotionController(controller, right);
            RectTransform vcursor = VrPointerPresentation.CurrentCursor(right);
            if (canvas == null || hand == null)
            {
                SetVisible(right, false);
                return;
            }

            Transform surface = canvas.transform;
            // VaM's own cursor transform already sits on the exact UI hit
            // point; it is authoritative where the ray-plane estimate was
            // off on native panels. Fall back to the plane when absent.
            Vector3 hit;
            if (vcursor != null)
            {
                hit = vcursor.position;
            }
            else
            {
                float denom = Vector3.Dot(surface.forward, hand.forward);
                if (Mathf.Abs(denom) < 0.0001f)
                {
                    SetVisible(right, false);
                    return;
                }
                float t = Vector3.Dot(surface.forward,
                    surface.position - hand.position) / denom;
                if (t <= 0f || t > 6f)
                {
                    SetVisible(right, false);
                    return;
                }
                hit = hand.position + hand.forward * t;
            }

            EnsureCreated();
            MuteNativeCursor(vcursor);
            RectTransform dot = right ? _right : _left;
            // The cursor owns a nested Canvas (overrideSorting, top order) so
            // its moves dirty only its own 1-element batch — the hovered
            // canvas is never rebuilt by cursor motion.
            int slot = right ? 1 : 0;
            if (dot.parent != surface)
            {
                dot.SetParent(surface, false);
                dot.SetAsLastSibling();
                int layer = surface.gameObject.layer;
                dot.gameObject.layer = layer;
                Transform ring = dot.GetChild(0);
                if (ring != null) ring.gameObject.layer = layer;
                _lastLocal[slot] = new Vector2(float.MinValue, 0f);
            }
            Vector3 towardHand = hit - hand.position;
            if (towardHand.sqrMagnitude > 0.000001f) towardHand.Normalize();
            Vector3 local = surface.InverseTransformPoint(
                hit - towardHand * 0.01f);
            // Own-canvas rebuild is ~free; a small dead zone still avoids
            // dirtying it when the hit point is perfectly still.
            Vector2 xy = new Vector2(local.x, local.y);
            if ((xy - _lastLocal[slot]).sqrMagnitude > 0.25f)
            {
                _lastLocal[slot] = xy;
                dot.localPosition = local;
            }
            SetVisible(right, true);
        }

        private readonly GameObject[] _lastTarget = new GameObject[2];
        private readonly Canvas[] _lastCanvas = new Canvas[2];

        private Canvas TargetCanvas(bool right, GameObject target)
        {
            int slot = right ? 1 : 0;
            if (!ReferenceEquals(_lastTarget[slot], target))
            {
                _lastTarget[slot] = target;
                Canvas canvas = target.GetComponentInParent<Canvas>();
                _lastCanvas[slot] =
                    canvas != null && canvas.renderMode == RenderMode.WorldSpace
                        ? canvas : null;
            }
            return _lastCanvas[slot];
        }

        private void SetVisible(bool right, bool visible)
        {
            RectTransform dot = right ? _right : _left;
            if (dot == null) return;
            if (!visible)
                _lastLocal[right ? 1 : 0] = new Vector2(float.MinValue, 0f);
            if (dot.gameObject.activeSelf != visible)
                dot.gameObject.SetActive(visible);
        }

        private void EnsureCreated()
        {
            if (_left != null && _right != null) return;
            if (_sprite == null) _sprite = CreateRingSprite();
            if (_left == null) _left = CreateDot("LeftCursorDot");
            if (_right == null) _right = CreateDot("RightCursorDot");
        }

        private RectTransform CreateDot(string name)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(CursorSize, CursorSize);
            Canvas canvas = go.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = 32767;
            GameObject ring = new GameObject("ring", typeof(RectTransform));
            RectTransform ringRect = (RectTransform)ring.transform;
            ringRect.SetParent(rect, false);
            ringRect.anchorMin = Vector2.zero;
            ringRect.anchorMax = Vector2.one;
            ringRect.offsetMin = ringRect.offsetMax = Vector2.zero;
            Image image = ring.AddComponent<Image>();
            image.sprite = _sprite;
            image.color = new Color(0.85f, 0.95f, 1f, 1f);
            image.raycastTarget = false;
            go.SetActive(false);
            return rect;
        }

        private void MuteNativeCursor(RectTransform cursor)
        {
            if (cursor == null || !_muted.Add(cursor.GetInstanceID())) return;
            Graphic graphic = cursor.GetComponentInChildren<Graphic>(true);
            if (graphic != null)
            {
                graphic.color = new Color(
                    graphic.color.r, graphic.color.g, graphic.color.b, 0f);
                return;
            }
            Renderer mesh = cursor.GetComponentInChildren<Renderer>(true);
            if (mesh != null) mesh.enabled = false;
        }

        private static Sprite CreateRingSprite()
        {
            const int size = 64;
            const float ringRadius = 16f;
            const float ringHalfWidth = 2.0f;
            const float glowSigma = 4.2f;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float c = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - c, dy = y - c;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float dr = Mathf.Abs(dist - ringRadius);
                    float core = Mathf.Clamp01(1f - (dr - ringHalfWidth) / 1.6f);
                    float glow = Mathf.Exp(-dr * dr / (2f * glowSigma * glowSigma)) * 0.4f;
                    float edge = Mathf.Clamp01((c - dist) / 5f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f,
                        Mathf.Clamp01(Mathf.Max(core, glow)) * edge));
                }
            }
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            Sprite sprite = Sprite.Create(
                tex, new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f), size);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        public void Dispose()
        {
            if (_left != null) UnityEngine.Object.Destroy(_left.gameObject);
            if (_right != null) UnityEngine.Object.Destroy(_right.gameObject);
            _left = null;
            _right = null;
        }
    }
}

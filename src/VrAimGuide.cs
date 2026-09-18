using System;
using UnityEngine;

namespace Quest3TriggerUI
{
    // The guide follows VaM's per-hand UI-ray camera transforms and has no endpoint marker.
    internal sealed class VrAimGuide : IDisposable
    {
        private const float Length = 0.80f;
        private const float StartOffset = 0.055f;
        private const float LineWidth = 0.00125f;
        private static readonly Color IdleStart = new Color(0.26f, 0.52f, 0.70f, 0.10f);
        private static readonly Color IdleEnd = new Color(0.26f, 0.52f, 0.70f, 0.055f);
        private static readonly Color HoverStart = new Color(0.72f, 0.43f, 0.16f, 0.18f);
        private static readonly Color HoverEnd = new Color(0.72f, 0.43f, 0.16f, 0.11f);
        private GameObject _root;
        private Material _material;
        private LineRenderer _left;
        private LineRenderer _right;
        private bool _leftHovered;
        private bool _rightHovered;

        internal void Tick(bool visible, Transform leftHand, Transform rightHand)
        {
            if (!visible || leftHand == null || rightHand == null) { SetVisible(false); return; }
            EnsureCreated();
            SetVisible(true);
            UpdateHand(_left, leftHand, false, VrPointerPresentation.CurrentLookTarget(false) != null);
            UpdateHand(_right, rightHand, true, VrPointerPresentation.CurrentLookTarget(true) != null);
        }

        private void EnsureCreated()
        {
            if (_root != null) return;
            _root = new GameObject("__Quest3AuxiliaryAimGuide");
            _root.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_root);
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            _material = new Material(shader);
            _material.hideFlags = HideFlags.HideAndDontSave;
            _left = CreateLine("LeftRay");
            _right = CreateLine("RightRay");
            SetColors(_left, false);
            SetColors(_right, false);
        }

        private LineRenderer CreateLine(string name)
        {
            GameObject child = new GameObject(name);
            child.hideFlags = HideFlags.HideAndDontSave;
            child.transform.SetParent(_root.transform, false);
            LineRenderer line = child.AddComponent<LineRenderer>();
            line.sharedMaterial = _material;
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.startWidth = LineWidth;
            line.endWidth = LineWidth;
            line.numCapVertices = 0;
            line.numCornerVertices = 0;
            line.receiveShadows = false;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return line;
        }

        private void UpdateHand(LineRenderer line, Transform hand, bool right, bool hovered)
        {
            line.SetPosition(0, hand.position + hand.forward * StartOffset);
            line.SetPosition(1, hand.position + hand.forward * Length);
            if (right)
            {
                if (_rightHovered != hovered) { _rightHovered = hovered; SetColors(line, hovered); }
            }
            else if (_leftHovered != hovered)
            {
                _leftHovered = hovered;
                SetColors(line, hovered);
            }
        }

        private static void SetColors(LineRenderer line, bool hovered)
        {
            line.startColor = hovered ? HoverStart : IdleStart;
            line.endColor = hovered ? HoverEnd : IdleEnd;
        }

        private void SetVisible(bool visible)
        {
            if (_root != null && _root.activeSelf != visible) _root.SetActive(visible);
        }

        public void Dispose()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            if (_material != null) UnityEngine.Object.Destroy(_material);
            _root = null; _material = null; _left = null; _right = null;
        }
    }
}

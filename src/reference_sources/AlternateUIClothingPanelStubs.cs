using System;
using System.Collections;
using UnityEngine;

namespace VUI
{
    internal class Layout { }
    internal class LayoutData { }
    internal class BorderLayout : Layout
    {
        internal BorderLayout(float spacing = 0) { }
        internal static readonly LayoutData Left = new LayoutData();
        internal static readonly LayoutData Center = new LayoutData();
        internal static readonly LayoutData Right = new LayoutData();
    }
    internal class VerticalFlow : Layout { internal VerticalFlow(int spacing = 0) { } }
    internal class FlowLayout { internal const int AlignDefault = 0; }
    internal class HorizontalFlow : Layout
    {
        internal HorizontalFlow(int spacing = 0, int align = 0, bool reverse = false) { }
    }
    internal class Insets { internal Insets(float all) { } }
    internal class PointerEvents { internal event Action<object> PointerClick; }
    internal class Widget
    {
        internal const float DontCare = -1;
        internal bool Enabled { get; set; }
        internal bool Visible { get; set; }
        internal bool Render { get; set; }
        internal Insets Padding { get; set; }
        internal Insets Borders { get; set; }
        internal Layout Layout { get; set; }
		internal object MinimumSize { get; set; }
		internal object MaximumSize { get; set; }
        internal PointerEvents Events = new PointerEvents();
        internal T Add<T>(T value, LayoutData data = null) where T : Widget { return value; }
        internal void BringToTop() { }
        internal void DoLayout() { }
		internal Root GetRoot() { return new Root(); }
    }
    internal class Panel : Widget
    {
        internal Panel(Layout layout = null) { }
        internal bool Clickthrough { get; set; }
        internal Color BackgroundColor { get; set; }
        internal Color BorderColor { get; set; }
    }
    internal class CheckBox : Widget
    {
        internal CheckBox(string text, Action<bool> changed, bool initial = false, string tooltip = "") { }
        internal bool Checked { get; set; }
    }
    internal class Label : Panel
    {
        internal const int AlignLeft = 1;
        internal const int AlignTop = 8;
        internal const int ClipEllipsis = 3;
        internal Label(string text = "") { Text = text; }
        internal string Text { get; set; }
        internal int FontSize { get; set; }
        internal int WrapMode { get; set; }
        internal int Alignment { get; set; }
        internal bool AutoTooltip { get; set; }
    }
    internal class Size { internal Size(float width, float height) { } }
    internal class Button : Widget
    {
		internal Button(string text = "", Action clicked = null, string tooltip = "") { Text = text; }
        internal string Text { get; set; }
        internal Color TextColor { get; set; }
    }
    internal class ToolButton : Button
    {
        internal ToolButton(string text = "", Action clicked = null, string tooltip = "") { Text = text; }
    }
    internal class Image : Widget { internal Texture Texture { get; set; } }
    internal class TextBox : Widget
    {
        internal TextBox(string text = "", string placeholder = "") { Text = text; }
        internal string Text { get; set; }
    }
	internal class Root : Widget { }
	internal class DialogWithButtons : Widget
	{
		internal const int OK = 1;
		internal const int Cancel = 2;
	}
	internal class InputDialog : DialogWithButtons
	{
		internal delegate void CloseHandler(int result);
		internal InputDialog(Root root, string title, string text, string initialValue)
		{
			Text = initialValue;
		}
		internal string Text { get; private set; }
		internal void RunDialog(CloseHandler handler) { }
		internal void CloseDialog(int result) { }
	}
    internal static class Style
    {
        internal static ThemeData Theme = new ThemeData();
        internal class ThemeData { internal Color BorderColor; }
    }
    internal static class Glue
    {
        internal static void LogErrorST(string message) { }
    }
}

namespace AUI.ClothingUI
{
    internal sealed class ClothingAtomInfo
    {
        internal FilterData Filter = new FilterData();
        internal void CriteriaChangedInternal() { }
        internal sealed class FilterData { internal bool FavoritesOnly; }
    }
    internal static class ClothingUI
    {
        internal static void OpenClothingUI(DAZClothingItem item, string tab = null) { }
    }
    internal static class FavoriteStore
    {
        internal static bool IsFavorite(DAZClothingItem item) { return false; }
        internal static void Toggle(DAZClothingItem item) { }
    }
}

internal sealed class AlternateUI : MonoBehaviour
{
    internal static AlternateUI Instance;
}

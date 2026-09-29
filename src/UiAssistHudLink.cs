using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Only HUD edges inspect UIAssist. The steady-state observer reads VaM flags only.
    internal static partial class UiAssistHudLink
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private sealed class Snapshot
        {
            internal Type Control, Globals;
            internal UnityEngine.Object Owner;
            internal object Display, Editor;
            internal Atom Target;
            internal int Standard, Ace;
        }
        private static Snapshot _pending;
        private static bool _observed, _visible, _changing;

        private static object Read(Type type, object instance, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
                if (p != null) return p.GetValue(instance, null);
                FieldInfo f = t.GetField(name, Flags | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(instance);
            }
            throw new MissingMemberException(type.FullName, name);
        }
        private static void Write(Type type, object instance, string name, object value)
        {
            PropertyInfo p = type.GetProperty(name, Flags);
            if (p != null) { p.SetValue(instance, value, null); return; }
            FieldInfo f = type.GetField(name, Flags);
            if (f == null) throw new MissingMemberException(type.FullName, name);
            f.SetValue(instance, value);
        }
        private static void Call(Type type, object instance, string name)
        {
            MethodInfo method = type.GetMethod(name, Flags, null, Type.EmptyTypes, null);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            method.Invoke(instance, null);
        }
        private static Snapshot FindEditor(SuperController controller)
        {
            Assembly assembly = AssemblyCatalog.FindByType("JayJayWon.UIAGlobals");
            if (assembly == null) return null;
            Type globals = assembly.GetType("JayJayWon.UIAGlobals");
            UnityEngine.Object owner = Read(globals, null, "mvrScript") as UnityEngine.Object;
            if (owner == null || !(bool)Read(globals, null, "isActivePlugin")) return null;
            Type control = assembly.GetType("JayJayWon.GameControlUI", true);
            Type modes = assembly.GetType("JayJayWon.GameControlDisplayModes", true);
            int ace = (int)Read(modes, null, "ace");
            if ((int)Read(control, null, "gameControlDisplayMode") != ace || !(bool)Read(control, null, "gridsActivated")) return null;
            object display = Read(control, null, "gridsDisplay");
            object editor = Read(assembly.GetType("JayJayWon.GridsDisplay", true), null, "_uiActiveClothingEditor");
            if (display == null || editor == null) return null;
            string uid = Read(editor.GetType(), editor, "_currentACEAtomName") as string;
            if (string.IsNullOrEmpty(uid))
            {
                object chooser = Read(editor.GetType(), editor, "personAtomNamesJSSC");
                if (chooser != null) uid = Read(chooser.GetType(), chooser, "val") as string;
            }
            Snapshot state = new Snapshot();
            state.Control = control; state.Globals = globals; state.Owner = owner;
            state.Display = display; state.Editor = editor; state.Ace = ace;
            state.Standard = (int)Read(modes, null, "standard");
            state.Target = string.IsNullOrEmpty(uid) ? null : controller.GetAtomByUid(uid);
            return state;
        }
        internal static void BeforeHide(SuperController controller)
        {
            if (_changing || controller == null || !controller.MainHUDVisible) return;
            Suspend(controller);
        }
        private static void Suspend(SuperController controller)
        {
            if (_pending != null || controller.isLoading || _presetBrowsing) return;
            long t0 = Mark();
            try
            {
                Snapshot state = FindEditor(controller);
                if (state == null) return;
                long t1 = Mark();
                _changing = true;
                // Native manual ACE exit selects 'standard' (1, not 0).
                // mode=standard + gridsActivated=false already stops the
                // per-frame Update->RefreshACE loop; skipping DestroyUIButtons
                // keeps ACEIDPlus rows (and their clothingItem/thumbnail
                // bindings) alive so the restore does not repay a GetThumbnail
                // storm per row (~0.5-1s at 100+ items).
                // Do not call CloseGrids: its optional auto-unpin changes unrelated UI placement.
                Write(state.Control, null, "gameControlDisplayMode", state.Standard);
                long t2 = Mark();
                Write(state.Control, null, "gridsActivated", false);
                long t3 = Mark();
                long t4 = Mark();
                if ((int)Read(state.Control, null, "gameControlDisplayMode") != state.Standard || (bool)Read(state.Control, null, "gridsActivated"))
                    throw new InvalidOperationException("UIAssist did not exit clothing editor mode");
                _pending = state;
                Log("服装编辑器已随控制面板关闭而退出；再次打开时按需恢复。");
                if (DeltaMs(t0, t4) > 100)
                    Log("[ACE] suspend total=" + DeltaMs(t0, t4) + "ms find=" +
                        DeltaMs(t0, t1) + " mode=" + DeltaMs(t1, t2) +
                        " grids=" + DeltaMs(t2, t3) + " destroy=" + DeltaMs(t3, t4));
            }
            catch (Exception e) { Error(e); }
            finally { _changing = false; }
        }
        internal static void AfterShow(SuperController controller)
        {
            if (_changing || controller == null || !controller.MainHUDVisible) return;
            _observed = true; _visible = true;
            Snapshot state = _pending;
            if (state == null) return;
            // Consume first: duplicate ShowMainHUD calls must not rebuild twice.
            _pending = null;
            try
            {
                if (controller.isLoading || state.Owner == null || state.Target == null ||
                    controller.GetAtomByUid(state.Target.uid) != state.Target ||
                    !ReferenceEquals(Read(state.Globals, null, "mvrScript"), state.Owner) ||
                    !(bool)Read(state.Globals, null, "isActivePlugin") ||
                    !ReferenceEquals(Read(state.Control, null, "gridsDisplay"), state.Display) ||
                    !ReferenceEquals(Read(state.Control.Assembly.GetType("JayJayWon.GridsDisplay", true), null, "_uiActiveClothingEditor"), state.Editor) ||
                    (int)Read(state.Control, null, "gameControlDisplayMode") != state.Standard ||
                    (bool)Read(state.Control, null, "gridsActivated") ||
                    controller.activeUI != SuperController.ActiveUI.None) return;
                _changing = true;
                // Reuse the editor object: its filters and per-person state remain intact.
                // Do not call OpenUiAssistClothingEditor, which selects the nearest person anew.
                object chooser = Read(state.Editor.GetType(), state.Editor, "personAtomNamesJSSC");
                if (chooser != null) Write(chooser.GetType(), chooser, "valNoCallback", state.Target.uid);
                long t0 = Mark();
                Write(state.Control, null, "gameControlDisplayMode", state.Ace);
                long t1 = Mark();
                Write(state.Control, null, "gridsActivated", true);
                long t2 = Mark();
                // Buttons were left in place by Suspend — refresh data only;
                // recreating them would re-null currentDAZClothingItem and
                // re-trigger the per-row thumbnail path.
                long t3 = Mark();
                long t4 = Mark();
                Call(state.Editor.GetType(), state.Editor, "RefreshACE");
                long t5 = Mark();
                Call(state.Control, null, "OnEnable");
                long t6 = Mark();
                Log("控制面板已打开，服装编辑器已恢复。");
                if (DeltaMs(t0, t6) > 100)
                    Log("[ACE] restore total=" + DeltaMs(t0, t6) + "ms mode=" +
                        DeltaMs(t0, t1) + " grids=" + DeltaMs(t1, t2) +
                        " destroy=" + DeltaMs(t2, t3) + " create=" + DeltaMs(t3, t4) +
                        " refresh=" + DeltaMs(t4, t5) + " onEnable=" + DeltaMs(t5, t6));
            }
            catch (Exception e)
            {
                Error(e);
                // A partial restore should not leave an expensive invisible editor running.
                try { Write(state.Control, null, "gameControlDisplayMode", state.Standard); Write(state.Control, null, "gridsActivated", false);
                Call(state.Display.GetType(), state.Display, "DestroyUIButtons"); }
                catch (Exception cleanup) { Error(cleanup); }
            }
            finally { _changing = false; }
        }
        internal static void Observe()
        {
            SuperController controller = SuperController.singleton;
            if (controller == null) { CancelPending(); ReleaseDockNavSuppression(); _observed = false; return; }
            if (controller.isLoading) CancelPending();
            ApplyPanelPresentation();
            EnsureAceOpenProbe();
            TickDockNavSuppression();
            TickPresetDock();
            TickFavoritesBar();
            TickSceneDock();
            TickBanBar();
            TickLockBar();
            NotePanelDockLayout();
            UpdatePresetButtons(controller);
            bool visible = controller.MainHUDVisible;
            if (!_observed) { _observed = true; _visible = visible; return; }
            if (visible == _visible) return;
            _visible = visible;
            if (visible) AfterShow(controller); else Suspend(controller);
        }
        internal static void CancelPending() { _pending = null; }
        internal static void Reset() { ClearPresetDock(); ClearFavoritesBar(); ClearSceneDock(); ClearBanBar(); ClearLockBar(); ClearPresetButtons(); ReleaseDockNavSuppression(); _presetBrowsing = false; _presetState = null; CancelPending(); _observed = false;
            // Uninstall the ACE instrumentation so a dead payload generation
            // leaves neither active patches nor pinned code behind.
            if (_aceProbeHarmony != null)
            {
                try { _aceProbeHarmony.UnpatchAll(_aceProbeHarmony.Id); } catch { }
                _aceProbeHarmony = null;
            }
            _aceProbeTried = false;
        }
        private static void Log(string message)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogInfo(message);
        }
        private static void Error(Exception error)
        {
            if (Quest3TriggerUIPlugin.Log != null) Quest3TriggerUIPlugin.Log.LogError("UIAssist HUD link: " + error);
        }
    }
    [HarmonyPatch(typeof(SuperController), "HideMainHUD")]
    internal static class SuspendClothingEditorWithHudPatch
    {
        private static void Prefix(SuperController __instance) { UiAssistHudLink.BeforeHide(__instance); }
    }
    [HarmonyPatch(typeof(SuperController), "ShowMainHUD")]
    internal static class RestoreClothingEditorWithHudPatch
    {
        private static void Postfix(SuperController __instance) { UiAssistHudLink.AfterShow(__instance); }
    }
    [HarmonyPatch(typeof(SuperController), "Load", new Type[] { typeof(string) })]
    internal static class CancelClothingEditorRestoreOnSceneLoadPatch
    {
        private static void Prefix() { UiAssistHudLink.CancelPending(); }
    }
}




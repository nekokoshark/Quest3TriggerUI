using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System;


namespace JayJayWon
{
    
    public static class UIAPluginInterop
    {
        public static JSONStorableAction _loadPluginsUIJSON = null;
        public static JSONStorableAction _findPluginsUIJSON = null;
        public static JSONStorableAction openBrowserAssistUIJSAction = null;
        public static JSONStorableAction closeBrowserAssistUIJSAction = null;
        public static JSONStorableAction toggleBrowserAssistUIJSAction = null;
        public static List<JSONStorableAction> openBrowserAssistResourceTypeUIJSActions = new List<JSONStorableAction>();

        private static JSONStorable _pluginAssistRegistered = null;
        private static JSONStorable _browserAssistRegistered = null;

        private static readonly List<ExtGazeTargetReceiver> _extGazeTargetReceivers = new List<ExtGazeTargetReceiver>();
        private static readonly HashSet<JSONStorable> _uiaPluginsList = new HashSet<JSONStorable>();


        public static void OnSceneLoaded()
        {
            UpdateExtTargetsWithLastGazedAtAtom("");
            UpdateExtTargetsWithLastGazedAtPerson("");
            UpdateExtTargetsWithLastGazedAtFemale("");
            UpdateExtTargetsWithLastGazedAtMale("");
            UpdateExtTargetsWithLastGazedAtNonPerson("");
        }

        public static void AtomNameUpdate(string oldName, string newName)
        {
            if (TargetControl.lastViewedAtom == oldName) UpdateExtTargetsWithLastGazedAtAtom(newName);
            if (TargetControl.lastViewedPerson == oldName)UpdateExtTargetsWithLastGazedAtPerson(newName);
            if (TargetControl.lastViewedFemale == oldName)UpdateExtTargetsWithLastGazedAtFemale(newName);
            if (TargetControl.lastViewedMale == oldName) UpdateExtTargetsWithLastGazedAtMale(newName);
            if (TargetControl.lastViewedNonPerson == oldName) UpdateExtTargetsWithLastGazedAtNonPerson(newName);
        }

        public static void AtomRemovedUpdate(Atom atom)
        {
            string atomName = atom.name;
            if (TargetControl.lastViewedAtom == atomName) UpdateExtTargetsWithLastGazedAtAtom("");
            if (TargetControl.lastViewedPerson == atomName) UpdateExtTargetsWithLastGazedAtPerson("");
            if (TargetControl.lastViewedFemale == atomName) UpdateExtTargetsWithLastGazedAtFemale("");
            if (TargetControl.lastViewedMale == atomName) UpdateExtTargetsWithLastGazedAtMale("");
            if (TargetControl.lastViewedNonPerson == atomName) UpdateExtTargetsWithLastGazedAtNonPerson("");
        }

        private class ExtGazeTarget
        {
            public JSONStorableString extGazeTargetNameJSON;
            // Could extend this to hold Custom Gaze Target details
        }
        private class ExtGazeTargetReceiver
        {
            public JSONStorable storable;
            public Dictionary<string, ExtGazeTarget> extGazeTargets;
        }

        public static void UpdateExtTargetsWithLastGazedAtAtom(string atomName)
        {
            foreach (var extGazeTargetReceiver in _extGazeTargetReceivers)
            {
                if (extGazeTargetReceiver.extGazeTargets.ContainsKey("UIALastGazedAtAtom")) extGazeTargetReceiver.extGazeTargets["UIALastGazedAtAtom"].extGazeTargetNameJSON.val = atomName;
            }
        }
        public static void UpdateExtTargetsWithLastGazedAtPerson(string personName)
        {
            foreach (var extGazeTargetReceiver in _extGazeTargetReceivers)
            {
                if (extGazeTargetReceiver.extGazeTargets.ContainsKey("UIALastGazedAtPerson")) extGazeTargetReceiver.extGazeTargets["UIALastGazedAtPerson"].extGazeTargetNameJSON.val = personName;
            }
        }
        public static void UpdateExtTargetsWithLastGazedAtFemale(string atomName)
        {
            foreach (var extGazeTargetReceiver in _extGazeTargetReceivers)
            {
                if (extGazeTargetReceiver.extGazeTargets.ContainsKey("UIALastGazedAtFemale")) extGazeTargetReceiver.extGazeTargets["UIALastGazedAtFemale"].extGazeTargetNameJSON.val = atomName;
            }
        }
        public static void UpdateExtTargetsWithLastGazedAtMale(string atomName)
        {
            foreach (var extGazeTargetReceiver in _extGazeTargetReceivers)
            {
                if (extGazeTargetReceiver.extGazeTargets.ContainsKey("UIALastGazedAtMale")) extGazeTargetReceiver.extGazeTargets["UIALastGazedAtMale"].extGazeTargetNameJSON.val = atomName;
            }
        }
        public static void UpdateExtTargetsWithLastGazedAtNonPerson(string atomName)
        {
            foreach (var extGazeTargetReceiver in _extGazeTargetReceivers)
            {
                if (extGazeTargetReceiver.extGazeTargets.ContainsKey("UIALastGazedAtNonPerson")) extGazeTargetReceiver.extGazeTargets["UIALastGazedAtNonPerson"].extGazeTargetNameJSON.val = atomName;
            }
        }

        public static int GetOtherActiveUIAssistCount()
        {
            int pluginCount = 0;
            foreach (JSONStorable storable in _uiaPluginsList)
            {
                JSONStorableBool isActive = new JSONStorableBool("isActive",false);
                storable.SendMessage("OnIsPluginActiveRequested", isActive, SendMessageOptions.DontRequireReceiver);
                if (isActive.val) pluginCount++;
            }
            return (pluginCount);
        }
        private static void UpdateOtherUIAssistVRHand(int thisUIAPluginVRHand)
        {
            foreach (JSONStorable storable in _uiaPluginsList)
            {
                JSONStorableBool isActive = new JSONStorableBool("isActive", false);
                storable.SendMessage("OnIsPluginActiveRequested", isActive, SendMessageOptions.DontRequireReceiver);
                if (isActive.val)
                {
                    int otherUIAPluginVRHand = LeftRight.left;
                    if (thisUIAPluginVRHand == LeftRight.left) otherUIAPluginVRHand = LeftRight.right;
                    
                    storable.SendMessage("OnUpdatePluginHandRequested", otherUIAPluginVRHand, SendMessageOptions.DontRequireReceiver);
                }
            }
        }

        public static void ResetVRHand(int pluginVRHand)
        {
            if (pluginVRHand == VRHandControl.leftHand) UIAGlobals.vrLeftOrRightHand = LeftRight.left;
            else if (pluginVRHand == VRHandControl.rightHand) UIAGlobals.vrLeftOrRightHand = LeftRight.right;
            else UIAGlobals.vrLeftOrRightHand = LeftRight.neither;
            if (UIAGlobals.vrLeftOrRightHand != LeftRight.neither) UpdateOtherUIAssistVRHand(UIAGlobals.vrLeftOrRightHand);
        }

        public static void AcquireAllAvailableBroadcastingPlugins(JSONStorable excludeStorable = null)
        {
            _extGazeTargetReceivers.Clear();
            foreach (var storable in SuperController.singleton
                .GetAtoms()
                .SelectMany(atom => atom.GetStorableIDs()
                .Select(atom.GetStorableByID)
                .Where(s => s is MVRScript)))
            {
                if (storable != excludeStorable) TryRegister(storable);
            }

            foreach (var storable in SuperController.singleton
                .GetComponentInChildren<MVRPluginManager>()
                .GetComponentsInChildren<MVRScript>()
                .Where(s => !ReferenceEquals(s, UIAGlobals.mvrScript)))
            {
                if (storable != excludeStorable) TryRegister(storable);
            }
        }
        public static void TryDeregister( JSONStorable storable)
        {
            if (_uiaPluginsList.Contains(storable))  _uiaPluginsList.Remove(storable);
            if (_uiaPluginsList.Count == 0) UIAGlobals.isPrimaryPlugin = true;

            var existing = _extGazeTargetReceivers.FirstOrDefault(r => r.storable == storable);
            if (existing != null) _extGazeTargetReceivers.Remove(existing);
            if (storable == _pluginAssistRegistered)
            {
                _loadPluginsUIJSON = null;
                _findPluginsUIJSON = null;
                _pluginAssistRegistered = null;
                AcquireAllAvailableBroadcastingPlugins(storable);
            }
            if (storable == _browserAssistRegistered)
            {
                openBrowserAssistUIJSAction = null;
                closeBrowserAssistUIJSAction = null;
                toggleBrowserAssistUIJSAction = null;
                openBrowserAssistResourceTypeUIJSActions.Clear();
                AcquireAllAvailableBroadcastingPlugins(storable);
            }
            bool unregistered = ExternalButtonProvider.UnregisterProvider(storable as MVRScript);
            if (unregistered && GameControlUI.gameControlDisplayMode == GameControlDisplayModes.externalButtonProviders)
            {
                if (ExternalButtonProvider.currentProvider == null) GameControlUI.gameControlDisplayMode = GameControlDisplayModes.standard;
                GameControlUI.RefreshWristUIButtonGrid();
            }

        }
        public static void TryRegister(JSONStorable storable)
        {
            if (!ReferenceEquals(storable, UIAGlobals.mvrScript) && !_uiaPluginsList.Contains(storable) && storable.name.EndsWith("_JayJayWon.UIAssist"))
            {
                _uiaPluginsList.Add(storable);
            }

            var existing = _extGazeTargetReceivers.FirstOrDefault(r => r.storable == storable);
            if (existing != null) _extGazeTargetReceivers.Remove(existing);

            // GazeTarget Receivers
            var gazeTargets = new Dictionary<string, object>();
            storable.SendMessage("OnGazeTargetsListRequested", gazeTargets, SendMessageOptions.DontRequireReceiver);
            if (gazeTargets.Count > 0)
            {
                ExtGazeTargetReceiver egtr = new ExtGazeTargetReceiver
                {
                    storable = storable,
                    extGazeTargets = new Dictionary<string, ExtGazeTarget>()
                };

                foreach (KeyValuePair<string, object> kvp in gazeTargets)
                {
                    switch (kvp.Key)
                    {
                        case "UIALastGazedAtAtom":
                        case "UIALastGazedAtFemale":
                        case "UIALastGazedAtMale":
                        case "UIALastGazedAtPerson":
                            var typed = kvp.Value as JSONStorableString;
                            if (typed != null)
                            {
                                ExtGazeTarget egt = new ExtGazeTarget();
                                egt.extGazeTargetNameJSON = typed;
                                egtr.extGazeTargets.Add(kvp.Key, egt);
                                if (kvp.Key == "UIALastGazedAtAtom") typed.val = TargetControl.lastViewedAtom;
                                if (kvp.Key == "UIALastGazedAtFemale") typed.val = TargetControl.lastViewedFemale;
                                if (kvp.Key == "UIALastGazedAtMale") typed.val = TargetControl.lastViewedMale;
                                if (kvp.Key == "UIALastGazedAtPerson") typed.val = TargetControl.lastViewedPerson;
                            }
                            else SuperController.LogMessage("UIAssist: External storable '" + storable.name + "' attempted to register an invalid Gaze Target Type.");
                            break;
                        default:
                            SuperController.LogMessage("UIAssist: External storable '" + storable.name + "' attempted to register an invalid Gaze Target.");
                            break;
                    }
                }
                _extGazeTargetReceivers.Add(egtr);
            }

            // PluginAssist Binding Actions
            if (storable.name.EndsWith("_JayJayWon.PluginAssist") && _pluginAssistRegistered == null)
            {
                List<object> bindings = new List<object>();
                storable.SendMessage("OnBindingsListRequested", bindings, SendMessageOptions.DontRequireReceiver);

                foreach (object obj in bindings)
                {
                    var typed = obj as JSONStorableAction;
                    if (typed != null)
                    {
                        if (typed.name == "Open Load Plugins UI") _loadPluginsUIJSON = typed;
                        if (typed.name == "Open Find Plugins UI") _findPluginsUIJSON = typed;
                        _pluginAssistRegistered = storable;
                    }
                }
            }

            var buttonListData = new Dictionary<string, object>();
            storable.SendMessage("OnUIAButtonListDataRequested", buttonListData, SendMessageOptions.DontRequireReceiver);
            if (buttonListData.Count>0)
            {
                ExternalButtonProvider.RegisterProvider(storable as MVRScript, buttonListData);
                if (GameControlUI.gameControlDisplayMode == GameControlDisplayModes.externalButtonProviders) GameControlUI.RefreshWristUIButtonGrid();
            }


            if (storable.name.EndsWith("_JayJayWon.BrowserAssist") && _browserAssistRegistered == null)
            {
                List<object> bindings = new List<object>();
                storable.SendMessage("OnBindingsListRequested", bindings, SendMessageOptions.DontRequireReceiver);

                foreach (object obj in bindings)
                {
                    var typed = obj as JSONStorableAction;
                    if (typed != null)
                    {
                        if (typed.name == "Open UI") openBrowserAssistUIJSAction = typed;
                        if (typed.name == "Close UI") closeBrowserAssistUIJSAction = typed;
                        if (typed.name == "Toggle UI Open/Close") toggleBrowserAssistUIJSAction = typed;
                        if (typed.name.StartsWith("Open UI:")) openBrowserAssistResourceTypeUIJSActions.Add(typed);
                        _browserAssistRegistered = storable;
                    }
                }
            }
        }

    }
}

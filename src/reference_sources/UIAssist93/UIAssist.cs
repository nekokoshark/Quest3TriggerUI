using MVR.FileManagementSecure;
using SimpleJSON;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace JayJayWon
{  
    public class UIAssist : MVRScript
    {
        private float pluginStartTime;
        public override void Init()
        {
            try
            {
                pluginStartTime = Time.time;
                UIAGlobals.isActivePlugin = false;
                UIAGlobals.mvrScript = this;
#if VAM_GT_1_21
                UIAGlobals.isRealClothingAvailable = true;
#endif

                string i = NodePhysicsModificationMode.enumManifestName;

                if (XRSettings.enabled) UIAGlobals.vrActive = true;

                foreach (Font font in Resources.FindObjectsOfTypeAll(typeof(Font)) as Font[])
                {
                    UIAGlobals.fontsByName.Add(font.name, font);
                }
            }
            catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
        }

        public void OnActionsProviderAvailable(JSONStorable storable)
        {
            UIAPluginInterop.TryRegister(storable);
        }

        public void OnActionsProviderDestroyed(JSONStorable storable)
        {
            UIAPluginInterop.TryDeregister(storable);
        }

        public void OnBrowserAssistAvailable(MVRScript baPlugin)
        {
            BAInterop.BAPlugin = baPlugin;
            BAInterop.RegisterVAREnablingCallback(this, null, RefreshACLCallbacks, null, RefreshACLCallbacks);
        }

        private void RefreshACLCallbacks()
        {
            foreach (Atom atom in SuperController.singleton.GetAtoms())
            {
                if (atom.type == "Person")
                {
                    ActiveClothingList.UnregisterClothingItemJSBCallbacks(atom);
                    ActiveClothingList.RegisterClothingItemJSBCallbacks(atom);
                }
            }
        }

        public void OnBrowserAssistDestroyed()
        {
            BAInterop.BAPlugin = null;
        }

        public void OnCloseVAMUIAttachedUI()
        {
            if (UIAGlobals.isActivePlugin)
            {
                if (GameControlUI.gameControlUIMode== GameControlUIModes.vrVAMUI && GameControlUI.gridsActivated)
                {
                    GridsControlDisplay.CloseGrids();
                }
            }
        }

        public void OnUpdatePluginHandRequested(int vrHandLR)
        {
            if (UIAGlobals.vrLeftOrRightHand != LeftRight.neither)
            {
                if (vrHandLR == LeftRight.left) VRSettings.vrHandControlJSEnum.val = VRHandControl.leftHand;
                else if (vrHandLR == LeftRight.right) VRSettings.vrHandControlJSEnum.val = VRHandControl.rightHand;
                //              RefreshLeftUIDisplay();
                GameControlUI.RefreshWristUIButtonGrid();
            }
        }
        public void OnIsPluginActiveRequested(JSONStorableBool isActive)
        {
            isActive.val = UIAGlobals.isActivePlugin;
        }


        // This method will be called by Keybindings when it is ready.
        public void OnBindingsListRequested(List<object> bindings)
        {
            try
            {
                bindings.Add(new JSONStorableAction("Toggle UI", () =>
                {
                    GridsControlDisplay.MainButtonClickDown();
                    GridsControlDisplay.MainButtonClickFinish();
                }
                ));
                bindings.Add(new JSONStorableAction("Previous Grid", () =>
                {
                    if (GameControlUI.gridsActivated && GameControlUI._currentGrid.gridIndex > GridsControlDisplay.GetMinLeftGridWithActiveButtons()) GridsControlDisplay.ScrollLeftButton();
                }
                ));
                bindings.Add(new JSONStorableAction("Next Grid", () =>
                {
                    if (GameControlUI.gridsActivated && GameControlUI._currentGrid !=null && GameControlUI._currentGrid.gridIndex < GridsControlDisplay.GetMaxRightGridWithActiveButtons()) GridsControlDisplay.ScrollRightButton();
                }
                ));
                bindings.Add(new JSONStorableAction("Frist Grid", () =>
                {
                    if (GameControlUI.gridsActivated) GridsControlDisplay.SetCurrentGrid(GridsControlDisplay.GetMinLeftGridWithActiveButtons());
                }
                ));

                bindings.Add(new JSONStorableAction("Last Grid", () =>
                {
                    if (GameControlUI.gridsActivated) GridsControlDisplay.SetCurrentGrid(GridsControlDisplay.GetMaxRightGridWithActiveButtons());
                }
                ));
                for (int screenRef = 0; screenRef < 10; screenRef++)
                {
                    int temp = screenRef;
                    bindings.Add(new JSONStorableAction("Open Grid " + (temp + 1).ToString(), () =>
                    {
                        if (GameControlUI.gridsActivated) { GridsControlDisplay.MainButtonClickDown(); GridsControlDisplay.MainButtonClickFinish(); }
                        GridsControlDisplay.SetCurrentGrid(temp);
                    }
                    ));
                }
                bindings.Add(new JSONStorableAction("Toggle Gaze Targets Display", () =>
                {
                    if (GameControlUI.gridsActivated) GameControlSettings.displayGazeSelectionsJSB.val = !GameControlSettings.displayGazeSelectionsJSB.val;
                }
                ));
                bindings.Add(new JSONStorableAction("Toggle Gaze Assisted Select", () =>
                {
                    if (GameControlUI.gridsActivated) GazeAssistedSelectTool.gazeAssistActiveJSB.val = !GazeAssistedSelectTool.gazeAssistActiveJSB.val;
                }
                ));
                bindings.Add(new JSONStorableAction("Toggle Heel Adjusts", () =>
                {
                    HeelAdjustTool.heelAdjustActiveJSB.val = !HeelAdjustTool.heelAdjustActiveJSB.val;
                }
                ));
                bindings.Add(new JSONStorableAction("Toggle Heel Height Adjusts", () =>
                {
                    HeelAdjustTool.heelAdjustRaisePeopleJSB.val = !HeelAdjustTool.heelAdjustRaisePeopleJSB.val;
                }
                ));
                bindings.Add(new JSONStorableAction("Toggle Hide Game Control UI", () =>
                {
                    UIAGlobals.hideGameControlUI = !UIAGlobals.hideGameControlUI;
                }
                ));

                if (PatreonFeatures.patreonContentEnabled && UIAStorables.quickLaunchButtonGrid != null)
                {
                    bindings.Add(new JSONStorableAction("Toggle Quick Launch Bar", () =>
                    {
                        if (GameControlUI.gridsActivated) GameControlSettings.activateQuickLaunchBarJSB.val = !GameControlSettings.activateQuickLaunchBarJSB.val;
                    }
                    ));

                    int qlbButtonCount = UIAStorables.quickLaunchButtonGrid.buttonList.Count;

                    for (int buttonRef = 0; buttonRef < qlbButtonCount; buttonRef++)
                    {
                        int temp = buttonRef;
                        bindings.Add(new JSONStorableAction("Activate QLB Button " + (temp + 1).ToString(), () =>
                        {
                            UIAButton button = UIAStorables.quickLaunchButtonGrid.buttonList[temp];
                            bool buttonIncludesACE = false;
                            foreach (UIAButtonOperation buttonOp in button.buttonOperations)
                            {
                                if (buttonOp.buttonOpTypeJSEnum.val == UIAButtonOpType.activeClothingEditor) buttonIncludesACE = true;
                            }
                            if (buttonIncludesACE && GameControlUI.gameControlDisplayMode != GameControlDisplayModes.ace && !GameControlUI.gridsActivated)
                            {
                                GridsControlDisplay.MainButtonClickDown();
                                GridsControlDisplay.MainButtonClickFinish();
                            }
                            if (SuperController.singleton.mainHUD == null) UIAButton.hudOnPreButtonAction = false;
                            else UIAButton.hudOnPreButtonAction = SuperController.singleton.mainHUD.gameObject.activeInHierarchy;

                            button.ActionInit(LeftRight.neither, false);
                        }
                        ));
                    }

                }
            }

            catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
        }

        private void OnSceneLoaded()
        {
            TargetControl.OnSceneLoaded();
            UIAPluginInterop.OnSceneLoaded();
            ForceVAMModesTool.OnSceneLoaded();
            AppearancePresetComponent.OnSceneLoaded();
            GameControlUI.OnSceneLoaded();
            if (PatreonFeatures.patreonContentEnabled) UIAStorables.triggerFunctionsGrid.OnTriggerEvent(TriggerFuncType.onSceneLoaded);


            foreach (Atom atom in SuperController.singleton.GetAtoms())
            {
                if (atom.type == "Person")
                {
                    HeelAdjustTool.RemoveHeelAdjustMocapOffsets(atom, true);
                    StartCoroutine(ActiveClothingList.RefreshACL(atom, false));
                }
            }
        }
        private void OnPreSceneSaved()
        {
            if (PatreonFeatures.patreonContentEnabled) UIAStorables.triggerFunctionsGrid.OnTriggerEvent(TriggerFuncType.onBeforeSceneSaved);
        }
        private void OnPostSceneSaved()
        {
            if (PatreonFeatures.patreonContentEnabled) UIAStorables.triggerFunctionsGrid.OnTriggerEvent(TriggerFuncType.onAfterSceneSaved);
        }
        private void OnAtomAdded(Atom atom)
        {
            ForceVAMModesTool.OnAtomAdded(atom);
            GameControlUI.AtomAddedUpdate(atom);
            //           MorphCache.RefreshCache(atom);
            ActiveClothingList.OnAtomAdded(atom);

            if (PatreonFeatures.patreonContentEnabled && (Time.time-pluginStartTime) >5f) UIAStorables.triggerFunctionsGrid.OnTriggerEvent(TriggerFuncType.onAtomAdded, atom);
        }

        protected void AtomNameUpdate(string oldName, string newName)
        {
            try
            {
                UIAPluginInterop.AtomNameUpdate(oldName, newName);
                GameControlUI.AtomNameUpdate(oldName, newName);
                HeelAdjustTool.AtomNameUpdate(oldName, newName);
                UIAStorables.AtomNameUpdate(oldName, newName);
                RelativePositionComponentUITab.AtomNameUpdate(oldName, newName);
                TargetControl.AtomNameUpdate(oldName, newName);
                ActiveClothingList.OnAtomNameUpdate(oldName, newName);
            }
            catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
        }
        protected void AtomRemovedUpdate(Atom atom)
        {
            try
            {
                UIAPluginInterop.AtomRemovedUpdate(atom);
                GameControlUI.AtomRemovedUpdate(atom);
                HeelAdjustTool.AtomRemovedUpdate(atom);
                UIAStorables.AtomRemovedUpdate(atom);
                RelativePositionComponentUITab.AtomRemovedUpdate(atom);
                TargetControl.AtomRemovedUpdate(atom);
                ActiveClothingList.OnAtomRemoved(atom);
                //                RefreshRightUIDisplay();
                if (atom.type=="Person")HeelAdjustTool.FootPoseReset(atom);
            }
            catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
        }

        void OnEnable()
        {
            try
            {
                if (UIAGlobals.isActivePlugin)
                {
                    GameControlUI.OnEnable();
                    ForceVAMModesTool.OnEnable();
                    SuperController.singleton.onSceneLoadedHandlers += OnSceneLoaded;

                    UIAStorables.triggerFunctionsGrid.StartPhraseRecognizer();
                }

            }
            catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
        }
        void OnDisable()
        {
            try
            {
                if (UIAGlobals.isActivePlugin)
                {
                    SuperController.singleton.onSceneLoadedHandlers -= OnSceneLoaded;
                    GameControlUI.OnDisable();
                    HeelAdjustTool.DisableHeelAdjust();
                    RelativePositionComponentUITab.DeactivateSpawnTest();
                    AtomUtils.DisableHidePersonNodes();

                    UIAStorables.triggerFunctionsGrid.StopPhraseRecognizer();
                }
            }
            catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
        }
        void OnDestroy()
        {
            try
            {
                if (UIAGlobals.isActivePlugin)
                {
                    UIAStorables.triggerFunctionsGrid.StopPhraseRecognizer();

                    HeelAdjustTool.DisableHeelAdjust();
                    GameControlUI.OnDestroy();

                    foreach (Atom atom in SuperController.singleton.GetAtoms()) ActiveClothingList.OnAtomRemoved(atom);

                    SuperController.singleton.onAtomUIDRenameHandlers -= _atomRenameHandle;
                    SuperController.singleton.onAtomRemovedHandlers -= _atomRemoveHandle;
                    SuperController.singleton.onAtomAddedHandlers -= _atomAddedHandle;
                    SuperController.singleton.onSceneLoadedHandlers -= _sceneLoadedHandle;

                    RelativePositionComponentUITab.DeactivateSpawnTest();
                    AtomUtils.DisableHidePersonNodes();

                    //Deregisters with Acidbubbles KeyBindings plugin
                    SuperController.singleton.BroadcastMessage("OnActionsProviderDestroyed", this, SendMessageOptions.DontRequireReceiver);

                    ClothingItemPreset.ClearCachedPresetTextures();

                    GridsControlDisplay.OnDestroy();

                    BAInterop.DeRegisterVAREnablingCallback(this);
                }

            }
            catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
        }

        public void LoadStorablesCatalogue()
        {
            string atomCataloguePath = FileUtils.GetLatestVARPath("JayJayWon.AtomStorablesCatalogue.latest:\\Saves\\PluginData\\JayJayWon\\UIAssist\\AtomStorablesCatalog.json");
            string personCataloguePath = FileUtils.GetLatestVARPath("JayJayWon.AtomStorablesCatalogue.latest:\\Saves\\PluginData\\JayJayWon\\UIAssist\\PersonStorablesCatalog.json");
            string coreControlCataloguePath = FileUtils.GetLatestVARPath("JayJayWon.AtomStorablesCatalogue.latest:\\Saves\\PluginData\\JayJayWon\\UIAssist\\CoreControlStorablesCatalog.json");

            if (FileManagerSecure.FileExists(atomCataloguePath) && FileManagerSecure.FileExists(personCataloguePath))
            {
                var atomCatalog = SuperController.singleton.LoadJSON(atomCataloguePath);
                var personCatalog = SuperController.singleton.LoadJSON(personCataloguePath);
                JSONNode coreControlCatalog = null;
                if (FileManagerSecure.FileExists(coreControlCataloguePath)) coreControlCatalog = SuperController.singleton.LoadJSON(coreControlCataloguePath);

                VAMStorablesCatalog.CreateCatalog(atomCatalog, personCatalog, coreControlCatalog);

                UIAGlobals.storableCataloguesExist = true;
            }
        }

        void Start()
        {
            try
            {
                if (containingAtom.name == "CoreControl" && containingAtom.type == "SessionPluginManager")
                {
#if (!VAM_GT_1_20_77_0)
                    SuperController.LogMessage("UIAssist v2 requires VAM v1.20.77 to run");
                    return;
#endif
                    StartCoroutine(MorphCache.PopulateCache());
                    // Connect with other UIA Plugins and any plugins seeking to register for Gaze Target updates
                    UIAPluginInterop.AcquireAllAvailableBroadcastingPlugins(this);

                    int otherActiveUIACount = UIAPluginInterop.GetOtherActiveUIAssistCount();
                    if ((otherActiveUIACount < 2 && UIAGlobals.vrActive) || otherActiveUIACount < 1)
                    {
                        if (otherActiveUIACount == 0) UIAGlobals.isPrimaryPlugin = true;
                        FileUtils.CreatePluginDataFolder();

                        UIAGlobals.isActivePlugin = true;
                        LoadStorablesCatalogue();

                        UIAStorables.InitStorables();
                        GameControlUI.InitUI();

                        _atomRenameHandle = new SuperController.OnAtomUIDRename(this.AtomNameUpdate);
                        SuperController.singleton.onAtomUIDRenameHandlers += _atomRenameHandle;

                        _atomRemoveHandle = new SuperController.OnAtomRemoved(this.AtomRemovedUpdate);
                        SuperController.singleton.onAtomRemovedHandlers += _atomRemoveHandle;

                        _sceneLoadedHandle = new SuperController.OnSceneLoaded(OnSceneLoaded);
                        SuperController.singleton.onSceneLoadedHandlers += _sceneLoadedHandle;

                        _atomAddedHandle = new SuperController.OnAtomAdded(OnAtomAdded);
                        SuperController.singleton.onAtomAddedHandlers += _atomAddedHandle;

                        _preSceneSaveHandle = new SuperController.OnSceneLoaded(OnPreSceneSaved);
                        SuperController.singleton.onBeforeSceneSaveHandlers += _preSceneSaveHandle;

                        _postSceneSaveHandle = new SuperController.OnSceneLoaded(OnPostSceneSaved);
                        SuperController.singleton.onSceneLoadedHandlers += _postSceneSaveHandle;

                        UIAPProfile.LoadDefaultUIAP();
                        ForceVAMModesTool.Start();

                        UIAPluginUI.InitUI();
                        _timeLastGazeTargetUpate = Time.time;

                        // Initiate integration with Acidbubbles KeyBindings
                        SuperController.singleton.BroadcastMessage("OnActionsProviderAvailable", this, SendMessageOptions.DontRequireReceiver);

                        // Initiate integration with BrowserAssist
                        SuperController.singleton.BroadcastMessage("OnBrowserAssistConsumerAvailable", this, SendMessageOptions.DontRequireReceiver);

                        UIAPluginInterop.ResetVRHand(VRSettings.vrHandControlJSEnum.val);

                        List<string> test = new List<string>();
                        test.Add("Temp");
                        PatreonFeatures.GridLayoutFeature(test);
                        if (test != null && test.Count == 16) _secondValidation = true;

                        foreach (Atom atom in SuperController.singleton.GetAtoms())
                        {
                            if (atom.type == "Person") ActiveClothingList.OnAtomAdded(atom);
                        }

                        if (PatreonFeatures.patreonContentEnabled) UIAStorables.triggerFunctionsGrid.OnTriggerEvent(TriggerFuncType.onUIAStarted);
                    }
                    else
                    {
                        if (UIAGlobals.vrActive) SuperController.LogMessage("A maximum of two UIAssist plugins can be active in VR");
                        else SuperController.LogMessage("A maximum of one UIAssist plugins can be active in Desktop mode");
                    }
                }
                else SuperController.LogMessage("UIAssist can only be loaded as a Session Plugin");

            }
            catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
        }

        void Update()
        {
            try
            {               
                if (UIAGlobals.isActivePlugin)
                {
 
                    float currentTime = Time.time;

                    ForceVAMModesTool.Update();

                    if (SuperController.singleton.GetSelectedAtom() != null && SuperController.singleton.GetSelectedAtom().name != TargetControl.lastSelectedAtom) TargetControl.UpdateSelectedAtom();

                    // Update Gaze targets every 0.1 seconds
                    if (_timeLastGazeTargetUpate < 0f || (currentTime - _timeLastGazeTargetUpate > UIAConsts._TimeBetweenGazeTargetUpdates))
                    {
                        TargetControl.UpdateGazeTargets();
                        RelativePositionComponentUITab.UpdateSpawnTestAtomPosition();

                        _timeLastGazeTargetUpate = currentTime;
                    }

                    // Updates Heel Adjust
                    if (_timeLastHAFileListUpdate < 0f || (currentTime - _timeLastHAFileListUpdate > UIAConsts._TimeBetweenHAFileUpdates))
                    {
                        if (HeelAdjustTool.heelAdjustActiveJSB.val) HeelAdjustTool.UpdateExistingHAFiles();
                        _timeLastHAFileListUpdate = currentTime;
                    }

                    // Upates Active Clothing Lists
                    if (_timeLastClothingUpdate < 0f || ((currentTime - _timeLastClothingUpdate) > UIAConsts._TimeBetweenClothingUpdates))
                    {
                        HeelAdjustTool.ResetAllSavedAtomRotationStates();
                        _timeLastClothingUpdate = currentTime;
                    }

                    // Check and handle if VR Move Atom is active 
                    if (MoveAtomComponent.atomVRMoveActive) MoveAtomComponent.UpdateAtomMove();

                    ActiveClothingList.Update();
                    GameControlUI.Update();
                }
            }
            catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
        }

        public bool _secondValidation = false;

        private float _timeLastClothingUpdate = -1f;
        private float _timeLastHAFileListUpdate = -1f;
        private float _timeLastGazeTargetUpate = -1f;



        SuperController.OnAtomUIDRename _atomRenameHandle;
        SuperController.OnAtomRemoved _atomRemoveHandle;
        SuperController.OnAtomAdded _atomAddedHandle;
        SuperController.OnSceneLoaded _sceneLoadedHandle;
        SuperController.OnSceneLoaded _preSceneSaveHandle;
        SuperController.OnSceneLoaded _postSceneSaveHandle;
    }
}

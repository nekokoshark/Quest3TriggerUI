using System;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SimpleJSON;
using MVR.FileManagementSecure;

namespace JayJayWon
{
    public static class UIAStorables
    {
        public static List<UIAButtonGrid> buttonGridsList ;
        public static UIAButtonGrid quickLaunchButtonGrid ;
        public static UIAButtonGrid globalFunctionsGrid;
        public static UIAButtonGrid triggerFunctionsGrid;

        public static VRSettings vrSettings;
        public static GameControlSettings gameControlSettings ;
        public static CustomTargetGroupSettings customTargetGroupSettings ;
        public static GazeTargetSettings gazeTargetingSettings ;
        public static PresetLoadSettings presetLoadSettings ;

        public static HeelAdjustTool heelAdjustTool ;
        public static ForceVAMModesTool forceVAMModesTool ;
        public static GazeAssistedSelectTool gazeAssistedSelectTool;    
        public static LeapMotionControlTool leapMotionControlTool;
        public static BulkGridSetupTool bulkGridSetupTool;

        public static ButtonSkinComponent defaultButtonSkin;
        public static ImportUIAPGridTool importUIAPGridTool ;

        public static int uiHelpExpandMode = UIHelpExpandMode.half;

        public static void InitStorables()
        {
            buttonGridsList = new List<UIAButtonGrid>();
            quickLaunchButtonGrid = new UIAButtonGrid(6,1,true);
            quickLaunchButtonGrid._buttonSize = "Small";
            globalFunctionsGrid = new UIAButtonGrid(0,1,false,true,false) ;
            triggerFunctionsGrid = new UIAButtonGrid(0, 1, false, false, true);

            vrSettings = new VRSettings();
            gameControlSettings = new GameControlSettings();
            customTargetGroupSettings = new CustomTargetGroupSettings();
            gazeTargetingSettings = new GazeTargetSettings();
            presetLoadSettings = new PresetLoadSettings();
            heelAdjustTool = new HeelAdjustTool();
            forceVAMModesTool = new ForceVAMModesTool();
            gazeAssistedSelectTool = new GazeAssistedSelectTool();
            leapMotionControlTool = new LeapMotionControlTool();
            bulkGridSetupTool = new BulkGridSetupTool();
            defaultButtonSkin = new ButtonSkinComponent(null);
            importUIAPGridTool = new ImportUIAPGridTool();
        }
        public static void AtomNameUpdate(string oldName, string newName)
        {
            foreach (UIAButtonGrid buttonGrid in buttonGridsList)
            {
                buttonGrid.AtomNameUpdate(oldName, newName);
            }
            quickLaunchButtonGrid.AtomNameUpdate(oldName, newName);
            globalFunctionsGrid.AtomNameUpdate(oldName, newName);
            triggerFunctionsGrid.AtomNameUpdate(oldName, newName);
        }

        public static void AtomRemovedUpdate(Atom atom)
        {
            string atomName = atom.name;
            foreach (UIAButtonGrid buttonGrid in buttonGridsList)
            {
                buttonGrid.AtomRemovedUpdate(atomName);
            }
            quickLaunchButtonGrid.AtomRemovedUpdate(atomName);
            globalFunctionsGrid.AtomRemovedUpdate(atomName);
            triggerFunctionsGrid.AtomRemovedUpdate(atomName);
        }
        public static void LoadJSON(JSONClass jc, string uiapPackageName)
        {
            int uiapFormatVersion = 1;

            if (jc["uiapFormatVersion"] != null) uiapFormatVersion = jc["uiapFormatVersion"].AsInt;

            if (jc["uiHelpExpandMode"] != null) uiHelpExpandMode = EnumManifest.GetEnumValFromStoreVal(UIHelpExpandMode.enumManifestName, jc["uiHelpExpandMode"]);

            if (uiapFormatVersion == 1)
            {
                vrSettings.RestoreFromJSON(jc);
                gameControlSettings.RestoreFromJSON(jc);
                heelAdjustTool.RestoreFromJSON(jc["ToolBox"].AsObject);
                gazeAssistedSelectTool.RestoreFromJSON(jc["ToolBox"].AsObject);
                forceVAMModesTool.RestoreFromJSON(jc["ToolBox"].AsObject);
                customTargetGroupSettings.LoadJSON(jc["ToolBox"].AsObject);
                gazeTargetingSettings.RestoreFromJSON(jc["ToolBox"].AsObject);
                presetLoadSettings.RestoreFromJSON(jc["ToolBox"].AsObject);
                leapMotionControlTool.RestoreFromJSON(jc["ToolBox"].AsObject);
            }
            else
            {
                if (jc["VRSettings"] != null) vrSettings.RestoreFromJSON(jc["VRSettings"].AsObject);
                if (jc["GridControlSettings"] != null) gameControlSettings.RestoreFromJSON(jc["GridControlSettings"].AsObject);
                if (jc["LeapMotionControlTool"] != null) leapMotionControlTool.RestoreFromJSON(jc["LeapMotionControlTool"].AsObject);
                if (jc["HeelAdjustTool"] != null) heelAdjustTool.RestoreFromJSON(jc["HeelAdjustTool"].AsObject);
                if (jc["GazeAssistedSelectTool"] != null) gazeAssistedSelectTool.RestoreFromJSON(jc["GazeAssistedSelectTool"].AsObject);
                if (jc["ForceVAMModesTool"] != null) forceVAMModesTool.RestoreFromJSON(jc["ForceVAMModesTool"].AsObject);
                if (jc["CustomTargetGroupSettings"] != null) customTargetGroupSettings.LoadJSON(jc["CustomTargetGroupSettings"].AsObject);
                if (jc["GazeTargetingSettings"] != null) gazeTargetingSettings.RestoreFromJSON(jc["GazeTargetingSettings"].AsObject);
                if (jc["PresetLoadSettings"] != null) presetLoadSettings.RestoreFromJSON(jc["PresetLoadSettings"].AsObject);
                
            }

            if (jc["DefaultButtonSkin"] != null)
            {
                defaultButtonSkin.LoadJSON((JSONClass)jc["DefaultButtonSkin"], uiapPackageName, uiapFormatVersion);
            }

            if (jc["QuickLaunchButtonGrid"] != null && PatreonFeatures.patreonContentEnabled && jc["QuickLaunchButtonGrid"]["gridRows"].AsInt>0 && jc["QuickLaunchButtonGrid"]["gridColumns"].AsInt > 0) quickLaunchButtonGrid.LoadJSON((JSONClass)jc["QuickLaunchButtonGrid"], uiapPackageName, uiapFormatVersion);
            else
            {
                quickLaunchButtonGrid = new UIAButtonGrid(6, 1, true);
                quickLaunchButtonGrid._buttonSize = "Small";
            }

            if (jc["globalFunctionsGrid"] != null && PatreonFeatures.patreonContentEnabled) globalFunctionsGrid.LoadJSON((JSONClass)jc["globalFunctionsGrid"], uiapPackageName, uiapFormatVersion);
            else globalFunctionsGrid = new UIAButtonGrid(0, 1, false,true,false);

            if (jc["triggerFunctionsGrid"] != null && PatreonFeatures.patreonContentEnabled) triggerFunctionsGrid.LoadJSON((JSONClass)jc["triggerFunctionsGrid"], uiapPackageName, uiapFormatVersion);
            else triggerFunctionsGrid = new UIAButtonGrid(0, 1, false, false, true);

            if (jc["ButtonGrids"] != null)
            {
                JSONArray gridArrayJSON = jc["ButtonGrids"].AsArray;
                for (int i = 0; i < gridArrayJSON.Count; i++)
                {
                    
                    JSONClass gridJSON = gridArrayJSON[i].AsObject;
                    UIAButtonGrid grid;
                    if (i >= buttonGridsList.Count)
                    {
                        grid = new UIAButtonGrid(3,3);
                        buttonGridsList.Add(grid);
                    }
                    else { grid = buttonGridsList[i]; }

                    grid.LoadJSON(gridJSON, uiapPackageName, uiapFormatVersion);
                }
            }
            SwitchUIAGridComponent.RefreshAllGridReferences();

            foreach (UIAButtonGrid grid in buttonGridsList) grid.PostLoadJSON();
            quickLaunchButtonGrid.PostLoadJSON();
            globalFunctionsGrid.PostLoadJSON();
            triggerFunctionsGrid.PostLoadJSON();
        }

        public static JSONClass GetJSON()
        {
            JSONClass jc = new JSONClass();

            jc["uiapFormatVersion"].AsInt = UIAConsts.uiapFormatVerion;
            jc["PluginVersion"] = UIAConsts.pluginVersion;
            jc["PluginMajorVersionID"].AsInt = UIAConsts.pluginMajorVersion;
            jc["PluginMinorVersionID"].AsInt = UIAConsts.pluginMinorVersion;
            jc["PluginDefectVersionID"].AsInt = UIAConsts.pluginDefectVersion;
            jc["PatreonOnlyVersion"].AsBool = PatreonFeatures.patreonContentEnabled;
            jc["uiHelpExpandMode"] = EnumManifest.GetStoreVal(UIHelpExpandMode.enumManifestName, uiHelpExpandMode);

            JSONArray gridArray = new JSONArray();
            buttonGridsList.ForEach((grid) =>
            {
                gridArray.Add(grid.GetJSON());
            });

            jc["ButtonGrids"] = gridArray;
            if(PatreonFeatures.patreonContentEnabled) jc["QuickLaunchButtonGrid"] = quickLaunchButtonGrid.GetJSON();
            if (PatreonFeatures.patreonContentEnabled) jc["globalFunctionsGrid"] = globalFunctionsGrid.GetJSON();
            if (PatreonFeatures.patreonContentEnabled) jc["triggerFunctionsGrid"] = triggerFunctionsGrid.GetJSON();

            jc["VRSettings"] = vrSettings.GetJSON();
            jc["GridControlSettings"] = gameControlSettings.GetJSON();
            jc["LeapMotionControlTool"]  = leapMotionControlTool.GetJSON();
            jc["HeelAdjustTool"] = heelAdjustTool.GetJSON();
            jc["GazeAssistedSelectTool"] = gazeAssistedSelectTool.GetJSON();
            jc["ForceVAMModesTool"] = forceVAMModesTool.GetJSON();
            jc["CustomTargetGroupSettings"] = customTargetGroupSettings.GetJSON();
            jc["GazeTargetingSettings"] = gazeTargetingSettings.GetJSON();
            jc["PresetLoadSettings"] = presetLoadSettings.GetJSON();

            jc["DefaultButtonSkin"] = defaultButtonSkin.GetJSON();

            return jc;
        }

        public static void CreateDefaultButtonGrids()
        {
            UIAButtonGrid defaultBG = new UIAButtonGrid(3, 3);
            buttonGridsList.Add(defaultBG);

            defaultBG.buttonList[0].buttonOperations[0].buttonOpCategoryJSEnum.val = UIAButtonCategory.clothing;

            defaultBG.buttonList[1].buttonOperations[0].buttonOpCategoryJSEnum.val = UIAButtonCategory.presets;
            defaultBG.buttonList[1].buttonOperations[0].buttonOpTypeJSEnum.val = UIAButtonOpType.loadClothPreset;
            defaultBG.buttonList[1].buttonOperations[0].fileReferenceDict[FileReferenceTypes.clothingPreset].filePathJSString.val = "MeshedVR.PresetsPack.latest:/Custom/Atom/Person/Clothing/MeshedVR/PresetsPack/Preset_VaM_Seductress_Sim.vap";
            defaultBG.buttonList[1].autoLabelJSBool.val = false;
            defaultBG.buttonList[1].buttonOperations[0].targetComponent.targetNameJSMultiEnum.valTopEnum = LastViewedTargetType.lastViewedFemale;

            defaultBG.buttonList[2].buttonOperations[0].buttonOpCategoryJSEnum.val = UIAButtonCategory.presets;
            defaultBG.buttonList[2].buttonOperations[0].fileReferenceDict[FileReferenceTypes.appearancePreset].filePathJSString.val = "MeshedVR.PresetsPack.latest:/Custom/Atom/Person/Appearance/MeshedVR/PresetsPack/Preset_Ren_Tara.vap";
            defaultBG.buttonList[2].autoLabelJSBool.val = false;
            defaultBG.buttonList[2].buttonOperations[0].targetComponent.targetNameJSMultiEnum.valTopEnum = LastViewedTargetType.lastViewedFemale;

            defaultBG.RecalcGazeSelections();
        }
        public static void UpdateCTGName(string oldName, string newName)
        {
            foreach (UIAButtonGrid bg in buttonGridsList)
            {
                foreach (UIAButton button in bg.buttonList)
                {
                    button.UpdateCTGName(oldName, newName);
                }
                bg._gazeSelectionCTGDic.Add(newName, bg._gazeSelectionCTGDic[oldName]);
                bg._gazeSelectionCTGDic.Remove(oldName);
            }
            TargetControl.lastViewedCTGDic.Add(newName, TargetControl.lastViewedCTGDic[oldName]);
            TargetControl.lastViewedCTGDic.Remove(oldName);
        }
        public static void RemoveCTGName(string cgtName)
        {
            foreach (UIAButtonGrid bg in buttonGridsList)
            {
                foreach (UIAButton button in bg.buttonList)
                {
                    button.RemoveCTGName(cgtName);
                }
                bg._gazeSelectionCTGDic.Remove(cgtName);
                bg.RecalcGazeSelections();
            }
            TargetControl.lastViewedCTGDic.Remove(cgtName);;
        }
        public static void AddCTGName(string cgtName)
        {
            foreach (UIAButtonGrid bg in buttonGridsList)
            {
                bg.RecalcGazeSelections();
            }
            TargetControl.lastViewedCTGDic.Add(cgtName, "");
        }
    }

    public class VRSettings : JSONStorableObject
    {
        public static JSONStorableEnumStringChooser vrHandControlJSEnum;
        public static JSONStorableEnumStringChooser vrPinModeJSEnum;
        public static JSONStorableEnumStringChooser vrUIAttachPositionJSEnum;
        public static JSONStorableBool autoPinOnGridOpenJSB;
        public static JSONStorableBool autoUnPinOnGridCloseJSB;
        public static JSONStorableEnumStringChooser gazeMenuSelectModeJSEnum;
        public static JSONStorableBool gazeActivateOpenVRGridJSB;
        public static JSONStorableBool gazeActivateCloseVRGridJSB;
        public static JSONStorableColor gazeSelectButtonColorJSC;
        public static JSONStorableBool hideWristUIWithShowVRHandsJSB;
        public static JSONStorableFloat vrHUDHorizontalOffsetJSF;
        public static JSONStorableFloat vrHUDVerticalOffsetJSF;
        public static JSONStorableFloat vrHUDDepthOffsetJSF;
        public static JSONStorableFloat vrPinMinOffsetJSF;
        public static JSONStorableFloat vrWristOpacityJSF;

        public static JSONStorableEnumStringChooser LcontrollerDoubleSelectModeJSE;
        public static JSONStorableEnumStringChooser RcontrollerDoubleSelectModeJSE;

        public VRSettings() : base()
        {
            vrHandControlJSEnum = new JSONStorableEnumStringChooser("vrHandControl", VRHandControl.enumManifestName, VRHandControl.vamUI, "Attach UI to",VRHandControlUpdated);
            RegisterParam(vrHandControlJSEnum);

            vrPinModeJSEnum = new JSONStorableEnumStringChooser("vrPinMode", VRPinMode.enumManifestName, VRPinMode.staticMode, "Detached UI Mode",VRPinModeUpdated);
            RegisterParam(vrPinModeJSEnum);

            vrUIAttachPositionJSEnum = new JSONStorableEnumStringChooser("vrUIAttachPosition",VAMUIattachPosition.enumManifestName,VAMUIattachPosition.rightBottom,"Launch button position", VRUIAttachPositionUpdated);
            RegisterParam(vrUIAttachPositionJSEnum);

            gazeMenuSelectModeJSEnum = new JSONStorableEnumStringChooser("gazeMenuSelectMode", VRGazeUIControlMode.enumManifestName, VRGazeUIControlMode.wristUIOnly, "Gaze UI Control",GazeUIControlUpdated);
            RegisterParam(gazeMenuSelectModeJSEnum);

            autoPinOnGridOpenJSB = new JSONStorableBool("autoPinOnScreenOpen", false);
            autoUnPinOnGridCloseJSB = new JSONStorableBool("autoUnPinOnScreenClose", false);
            RegisterParam(autoPinOnGridOpenJSB);
            RegisterParam(autoUnPinOnGridCloseJSB);

            gazeActivateOpenVRGridJSB = new JSONStorableBool("gazeActivateOpenVRScreen", true);
            gazeActivateCloseVRGridJSB = new JSONStorableBool("gazeActivateCloseVRScreen", true);
            hideWristUIWithShowVRHandsJSB = new JSONStorableBool("hideWristUIWithShowVRHands", false);
            HSVColor hsvColor = new HSVColor();
            Color gazeButtonColor = new Color(128f / 255f, 142f / 255f, 216f / 255f, 1f);
            Color.RGBToHSV(gazeButtonColor, out hsvColor.H, out hsvColor.S, out hsvColor.V);
            gazeSelectButtonColorJSC = new JSONStorableColor("gazeSelectButtonColor", hsvColor);
            RegisterParam(gazeSelectButtonColorJSC);
            RegisterParam(gazeActivateOpenVRGridJSB);
            RegisterParam(gazeActivateCloseVRGridJSB);
            RegisterParam(hideWristUIWithShowVRHandsJSB);

            vrHUDHorizontalOffsetJSF = new JSONStorableFloat("vrHUDHorizontalOffset", 0.5f, 0.3f, 0.8f);
            vrHUDVerticalOffsetJSF = new JSONStorableFloat("vrHUDVerticalOffset", 0.5f, 0.2f, 0.8f);
            vrHUDDepthOffsetJSF = new JSONStorableFloat("vrHUDDepth", 0.6f, 0.1f, 2f);
            vrPinMinOffsetJSF = new JSONStorableFloat("vrPinMinOffset", 0.7f, 0.3f, 1f);
            vrWristOpacityJSF = new JSONStorableFloat("vrWristOpacity", 1f, 0f, 1f);
            RegisterParam(vrHUDHorizontalOffsetJSF);
            RegisterParam(vrHUDVerticalOffsetJSF);
            RegisterParam(vrHUDDepthOffsetJSF);
            RegisterParam(vrPinMinOffsetJSF);
            RegisterParam(vrWristOpacityJSF);

            LcontrollerDoubleSelectModeJSE = new JSONStorableEnumStringChooser("LcontrollerDoubleSelectMode", VRControllerDoubleSelectMode.enumManifestName, VRControllerDoubleSelectMode.togglePin, "Left Controller Double Select");
            RcontrollerDoubleSelectModeJSE = new JSONStorableEnumStringChooser("RcontrollerDoubleSelectMode", VRControllerDoubleSelectMode.enumManifestName, VRControllerDoubleSelectMode.togglePin, "Right Controller Double Select");
            RegisterParam(LcontrollerDoubleSelectModeJSE);
            RegisterParam(RcontrollerDoubleSelectModeJSE);
        }

        private void VRHandControlUpdated(string value)
        {
            UIAPluginInterop.ResetVRHand(vrHandControlJSEnum.val);
            GameControlUI.RefreshGameControlUIModes();
            GridsControlDisplay.ResetButtonPositions();
            UpdatePinModeChoices();
        }

        private void VRUIAttachPositionUpdated(int value)
        {
            GridsControlDisplay.ResetVAMUILaunchButtonPosition();
        }
        private void VRPinModeUpdated(string value)
        {
            GameControlUI.RefreshGameControlUIModes();
        }

        private void GazeUIControlUpdated(string value)
        {
            GameControlUI.RefreshGameControlUIModes();
        }

        private void UpdatePinModeChoices()
        {
            
            if (vrHandControlJSEnum.val!=VRHandControl.disabled) vrPinModeJSEnum.SetEnumChoices(VRPinMode.enumManifestName);
            else
            {
                List<int> exclusions = new List<int>();
                exclusions.Add(VRPinMode.disabled);
                vrPinModeJSEnum.SetEnumChoices(VRPinMode.enumManifestName,exclusions);
            }
        }

        public override void RestoreFromJSON(JSONClass jc, string _paramName = null)
        {
            base.RestoreFromJSON(jc, _paramName);
            UpdatePinModeChoices();
        }
    }

    public class GameControlSettings : JSONStorableObject
    {
        public static JSONStorableBool displayGazeSelectionsJSB;
        public static JSONStorableBool displayGridLabelsJSB;
        public static JSONStorableBool activateQuickLaunchBarJSB;
        public static JSONStorableBool aceClothingItemUIOpenJSB;
        public static JSONStorableBool i24hClockEnableJSB;

        public static JSONStorableFloat desktopUIScaleJSF;
        public static JSONStorableFloat desktopHOffsetJSF;
        public static JSONStorableFloat desktopVOffsetJSF;

        public static JSONStorableFloat desktopPixelsPUJSF;
        public static JSONStorableFloat vrPixelsPUJSF;

        public GameControlSettings() : base()
        {
            displayGazeSelectionsJSB = new JSONStorableBool("displayGazeSelections", true, RefreshButtonGrids);
            displayGridLabelsJSB = new JSONStorableBool("displayScreenLabels", false, RefreshButtonGrids);
            activateQuickLaunchBarJSB = new JSONStorableBool("activateQuickLaunchBar", false, QLBToggled);
            aceClothingItemUIOpenJSB = new JSONStorableBool("aceClothingItemUIOpen", true);

            i24hClockEnableJSB = new JSONStorableBool("24hClockEnable", false, RefreshButtonGrids);
            desktopUIScaleJSF = new JSONStorableFloat("desktopUIScale", 1f, 0.2f, 2f);
            desktopHOffsetJSF = new JSONStorableFloat("desktopHOffset", 0.9f, 0f, 1f);
            desktopVOffsetJSF = new JSONStorableFloat("desktopVOffset", 0.065f, 0f, 1f);

            desktopPixelsPUJSF = new JSONStorableFloat("desktopPixelsPU", 1.8f, 0.5f, 5f);
            vrPixelsPUJSF = new JSONStorableFloat("VRPixelsPU", 4f, 1f, 10f);

            RegisterParam(displayGazeSelectionsJSB);
            RegisterParam(displayGridLabelsJSB);
            RegisterParam(activateQuickLaunchBarJSB);
            RegisterParam(aceClothingItemUIOpenJSB);
            RegisterParam(i24hClockEnableJSB);

            RegisterParam(desktopUIScaleJSF);
            RegisterParam(desktopHOffsetJSF);
            RegisterParam(desktopVOffsetJSF);

            RegisterParam(desktopPixelsPUJSF);
            RegisterParam(vrPixelsPUJSF);
        }

        private void QLBToggled(bool value)
        {
            if (ButtonSetupPluginUI.gridSelectorJSSC!=null && ButtonSetupPluginUI.gridSelectorJSSC.val == "QL" ) ButtonSetupPluginUI.gridSelectorJSSC.val = "1";
            RefreshButtonGrids(value);
        }

        private void RefreshButtonGrids(bool value)
        {           
            GameControlUI.RefreshWristUIButtonGrid();
        }
    }

    public class LeapMotionControlTool:JSONStorableObject
    {
        public LeapMotionControlTool()
        {
            leapMotionControlActiveJSB = new JSONStorableBool("leapMotionControlActive", true, SwitchControl);
            leapMotionAutoEnableDisableJSE = new JSONStorableEnumStringChooser("leapMotionAutoEnableDisableEnum", LeftRight.enumManifestName, LeftRight.neither, "Leap Hands enabled with VR Controller proximity");

            leapFreeMoveControlActiveJSB = new JSONStorableBool("leapFreeMoveControlActive", true, SwitchControl);
            leapFreeMoveFemaleRotationActiveJSB = new JSONStorableBool("leapFreeMoveFemaleRotationActive", true);
            leapFreeMoveMaleRotationActiveJSB = new JSONStorableBool("leapFreeMoveMaleRotationActive", false);

            RegisterParam(leapMotionControlActiveJSB);
            RegisterParam(leapMotionAutoEnableDisableJSE);
            RegisterParam(leapFreeMoveControlActiveJSB);
            RegisterParam(leapFreeMoveFemaleRotationActiveJSB);
            RegisterParam(leapFreeMoveMaleRotationActiveJSB);
        }
        private void SwitchControl(bool val)
        {
//            _mvrScript.RefreshLeftUIDisplay();
        }

        public static JSONStorableBool leapMotionControlActiveJSB;
        public static JSONStorableBool leapFreeMoveControlActiveJSB;
        public static JSONStorableBool leapFreeMoveFemaleRotationActiveJSB;
        public static JSONStorableBool leapFreeMoveMaleRotationActiveJSB;

        public static JSONStorableEnumStringChooser leapMotionAutoEnableDisableJSE;

    }

    public class PresetLoadSettings : JSONStorableObject
    {
        public PresetLoadSettings()
        {
            suppressScaleLoadJSB = new JSONStorableBool("suppressScaleLoad", false);
            suppressPresetLocksJSB = new JSONStorableBool("suppressPresetLocks", true);
            suppressClothingLoadJSB = new JSONStorableBool("suppressClothingLoad",false);
            onlyLoadClothingFromAppPresetJSB = new JSONStorableBool("onlyLoadClothingFromAppPreset", false);
            RegisterParam(suppressScaleLoadJSB);
            RegisterParam(suppressPresetLocksJSB);
            RegisterParam(suppressClothingLoadJSB);
            RegisterParam(onlyLoadClothingFromAppPresetJSB);
        }

        public static JSONStorableBool suppressScaleLoadJSB;
        public static JSONStorableBool suppressPresetLocksJSB;
        public static JSONStorableBool suppressClothingLoadJSB;
        public static JSONStorableBool onlyLoadClothingFromAppPresetJSB;
    }

    public class GazeAssistedSelectTool : JSONStorableObject
    {
        public GazeAssistedSelectTool()
        {
            gazeAssistActiveJSB = new JSONStorableBool("gazeAssistedSelectActive", false);
            RegisterParam(gazeAssistActiveJSB);
        }

        public static JSONStorableBool gazeAssistActiveJSB;

    }
    public class GazeTargetSettings : JSONStorableObject
    {
        public GazeTargetSettings()
        {
            RegisterParam(defaultGazeTargetJSEnum);
            RegisterParam(gazeSelectOffAtomsJSB);
        }

        public static JSONStorableEnumStringChooser defaultGazeTargetJSEnum = new JSONStorableEnumStringChooser("defaultGazeTarget", LastViewedTargetType.enumManifestName, LastViewedTargetType.lastViewedFemale, "Default Gaze Target");

        public static JSONStorableBool gazeSelectOffAtomsJSB = new JSONStorableBool("gazeSelectOffAtoms", false);
    }

    public class BulkGridSetupTool : JSONStorableObject
    {
        public BulkGridSetupTool()
        {
            buttonsTypeJSEnum = new JSONStorableEnumStringChooser("buttonsType", BulkGridSetupButtonTypes.enumManifestName, UIAButtonOpType.loadAppPreset, "New Grid Button Types", ButtonsTypeUpdated);
            buttonsTargetTypeJSEnum = new JSONStorableEnumStringChooser("targetsType", BulkGridSetupTargetTypes.enumManifestName, LastViewedTargetType.lastViewedFemale, "");
            useMergeLoadJSB = new JSONStorableBool("mergeLoad", false);
            suppressClothingLoadJSB = new JSONStorableBool("suppressClothingLoad", false);
            suppressPersonScaleLoadJSB = new JSONStorableBool("suppressScaleLoad", false);

        }

        private void ButtonsTypeUpdated(string val)
        {
 //           _mvrScript.RefreshLeftUIDisplay();
        }

        public static void FolderSelected(string folder)
        {
            if (folder != "" && FileManagerSecure.DirectoryExists(folder))
            {
                string[] files = FileManagerSecure.GetFiles(folder, "*.vap");
                int fileCount = files.Count<string>();

                int cols = 0;
                int rows = 0;
                string buttonSize = "";

                if (fileCount <= 9)
                {
                    cols = 3;
                    rows = 3;
                    buttonSize = "Medium";
                }
                else if (fileCount <= 16)
                {
                    cols = 4;
                    rows = 4;
                    buttonSize = "Medium";
                }
                else if (fileCount <= 20)
                {
                    cols = 5;
                    rows = 5;
                    buttonSize = "Medium";
                }
                else if (fileCount <= 30)
                {
                    cols = 5;
                    rows = 6;
                    buttonSize = "Medium";
                }
                else if (fileCount <= 42)
                {
                    cols = 6;
                    rows = 7;
                    buttonSize = "Small";
                }
                else if (fileCount <= 49)
                {
                    cols = 7;
                    rows = 7;
                    buttonSize = "Mini";
                }
                else if (fileCount <= 64)
                {
                    cols = 8;
                    rows = 8;
                    buttonSize = "Mini";
                }
                else
                {
                    cols = 9;
                    rows = 9;
                    buttonSize = "Mini";
                }

                UIAButtonGrid newBG = new UIAButtonGrid(cols, rows);
                newBG._buttonSize = buttonSize;
                UIAStorables.buttonGridsList.Add(newBG);

                for (int i = 0; i < Math.Min(81, fileCount); i++)
                {
                    UIAButton button = newBG.buttonList[i];
                    UIAButtonOperation buttonOp = button.buttonOperations[0];
                    buttonOp.buttonOpCategoryJSEnum.val = UIAButtonCategory.presets;
                    buttonOp.buttonOpTypeJSEnum.val = UIAButtonOpType.loadAppPreset;
                    buttonOp.fileReferenceDict[FileReferenceTypes.appearancePreset].mergeLoadPresetJSBool.val = useMergeLoadJSB.val;

                    buttonOp.buttonOpCategoryJSEnum.val = UIAButtonCategory.presets;
                    buttonOp.buttonOpTypeJSEnum.val = buttonsTypeJSEnum.val;

                    FileReference fileRef = buttonOp.fileReferenceDict[buttonOp.GetFileReferenceTypes().First()];
                    fileRef.mergeLoadPresetJSBool.val = useMergeLoadJSB.val;

                    if (buttonsTypeJSEnum.val == UIAButtonOpType.loadAppPreset)
                    {
                        AppearancePresetComponent appComp = buttonOp.appearancePresetComponent;
                        appComp.suppressClothingLoadJSBool.val = suppressClothingLoadJSB.val;
                        appComp.suppressPersonScaleLoadJSBool.val = suppressPersonScaleLoadJSB.val;
                    }
                    fileRef.filePathJSString.val = FileManagerSecure.NormalizePath(files[i]);
                    button.autoLabelJSBool.val = false;

                    TargetComponent targetComp = buttonOp.targetComponent;
                    if (buttonsTargetTypeJSEnum.displayVal.StartsWith("User chosen"))
                        targetComp.targetCategoryJSEnum.val = TargetCategory.userChosenAtom;

                    targetComp.targetNameJSMultiEnum.valTopEnum = buttonsTargetTypeJSEnum.val;
                }
                ButtonSetupPluginUI.gridSelectorJSSC.val = UIAStorables.buttonGridsList.Count.ToString();
                newBG.RecalcGazeSelections();
            }
        }

        public static JSONStorableEnumStringChooser buttonsTypeJSEnum;
        public static JSONStorableEnumStringChooser buttonsTargetTypeJSEnum;
        public static JSONStorableBool useMergeLoadJSB;
        public static JSONStorableBool suppressClothingLoadJSB;
        public static JSONStorableBool suppressPersonScaleLoadJSB;
    }


    public class ForceVAMModesTool : JSONStorableObject
    {
        public ForceVAMModesTool()
        {
            forceEditModeLoadSceneJSB = new JSONStorableBool("forceEditModeLoadScene", false);
            forceEditModeOpenUIJSB = new JSONStorableBool("forceEditModeOpenUI", false);
            forcePlayModeCloseUIJSB = new JSONStorableBool("forcePlayModeCloseUI", false);
            forceVAMUIVerticalJSB = new JSONStorableBool("forceVAMUIVertical", false);
            forceVSyncOnInDesktopJSB = new JSONStorableBool("forceVSyncOnInNoVR", false);
            forceVSyncOffInVRJSB = new JSONStorableBool("forceVSyncOffInVR", false);
            forceUIOnTopInDesktopJSB = new JSONStorableBool("forceUIOnTopInDesktop", false);
            forceDisableUIOnTopInVRJSB = new JSONStorableBool("forceDisableUIOnTopInVR", false);

            forceDisableFreezePhysicsOnGrabNewPersonJSB = new JSONStorableBool("forceDisableFreezePhysicsOnGrabNewPerson", false);
            forceDisableFreezePhysicsOnGrabSceneLoadJSB = new JSONStorableBool("forceDisableFreezePhysicsOnGrabSceneLoad", false);
            RegisterParam(forceEditModeLoadSceneJSB);
            RegisterParam(forceEditModeOpenUIJSB);
            RegisterParam(forcePlayModeCloseUIJSB);
            RegisterParam(forceVAMUIVerticalJSB);
            RegisterParam(forceVSyncOnInDesktopJSB);
            RegisterParam(forceVSyncOffInVRJSB);
            RegisterParam(forceUIOnTopInDesktopJSB);
            RegisterParam(forceDisableUIOnTopInVRJSB);
            RegisterParam(forceDisableFreezePhysicsOnGrabNewPersonJSB);
            RegisterParam(forceDisableFreezePhysicsOnGrabSceneLoadJSB);
        }

        public static void OnSceneLoaded()
        {
            if (forceEditModeLoadSceneJSB.val) UIAGlobals.mvrScript.StartCoroutine(EnableEditModeCoroutine());

            if (forceDisableFreezePhysicsOnGrabSceneLoadJSB.val)
            {
                foreach (Atom atom in AtomUtils.GetAtoms())
                {
                    if (atom.type == "Person")
                    {
                        JSONStorable controlStorable = atom.GetStorableByID("control");
                        JSONStorableBool grabFreezePhys = controlStorable.GetBoolJSONParam("freezeAtomPhysicsWhenGrabbed");
                        grabFreezePhys.val = false;
                    }
                }
            }
        }

        public static void OnAtomAdded(Atom atom)
        {
            if (atom.type == "Person" && !SuperController.singleton.isLoading && ForceVAMModesTool.forceDisableFreezePhysicsOnGrabNewPersonJSB.val)
            {
                JSONStorable controlStorable = atom.GetStorableByID("control");
                JSONStorableBool grabFreezePhys = controlStorable.GetBoolJSONParam("freezeAtomPhysicsWhenGrabbed");
                grabFreezePhys.val = false;
            }
        }

        public static void OnEnable()
        {
            if (SuperController.singleton.mainHUD == null || !SuperController.singleton.mainHUD.gameObject.activeSelf) _latestVAMUIOpen = false;
            else _latestVAMUIOpen = true;
        }

        public static void Start()
        {
            if (SuperController.singleton.mainHUD == null || !SuperController.singleton.mainHUD.gameObject.activeSelf) _latestVAMUIOpen = false;
            else _latestVAMUIOpen = true;

            if (forceVSyncOffInVRJSB.val && UIAGlobals.vrActive) UserPreferences.singleton.desktopVsync = false;
            if (forceDisableUIOnTopInVRJSB.val && UIAGlobals.vrActive) UserPreferences.singleton.overlayUI = false;
            if (forceVSyncOnInDesktopJSB.val && !UIAGlobals.vrActive) UserPreferences.singleton.desktopVsync = true;
            if (forceUIOnTopInDesktopJSB.val && !UIAGlobals.vrActive) UserPreferences.singleton.overlayUI = true;
        }

        public static void Update()
        {
            bool currentVAMUIOpen = true;

            if (SuperController.singleton.mainHUD == null || !SuperController.singleton.mainHUD.gameObject.activeSelf) currentVAMUIOpen = false;

            if (currentVAMUIOpen != _latestVAMUIOpen)
            {
                if (currentVAMUIOpen)
                {
                    if (forceEditModeOpenUIJSB.val) SuperController.singleton.gameMode = SuperController.GameMode.Edit;
                }
                if (!currentVAMUIOpen && forcePlayModeCloseUIJSB.val) SuperController.singleton.gameMode = SuperController.GameMode.Play;

                _latestVAMUIOpen = currentVAMUIOpen;
            }
        }

        private static IEnumerator EnableEditModeCoroutine()
        {
            while (SuperController.singleton.isLoading)
                yield return 0;

            while (SuperController.singleton.freezeAnimation)
                yield return 0;

            yield return 0;

            SuperController.singleton.gameMode = SuperController.GameMode.Edit;
        }

        public static JSONStorableBool forceEditModeLoadSceneJSB;
        public static JSONStorableBool forceEditModeOpenUIJSB;
        public static JSONStorableBool forcePlayModeCloseUIJSB;
        public static JSONStorableBool forceVAMUIVerticalJSB;
        public static JSONStorableBool forceVSyncOnInDesktopJSB;
        public static JSONStorableBool forceVSyncOffInVRJSB;
        public static JSONStorableBool forceUIOnTopInDesktopJSB;
        public static JSONStorableBool forceDisableUIOnTopInVRJSB;

        public static JSONStorableBool forceDisableFreezePhysicsOnGrabNewPersonJSB;
        public static JSONStorableBool forceDisableFreezePhysicsOnGrabSceneLoadJSB;

        public static bool _latestVAMUIOpen;
    }
    public class CustomTargetGroupSettings
    {
        public CustomTargetGroupSettings()
        {

        }

        public static List<string> GetCurrentCTGNamesList()
        {
            List<string> cgtNamesList = new List<string>();
            foreach (CustomTargetGroupType cgt in customTargetGroupTypeList)
            {
                cgtNamesList.Add(cgt.cgtNameJSS.val);
            }
            return (cgtNamesList);
        }
        public static bool CGTListNameDuplicate(string cgtName)
        {
            int count = 0;
            foreach (CustomTargetGroupType cgt in customTargetGroupTypeList)
            {
                if (cgt.cgtNameJSS.val == cgtName) count++;
                if (count > 1) return true;
            }
            return (false);
        }
        public static string GetNextUniqueCGTName()
        {
            List<string> cgtNamesList = GetCurrentCTGNamesList();
            for (int i = 1; i <= 1000; i++)
            {
                if (!cgtNamesList.Contains("CTG" + i.ToString())) return ("CTG" + i.ToString());
            }
            return (null);
        }
        public static string AddNewCTG()
        {
            CustomTargetGroupType cgt = new CustomTargetGroupType();
            string newName = GetNextUniqueCGTName();
            customTargetGroupTypeList.Add(cgt);

            cgt.cgtNameJSS.defaultVal = newName;
            cgt.cgtNameJSS.valNoCallback = newName;
            UIAStorables.AddCTGName(newName);

            return newName;
        }

        public static void DeleteCTG(string ctgName)
        {
            CustomTargetGroupType ctg = GetCGTFromName(ctgName);
            if (ctg != null)
            {
                customTargetGroupTypeList.Remove(ctg);
                UIAStorables.RemoveCTGName(ctg.cgtNameJSS.val);
            }
        }

        public static CustomTargetGroupType GetCGTFromName(string cgtName)
        {
            foreach (CustomTargetGroupType cgt in customTargetGroupTypeList)
            {
                if (cgt.cgtNameJSS.val == cgtName) return (cgt);
            }

            return (null);
        }

        public JSONClass GetJSON()
        {
            JSONClass jc = new JSONClass();
            JSONArray customGazeTargetTypeArray = new JSONArray();
            customTargetGroupTypeList.ForEach((cgt) =>
            {
                customGazeTargetTypeArray.Add(cgt.GetJSON());
            });

            jc["customGazeTargets"] = customGazeTargetTypeArray;

            return (jc);
        }

        public void LoadJSON(JSONClass jc)
        {
            customTargetGroupTypeList.Clear();
            TargetControl.lastViewedCTGDic.Clear();
            if (jc["customGazeTargets"] != null)
            {
                JSONArray customGazeTargetTypeArray = jc["customGazeTargets"].AsArray;
                for (int i = 0; i < customGazeTargetTypeArray.Count; i++)
                {
                    CustomTargetGroupType cgt = new CustomTargetGroupType();
                    cgt.RestoreFromJSON(customGazeTargetTypeArray[i].AsObject);
                    cgt.cgtNameJSS.defaultVal = cgt.cgtNameJSS.val;
                    customTargetGroupTypeList.Add(cgt);
                    TargetControl.lastViewedCTGDic.Add(cgt.cgtNameJSS.val, "");
                }
            }

        }

        public static List<CustomTargetGroupType> customTargetGroupTypeList = new List<CustomTargetGroupType>();
    }

    public class CustomTargetGroupType : JSONStorableObject
    {
        public CustomTargetGroupType()
        {
            cgtNameJSS = new JSONStorableString("_cgtName", "", SwitchCGTName);
            cgtAtomCategoryJSEnum = new JSONStorableEnumStringChooser("_cgtAtomCategory", AtomCategories.enumManifestName, AtomCategories.misc, "Atom Category", SwitchAtomCategory);
            cgtAtomTypeJSEnum = new JSONStorableEnumStringChooser("_cgtAtomType", AtomTypes.enumManifestName, AtomTypes.cua, "Atom Type", AtomTypes.GetAtomTypesExcluding(cgtAtomCategoryJSEnum.val));

            cgtNameFilterTypeJSEnum = new JSONStorableEnumStringChooser("_cgtNameFilterType", CGTNameFilterTypes.enumManifestName, CGTNameFilterTypes.none, "Atom Name Filter", SwitchNameFilterType);
            cgtNameFilterJSS = new JSONStorableString("_cgtNameFilter", "");
            cgtPersonGenderFilterJSEnum = new JSONStorableEnumStringChooser("_cgtPersonGenderFilter", GenderTypes.enumManifestName, GenderTypes.any, "Gender Filter");

            RegisterParam(cgtNameJSS);
            RegisterParam(cgtAtomCategoryJSEnum);
            RegisterParam(cgtAtomTypeJSEnum);
            RegisterParam(cgtNameFilterTypeJSEnum);
            RegisterParam(cgtNameFilterJSS);
            RegisterParam(cgtPersonGenderFilterJSEnum);
        }
        public bool IsInScope(Atom atom, bool male, bool female)
        {
            bool gender = true;
            bool name = true;
            bool type = true;
            if (atom.type != cgtAtomTypeJSEnum.displayVal) type = false;
            if (atom.type == "Person" && cgtPersonGenderFilterJSEnum.val != GenderTypes.any)
            {
                if (cgtPersonGenderFilterJSEnum.val == GenderTypes.maleOnly && !male) gender = false;
                if (cgtPersonGenderFilterJSEnum.val == GenderTypes.femaleOnly && !female) gender = false;
            }
            if (cgtNameFilterTypeJSEnum.val != CGTNameFilterTypes.none && cgtNameFilterJSS.val != "")
            {
                if (cgtNameFilterTypeJSEnum.val == CGTNameFilterTypes.startsWith && !atom.name.StartsWith(cgtNameFilterJSS.val)) name = false;
                if (cgtNameFilterTypeJSEnum.val == CGTNameFilterTypes.endsWith && !atom.name.EndsWith(cgtNameFilterJSS.val)) name = false;
                if (cgtNameFilterTypeJSEnum.val == CGTNameFilterTypes.contains && !atom.name.Contains(cgtNameFilterJSS.val)) name = false;
            }
            return (gender && name && type);
        }

        public bool isPersonCGT { get
            {
                if (cgtAtomTypeJSEnum.val == AtomTypes.person) return true;
                return false;
            } }

        public void SwitchAtomCategory(string atomCategory)
        {
             cgtAtomTypeJSEnum.SetEnumChoices(AtomTypes.enumManifestName, AtomTypes.GetAtomTypesExcluding(cgtAtomCategoryJSEnum.val));

        }
        public void SwitchNameFilterType(string nameFilterType)
        {
            try
            {
  //              _mvrScript.RefreshLeftUIDisplay();
            }
            catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
        }
        public void SwitchCGTName(string cgtName)
        {
            try
            {
                if (CustomTargetGroupSettings.CGTListNameDuplicate(cgtName))
                {
                    cgtNameJSS.valNoCallback = cgtNameJSS.defaultVal;
                    SuperController.LogMessage("UIAssist: Custom Target Groups must have a unique name.");
                }
                else if (cgtName == "")
                {
                    cgtNameJSS.valNoCallback = cgtNameJSS.defaultVal;
                    SuperController.LogMessage("UIAssist: Custom Target Groups must not have a blank name.");
                }
                else if (cgtName == "Atom" || cgtName == "Person" || cgtName == "Female" || cgtName == "Male")
                {
                    cgtNameJSS.valNoCallback = cgtNameJSS.defaultVal;
                    SuperController.LogMessage("UIAssist: Custom Target Groups cannot be named '" + cgtName + "' to avoid confliciting with the standard Gaze Target Types.");
                }
                else if (cgtNameJSS.defaultVal!="")
                {
                    UIAStorables.UpdateCTGName(cgtNameJSS.defaultVal, cgtNameJSS.val);
                    if (TargetGroupSettingsUITab.customTargetGroupSelectorJSSC.val == cgtNameJSS.defaultVal) TargetGroupSettingsUITab.customTargetGroupSelectorJSSC.valNoCallback = cgtName;
                    cgtNameJSS.SetDefaultFromCurrent();
                    
                }
            }
            catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
        }

        public JSONStorableString cgtNameJSS;
        public JSONStorableEnumStringChooser cgtAtomCategoryJSEnum;
        public JSONStorableEnumStringChooser cgtAtomTypeJSEnum;
        public JSONStorableEnumStringChooser cgtNameFilterTypeJSEnum;
        public JSONStorableString cgtNameFilterJSS;
        public JSONStorableEnumStringChooser cgtPersonGenderFilterJSEnum;

    }

    public class HeelAdjustTool : JSONStorableObject
    {
        public HeelAdjustTool()
        {
            heelAdjustActiveJSB = new JSONStorableBool("heelAdjustActive", true, SwitchHeelAdjustActive);
            RegisterParam(heelAdjustActiveJSB);
            heelAdjustRaisePeopleJSB = new JSONStorableBool("heelAdjustRaisePerson", false, SwitchHeelAdjustRaisePerson);
            RegisterParam(heelAdjustRaisePeopleJSB);
            heelAdjustMocapJSB = new JSONStorableBool("heelAdjustMocapJSB", true, SwitchHeelAdjustMocap);
            RegisterParam(heelAdjustMocapJSB);

            _heelCollidersVisibleJSON = new JSONStorableBool("Heel Coliders Visible", false, SwitchHeelCollidersVisible);
            _footRotationXJSON = new JSONStorableFloat("Foot Rotation (X)", 0f, SwitchHAVal, -90f, +90f, true);
            _toeRotationXJSON = new JSONStorableFloat("Toe Rotation (X)", 0f, SwitchHAVal, -90f, +90f, true);
            _heelColliderHeightJSON = new JSONStorableFloat("Heel Collider Height", 0f, SwitchHAVal, 0f, +50f, true);
            _toeColliderHeightJSON = new JSONStorableFloat("Toe Collider Height", 0f, SwitchHAVal, 0f, +50f, true);
            _heelColliderBackOffsetJSON = new JSONStorableFloat("Heel Collider Offset", 0f, SwitchHAVal, -0.1f, +0.1f, true);

            _toeColliderLengthJSON = new JSONStorableFloat("Toe Collider Length", 1f, SwitchHAVal, 0.5f, 3f);
            _toeColliderOffsetJSON = new JSONStorableFloat("Toe Collider Offset", 0f, SwitchHAVal, -0.1f, 0.1f);

            _selectedHASettings = new HASetting();
        }
        public static void DisableHeelAdjust()
        {
            if (UIAGlobals.isPrimaryPlugin)
            {
                foreach (Atom atom in SuperController.singleton.GetAtoms())
                {
                    if (atom.type == "Person")
                    {
                        FootPoseReset(atom, true);
                    }
                }
            }
        }
        public static void SwitchSelectedClothingItem(string item)
        {
            foreach (ActiveClothingList acl in TargetControl.atomActiveClothingDictionary.Values.ToList<ActiveClothingList>())
            {
                foreach (KeyValuePair<DAZClothingItem, string> kvp in acl._activeClothingDisplayNames)
                {
                    if (kvp.Value == item)
                    {
                        _selectedHAFileName = acl._activeClothingHAFileNameByDCI[kvp.Key];
                        string haFileName = UIAConsts._PluginDataSubfolderName + "\\" + UIAConsts._HeelAdjustSubfolderName + "\\" + _selectedHAFileName + "." + UIAConsts._HeelAdjustFileExtension;
                        if (FileManagerSecure.FileExists(haFileName)) LoadHASetting(_selectedHASettings, haFileName);
                        else _selectedHASettings.Reset();
                        UpdateDisplayVals(_selectedHASettings);
                        //                       _mvrScript.RefreshLeftUIDisplay();
                        SetAllHACollidersVisible(_heelCollidersVisibleJSON.val);

                        return;
                    }
                }
            }


        }

        private static void SwitchHAVal(float val)
        {
            UpdateHASetting(_selectedHASettings);
            SaveHASetting(_selectedHASettings, UIAConsts._PluginDataSubfolderName + "\\" + UIAConsts._HeelAdjustSubfolderName + "\\" + _selectedHAFileName + "." + UIAConsts._HeelAdjustFileExtension);
            RefreshHA();
        }
        private static void UpdateDisplayVals(HASetting haSetting)
        {
            _footRotationXJSON.valNoCallback = haSetting._footRotation.x;
            _toeRotationXJSON.valNoCallback = haSetting._toeRotation.x;
            _heelColliderHeightJSON.valNoCallback = haSetting._heelColliderHeight;
            _toeColliderHeightJSON.valNoCallback = haSetting._toeColliderHeight;
            _heelColliderBackOffsetJSON.valNoCallback = haSetting._heelColliderBackOffset;
            _toeColliderOffsetJSON.valNoCallback = haSetting._toeColliderOffset;
            _toeColliderLengthJSON.valNoCallback = haSetting._toeColliderLength;
        }

        private static void UpdateHASetting(HASetting haSetting)
        {
            haSetting._footRotation.x = _footRotationXJSON.val; ;
            haSetting._toeRotation.x = _toeRotationXJSON.val;
            haSetting._heelColliderHeight = _heelColliderHeightJSON.val;
            haSetting._toeColliderHeight = _toeColliderHeightJSON.val;
            haSetting._heelColliderBackOffset = _heelColliderBackOffsetJSON.val;
            haSetting._toeColliderOffset = _toeColliderOffsetJSON.val;
            haSetting._toeColliderLength = _toeColliderLengthJSON.val;
        }

        private static void SwitchHeelCollidersVisible(bool visible)
        {
            SetAllHACollidersVisible(visible);
            RefreshHA();
        }
        private static void SwitchHeelAdjustActive(bool haActive)
        {
            try
            {
                if (haActive)
                {
 //                   SwitchHeelAdjustRaisePerson(heelAdjustRaisePeopleJSB.val);

                    if (heelAdjustRaisePeopleJSB.val)
                    {
                        // If Raising people is now being switched to active, then disable the temporary suppression.
                        foreach (KeyValuePair<string, HASetting> kvp in _atomHASettings)
                        {
                            if (kvp.Value != null) kvp.Value._suppressControlNodeHeightReset = false;
                        }
                    }
                    RefreshHA();
                }
                else
                {
//                    SwitchHeelAdjustRaisePerson(false);
                    foreach (Atom atom in SuperController.singleton.GetAtoms())
                    {
                        if (atom.type == "Person") FootPoseReset(atom);
                    }
                }

            }
            catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
        }
        private static void SwitchHeelAdjustRaisePerson(bool haRaisePerson)
        {
            try
            {
                if (haRaisePerson)
                {
                    foreach (KeyValuePair<string, HASetting> kvp in _atomHASettings)
                    {
                        if (kvp.Value != null) kvp.Value._suppressControlNodeHeightReset = false;
                    }
                }
                foreach (Atom atom in SuperController.singleton.GetAtoms())
                {
                    if (atom.type == "Person")
                    {
                        if (haRaisePerson) /*UpdateAtomHeightOffset(atom);*/;
                        else ResetAtomHeightOffset(atom);
                    }
                }
                RefreshHA();

            }
            catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
        }

        private static void SwitchHeelAdjustMocap(bool haMocap)
        {
            foreach (Atom atom in SuperController.singleton.GetAtoms())
            {
                if (atom.type == "Person") HeelAdjustMocap(atom);
            }
        }

        public static void UpdateExistingHAFiles()
        {
            _existingHAFiles = new List<string>();
            if (FileManagerSecure.DirectoryExists(UIAConsts._PluginDataSubfolderName + "\\" + UIAConsts._HeelAdjustSubfolderName))
            {
                foreach (string file in SuperController.singleton.GetFilesAtPath(UIAConsts._PluginDataSubfolderName + "\\" + UIAConsts._HeelAdjustSubfolderName))
                {
                    string fileName = FileManagerSecure.GetFileName(file);
                    _existingHAFiles.Add(fileName.Replace("." + UIAConsts._HeelAdjustFileExtension, ""));
                }
            }
        }
        private static Transform CreateSingleShoeCollider(Atom atom, string tranformHierarchy, string colliderNamePreFix, Vector3 baseRotation, float colliderSize)
        {
            Transform limbTransform = atom.gameObject.transform.Find(tranformHierarchy);
            GameObject rescaleGO = new GameObject();

            if (limbTransform == null) return null;
            else
            {
                rescaleGO.transform.position = limbTransform.position;
                rescaleGO.transform.rotation = limbTransform.rotation;
                rescaleGO.transform.parent = limbTransform;
                rescaleGO.transform.localEulerAngles = baseRotation;
                rescaleGO.name = colliderNamePreFix + "ColliderRescaleUIA";
                rescaleGO.transform.localScale = new Vector3(1f, 0.01f, 1f);

                GameObject colliderGO = GameObject.CreatePrimitive(PrimitiveType.Cube);
                colliderGO.transform.position = rescaleGO.transform.position;
                colliderGO.transform.parent = rescaleGO.transform;
                colliderGO.transform.localScale = new Vector3(colliderSize, UIAConsts._ShoeColliderBaseScale, colliderSize);
                colliderGO.name = "ColliderUIA";

                colliderGO.transform.localPosition = new Vector3(0f, -UIAConsts._ShoeColliderBaseScale / 2, 0f);
                colliderGO.transform.localEulerAngles = new Vector3(0f, 0.0f, 0.0f);
                bool visible = (_heelCollidersVisibleJSON.val && TargetControl.atomActiveClothingDictionary[atom.name]._activeClothingHAFileNames.Contains(_selectedHAFileName));
                colliderGO.GetComponent<Renderer>().enabled = visible;
                colliderGO.GetComponent<Renderer>().material.color = Color.green;

            }

            return (rescaleGO.transform);
        }
        private static void SetColliderVisible(Atom atom, string limbHierarchy, string prefix, bool visible)
        {
            string colliderRescaleName = prefix + "ColliderRescaleUIA";
            string colliderName = "ColliderUIA";

            Transform collider = atom.gameObject.transform.Find(limbHierarchy + "/" + colliderRescaleName + "/" + colliderName);

            if (collider != null) collider.gameObject.GetComponent<Renderer>().enabled = visible;
        }

        private static void SetAllHACollidersVisible(bool val)
        {
            foreach (Atom atom in SuperController.singleton.GetAtoms())
            {
                SetAllCollidersVisibleForAtom(atom, val);
            }

        }
        private static void SetAllCollidersVisibleForAtom(Atom atom, bool val)
        {
            if (atom.type == "Person" && TargetControl.atomActiveClothingDictionary[atom.name]._activeClothingHAFileNames.Contains(_selectedHAFileName) && !_selectedHASettings.IsNullValue())
            {
                SetColliderVisible(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot", "rShoeHeel", val);
                SetColliderVisible(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot", "lShoeHeel", val);
                SetColliderVisible(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot/rToe", "rShoeToe", val);
                SetColliderVisible(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot/lToe", "lShoeToe", val);
            }
            else if (atom.type == "Person")
            {
                SetColliderVisible(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot", "rShoeHeel", false);
                SetColliderVisible(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot", "lShoeHeel", false);
                SetColliderVisible(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot/rToe", "rShoeToe", false);
                SetColliderVisible(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot/lToe", "lShoeToe", false);
            }
        }

        private static void CreateHAColliders(Atom atom)
        {

            // if there are any colliders active from the HeelAdjust plugin - then remove them (needed because HA plugin doesnt tidy up OnDestroy)
            RemoveCollider(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot", "rShoeHeel", false);
            RemoveCollider(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot", "lShoeHeel", false);
            RemoveCollider(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot/rToe", "rShoeToe", false);
            RemoveCollider(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot/lToe", "lShoeToe", false);

            Transform rShoeToeRescaleTran = CreateSingleShoeCollider(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot/rToe", "rShoeToe", _ToeColliderBaseRotationR, 0.05f);
            Transform lShoeToeRescaleTran = CreateSingleShoeCollider(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot/lToe", "lShoeToe", _ToeColliderBaseRotationL, 0.05f);
            Transform rShoeHeelRescaleTran = CreateSingleShoeCollider(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot", "rShoeHeel", _HeelColliderBaseRotationR, 0.02f);
            Transform lShoeHeelRescaleTran = CreateSingleShoeCollider(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot", "lShoeHeel", _HeelColliderBaseRotationL, 0.02f);

        }

        private static void ApplySingleColiderHeightRotation(Atom atom, Transform transform, float colliderHeight, float colliderLength, Vector3 colliderRotation, Vector3 baseColliderRotation, float colliderUpdateDelay)
        {
            UIAGlobals.mvrScript.StartCoroutine(DelayedColliderUpdate(atom, transform, colliderHeight, colliderLength, colliderUpdateDelay));
            transform.transform.localEulerAngles = new Vector3(-colliderRotation.x + baseColliderRotation.x, baseColliderRotation.y, baseColliderRotation.z);
        }
        private static IEnumerator DelayedColliderUpdate(Atom atom, Transform transform, float colliderHeight, float colliderLength, float colliderUpdateDelay)
        {
            if (colliderUpdateDelay == 0.001f) yield return new WaitForEndOfFrame();
            else
            {
                yield return new WaitForSeconds(colliderUpdateDelay);
            }

            if (transform != null)
            {
                Transform colliderT = transform.Find("ColliderUIA");
                if (colliderT != null)
                {
                    colliderT.gameObject.GetComponent<Collider>().enabled = true;
                    transform.transform.localScale = new Vector3(1f, colliderHeight, colliderLength);
                }
            }

            if (_atomTempLift.ContainsKey(atom.name))
            {
                FreeControllerV3 atomControlFCV3 = atom.freeControllers.First(fc => fc.name == "control");
                atomControlFCV3.transform.position -= Vector3.up * _atomTempLift[atom.name];
                _atomTempLift.Remove(atom.name);
            }

        }
        private static void ApplyHAColliderHeightsRotations(Atom atom, HASetting haSetting, bool vamBuiltInHeel, float colliderUpdateDelay)
        {
            float toeColliderHeight = 0.0001f;
            float heelColliderHeight = 0.0001f;
            if (!vamBuiltInHeel)
            {
                toeColliderHeight = haSetting._toeColliderHeight;
                heelColliderHeight = haSetting._heelColliderHeight;
            }

            Transform rToeColliderRescale = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot/rToe/rShoeToeColliderRescaleUIA");
            ApplySingleColiderHeightRotation(atom, rToeColliderRescale, toeColliderHeight, haSetting._toeColliderLength, haSetting._toeRotation, _ToeColliderBaseRotationR, colliderUpdateDelay);

            Transform rHeelColliderRescale = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot/rShoeHeelColliderRescaleUIA");
            ApplySingleColiderHeightRotation(atom, rHeelColliderRescale, heelColliderHeight, 1f, haSetting._footRotation, _HeelColliderBaseRotationR, colliderUpdateDelay);

            Transform lToeColliderRescale = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot/lToe/lShoeToeColliderRescaleUIA");
            ApplySingleColiderHeightRotation(atom, lToeColliderRescale, toeColliderHeight, haSetting._toeColliderLength, haSetting._toeRotation, _ToeColliderBaseRotationL, colliderUpdateDelay);

            Transform lHeelColliderRescale = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot/lShoeHeelColliderRescaleUIA");
            ApplySingleColiderHeightRotation(atom, lHeelColliderRescale, heelColliderHeight, 1f, haSetting._footRotation, _HeelColliderBaseRotationL, colliderUpdateDelay);

            //            Transform rHeelCollider = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot/rShoeHeelColliderRescaleUIA/rShoeHeelColliderUIA");
            //            Transform lHeelCollider = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot/lShoeHeelColliderRescaleUIA/lShoeHeelColliderUIA");
            Transform rHeelCollider = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot/rShoeHeelColliderRescaleUIA/ColliderUIA");
            Transform lHeelCollider = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot/lShoeHeelColliderRescaleUIA/ColliderUIA");
            Transform rToeCollider = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot/rToe/rShoeToeColliderRescaleUIA/ColliderUIA");
            Transform lToeCollider = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot/lToe/lShoeToeColliderRescaleUIA/ColliderUIA");

            rHeelCollider.transform.localPosition = new Vector3(0f, -UIAConsts._ShoeColliderBaseScale / 2, haSetting._heelColliderBackOffset);
            lHeelCollider.transform.localPosition = new Vector3(0f, -UIAConsts._ShoeColliderBaseScale / 2, haSetting._heelColliderBackOffset);
            rToeCollider.transform.localPosition = new Vector3(0f, -UIAConsts._ShoeColliderBaseScale / 2, haSetting._toeColliderOffset);
            lToeCollider.transform.localPosition = new Vector3(0f, -UIAConsts._ShoeColliderBaseScale / 2, haSetting._toeColliderOffset);

            //adjust foot pose to the given angles
            Transform rfoot = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot");
            Transform rtoe = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot/rToe");
            Transform lfoot = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot");
            Transform ltoe = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot/lToe");

            JSONStorableFloat lFootDriveXJSF = atom.GetStorableByID("lFootControl").GetFloatJSONParam("jointDriveXTarget");
            JSONStorableFloat lToeDriveXJSF = atom.GetStorableByID("lToeControl").GetFloatJSONParam("jointDriveXTarget");
            JSONStorableFloat rFootDriveXJSF = atom.GetStorableByID("rFootControl").GetFloatJSONParam("jointDriveXTarget");
            JSONStorableFloat rToeDriveXJSF = atom.GetStorableByID("rToeControl").GetFloatJSONParam("jointDriveXTarget");

            Vector3 rf = new Vector3(haSetting._footRotation.x + rFootDriveXJSF.val, haSetting._footRotation.y, haSetting._footRotation.z);
            Vector3 rt = new Vector3(haSetting._toeRotation.x - haSetting._footRotation.x + rToeDriveXJSF.val, haSetting._toeRotation.y, haSetting._toeRotation.z);
            Vector3 lf = new Vector3(haSetting._footRotation.x + lFootDriveXJSF.val, -haSetting._footRotation.y, -haSetting._footRotation.z);
            Vector3 lt = new Vector3(haSetting._toeRotation.x - haSetting._footRotation.x + lToeDriveXJSF.val, -haSetting._toeRotation.y, -haSetting._toeRotation.z);

            if (rfoot.GetComponent<DAZBone>().baseJointRotation != rf || rtoe.GetComponent<DAZBone>().baseJointRotation != rt || lfoot.GetComponent<DAZBone>().baseJointRotation != lf || ltoe.GetComponent<DAZBone>().baseJointRotation != lt)
            {
                if (_savedRotationStatesDict.ContainsKey(atom.name)) _savedRotationStatesDict[atom.name]._timeSaved = Time.unscaledTime;
                else
                {
                    ToeFootRotationStates savedRotationStates = GetCurrentRotationStates(atom);
                    _savedRotationStatesDict.Add(atom.name, savedRotationStates);
                }
                ResetRotationStates(atom, null);
                rfoot.GetComponent<DAZBone>().baseJointRotation = rf;
                rtoe.GetComponent<DAZBone>().baseJointRotation = rt;
                lfoot.GetComponent<DAZBone>().baseJointRotation = lf;
                ltoe.GetComponent<DAZBone>().baseJointRotation = lt;
            }
        }
        private static ToeFootRotationStates GetCurrentRotationStates(Atom atom)
        {

            ToeFootRotationStates rotationStates = new ToeFootRotationStates();

            FreeControllerV3 rf = atom.freeControllers.First(fc => fc.name == "rFootControl");
            FreeControllerV3 rt = atom.freeControllers.First(fc => fc.name == "rToeControl");
            FreeControllerV3 lf = atom.freeControllers.First(fc => fc.name == "lFootControl");
            FreeControllerV3 lt = atom.freeControllers.First(fc => fc.name == "lToeControl");

            rotationStates._rFootRotationState = rf.currentRotationState;
            rotationStates._rToeRotationState = rt.currentRotationState;
            rotationStates._lFootRotationState = lf.currentRotationState;
            rotationStates._lToeRotationState = lt.currentRotationState;
            rotationStates._timeSaved = Time.unscaledTime;

            return (rotationStates);
        }

        public static void ResetRotationStates(Atom atom, ToeFootRotationStates rotationStates)
        {
            if (atom != null)
            {
                FreeControllerV3 rf = atom.freeControllers.First(fc => fc.name == "rFootControl");
                FreeControllerV3 rt = atom.freeControllers.First(fc => fc.name == "rToeControl");
                FreeControllerV3 lf = atom.freeControllers.First(fc => fc.name == "lFootControl");
                FreeControllerV3 lt = atom.freeControllers.First(fc => fc.name == "lToeControl");

                if (rotationStates == null)
                {
                    rf.currentRotationState = FreeControllerV3.RotationState.Off;
                    rt.currentRotationState = FreeControllerV3.RotationState.Off;
                    lf.currentRotationState = FreeControllerV3.RotationState.Off;
                    lt.currentRotationState = FreeControllerV3.RotationState.Off;
                }
                else
                {
                    rf.currentRotationState = rotationStates._rFootRotationState;
                    rt.currentRotationState = rotationStates._rToeRotationState;
                    lf.currentRotationState = rotationStates._lFootRotationState;
                    lt.currentRotationState = rotationStates._lToeRotationState;
                }
            }
        }
        public static void ResetAllSavedAtomRotationStates(bool setOff = false)
        {
            List<string> atomsToRemoveFromDictionary = new List<string>();
            foreach (KeyValuePair<string, ToeFootRotationStates> kvp in _savedRotationStatesDict)
            {
                if (Time.unscaledTime - kvp.Value._timeSaved > UIAConsts._FCV3ResetDelay)
                {
                    Atom atom = SuperController.singleton.GetAtomByUid(kvp.Key);
                    ResetRotationStates(atom, kvp.Value);
                    atomsToRemoveFromDictionary.Add(kvp.Key);
                }

            }
            foreach (string atomName in atomsToRemoveFromDictionary) _savedRotationStatesDict.Remove(atomName);
        }
        private static void AjustLinkedAPHeights(string personUID, float heightAdjust)
        {
            if (heightAdjust > 0.001f || heightAdjust < -0.001f)
            {
                foreach (Atom atom in SuperController.singleton.GetAtoms())
                {
                    if (atom.type == "AnimationPattern")
                    {
                        JSONStorable storable = atom.GetStorableByID("AnimatedObject");
                        if (storable != null)
                        {
                            JSONClass apAnimatiedObjectJSON = storable.GetJSON();
                            string reciever = apAnimatiedObjectJSON["receiver"];
                            if (reciever != null)
                            {
                                if (reciever.Contains(":") && reciever.Length > reciever.IndexOf(':'))
                                {
                                    string atomName = reciever.Substring(0, reciever.IndexOf(':'));
                                    string nodeName = reciever.Substring(reciever.IndexOf(':') + 1);
                                    if (atomName == personUID && (nodeName == "control" || nodeName == "lToeControl" || nodeName == "rToeControl" || nodeName == "lFootControl" || nodeName == "rFootControl"))
                                    {
                                        FreeControllerV3 fcAP = atom.freeControllers.First(fc => fc.name == "control");
                                        fcAP.transform.position += Vector3.up * heightAdjust;
                                    }
                                }
                            }

                        }
                    }
                }
            }
        }
        private static void ResetAtomHeightOffset(Atom atom)
        {
            
            if (_atomHASettings.ContainsKey(atom.name) && _atomHASettings[atom.name] != null && !_atomHASettings[atom.name]._suppressControlNodeHeightReset)
            {
                FreeControllerV3 atomControlFCV3 = atom.freeControllers.First(fc => fc.name == "control");
                atomControlFCV3.transform.position -= Vector3.up * _atomHASettings[atom.name]._controlNodeHeightOffset * UIAConsts._ShoeColliderBaseScale;

                AjustLinkedAPHeights(atom.name, -(_atomHASettings[atom.name]._controlNodeHeightOffset * UIAConsts._ShoeColliderBaseScale));
                _atomHASettings[atom.name]._controlNodeHeightOffset = 0f;
            }
        }

        private static void UpdateAtomHeightOffset(Atom atom)
        {
            if (_atomHASettings.ContainsKey(atom.name) && _atomHASettings[atom.name] != null)
            {
                HASetting haSetting = _atomHASettings[atom.name];

                float heightAdjustment = 0f;
                heightAdjustment = haSetting._heelColliderHeight - (0.064f / UIAConsts._ShoeColliderBaseScale) - haSetting._controlNodeHeightOffset;
                haSetting._controlNodeHeightOffset = haSetting._heelColliderHeight - (0.064f / UIAConsts._ShoeColliderBaseScale);

                FreeControllerV3 atomControlFCV3 = atom.freeControllers.First(fc => fc.name == "control");

                atomControlFCV3.transform.position += Vector3.up * heightAdjustment * UIAConsts._ShoeColliderBaseScale;

                if (heightAdjustment > 1f && !_atomTempLift.ContainsKey(atom.name))
                {
                    //                  _atomTempLift.Add(atom.name, 0.1f);
                    //                  atomControlFCV3.transform.position += Vector3.up * 0.1f;
                }
                AjustLinkedAPHeights(atom.name, heightAdjustment * UIAConsts._ShoeColliderBaseScale);
            }
        }
        private static void LoadHASetting(HASetting haSetting, string path)
        {
            string loadedSettings = SuperController.singleton.ReadFileIntoString(path);
            string[] loadedSettingsLines = loadedSettings.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None);
            string[] footVals = loadedSettingsLines[0].Split(new string[] { "," }, StringSplitOptions.None);
            string[] toeVals = loadedSettingsLines[1].Split(new string[] { "," }, StringSplitOptions.None);
            string[] heelColliderVals = loadedSettingsLines[2].Split(new string[] { "," }, StringSplitOptions.None);
            string[] toeColliderVals = loadedSettingsLines[3].Split(new string[] { "," }, StringSplitOptions.None);
            haSetting._footRotation.x = float.Parse(footVals[0]);
            haSetting._footRotation.y = float.Parse(footVals[1]);
            haSetting._footRotation.z = float.Parse(footVals[2]);
            haSetting._toeRotation.x = float.Parse(toeVals[0]);
            haSetting._toeRotation.y = float.Parse(toeVals[1]);
            haSetting._toeRotation.z = float.Parse(toeVals[2]);
            haSetting._heelColliderHeight = float.Parse(heelColliderVals[0]);
            haSetting._heelColliderBackOffset = float.Parse(heelColliderVals[1]);
            haSetting._toeColliderHeight = float.Parse(toeColliderVals[0]);
            if (toeColliderVals.Count() > 1) haSetting._toeColliderOffset = float.Parse(toeColliderVals[1]);
            if (toeColliderVals.Count() > 2) haSetting._toeColliderLength = float.Parse(toeColliderVals[2]);
        }
        private static void SaveHASetting(HASetting has, string path)
        {
            if (has.IsNullValue())
            {
                if (FileManagerSecure.FileExists(path)) FileManagerSecure.DeleteFile(path);
            }
            else
            {
                string outputString = has._footRotation.x.ToString() + "," + has._footRotation.y.ToString() + "," + has._footRotation.z.ToString() + "\n";
                outputString = outputString + has._toeRotation.x.ToString() + "," + has._toeRotation.y.ToString().ToString() + "," + has._toeRotation.z.ToString().ToString() + "\n";
                outputString = outputString + has._heelColliderHeight.ToString().ToString() + "," + has._heelColliderBackOffset.ToString().ToString() + "\n";
                outputString = outputString + has._toeColliderHeight.ToString().ToString() + "," + has._toeColliderOffset.ToString().ToString() + "," + has._toeColliderLength.ToString().ToString();
                SuperController.singleton.SaveStringIntoFile(path, outputString);
            }


        }
        public static HASetting GetHASetting(Atom atom)
        {
            if (_atomHASettings.ContainsKey(atom.name) && _atomHASettings[atom.name] != null) return (_atomHASettings[atom.name]);

            return (null);
        }

        private static void RefreshHA()
        {
            foreach (var atom in SuperController.singleton.GetAtoms().Where(x=>x.type == "Person"))
            {
                ActiveClothingList.ApplyHA(atom);
            }
        }
        private static void ApplyHA(Atom atom, string path)
        {
            HASetting haSetting = null;
            bool newAtom = !_atomHASettings.ContainsKey(atom.name);
            if (newAtom || _atomHASettings[atom.name] == null) haSetting = new HASetting();
            else haSetting = _atomHASettings[atom.name];

            if (!newAtom && _atomHASettings[atom.name] == null) _atomHASettings[atom.name] = haSetting;

            if (FileManagerSecure.FileExists(path))
            {
                LoadHASetting(haSetting, path);

                if (newAtom)
                {
                    // This is a new Atom (either through scene load or adding a person)
                    // Therefore we assume the person is at the correct height
                    haSetting._controlNodeHeightOffset = haSetting._heelColliderHeight - (0.064f / UIAConsts._ShoeColliderBaseScale);
                    // Heel Adjust raising people is not active, then we dont want to lower people - so we temporarily suppress this until HeelAdjust raising people is activated
                    if (!heelAdjustActiveJSB.val || !heelAdjustRaisePeopleJSB.val) haSetting._suppressControlNodeHeightReset = true;
                    _atomHASettings.Add(atom.name, haSetting);
                }

                float colliderUpdateDelay = 0.001f;
                if (heelAdjustRaisePeopleJSB.val)
                {
                    UpdateAtomHeightOffset(atom);
                    colliderUpdateDelay = 0.3f;
                }


                Transform rt = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot/rShoeHeelColliderRescaleUIA");
                if (rt == null)
                {
                    CreateHAColliders(atom);
                    rt = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot/rShoeHeelColliderRescaleUIA");
                }
                else SetAllCollidersVisibleForAtom(atom, _heelCollidersVisibleJSON.val);

                if (rt.transform.localScale.y > haSetting._heelColliderHeight) colliderUpdateDelay = 0.001f;

                if (path.EndsWith("MeshedVR-HarliHeels.heeladjust") || path.EndsWith("MeshedVR-CasualDenimShoes.heeladjust")) ApplyHAColliderHeightsRotations(atom, haSetting, true, colliderUpdateDelay);
                else ApplyHAColliderHeightsRotations(atom, haSetting, false, colliderUpdateDelay);
            }

        }
        private static void RemoveCollider(Atom atom, string limbHierarchy, string prefix, bool UIA = true)
        {
            string colliderRescaleName = prefix + "ColliderRescale";
            string colliderName = "Collider";

            if (UIA)
            {
                colliderRescaleName = colliderRescaleName + "UIA";
                colliderName = colliderName + "UIA";
            }

            Transform collider = atom.gameObject.transform.Find(limbHierarchy + "/" + colliderRescaleName + "/" + colliderName);
            if (collider != null)
            {
                collider.parent = null;
                GameObject.Destroy(collider.gameObject);
            }

            Transform colliderRescale = atom.gameObject.transform.Find(limbHierarchy + "/" + colliderRescaleName);
            if (colliderRescale != null)
            {
                colliderRescale.parent = null;
                GameObject.Destroy(collider.gameObject);
            }
        }

        public static void RemoveColliders(Atom atom)
        {
            RemoveCollider(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot", "rShoeHeel");
            RemoveCollider(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot", "lShoeHeel");
            RemoveCollider(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot/rToe", "rShoeToe");
            RemoveCollider(atom, "rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot/lToe", "lShoeToe");
        }

        public static void FootPoseReset(Atom atom, bool suppressHeightReset = false)
        {
            if (!suppressHeightReset) ResetAtomHeightOffset(atom);
            Transform rt = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot/rToe/rShoeToeColliderRescaleUIA");
            if (rt != null)
            {
                if (_savedRotationStatesDict.ContainsKey(atom.name)) _savedRotationStatesDict[atom.name]._timeSaved = Time.unscaledTime;
                else
                {
                    ToeFootRotationStates savedRotationStates = GetCurrentRotationStates(atom);
                    _savedRotationStatesDict.Add(atom.name, savedRotationStates);
                }
                ResetRotationStates(atom, null);
                //reset foot and toe rotations to their defaults
                Vector3 rfootBaseRotation = new Vector3(-15f, 0f, 0f);
                Vector3 rtoeBaseRotation = new Vector3(0f, 0f, 0f);
                Vector3 lfootBaseRotation = new Vector3(-15f, 0f, 0f);
                Vector3 ltoeBaseRotation = new Vector3(0f, 0f, 0f);

                var rfoot = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot");
                var rtoe = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot/rToe");
                var lfoot = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot");
                var ltoe = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/lThigh/lShin/lFoot/lToe");

                rfoot.GetComponent<DAZBone>().baseJointRotation = rfootBaseRotation;
                rtoe.GetComponent<DAZBone>().baseJointRotation = rtoeBaseRotation;
                lfoot.GetComponent<DAZBone>().baseJointRotation = lfootBaseRotation;
                ltoe.GetComponent<DAZBone>().baseJointRotation = ltoeBaseRotation;

                RemoveColliders(atom);
            }
            if (_atomHASettings.ContainsKey(atom.name) && _atomHASettings[atom.name]!=null) _atomHASettings[atom.name].Reset();
        }
        public static bool isPrestigitisHALoaded(Atom atom)
        {
            bool foundHeelAdjustPlugin = false;
            MVRPluginManager manager = atom.GetStorableByID("PluginManager") as MVRPluginManager;
            if (manager != null)
            {
                foreach (Transform transform in manager.gameObject.transform.Find("Plugins"))
                {
                    MVRScript pluginStorable = transform.gameObject.GetComponent<MVRScript>();
                    if (pluginStorable != null)
                    {
                        string storableName = pluginStorable.name;
                        string pluginType = storableName.Substring(storableName.IndexOf('_') + 1);
                        if (pluginType.StartsWith("prestigitis.HeelAdjust"))
                        {
                            foundHeelAdjustPlugin = true;
                            break;
                        }
                    }
                }
            }

            return (foundHeelAdjustPlugin);
        }

        public static bool isShoeTagActive()
        {
            foreach (ActiveClothingList acl in TargetControl.atomActiveClothingDictionary.Values.ToList<ActiveClothingList>())
            {
                foreach (List<string> tagList in acl._activeClothingTags.Values.ToList<List<string>>())
                {
                    if (tagList.Contains("shoes")) return (true);
                }
            }

            return (false);
        }
        public static List<string> GetActiveShoeNameList()
        {
            List<string> clothingItemNames = new List<string>();
            foreach (ActiveClothingList acl in TargetControl.atomActiveClothingDictionary.Values.ToList<ActiveClothingList>())
            {
                foreach (KeyValuePair<DAZClothingItem, List<string>> kvp in acl._activeClothingTags)
                {
                    string clothingItemName = acl._activeClothingDisplayNames[kvp.Key];
                    if (kvp.Value.Contains("shoes") && !clothingItemNames.Contains(clothingItemName)) clothingItemNames.Add(clothingItemName);
                }
            }

            return (clothingItemNames);
        }

        public static void LoadFootPosesForClothing(Atom atom, List<string> activePersonClothing)
        {
            Transform rt = atom.gameObject.transform.Find("rescale2/PhysicsModel/Genesis2Female/hip/pelvis/rThigh/rShin/rFoot/rToe/rShoeToeColliderRescale");
            if (rt != null && isPrestigitisHALoaded(atom)) return;

            if (activePersonClothing == null || _existingHAFiles == null)
            {
                FootPoseReset(atom);
                if (_atomHASettings.ContainsKey(atom.name)) _atomHASettings[atom.name] = null;
                else _atomHASettings.Add(atom.name, null);
            }
            else
            {

                //get intersection of active clothing and files
                List<string> loadableProfiles = activePersonClothing.Intersect(_existingHAFiles).ToList();
                if (loadableProfiles.Count < 1) //intersection list is empty
                {
                    FootPoseReset(atom);
                    if (_atomHASettings.ContainsKey(atom.name)) _atomHASettings[atom.name] = null;
                    else _atomHASettings.Add(atom.name, null);
                }
                else
                {
                    //a heeladjust file was found for one of the active clothing items, load the first one
                    ApplyHA(atom, UIAConsts._PluginDataSubfolderName + "\\" + UIAConsts._HeelAdjustSubfolderName + "\\" + loadableProfiles.First().ToString() + "." + UIAConsts._HeelAdjustFileExtension);
                }
            }
        }
        public static void AtomNameUpdate(string oldName, string newName)
        {
            if (_savedRotationStatesDict.ContainsKey(oldName))
            {
                _savedRotationStatesDict.Add(newName, _savedRotationStatesDict[oldName]);
                _savedRotationStatesDict.Remove(oldName);
            }
            if (_atomHASettings.ContainsKey(oldName))
            {
                _atomHASettings.Add(newName, _atomHASettings[oldName]);
                _atomHASettings.Remove(oldName);
            }
        }

        public static void AtomRemovedUpdate(Atom atom)
        {
            string oldName = atom.name;
            if (_savedRotationStatesDict.ContainsKey(oldName)) _savedRotationStatesDict.Remove(oldName);
            if (_atomHASettings.ContainsKey(oldName)) _atomHASettings.Remove(oldName);
            RemoveHeelAdjustMocapOffsets(atom, true);
        }

        public static void RemoveHeelAdjustMocapOffsets(Atom atom, bool forceRemove)
        {
            if (atom == null || atom.type != "Person") return;

            var mocapFootControls = atom.motionAnimationControls.Where(x => x.controller.name == "rFootControl" || x.controller.name == "lFootControl" || x.controller.name == "rToeControl" || x.controller.name == "lToeControl");

            foreach (var mac in mocapFootControls)
            {
                
                if (mac.clip.steps.Count == 0 || forceRemove)
                {
                    RemoveOffsetParam(mac, "modified"+mac.controller.name + "XOffset");
                    RemoveOffsetParam(mac, "modified" + mac.controller.name + "YOffset");
                    RemoveOffsetParam(mac, "modified" + mac.controller.name + "ZOffset");
                }
            }

        }

        private static void RemoveOffsetParam(MotionAnimationControl mac, string paramName)
        {
            if (mac.GetFloatParamNames().Contains(paramName)) mac.DeregisterFloat(mac.GetFloatJSONParam(paramName));

        }

        public static void HeelAdjustMocap(Atom atom,  bool applyRotations = true)
        {
            HASetting targetHASetting = null;
            float xFootAdjustment = 0f;

            if (!_atomHASettings.ContainsKey(atom.name) || _atomHASettings[atom.name] == null || !heelAdjustMocapJSB.val) targetHASetting = new HASetting();
            else targetHASetting = _atomHASettings[atom.name];

            if (targetHASetting._footRotation.x != 0f) xFootAdjustment = 15f;

            var mocapFootControls = atom.motionAnimationControls.Where(x => x.controller.name == "rFootControl" || x.controller.name == "lFootControl");

            foreach (var mfc in mocapFootControls)
            {
                Vector3 targetFootRotation = new Vector3(targetHASetting._footRotation.x+ xFootAdjustment, targetHASetting._footRotation.y, targetHASetting._footRotation.z);
                if (mfc.clip.steps.Count > 0) SetMocapAnimControlOffset(mfc,mfc.controller.name, targetFootRotation, applyRotations);
            }
            var mocapToeControls = atom.motionAnimationControls.Where(x => x.controller.name == "rToeControl" || x.controller.name == "lToeControl");
            foreach (var mfc in mocapToeControls)
            {
                if (mfc.clip.steps.Count > 0) SetMocapAnimControlOffset(mfc, mfc.controller.name, targetHASetting._toeRotation, applyRotations);
            }
        }

        private static void SetMocapAnimControlOffset(MotionAnimationControl mac, string footToeLabel, Vector3 targetOffset, bool applyRotations)
        {
            Vector3 offset = new Vector3();
            string paramNamePrefix = "modified" + footToeLabel;
            offset.x = GetMocapAnimControlOffset(mac, paramNamePrefix + "XOffset", targetOffset.x);
            offset.y = GetMocapAnimControlOffset(mac, paramNamePrefix + "YOffset", targetOffset.y);
            offset.z = GetMocapAnimControlOffset(mac, paramNamePrefix + "ZOffset", targetOffset.z);

            if (applyRotations && (offset.x!=0f || offset.y != 0f || offset.z != 0f ))
            {
                foreach (var macStep in mac.clip.steps)  macStep.rotation *= Quaternion.Euler(offset);
                mac.PlaybackStep(SuperController.singleton.motionAnimationMaster.playbackCounter);
            }
        }

        private static float GetMocapAnimControlOffset(MotionAnimationControl mac, string paramName, float targetOffsetVertex)
        {
            float currentOffset = 0f;
            if (mac.GetFloatParamNames().Contains(paramName))
            {
                currentOffset = mac.GetFloatParamValue(paramName);
                mac.SetFloatParamValue(paramName, targetOffsetVertex);
            }
            else
            {
                var offsetJSF = new JSONStorableFloat(paramName, targetOffsetVertex, -180f, 180f, false);
                offsetJSF.isStorable = false;
                offsetJSF.isRestorable = false;
                mac.RegisterFloat(offsetJSF);

            }


            return targetOffsetVertex - currentOffset;
        }


        public static bool IsMocapHeelAdjustAvailable()
        {
            foreach (Atom atom in AtomUtils.GetPersonAtoms(GenderTypes.any, true))
            {
                if (IsMocapHeelAdjustAvailable(atom)) return true;
            }

            return false;
        }

        public static bool IsMocapHeelAdjustAvailable(Atom atom)
        {
            if (atom == null || atom.type != "Person") return false;
            var mocapFootControls = atom.motionAnimationControls.Where(x => x.controller.name == "rFootControl" || x.controller.name == "lFootControl");
            foreach (var mfc in mocapFootControls)
            {
                if (mfc.clip.steps.Count > 0) return true;
            }
            return false;
        }

        public static JSONStorableBool heelAdjustActiveJSB;
        public static JSONStorableBool heelAdjustRaisePeopleJSB;
        public static JSONStorableBool heelAdjustMocapJSB;

        public static JSONStorableBool _heelCollidersVisibleJSON;
        public static JSONStorableFloat _footRotationXJSON;
        public static JSONStorableFloat _toeRotationXJSON;
        public static JSONStorableFloat _heelColliderHeightJSON;
        public static JSONStorableFloat _toeColliderHeightJSON;
        public static JSONStorableFloat _heelColliderBackOffsetJSON;
        public static JSONStorableFloat _toeColliderLengthJSON;
        public static JSONStorableFloat _toeColliderOffsetJSON;

        public static HASetting _selectedHASettings;
        public static string _selectedHAFileName;

        private static List<string> _existingHAFiles;
        private static Dictionary<string, ToeFootRotationStates> _savedRotationStatesDict = new Dictionary<string, ToeFootRotationStates>();
        private static Dictionary<string, HASetting> _atomHASettings = new Dictionary<string, HASetting>();
        private static Dictionary<string, float> _atomTempLift = new Dictionary<string, float>();
        //       private Vector3 _ToeColliderBaseRotationR = new Vector3(-16.7f, 0f, -4.3f);
        //       private Vector3 _ToeColliderBaseRotationL = new Vector3(-16.7f, 0f, 4.3f);
        private static Vector3 _ToeColliderBaseRotationR = new Vector3(-23f, 0f, -4.3f);
        private static Vector3 _ToeColliderBaseRotationL = new Vector3(-23f, 0f, 4.3f);
        private static Vector3 _HeelColliderBaseRotationR = new Vector3(-27f, 0f, 0f); //rFoot world rotation: Vector3(13.1f, 9.2f, 1.3f);
        private static Vector3 _HeelColliderBaseRotationL = new Vector3(-27f, 0f, 0f); //lFoot world rotation: Vector3(13.1f, 351.4f, 359.4f);
    }
    public class HASetting
    {
        public Vector3 _footRotation;
        public Vector3 _toeRotation;
        public float _heelColliderHeight = 0f;
        public float _heelColliderBackOffset = -0.03f;
        public float _toeColliderHeight = 0f;
        public float _toeColliderOffset = 0f;
        public float _toeColliderLength = 1f;
        public float _controlNodeHeightOffset = 0f;
        public bool _suppressControlNodeHeightReset = false;

        public JSONClass GetJSON(string atomName)
        {
            JSONClass jc = new JSONClass();

            jc["atomName"] = atomName;
            jc["footRotationX"].AsFloat = _footRotation.x;
            jc["footRotationY"].AsFloat = _footRotation.y;
            jc["footRotationZ"].AsFloat = _footRotation.z;

            jc["toeRotationX"].AsFloat = _toeRotation.x;
            jc["toeRotationY"].AsFloat = _toeRotation.y;
            jc["toeRotationZ"].AsFloat = _toeRotation.z;

            jc["heelColliderHeight"].AsFloat = _heelColliderHeight;
            jc["toeColliderHeight"].AsFloat = _toeColliderHeight;
            jc["toeColliderOffset"].AsFloat = _toeColliderOffset;
            jc["toeColliderLength"].AsFloat = _toeColliderLength;
            jc["heelColliderBackOffset"].AsFloat = _heelColliderBackOffset;
            jc["controlNodeHeightOffset"].AsFloat = _controlNodeHeightOffset;
            jc["suppressControlNodeHeightReset"].AsBool = _suppressControlNodeHeightReset;

            return (jc);
        }
        public void RestoreJSON(JSONClass jc)
        {
            _footRotation.x = jc["footRotationX"].AsFloat;
            _footRotation.y = jc["footRotationY"].AsFloat;
            _footRotation.z = jc["footRotationZ"].AsFloat;

            _toeRotation.x = jc["toeRotationX"].AsFloat;
            _toeRotation.y = jc["toeRotationY"].AsFloat;
            _toeRotation.z = jc["toeRotationZ"].AsFloat;

            _heelColliderHeight = jc["heelColliderHeight"].AsFloat;
            _toeColliderHeight = jc["toeColliderHeight"].AsFloat;
            _toeColliderLength = jc["toeColliderLength"].AsFloat;
            _toeColliderOffset = jc["toeColliderOffset"].AsFloat;
            _heelColliderBackOffset = jc["heelColliderBackOffset"].AsFloat;
            _controlNodeHeightOffset = jc["controlNodeHeightOffset"].AsFloat;
            _suppressControlNodeHeightReset = jc["suppressControlNodeHeightReset"].AsBool;
        }

        public void Reset()
        {
            _footRotation.x = 0f;
            _footRotation.y = 0f;
            _footRotation.z = 0f;

            _toeRotation.x = 0f;
            _toeRotation.y = 0f;
            _toeRotation.z = 0f;

            _heelColliderHeight = 0f;
            _toeColliderHeight = 0f;
            _toeColliderLength = 1f;
            _toeColliderOffset = 0f;
            _heelColliderBackOffset = 0f;
            _controlNodeHeightOffset = 0f;
            _suppressControlNodeHeightReset = false;
        }

        public bool IsNullValue()
        {
            if (_footRotation.x == 0f && _toeRotation.x == 0f && _heelColliderHeight == 0f && _toeColliderHeight == 0f) return true;
            return false;
        }
    }

    public class ToeFootRotationStates
    {
        public FreeControllerV3.RotationState _lToeRotationState;
        public FreeControllerV3.RotationState _lFootRotationState;

        public FreeControllerV3.RotationState _rToeRotationState;
        public FreeControllerV3.RotationState _rFootRotationState;
        public float _timeSaved;
    }
}

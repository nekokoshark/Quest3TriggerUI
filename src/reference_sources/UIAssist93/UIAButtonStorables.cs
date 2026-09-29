using Leap.Unity;
using Leap.Unity.Query;
using MeshVR;
using MVR.FileManagementSecure;
using SimpleJSON;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Windows.Speech;

namespace JayJayWon
{
    public class UIAButtonGrid : JSONStorableObject
    {
        private JSONStorableInt gridRowsJSInt;
        private JSONStorableInt gridColumnsJSInt;
        public JSONStorableEnumStringChooser buttonSizeJSEnum;
        public JSONStorableString gridLabelJSS;
        public JSONStorableBool quickLaunchBarJSBool;
        public JSONStorableBool globalFunctionsJSB;
        public JSONStorableBool triggerFunctionsJSB;

        private JSONStorableInt topSliderCountJSInt;
        private JSONStorableInt bottomSliderCountJSInt;

        public int topsliderCount
        {
            get { return topSliderCountJSInt.val; }
            set {
                topSliderCountJSInt.val = value;
                UpdateGridSliders();
            }
        }

        public int bottomSliderCount
        {
            get { return bottomSliderCountJSInt.val; }
            set {
                bottomSliderCountJSInt.val = value;
                UpdateGridSliders();
            }
        }

        public void OnTriggerEvent(int triggerEventType, Atom atom = null, string phrase="")
        {
            if (isTriggerFunctionsGrid)
            {
                foreach (var button in buttonList) button.OnTriggerEvent(triggerEventType, atom, phrase);
            }
        }

        public void StartPhraseRecognizer()
        {
            if (isTriggerFunctionsGrid)
            {
                foreach (var button in buttonList) button.StartPhraseRecognizer();
            }

        }

        public void StopPhraseRecognizer()
        {
            if (isTriggerFunctionsGrid)
            {
                foreach (var button in buttonList) button.StopPhraseRecognizer();
            }
        }

        public bool isOriginalGrid { get
            {
                return !(quickLaunchBarJSBool.val || globalFunctionsJSB.val || triggerFunctionsJSB.val);
            } }

        public bool isTriggerFunctionsGrid
        {
            get
            {
                return triggerFunctionsJSB.val;
            }
        }

        public bool isVisibleGrid
        {
            get
            {
                return !(globalFunctionsJSB.val || triggerFunctionsJSB.val);
            }
        }
        public string gridRef
        {
            get {
                if (UIAStorables.quickLaunchButtonGrid == this) return "QL";
                if (UIAStorables.globalFunctionsGrid == this) return "GF";
                if (UIAStorables.triggerFunctionsGrid == this) return "TF";
                return (UIAStorables.buttonGridsList.IndexOf(this) + 1).ToString();
            }
        }
        public int gridIndex
        {
            get { return (UIAStorables.buttonGridsList.IndexOf(this)); }
        }

        public int GetButtonIndexInGrid (UIAButton button)
        {
            return buttonList.IndexOf(button);
        }

        public int GetButtonRowRefInGrid (UIAButton button)
        {
            if (isVisibleGrid)
            {
                int buttonRef = GetButtonIndexInGrid(button);
                return (buttonRef / gridColumns) + 1;
            }
            else return 1;
            
        }

        public int GetButtonColRefInGrid(UIAButton button)
        {
            if (isVisibleGrid)
            {
                int buttonRef = GetButtonIndexInGrid(button);
                return (buttonRef % gridColumns) + 1;
            }
            else return GetButtonIndexInGrid(button)+1;
        }

        public string GetButtonRCRef(UIAButton button)
        {
            if (isVisibleGrid) return "R" + GetButtonRowRefInGrid(button) + "C" + GetButtonColRefInGrid(button);
            else
            {
                return gridRef + GetButtonColRefInGrid(button);
            }
        }

        public string GetButtonRCRef(int buttonIndex)
        {
            return GetButtonRCRef(buttonList[buttonIndex]);
        }

        public int GetButtonIndexFromRCRef(string buttonRCRef)
        {
            if (isVisibleGrid)
            {
                int indexOfC = buttonRCRef.IndexOf('C');
                string rowRef = buttonRCRef.Substring(1, indexOfC - 1);
                string colRef = buttonRCRef.Substring(indexOfC + 1);
                return ((int.Parse(rowRef) - 1) * gridColumns) + int.Parse(colRef) - 1;
            }
            else
            {
                return int.Parse(buttonRCRef.Substring(2))-1;
            }
            
        }

        public UIAButton GetButtonFromRCRef(string buttonRCRef)
        {
            return buttonList[GetButtonIndexFromRCRef(buttonRCRef)];
        }

        public int gridRows {
            get { return gridRowsJSInt.val; }
            set {
                gridRowsJSInt.val = value;
                if (isVisibleGrid)
                {
                    UpdateGridButtons();
                    RecalcGazeSelections();
                }

            }
        }
        public int gridColumns
        {
            get { return gridColumnsJSInt.val; }
            set {
                gridColumnsJSInt.val = value;
                if (isVisibleGrid)
                {
                    UpdateGridButtons();
                    RecalcGazeSelections();
                }
                
            }
        }

        public string GetGridLayout()
        {
            return gridColumns.ToString() + "x" + gridRows.ToString();
        }

        public string _buttonSize
        {
            get { return buttonSizeJSEnum.displayVal; }
            set { buttonSizeJSEnum.val = ButtonSize.GetEnumVal(value); }
        }
        public string gridLabel
        {
            get
            {
                if (gridLabelJSS.val == "" || !GameControlSettings.displayGridLabelsJSB.val) return gridRef;
                return gridLabelJSS.val;
            }
            set { gridLabelJSS.val = value; }
        }


        public bool GetTreeBrowserRowCollapsedRow(int row)
        {
            if (!treeBrowserRowCollapsedDict.ContainsKey(row)) return true;
            return treeBrowserRowCollapsedDict[row];
        }

        public void SetTreeBrowserRowCollapsedRow(int row, bool collapsed)
        {
            if (!treeBrowserRowCollapsedDict.ContainsKey(row)) treeBrowserRowCollapsedDict.Add(row, collapsed);
            else treeBrowserRowCollapsedDict[row] = collapsed;
        }

        public bool treeBrowserCollapsed = true;
        private Dictionary<int, bool> treeBrowserRowCollapsedDict;

        public int _activeButtonCount {
            get
            {
                int activeButtonCount = 0;

                int _buttonCount = 1;
                foreach (UIAButton button in buttonList)
                {
                    if (!button.ContainsAllBlankOperations() && _buttonCount <= (buttonCount))
                    {
                        activeButtonCount++;

                    }
                    _buttonCount++;
                    }
                return activeButtonCount;
            }
        }
        public int _maxButtonRow
        {
            get
            {
                int maxButtonRow = 0;

                int _buttonCount = 1;
                foreach (UIAButton button in buttonList)
                {
                    if (!button.ContainsAllBlankOperations() && _buttonCount <= (buttonCount))
                    {
                        maxButtonRow = (buttonCount / gridColumns);
                        if (buttonCount % gridColumns > 0) maxButtonRow++;
                    }
                    _buttonCount++;
                }
                return maxButtonRow;
            }
        }

        public bool _gazeSelectionAtoms = false;
        public bool _gazeSelectionFemales = false;
        public bool _gazeSelectionMales = false;
        public bool _gazeSelectionPerson = false;
        public bool _gazeSelectionNonPerson = false;
        public Dictionary<string, bool> _gazeSelectionCTGDic;
        public bool _vamSelectionAtoms = false;
        public bool _vamSelectionFemales = false;
        public bool _vamSelectionMales = false;
        public bool _vamSelectionPerson = false;
        public bool _vamSelectionNonPerson = false;

        public int buttonCount
        {
            get {
                if (isVisibleGrid) return (gridRows * gridColumns);
                else return buttonList.Count();
            }
        }

        public List<UIAButton> buttonList = new List<UIAButton>();

        public List<UIASlider> topSliderList = new List<UIASlider>();
        public List<UIASlider> bottomSliderList = new List<UIASlider>();

        public UIAButtonGrid(int columns = 0, int rows = 1, bool quickLaunchBar = false, bool globalFunctions=false, bool triggerFunctions = false)
        {
            try
            {
                gridRowsJSInt = new JSONStorableInt("gridRows", rows, 1, 9,false);
                gridColumnsJSInt = new JSONStorableInt("gridColumns", columns, 1, 9,false);
                buttonSizeJSEnum = new JSONStorableEnumStringChooser("buttonSize", ButtonSize.enumManifestName, ButtonSize.medium, "", SwitchButtonSize);
                gridLabelJSS = new JSONStorableString("gridLabel", "");
                quickLaunchBarJSBool = new JSONStorableBool("quickLaunchBar", quickLaunchBar);
                globalFunctionsJSB = new JSONStorableBool("globalFunctions", globalFunctions);
                triggerFunctionsJSB = new JSONStorableBool("triggerFunctionsJSB", triggerFunctions);
                topSliderCountJSInt = new JSONStorableInt("topSliderCount", 0, 0, 5);
                bottomSliderCountJSInt = new JSONStorableInt("bottomSliderCount", 0, 0, 5);

                treeBrowserRowCollapsedDict = new Dictionary<int, bool>();
                RegisterParam(gridRowsJSInt);
                RegisterParam(gridColumnsJSInt);
                RegisterParam(buttonSizeJSEnum);
                RegisterParam(gridLabelJSS);
                RegisterParam(quickLaunchBarJSBool);
                RegisterParam(globalFunctionsJSB);
                RegisterParam(triggerFunctionsJSB);

                RegisterParam(topSliderCountJSInt);
                RegisterParam(bottomSliderCountJSInt);

                _gazeSelectionCTGDic = new Dictionary<string, bool>();
                UpdateGridButtons();
                UpdateGridSliders();
            }
            catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
        }

        private void SwitchButtonSize (string val)
        {
            GameControlUI.RefreshWristUIButtonGrid();
        }

        public void AtomNameUpdate(string oldName, string newName)
        {
            foreach (UIAButton button in buttonList)
            {
                button.AtomNameUpdate(oldName, newName);
            }
        }
        public void AtomRemovedUpdate(string oldName)
        {
            foreach (UIAButton button in buttonList)
            {
                button.AtomRemovedUpdate(oldName);
            }
        }

        public bool ContainsBlankButtons()
        {
            foreach (UIAButton button in buttonList.GetRange(0, buttonCount))
            {
                if (button.ContainsAllBlankOperations()) return (true);
            }
            return (false);
        }
        public int GetFirstBlankButtonIndex()
        {
            for (int i = 0; i < buttonList.GetRange(0, buttonCount).Count; i++)
            {
                if (buttonList[i].ContainsAllBlankOperations()) return (i);
            }
            return (-1);
        }
        public Vector2 GetButtonDimensions()
        {
            Vector2 dimensions = new Vector2();
            switch (buttonSizeJSEnum.val)
            {
                case ButtonSize.micro:
                    dimensions = ButtonSize.Micro.dimensions;
                    break;
                case ButtonSize.mini:
                    dimensions = ButtonSize.Mini.dimensions;
                    break;
                case ButtonSize.small:
                    dimensions = ButtonSize.Small.dimensions;
                    break;
                case ButtonSize.medium:
                    dimensions = ButtonSize.Medium.dimensions;
                    break;
                case ButtonSize.large:
                    dimensions = ButtonSize.Large.dimensions;
                    break;
            }

            return (dimensions);
        }
        public void UpdateGridButtons()
        {
            if (buttonList.Count < (buttonCount))
            {
                for (int i = buttonList.Count; i < (buttonCount); i++)
                {
                    UIAButton newButton = new UIAButton(this);
                    buttonList.Add(newButton);
                }
            }
        }

        public void UpdateGridSliders()
        {
            if (topSliderCountJSInt.val > topSliderList.Count)
            {
                for (int i = topSliderList.Count; i <= topSliderCountJSInt.val-1; i++)
                {
                    UIASlider newSlider = new UIASlider(this);
                    topSliderList.Add(newSlider);
                }
            }

            if (bottomSliderCountJSInt.val > bottomSliderList.Count)
            {
                for (int i = bottomSliderList.Count; i <= bottomSliderCountJSInt.val-1; i++)
                {
                    UIASlider newSlider = new UIASlider(this);
                    bottomSliderList.Add(newSlider);
                }
            }
        }


        public bool IsSelectionTargetButton()
        {
            bool cgtGazeSelection = false;
            foreach (KeyValuePair<string, bool> kvp in _gazeSelectionCTGDic)
            {
                if (kvp.Value)
                {
                    cgtGazeSelection = true;
                    break;
                }
            }
            return (_gazeSelectionAtoms || _gazeSelectionMales || _gazeSelectionFemales || _gazeSelectionPerson || _vamSelectionAtoms || _vamSelectionMales || _vamSelectionFemales || _vamSelectionPerson || cgtGazeSelection);
        }
        public void RecalcGazeSelections()
        {
            _gazeSelectionAtoms = false;
            _gazeSelectionMales = false;
            _gazeSelectionFemales = false;
            _gazeSelectionPerson = false;
            _gazeSelectionNonPerson = false;
            _vamSelectionAtoms = false;
            _vamSelectionMales = false;
            _vamSelectionFemales = false;
            _vamSelectionPerson = false;
            _vamSelectionNonPerson = false;
            _gazeSelectionCTGDic.Clear();
            foreach (CustomTargetGroupType cgt in CustomTargetGroupSettings.customTargetGroupTypeList)
            {
                _gazeSelectionCTGDic.Add(cgt.cgtNameJSS.val, false);
            }
            int gridCount = Math.Min(gridColumns * gridRows, buttonList.Count);

            // TBD need to get targetComponents for buttons and Sliders
            foreach (UIAButton button in buttonList.GetRange(0, gridCount))
            {
                foreach (UIAButtonOperation buttonOp in button.buttonOperations)
                {
                    if (buttonOp.targetComponent.targetCategoryJSEnum.val == TargetCategory.gazeSelectedAtom && UIAButtonOpType.IsAtomTargetable(buttonOp.buttonOpTypeJSEnum.val))
                    {
                        if (buttonOp.targetComponent.targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.topEnumValFlag)
                        {
                            int lastViewedTarget = buttonOp.targetComponent.targetNameJSMultiEnum.valTopEnum;
                            if (lastViewedTarget == LastViewedTargetType.lastViewedAtom) _gazeSelectionAtoms = true;
                            if (lastViewedTarget == LastViewedTargetType.lastViewedPerson) _gazeSelectionPerson = true;
                            if (lastViewedTarget == LastViewedTargetType.lastViewedFemale) _gazeSelectionFemales = true;
                            if (lastViewedTarget == LastViewedTargetType.lastViewedMale) _gazeSelectionMales = true;
                            if (lastViewedTarget == LastViewedTargetType.lastViewedNonPerson) _gazeSelectionNonPerson = true;
                        }
                        else if (buttonOp.targetComponent.targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.mainValFlag)
                        {
                            string cgtName = buttonOp.targetComponent.targetNameJSMultiEnum.mainVal;
                            if (_gazeSelectionCTGDic.ContainsKey(cgtName)) _gazeSelectionCTGDic[cgtName] = true;
                        }
                    }
                    else if (buttonOp.targetComponent.targetCategoryJSEnum.val == TargetCategory.vamSelectedAtom && UIAButtonOpType.IsAtomTargetable(buttonOp.buttonOpTypeJSEnum.val))
                    {
                        if (buttonOp.targetComponent.targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.topEnumValFlag)
                        {
                            int lastSelectedAtom = buttonOp.targetComponent.targetNameJSMultiEnum.valTopEnum;
                            if (lastSelectedAtom == LastSelectedTargetType.lastSelectedAtom) _vamSelectionAtoms = true;
                            if (lastSelectedAtom == LastSelectedTargetType.lastSelectedPerson) _vamSelectionPerson = true;
                            if (lastSelectedAtom == LastSelectedTargetType.lastSelectedFemale) _vamSelectionFemales = true;
                            if (lastSelectedAtom == LastSelectedTargetType.lastSelectedMale) _vamSelectionMales = true;
                            if (lastSelectedAtom == LastSelectedTargetType.lastSelectedNonPerson) _vamSelectionNonPerson = true;
                        }
                    }
                }

            }

        }

        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            JSONClass jc = base.GetJSON(jspLoadExclusions);

            JSONArray buttonArray = new JSONArray();
            foreach (UIAButton button in buttonList.GetRange(0, buttonCount))
            {
                buttonArray.Add(button.GetJSON());
            }
            jc["Buttons"] = buttonArray;

            if (isOriginalGrid)
            {
                JSONArray topSliderArray = new JSONArray();
                foreach (UIASlider slider in topSliderList.GetRange(0, topsliderCount))
                {
                    topSliderArray.Add(slider.GetJSON());
                }
                if (topSliderArray.Count>0) jc["topSliders"] = topSliderArray;

                JSONArray bottomSliderArray = new JSONArray();
                foreach (UIASlider slider in bottomSliderList.GetRange(0, bottomSliderCount))
                {
                    bottomSliderArray.Add(slider.GetJSON());
                }
                if (bottomSliderArray.Count > 0) jc["bottomSliders"] = bottomSliderArray;
            }

            return jc;
        }
        public void LoadJSON(JSONClass gridJSON, string uiapPackageName, int uiapFormatVersion)
        {
            base.RestoreFromJSON(gridJSON);

            buttonList.Clear();
            if (gridJSON["Buttons"] != null)
            {
                JSONArray buttonArrayJSON = gridJSON["Buttons"].AsArray;
                for (int i = 0; i < buttonArrayJSON.Count; i++)
                {
                    JSONClass buttonJSON = buttonArrayJSON[i].AsObject;
                    UIAButton button = new UIAButton(this);
                    buttonList.Add(button);

                    button.LoadJSON(buttonJSON, uiapPackageName, uiapFormatVersion);
                }
            }

            if (isOriginalGrid)
            {
                topSliderList.Clear();
                bottomSliderList.Clear();
                if (gridJSON["topSliders"]!= null)
                {
                    JSONArray topSliderArrayJSON = gridJSON["topSliders"].AsArray;
                    for (int i = 0; i < topSliderArrayJSON.Count; i++)
                    {
                        JSONClass sliderJSON = topSliderArrayJSON[i].AsObject;
                        UIASlider slider = new UIASlider(this);
                        topSliderList.Add(slider);
                        slider.RestoreFromJSON(sliderJSON);
                    }
                }
                else topsliderCount = 0;

                if (gridJSON["bottomSliders"] != null)
                {
                    JSONArray bottomSliderArrayJSON = gridJSON["bottomSliders"].AsArray;
                    for (int i = 0; i < bottomSliderArrayJSON.Count; i++)
                    {
                        JSONClass sliderJSON = bottomSliderArrayJSON[i].AsObject;
                        UIASlider slider = new UIASlider(this);
                        bottomSliderList.Add(slider);
                        slider.RestoreFromJSON(sliderJSON);
                    }
                }
                else bottomSliderCount = 0;
            }
            if (isVisibleGrid) RecalcGazeSelections();
            else
            {
                if (buttonList.Count > 0)
                {
                    gridRows = 1;
                    gridColumns = buttonList.Count;
                }
                else
                {
                    gridRows= 1; gridColumns= 0;
                }
            }
        }

        public void PostLoadJSON()
        {
            foreach (UIAButton button in buttonList) button.PostLoadJSON();
        }
    }

    public abstract class ButtonComponentBase : JSONStorableObject
    {
        public UIAButton parentButton=null;

        public UIASlider parentSlider=null;

        public ButtonComponentBase(UIAButton _parent)
        {
            parentButton = _parent;
        }

        public ButtonComponentBase(UIASlider _parent)
        {
            parentSlider = _parent;
        }
    }
    public abstract class ButtonOperationComponentBase : ButtonComponentBase
    {
        public UIAButtonOperation parentButtonOperation=null;

        public ButtonOperationComponentBase(UIAButtonOperation parent):base(parent?.parentButton)
        {
            parentButtonOperation = parent;
        }

        public ButtonOperationComponentBase(UIASlider parentSlider) : base(parentSlider)
        {           
        }
    }

    public class FileReference : ButtonOperationComponentBase
    {
        public JSONStorableEnumStringChooser fileSelectionModeJSEnum;
        public JSONStorableString filePathJSString;
        public JSONStorableBool useLatestVARJSBool { get; set; }
        public JSONStorableBool mergeLoadPresetJSBool;
        public JSONStorableBool forceUniqueSaveNameJSB;
        public JSONStorableBool includeSubFoldersJSB;
        public JSONStorableBool excludeHiddenJSB;
        public JSONStorableBool onlyFavoritesJSB;

        public JSONStorableBool morphPresetIncludePhysicalJSB;
        public JSONStorableBool morphPresetIncludeAppJSB;

        public JSONStorableBool generalPresetIncludeAppJSB;
        public JSONStorableBool generalPresetIncludePhysicalJSB;
        public JSONStorableBool generalPresetIncludePoseJSB;

        public JSONStorableBool onlyReplaceRealClothingJSB;
        public JSONStorableBool suppressHairColorJSB;
        public JSONStorableBool suppressHairStyleJSB;

        public JSONStorableBool posePresetSnapBoneToPoseJSB;

        public JSONStorableBool closeGridOnLoadUIAPJSB;

        public BAOrderedResourceFilter baFilter;

        public JSONStorableBool useDefaultBAFilterJSB;

        private Dictionary<string, string> lastSelectedFilePerAtom;

        public string lastSelectedFolderGameUI { get; private set; } = "";

        public Texture2D buttonTexture { get; set; }

        public string currentActionSelectedFile { get; protected set; } = "";
        public bool currentActionFileSelectComplete { get; protected set; } = false;

        public int fileReferenceType { get; protected set; }
        public int fileReferenceCategory { get
            {
                return FileReferenceTypes.GetFileRefCategory(fileReferenceType);
            }
        }

        public bool IsThumbnailFileRefType()
        {
            if (fileReferenceCategory != FileReferenceCategory.uiapCat && fileReferenceType != FileReferenceTypes.addonPackagesFolder && fileReferenceType != FileReferenceTypes.plugin) return true;
            return false;
        }

        public FileReference(int _fileRefType, UIAButtonOperation parent) : base(parent)
        {
            fileReferenceType = _fileRefType;

            baFilter = new BAOrderedResourceFilter(_fileRefType, "");

            fileSelectionModeJSEnum = new JSONStorableEnumStringChooser("fileSelectionMode", FileSelectionMode.enumManifestName, FileSelectionMode.singleFile, "", FileSelectModeChanged, fileSelectionModeExclusions);
            RegisterParam(fileSelectionModeJSEnum);

            filePathJSString = new JSONStorableString("filePath", "", FilePathUpdated);
            RegisterParam(filePathJSString);

            useLatestVARJSBool = new JSONStorableBool("useLatestVAR", true);
            RegisterParam(useLatestVARJSBool);

            mergeLoadPresetJSBool = new JSONStorableBool("mergeLoadPreset", false);
            RegisterParam(mergeLoadPresetJSBool);

            closeGridOnLoadUIAPJSB = new JSONStorableBool("closeGridOnLoadUIAP", true);
            RegisterParam(closeGridOnLoadUIAPJSB);

            useDefaultBAFilterJSB = new JSONStorableBool("useDefaultBAFilter", true);
            RegisterParam(useDefaultBAFilterJSB);

            forceUniqueSaveNameJSB = new JSONStorableBool("forceUniqueSaveName", false);
            RegisterParam(forceUniqueSaveNameJSB);

            includeSubFoldersJSB = new JSONStorableBool("includeSubFolders", false);
            RegisterParam(includeSubFoldersJSB);

            excludeHiddenJSB = new JSONStorableBool("excludeHidden", true);
            RegisterParam(excludeHiddenJSB);

            onlyFavoritesJSB = new JSONStorableBool("onlyFavorites", false);
            RegisterParam(onlyFavoritesJSB);

            morphPresetIncludePhysicalJSB = new JSONStorableBool("morphPresetIncludePhysical", false);
            morphPresetIncludeAppJSB = new JSONStorableBool("morphPresetIncludeApp", true);
            if (fileReferenceType == FileReferenceTypes.morphPreset)
            {
                RegisterParam(morphPresetIncludePhysicalJSB);
                RegisterParam(morphPresetIncludeAppJSB);
            }

            generalPresetIncludeAppJSB = new JSONStorableBool("generalPresetIncludeApp", true);
            generalPresetIncludePhysicalJSB = new JSONStorableBool("generalPresetIncludePhysical", true);
            generalPresetIncludePoseJSB = new JSONStorableBool("generalPresetIncludePose", true);
            if (fileReferenceType == FileReferenceTypes.generalPreset)
            {
                RegisterParam(generalPresetIncludeAppJSB);
                RegisterParam(generalPresetIncludePhysicalJSB);
                RegisterParam(generalPresetIncludePoseJSB);
            }

            posePresetSnapBoneToPoseJSB = new JSONStorableBool("posePresetSnapBoneToPose", true);
            if (fileReferenceType == FileReferenceTypes.posePreset) RegisterParam(posePresetSnapBoneToPoseJSB);

            onlyReplaceRealClothingJSB = new JSONStorableBool("onlyRemoveRealClothing", true);
            if (fileReferenceType == FileReferenceTypes.clothingPreset) RegisterParam(onlyReplaceRealClothingJSB);

            suppressHairColorJSB = new JSONStorableBool("suppressHairColor", false, SuppressHairColorCallback);
            suppressHairStyleJSB = new JSONStorableBool("suppressHairStyle", false, SuppressHairStyleCallback);
            if (fileReferenceType == FileReferenceTypes.hairPreset)
            {
                RegisterParam(suppressHairColorJSB);
                RegisterParam(suppressHairStyleJSB);
            }

            lastSelectedFilePerAtom = new Dictionary<string, string>();
        }

        private void SuppressHairColorCallback(bool val)
        {
            if (val) suppressHairStyleJSB.val = false;
        }

        private void SuppressHairStyleCallback(bool val)
        {
            if (val) suppressHairColorJSB.val = false;
        }

        private List<int> fileSelectionModeExclusions { get
            {
                List<int> fileSelectionModeExclusions = new List<int>();

                if (parentButtonOperation.buttonOpCategoryJSEnum.val == UIAButtonCategory.presets && parentButtonOperation.savePresetJSB.val)
                {
                    fileSelectionModeExclusions.Add(FileSelectionMode.alphabeticalFromFolder);
                    fileSelectionModeExclusions.Add(FileSelectionMode.randomFromFolder);
                    fileSelectionModeExclusions.Add(FileSelectionMode.randomFromBA);
                    fileSelectionModeExclusions.Add(FileSelectionMode.sequentialFromBA);
                    fileSelectionModeExclusions.Add(FileSelectionMode.chooseFromBA);
                }
                if ((fileReferenceCategory != FileReferenceCategory.presetCat && fileReferenceCategory != FileReferenceCategory.sceneCat && fileReferenceCategory != FileReferenceCategory.cuaCat && fileReferenceCategory != FileReferenceCategory.clothingCat && fileReferenceCategory != FileReferenceCategory.audio) || parentButtonOperation.buttonOpTypeJSEnum.val == UIAButtonOpType.loadSubScene)
                {
                    fileSelectionModeExclusions.Add(FileSelectionMode.randomFromBA);
                    fileSelectionModeExclusions.Add(FileSelectionMode.sequentialFromBA);
                    fileSelectionModeExclusions.Add(FileSelectionMode.chooseFromBA);
                }
                return fileSelectionModeExclusions;
            } }
        public void UpdateFileSelectionModeExclusions()
        {

            fileSelectionModeJSEnum.SetEnumChoices(FileSelectionMode.enumManifestName, fileSelectionModeExclusions);
        }

        public void CopyFrom(FileReference fileReference)
        {
            if (fileReference != null)
            {
                base.CopyFrom(fileReference);
                buttonTexture = fileReference.buttonTexture;
                baFilter.RestoreJSON(fileReference.baFilter.GetJSON());
            }
        }

        public void QueueLoadTexture(string url)
        {

            if (string.IsNullOrEmpty(url))
                return;
            var normalizedPath = SuperController.singleton.NormalizeLoadPath(url);

            var fileExists = false;
            if (!string.IsNullOrEmpty(url))
            {
                
                try
                {
                    // This will cause an exception if the path is in unsecure locations
                    fileExists = FileManagerSecure.FileExists(normalizedPath);
                }
                catch
                {
                    fileExists = false;
                }
            }

            if (!fileExists)
            {
                buttonTexture = null;
                return;
            }

            ImageUtils.QueueLoadTexture(normalizedPath, QueuedImageLoadCallback);
        }

        private void QueuedImageLoadCallback(ImageLoaderThreaded.QueuedImage qi)
        {
            buttonTexture = qi.tex;
            parentButton.UpdateThumbnailImage(buttonTexture);
        }

        private void FileSelectModeChanged(string mode)
        {
            if (fileSelectionModeJSEnum.val != FileSelectionMode.singleFile && fileSelectionModeJSEnum.val != FileSelectionMode.none && filePathJSString.val != "" && !FileManagerSecure.DirectoryExists(filePathJSString.val))
            {
                filePathJSString.val = FileManagerSecure.GetDirectoryName(filePathJSString.val);
            }

            if (fileSelectionModeJSEnum.val == FileSelectionMode.singleFile && IsThumbnailFileRefType() && filePathJSString.val.Contains('.')) QueueLoadTexture(filePathJSString.val.Substring(0, filePathJSString.val.LastIndexOf('.')) + ".jpg");
            else buttonTexture = null;
        }

        public string GetFileRefTypeDescription()
        {
            return FileReferenceTypes.GetFileRefDescription(fileReferenceType);
        }

        public string GetFileTypeFilter()
        {
            return FileReferenceCategory.GetFileTypeFilter(fileReferenceCategory);
        }

        public string GetDefaultFolder()
        {
            int targetValTopEnum = parentButtonOperation.targetComponent.targetNameJSMultiEnum.valTopEnum;
            if (parentButtonOperation.buttonOpTypeJSEnum.val == UIAButtonOpType.spawnAtom && fileReferenceType == FileReferenceTypes.generalPreset) return "Custom\\Atom\\" + parentButtonOperation.spawnAtomComponent.atomTypeJSEnum.displayVal;
            else if (fileReferenceType == FileReferenceTypes.generalPreset)
            {
                if (parentButtonOperation.targetComponent.targetCategoryJSEnum.val == TargetCategory.specificAtom)
                {
                    if (parentButtonOperation.targetComponent.specificAtomTypeJSS.val != "") return ("Custom\\Atom\\" + parentButtonOperation.targetComponent.specificAtomTypeJSS.val);
                }
                else if (parentButtonOperation.targetComponent.targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.mainValFlag)
                {
                    string cgtName = parentButtonOperation.targetComponent.targetNameJSMultiEnum.mainVal;
                    CustomTargetGroupType cgt = CustomTargetGroupSettings.GetCGTFromName(cgtName);
                    return "Custom\\Atom\\" + cgt.cgtAtomTypeJSEnum.displayVal;

                }
                else if (parentButtonOperation.targetComponent.targetCategoryJSEnum.val == TargetCategory.userChosenAtom)
                {
                    if (targetValTopEnum != UserChosenTargetType.anyAtoms && targetValTopEnum != UserChosenTargetType.nonPersonAtoms) return "Custom\\Atom\\Person\\General";
                }
                else if (parentButtonOperation.targetComponent.targetCategoryJSEnum.val == TargetCategory.gazeSelectedAtom)
                {
                    if (targetValTopEnum != LastViewedTargetType.lastViewedAtom && targetValTopEnum != LastViewedTargetType.lastViewedNonPerson) return "Custom\\Atom\\Person\\General";
                }
                else if (parentButtonOperation.targetComponent.targetCategoryJSEnum.val == TargetCategory.vamSelectedAtom)
                {
                    if (targetValTopEnum != LastSelectedTargetType.lastSelectedAtom && targetValTopEnum != LastSelectedTargetType.lastSelectedNonPerson) return "Custom\\Atom\\Person\\General";
                }
                else if (parentButtonOperation.targetComponent.targetCategoryJSEnum.val == TargetCategory.atomGroup)
                {
                    if (targetValTopEnum != AllAtomsTargetType.allAtoms && targetValTopEnum != AllAtomsTargetType.allNonPersonAtoms) return "Custom\\Atom\\Person\\General";
                }
                return "Custom\\Atom";
            }
            return FileReferenceTypes.GetDefaultFolder(fileReferenceType);
        }

        public void GetFileFolderDialog()
        {
            string folderName;
            if (filePathJSString.val != "" && filePathJSString.val.Contains(GetDefaultFolder()))
            {
                if (FileManagerSecure.DirectoryExists(filePathJSString.val)) folderName = filePathJSString.val;
                else folderName = FileManagerSecure.GetDirectoryName(filePathJSString.val);
            }
            else folderName = GetDefaultFolder();

            List<ShortCut> shortcuts;
            if (GetDefaultFolder() == "Custom\\Atom")
            {
                shortcuts = new List<ShortCut>();
                foreach (ShortCut sc in FileManagerSecure.GetShortCutsForDirectory(GetDefaultFolder(), false, false, true))
                {
                    if (FileManagerSecure.GetDirectories(sc.path).Count() == 1)
                    {
                        string fname = FileManagerSecure.GetDirectories(sc.path).First();
                        string dName = fname.Substring(fname.LastIndexOf('\\') + 1);
                        if (dName == "Person")
                        {
                            bool containsGeneral = false;
                            foreach (string personPresetFolder in FileManagerSecure.GetDirectories(fname))
                            {
                                string personPresetFolderName = personPresetFolder.Substring(personPresetFolder.LastIndexOf('\\') + 1);
                                if (personPresetFolderName == "General") containsGeneral = true;
                            }
                            if (!containsGeneral) continue;
                        }
                    }
                    shortcuts.Add(sc);
                }
            }
            else shortcuts = FileManagerSecure.GetShortCutsForDirectory(GetDefaultFolder(), false, false, true);

            string prefixRemoval = FileReferenceTypes.GetPrefixRemoval(fileReferenceType);

            if ((fileSelectionModeJSEnum.val == FileSelectionMode.singleFile || parentButtonOperation.buttonOpTypeJSEnum.val == UIAButtonOpType.loadMotionCapture) && fileReferenceType != FileReferenceTypes.addonPackagesFolder)
            {
                SuperController.singleton.GetMediaPathDialog(FileRefSelected, GetFileTypeFilter(), folderName, false, true, false, prefixRemoval, false, shortcuts);
                if (parentButtonOperation.buttonOpCategoryJSEnum.val == UIAButtonCategory.presets && parentButtonOperation.savePresetJSB.val)
                {
                    uFileBrowser.FileBrowser browser = SuperController.singleton.mediaFileBrowserUI;
                    browser.SetTextEntry(true);
                    browser.fileEntryField.text = String.Format("{0}.{1}", ((int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString(), "vap");
                    browser.ActivateFileNameField();

                }
            }
            else if (fileSelectionModeJSEnum.val != FileSelectionMode.none || fileReferenceType == FileReferenceTypes.addonPackagesFolder) SuperController.singleton.GetDirectoryPathDialog(FolderPathSelected, folderName, shortcuts, false);
        }

        private void FolderPathSelected(string folderPath)
        {
            FileRefSelected(folderPath, false);
        }

        private void FileRefSelected(string newFileRef, bool cancel)
        {
            if (newFileRef != "")
            {
                newFileRef = FileUtils.NormalizeAndRemoveVARPath(newFileRef);
                if (parentButtonOperation.buttonOpCategoryJSEnum.val == UIAButtonCategory.presets && parentButtonOperation.savePresetJSB.val && fileSelectionModeJSEnum.val == FileSelectionMode.singleFile)
                {
                    newFileRef = FileManagerSecure.GetDirectoryName(newFileRef) + "\\Preset_" + FileManagerSecure.GetFileName(newFileRef);
                }
                filePathJSString.val = newFileRef;
                if (parentButtonOperation.buttonOpTypeJSEnum.val == UIAButtonOpType.loadMotionCapture) parentButtonOperation.motionCaptureComponent.ReloadMotionCaptureSceneData(newFileRef);
            }
        }

        private void FilePathUpdated(string fileName)
        {
            if (fileReferenceType == FileReferenceTypes.clothingPreset) UIAButton.presetMergeClothingGeometryIDs.Remove(fileName);

            if (fileSelectionModeJSEnum.val == FileSelectionMode.singleFile && IsThumbnailFileRefType())
            {
                int periodIndex = filePathJSString.val.LastIndexOf('.');
                if (periodIndex > -1) QueueLoadTexture(filePathJSString.val.Substring(0, filePathJSString.val.LastIndexOf('.')) + ".jpg");
            }
            else buttonTexture = null;
        }
        public override void RestoreFromJSON(JSONClass jc, string uiapPackageName)
        {
            base.RestoreFromJSON(jc, null);
            if (jc["baFilter"] != null) baFilter.RestoreJSON(jc["baFilter"] as JSONClass);
            filePathJSString.val = FileUtils.NormalizeAndRemoveVARPath(filePathJSString.val);
            if (uiapPackageName != "" && !filePathJSString.val.Contains(":") && FileManagerSecure.FileExists(uiapPackageName + ":/" + filePathJSString.val)) filePathJSString.val = uiapPackageName + ":/" + filePathJSString.val;

            if (fileSelectionModeJSEnum.val == FileSelectionMode.singleFile && IsThumbnailFileRefType() && filePathJSString.val != "")
            {
                int periodIndex = filePathJSString.val.LastIndexOf('.');
                if (periodIndex > -1) QueueLoadTexture(filePathJSString.val.Substring(0, periodIndex) + ".jpg");
            }
            else buttonTexture = null;
        }
        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            var jc = base.GetJSON(jspLoadExclusions);
            jc["baFilter"] = baFilter.GetJSON();
            return jc;
        }
        public string GetCurrentFile(string atomName)
        {
            if (fileSelectionModeJSEnum.val == FileSelectionMode.none) return "";
            if (fileSelectionModeJSEnum.val == FileSelectionMode.singleFile) return filePathJSString.val;
            if (lastSelectedFilePerAtom.ContainsKey(atomName)) return lastSelectedFilePerAtom[atomName];
            return "";
        }
        public void ClearCurrentFile(string atomName)
        {
            lastSelectedFilePerAtom.Remove(atomName);
        }
        private void SetLastSelectedFile(string atomName, string lastSelectedFile)
        {
            if (!lastSelectedFilePerAtom.ContainsKey(atomName)) lastSelectedFilePerAtom.Add(atomName, lastSelectedFile);
            else lastSelectedFilePerAtom[atomName] = lastSelectedFile;
        }
        public void AtomNameUpdate(string oldName, string newName)
        {
            foreach (string key in lastSelectedFilePerAtom.Keys.ToList())
            {
                if (key == oldName)
                {
                    lastSelectedFilePerAtom[newName] = lastSelectedFilePerAtom[key];
                    lastSelectedFilePerAtom.Remove(key);
                }
            }
        }

        public void AtomRemovedUpdate(string oldName)
        {
            foreach (string key in lastSelectedFilePerAtom.Keys.ToList())
            {
                if (key == oldName) lastSelectedFilePerAtom.Remove(key);
            }
        }
        public void Reset()
        {
            currentActionSelectedFile = "";
            currentActionFileSelectComplete = false;
        }

        public bool GetNextFileSelection()
        {
            string filePath;
            string firstTargetAtomName = "";
            string lastSelectedFile = "";
            bool fileSelectionComplete = true;

            List<string> targetAtomNamesForCurrentAction = new List<string>();

            if (parentButtonOperation.IsComponentInButtonOpType(ButtonComponentTypes.targetComponent)) targetAtomNamesForCurrentAction = parentButtonOperation.targetComponent.currentActionTargetAtomNames;

            if (targetAtomNamesForCurrentAction.Count > 0) firstTargetAtomName = targetAtomNamesForCurrentAction.First();

            if (firstTargetAtomName != "" && lastSelectedFilePerAtom.ContainsKey(firstTargetAtomName)) lastSelectedFile = lastSelectedFilePerAtom[firstTargetAtomName];

            if (fileReferenceType == FileReferenceTypes.addonPackagesFolder) filePath = filePathJSString.val;
            else
            {
                fileSelectionComplete = PatreonFeatures.GetNextFileSelection(this, out filePath, lastSelectedFile, UserSelectedFileReferenceCallback);

                if (fileSelectionComplete)
                {
                    if (filePath != "" && filePath.Contains(":") && filePath.IndexOf(':') > 1 && useLatestVARJSBool.val) filePath = FileUtils.GetLatestVARPath(filePath);
                    SetLastSelectedFileReferenceToTargetAtoms(filePath);
                }
            }

            return fileSelectionComplete;
        }

        private void SetLastSelectedFileReferenceToTargetAtoms(string filePath)
        {
            currentActionSelectedFile = filePath;
            currentActionFileSelectComplete = true;
            UIAButton.presetMergeClothingGeometryIDs.Remove(filePath);

            if (parentButtonOperation.IsComponentInButtonOpType(ButtonComponentTypes.targetComponent))
            {
                foreach (string targetAtomName in parentButtonOperation.targetComponent.currentActionTargetAtomNames)
                {
                    SetLastSelectedFile(targetAtomName, filePath);
                }
            }
        }

        public void UpdateCurrentActionSelectedFileWithLatest()
        {
            if (currentActionSelectedFile.Contains(":"))
            {
                if (currentActionSelectedFile.Substring(0, currentActionSelectedFile.IndexOf(":")).EndsWith(".latest"))
                {
                    currentActionSelectedFile = FileUtils.GetLatestVARPath(currentActionSelectedFile);
                    SetLastSelectedFileReferenceToTargetAtoms(currentActionSelectedFile);
                }
            }
        }

        private void UserSelectedFileReferenceCallback(string filePath, bool cancel)
        {
            if (filePath != "") lastSelectedFolderGameUI = FileManagerSecure.GetDirectoryName(filePath);            
            
            if (filePath != "" && parentButtonOperation.buttonOpCategoryJSEnum.val == UIAButtonCategory.presets && parentButtonOperation.savePresetJSB.val)
            {
                filePath = FileManagerSecure.GetDirectoryName(filePath) + "\\Preset_" + FileManagerSecure.GetFileName(filePath);
            }
            if (filePath != "" && filePath.Contains(":") && filePath.IndexOf(':') > 1 && useLatestVARJSBool.val) filePath = FileUtils.GetLatestVARPath(filePath);
            SetLastSelectedFileReferenceToTargetAtoms(filePath);
            parentButton.CheckActionFileReferences();
        }
    }

    public class PresetLockStore
    {
        public bool _generalPresetLock;
        public bool _appPresetLock;
        public bool _posePresetLock;
        public bool _animationPresetLock;
        public bool _glutePhysPresetLock;
        public bool _breastPhysPresetLock;
        public bool _pluginPresetLock;
        public bool _skinPresetLock;
        public bool _morphPresetLock;
        public bool _hairPresetLock;
        public bool _clothingPresetLock;

        public void StorePresetLocks(Atom atom, bool clearAllLocks = false, bool lockClothingPreset = false)
        {
            List<PresetManagerControl> pmControlList = atom.presetManagerControls;
            foreach (PresetManagerControl pmc in pmControlList)
            {
                if (pmc.name == "geometry") _generalPresetLock = pmc.lockParams;
                if (pmc.name == "AppearancePresets") _appPresetLock = pmc.lockParams;
                if (pmc.name == "PosePresets") _posePresetLock = pmc.lockParams;
                if (pmc.name == "AnimationPresets") _animationPresetLock = pmc.lockParams;
                if (pmc.name == "FemaleGlutePhysicsPresets") _glutePhysPresetLock = pmc.lockParams;
                if (pmc.name == "FemaleBreastPhysicsPresets") _breastPhysPresetLock = pmc.lockParams;
                if (pmc.name == "PluginPresets") _pluginPresetLock = pmc.lockParams;
                if (pmc.name == "SkinPresets") _skinPresetLock = pmc.lockParams;
                if (pmc.name == "MorphPresets") _morphPresetLock = pmc.lockParams;
                if (pmc.name == "HairPresets") _hairPresetLock = pmc.lockParams;
                if (pmc.name == "ClothingPresets") _clothingPresetLock = pmc.lockParams;

                if (pmc.name == "ClothingPresets" && lockClothingPreset) pmc.lockParams = true;
                else if (clearAllLocks)
                {
                    pmc.lockParams = false;
                }
            }

        }
        public void RestorePresetLocks(Atom atom)
        {
            List<PresetManagerControl> pmControlList = atom.presetManagerControls;
            foreach (PresetManagerControl pmc in pmControlList)
            {
                if (pmc.name == "geometry") pmc.lockParams = _generalPresetLock;
                if (pmc.name == "AppearancePresets") pmc.lockParams = _appPresetLock;
                if (pmc.name == "PosePresets") pmc.lockParams = _posePresetLock;
                if (pmc.name == "AnimationPresets") pmc.lockParams = _animationPresetLock;
                if (pmc.name == "FemaleGlutePhysicsPresets") pmc.lockParams = _glutePhysPresetLock;
                if (pmc.name == "FemaleBreastPhysicsPresets") pmc.lockParams = _breastPhysPresetLock;
                if (pmc.name == "PluginPresets") pmc.lockParams = _pluginPresetLock;
                if (pmc.name == "SkinPresets") pmc.lockParams = _skinPresetLock;
                if (pmc.name == "MorphPresets") pmc.lockParams = _morphPresetLock;
                if (pmc.name == "HairPresets") pmc.lockParams = _hairPresetLock;
                if (pmc.name == "ClothingPresets") pmc.lockParams = _clothingPresetLock;
            }
        }
    }

    public class NodePhysicsXYZAngleElement : JSONStorableObject
    {
        public JSONStorableEnumStringChooser modifyXJSEnum;
        public JSONStorableEnumStringChooser modifyYJSEnum;
        public JSONStorableEnumStringChooser modifyZJSEnum;

        public JSONStorableFloat xAngleJSF;
        public JSONStorableFloat yAngleJSF;
        public JSONStorableFloat zAngleJSF;

        public NodePhysicsXYZAngleElement(string contextLabel)
        {
            modifyXJSEnum = new JSONStorableEnumStringChooser("ModifyXAngle", "NodePhysicsModificationMode", NodePhysicsModificationMode.noChange, "Modify " + contextLabel + "XTarget");
            modifyYJSEnum = new JSONStorableEnumStringChooser("ModifyYAngle", "NodePhysicsModificationMode", NodePhysicsModificationMode.noChange, "Modify " + contextLabel + "YTarget");
            modifyZJSEnum = new JSONStorableEnumStringChooser("ModifyZAngle", "NodePhysicsModificationMode", NodePhysicsModificationMode.noChange, "Modify " + contextLabel + "ZTarget");

            xAngleJSF = new JSONStorableFloat(contextLabel + "XTarget", 0f, -180f, +180f);          
            yAngleJSF = new JSONStorableFloat(contextLabel + "YTarget", 0f, -180f, +180f);
            zAngleJSF = new JSONStorableFloat(contextLabel + "ZTarget", 0f, -180f, +180f);
            RegisterParam(modifyXJSEnum);
            RegisterParam(modifyYJSEnum);
            RegisterParam(modifyZJSEnum);
            RegisterParam(xAngleJSF);
            RegisterParam(yAngleJSF);
            RegisterParam(zAngleJSF);
        }

        public bool IsMatchingState(JSONStorable nodeStorable)
        {
            if (modifyXJSEnum.val != NodePhysicsModificationMode.noChange)
            {
                JSONStorableFloat nodeXAngle = nodeStorable.GetFloatJSONParam(xAngleJSF.name);
                if (modifyXJSEnum.val == NodePhysicsModificationMode.defaultValue && nodeXAngle.val != nodeXAngle.defaultVal) return false;
                if (modifyXJSEnum.val == NodePhysicsModificationMode.customValue && nodeXAngle.val != xAngleJSF.val) return false;
            }
            if (modifyYJSEnum.val != NodePhysicsModificationMode.noChange)
            {
                JSONStorableFloat nodeYAngle = nodeStorable.GetFloatJSONParam(yAngleJSF.name);
                if (modifyYJSEnum.val == NodePhysicsModificationMode.defaultValue && nodeYAngle.val != nodeYAngle.defaultVal) return false;
                if (modifyYJSEnum.val == NodePhysicsModificationMode.customValue && nodeYAngle.val != yAngleJSF.val) return false;
            }
            if (modifyZJSEnum.val != NodePhysicsModificationMode.noChange)
            {
                JSONStorableFloat nodeZAngle = nodeStorable.GetFloatJSONParam(zAngleJSF.name);
                if (modifyZJSEnum.val == NodePhysicsModificationMode.defaultValue && nodeZAngle.val != nodeZAngle.defaultVal) return false;
                if (modifyZJSEnum.val == NodePhysicsModificationMode.customValue && nodeZAngle.val != zAngleJSF.val) return false;
            }

            return true;
        }

        public void SetNodePhysicsAction(JSONStorable nodeStorable)
        {
            if (modifyXJSEnum.val != NodePhysicsModificationMode.noChange)
            {
                JSONStorableFloat nodeXAngle = nodeStorable.GetFloatJSONParam(xAngleJSF.name);
                if (modifyXJSEnum.val == NodePhysicsModificationMode.defaultValue) nodeXAngle.SetValToDefault();
                if (modifyXJSEnum.val == NodePhysicsModificationMode.customValue) nodeXAngle.val = xAngleJSF.val;
            }
            if (modifyYJSEnum.val != NodePhysicsModificationMode.noChange)
            {
                JSONStorableFloat nodeYAngle = nodeStorable.GetFloatJSONParam(yAngleJSF.name);
                if (modifyYJSEnum.val == NodePhysicsModificationMode.defaultValue) nodeYAngle.SetValToDefault();
                if (modifyYJSEnum.val == NodePhysicsModificationMode.customValue) nodeYAngle.val = yAngleJSF.val;
            }
            if (modifyZJSEnum.val != NodePhysicsModificationMode.noChange)
            {
                JSONStorableFloat nodeZAngle = nodeStorable.GetFloatJSONParam(zAngleJSF.name);
                if (modifyZJSEnum.val == NodePhysicsModificationMode.defaultValue) nodeZAngle.SetValToDefault();
                if (modifyZJSEnum.val == NodePhysicsModificationMode.customValue) nodeZAngle.val = zAngleJSF.val;
            }

        }
    }
    public class NodePhysicsThesholdSprintDamperElement : JSONStorableObject
    {
        public JSONStorableEnumStringChooser modifyJSEnum;

        public JSONStorableFloat springJSF;
        public JSONStorableFloat damperJSF;
        public JSONStorableFloat thresholdJSF;

        public NodePhysicsThesholdSprintDamperElement(string contextLabel)
        {
            modifyJSEnum = new JSONStorableEnumStringChooser("Modify", "NodePhysicsModificationMode", NodePhysicsModificationMode.noChange,  "Modify "+contextLabel);

            if (contextLabel == "complyPosition")
            {
                springJSF = new JSONStorableFloat(contextLabel + "Spring", 1500f, 0f, 10000f, false);
                damperJSF = new JSONStorableFloat(contextLabel + "Damper", 100f, 0f, 1000f, false);
                thresholdJSF = new JSONStorableFloat(contextLabel + "Threshold", 0.001f, 0.0001f, 0.1f, true);
            }
            else
            {
                springJSF = new JSONStorableFloat(contextLabel + "Spring", 150f, 0f, 1000f, false);
                damperJSF = new JSONStorableFloat(contextLabel + "Damper", 10f, 0f, 100f, false);
                thresholdJSF = new JSONStorableFloat(contextLabel + "Threshold", 5f, 0.1f, 30f, true);
            }
            RegisterParam(modifyJSEnum);
            RegisterParam(springJSF);
            RegisterParam(damperJSF);
            RegisterParam(thresholdJSF);
        }

        public bool IsMatchingState(JSONStorable nodeStorable)
        {
            if (modifyJSEnum.val == NodePhysicsModificationMode.noChange) return true;

            JSONStorableFloat nodeSpringJSF = nodeStorable.GetFloatJSONParam(springJSF.name);
            JSONStorableFloat nodeDamperJSF = nodeStorable.GetFloatJSONParam(damperJSF.name);
            JSONStorableFloat nodeThresholdJSF = nodeStorable.GetFloatJSONParam(thresholdJSF.name);

            if (modifyJSEnum.val == NodePhysicsModificationMode.defaultValue)
            {
                if (nodeSpringJSF.val != nodeSpringJSF.defaultVal) return false;
                if (nodeDamperJSF.val != nodeDamperJSF.defaultVal) return false;
                if (nodeThresholdJSF.val != nodeThresholdJSF.defaultVal) return false;
            }
            else if (modifyJSEnum.val == NodePhysicsModificationMode.customValue)
            {
                if (nodeSpringJSF.val != springJSF.val) return false;
                if (nodeDamperJSF.val != damperJSF.val) return false;
                if (nodeThresholdJSF.val != thresholdJSF.val) return false;
            }

            return true;
        }

        public void SetNodePhysicsAction(JSONStorable nodeStorable)
        {
            if (modifyJSEnum.val == NodePhysicsModificationMode.noChange) return;

            JSONStorableFloat nodeSpringJSF = nodeStorable.GetFloatJSONParam(springJSF.name);
            JSONStorableFloat nodeDamperJSF = nodeStorable.GetFloatJSONParam(damperJSF.name);
            JSONStorableFloat nodeThresholdJSF = nodeStorable.GetFloatJSONParam(thresholdJSF.name);

            if (modifyJSEnum.val == NodePhysicsModificationMode.defaultValue)
            {
                nodeSpringJSF.SetValToDefault();
                nodeDamperJSF.SetValToDefault();
                nodeDamperJSF.SetValToDefault();

            }
            else if (modifyJSEnum.val == NodePhysicsModificationMode.customValue)
            {
                nodeSpringJSF.val = springJSF.val;
                nodeDamperJSF.val = damperJSF.val;
                nodeDamperJSF.val = thresholdJSF.val;
            }
        }
    }

    public class NodePhysicsSpringDamperForceElement : JSONStorableObject
    {
        public JSONStorableEnumStringChooser modifyJSEnum;

        public JSONStorableFloat springJSF;
        public JSONStorableFloat damperJSF;
        public JSONStorableFloat maxForceJSF;

        public NodePhysicsSpringDamperForceElement(string contextLabel)
        {
            modifyJSEnum = new JSONStorableEnumStringChooser("Modify", "NodePhysicsModificationMode", NodePhysicsModificationMode.noChange, "Modify " + contextLabel);
            if (contextLabel=="holdRotation")
            {
                springJSF = new JSONStorableFloat(contextLabel + "Spring", 100f, 0f, 1000f, false);
                damperJSF = new JSONStorableFloat(contextLabel + "Damper", 5f, 0f, 10f, false);
                maxForceJSF = new JSONStorableFloat(contextLabel + "MaxForce", 1000f, 0f, 1000f, false);
            }
            else if (contextLabel == "linkRotation" || contextLabel == "linkPosition")
            {
                springJSF = new JSONStorableFloat(contextLabel + "Spring", 100000f, 0f, 100000f, false);
                damperJSF = new JSONStorableFloat(contextLabel + "Damper", 250f, 0f, 1000f, false);
                maxForceJSF = new JSONStorableFloat(contextLabel + "MaxForce", 100000f, 0f, 100000f, false);
            }
            else if (contextLabel == "jointDrive")
            {
                springJSF = new JSONStorableFloat(contextLabel + "Spring", 25f, 0f, 200f, false);
                damperJSF = new JSONStorableFloat(contextLabel + "Damper", 0.5f, 0f, 10f, false);
                maxForceJSF = new JSONStorableFloat(contextLabel + "MaxForce", 5f, 0f, 100f, false);
            }
            else
            {
                springJSF = new JSONStorableFloat(contextLabel + "Spring", 2000f, 0f, 10000f, false);
                damperJSF = new JSONStorableFloat(contextLabel + "Damper", 35f, 0f, 100f, false);
                maxForceJSF = new JSONStorableFloat(contextLabel + "MaxForce", 1000f, 0f, 10000f, false);
            }
            
            RegisterParam(modifyJSEnum);
            RegisterParam(springJSF);
            RegisterParam(damperJSF);
            RegisterParam(maxForceJSF);
        }
        public bool IsMatchingState(JSONStorable nodeStorable)
        {
            if (modifyJSEnum.val == NodePhysicsModificationMode.noChange) return true;

            JSONStorableFloat nodeSpringJSF = nodeStorable.GetFloatJSONParam(springJSF.name);
            JSONStorableFloat nodeDamperJSF = nodeStorable.GetFloatJSONParam(damperJSF.name);
            JSONStorableFloat nodeMaxForceJSF = nodeStorable.GetFloatJSONParam(maxForceJSF.name);

            if (modifyJSEnum.val== NodePhysicsModificationMode.defaultValue)
            {
                if (nodeSpringJSF.val != nodeSpringJSF.defaultVal) return false;
                if (nodeDamperJSF.val != nodeDamperJSF.defaultVal) return false;
                if (nodeMaxForceJSF.val != nodeMaxForceJSF.defaultVal) return false;
            }
            else if (modifyJSEnum.val == NodePhysicsModificationMode.customValue)
            {
                if (nodeSpringJSF.val != springJSF.val) return false;
                if (nodeDamperJSF.val != damperJSF.val) return false;
                if (nodeMaxForceJSF.val != maxForceJSF.val) return false;
            }
            return true;
        }
        public void SetNodePhysicsAction(JSONStorable nodeStorable)
        {
            if (modifyJSEnum.val == NodePhysicsModificationMode.noChange) return;

            JSONStorableFloat nodeSpringJSF = nodeStorable.GetFloatJSONParam(springJSF.name);
            JSONStorableFloat nodeDamperJSF = nodeStorable.GetFloatJSONParam(damperJSF.name);
            JSONStorableFloat nodeMaxForceJSF = nodeStorable.GetFloatJSONParam(maxForceJSF.name);

            if (modifyJSEnum.val == NodePhysicsModificationMode.defaultValue)
            {
                nodeSpringJSF.SetValToDefault();
                nodeDamperJSF.SetValToDefault();
                nodeMaxForceJSF.SetValToDefault();

            }
            else if (modifyJSEnum.val == NodePhysicsModificationMode.customValue)
            {
                nodeSpringJSF.val = springJSF.val;
                nodeDamperJSF.val = damperJSF.val;
                nodeMaxForceJSF.val = maxForceJSF.val;
            }
        }

    }

    public class NodePhysicsPositionRotationSDF : JSONStorableObject
    {
        public NodePhysicsSpringDamperForceElement positionSpringDamperForce;
        public NodePhysicsSpringDamperForceElement rotationSpringDamperForce;

        public NodePhysicsPositionRotationSDF(string contextLabel)
        {
            positionSpringDamperForce = new NodePhysicsSpringDamperForceElement(contextLabel + "Position");
            rotationSpringDamperForce = new NodePhysicsSpringDamperForceElement(contextLabel + "Rotation");
        }
        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            JSONClass jc = base.GetJSON(jspLoadExclusions);
            jc["positionSpringDamperForce"] = positionSpringDamperForce.GetJSON();
            jc["rotationSpringDamperForce"] = rotationSpringDamperForce.GetJSON();
            return jc;
        }

        public void LoadJSON(JSONClass jc)
        {
            base.RestoreFromJSON(jc);
            if (jc["positionSpringDamperForce"] != null) positionSpringDamperForce.RestoreFromJSON((JSONClass)jc["positionSpringDamperForce"]);
            if (jc["rotationSpringDamperForce"] != null) rotationSpringDamperForce.RestoreFromJSON((JSONClass)jc["rotationSpringDamperForce"]);
        }

        public void CopyFrom(NodePhysicsPositionRotationSDF sourceComponent, string contextLabel)
        {
            base.CopyFrom(sourceComponent);
            positionSpringDamperForce = new NodePhysicsSpringDamperForceElement(contextLabel + "Position");
            rotationSpringDamperForce = new NodePhysicsSpringDamperForceElement(contextLabel + "Rotation");
            positionSpringDamperForce.CopyFrom(sourceComponent.positionSpringDamperForce);
            rotationSpringDamperForce.CopyFrom(sourceComponent.rotationSpringDamperForce);
        }

        public bool IsMatchingState(JSONStorable nodeStorable)
        {
            if (!positionSpringDamperForce.IsMatchingState(nodeStorable)) return false;
            if (!rotationSpringDamperForce.IsMatchingState(nodeStorable)) return false;
            return true;
        }

        public void SetNodePhysicsAction(JSONStorable nodeStorable)
        {
            positionSpringDamperForce.SetNodePhysicsAction(nodeStorable);
            rotationSpringDamperForce.SetNodePhysicsAction(nodeStorable);

        }

    }
    public class NodePhysicsCompliance : JSONStorableObject
    {
        public NodePhysicsThesholdSprintDamperElement positionComplianceNodePhysics;
        public NodePhysicsThesholdSprintDamperElement rotationComplianceNodePhysics;

        public JSONStorableEnumStringChooser modifyComplianceSpeedJSEnum;
        public JSONStorableFloat complianceSpeedJSF;
        public JSONStorableEnumStringChooser modifyJointDriveSpringJSEnum;
        public JSONStorableFloat jointDriveSpringJSF;

        public NodePhysicsCompliance(string contextLabel)
        {
            positionComplianceNodePhysics = new NodePhysicsThesholdSprintDamperElement(contextLabel + "Position");
            rotationComplianceNodePhysics = new NodePhysicsThesholdSprintDamperElement(contextLabel + "Rotation");
            modifyComplianceSpeedJSEnum = new JSONStorableEnumStringChooser("ModifyComplianceSpeed", "NodePhysicsModificationMode", NodePhysicsModificationMode.noChange, "Modify " + contextLabel +"Speed");
            complianceSpeedJSF = new JSONStorableFloat(contextLabel + "Speed", 10f, 0f, 100f, true);
            modifyJointDriveSpringJSEnum = new JSONStorableEnumStringChooser("ModifyJointDriveSpring", "NodePhysicsModificationMode", NodePhysicsModificationMode.noChange, "Modify " + contextLabel + " JointDriveSpring");
            jointDriveSpringJSF = new JSONStorableFloat(contextLabel + "JointDriveSpring", 20f, 0f, 100f, false);
            RegisterParam(modifyComplianceSpeedJSEnum);
            RegisterParam(complianceSpeedJSF);
            RegisterParam(modifyJointDriveSpringJSEnum);
            RegisterParam(jointDriveSpringJSF);
        }
        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            JSONClass jc = base.GetJSON(jspLoadExclusions);
            jc["positionComplianceNodePhysics"] = positionComplianceNodePhysics.GetJSON();
            jc["rotationComplianceNodePhysics"] = rotationComplianceNodePhysics.GetJSON();
            return jc;
        }

        public void LoadJSON(JSONClass jc)
        {
            base.RestoreFromJSON(jc);
            if (jc["positionComplianceNodePhysics"] != null) positionComplianceNodePhysics.RestoreFromJSON((JSONClass)jc["positionComplianceNodePhysics"]);
            if (jc["rotationComplianceNodePhysics"] != null) rotationComplianceNodePhysics.RestoreFromJSON((JSONClass)jc["rotationComplianceNodePhysics"]);
        }

        public void CopyFrom(NodePhysicsCompliance sourceComponent, string contextLabel)
        {
            base.CopyFrom(sourceComponent);
            positionComplianceNodePhysics = new NodePhysicsThesholdSprintDamperElement(contextLabel + "Position");
            rotationComplianceNodePhysics = new NodePhysicsThesholdSprintDamperElement(contextLabel + "Rotation");
            positionComplianceNodePhysics.CopyFrom(sourceComponent.positionComplianceNodePhysics);
            rotationComplianceNodePhysics.CopyFrom(sourceComponent.rotationComplianceNodePhysics);
        }
        public bool IsMatchingState(JSONStorable nodeStorable)
        {
            if (modifyComplianceSpeedJSEnum.val != NodePhysicsModificationMode.noChange)
            {
                JSONStorableFloat nodeComplianceSpeed = nodeStorable.GetFloatJSONParam(complianceSpeedJSF.name);
                if (modifyComplianceSpeedJSEnum.val == NodePhysicsModificationMode.defaultValue && nodeComplianceSpeed.val != nodeComplianceSpeed.defaultVal) return false;
                if (modifyComplianceSpeedJSEnum.val == NodePhysicsModificationMode.customValue && nodeComplianceSpeed.val != complianceSpeedJSF.val) return false;
            }
            if (modifyJointDriveSpringJSEnum.val != NodePhysicsModificationMode.noChange)
            {
                JSONStorableFloat nodeJointDriveSpring = nodeStorable.GetFloatJSONParam(jointDriveSpringJSF.name);
                if (modifyJointDriveSpringJSEnum.val == NodePhysicsModificationMode.defaultValue && nodeJointDriveSpring.val != nodeJointDriveSpring.defaultVal) return false;
                if (modifyJointDriveSpringJSEnum.val == NodePhysicsModificationMode.customValue && nodeJointDriveSpring.val != jointDriveSpringJSF.val) return false;
            }

            if (!positionComplianceNodePhysics.IsMatchingState(nodeStorable)) return false;
            if (!rotationComplianceNodePhysics.IsMatchingState(nodeStorable)) return false;

            return true;
        }
        public void SetNodePhysicsAction(JSONStorable nodeStorable)
        {
            if (modifyComplianceSpeedJSEnum.val != NodePhysicsModificationMode.noChange)
            {
                JSONStorableFloat nodeComplianceSpeed = nodeStorable.GetFloatJSONParam(complianceSpeedJSF.name);
                if (modifyComplianceSpeedJSEnum.val == NodePhysicsModificationMode.defaultValue)  nodeComplianceSpeed.SetValToDefault();
                if (modifyComplianceSpeedJSEnum.val == NodePhysicsModificationMode.customValue) nodeComplianceSpeed.val = complianceSpeedJSF.val;
            }
            if (modifyJointDriveSpringJSEnum.val != NodePhysicsModificationMode.noChange)
            {
                JSONStorableFloat nodeJointDriveSpring = nodeStorable.GetFloatJSONParam(jointDriveSpringJSF.name);
                if (modifyJointDriveSpringJSEnum.val == NodePhysicsModificationMode.defaultValue)nodeJointDriveSpring.SetValToDefault() ;
                if (modifyJointDriveSpringJSEnum.val == NodePhysicsModificationMode.customValue) nodeJointDriveSpring.val = jointDriveSpringJSF.val;
            }
            positionComplianceNodePhysics.SetNodePhysicsAction(nodeStorable);
            rotationComplianceNodePhysics.SetNodePhysicsAction(nodeStorable);

        }


    }
    public class NodePhysicsComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public NodePhysicsPositionRotationSDF holdNodePhysics;
        public NodePhysicsPositionRotationSDF linkNodePhysics;
        public NodePhysicsSpringDamperForceElement jointDriveSDFPhysics;
        public NodePhysicsXYZAngleElement jointDriveXYZPhysics;
        public NodePhysicsCompliance nodePhysicsCompliance;

        public JSONStorableEnumStringChooser modifyMaxVelocityJSEnum;
        public JSONStorableBool maxVelocityEnabledJSB;
        public JSONStorableFloat maxVelocityJSF;

        public NodePhysicsComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;
            holdNodePhysics = new NodePhysicsPositionRotationSDF("hold");
            linkNodePhysics = new NodePhysicsPositionRotationSDF("link");
            jointDriveSDFPhysics = new NodePhysicsSpringDamperForceElement("jointDrive");
            jointDriveXYZPhysics = new NodePhysicsXYZAngleElement("jointDrive");
            nodePhysicsCompliance = new NodePhysicsCompliance("comply");

            modifyMaxVelocityJSEnum = new JSONStorableEnumStringChooser("ModifyMaxVelocity", "NodePhysicsModificationMode", NodePhysicsModificationMode.noChange, "Modify Max Velocity Physics");
            maxVelocityEnabledJSB = new JSONStorableBool("maxVelocityEnable", true);
            maxVelocityJSF = new JSONStorableFloat("maxVelocity", 10f, 0f, 100f, false);
            RegisterParam(modifyMaxVelocityJSEnum);
            RegisterParam(maxVelocityEnabledJSB);
            RegisterParam(maxVelocityJSF);
        }

        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            JSONClass jc = base.GetJSON(jspLoadExclusions);
            jc["holdNodePhysics"] = holdNodePhysics.GetJSON();
            jc["linkNodePhysics"] = linkNodePhysics.GetJSON();
            jc["jointDriveSDFPhysics"] = jointDriveSDFPhysics.GetJSON();
            jc["jointDriveXYZPhysics"] = jointDriveXYZPhysics.GetJSON();
            jc["nodePhysicsCompliance"] = nodePhysicsCompliance.GetJSON();
            return jc;
        }

        public void LoadJSON(JSONClass jc)
        {
            base.RestoreFromJSON(jc);
            if (jc["holdNodePhysics"] != null) holdNodePhysics.LoadJSON((JSONClass)jc["holdNodePhysics"]);
            if (jc["linkNodePhysics"] != null) linkNodePhysics.LoadJSON((JSONClass)jc["linkNodePhysics"]);
            if (jc["jointDriveSDFPhysics"] != null) jointDriveSDFPhysics.RestoreFromJSON((JSONClass)jc["jointDriveSDFPhysics"]);
            if (jc["jointDriveXYZPhysics"] != null) jointDriveXYZPhysics.RestoreFromJSON((JSONClass)jc["jointDriveXYZPhysics"]);
            if (jc["nodePhysicsCompliance"] != null) nodePhysicsCompliance.LoadJSON((JSONClass)jc["nodePhysicsCompliance"]);
        }

        public void CopyFrom(NodePhysicsComponent sourceComponent)
        {
            base.CopyFrom(sourceComponent);
            holdNodePhysics = new NodePhysicsPositionRotationSDF("hold");
            linkNodePhysics = new NodePhysicsPositionRotationSDF("link");
            jointDriveSDFPhysics = new NodePhysicsSpringDamperForceElement("jointDrive");
            jointDriveXYZPhysics = new NodePhysicsXYZAngleElement("jointDrive");
            nodePhysicsCompliance = new NodePhysicsCompliance("comply");

            holdNodePhysics.CopyFrom(sourceComponent.holdNodePhysics, "hold");
            linkNodePhysics.CopyFrom(sourceComponent.linkNodePhysics, "link");
            jointDriveSDFPhysics.CopyFrom(sourceComponent.jointDriveSDFPhysics);
            jointDriveXYZPhysics.CopyFrom(sourceComponent.jointDriveXYZPhysics);
            nodePhysicsCompliance.CopyFrom(sourceComponent.nodePhysicsCompliance, "comply");
        }

        public bool IsMatchingState(Atom atom, List<string> activeNodeSelections)
        {
            
            if (activeNodeSelections.Count == 0) return false;
            foreach(var nodeName in activeNodeSelections)
            {
                JSONStorable nodeStorable = atom.GetStorableByID(nodeName);
                if (modifyMaxVelocityJSEnum.val !=NodePhysicsModificationMode.noChange)
                {
                    JSONStorableBool nodeMaxVelocityEnabled = nodeStorable.GetBoolJSONParam("maxVelocityEnabled");
                    JSONStorableFloat nodeMaxVelocity = nodeStorable.GetFloatJSONParam("maxVelocity");
                    if (modifyMaxVelocityJSEnum.val == NodePhysicsModificationMode.defaultValue)
                    {                        
                        if (nodeMaxVelocityEnabled.val != nodeMaxVelocityEnabled.defaultVal) return false;                        
                        if (nodeMaxVelocity.val != nodeMaxVelocity.defaultVal) return false;

                    }
                    if (modifyMaxVelocityJSEnum.val == NodePhysicsModificationMode.customValue)
                    {
                        if (nodeMaxVelocityEnabled.val != maxVelocityEnabledJSB.val) return false;
                        if (nodeMaxVelocity.val != nodeMaxVelocity.val) return false;
                    }

                }
                if (!holdNodePhysics.IsMatchingState(nodeStorable)) return false;
                if (!linkNodePhysics.IsMatchingState(nodeStorable)) return false;
                if (nodeName != "hipControl" && !jointDriveSDFPhysics.IsMatchingState(nodeStorable)) return false;
                if (nodeName != "hipControl" && !jointDriveXYZPhysics.IsMatchingState(nodeStorable)) return false;
                if (!nodePhysicsCompliance.IsMatchingState(nodeStorable)) return false;
            }
            return true;
        }
        public void SetNodePhysicsAction(Atom targetAtom, List<string> activeNodeSelections)
        {
            if (activeNodeSelections.Count == 0) return ;
            foreach (var nodeName in activeNodeSelections)
            {
                JSONStorable nodeStorable = targetAtom.GetStorableByID(nodeName);
                if (modifyMaxVelocityJSEnum.val != NodePhysicsModificationMode.noChange)
                {
                    JSONStorableBool nodeMaxVelocityEnabled = nodeStorable.GetBoolJSONParam("maxVelocityEnabled");
                    JSONStorableFloat nodeMaxVelocity = nodeStorable.GetFloatJSONParam("maxVelocity");
                    if (modifyMaxVelocityJSEnum.val == NodePhysicsModificationMode.defaultValue)
                    {
                        nodeMaxVelocityEnabled.SetValToDefault();
                        nodeMaxVelocity.SetValToDefault();
                    }
                    if (modifyMaxVelocityJSEnum.val == NodePhysicsModificationMode.customValue)
                    {
                        nodeMaxVelocityEnabled.val = maxVelocityEnabledJSB.val;
                        nodeMaxVelocity.val = nodeMaxVelocity.val;
                    }

                }
                holdNodePhysics.SetNodePhysicsAction(nodeStorable);
                linkNodePhysics.SetNodePhysicsAction(nodeStorable);
                if (nodeName != "hipControl" && nodeName != "control")
                {
                    jointDriveSDFPhysics.SetNodePhysicsAction(nodeStorable);
                    jointDriveXYZPhysics.SetNodePhysicsAction(nodeStorable);
                }
                if (nodeName != "control") nodePhysicsCompliance.SetNodePhysicsAction(nodeStorable);
            }


        }

    }
    public class NodeSelectionComponent : ButtonOperationComponentBase
    {
        private Dictionary<string, JSONStorableBool> nodeSelectionDict = new Dictionary<string, JSONStorableBool>();

        public NodeSelectionComponent(UIAButtonOperation parent) : base(parent)
        {
            foreach (string nodeName in UIAConsts.headFCs) InitNodeName(nodeName);
            foreach (string nodeName in UIAConsts.bodyFCs) InitNodeName(nodeName);
            foreach (string nodeName in UIAConsts.genFCs) InitNodeName(nodeName);
            foreach (string nodeName in UIAConsts.limbFCs) InitNodeName(nodeName);
        }

        private void InitNodeName(string nodeName)
        {
            nodeSelectionDict.Add(nodeName, new JSONStorableBool(nodeName,false));
            RegisterParam(nodeSelectionDict[nodeName]);
        }

        public List<string> GetActiveNodeSelections()
        {
            var nodeControlSelections = new List<string>();
            foreach (string nodeName in UIAConsts.bodyFCs)
            {
                if (nodeSelectionDict[nodeName].val) nodeControlSelections.Add(nodeName);
            }
            foreach (string nodeName in UIAConsts.headFCs)
            {
                if (nodeSelectionDict[nodeName].val) nodeControlSelections.Add(nodeName);
            }
            foreach (string nodeName in UIAConsts.limbFCs)
            {
                if (nodeSelectionDict[nodeName].val) nodeControlSelections.Add(nodeName);
            }
            foreach (string nodeName in UIAConsts.genFCs)
            {
                if (nodeSelectionDict[nodeName].val) nodeControlSelections.Add(nodeName);
            }
            return nodeControlSelections;

        }
        public List<JSONStorableBool> GetNodeControlSelections(int nodeBodyRegion)
        {
            var nodeControlSelections = new List<JSONStorableBool>();

            var nodeNames = new List<string>();
            if (nodeBodyRegion == NodeBodyRegion.torso) nodeNames = UIAConsts.bodyFCs;
            if (nodeBodyRegion == NodeBodyRegion.head) nodeNames = UIAConsts.headFCs;
            if (nodeBodyRegion == NodeBodyRegion.gens) nodeNames = UIAConsts.genFCs;
            if (nodeBodyRegion == NodeBodyRegion.limbs) nodeNames = UIAConsts.limbFCs;

            foreach (var nodeName in nodeNames)
            {
                nodeControlSelections.Add(nodeSelectionDict[nodeName]);
            }

            return nodeControlSelections;
        }
    }
    public class NodeControlComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public JSONStorableEnumStringChooser multiNodeControlStateJSEnum;

        private Dictionary<string, JSONStorableEnumStringChooser> customNodeControlStateDict = new Dictionary<string, JSONStorableEnumStringChooser>();

        private JSONStorableEnumStringChooser rootNodeControlState;

        public NodeControlComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;
            multiNodeControlStateJSEnum = new JSONStorableEnumStringChooser("multiNodeControlStateOn", MultiNodeControlState.enumManifestName, MultiNodeControlState.custom, "Control Preset");
            RegisterParam(multiNodeControlStateJSEnum);

            foreach (string nodeName in UIAConsts.headFCs) InitNodeName(nodeName);
            foreach (string nodeName in UIAConsts.bodyFCs) InitNodeName(nodeName);
            foreach (string nodeName in UIAConsts.genFCs) InitNodeName(nodeName);
            foreach (string nodeName in UIAConsts.limbFCs) InitNodeName(nodeName);

            rootNodeControlState = new JSONStorableEnumStringChooser("rootNode", NodeControlState.enumManifestName, NodeControlState.on, "Control State");
            RegisterParam(rootNodeControlState);
        }

        private void SetControlState(FreeControllerV3 fc, int targetState,bool isRotation)
        {
            if (isRotation) fc.currentRotationState = GetEquivalentFC3Rotation(targetState);
            else fc.currentPositionState = GetEquivalentFC3Position(targetState);
        }
        public void SetControlStateAction(Atom targetAtom, bool isRotation)
        {
            if (multiNodeControlStateJSEnum.val != MultiNodeControlState.custom)
            {
                List<string> joints = targetMultiNodeControlJointNames;
                int targetState = targetMultiNodeControlState;

                foreach (var fc in targetAtom.freeControllers)
                {
                    if (joints.Contains(fc.name)) SetControlState(fc, targetState, isRotation);
                    else SetControlState(fc, NodeControlState.off, isRotation);
                }
            }
            else
            {
                Dictionary<string, FreeControllerV3> atomFCDict = new Dictionary<string, FreeControllerV3>();
                foreach (var fc in targetAtom.freeControllers) atomFCDict.Add(fc.name, fc);

                foreach (var kvp in customNodeControlStateDict)
                {
                    if (kvp.Value.val != NodeControlState.noChange) SetControlState(atomFCDict[kvp.Key], kvp.Value.val,isRotation);
                }
            }
        }

        private int targetMultiNodeControlState
        {
            get
            {
                if (multiNodeControlStateJSEnum.val == MultiNodeControlState.complyAllJoints || multiNodeControlStateJSEnum.val == MultiNodeControlState.complyKeyElbowKneeJoints || multiNodeControlStateJSEnum.val == MultiNodeControlState.complyKeyJoints) return NodeControlState.comply;
                if (multiNodeControlStateJSEnum.val == MultiNodeControlState.offAllJoints) return NodeControlState.off;
                return NodeControlState.on;
            }
        }

        private List<string> targetMultiNodeControlJointNames
        {
            get
            {
                if (multiNodeControlStateJSEnum.val == MultiNodeControlState.complyKeyJoints || multiNodeControlStateJSEnum.val == MultiNodeControlState.onKeyJoints) return UIAConsts.keyJointFCs;
                if (multiNodeControlStateJSEnum.val == MultiNodeControlState.complyKeyElbowKneeJoints || multiNodeControlStateJSEnum.val == MultiNodeControlState.onKeyElbowKneeJoints) return UIAConsts.keyElbowKneeJointFCs;
                return UIAConsts.allJointFCs;
            }
        }

        private FreeControllerV3.RotationState GetEquivalentFC3Rotation(int nodeState)
        {
            if (nodeState == NodeControlState.on) return FreeControllerV3.RotationState.On ;
            if (nodeState == NodeControlState.off) return FreeControllerV3.RotationState.Off;
            if (nodeState == NodeControlState.comply) return FreeControllerV3.RotationState.Comply;
            if (nodeState == NodeControlState.parentLink) return FreeControllerV3.RotationState.ParentLink;
            if (nodeState == NodeControlState.physicsLink ) return FreeControllerV3.RotationState.PhysicsLink;
            if (nodeState == NodeControlState.lockNode ) return FreeControllerV3.RotationState.Lock;
            if (nodeState == NodeControlState.hold ) return FreeControllerV3.RotationState.Hold;
            return FreeControllerV3.RotationState.Off;
        }
        private FreeControllerV3.PositionState GetEquivalentFC3Position(int nodeState)
        {
            if (nodeState == NodeControlState.on) return FreeControllerV3.PositionState.On;
            if (nodeState == NodeControlState.off) return FreeControllerV3.PositionState.Off;
            if (nodeState == NodeControlState.comply) return FreeControllerV3.PositionState.Comply;
            if (nodeState == NodeControlState.parentLink) return FreeControllerV3.PositionState.ParentLink;
            if (nodeState == NodeControlState.physicsLink) return FreeControllerV3.PositionState.PhysicsLink;
            if (nodeState == NodeControlState.lockNode) return FreeControllerV3.PositionState.Lock;
            if (nodeState == NodeControlState.hold) return FreeControllerV3.PositionState.Hold;
            return FreeControllerV3.PositionState.Off;
        }
        private int GetEquivalentNodeControlState(FreeControllerV3.RotationState fc3RotationState)
        {
            if (fc3RotationState == FreeControllerV3.RotationState.On) return NodeControlState.on;
            if (fc3RotationState == FreeControllerV3.RotationState.Off) return NodeControlState.off;
            if (fc3RotationState == FreeControllerV3.RotationState.Comply) return NodeControlState.comply;
            if (fc3RotationState == FreeControllerV3.RotationState.ParentLink) return NodeControlState.parentLink;
            if (fc3RotationState == FreeControllerV3.RotationState.PhysicsLink) return NodeControlState.physicsLink;
            if (fc3RotationState == FreeControllerV3.RotationState.Lock) return NodeControlState.lockNode;
            if (fc3RotationState == FreeControllerV3.RotationState.Hold) return NodeControlState.hold;
            return -1;
        }
        private int GetEquivalentNodeControlState(FreeControllerV3.PositionState fc3RotationState)
        {
            if (fc3RotationState == FreeControllerV3.PositionState.On) return NodeControlState.on;
            if (fc3RotationState == FreeControllerV3.PositionState.Off) return NodeControlState.off;
            if (fc3RotationState == FreeControllerV3.PositionState.Comply) return NodeControlState.comply;
            if (fc3RotationState == FreeControllerV3.PositionState.ParentLink) return NodeControlState.parentLink;
            if (fc3RotationState == FreeControllerV3.PositionState.PhysicsLink) return NodeControlState.physicsLink;
            if (fc3RotationState == FreeControllerV3.PositionState.Lock) return NodeControlState.lockNode;
            if (fc3RotationState == FreeControllerV3.PositionState.Hold) return NodeControlState.hold;
            return -1;
        }
        private bool MatchFCState(FreeControllerV3 fc3,int nodeTargetState, bool isRotation)
        {
            if (isRotation)
            {
                if (nodeTargetState!= NodeControlState.noChange && nodeTargetState != GetEquivalentNodeControlState(fc3.currentRotationState)) return false;
            }
            else
            {
                if (nodeTargetState != NodeControlState.noChange && nodeTargetState != GetEquivalentNodeControlState(fc3.currentPositionState)) return false;
            }
            return true;
        }
        public bool IsMatchingState(Atom atom, bool isRotations)
        {
            if (multiNodeControlStateJSEnum.val != MultiNodeControlState.custom)
            {
                List<string> joints = targetMultiNodeControlJointNames;              
                int targetState = targetMultiNodeControlState;

                foreach (var fc in atom.freeControllers)
                {
                    if (joints.Contains(fc.name) && !MatchFCState(fc, targetState, isRotations)) return false;
                    if (!joints.Contains(fc.name) && !MatchFCState(fc, NodeControlState.off, isRotations)) return false;
                }
            }
            else
            {
                Dictionary<string, FreeControllerV3> atomFCDict = new Dictionary<string, FreeControllerV3>();
                foreach (var fc in atom.freeControllers) atomFCDict.Add(fc.name, fc);
                foreach (var kvp in customNodeControlStateDict)
                {
                    if (!MatchFCState(atomFCDict[kvp.Key], kvp.Value.val, isRotations)) return false;
                }
            }

            return true;
        }
        private void InitNodeName(string nodeName)
        {
            customNodeControlStateDict.Add(nodeName, new JSONStorableEnumStringChooser(nodeName, NodeControlState.enumManifestName, NodeControlState.noChange, nodeName));
            RegisterParam(customNodeControlStateDict[nodeName]);
        }

        public List<JSONStorableEnumStringChooser> GetNodeControlStates(int nodeBodyRegion)
        {
            var nodeControlStates = new List<JSONStorableEnumStringChooser>();

            var nodeNames = new List<string>();
            if (nodeBodyRegion == NodeBodyRegion.torso) nodeNames = UIAConsts.bodyFCs;
            if (nodeBodyRegion == NodeBodyRegion.head) nodeNames = UIAConsts.headFCs;
            if (nodeBodyRegion == NodeBodyRegion.gens) nodeNames = UIAConsts.genFCs;
            if (nodeBodyRegion == NodeBodyRegion.limbs) nodeNames = UIAConsts.limbFCs;

            foreach (var nodeName in nodeNames) {
                nodeControlStates.Add(customNodeControlStateDict[nodeName]);
            }

            return nodeControlStates;
        }
        public JSONStorableEnumStringChooser GetRootNodeControlStates()
        {
            return rootNodeControlState;
        }
    }
    public class AppearancePresetComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public JSONStorableBool suppressClothingLoadJSBool;
        public JSONStorableBool onlySuppressRealClothingJSB;
        public JSONStorableBool suppressPersonScaleLoadJSBool;

        public static string lastLoadSceneFromVar = "";

        public AppearancePresetComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;

            suppressClothingLoadJSBool = new JSONStorableBool("suppressClothingLoad", false);
            onlySuppressRealClothingJSB = new JSONStorableBool("onlySuppressRealClothing", true);
            
            RegisterParam(suppressClothingLoadJSBool);
            RegisterParam(onlySuppressRealClothingJSB);           

            suppressPersonScaleLoadJSBool = new JSONStorableBool("suppressPersonScaleLoad", false);
            RegisterParam(suppressPersonScaleLoadJSBool);
        }

        public static void OnSceneLoaded()
        {
            string currentLoadDir = SuperController.singleton.currentLoadDir;
            if (currentLoadDir.Contains(":/Saves/scene")) lastLoadSceneFromVar = currentLoadDir;
        }

        public void PlayLegacyPresetAction(Atom atom)
        {
            FileReference fileRef = null;

            switch (buttonTypeJSEnum.val)
            {
                case UIAButtonOpType.loadLegacyLook:
                case UIAButtonOpType.saveLegacyLook:
                    fileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.legacyLookPreset];
                    break;
                case UIAButtonOpType.loadLegacyPose:
                case UIAButtonOpType.saveLegacyPose:
                    fileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.legacyPosePreset];
                    break;
                case UIAButtonOpType.loadLegacyPreset:
                case UIAButtonOpType.saveLegacyPreset:
                    fileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.legacyPreset];
                    break;
            }

            if (fileRef.currentActionSelectedFile != "" && (FileManagerSecure.FileExists(fileRef.currentActionSelectedFile) || UIAButtonOpType.IsLegacyPresetSaveType(buttonTypeJSEnum.val)))
            {
                switch (buttonTypeJSEnum.val)
                {
                    case UIAButtonOpType.saveLegacyPreset:
                    case UIAButtonOpType.saveLegacyPose:
                    case UIAButtonOpType.saveLegacyLook:
                        SuperController.singleton.Save(fileRef.currentActionSelectedFile, atom, buttonTypeJSEnum.val == UIAButtonOpType.saveLegacyPose || buttonTypeJSEnum.val == UIAButtonOpType.saveLegacyPose, buttonTypeJSEnum.val == UIAButtonOpType.saveLegacyLook || buttonTypeJSEnum.val == UIAButtonOpType.saveLegacyPose);
                        break;
                    case UIAButtonOpType.loadLegacyLook:
                        atom.LoadAppearancePreset(fileRef.currentActionSelectedFile);
                        break;
                    case UIAButtonOpType.loadLegacyPose:
                        atom.LoadPhysicalPreset(fileRef.currentActionSelectedFile);
                        break;
                    case UIAButtonOpType.loadLegacyPreset:
                        atom.LoadPreset(fileRef.currentActionSelectedFile);
                        break;
                }
            }
        }

        public void PlayPresetAction(Atom atom)
        {
            JSONStorable js = null;
            FileReference fileRef = null;

            switch (buttonTypeJSEnum.val)
            {
                case UIAButtonOpType.loadAppPreset:
                    if (!PresetLoadSettings.onlyLoadClothingFromAppPresetJSB.val)  js = atom.GetStorableByID("AppearancePresets");
                    else js = atom.GetStorableByID("ClothingPresets");
                    fileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.appearancePreset];
                    break;
                case UIAButtonOpType.loadAnimationPreset:
                    js = atom.GetStorableByID("AnimationPresets");
                    fileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.animationPreset];
                    break;
                case UIAButtonOpType.loadPosePreset:
                    js = atom.GetStorableByID("PosePresets");
                    fileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.posePreset];
                    break;
                case UIAButtonOpType.loadHairPreset:
                    js = atom.GetStorableByID("HairPresets");
                    fileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.hairPreset];
                    break;
                case UIAButtonOpType.loadGlutePreset:
                    js = atom.GetStorableByID("FemaleGlutePhysicsPresets");
                    if (js == null) js = atom.GetStorableByID("MaleGlutePhysicsPresets");
                    fileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.glutePreset];
                    break;
                case UIAButtonOpType.loadBreastPreset:
                    js = atom.GetStorableByID("FemaleBreastPhysicsPresets");
                    if (js == null) js = atom.GetStorableByID("MaleBreastPhysicsPresets");
                    fileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.breastPreset];
                    break;
                case UIAButtonOpType.loadMorphPreset:
                    js = atom.GetStorableByID("MorphPresets");
                    fileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.morphPreset];
                    break;
                case UIAButtonOpType.loadSkinPreset:
                    js = atom.GetStorableByID("SkinPresets");
                    fileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.skinPreset];
                    break;
                case UIAButtonOpType.loadGeneralPreset:
                    js = atom.GetStorableByID("Preset");
                    fileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.generalPreset];
                    break;
                case UIAButtonOpType.loadClothPreset:
                    js = atom.GetStorableByID("ClothingPresets");
                    fileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.clothingPreset];
                    break;
                case UIAButtonOpType.loadPluginsPreset:
                    if (atom.name == "CoreControl" && atom.type == "SessionPluginManager")
                    {
                        js = SuperController.singleton.sessionPresetManagerControl;
                        fileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.sessionPluginPreset];
                    }
                    else if (atom.name == "CoreControl")
                    {
                        js = atom.GetStorableByID("PluginManagerPresets");
                        fileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.scenePluginPreset];
                    }
                    else
                    {
                        js = atom.GetStorableByID("PluginPresets");
                        fileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.pluginsPreset];
                    }
                    
                    break;
            }

            if (js != null && fileRef.currentActionSelectedFile != "")
            {
                // If VAR enabling has activated a VAR with this in then we will need to update currentActionSelectedFile
                fileRef.UpdateCurrentActionSelectedFileWithLatest();

                if (parentButtonOperation.savePresetJSB.val) SavePreset(atom,js,fileRef) ;
                else if (FileManagerSecure.FileExists(fileRef.currentActionSelectedFile))
                {
                    if (buttonTypeJSEnum.val == UIAButtonOpType.loadSkinPreset && parentButtonOperation.skinPresetDecalComponent.AnyDecalLoadsActive() && PatreonFeatures.patreonContentEnabled) PatreonFeatures.SkinPresetDecalLoad(atom, fileRef, parentButtonOperation.skinPresetDecalComponent);
                    else if (buttonTypeJSEnum.val == UIAButtonOpType.loadHairPreset && fileRef.suppressHairStyleJSB.val)
                    {
                        var hairPresetJC = (JSONClass)SuperController.singleton.LoadJSON(SuperController.singleton.NormalizePath(fileRef.currentActionSelectedFile));
                        var hairPresetStorablesByID = JSONUtils.GetJCStorablesByID(hairPresetJC["storables"].AsArray);
                        var hairItemIDs = JSONUtils.GetHairIDs(hairPresetStorablesByID["geometry"], FileUtils.GetFullPackageName(fileRef.currentActionSelectedFile));

                        if (hairItemIDs.Count > 0)
                        {
                            JSONClass hairSimJC = null;
                            JSONClass hairScalpJC = null;
                            foreach (var hairID in hairItemIDs)
                            {
                                if (hairSimJC == null && hairPresetStorablesByID.ContainsKey(hairID + "Sim")) hairSimJC = hairPresetStorablesByID[hairID + "Sim"];

                                var scalpID = hairPresetStorablesByID.Keys.FirstOrDefault(x => x.StartsWith(hairID) && (x.EndsWith("ScalpMaterial") || x.EndsWith("ScalpMaterialCombined")));
                               
                                if (scalpID!=null && hairPresetStorablesByID.ContainsKey(scalpID) && (hairScalpJC == null || hairPresetStorablesByID[scalpID]["Alpha Adjust"].AsFloat > -1f)) hairScalpJC = hairPresetStorablesByID[scalpID];
                            }
                            if (hairSimJC != null) HairColorComponent.UpdateHairMaterials(atom, hairSimJC);
                            if (hairScalpJC != null) HairColorComponent.UpdateScalpMaterials(atom, hairScalpJC);
                        }
                    }
                    else if (buttonTypeJSEnum.val == UIAButtonOpType.loadHairPreset && fileRef.suppressHairColorJSB.val && atom.GetComponentsInChildren<DAZHairGroup>().Count() > 0)
                    {
                        JSONClass sourceHairSimJC = null;
                        JSONClass sourceHairScalpJC = null;
                        foreach (DAZHairGroup hairGroup in atom.GetComponentsInChildren<DAZHairGroup>())
                        {
                            HairSimControl hsc = hairGroup.GetComponentInChildren<HairSimControl>();
                            if (hsc != null)
                            {
                                sourceHairSimJC = hsc.GetJSON(true, true, true);
                                DAZSkinWrapMaterialOptions scalpMaterials = hairGroup.GetComponentInChildren<DAZSkinWrapMaterialOptions>();
                                if (scalpMaterials != null && (scalpMaterials.GetFloatParamValue("Alpha Adjust") > -1f || sourceHairScalpJC == null)) sourceHairScalpJC = scalpMaterials.GetJSON(true, true, true);
                            }
                        }

                        var hairPresetJC = (JSONClass)SuperController.singleton.LoadJSON(SuperController.singleton.NormalizePath(fileRef.currentActionSelectedFile));
                        var hairPresetStorablesByID = JSONUtils.GetJCStorablesByID(hairPresetJC["storables"].AsArray);
                        var hairItemIDs = JSONUtils.GetHairIDs(hairPresetStorablesByID["geometry"], FileUtils.GetFullPackageName(fileRef.currentActionSelectedFile));

                        if (hairItemIDs.Count > 0)
                        {
                            foreach (var hairID in hairItemIDs)
                            {
                                if (sourceHairSimJC != null && hairPresetStorablesByID.ContainsKey(hairID + "Sim")) HairColorComponent.UpdateHairMaterialColorJC(sourceHairSimJC, hairPresetStorablesByID[hairID + "Sim"]);
                                var scalpID = hairPresetStorablesByID.Keys.FirstOrDefault(x => x.StartsWith(hairID) && (x.EndsWith("ScalpMaterial") || x.EndsWith("ScalpMaterialCombined")));

                                if (scalpID!=null && hairPresetStorablesByID.ContainsKey(scalpID) && sourceHairScalpJC != null && hairPresetStorablesByID[scalpID]["Alpha Adjust"].AsFloat > -1f) HairColorComponent.UpdateHairScalpColorJC(sourceHairScalpJC, hairPresetStorablesByID[scalpID]);
                            }
                        }

                        JSONStorable presetJS = atom.GetStorableByID("HairPresets");
                        PresetManager pm = presetJS.GetComponentInChildren<PresetManager>();

                        atom.SetLastRestoredData(hairPresetJC, true, true);
                        pm.LoadPresetFromJSON(hairPresetJC, false);

                    }
                    else LoadPreset(atom, js, fileRef, (suppressClothingLoadJSBool.val || PresetLoadSettings.suppressClothingLoadJSB.val) && buttonTypeJSEnum.val == UIAButtonOpType.loadAppPreset, (suppressPersonScaleLoadJSBool.val || PresetLoadSettings.suppressScaleLoadJSB.val) && buttonTypeJSEnum.val == UIAButtonOpType.loadAppPreset, onlySuppressRealClothing: onlySuppressRealClothingJSB.val, onlyRemoveRealClothing: fileRef.onlyReplaceRealClothingJSB.val);
                        
                }
                
            }
        }


        public static void SavePreset(Atom atom, JSONStorable js, FileReference fileRef)
        {
            JSONStorableBool loadOnSelectJSON = js.GetBoolJSONParam("loadPresetOnSelect");
            bool preState = loadOnSelectJSON.val;
            loadOnSelectJSON.val = false;

            JSONStorableUrl presetPathJSON = js.GetUrlJSONParam("presetBrowsePath");

            string fileName = SuperController.singleton.NormalizePath(fileRef.currentActionSelectedFile);

            if (fileRef.fileSelectionModeJSEnum.val==FileSelectionMode.singleFile && fileRef.forceUniqueSaveNameJSB.val && FileManagerSecure.FileExists(fileName))
            {
                string newFileName = "";
                string fileNameWithoutExt = fileName.Substring(0, fileName.Length - 4);
                for (int i=1; i<10000; i++)
                {
                    newFileName = fileNameWithoutExt + i.ToString() + ".vap";
                    if (!FileManagerSecure.FileExists(newFileName))
                    {
                        fileName = newFileName;
                        break;
                    }
                }
                if (fileName != newFileName)
                {
                    loadOnSelectJSON.val = preState;
                    return;
                }
            }

            presetPathJSON.val = fileName;

            js.CallAction("StorePresetWithScreenshot");

            loadOnSelectJSON.val = preState;
        }
        private static HashSet<DAZClothingItem> LockNonRealClothing(Atom atom)
        {
            var lockedDCIs = new HashSet<DAZClothingItem>();
#if VAM_GT_1_21
            if (TargetControl.atomActiveClothingDictionary.ContainsKey(atom.name))
            {
                ActiveClothingList acl = TargetControl.atomActiveClothingDictionary[atom.name];
                foreach (var dci in acl._activeClothingDCIs)
                {
                    if (!dci.isRealItem && !dci.locked)
                    {
                        dci.SetLocked(true);
                        lockedDCIs.Add(dci);
                    }
                }
            }
            else
            {
                SuperController.LogError("UIA.AppearancePresetComponent.LockNonRealClothing: Unable to find active clothing list for Atom '" + atom.name + "'");
            }
#endif
            return lockedDCIs;
        }
        private static void UnlockNonRealClothing(HashSet<DAZClothingItem> lockedDCIs)
        {
#if VAM_GT_1_21
            foreach (var dci in lockedDCIs) dci.SetLocked(false);
#endif
        }

        private static void RemoveNonRealClothing(Atom atom)
        {
#if VAM_GT_1_21
            if (TargetControl.atomActiveClothingDictionary.ContainsKey(atom.name))
            {
                JSONStorable receiver = atom.GetStorableByID("geometry");

                ActiveClothingList acl = TargetControl.atomActiveClothingDictionary[atom.name];
                foreach (var dci in acl._activeClothingDCIs)
                {
                    if (!dci.isRealItem)
                    {                        
                        if (receiver != null)
                        {
                            JSONStorableBool active = receiver.GetBoolJSONParam("clothing:" + dci.uid);
                            if (active != null) active.val = false;
                        }
                    }
                }
            }
            else
            {
                SuperController.LogError("UIA.AppearancePresetComponent.RemoveNonRealClothing: Unable to find active clothing list for Atom '" + atom.name+"'");
            }
#endif
        }
        public static void RemoveRealClothing(Atom atom)
        {
#if VAM_GT_1_21
            if (TargetControl.atomActiveClothingDictionary.ContainsKey(atom.name))
            {
                JSONStorable receiver = atom.GetStorableByID("geometry");

                ActiveClothingList acl = TargetControl.atomActiveClothingDictionary[atom.name];
                foreach (var dci in acl._activeClothingDCIs)
                {
                    if (dci.isRealItem)
                    {
                        if (receiver != null)
                        {
                            JSONStorableBool active = receiver.GetBoolJSONParam("clothing:" + dci.uid);
                            if (active != null) active.val = false;
                        }
                    }
                }
            }
            else
            {
                SuperController.LogError("UIA.AppearancePresetComponent.RemoveRealClothing: Unable to find active clothing list for Atom '" + atom.name + "'");
            }
#endif
        }


        public static JSONClass GetNonRealClothingPreset(FileReference fileRef)
        {
            JSONClass nonRealClothingPresetJC = new JSONClass();
            nonRealClothingPresetJC["setUnlistedParamsToDefault"].AsBool = true;

            JSONArray nonRealClothingPresetStorablesJA = new JSONArray();
            nonRealClothingPresetJC["storables"] = nonRealClothingPresetStorablesJA;

            JSONClass nonRealClothingPresetGeometryJC = new JSONClass();
            nonRealClothingPresetGeometryJC["id"] = "geometry";
            JSONArray nonRealClothingPresetClothingGeometryJA = new JSONArray();
            nonRealClothingPresetGeometryJC["clothing"] = nonRealClothingPresetClothingGeometryJA;
            nonRealClothingPresetStorablesJA.Add(nonRealClothingPresetGeometryJC);

            JSONClass appPresetJC = (JSONClass)SuperController.singleton.LoadJSON(SuperController.singleton.NormalizePath(fileRef.currentActionSelectedFile));

            if (appPresetJC["storables"] == null) return nonRealClothingPresetJC;

            var appPresetStorableJCs = JSONUtils.GetJCStorablesByID(appPresetJC["storables"].AsArray);

            HashSet<string> enabledNonRealClothingGeometryIDs= new HashSet<string>();

            if (appPresetStorableJCs.ContainsKey("geometry"))
            {
                var geometryJC = appPresetStorableJCs["geometry"];
                if (geometryJC["clothing"] != null)
                {
                    JSONArray clothingGeometryJA = geometryJC["clothing"].AsArray;
                    foreach (JSONClass clothingGeometryItemJC in clothingGeometryJA)
                    {
                        string internalID ;
                        if (clothingGeometryItemJC["internalId"] != null) internalID = clothingGeometryItemJC["internalId"];
                        else internalID =  FileManagerSecure.GetFileName( clothingGeometryItemJC["id"]);

                        if (clothingGeometryItemJC["enabled"]==null || clothingGeometryItemJC["enabled"].AsBool)
                        {
                            string itemControlStorableID = internalID + "ItemControl";
                            if (appPresetStorableJCs.ContainsKey(itemControlStorableID) && appPresetStorableJCs[itemControlStorableID]["isRealClothingItem"]!=null)
                            {
                                bool isRealClothingItem = appPresetStorableJCs[itemControlStorableID]["isRealClothingItem"].AsBool;
                                if (!isRealClothingItem)
                                {
                                    enabledNonRealClothingGeometryIDs.Add(internalID);
                                    nonRealClothingPresetClothingGeometryJA.Add(clothingGeometryItemJC);
                                }
                            }
                            
                        }
                    }
                }
            }

            foreach (var kvp in appPresetStorableJCs)
            {
                foreach(string enabledNonRealClothingGeometryID in enabledNonRealClothingGeometryIDs)
                {
                    if (kvp.Key.StartsWith(enabledNonRealClothingGeometryID+ "WrapControl") || kvp.Key.StartsWith(enabledNonRealClothingGeometryID + "Sim") || kvp.Key.StartsWith(enabledNonRealClothingGeometryID + "ItemControl") || kvp.Key.StartsWith(enabledNonRealClothingGeometryID + "Material"))
                    {
                        nonRealClothingPresetStorablesJA.Add(kvp.Value);
                    }
                }
            }
            if (nonRealClothingPresetClothingGeometryJA.Count == 0) return null;

            return nonRealClothingPresetJC;
        }

        private static void LoadClothingPresetFromAppPreset(Atom atom, JSONStorable js, FileReference fileRef, bool mergeLoad)
        {

            string presetPackageName = "";
            string presetFilePath = fileRef.currentActionSelectedFile;

            if (presetFilePath.Contains(":")) presetPackageName = presetFilePath.Substring(0, presetFilePath.IndexOf(':'));

            JSONClass presetJSON = (JSONClass)SuperController.singleton.LoadJSON(SuperController.singleton.NormalizePath(presetFilePath));
            if (presetPackageName != "")
            {
                string presetJSONString = presetJSON.ToString();
                if (presetJSONString.Contains("SELF:"))
                {
                    presetJSONString = presetJSONString.Replace("SELF:", presetPackageName + ":");
                    presetJSON = (JSONClass)JSON.Parse(presetJSONString);
                }
            }

            PresetManager pm = js.GetComponentInChildren<PresetManager>();
            atom.SetLastRestoredData(presetJSON, true, true);
            pm.LoadPresetFromJSON(presetJSON, mergeLoad);
        }

        public static void LoadPreset(Atom atom, JSONStorable js, FileReference fileRef, bool suppressClothingLoad = false, bool suppressScaleLoad = false, bool suppressMegeLoad = false, bool onlySuppressRealClothing = true, bool onlyRemoveRealClothing = true)
        {
            if (fileRef.currentActionSelectedFile == "") return;
            PresetLockStore tempPresetLockStore = new PresetLockStore();
            if (atom.type == "Person") tempPresetLockStore.StorePresetLocks(atom, PresetLoadSettings.suppressPresetLocksJSB.val, suppressClothingLoad);

            JSONClass nonRealClothingPresetJC = null;

            if (js.name=="AppearancePresets" && suppressClothingLoad && onlySuppressRealClothing &&(!fileRef.mergeLoadPresetJSBool.val || suppressMegeLoad) && UIAGlobals.isRealClothingAvailable)
            {
                RemoveNonRealClothing(atom);
                nonRealClothingPresetJC = GetNonRealClothingPreset(fileRef);

                if (nonRealClothingPresetJC != null)
                {
                    var clothingPMC = atom.presetManagerControls.First(x => x.name == "ClothingPresets");
                    clothingPMC.lockParams = false;
                    JSONStorable presetJS = atom.GetStorableByID("ClothingPresets");
                    PresetManager pm = presetJS.GetComponentInChildren<PresetManager>();

                    atom.SetLastRestoredData(nonRealClothingPresetJC, true, true);
                    pm.LoadPresetFromJSON(nonRealClothingPresetJC, true);
                    clothingPMC.lockParams = suppressClothingLoad;
                }
            }

            HashSet<DAZClothingItem> lockedDCIs =null;
            if (js.name=="ClothingPresets" && fileRef.parentButtonOperation.buttonOpTypeJSEnum.val ==UIAButtonOpType.loadClothPreset && !fileRef.mergeLoadPresetJSBool.val && onlyRemoveRealClothing && UIAGlobals.isRealClothingAvailable)
            {
                lockedDCIs = LockNonRealClothing(atom);
            }

            if (js.name == "AppearancePresets" && suppressScaleLoad && PatreonFeatures.patreonContentEnabled) PatreonFeatures.AppPresetSuppressScaleLoad(atom, fileRef, fileRef.mergeLoadPresetJSBool.val && !suppressMegeLoad);
            else if (js.name == "ClothingPresets" && fileRef.parentButtonOperation.buttonOpTypeJSEnum.val == UIAButtonOpType.loadAppPreset) LoadClothingPresetFromAppPreset(atom, js, fileRef, fileRef.mergeLoadPresetJSBool.val && !suppressMegeLoad);
            else
            {
                JSONStorableBool loadOnSelectJSON = js.GetBoolJSONParam("loadPresetOnSelect");
                bool preState = loadOnSelectJSON.val;
                loadOnSelectJSON.val = false;
                bool preState1 = false;
                bool preState2 = false;
                bool preState3 = false;
                if (js.name == "PosePresets")
                {
                    JSONStorable snapStorable = atom.GetStorableByID("CharacterPoseSnapRestore");
                    JSONStorableBool snapEnabled = snapStorable.GetBoolJSONParam("enabled");
                    preState1 = snapEnabled.val;
                    snapEnabled.val = fileRef.posePresetSnapBoneToPoseJSB.val;
                }
                if (js.name == "MorphPresets")
                {
                    JSONStorableBool includePhysical = js.GetBoolJSONParam("includePhysical");
                    preState1 = includePhysical.val;
                    includePhysical.val = fileRef.morphPresetIncludePhysicalJSB.val;
                    JSONStorableBool includeAppearance = js.GetBoolJSONParam("includeAppearance");
                    preState2 = includeAppearance.val;
                    includeAppearance.val = fileRef.morphPresetIncludeAppJSB.val;
                }
                if (js.name == "geometry")
                {
                    JSONStorableBool includePhysical = js.GetBoolJSONParam("includePhysical");
                    preState1 = includePhysical.val;
                    includePhysical.val = fileRef.generalPresetIncludePhysicalJSB.val;

                    JSONStorableBool includeAppearance = js.GetBoolJSONParam("includeAppearance");
                    preState2 = includeAppearance.val;
                    includeAppearance.val = fileRef.generalPresetIncludeAppJSB.val;

                    JSONStorableBool includePose = js.GetBoolJSONParam("includeOptional");
                    preState3 = includePose.val;
                    includePose.val = fileRef.generalPresetIncludePoseJSB.val;
                }

                JSONStorableUrl presetPathJSON = js.GetUrlJSONParam("presetBrowsePath");
                string pathPreState = presetPathJSON.val;
                presetPathJSON.val = SuperController.singleton.NormalizePath(fileRef.currentActionSelectedFile);

                if (fileRef.mergeLoadPresetJSBool.val && !suppressMegeLoad) js.CallAction("MergeLoadPreset");
                else js.CallAction("LoadPreset");

                fileRef.Reset();

                presetPathJSON.val = pathPreState;
                loadOnSelectJSON.val = preState;

                if (js.name == "PosePresets")
                {
                    JSONStorable snapStorable = atom.GetStorableByID("CharacterPoseSnapRestore");
                    JSONStorableBool snapEnabled = snapStorable.GetBoolJSONParam("enabled");
                    snapEnabled.val = preState1;
                }
                if (js.name == "MorphPresets")
                {
                    JSONStorableBool includePhysical = js.GetBoolJSONParam("includePhysical");
                    includePhysical.val = preState1;
                    JSONStorableBool includeAppearance = js.GetBoolJSONParam("includeAppearance");
                    includeAppearance.val = preState2;
                }
                if (js.name == "GeneralPresets")
                {
                    JSONStorableBool includePhysical = js.GetBoolJSONParam("includePhysical");
                    includePhysical.val = preState1;

                    JSONStorableBool includeAppearance = js.GetBoolJSONParam("includeAppearance");
                    includeAppearance.val = preState2;

                    JSONStorableBool includePose = js.GetBoolJSONParam("includeOptional");
                    includePose.val = preState3;
                }
            }

            if (js.name=="PosePresets")
            {
                JSONStorable geometryReceiver = atom.GetStorableByID("geometry");
                JSONStorableBool harliHeelsEnabled = geometryReceiver.GetBoolJSONParam("clothing:Harli Heels");
                JSONStorableBool casualDenimHeelsEnabled = geometryReceiver.GetBoolJSONParam("clothing:Casual Denim Shoes");
                if (harliHeelsEnabled != null && harliHeelsEnabled.val)
                {
                    harliHeelsEnabled.val = false;
                    harliHeelsEnabled.val = true;
                }
                if (casualDenimHeelsEnabled != null && casualDenimHeelsEnabled.val)
                {
                    casualDenimHeelsEnabled.val = false;
                    casualDenimHeelsEnabled.val = true;
                }
            }

            if (lockedDCIs != null) UnlockNonRealClothing(lockedDCIs);

            if (atom.type == "Person") tempPresetLockStore.RestorePresetLocks(atom);
            if (js.name == "ClothingPresets" || js.name == "Preset" || (js.name == "AppearancePresets" && !suppressClothingLoad)) GridsDisplay._uiActiveClothingEditor.RefreshClothing(atom.name);
        }

        public void ResetAppearance(Atom atom, bool resetScale, bool appearanceReset, bool poseReset)
        {
            JSONArray atomsArray = null;
            if (SuperController.singleton.loadJson != null) atomsArray = SuperController.singleton.loadJson["atoms"].AsArray;
            bool atomExistedAtLoad = false;
            bool varReferenceAvailable = true;
            string varPackageName = "";
            if (lastLoadSceneFromVar.Contains(":/Saves/scene")) varPackageName = lastLoadSceneFromVar.Substring(0, lastLoadSceneFromVar.IndexOf(':'));

            JSONArray newStorablesJSON = new JSONArray();
            JSONClass atomAtLoadJSON = null;
            if (atomsArray != null)
            {
                foreach (JSONClass atomJSON in atomsArray)
                {
                    if ((string)atomJSON["id"] == atom.name && (string)atomJSON["type"] == atom.type)
                    {
                        if (atomJSON["storables"] != null)
                        {
                            atomExistedAtLoad = true;
                            atomAtLoadJSON = atomJSON;

                            JSONArray storablesJSON = atomJSON["storables"].AsArray;
                            

                            foreach (JSONClass storableJSON in storablesJSON)
                            {
                                if (!((string)storableJSON["id"]).EndsWith("Animation"))
                                {
                                    string storableString = storableJSON.ToString();
                                    
                                    if (storableString.Contains("SELF:"))
                                    {
                                        if (lastLoadSceneFromVar.Contains(":/Saves/scene")) newStorablesJSON.Add((JSONClass)JSON.Parse(storableString.Replace("SELF", varPackageName)));
                                        else varReferenceAvailable = false;
                                    }
                                    else newStorablesJSON.Add(storableJSON);
                                }
                                else newStorablesJSON.Add(storableJSON);
                            }
                        }
                        break;
                    }
                }
            }

            if (resetScale)
            {

                JSONStorable scaleStorableJSON = atom.GetStorableByID("rescaleObject");
                JSONStorableFloat scaleJSON = scaleStorableJSON.GetFloatJSONParam("scale");
                if (atomExistedAtLoad)
                {
                    float resetScaleVal = 1f;
                    foreach (JSONClass storable in atomAtLoadJSON["storables"].AsArray)
                    {
                        if (storable["id"].Value == "rescaleObject")
                        {
                            resetScaleVal = storable["scale"].AsFloat;
                            break;
                        }
                    }
                    scaleJSON.val = resetScaleVal;
                }
                else { scaleJSON.val = 1f; }
            }

            if (appearanceReset || poseReset)
            {
                if (atomExistedAtLoad && varReferenceAvailable)
                {
                    JSONClass newSave = new JSONClass();
                    JSONArray newAtomsArray = new JSONArray();
                    newSave["atoms"] = newAtomsArray;
                    JSONClass newAtom = new JSONClass();
                    newAtomsArray.Add(newAtom);

                    newAtom["id"] = atom.name;
                    newAtom["type"] = atom.type;
                    newAtom["storables"] = newStorablesJSON;

                    PresetLockStore tempPresetLockStore1 = new PresetLockStore();
                    if (atom.type == "Person") tempPresetLockStore1.StorePresetLocks(atom, true, false);

                    if (FileUtils.CreatePluginDataFolder())
                    {
                        SuperController.singleton.SaveJSON(newSave, UIAConsts._PluginDataSubfolderName + "\\UIAtemp.json");
                        
                        if (appearanceReset) atom.LoadAppearancePreset(UIAConsts._PluginDataSubfolderName + "\\UIAtemp.json");
                        if (poseReset) atom.LoadPhysicalPreset(UIAConsts._PluginDataSubfolderName + "\\UIAtemp.json");
                        FileManagerSecure.DeleteFile(UIAConsts._PluginDataSubfolderName + "\\UIAtemp.json");
                    }

                    if (atom.type == "Person") tempPresetLockStore1.RestorePresetLocks(atom);
                }
                if (!atomExistedAtLoad)
                {
                    PresetLockStore tempPresetLockStore1 = new PresetLockStore();
                    if (atom.type == "Person") tempPresetLockStore1.StorePresetLocks(atom, true, false);

                    if (appearanceReset) atom.ResetAppearance();
                    if (poseReset) atom.ResetPhysical();

                    if (atom.type == "Person") tempPresetLockStore1.RestorePresetLocks(atom);
                }
            }
            
        }


    }

    public class ClothingComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public JSONStorableBool accessoryTagRemoveJSBool;
        public JSONStorableBool bodysuitTagRemoveJSBool;
        public JSONStorableBool bottomTagRemoveJSBool;
        public JSONStorableBool braTagRemoveJSBool;
        public JSONStorableBool dressTagRemoveJSBool;
        public JSONStorableBool glassesTagRemoveJSBool;
        public JSONStorableBool glovesTagRemoveJSBool;
        public JSONStorableBool hatTagRemoveJSBool;
        public JSONStorableBool jewelryTagRemoveJSBool;
        public JSONStorableBool maskTagRemoveJSBool;
        public JSONStorableBool pantiesTagRemoveJSBool;
        public JSONStorableBool pantsTagRemoveJSBool;
        public JSONStorableBool shirtTagRemoveJSBool;
        public JSONStorableBool shoesTagRemoveJSBool;
        public JSONStorableBool shortsTagRemoveJSBool;
        public JSONStorableBool skirtTagRemoveJSBool;
        public JSONStorableBool socksTagRemoveJSBool;
        public JSONStorableBool stockingsTagRemoveJSBool;
        public JSONStorableBool sweaterTagRemoveJSBool;
        public JSONStorableBool topTagRemoveJSBool;
        public JSONStorableBool underwearTagRemoveJSBool;

        public JSONStorableBool armsTagRemoveJSBool;
        public JSONStorableBool feetTagRemoveJSBool;
        public JSONStorableBool fullBodyTagRemoveJSBool;
        public JSONStorableBool handsTagRemoveJSBool;
        public JSONStorableBool headTagRemoveJSBool;
        public JSONStorableBool hipTagRemoveJSBool;
        public JSONStorableBool legsTagRemoveJSBool;
        public JSONStorableBool neckTagRemoveJSBool;
        public JSONStorableBool torsoTagRemoveJSBool;

        public JSONStorableBool onlyRemoveRealClothingJSB;

        public JSONStorableString userTagListJSS;

        public JSONStorableEnumStringChooser clothingGenderJSE;

        public List<string> tagsToRemoveList
        {
            get
            {
                List<string> tags = new List<string>();
                foreach (JSONStorableParam jsp in GetParamList())
                {
                    if (jsp.name.EndsWith("Tag"))
                    {
                        JSONStorableBool tagRemoveJSB = jsp as JSONStorableBool;
                        if (tagRemoveJSB.val) tags.Add(jsp.name.Substring(0, jsp.name.Length - 3));
                    }
                }

                var userTags = userTagListJSS.val.Split(',').ToList();
                foreach(var userTag in userTags) tags.Add(userTag.Trim().ToLower());

                return tags;
            }
        }

        public List<JSONStorableBool> GetRegionTagBools()
        {
            List<JSONStorableBool> tags = new List<JSONStorableBool>();
            foreach (JSONStorableParam jsp in GetParamList())
            {
                if (jsp.name.EndsWith("Tag"))
                {
                    JSONStorableBool tagRemoveJSB = jsp as JSONStorableBool;
                    if (tagRemoveJSB.name=="armsTag" || tagRemoveJSB.name == "handsTag" ||tagRemoveJSB.name == "legsTag" || tagRemoveJSB.name == "feetTag" || tagRemoveJSB.name == "headTag" || tagRemoveJSB.name == "neckTag" || tagRemoveJSB.name == "fullbodyTag" || tagRemoveJSB.name == "hipTag" || tagRemoveJSB.name == "torsoTag") tags.Add(tagRemoveJSB);
                }
            }
            return tags;
        }

        public List<JSONStorableBool> GetTypeTagBools()
        {
            List<JSONStorableBool> tags = new List<JSONStorableBool>();
            foreach (JSONStorableParam jsp in GetParamList())
            {
                if (jsp.name.EndsWith("Tag"))
                {
                    JSONStorableBool tagRemoveJSB = jsp as JSONStorableBool;
                    if (tagRemoveJSB.name == "armsTag" || tagRemoveJSB.name == "handsTag" || tagRemoveJSB.name == "legsTag" || tagRemoveJSB.name == "feetTag" || tagRemoveJSB.name == "headTag" || tagRemoveJSB.name == "neckTag" || tagRemoveJSB.name == "fullbodyTag" || tagRemoveJSB.name == "hipTag" || tagRemoveJSB.name == "torsoTag") break;
                    else tags.Add(tagRemoveJSB);
                }
            }
            return tags;
        }

        public ClothingComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;

            accessoryTagRemoveJSBool = new JSONStorableBool("accessoryTag", false);
            RegisterParam(accessoryTagRemoveJSBool);
            bodysuitTagRemoveJSBool = new JSONStorableBool("bodysuitTag", false);
            RegisterParam(bodysuitTagRemoveJSBool);
            bottomTagRemoveJSBool = new JSONStorableBool("bottomTag", false);
            RegisterParam(bottomTagRemoveJSBool);
            braTagRemoveJSBool = new JSONStorableBool("braTag", false);
            RegisterParam(braTagRemoveJSBool);
            dressTagRemoveJSBool = new JSONStorableBool("dressTag", false);
            RegisterParam(dressTagRemoveJSBool);
            glassesTagRemoveJSBool = new JSONStorableBool("glassesTag", false);
            RegisterParam(glassesTagRemoveJSBool);
            glovesTagRemoveJSBool = new JSONStorableBool("glovesTag", false);
            RegisterParam(glovesTagRemoveJSBool);
            hatTagRemoveJSBool = new JSONStorableBool("hatTag", false);
            RegisterParam(hatTagRemoveJSBool);
            jewelryTagRemoveJSBool = new JSONStorableBool("jewelryTag", false);
            RegisterParam(jewelryTagRemoveJSBool);
            maskTagRemoveJSBool = new JSONStorableBool("maskTag", false);
            RegisterParam(maskTagRemoveJSBool);
            pantiesTagRemoveJSBool = new JSONStorableBool("pantiesTag", false);
            RegisterParam(pantiesTagRemoveJSBool);
            pantsTagRemoveJSBool = new JSONStorableBool("pantsTag", false);
            RegisterParam(pantsTagRemoveJSBool);
            shirtTagRemoveJSBool = new JSONStorableBool("shirtTag", false);
            RegisterParam(shirtTagRemoveJSBool);
            shoesTagRemoveJSBool = new JSONStorableBool("shoesTag", false);
            RegisterParam(shoesTagRemoveJSBool);
            shortsTagRemoveJSBool = new JSONStorableBool("shortsTag", false);
            RegisterParam(shortsTagRemoveJSBool);
            skirtTagRemoveJSBool = new JSONStorableBool("skirtTag", false);
            RegisterParam(skirtTagRemoveJSBool);
            socksTagRemoveJSBool = new JSONStorableBool("socksTag", false);
            RegisterParam(socksTagRemoveJSBool);
            stockingsTagRemoveJSBool = new JSONStorableBool("stockingsTag", false);
            RegisterParam(stockingsTagRemoveJSBool);
            sweaterTagRemoveJSBool = new JSONStorableBool("sweaterTag", false);
            RegisterParam(sweaterTagRemoveJSBool);
            topTagRemoveJSBool = new JSONStorableBool("topTag", false);
            RegisterParam(topTagRemoveJSBool);
            underwearTagRemoveJSBool = new JSONStorableBool("underwearTag", false);
            RegisterParam(underwearTagRemoveJSBool);

            armsTagRemoveJSBool = new JSONStorableBool("armsTag", false);
            RegisterParam(armsTagRemoveJSBool);
            feetTagRemoveJSBool = new JSONStorableBool("feetTag", false);
            RegisterParam(feetTagRemoveJSBool);
            fullBodyTagRemoveJSBool = new JSONStorableBool("fullbodyTag", false);
            RegisterParam(fullBodyTagRemoveJSBool);
            handsTagRemoveJSBool = new JSONStorableBool("handsTag", false);
            RegisterParam(handsTagRemoveJSBool);
            headTagRemoveJSBool = new JSONStorableBool("headTag", false);
            RegisterParam(headTagRemoveJSBool);
            hipTagRemoveJSBool = new JSONStorableBool("hipTag", false);
            RegisterParam(hipTagRemoveJSBool);
            legsTagRemoveJSBool = new JSONStorableBool("legsTag", false);
            RegisterParam(legsTagRemoveJSBool);
            neckTagRemoveJSBool = new JSONStorableBool("neckTag", false);
            RegisterParam(neckTagRemoveJSBool);
            torsoTagRemoveJSBool = new JSONStorableBool("torsoTag", false);
            RegisterParam(torsoTagRemoveJSBool);

            userTagListJSS = new JSONStorableString("userTagList", "");
            RegisterParam(userTagListJSS);

            onlyRemoveRealClothingJSB = new JSONStorableBool("onlyRemoveRealClothing", true);
            RegisterParam(onlyRemoveRealClothingJSB);

            clothingGenderJSE = new JSONStorableEnumStringChooser("clothingGender", ClothingGenderTypes.enumManifestName, ClothingGenderTypes.femaleClothing, "Clothing Gender",callback: delegate (int val) { parentButtonOperation.RefreshFileRefTypes(); });
            RegisterParam(clothingGenderJSE);
        }


        public void LoadJSON(JSONClass clothingPresetComponentJSON, int uiapVersion)
        {
            base.RestoreFromJSON(clothingPresetComponentJSON);
            if (uiapVersion == 1 && clothingPresetComponentJSON["tagsToRemove"] != null)
            {
                JSONArray tagsToRemoveArray = clothingPresetComponentJSON["tagsToRemove"].AsArray;
                for (int i = 0; i < tagsToRemoveArray.Count; i++)
                {
                    string tagToRemove = tagsToRemoveArray[i].Value;
                    JSONStorableBool tagJSBool = GetJSONParam(tagToRemove.ToLower() + "Tag") as JSONStorableBool;
                    if (tagJSBool != null) tagJSBool.val = true;
                }
            }

        }

        public static void LockActiveClothing(Atom atom)
        {
            if (!ActiveClothingList.atomActiveClothingDictionary.ContainsKey(atom.name)) return;
            else
            {
                var acl = ActiveClothingList.atomActiveClothingDictionary[atom.name];

                foreach (var dci in acl._activeClothingDCIs) dci.SetLocked(true);
            }
        }

        public static void UnlockAllClothing(Atom atom)
        {
            if (!ActiveClothingList.atomActiveClothingDictionary.ContainsKey(atom.name)) return;
            else
            {
                var acl = ActiveClothingList.atomActiveClothingDictionary[atom.name];

                foreach (var dci in acl._activeClothingDCIs) dci.SetLocked(false);
            }
        }

        public static void ClothingActions(Atom atom, int mode, bool reverse, bool onlyRemoveRealClothing=true)
        {
            foreach (string receiverName in atom.GetStorableIDs())
            {
                JSONStorable receiver = atom.GetStorableByID(receiverName);

                if (receiver != null)
                {
                    if (receiver.storeId.Length >= 3 && receiver.storeId.Substring(receiver.storeId.Length - 3) == "Sim")
                    {
                        JSONStorableBool unDressBool = receiver.GetBoolJSONParam("allowDetach");
                        JSONStorableBool simEnabledBool = receiver.GetBoolJSONParam("simEnabled");

                        if (mode == ClothingActionMode.undress && unDressBool != null) { unDressBool.val = !reverse; }
                        if (mode == ClothingActionMode.resetSim && simEnabledBool != null) { if (simEnabledBool.val) { receiver.CallAction("Reset"); } }
                    }

                    
                    if (receiver.storeId == "geometry" && mode == ClothingActionMode.remove)
                    {
                        if (onlyRemoveRealClothing && UIAGlobals.isRealClothingAvailable) AppearancePresetComponent.RemoveRealClothing(atom);
                        else {
                            foreach (string target in receiver.GetBoolParamNames())
                            {
                                if (target.Length > 8 && target.Substring(0, 9) == "clothing:")
                                {
                                    JSONStorableBool boolTarget = receiver.GetBoolJSONParam(target);
                                    if (boolTarget != null) { boolTarget.val = false; }
                                }
                            }
                        }
                     
                    }
                }
            }
        }

        public void RemoveClothingByTag(Atom atom)
        {
            if (PatreonFeatures.patreonContentEnabled)
            {
                if (tagsToRemoveList.Count > 0 && TargetControl.atomActiveClothingDictionary.ContainsKey(atom.name))
                {
                    PatreonFeatures.RemoveTagClothingItems(atom, TargetControl.atomActiveClothingDictionary[atom.name]._activeClothingTags, tagsToRemoveList);
                }
            }
                
        }

        public void ClothingPresetActions(Atom atom, int actionMode, bool reverse, FileReference fileRef, TargetComponent targetComponent)
        {
            if (PatreonFeatures.patreonContentEnabled)
            {
                if (actionMode == ClothingActionMode.merge && tagsToRemoveList.Count > 0 && TargetControl.atomActiveClothingDictionary.ContainsKey(atom.name))
                {
                    PatreonFeatures.RemoveTagClothingItems(atom, TargetControl.atomActiveClothingDictionary[atom.name]._activeClothingTags, tagsToRemoveList);
                }
                else if (actionMode == ClothingActionMode.merge)
                {
                    if (!UIAButton.presetMergeClothingGeometryIDs.ContainsKey(fileRef.currentActionSelectedFile)) UIAButton.presetMergeClothingGeometryIDs.Add(fileRef.currentActionSelectedFile, null);
                    else UIAButton.presetMergeClothingGeometryIDs[fileRef.currentActionSelectedFile] = null;
                }

                if (actionMode == ClothingActionMode.merge) UIAButton.cachedClothingPresetFiles.Remove(fileRef.currentActionSelectedFile);

                if (FileManagerSecure.FileExists(fileRef.currentActionSelectedFile))
                {
                    PatreonFeatures.ClothingPresetActions(atom, actionMode, fileRef.currentActionSelectedFile, reverse);

                    if (reverse) targetComponent.ResetLastUserChosenAtom();

                }

            }
        }

        public int GetUndressAllClothingButtonState(Atom atom)
        {           
            int clothingItemCount = 0;
            foreach (string receiverName in atom.GetStorableIDs())
            {
                JSONStorable receiver = atom.GetStorableByID(receiverName);
                if (receiver != null)
                {
                    if (receiver.storeId.Length >= 3 && receiver.storeId.Substring(receiver.storeId.Length - 3) == "Sim")
                    {
                        JSONStorableBool unDressBool = receiver.GetBoolJSONParam("allowDetach");
                        if (unDressBool != null)
                        {
                            if (!unDressBool.val) return ButtonState.inactive;
                            clothingItemCount++;
                        }
                        
                    }
                }
            }
            if (clothingItemCount > 0) return ButtonState.active;
            return ButtonState.inactive;
        }
    }

    public class VAMPlayEditModeComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public JSONStorableBool closeGameUIonPlayModeJSBool;
        public JSONStorableBool openGameUIonEditModeJSBool;

        public VAMPlayEditModeComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;

            closeGameUIonPlayModeJSBool = new JSONStorableBool("closeGameUIonPlayMode", false);
            RegisterParam(closeGameUIonPlayModeJSBool);
            openGameUIonEditModeJSBool = new JSONStorableBool("openGameUIonEditMode", false);
            RegisterParam(openGameUIonEditModeJSBool);
        }
    }

    public class SpawnAtomComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public JSONStorableEnumStringChooser atomCategoryJSEnum;
        public JSONStorableEnumStringChooser atomTypeJSEnum;
        public JSONStorableBool parentLinkToTargetJSBool;
        public JSONStorableBool atomSpawnSpecifyNameJSBool;
        public JSONStorableString atomSpawnNameJSString;
        public JSONStorableBool selectAtomOnSpawnJSB;
        public JSONStorableBool onlyGeneralPresetJSB;

        public static bool atomSpawningActive = false;

        private RelativePositionComponent relativePositionComponent { get { return parentButtonOperation.relativePositionComponent; } }

        public SpawnAtomComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;

            atomCategoryJSEnum = new JSONStorableEnumStringChooser("atomCategory", AtomCategories.enumManifestName, AtomCategories.people, "", AtomCategoryUpdated);
            RegisterParam(atomCategoryJSEnum);
            atomTypeJSEnum = new JSONStorableEnumStringChooser("atomType", AtomTypes.enumManifestName, AtomTypes.person, "", AtomTypeUpdated, AtomTypes.GetAtomTypesExcluding(atomCategoryJSEnum.val));
            RegisterParam(atomTypeJSEnum);
            parentLinkToTargetJSBool = new JSONStorableBool("parentLinkToTarget", false);
            RegisterParam(parentLinkToTargetJSBool);
            atomSpawnSpecifyNameJSBool = new JSONStorableBool("atomSpawnSpecifyName", false);
            RegisterParam(atomSpawnSpecifyNameJSBool);
            atomSpawnNameJSString = new JSONStorableString("atomSpawnName", "");
            RegisterParam(atomSpawnNameJSString);
            selectAtomOnSpawnJSB = new JSONStorableBool("selectAtomOnSpawn", false);
            RegisterParam(selectAtomOnSpawnJSB);
            onlyGeneralPresetJSB = new JSONStorableBool("onlyGeneralPreset", false,OnlyGeneralPresetUpdated);
            RegisterParam(onlyGeneralPresetJSB);
        }

        protected void AtomCategoryUpdated(int atomCat)
        {
            atomTypeJSEnum.SetEnumChoices(AtomTypes.enumManifestName, AtomTypes.GetAtomTypesExcluding(atomCat));
        }
        protected void AtomTypeUpdated(int atomType)
        {
            parentButtonOperation.RefreshFileRefTypes();
        }

        protected void OnlyGeneralPresetUpdated(bool onlyGeneralPreset)
        {
            parentButtonOperation.RefreshFileRefTypes();
        }

        public void SpawnAtomAction()
        {
            atomSpawningActive = true;

            string atomSpecifiedName = "";
            if (atomSpawnSpecifyNameJSBool.val) atomSpecifiedName = atomSpawnNameJSString.val;
            string atomType = atomTypeJSEnum.displayVal;
            if (buttonTypeJSEnum.val == UIAButtonOpType.loadGeneralPreset)
            {
                atomType = parentButtonOperation.targetComponent.specificAtomTypeJSS.val;
                atomSpecifiedName = parentButtonOperation.targetComponent.targetNameJSMultiEnum.displayVal;
            }

            if (atomType != "")
            {
                UIAGlobals.mvrScript.StartCoroutine(CreateAtom(atomType, atomSpecifiedName, (atomName) =>
                {
                    try
                    {                       
                        relativePositionComponent.TeleportAtomToSpawnPoint(atomName);

                        UIAGlobals.mvrScript.StartCoroutine(RestoreAtomPreset(atomName));

                    }
                    catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
                }));
            }
            else SuperController.LogMessage("UIAssist.SpawnAtomComponent.SpawnAtomAction: The atom type to be spawned cant be identified. If this is General Preset Button Operation being performed on a missing Target Atom, then try resetting the button target to an exising atom of the correct type, then delete the atom again.");
        }

        private IEnumerator CreateAtom(string atomType, string atomName, Action<string> onAtomCreated)
        {
            string newAtomName = atomName;
            if (atomName == "") newAtomName = atomType;

            newAtomName = AtomUtils.GetNextAvailableAtomName(newAtomName);

            UIAGlobals.uiaAtomSpawnInProgress = true;
            yield return SuperController.singleton.AddAtomByType(atomType, newAtomName);
            UIAGlobals.uiaAtomSpawnInProgress = false;


            onAtomCreated(newAtomName);
        }
        private IEnumerator RestoreAtomPreset(string atomName)
        {
            yield return new WaitForEndOfFrame();

            try
            {
                Atom atom = SuperController.singleton.GetAtomByUid(atomName);

                if (selectAtomOnSpawnJSB.val) {
                    FreeControllerV3 fcV3 = atom.freeControllers.First(fc => fc.name == "control");
                    SuperController.singleton.SelectController(fcV3);
                }
                if (parentLinkToTargetJSBool.val && (relativePositionComponent.atomPositionRelativeToJSEnum.val == RelativePositionMode.lastViewedFemale || relativePositionComponent.atomPositionRelativeToJSEnum.val == RelativePositionMode.lastViewedMale || relativePositionComponent.atomPositionRelativeToJSEnum.val == RelativePositionMode.lastViewedPerson))
                {
                    string targetAtomName = relativePositionComponent.GetRelativePositionAtomName();
                    if (targetAtomName != "")
                    {
                        string targetNode = relativePositionComponent.targetNodeJSSC.val;
                        if (targetNode == "Lips" || targetNode == "Mouth") targetNode = "head";
                        else if (targetNode == "Labia" || targetNode == "Vagina") targetNode = "pelvis";
                        Atom targetAtom = SuperController.singleton.GetAtomByUid(targetAtomName);
                        atom.mainController.SetLinkToAtom(targetAtomName);
                        Rigidbody linkRB = targetAtom.rigidbodies.First(rb => rb.name == targetNode);
                        atom.mainController.linkToRB = linkRB;
                        atom.mainController.currentPositionState = FreeControllerV3.PositionState.ParentLink;
                        atom.mainController.currentRotationState = FreeControllerV3.RotationState.ParentLink;
                    }
                }
                if (atomTypeJSEnum.val == AtomTypes.person && buttonTypeJSEnum.val != UIAButtonOpType.loadGeneralPreset && (buttonTypeJSEnum.val != UIAButtonOpType.spawnAtom || !onlyGeneralPresetJSB.val))
                {
                    FileReference posePresetFileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.posePreset];

                    if (posePresetFileRef.fileSelectionModeJSEnum.val != FileSelectionMode.none)
                    {
                        JSONStorable js = atom.GetStorableByID("PosePresets");
                        AppearancePresetComponent.LoadPreset(atom, js, posePresetFileRef, false, false,true,false);
                    }
                }
                else if (atomTypeJSEnum.val != AtomTypes.subScene)
                {
                    FileReference generalPresetFileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.generalPreset];

                    if (generalPresetFileRef.fileSelectionModeJSEnum.val != FileSelectionMode.none)
                    {
                        JSONStorable js = atom.GetStorableByID("Preset");
                        AppearancePresetComponent.LoadPreset(atom, js, generalPresetFileRef, false, false,true);
                    }

                }
                if (parentButtonOperation.pluginsLoadComponent != null)  parentButtonOperation.pluginsLoadComponent.LoadPluginsAsPreset(atom);
                if (buttonTypeJSEnum.val != UIAButtonOpType.loadGeneralPreset && atomSpawnSpecifyNameJSBool.val)
                {
                    atom.SetUID(atomSpawnNameJSString.val);
                    atomName = atom.uid;
                }
                else if (buttonTypeJSEnum.val == UIAButtonOpType.loadGeneralPreset)
                {
                    
                    atom.SetUID(parentButtonOperation.targetComponent.targetNameJSMultiEnum.mainVal);
                    atomName = atom.uid;
                }
                atomSpawningActive = false;
                SuperController.singleton.helpHUDText.text = "";

            }
            catch (Exception e) { SuperController.LogError("UIA.SpawnAtomComponent.RestoreAtomPreset exception caught pre appearance load: " + e); }
            yield return new WaitForFixedUpdate();
            try
            {
                Atom atom1 = SuperController.singleton.GetAtomByUid(atomName);

                if (buttonTypeJSEnum.val != UIAButtonOpType.loadGeneralPreset && atomTypeJSEnum.val == AtomTypes.person && (buttonTypeJSEnum.val != UIAButtonOpType.spawnAtom || !onlyGeneralPresetJSB.val))
                {
                    FileReference appearancePresetFileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.appearancePreset];
                    if (appearancePresetFileRef.fileSelectionModeJSEnum.val != FileSelectionMode.none)
                    {
                        JSONStorable js1 = atom1.GetStorableByID("AppearancePresets");
                        AppearancePresetComponent.LoadPreset(atom1, js1, appearancePresetFileRef, false, false, true, true);
                        //                      AppearancePresetComponent.LoadPreset(atom1, js1, appearancePresetFileRef, false, false, true);
                    }

                    FileReference pluginPresetFileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.pluginsPreset];
                    if (pluginPresetFileRef.fileSelectionModeJSEnum.val != FileSelectionMode.none)
                    {
                        JSONStorable js2 = atom1.GetStorableByID("PluginPresets");
                        AppearancePresetComponent.LoadPreset(atom1, js2, pluginPresetFileRef, false, false);
                    }

                    
                }
                else if (buttonTypeJSEnum.val != UIAButtonOpType.loadGeneralPreset && atomTypeJSEnum.val == AtomTypes.subScene)
                {
                    JSONStorable js1 = atom1.GetStorableByID("SubScene");
                    JSONStorableUrl subScenePathJSON = js1.GetUrlJSONParam("browsePath");

                    string subScenePreset = parentButtonOperation.fileReferenceDict[FileReferenceTypes.subScene].currentActionSelectedFile;

                    if (subScenePreset != "" && FileManagerSecure.FileExists(SuperController.singleton.NormalizePath(subScenePreset)))
                    {
                        if (subScenePathJSON.val != SuperController.singleton.NormalizePath(subScenePreset)) subScenePathJSON.val = SuperController.singleton.NormalizePath(subScenePreset);
                        else js1.CallAction("LoadSubScene");
                    }
                    atom1.SetUID(atomName);
                }

                if (buttonTypeJSEnum.val == UIAButtonOpType.spawnAtom && atomTypeJSEnum.val == AtomTypes.cua)
                {
                    JSONStorable js1 = atom1.GetStorableByID("asset");
                    string cuaFilePath = parentButtonOperation.fileReferenceDict[FileReferenceTypes.cua].currentActionSelectedFile;

                    if (!string.IsNullOrEmpty(cuaFilePath))
                    {
                        string cuaNormalizedPath = SuperController.singleton.NormalizePath(cuaFilePath);

                        if (cuaNormalizedPath != "" && FileManagerSecure.FileExists(cuaNormalizedPath))
                        {
                            UIAGlobals.mvrScript.StartCoroutine(LoadCUA(js1, cuaNormalizedPath));
                        }
                    }                        
                }
            }
            catch (Exception e) { SuperController.LogError("UIA.SpawnAtomComponent.RestoreAtomPreset exception caught post appearance load: " + e); }
        }

        private IEnumerator LoadCUA(JSONStorable assetJS, string path)
        {
            JSONStorableUrl subScenePathJSUrl = assetJS.GetUrlJSONParam("assetUrl");
            JSONStorableStringChooser assetNameJSSC = assetJS.GetStringChooserJSONParam("assetName");

            subScenePathJSUrl.val = path;

            while (assetNameJSSC.choices.Count<2) yield return new WaitForEndOfFrame() ;

            assetNameJSSC.val = assetNameJSSC.choices[1];
        }

    }

    public class RelativePositionComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public JSONStorableEnumStringChooser atomPositionRelativeToJSEnum;
        public JSONStorableStringChooser targetNodeJSSC;

        public JSONStorableFloat atomPositionXJSFloat;
        public JSONStorableFloat atomPositionYJSFloat;
        public JSONStorableFloat atomPositionZJSFloat;
        public JSONStorableFloat atomRotationXJSFloat;
        public JSONStorableFloat atomRotationYJSFloat;
        public JSONStorableFloat atomRotationZJSFloat;

        public JSONStorableBool atomAbsoluteHeightJSBool;
        public JSONStorableBool atomAbsolutePitchJSBool;
        public JSONStorableBool atomAbsoluteRollJSBool;


        public RelativePositionComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;

            atomPositionRelativeToJSEnum = new JSONStorableEnumStringChooser("atomPositionRelativeTo", RelativePositionMode.enumManifestName, RelativePositionMode.sceneOrigin, "", GetPositionRelativeToExclusions());
            targetNodeJSSC = new JSONStorableStringChooser("targetNode", GetPersonNodeChoices(), "control", "");
            atomPositionXJSFloat = new JSONStorableFloat("atomPositionX", 0f, -3f, 3f, false);
            atomPositionYJSFloat = new JSONStorableFloat("atomPositionY", 0f, -3f, 3f, false);
            atomPositionZJSFloat = new JSONStorableFloat("atomPositionZ", 0f, -3f, 3f, false);
            atomRotationXJSFloat = new JSONStorableFloat("atomRotationX", 0f, -180f, 180f);
            atomRotationYJSFloat = new JSONStorableFloat("atomRotationY", 0f, -180f, 180f);
            atomRotationZJSFloat = new JSONStorableFloat("atomRotationZ", 0f, -180f, 180f);
            atomAbsoluteHeightJSBool = new JSONStorableBool("atomAbsoluteHeight", false);
            atomAbsolutePitchJSBool = new JSONStorableBool("atomAbsolutePitch", false);
            atomAbsoluteRollJSBool = new JSONStorableBool("atomAbsoluteRoll", false);

            RegisterParam(atomPositionRelativeToJSEnum);
            RegisterParam(targetNodeJSSC);

            RegisterParam(atomPositionXJSFloat);
            RegisterParam(atomPositionYJSFloat);
            RegisterParam(atomPositionZJSFloat);
            RegisterParam(atomRotationXJSFloat);
            RegisterParam(atomRotationYJSFloat);
            RegisterParam(atomRotationZJSFloat);

            RegisterParam(atomAbsoluteHeightJSBool);
            RegisterParam(atomAbsolutePitchJSBool);
            RegisterParam(atomAbsoluteRollJSBool);
        }

        public static List<string> GetPersonNodeChoices()
        {
            List<string> nodeChoices = new List<string>();
            nodeChoices.Add("control");
            nodeChoices.Add("headControl");
            nodeChoices.Add("head");
            nodeChoices.Add("Lips");
            nodeChoices.Add("Mouth");
            nodeChoices.Add("neckControl");
            nodeChoices.Add("neck");
            nodeChoices.Add("lShoulderControl");
            nodeChoices.Add("lCollar");
            nodeChoices.Add("lPectoral");
            nodeChoices.Add("lArmControl");
            nodeChoices.Add("lShldr");
            nodeChoices.Add("lElbowControl");
            nodeChoices.Add("lForeArm");
            nodeChoices.Add("lHandControl");
            nodeChoices.Add("lHand");
            nodeChoices.Add("rShoulderControl");
            nodeChoices.Add("rCollar");
            nodeChoices.Add("rPectoral");
            nodeChoices.Add("rArmControl");
            nodeChoices.Add("rShldr");
            nodeChoices.Add("rElbowControl");
            nodeChoices.Add("rForeArm");
            nodeChoices.Add("rHandControl");
            nodeChoices.Add("rHand");

            nodeChoices.Add("chestControl");
            nodeChoices.Add("chest");
            nodeChoices.Add("lNippleControl");
            nodeChoices.Add("lNipple");
            nodeChoices.Add("rNippleControl");
            nodeChoices.Add("rNipple");

            nodeChoices.Add("abdomen2Control");
            nodeChoices.Add("abdomen2");
            nodeChoices.Add("pelvisControl");
            nodeChoices.Add("pelvis");
            nodeChoices.Add("hipControl");
            nodeChoices.Add("hip");
            nodeChoices.Add("abdomenControl");
            nodeChoices.Add("abdomen");
            nodeChoices.Add("LGlute");
            nodeChoices.Add("RGlute");

            nodeChoices.Add("testesControl");
            nodeChoices.Add("penisBaseControl");
            nodeChoices.Add("penisMidControl");
            nodeChoices.Add("penisTipControl");

            nodeChoices.Add("Labia");
            nodeChoices.Add("Vagina");

            nodeChoices.Add("lThighControl");
            nodeChoices.Add("lThigh");
            nodeChoices.Add("lKneeControl");
            nodeChoices.Add("lShin");
            nodeChoices.Add("lFootControl");
            nodeChoices.Add("lFoot");
            nodeChoices.Add("lToeControl");

            nodeChoices.Add("rThighControl");
            nodeChoices.Add("rThigh");
            nodeChoices.Add("rKneeControl");
            nodeChoices.Add("rShin");
            nodeChoices.Add("rFootControl");
            nodeChoices.Add("rFoot");
            nodeChoices.Add("rToeControl");

            return nodeChoices;
        }

        public string GetRelativePositionAtomName()
        {
            if (atomPositionRelativeToJSEnum.val == RelativePositionMode.lastViewedFemale) return TargetControl.lastViewedFemale;
            if (atomPositionRelativeToJSEnum.val == RelativePositionMode.lastViewedMale) return TargetControl.lastViewedMale;
            if (atomPositionRelativeToJSEnum.val == RelativePositionMode.lastViewedPerson) return TargetControl.lastViewedPerson;

            

            return "";
        }

        public void UpdateSpawnPosition(ref Vector3 position, ref Quaternion rotation, string testAtomName = null)
        {
            string gazeTargetAtomName = GetRelativePositionAtomName();
            if (atomPositionRelativeToJSEnum.val == RelativePositionMode.altAtom)
            {
                if (parentButtonOperation.targetComponent.currentActionAltTargetAtomNames.Count != 1) return;
                gazeTargetAtomName = parentButtonOperation.targetComponent.currentActionAltTargetAtomNames[0];
            }
            
            if (testAtomName != null) gazeTargetAtomName = testAtomName;

            Vector3 offsetPosition = new Vector3(atomPositionXJSFloat.val, atomPositionYJSFloat.val, atomPositionZJSFloat.val);
            Vector3 basePosition = new Vector3(0f, 0f, 0f);
            Quaternion baseRotation = new Quaternion();
            if (atomAbsoluteHeightJSBool.val) offsetPosition.y = 0f;
            Transform relativeToTransform = GetSpawnRelativeToTransform(gazeTargetAtomName);
            if (relativeToTransform != null)
            {
                basePosition = relativeToTransform.position;
                baseRotation = relativeToTransform.rotation;
                if (buttonTypeJSEnum.val != UIAButtonOpType.teleportPlayer) offsetPosition = relativeToTransform.rotation * offsetPosition;
            }

            position.x = basePosition.x + offsetPosition.x;
            if (!atomAbsoluteHeightJSBool.val) position.y = basePosition.y + offsetPosition.y;
            else position.y = atomPositionYJSFloat.val;
            position.z = basePosition.z + offsetPosition.z;

            if (buttonTypeJSEnum.val != UIAButtonOpType.teleportPlayer)
            {
                /*               float xRotation = baseRotation.eulerAngles.x + atomRotationXJSFloat.val;
                               if (atomAbsolutePitchJSBool.val) xRotation = atomRotationXJSFloat.val;
                               float zRotation = baseRotation.eulerAngles.z + atomRotationXJSFloat.val;
                               if (atomAbsoluteRollJSBool.val) zRotation = atomRotationZJSFloat.val;
                               rotation.eulerAngles = new Vector3(xRotation, baseRotation.eulerAngles.y + atomRotationYJSFloat.val, zRotation);
                */
                Quaternion offsetRotation = new Quaternion();
                offsetRotation.eulerAngles = new Vector3(atomRotationXJSFloat.val, atomRotationYJSFloat.val, atomRotationZJSFloat.val);

                rotation = baseRotation * offsetRotation;
                if (atomAbsolutePitchJSBool.val) rotation = Quaternion.Euler(atomRotationXJSFloat.val, rotation.y, rotation.z);
                if (atomAbsoluteRollJSBool.val) rotation = Quaternion.Euler(rotation.x, rotation.y, atomRotationZJSFloat.val);
            }
            else
            {

                if (atomPositionRelativeToJSEnum.val == RelativePositionMode.sceneOrigin) rotation.eulerAngles = new Vector3(0f, atomRotationYJSFloat.val, 0f);
                else rotation.SetLookRotation(basePosition - position, SuperController.singleton.navigationRig.up);
                
            }

        }

        public void TeleportAtomToSpawnPoint(string teleportAtomName, bool applyPresets = false)
        {
            Vector3 position = new Vector3(0f, 0f, 0f);
            Quaternion rotation = new Quaternion();
            UpdateSpawnPosition(ref position, ref rotation);
            TeleportAtomToSpawnPoint(teleportAtomName, position, rotation, applyPresets);
        }
        public void TeleportAtomToSpawnPoint(string teleportAtomName, Vector3 position, Quaternion rotation, bool applyPresets = false)
        {
            Atom atom = SuperController.singleton.GetAtomByUid(teleportAtomName);
            if (atom != null)
            {
                JSONClass atomsJSON = SuperController.singleton.GetSaveJSON(atom);
                JSONArray atomsArrayJSON = atomsJSON["atoms"].AsArray;
                JSONClass atomJSON = (JSONClass)atomsArrayJSON[0];

                JSONArray storablesArrayJSON = atomJSON["storables"].AsArray;

                atomJSON["position"]["x"].AsFloat = position.x;
                atomJSON["position"]["y"].AsFloat = position.y;
                atomJSON["position"]["z"].AsFloat = position.z;
                atomJSON["containerPosition"]["x"].AsFloat = position.x;
                atomJSON["containerPosition"]["y"].AsFloat = position.y;
                atomJSON["containerPosition"]["z"].AsFloat = position.z;
                atomJSON["rotation"]["x"].AsFloat = rotation.eulerAngles.x;
                atomJSON["rotation"]["y"].AsFloat = rotation.eulerAngles.y;
                atomJSON["rotation"]["z"].AsFloat = rotation.eulerAngles.z;
                atomJSON["containerRotation"]["x"].AsFloat = rotation.eulerAngles.x;
                atomJSON["containerRotation"]["y"].AsFloat = rotation.eulerAngles.y;
                atomJSON["containerRotation"]["z"].AsFloat = rotation.eulerAngles.z;

                Dictionary<string, int> storablesIndexDict = new Dictionary<string, int>();

                for (int i = 0; i < storablesArrayJSON.Count; i++)
                {
                    JSONClass storableJSON = (JSONClass)storablesArrayJSON[i];
                    storablesIndexDict.Add(storableJSON["id"].Value, i);
                    if (storableJSON["id"].Value == "control")
                    {
                        atomJSON["storables"][i]["position"]["x"].AsFloat = position.x;
                        atomJSON["storables"][i]["position"]["y"].AsFloat = position.y;
                        atomJSON["storables"][i]["position"]["z"].AsFloat = position.z;
                    }
                    if (storableJSON["id"].Value == "hip")
                    {
                        atomJSON["storables"][i]["rootPosition"]["x"].AsFloat = position.x;
                        atomJSON["storables"][i]["rootPosition"]["y"].AsFloat = atomJSON["storables"][i]["rootPosition"]["y"].AsFloat + position.y;
                        atomJSON["storables"][i]["rootPosition"]["z"].AsFloat = position.z;
                    }
                    if (storableJSON["id"].Value.StartsWith("hair"))
                    {
                        atomJSON["storables"][i]["position"]["x"].AsFloat = atomJSON["storables"][i]["position"]["x"].AsFloat + position.x;
                        atomJSON["storables"][i]["position"]["y"].AsFloat = atomJSON["storables"][i]["position"]["y"].AsFloat + position.y;
                        atomJSON["storables"][i]["position"]["z"].AsFloat = atomJSON["storables"][i]["position"]["z"].AsFloat + position.z;
                    }

                    if (storableJSON["id"].Value == "control" || storableJSON["id"].Value == "hip" || storableJSON["id"].Value.StartsWith("hair"))
                    {
                        string rotationKeyName = "";
                        if (storableJSON["id"].Value == "hip") rotationKeyName = "rootRotation";
                        else rotationKeyName = "rotation";
                        atomJSON["storables"][i][rotationKeyName]["x"].AsFloat = rotation.eulerAngles.x;
                        atomJSON["storables"][i][rotationKeyName]["y"].AsFloat = rotation.eulerAngles.y;
                        atomJSON["storables"][i][rotationKeyName]["z"].AsFloat = rotation.eulerAngles.z;
                    }
                }

                atom.PreRestore();
                atom.RestoreTransform(atomJSON);
                atom.Restore(atomJSON);
                atom.LateRestore(atomJSON);
                atom.PostRestore();

                ClothingComponent.ClothingActions(atom, ClothingActionMode.resetSim, false);
            }

        }

        public void TeleportPlayer()
        {
            Vector3 targetPosition = new Vector3(0f, 0f, 0f);
            Quaternion rotation = new Quaternion();
            UpdateSpawnPosition(ref targetPosition, ref rotation);
            var navigationRig = SuperController.singleton.navigationRig;
            var camera = SuperController.singleton.lookCamera;
            var cameraMoveDelta = targetPosition - camera.transform.position;

            var nrPosition = navigationRig.position;
            nrPosition += cameraMoveDelta;
            var up = navigationRig.up;

            var upDelta = Vector3.Dot(cameraMoveDelta, up);
            nrPosition += up * (0f - upDelta);
            navigationRig.position = nrPosition;
            SuperController.singleton.playerHeightAdjust += upDelta;

            navigationRig.rotation = rotation;

            SuperController.singleton.SyncMonitorRigPosition();
        }

        public Transform GetSpawnRelativeToTransform(string gazeTargetAtomName)
        {
            GameObject emptyGO = new GameObject();
            Transform relativeToTransform = emptyGO.transform;
            relativeToTransform.position = new Vector3(0f, 0f, 0f);
            relativeToTransform.rotation = new Quaternion(0f, 0f, 0f, 1f);

            if (atomPositionRelativeToJSEnum.val == RelativePositionMode.lastViewedFemale || atomPositionRelativeToJSEnum.val == RelativePositionMode.lastViewedMale || atomPositionRelativeToJSEnum.val == RelativePositionMode.lastViewedPerson)
            {
                Atom targetRelativePositionAtom = SuperController.singleton.GetAtomByUid(gazeTargetAtomName);
                if (targetRelativePositionAtom != null)
                {
                    if (targetNodeJSSC.val == "Lips" || targetNodeJSSC.val == "Mouth" || targetNodeJSSC.val == "Labia" || targetNodeJSSC.val == "Vagina")
                    {
                        string targetNode = targetNodeJSSC.val;
                        if (targetNodeJSSC.val == "Lips") targetNode = "LipTrigger";
                        else if (targetNodeJSSC.val == "Mouth") targetNode = "MouthTrigger";
                        else if (targetNodeJSSC.val == "Labia") targetNode = "LabiaTrigger";
                        else if (targetNodeJSSC.val == "Vagina") targetNode = "VaginaTrigger";
                        Rigidbody nodeRB = targetRelativePositionAtom.rigidbodies.First(rb => rb.name == targetNode);
                        if (nodeRB != null) relativeToTransform = nodeRB.transform;
                    }
                    else if (targetNodeJSSC.val.Contains("control") || targetNodeJSSC.val.Contains("Control"))
                    {
                        FreeControllerV3 nodeFCV3 = targetRelativePositionAtom.freeControllers.First(fc => fc.name == targetNodeJSSC.val);
                        if (nodeFCV3 != null) relativeToTransform = nodeFCV3.transform;
                    }
                    else
                    {
                        Rigidbody nodeRB = targetRelativePositionAtom.rigidbodies.First(rb => rb.name == targetNodeJSSC.val);
                        if (nodeRB != null) relativeToTransform = nodeRB.transform;
                    }
                }
            }
            else if (atomPositionRelativeToJSEnum.val == RelativePositionMode.altAtom)
            {
                Atom targetRelativePositionAtom = SuperController.singleton.GetAtomByUid(gazeTargetAtomName);
                if (targetRelativePositionAtom != null)
                {
                    FreeControllerV3 nodeFCV3 = targetRelativePositionAtom.freeControllers.First(fc => fc.name == "control");
                    if (nodeFCV3 != null) relativeToTransform = nodeFCV3.transform;
                }
            }
            else if (atomPositionRelativeToJSEnum.val == RelativePositionMode.vrHead || (!UIAGlobals.vrActive && (atomPositionRelativeToJSEnum.val == RelativePositionMode.vrLeftHand || atomPositionRelativeToJSEnum.val == RelativePositionMode.vrRightHand))) relativeToTransform = SuperController.singleton.lookCamera.transform;
            else if (atomPositionRelativeToJSEnum.val == RelativePositionMode.vrLeftHand) relativeToTransform = SuperController.singleton.leftHand.transform;
            else if (atomPositionRelativeToJSEnum.val == RelativePositionMode.vrRightHand) relativeToTransform = SuperController.singleton.rightHand.transform;

            return relativeToTransform;
        }


        public List<int> GetPositionRelativeToExclusions()
        {
            List<int> exclusions = new List<int>();
            if (buttonTypeJSEnum.val == UIAButtonOpType.teleportAtom)
            {
                exclusions.Add(RelativePositionMode.lastViewedFemale);
                exclusions.Add(RelativePositionMode.lastViewedMale);
                exclusions.Add(RelativePositionMode.lastViewedPerson);
            }
            else exclusions.Add(RelativePositionMode.altAtom);

            if (buttonTypeJSEnum.val == UIAButtonOpType.teleportPlayer)
            {
                exclusions.Add(RelativePositionMode.vrHead);
                exclusions.Add(RelativePositionMode.vrLeftHand);
                exclusions.Add(RelativePositionMode.vrRightHand);
            }
            return exclusions;
        }
        public void ButtonTypeUpdated()
        {
            atomPositionRelativeToJSEnum.SetEnumChoices(RelativePositionMode.enumManifestName, GetPositionRelativeToExclusions(), true, true);

            if (buttonTypeJSEnum.val == UIAButtonOpType.teleportPlayer)
            {
                atomPositionRelativeToJSEnum.val = RelativePositionMode.lastViewedFemale;
                targetNodeJSSC.val = "head";
                atomPositionXJSFloat.val = 0f;
                atomPositionYJSFloat.val = 0f;
                atomPositionZJSFloat.val = 1.5f;
                atomAbsoluteHeightJSBool.val = false;
            }
        }

        public List<string> GetMoveRelativeToChoices()
        {
            return (atomPositionRelativeToJSEnum.displayChoices);
        }

    }
    public class MoveAtomComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public JSONStorableBool lockedPositionXJSB;
        public JSONStorableBool lockedPositionYJSB;
        public JSONStorableBool lockedPositionZJSB;
        public JSONStorableBool lockedRotationXJSB;
        public JSONStorableBool lockedRotationYJSB;
        public JSONStorableBool lockedRotationZJSB;
        public JSONStorableBool vrMoveDisablePhysicsJSB;

        public static bool atomVRMoveActive = false;
        public static bool atomVRMovePaused = true;
        public static bool atomVRMoveLeapActivated = false;
        public static float atomVRMoveStartTime = 0f;
        public static string atomVRMoveAtomName = "";
        public static int atomVRMoveControllerLorR;
        public static UIAButtonOperation atomVRMoveUIAButton = null;

        public static Vector3 atomVRMoveControllerLastPosition = new Vector3();
        public static Quaternion atomVRMoveControllerLastRotation = new Quaternion();
        public static bool currentVRMovelockedPositionX = false;
        public static bool currentVRMovelockedPositionY = false;
        public static bool currentVRMovelockedPositionZ = false;
        public static bool currentVRMovelockedRotationX = false;
        public static bool currentVRMovelockedRotationY = false;
        public static bool currentVRMovelockedRotationZ = false;

        public MoveAtomComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;

            lockedPositionXJSB = new JSONStorableBool("lockedPositionX", false);
            lockedPositionYJSB = new JSONStorableBool("lockedPositionY", true);
            lockedPositionZJSB = new JSONStorableBool("lockedPositionZ", false);
            lockedRotationXJSB = new JSONStorableBool("lockedRotationX", true);
            lockedRotationYJSB = new JSONStorableBool("lockedRotationY", false);
            lockedRotationZJSB = new JSONStorableBool("lockedRotationZ", true);
            vrMoveDisablePhysicsJSB = new JSONStorableBool("vrMoveDisablePhysics", false);

            RegisterParam(lockedPositionXJSB);
            RegisterParam(lockedPositionYJSB);
            RegisterParam(lockedPositionZJSB);
            RegisterParam(lockedRotationXJSB);
            RegisterParam(lockedRotationYJSB);
            RegisterParam(lockedRotationZJSB);
            RegisterParam(vrMoveDisablePhysicsJSB);
        }

        public void PlayMoveAtomAction(Atom atom, int vrHand, bool leapActivated)
        {
            if (UIAGlobals.vrActive && vrHand != LeftRight.neither)
            {
                if (!atomVRMoveActive || atomVRMoveUIAButton != parentButtonOperation)
                {
                    if (atomVRMoveActive)
                    {
                        Atom previousMoveAtom = SuperController.singleton.GetAtomByUid(atomVRMoveAtomName);
                        if (previousMoveAtom != null)
                        {
                            previousMoveAtom.tempFreezePhysics = false;
                        }
                    }

                    if (vrMoveDisablePhysicsJSB.val) atom.tempFreezePhysics = true;
                    atomVRMoveActive = true;
                    atomVRMoveAtomName = atom.name;
                    atomVRMoveUIAButton = parentButtonOperation;
                    atomVRMoveLeapActivated = leapActivated;
                    FreeControllerV3 atomFC = atom.mainController;
                    Transform vrHandTransform = null;
                    if (vrHand == LeftRight.neither && VRSettings.vrHandControlJSEnum.val == VRHandControl.leftHand) vrHand = LeftRight.left;
                    if (vrHand == LeftRight.neither && VRSettings.vrHandControlJSEnum.val == VRHandControl.rightHand) vrHand = LeftRight.right;
                    if (vrHand == LeftRight.neither && VRSettings.vrHandControlJSEnum.val == VRHandControl.disabled) vrHand = LeftRight.right;

                    if (vrHand == LeftRight.left) vrHandTransform = SuperController.singleton.leftHand;
                    else if (vrHand == LeftRight.right) vrHandTransform = SuperController.singleton.rightHand;
                    atomVRMoveControllerLorR = vrHand;
                    atomVRMoveControllerLastPosition = vrHandTransform.position;
                    atomVRMoveControllerLastRotation = vrHandTransform.rotation;

                    currentVRMovelockedPositionX = lockedPositionXJSB.val;
                    currentVRMovelockedPositionY = lockedPositionYJSB.val;
                    currentVRMovelockedPositionZ = lockedPositionZJSB.val;
                    currentVRMovelockedRotationX = lockedRotationXJSB.val;
                    currentVRMovelockedRotationY = lockedRotationYJSB.val;
                    currentVRMovelockedRotationZ = lockedRotationZJSB.val;
                }
                else StopAtomVRMove(GameControlUI._leftHLeapControl, GameControlUI._rightHLeapControl);
            }

        }

        public static void UpdateAtomMove()
        {
            Transform vrMoveHandTransform = null;
            Atom atom = SuperController.singleton.GetAtomByUid(atomVRMoveAtomName);
            if (atomVRMoveControllerLorR == LeftRight.left) vrMoveHandTransform = SuperController.singleton.leftHand;
            else if (atomVRMoveControllerLorR == LeftRight.right) vrMoveHandTransform = SuperController.singleton.rightHand;

            if (atom != null && vrMoveHandTransform != null)
            {
                bool buttonGrabActived = false;

                if (atomVRMoveControllerLorR == LeftRight.left && (SuperController.singleton.GetLeftGrab() || GameControlUI._leftHLeapControl._pinchVRMoveActived)) buttonGrabActived = true;
                if (atomVRMoveControllerLorR == LeftRight.right && (SuperController.singleton.GetRightGrab() || GameControlUI._rightHLeapControl._pinchVRMoveActived)) buttonGrabActived = true;
                GameControlUI._leftHLeapControl._pinchVRMoveActived = false;
                GameControlUI._rightHLeapControl._pinchVRMoveActived = false;

                if (buttonGrabActived)
                {
                    atomVRMovePaused = false;
                    atomVRMoveStartTime = Time.time;

                    atomVRMoveControllerLastPosition = vrMoveHandTransform.position;
                    atomVRMoveControllerLastRotation = vrMoveHandTransform.rotation;
                }
                bool buttonGrabDeactived = false;
                if (atomVRMoveControllerLorR == LeftRight.left && (SuperController.singleton.GetLeftGrabRelease() || GameControlUI._leftHLeapControl._pinchVRMoveDeActived)) buttonGrabDeactived = true;
                if (atomVRMoveControllerLorR == LeftRight.right && (SuperController.singleton.GetRightGrabRelease() || GameControlUI._rightHLeapControl._pinchVRMoveDeActived)) buttonGrabDeactived = true;
                GameControlUI._leftHLeapControl._pinchVRMoveDeActived = false;
                GameControlUI._rightHLeapControl._pinchVRMoveDeActived = false;
                if (buttonGrabDeactived)
                {
                    atomVRMovePaused = true;
                    float timeSinceMoveStarted = Time.time - atomVRMoveStartTime;
                    if (timeSinceMoveStarted < 0.5f) StopAtomVRMove(GameControlUI._leftHLeapControl, GameControlUI._rightHLeapControl);
                    atomVRMoveStartTime = 0f;
                }

                if (!atomVRMovePaused && atomVRMoveActive)
                {
                    Vector3 direction = atomVRMoveControllerLastPosition - vrMoveHandTransform.position;

                    float angleChange = Quaternion.Angle(atomVRMoveControllerLastRotation, vrMoveHandTransform.rotation);

                    if (direction.magnitude > 0.06f)
                    {
                        Vector3 newPosition = new Vector3();
                        Vector3 currentPosition = atom.mainController.transform.position;
                        direction = direction.normalized * Mathf.Clamp(direction.magnitude - 0.06f, 0f, 0.5f);
                        newPosition = currentPosition + (direction * ((0f - Time.deltaTime) * 10f));
                        if (currentVRMovelockedPositionX) newPosition.x = currentPosition.x;
                        if (currentVRMovelockedPositionY) newPosition.y = currentPosition.y;
                        if (currentVRMovelockedPositionZ) newPosition.z = currentPosition.z;
                        atom.mainController.transform.position = newPosition;
                    }
                    else if (Mathf.Abs(angleChange) > 10f)
                    {
                        float rotationMultiplier = Time.deltaTime * 4.5f;

                        Quaternion angleDifference = vrMoveHandTransform.rotation * Quaternion.Inverse(atomVRMoveControllerLastRotation);
                        Vector3 angleDifferenceVector = new Vector3((angleDifference.eulerAngles.x), (angleDifference.eulerAngles.y), (angleDifference.eulerAngles.z));

                        if (angleDifferenceVector.y > 180f) angleDifferenceVector.y = angleDifferenceVector.y - 360f;
                        if (angleDifferenceVector.x > 180f) angleDifferenceVector.x = angleDifferenceVector.x - 360f;
                        if (angleDifferenceVector.z > 180f) angleDifferenceVector.z = angleDifferenceVector.z - 360f;

                        angleDifferenceVector = new Vector3((angleDifferenceVector.x * rotationMultiplier), (angleDifferenceVector.y * rotationMultiplier), (angleDifferenceVector.z * rotationMultiplier));

                        if (currentVRMovelockedRotationX) angleDifferenceVector.x = 0f;
                        if (currentVRMovelockedRotationY) angleDifferenceVector.y = 0f;
                        if (currentVRMovelockedRotationZ) angleDifferenceVector.z = 0f;

                        Quaternion lockedRotationDifference = Quaternion.Euler(angleDifferenceVector.x, angleDifferenceVector.y, angleDifferenceVector.z);

                        atom.mainController.transform.rotation *= lockedRotationDifference;
                    }
                }
            }

        }
        public static void StopAtomVRMove(LeapTouchControl _leftHLeapControl, LeapTouchControl _rightHLeapControl)
        {
            Atom atom = SuperController.singleton.GetAtomByUid(atomVRMoveAtomName);
            atomVRMoveActive = false;
            _leftHLeapControl._pinchVRMoveActived = false;
            _rightHLeapControl._pinchVRMoveActived = false;
            _leftHLeapControl._pinchVRMoveDeActived = false;
            _rightHLeapControl._pinchVRMoveDeActived = false;
            SuperController.singleton.helpHUDText.text = "";
            atomVRMoveLeapActivated = false;
            if (atom != null) atom.tempFreezePhysics = false;
        }

    }

    public class MotionCaptureComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public JSONStorableStringChooser motionCaptureAtomNameJSSC;
        public JSONStorableBool mocapLoadNodeControlStatesJSB;
        public JSONStorableBool mocapLoadNodePhysicsJSB;
        public JSONStorableFloat mocapHAFootRotationJSF;
        public JSONStorableFloat mocapHAToeRotationJSF;
        public JSONStorableFloat mocapHAHeelHeightJSF;

        public JSONClass motionCaptureSceneFileJSON = null;
        public JSONClass moCapFileJSON = null;

        public MotionCaptureComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;

            mocapLoadNodeControlStatesJSB = new JSONStorableBool("mocapLoadNodeControlStates", true);
            mocapLoadNodePhysicsJSB = new JSONStorableBool("mocapLoadNodePhysics", true);
            motionCaptureAtomNameJSSC = new JSONStorableStringChooser("motionCaptureAtomName", null,"","");
            mocapHAFootRotationJSF = new JSONStorableFloat("mocapHAFootRotation",0f,0f,90f);
            mocapHAToeRotationJSF = new JSONStorableFloat("mocapHAToeRotation", 0f, 0f, 90f);
            mocapHAHeelHeightJSF = new JSONStorableFloat("mocapHAHeelHeight", 0f, 0f, 50f);

            
            RegisterParam(motionCaptureAtomNameJSSC);
            RegisterParam(mocapLoadNodePhysicsJSB);
            RegisterParam(mocapLoadNodeControlStatesJSB);
            RegisterParam(mocapHAFootRotationJSF);
            RegisterParam(mocapHAToeRotationJSF);
            RegisterParam(mocapHAHeelHeightJSF);
        }

        public void ReloadMotionCaptureSceneData(string fileName)
        {
            if (FileManagerSecure.FileExists(fileName))
            {
                if (parentButtonOperation.buttonOpTypeJSEnum.val == UIAButtonOpType.loadMotionCapture)
                {
                    motionCaptureSceneFileJSON = SuperController.singleton.LoadJSON(fileName).AsObject;
                    List<string> atomChoices = GetMotionCaptureAtomChoices();
                    if (!atomChoices.Contains(motionCaptureAtomNameJSSC.val)) motionCaptureAtomNameJSSC.val = atomChoices[0];
                }
                else if (parentButtonOperation.buttonOpTypeJSEnum.val == UIAButtonOpType.loadELMotionCapture)
                {
                    moCapFileJSON = SuperController.singleton.LoadJSON(fileName).AsObject;
                }


            }
            else motionCaptureSceneFileJSON = null;
        }

        public void RefreshMocapAtomChoices()
        {
            motionCaptureAtomNameJSSC.choices = GetMotionCaptureAtomChoices();
        }

        public List<string> GetMotionCaptureAtomChoices()
        {
            List<string> popupChoices = new List<string>();

            FileReference sceneFileRef = parentButtonOperation.fileReferenceDict[FileReferenceTypes.scene];

            if (FileManagerSecure.FileExists(sceneFileRef.filePathJSString.val))
            {
                if (motionCaptureSceneFileJSON == null) motionCaptureSceneFileJSON = (JSONClass)SuperController.singleton.LoadJSON(sceneFileRef.filePathJSString.val);

                if (motionCaptureSceneFileJSON != null)
                {
                    JSONArray sceneFileAtomsJSON = motionCaptureSceneFileJSON["atoms"].AsArray;
                    if (sceneFileAtomsJSON != null)
                    {
                        List<string> personAtomNames = new List<string>();
                        foreach (JSONClass sceneFileAtomJSON in sceneFileAtomsJSON)
                        {
                            if (sceneFileAtomJSON["type"] != null && sceneFileAtomJSON["type"].Value == "Person")
                            {
                                foreach (JSONClass atomStorable in sceneFileAtomJSON["storables"].AsArray)
                                {
                                    if (atomStorable["id"].Value.EndsWith("Animation"))
                                    {
                                        popupChoices.Add(sceneFileAtomJSON["id"].Value);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    else popupChoices.Add("Scene File is not valid format");
                }
                else popupChoices.Add("Scene File is not valid format");
            }
            else popupChoices.Add("Scene File does not exist");

            if (popupChoices.Count == 0) popupChoices.Add("Scene does not contain Person atoms with Motion Capture");

            return (popupChoices);
        }

        private void OffsetMocapStepRotation(JSONArray steps, float xRotationOffset)
        {
            foreach (JSONNode step in steps)
            {
                if (step["originalRotation"] == null)
                {
                    step["originalRotation"]["x"].AsFloat = step["rotation"]["x"].AsFloat;
                    step["originalRotation"]["y"].AsFloat = step["rotation"]["y"].AsFloat;
                    step["originalRotation"]["z"].AsFloat = step["rotation"]["z"].AsFloat;
                    step["originalRotation"]["w"].AsFloat = step["rotation"]["w"].AsFloat;
                }
                Quaternion rotation = new Quaternion(step["originalRotation"]["x"].AsFloat, step["originalRotation"]["y"].AsFloat, step["originalRotation"]["z"].AsFloat, step["originalRotation"]["w"].AsFloat);

                rotation *= Quaternion.Euler(Vector3.right * -xRotationOffset);
                step["rotation"]["x"].AsFloat = rotation.x;
                step["rotation"]["y"].AsFloat = rotation.y;
                step["rotation"]["z"].AsFloat = rotation.z;
                step["rotation"]["w"].AsFloat = rotation.w;
            }
        }

        private Dictionary<string, JSONNode> GetStorableDictionaryFromArray(JSONNode personData, Atom targetAtom)
        {
            Dictionary<string, JSONNode> storablesDict = new Dictionary<string, JSONNode>();

            var heightAdjustment = mocapHAHeelHeightJSF.val * UIAConsts._ShoeColliderBaseScale;

            foreach (JSONNode storable in personData["storables"].AsArray)
            {
                if ((mocapHAHeelHeightJSF.val != 0 || storable["originalLocalPosition"] != null )&& storable["id"].Value.EndsWith("Control"))
                {
                    if (storable["originalLocalPosition"] == null) storable["originalLocalPosition"]["y"].AsFloat = storable["localPosition"]["y"].AsFloat;

                    storable["localPosition"]["y"].AsFloat = storable["originalLocalPosition"]["y"].AsFloat - heightAdjustment;
                }
                if ((mocapHAHeelHeightJSF.val != 0 || (storable["mocapHAHeelHeightRemoved"] != null && storable["mocapHAHeelHeightRemoved"].AsFloat!=0))  && storable["id"].Value.EndsWith("Animation"))
                {
                    foreach (JSONNode step in storable["steps"].AsArray)
                    {
                        if (step["originalPosition"] == null) step["originalPosition"]["y"].AsFloat = step["position"]["y"].AsFloat;
                        step["position"]["y"].AsFloat = step["originalPosition"]["y"].AsFloat - heightAdjustment;
                    }

                    storable["mocapHAHeelHeightRemoved"].AsFloat = heightAdjustment;


                }

                if (mocapHAFootRotationJSF.val!=0 && (storable["id"].Value == "rFootAnimation" || storable["id"].Value == "lFootAnimation"))
                {
                    OffsetMocapStepRotation(storable["steps"].AsArray, mocapHAFootRotationJSF.val);
                }
                if (mocapHAToeRotationJSF.val != 0 &&  (storable["id"].Value == "rToeAnimation" || storable["id"].Value == "lToeAnimation"))
                {
                    OffsetMocapStepRotation(storable["steps"].AsArray, mocapHAToeRotationJSF.val);
                }
                if (storable["id"].Value.EndsWith("Control"))
                {
                    if (storable["linkTo"] != null)
                    {
                        if (storable["originalLinkTo"] != null) storable["linkTo"] = storable["originalLinkTo"].Value;

                        string origLinkTo = storable["linkTo"].Value;
                        if (personData["id"].Value == origLinkTo.Substring(0, origLinkTo.IndexOf(":")))
                        {
                            string linkTo = targetAtom.name + ":" + origLinkTo.Substring(origLinkTo.IndexOf(":") + 1);
                            storable["originalLinkTo"] = origLinkTo;
                            storable["linkTo"] = linkTo;
                        }
                        
                    }
                }
                storablesDict.Add(storable["id"].Value, storable);
            }
            return (storablesDict);
        }

        public void LoadMocapFromMocapFile(Atom targetAtom, string mocapFileName)
        {
            if (moCapFileJSON == null && mocapFileName != "" && FileManagerSecure.FileExists(mocapFileName)) moCapFileJSON = SuperController.singleton.LoadJSON(mocapFileName).AsObject;
            if (moCapFileJSON != null)
            {
                RestoreAtomAnim(targetAtom, moCapFileJSON["Person"].AsObject);
                RestoreMotionAnimMaster(moCapFileJSON["CoreControl"].AsObject);
            }
            
        }

        public void LoadMocapFromSceneToAtom(Atom targetAtom, string mocapFileName)
        {
            bool singleFileSelect = parentButtonOperation.fileReferenceDict[FileReferenceTypes.scene].fileSelectionModeJSEnum.val == FileSelectionMode.singleFile;
            if (singleFileSelect)
            {
                if (motionCaptureAtomNameJSSC.val == "Scene File is not valid format" || motionCaptureAtomNameJSSC.val == "Scene File does not exist" || motionCaptureAtomNameJSSC.val == "Scene does not contain Person atoms with Motion Capture") return;
            }

            if (mocapFileName != "" && FileManagerSecure.FileExists(mocapFileName))
            {
                if (!singleFileSelect || motionCaptureSceneFileJSON == null) motionCaptureSceneFileJSON = (JSONClass)SuperController.singleton.LoadJSON(mocapFileName);

                if (motionCaptureSceneFileJSON != null)
                {
                    // Get the source atom from
                    JSONArray sceneFileAtomsJSON = motionCaptureSceneFileJSON["atoms"].AsArray;
                    JSONClass sceneFileSourceAtomJSON = null;
                    JSONClass sceneFileCoreControlAtomJSON = null;
                    if (sceneFileAtomsJSON != null)
                    {
                        foreach (JSONClass sceneFileAtomJSON in sceneFileAtomsJSON)
                        {
                            if (sceneFileAtomJSON["id"].Value == "CoreControl")
                            {
                                sceneFileCoreControlAtomJSON = sceneFileAtomJSON;
                            }
                            if (sceneFileAtomJSON["type"] != null && sceneFileAtomJSON["type"].Value == "Person")
                            {
                                if (sceneFileAtomJSON["id"].Value == motionCaptureAtomNameJSSC.val || !singleFileSelect)
                                {
                                    sceneFileSourceAtomJSON = sceneFileAtomJSON;
                                }
                            }

                            if (sceneFileSourceAtomJSON != null && sceneFileCoreControlAtomJSON != null) break;
                        }
                    }
                    if (sceneFileSourceAtomJSON != null && sceneFileCoreControlAtomJSON != null)
                    {
                        RestoreAtomAnim(targetAtom, sceneFileSourceAtomJSON);
                        RestoreMotionAnimMaster(sceneFileCoreControlAtomJSON);
                    }
                }
            }
            

        }

        private void RestoreAtomAnim (Atom targetAtom, JSONClass sceneFileSourceAtomJSON)
        {
            // Create a lookup Dictionary for storables in the source atom
            Dictionary<string, JSONNode> sceneFileAtomStorablesDict = GetStorableDictionaryFromArray(sceneFileSourceAtomJSON, targetAtom);
            HeelAdjustTool.RemoveHeelAdjustMocapOffsets(targetAtom, true);

            // Process Target Atom to obtatin target Storables
            List<string> rbStorableIds = new List<string>();
            List<string> storableIDs = targetAtom.GetStorableIDs();
            List<string> nodeNames = RelativePositionComponent.GetPersonNodeChoices();       

            foreach (string id in storableIDs)
            {               
                if (id.EndsWith("Animation"))
                {
                    JSONStorable storable = targetAtom.GetStorableByID(id);
                    if (sceneFileAtomStorablesDict.ContainsKey(id))
                    {
                        SuperController.singleton.StartCoroutine(RestoreAndLateRestore(storable, (JSONClass)sceneFileAtomStorablesDict[id]));
 //                       storable.RestoreFromJSON((JSONClass)sceneFileAtomStorablesDict[id]);
 //                       storable.LateRestoreFromJSON((JSONClass)sceneFileAtomStorablesDict[id]);
                    }
                    else storable.RestoreAllFromDefaults();
                }
                else if (id.EndsWith("Control") && nodeNames.Contains(id))
                {
                    JSONStorable storable = targetAtom.GetStorableByID(id);
                    if (sceneFileAtomStorablesDict.ContainsKey(id))
                    {
                        SuperController.singleton.StartCoroutine(RestoreAndLateRestore(storable, (JSONClass)sceneFileAtomStorablesDict[id],true));
//                        storable.RestoreFromJSON((JSONClass)sceneFileAtomStorablesDict[id]);
 //                       storable.LateRestoreFromJSON((JSONClass)sceneFileAtomStorablesDict[id]);
                    }
                    else storable.RestoreAllFromDefaults();
                }
                else if (nodeNames.Contains(id) && id != "control")
                {
                    JSONStorable storable = targetAtom.GetStorableByID(id);
                    if (sceneFileAtomStorablesDict.ContainsKey(id)) rbStorableIds.Add(id);
                    else storable.RestoreAllFromDefaults();
                }
            }
            foreach (string storableId in rbStorableIds)
            {
                JSONStorable rbStorable = targetAtom.GetStorableByID(storableId);
                SuperController.singleton.StartCoroutine(RestoreAndLateRestore(rbStorable, (JSONClass)sceneFileAtomStorablesDict[storableId]));
//                rbStorable.RestoreFromJSON((JSONClass)sceneFileAtomStorablesDict[storableId]);
//                rbStorable.LateRestoreFromJSON((JSONClass)sceneFileAtomStorablesDict[storableId]);
            }
            UIAGlobals.mvrScript.StartCoroutine(ActiveClothingList.RefreshACL(targetAtom));
            ClothingComponent.ClothingActions(targetAtom, ClothingActionMode.resetSim, false);
        }
        private void RestoreMotionAnimMaster(JSONClass sceneFileCoreControlAtomJSON)
        {
            foreach (JSONClass storableJC in sceneFileCoreControlAtomJSON["storables"].AsArray)
            {
                if (string.Equals(storableJC["id"], "MotionAnimationMaster"))
                {
                    storableJC["playbackCounter"] = "0";
                    storableJC["startTimestep"] = "0";
                    /*                           if (storable["stopTimestep"].AsFloat < SuperController.singleton.motionAnimationMaster.totalTime)
                                               {
                                                   storable["stopTimestep"].AsFloat = SuperController.singleton.motionAnimationMaster.totalTime;
                                               }
                                               if (storable["recordedLength"].AsFloat < SuperController.singleton.motionAnimationMaster.totalTime - SuperController.singleton.motionAnimationMaster.loopbackTime)
                                               {
                                                   storable["recordedLength"].AsFloat = SuperController.singleton.motionAnimationMaster.totalTime - SuperController.singleton.motionAnimationMaster.loopbackTime;
                                               }*/
                    SuperController.singleton.motionAnimationMaster.RestoreFromJSON(storableJC);

 //                   UIAGlobals.mvrScript.StartCoroutine(RestoreAndLateRestore(SuperController.singleton.motionAnimationMaster, storableJC));
                    break;
                }
            }
            SuperController.singleton.motionAnimationMaster.StartPlayback();
        }

        private IEnumerator RestoreAndLateRestore(JSONStorable storable, JSONClass jc, bool forceResetParentLink = false)
        {
            storable.PreRestore();
            yield return new WaitForEndOfFrame();
            storable.RestoreFromJSON(jc);
            yield return new WaitForEndOfFrame();
            storable.LateRestoreFromJSON(jc);
            yield return new WaitForEndOfFrame();
            storable.PostRestore();
            if (forceResetParentLink && jc["linkTo"]!=null)
            {
                yield return new WaitForEndOfFrame();
                storable.SetStringChooserParamValue("positionState", "Off");
                storable.SetStringChooserParamValue("rotationState", "Off");
                yield return new WaitForSecondsRealtime(1);
                storable.SetStringChooserParamValue("positionState", "ParentLink");
                storable.SetStringChooserParamValue("rotationState", "ParentLink");
            }
        }
    }

    public class WorldScaleComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public JSONStorableFloat worldScaleAbsJSF;
        public JSONStorableFloat worldScaleIncrementJSF;

        public WorldScaleComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;

            worldScaleAbsJSF = new JSONStorableFloat("worldScaleAbs", 1f, 0.1f, 20f);
            worldScaleIncrementJSF = new JSONStorableFloat("worldScaleIncrement", 0.1f, -1f, 1f);

            RegisterParam(worldScaleAbsJSF);
            RegisterParam(worldScaleIncrementJSF);
        }
    }

    public class BAFilterComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public BAOrderedResourceFilter baFilter;

        public JSONStorableEnumStringChooser baFilterSelectModeJSEnum;
        public JSONStorableBool openBrowserAssistJSB;

        public BAFilterComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;

            baFilter = new BAOrderedResourceFilter(BAResourceTypeEnum.scene, "");

            baFilterSelectModeJSEnum = new JSONStorableEnumStringChooser("baFilterParam", BASelectModeFilter.enumManifestName, BASelectModeFilter.single, "BA SelectMode");
            openBrowserAssistJSB = new JSONStorableBool("openBrowserAssist", true);
            RegisterParam(baFilterSelectModeJSEnum);
            RegisterParam(openBrowserAssistJSB);


        }

        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            var jc = base.GetJSON(jspLoadExclusions);
            jc["baFilter"] = baFilter.GetJSON();
            return jc;
        }

        public override void RestoreFromJSON(JSONClass jc, string _paramName = null)
        {
            base.RestoreFromJSON(jc, _paramName);
            baFilter.RestoreJSON(jc["baFilter"].AsObject);
        }
        public void CopyFrom(BAFilterComponent baFilterComponent)
        {
            base.CopyFrom(baFilterComponent);

            baFilter.RestoreJSON(baFilterComponent.baFilter.GetJSON());
        }

        public void SetFilters()
        {
            if (BAInterop.BAInteropVersion >= 4)
            {
                BAInterop.SetMainResourceFilters(baFilter, baFilterSelectModeJSEnum.val, BADesktopDisplayModeEnum.noChange);
                if (openBrowserAssistJSB.val && UIAPluginInterop.openBrowserAssistUIJSAction!=null) UIAPluginInterop.openBrowserAssistUIJSAction.actionCallback();
            }            
        }
    }

    public class BARulesetSelectionComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public JSONStorableEnumStringChooser baRuleSetTypeJSEnum;
        public JSONStorableStringChooser customRuleSetNameJSSC;
        public JSONStorableBool applyRulesetJSB;

        public BARulesetSelectionComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;
            baRuleSetTypeJSEnum = new JSONStorableEnumStringChooser("baRuleSetType", BAVAREnablingRulesetType.enumManifestName, BAVAREnablingRulesetType.allDisabled, "BA Ruleset Mode");
            customRuleSetNameJSSC = new JSONStorableStringChooser("customRuleSetName", null, "", "Custom Ruleset");

            applyRulesetJSB = new JSONStorableBool("applyRuleset", true);

            RegisterParam(customRuleSetNameJSSC);
            RegisterParam(baRuleSetTypeJSEnum);
            RegisterParam(applyRulesetJSB);
        }

        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            var jc = base.GetJSON(jspLoadExclusions);
            return jc;
        }

        public override void RestoreFromJSON(JSONClass jc, string _paramName = null)
        {
            base.RestoreFromJSON(jc, _paramName);
        }
        public void CopyFrom(BAFilterComponent baFilterComponent)
        {
            base.CopyFrom(baFilterComponent);
        }

        public void SwitchBARuleset()
        {
            if (BAInterop.isBAVARManagementEnabled)
            {
                if (baRuleSetTypeJSEnum.val == BAVAREnablingRulesetType.allEnabled) BAInterop.SwitchVAREnablingRulesetAllEnabled(applyRulesetJSB.val);
                else if (baRuleSetTypeJSEnum.val == BAVAREnablingRulesetType.allDisabled) BAInterop.SwitchVAREnablingRulesetAllDisabled(applyRulesetJSB.val);
                else if (baRuleSetTypeJSEnum.val == BAVAREnablingRulesetType.custom) BAInterop.SwitchVAREnablingRuleset(customRuleSetNameJSSC.val, applyRulesetJSB.val);
            }

        }

        public static void ApplyCurrentRuleset()
        {
            if (BAInterop.isBAVARManagementEnabled)
            {
                BAInterop.ApplyVAREnablingRuleset();
            }
        }

        public void RefreshCustomRulesetNames()
        {
            BAInterop.GetVAREnablingRulesetNames(RulesetNamesCallback);
        }

        private void RulesetNamesCallback(List<string> rulesetNames)
        {
            customRuleSetNameJSSC.choices = rulesetNames;
        }
    }


    public class MorphControlComponent : ButtonOperationComponentBase
    {
        public JSONStorableEnumStringChooser buttonTypeJSEnum;
        public JSONStorableStringChooser morphUIDJSSC;

        public JSONStorableEnumStringChooser morphGenderJSEnum;

        public JSONStorableFloat morphValueJSF;
        public JSONStorableFloat morphIncrementValueJSF;

        public JSONStorableEnumStringChooser morphZeroJSEnum;

        public JSONStorableString morphUIDDisplayJS;

        public MorphControlComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;

            morphGenderJSEnum = new JSONStorableEnumStringChooser("morphGender", MorphGenderTypes.enumManifestName, MorphGenderTypes.femaleMorphs, "Morph Gender Type",MorphGenderChangeCallback);

            morphUIDJSSC = new JSONStorableStringChooser("morphUID", null, "", "Morph Name",MorphUIDChangeCallback);
            morphUIDDisplayJS = new JSONStorableString("morphUIDDisply", "");
            morphValueJSF = new JSONStorableFloat("morphValue", 0f, 0f, 1f);
            morphValueJSF.constrained = false;

            MorphCache.UpdateMorphChoices(morphUIDJSSC, morphGenderJSEnum.val);


            if (morphUIDJSSC.choices!=null && morphUIDJSSC.choices.Count>0) morphUIDJSSC.val = morphUIDJSSC.choices[0];

            morphIncrementValueJSF = new JSONStorableFloat("morphIncrementValue", 0.01f, -1f, 1f);
            morphIncrementValueJSF.constrained = false;

            morphZeroJSEnum = new JSONStorableEnumStringChooser("morphZero", MorphZeroTypes.enumManifestName, MorphZeroTypes.nearZero1, "Zero Morphs");

            RegisterParam(morphGenderJSEnum);
            RegisterParam(morphUIDJSSC);
            RegisterParam(morphValueJSF);
            RegisterParam(morphIncrementValueJSF);
            
            RegisterParam(morphZeroJSEnum);
        }

        private void MorphGenderChangeCallback(int val)
        {
            MorphCache.UpdateMorphChoices(morphUIDJSSC, val);
            if (morphUIDJSSC.choices.Count > 0) morphUIDJSSC.val = morphUIDJSSC.choices[0];
        }

        private void MorphUIDChangeCallback(string val)
        {
            morphUIDDisplayJS.val = val;
            MorphCache.UpdateMorphValueMinMax(morphValueJSF, morphUIDJSSC.val, morphGenderJSEnum.val);
        }

        private GenerateDAZMorphsControlUI GetMorphControlUI(Atom targetAtom)
        {
            if (targetAtom == null || targetAtom.type != "Person") return null;

            JSONStorable receiver = targetAtom.GetStorableByID("geometry");
            if (receiver != null)
            {
                DAZCharacterSelector character = receiver as DAZCharacterSelector;
                if (morphGenderJSEnum.val == MorphGenderTypes.maleMorphs) return character.morphsControlMaleUI;
                return character.morphsControlFemaleUI ;
            }
            return null;
        }

        private DAZMorph GetMorph(Atom targetAtom)
        {
            GenerateDAZMorphsControlUI morphControl = GetMorphControlUI(targetAtom);
            if (morphControl!=null) return morphControl.GetMorphByUid(morphUIDJSSC.val);

            return null;
        }


        public void SetMorphValue(Atom targetAtom)
        {
            var morph = GetMorph(targetAtom);
            if (morph == null) return;

            morph.morphValue = morphValueJSF.val;
        }

        public void AdjustMorphValue(Atom targetAtom)
        {
            var morph = GetMorph(targetAtom);
            if (morph == null) return;

            morph.morphValue += morphIncrementValueJSF.val;
        }

        public void ResetMorphValue(Atom targetAtom)
        {
            var morph = GetMorph(targetAtom);
            if (morph == null) return;

            morph.SetDefaultValue();
        }

        public void ZeroMorphValue(Atom targetAtom)
        {
            GenerateDAZMorphsControlUI morphControl = GetMorphControlUI(targetAtom);
            if (morphZeroJSEnum.val == MorphZeroTypes.all) morphControl.ZeroAll();
            if (morphZeroJSEnum.val == MorphZeroTypes.morph) morphControl.ZeroMorph();
            if (morphZeroJSEnum.val == MorphZeroTypes.pose) morphControl.ZeroPose();
            if (morphZeroJSEnum.val == MorphZeroTypes.nearZero1) morphControl.ZeroNearZero();
            if (morphZeroJSEnum.val == MorphZeroTypes.nearZero5) morphControl.ZeroNearZeroMore();
        }
    }


    public class VAMTriggerActionComponent : ButtonOperationComponentBase
    {
        public JSONStorableStringChooser receiverNameJSSC;
        public JSONStorableStringChooser targetNameJSSC;

        public JSONStorableFloat floatParamJSF;
        public JSONStorableEnumStringChooser boolToggleParamJSESC;
        public JSONStorableColor colorParamJSC;
        public JSONStorableVector3 vector3ParamJSV3;
        public JSONStorableString stringParamJSS;
        public JSONStorableUrl urlParamJSU;
        public JSONStorableStringChooser stringChooserParamJSSC;
        public JSONStorableStringChooser audioClipTypeJSSC;
        public JSONStorableStringChooser audioClipCatJSSC;
        public JSONStorableStringChooser audioClipJSSC;

        private bool onlyFloatParam = false;

        private TargetComponent targetComponent { get
            {

                if (parentButton!=null) return parentButtonOperation.targetComponent;
                return parentSlider.targetComponent;
            } }

        public string targetAtomType { get
            {
                return targetComponent.targetAtomType;
            } }

        public JSONStorable.Type paramStorableType { get; private set; }

        public VAMTriggerActionComponent(UIASlider parent) : base(parent)
        {
            onlyFloatParam = true;
            Init();
        }

        public VAMTriggerActionComponent(UIAButtonOperation parent) : base(parent)
        {
            Init();
        }

        private void Init()
        {
            receiverNameJSSC = new JSONStorableStringChooser("receiverName", null, "", "Receiver", ReceiverNameUpdate);
            targetNameJSSC = new JSONStorableStringChooser("targetName", null, "", "Receiver Target", TargetNameUpdate);

            boolToggleParamJSESC = new JSONStorableEnumStringChooser("boolToggleParam", BoolToggle.enumManifestName, BoolToggle.toggle, "Target Value:");
            floatParamJSF = new JSONStorableFloat("floatParam", 0f, 0f, 1f);
            colorParamJSC = new JSONStorableColor("colorParam", new HSVColor());
            vector3ParamJSV3 = new JSONStorableVector3("vector3Param", new Vector3(), new Vector3(), new Vector3());
            stringParamJSS = new JSONStorableString("stringParam", "");
            urlParamJSU = new JSONStorableUrl("urlPAram", "");
            stringChooserParamJSSC = new JSONStorableStringChooser("stringChooseParam", null, "", "Target Value:");
            audioClipTypeJSSC = new JSONStorableStringChooser("audioClipType", new List<string>() { "Embedded", "URL" }, "Embedded", "Clip Type", AudioClipTypeUpdate);
            audioClipCatJSSC = new JSONStorableStringChooser("audioClipCat", null, "FemaleBreath", "Clip Category", AudioClipCatUpdate);
            audioClipJSSC = new JSONStorableStringChooser("audioClip", null, "FemGasp03", "Clip Name");

            if (UIAGlobals.storableCataloguesExist)
            {
                RefreshReceiverNames();
                RefreshClipCats();
                RefreshClips();
            }


            RegisterParam(receiverNameJSSC);
            RegisterParam(targetNameJSSC);
            RegisterParam(boolToggleParamJSESC);
            RegisterParam(floatParamJSF);
            RegisterParam(colorParamJSC);
            RegisterParam(vector3ParamJSV3);
            RegisterParam(stringParamJSS);
            RegisterParam(urlParamJSU);
            RegisterParam(stringChooserParamJSSC);
            RegisterParam(audioClipTypeJSSC);
            RegisterParam(audioClipCatJSSC);
            RegisterParam(audioClipJSSC);

            if (UIAGlobals.storableCataloguesExist) ResetParams();
        }

        private void ResetParams()
        {
            string targetAtomType = targetComponent.targetAtomType;

            if (receiverNameJSSC.val!="" && targetNameJSSC.val != "")
            {
                paramStorableType = VAMStorablesCatalog.GetJSONStorableType(targetAtomType, receiverNameJSSC.val, targetNameJSSC.val);
                switch (paramStorableType)
                {
                    case JSONStorable.Type.Bool:
                        boolToggleParamJSESC.val = BoolToggle.toggle;
//                        VAMStorablesCatalog.ResetJSONParam(targetAtomType, receiverNameJSSC.val, targetNameJSSC.val, boolToggleParamJSSC);
                        break;
                    case JSONStorable.Type.Float:
                        VAMStorablesCatalog.ResetJSONParam(targetAtomType, receiverNameJSSC.val, targetNameJSSC.val, floatParamJSF);
                        break;
                    case JSONStorable.Type.Color:
                        VAMStorablesCatalog.ResetJSONParam(targetAtomType, receiverNameJSSC.val, targetNameJSSC.val, colorParamJSC);
                        break;
                    case JSONStorable.Type.Vector3:
                        VAMStorablesCatalog.ResetJSONParam(targetAtomType, receiverNameJSSC.val, targetNameJSSC.val, vector3ParamJSV3);
                        break;
                    case JSONStorable.Type.String:
                        VAMStorablesCatalog.ResetJSONParam(targetAtomType, receiverNameJSSC.val, targetNameJSSC.val, stringParamJSS);
                        break;
                    case JSONStorable.Type.Url:
                        VAMStorablesCatalog.ResetJSONParam(targetAtomType, receiverNameJSSC.val, targetNameJSSC.val, urlParamJSU);
                        break;
                    case JSONStorable.Type.StringChooser:
                    case JSONStorable.Type.StringChooserAction:
                        VAMStorablesCatalog.ResetJSONParam(targetAtomType, receiverNameJSSC.val, targetNameJSSC.val, stringChooserParamJSSC);
                        break;
                    case JSONStorable.Type.Action:
                    case JSONStorable.Type.PresetFilePathAction:
                    case JSONStorable.Type.AudioClipAction:                        
                        break;
                }
            }

        }

        public void RefreshReceiverNames()
        {
            if (UIAGlobals.storableCataloguesExist)
            {
                string targetAtomType = targetComponent.targetAtomType;

                JSONStorable.Type typeFilter = JSONStorable.Type.None;
                if (onlyFloatParam) typeFilter = JSONStorable.Type.Float;
                receiverNameJSSC.choices = VAMStorablesCatalog.GetStorableNames(targetAtomType, typeFilter);
                if (!receiverNameJSSC.choices.Contains(receiverNameJSSC.val))
                {
                    receiverNameJSSC.valNoCallback = "";
                    receiverNameJSSC.val = receiverNameJSSC.choices[0];
                }
            }

        }

        private void RefreshTargetNames()
        {
            string targetAtomType = targetComponent.targetAtomType;

            JSONStorable.Type typeFilter = JSONStorable.Type.None;
            if (onlyFloatParam) typeFilter = JSONStorable.Type.Float;
            targetNameJSSC.choices = VAMStorablesCatalog.GetParamNames(targetAtomType, receiverNameJSSC.val, typeFilter);
            if (!targetNameJSSC.choices.Contains(targetNameJSSC.val))
            {
                targetNameJSSC.valNoCallback = "";
                targetNameJSSC.val = targetNameJSSC.choices[0];
            }

        }

        private void ReceiverNameUpdate(string recieverName)
        {
            RefreshTargetNames();
            ResetParams();
        }

        private void TargetNameUpdate(string targetName)
        {
            ResetParams();
        }

        private void AudioClipTypeUpdate(string clipType)
        {
            RefreshClipCats();
        }

        private void AudioClipCatUpdate(string clipCat)
        {
            RefreshClips();
        }

        private void RefreshClipCats()
        {
            if (audioClipTypeJSSC.val == "URL") audioClipCatJSSC.choices = new List<string>() { "None" ,"web" };
            else audioClipCatJSSC.choices = EmbeddedAudioClipManager.singleton.GetCategories();

            if (!audioClipCatJSSC.choices.Contains(audioClipCatJSSC.val))
            {
                audioClipCatJSSC.valNoCallback = "";
                audioClipCatJSSC.val = audioClipCatJSSC.choices[0];
            }
        }

        private void RefreshClips()
        {
            if (audioClipCatJSSC.val == "None")  audioClipJSSC.choices = new List<string>() { "None"};
            else
            {
                List<string> audioClipChoices = new List<string>();
                if (audioClipCatJSSC.val == "web")
                {
                    foreach (string clipName in URLAudioClipManager.singleton.GetCategoryClips("web").Select((clip) => clip.uid).ToList()) { audioClipChoices.Add(clipName); }
                }
                else if (audioClipCatJSSC.val != "")
                {
                    foreach (string clipName in EmbeddedAudioClipManager.singleton.GetCategoryClips(audioClipCatJSSC.val).Select((clip) => clip.uid).ToList())
                    {
                        audioClipChoices.Add(clipName);
                    }
                }
                if (audioClipChoices.Count == 0) audioClipChoices.Add("None");

                audioClipJSSC.choices = audioClipChoices;                
            }
            if (!audioClipJSSC.choices.Contains(audioClipJSSC.val))
            {
                audioClipJSSC.valNoCallback = "";
                audioClipJSSC.val = audioClipJSSC.choices[0];
            }
        }

        public JSONStorableFloat GetTargetFloatParam(Atom atom)
        {
            if (UIAGlobals.storableCataloguesExist)
            {
                var storable = atom.GetStorableByID(receiverNameJSSC.val);
                if (storable != null)
                {
                    var targetJSF = storable.GetFloatJSONParam(targetNameJSSC.val);
                    return targetJSF;
                }
            }
            return null;
        }

        public void TriggerAction(Atom atom)
        {
            if (UIAGlobals.storableCataloguesExist)
            {
                var storable = atom.GetStorableByID(receiverNameJSSC.val);
                if (storable != null)
                {
                    paramStorableType = VAMStorablesCatalog.GetJSONStorableType(targetAtomType, receiverNameJSSC.val, targetNameJSSC.val);
                    switch (paramStorableType)
                    {
                        case JSONStorable.Type.Bool:
                            var targetJSB = storable.GetBoolJSONParam(targetNameJSSC.val);
                            if (targetJSB != null)
                            {
                                if (boolToggleParamJSESC.val == BoolToggle.toggle) targetJSB.val = !targetJSB.val;
                                else if (boolToggleParamJSESC.val == BoolToggle.setTrue) targetJSB.val = true;
                                else targetJSB.val = false;
                            }
                            break;
                        case JSONStorable.Type.Float:
                            var targetJSF = storable.GetFloatJSONParam(targetNameJSSC.val);
                            if (targetJSF != null) targetJSF.val = floatParamJSF.val;
                            break;
                        case JSONStorable.Type.Color:
                            var targetJSC = storable.GetColorJSONParam(targetNameJSSC.val);
                            if (targetJSC != null)
                            {
                                var color = new HSVColor();
                                color.H = colorParamJSC.val.H; color.V = colorParamJSC.val.V; color.S = colorParamJSC.val.S;
                                targetJSC.val = color;
                            }
                            break;
                        case JSONStorable.Type.Vector3:
                            // Do nothing - theres only one occurence of this in VAM and its not supported by VAM trigger system (theoretically UIA could support it)
                            break;
                        case JSONStorable.Type.String:
                            var targetJSS = storable.GetStringJSONParam(targetNameJSSC.val);
                            if (targetJSS != null) targetJSS.val = stringParamJSS.val;
                            break;
                        case JSONStorable.Type.Url:
                            var targetJSU = storable.GetUrlJSONParam(targetNameJSSC.val);
                            if (targetJSU != null) targetJSU.val = urlParamJSU.val;
                            break;
                        case JSONStorable.Type.StringChooser:
                            var targetJSSC = storable.GetStringChooserJSONParam(targetNameJSSC.val);
                            if (targetJSSC != null) targetJSSC.val = stringChooserParamJSSC.val;
                            break;
                        case JSONStorable.Type.StringChooserAction:
                            var targetJSSCA = storable.GetStringChooserAction(targetNameJSSC.val);
                            if (targetJSSCA != null) targetJSSCA.actionCallback(stringChooserParamJSSC.val);
                            break;
                        case JSONStorable.Type.Action:
                            var targetJSA = storable.GetAction(targetNameJSSC.val);
                            targetJSA.actionCallback();
                            break;
                        case JSONStorable.Type.PresetFilePathAction:
                            //                       var pm = storable.GetComponentInChildren<PresetManager>();
                            var targetJSFPA = storable.GetPresetFilePathAction(targetNameJSSC.val);
                            //                       var targetJSPBP = storable.GetUrlJSONParam("presetBrowsePath");
                            targetJSFPA.Browse(null);
                            break;
                        case JSONStorable.Type.AudioClipAction:
                            var targetJSACA = storable.GetAudioClipAction(targetNameJSSC.val);

                            if (audioClipCatJSSC.val != "None" && audioClipCatJSSC.val != "" && audioClipJSSC.val != "None" && audioClipJSSC.val != "") targetJSACA.actionCallback(GetAudioClip(audioClipCatJSSC.val, audioClipJSSC.val));
                            break;

                    }
                }
            }
                
      
        }

        private NamedAudioClip GetAudioClip(string audioCategory, string clipName)
        {
            NamedAudioClip clip = null;
            if (audioCategory == "web") { clip = URLAudioClipManager.singleton.GetClip(clipName); }
            else { clip = EmbeddedAudioClipManager.singleton.GetClip(clipName); }
            return clip;
        }
    }


    public class UserPreferencesComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public JSONStorableEnumStringChooser shaderQualityJSEnum;
        public JSONStorableEnumStringChooser msaaLevelJSEnum;

        public JSONStorableEnumStringChooser pixelLightCountJSEnum;
        public JSONStorableEnumStringChooser smoothPassesJSEnum;
        public JSONStorableEnumStringChooser glowEffectsJSEnum;
        public JSONStorableEnumStringChooser physicsRateJSEnum;
        public JSONStorableEnumStringChooser physicsUpdateCapJSEnum;

        public JSONStorableBool softbodyPhysicsJSB;
        public JSONStorableBool desktopVSyncJSB;
        public JSONStorableBool realtimeReflectionProbesJSB;
        public JSONStorableBool mirrorReflectionsJSB;
        public JSONStorableBool hqPhysicsJSB;

        public JSONStorableFloat renderScaleJSF;

        public JSONStorableFloat playerHeightAdjustJSF;

        public JSONStorableEnumStringChooser navigateLockHeightJSEnum;
        public JSONStorableEnumStringChooser freeMoveFollowFloorJSEnum;
        public JSONStorableEnumStringChooser disableAllNavJSEnum;
        public JSONStorableEnumStringChooser disableGrabNavJSEnum;
        public JSONStorableEnumStringChooser disableTeleportJSEnum;
        public JSONStorableEnumStringChooser teleportAllowRotJSEnum;
        public JSONStorableFloat freeMoveMultiplierJSF;
        public JSONStorableFloat grabNavPosMultJSF;
        public JSONStorableFloat grabNavRotMultJSF;

        public UserPreferencesComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;

            shaderQualityJSEnum = new JSONStorableEnumStringChooser("shaderQuality", UserPrefShaderQuality.enumManifestName, UserPrefShaderQuality.high, "Shader Quality");
            msaaLevelJSEnum = new JSONStorableEnumStringChooser("msaaLevel", UserPrefMSAALevel.enumManifestName, UserPrefMSAALevel.x8, "MSAA Level");
            pixelLightCountJSEnum = new JSONStorableEnumStringChooser("pixelLightCount", UserPrefPixelLightCount.enumManifestName, UserPrefPixelLightCount.two, "Pixel Light Count");
            smoothPassesJSEnum = new JSONStorableEnumStringChooser("smoothPasses", UserPrefSmoothPasses.enumManifestName, UserPrefSmoothPasses.two, "Smooth Passes");

            glowEffectsJSEnum = new JSONStorableEnumStringChooser("glowEffects", UserPrefGlowEffects.enumManifestName, UserPrefGlowEffects.off, "Glow Effects");
            physicsRateJSEnum = new JSONStorableEnumStringChooser("physicsRate", UserPrefPhysicsRate.enumManifestName, UserPrefPhysicsRate.auto, "Physics Rate");
            physicsUpdateCapJSEnum = new JSONStorableEnumStringChooser("physicsUpdateCap", UserPrefPhysicsUpdateCap.enumManifestName, UserPrefPhysicsUpdateCap.two, "Physics Update Cap");

            softbodyPhysicsJSB = new JSONStorableBool("softbodyPhysics", true);
            desktopVSyncJSB = new JSONStorableBool("desktopVSync", true);
            realtimeReflectionProbesJSB = new JSONStorableBool("realtimeReflectionProbes", true);
            mirrorReflectionsJSB = new JSONStorableBool("mirrorReflections", true);
            hqPhysicsJSB = new JSONStorableBool("hqPhysics", true);

            renderScaleJSF = new JSONStorableFloat("renderScale", 1f, 0.5f, 2f);
            playerHeightAdjustJSF = new JSONStorableFloat("playerHeightAdjust", 0f, -10f, 10f);

            navigateLockHeightJSEnum = new JSONStorableEnumStringChooser("navigateLockHeight", BoolToggle.enumManifestName, BoolToggle.toggle, "Toggle or Set");
            freeMoveFollowFloorJSEnum = new JSONStorableEnumStringChooser("freeMoveFollowFloor", BoolToggle.enumManifestName, BoolToggle.toggle, "Toggle or Set");
            disableAllNavJSEnum = new JSONStorableEnumStringChooser("disableAllNav", BoolToggle.enumManifestName, BoolToggle.toggle, "Toggle or Set");
            disableGrabNavJSEnum = new JSONStorableEnumStringChooser("disableGrabNav", BoolToggle.enumManifestName, BoolToggle.toggle, "Toggle or Set");
            disableTeleportJSEnum = new JSONStorableEnumStringChooser("disableTeleport", BoolToggle.enumManifestName, BoolToggle.toggle, "Toggle or Set");
            teleportAllowRotJSEnum = new JSONStorableEnumStringChooser("teleportAllowRot", BoolToggle.enumManifestName, BoolToggle.toggle, "Toggle or Set");
            freeMoveMultiplierJSF = new JSONStorableFloat("freeMoveMultiplier", 1f, 0.2f, 8f, true);
            grabNavPosMultJSF = new JSONStorableFloat("grabNavPosMult", 0.5f, 0.2f, 4f, true);
            grabNavRotMultJSF = new JSONStorableFloat("grabNavRotMult", 0.5f, 0.1f, 2f, true);

            RegisterParam(shaderQualityJSEnum);
            RegisterParam(msaaLevelJSEnum);

            RegisterParam(pixelLightCountJSEnum);
            RegisterParam(smoothPassesJSEnum);
            RegisterParam(glowEffectsJSEnum);
            RegisterParam(physicsRateJSEnum);
            RegisterParam(physicsUpdateCapJSEnum);

            RegisterParam(softbodyPhysicsJSB);
            RegisterParam(desktopVSyncJSB);
            RegisterParam(realtimeReflectionProbesJSB);
            RegisterParam(mirrorReflectionsJSB);
            RegisterParam(hqPhysicsJSB);
            RegisterParam(renderScaleJSF);
            RegisterParam(playerHeightAdjustJSF);
            RegisterParam(navigateLockHeightJSEnum);
            RegisterParam(freeMoveFollowFloorJSEnum);
            RegisterParam(disableAllNavJSEnum);
            RegisterParam(disableGrabNavJSEnum);
            RegisterParam(disableTeleportJSEnum);
            RegisterParam(teleportAllowRotJSEnum);
            RegisterParam(freeMoveMultiplierJSF);
            RegisterParam(grabNavPosMultJSF);
            RegisterParam(grabNavRotMultJSF);
        }

        public void SetPlayerHeightAdjust()
        {
            SuperController.singleton.playerHeightAdjust = playerHeightAdjustJSF.val;
        }

        public void SetFreeMoveMultiplier()
        {
            SuperController.singleton.freeMoveMultiplier = freeMoveMultiplierJSF.val;
        }

        public void SetGrabNavPosMult()
        {
            SuperController.singleton.grabNavigationPositionMultiplier = grabNavPosMultJSF.val;
        }

        public void SetGrabNavRotMult()
        {
            SuperController.singleton.grabNavigationRotationMultiplier = grabNavRotMultJSF.val;
        }
        public void SetDisableAllNav()
        {
            if (disableAllNavJSEnum.val == BoolToggle.toggle) SuperController.singleton.disableAllNavigation = !SuperController.singleton.disableAllNavigation;
            if (disableAllNavJSEnum.val == BoolToggle.setTrue) SuperController.singleton.disableAllNavigation = true;
            if (disableAllNavJSEnum.val == BoolToggle.setFalse) SuperController.singleton.disableAllNavigation = false;
        }
        public void SetDisableGrabNav()
        {
            if (disableGrabNavJSEnum.val == BoolToggle.toggle) SuperController.singleton.disableGrabNavigation = !SuperController.singleton.disableGrabNavigation;
            if (disableGrabNavJSEnum.val == BoolToggle.setTrue) SuperController.singleton.disableGrabNavigation = true;
            if (disableGrabNavJSEnum.val == BoolToggle.setFalse) SuperController.singleton.disableGrabNavigation = false;
        }
        public void SetDisableTeleport()
        {
            if (disableTeleportJSEnum.val == BoolToggle.toggle) SuperController.singleton.disableTeleport = !SuperController.singleton.disableTeleport;
            if (disableTeleportJSEnum.val == BoolToggle.setTrue) SuperController.singleton.disableTeleport = true;
            if (disableTeleportJSEnum.val == BoolToggle.setFalse) SuperController.singleton.disableTeleport = false;
        }
        public void SetTeleportAllowRot()
        {
            if (teleportAllowRotJSEnum.val == BoolToggle.toggle) SuperController.singleton.teleportAllowRotation = !SuperController.singleton.teleportAllowRotation;
            if (teleportAllowRotJSEnum.val == BoolToggle.setTrue) SuperController.singleton.teleportAllowRotation = true;
            if (teleportAllowRotJSEnum.val == BoolToggle.setFalse) SuperController.singleton.teleportAllowRotation = false;
        }

        public void SetNavigateLockHeight()
        {
            if (navigateLockHeightJSEnum.val == BoolToggle.toggle) SuperController.singleton.lockHeightDuringNavigate = !SuperController.singleton.lockHeightDuringNavigate;
            if (navigateLockHeightJSEnum.val == BoolToggle.setTrue) SuperController.singleton.lockHeightDuringNavigate = true;
            if (navigateLockHeightJSEnum.val == BoolToggle.setFalse) SuperController.singleton.lockHeightDuringNavigate = false;
        }

        public void SetFreeMoveFollowFloor()
        {
            if (freeMoveFollowFloorJSEnum.val == BoolToggle.toggle) SuperController.singleton.freeMoveFollowFloor = !SuperController.singleton.freeMoveFollowFloor;
            if (freeMoveFollowFloorJSEnum.val == BoolToggle.setTrue) SuperController.singleton.freeMoveFollowFloor = true;
            if (freeMoveFollowFloorJSEnum.val == BoolToggle.setFalse) SuperController.singleton.freeMoveFollowFloor = false;
        }
    }


    public class ShowVRHandsComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public JSONStorableBool toggleShowLeftVRHandJSB;
        public JSONStorableBool toggleShowRightVRHandJSB;
        public JSONStorableEnumStringChooser leapHandsControlJSE;
        public JSONStorableBool enableLeapHandsJSB;

        public ShowVRHandsComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;

            toggleShowLeftVRHandJSB = new JSONStorableBool("toggleShowLeftVRHand", true);
            toggleShowRightVRHandJSB = new JSONStorableBool("toggleShowRightVRHand", true);
            var exclusion = new List<int>();
            exclusion.Add(LeftRight.neither);
            leapHandsControlJSE = new JSONStorableEnumStringChooser("leapHandsControl", LeftRight.enumManifestName, LeftRight.both, "Toggle VR Hand", exclusion);

            enableLeapHandsJSB = new JSONStorableBool("enableLeapHands", true);


            RegisterParam(toggleShowLeftVRHandJSB);
            RegisterParam(toggleShowRightVRHandJSB);
            RegisterParam(enableLeapHandsJSB);
            RegisterParam(leapHandsControlJSE);
        }
        public void SetLeapMotion()
        {
            if (leapHandsControlJSE.val == LeftRight.left || leapHandsControlJSE.val == LeftRight.both) UserPreferences.singleton.leapHandModelControl.leftHandEnabled = enableLeapHandsJSB.val;

            if (leapHandsControlJSE.val == LeftRight.right || leapHandsControlJSE.val == LeftRight.both) UserPreferences.singleton.leapHandModelControl.rightHandEnabled = enableLeapHandsJSB.val;
        }
        public void ToggleLeapMotion()
        {
            if (leapHandsControlJSE.val == LeftRight.left || leapHandsControlJSE.val == LeftRight.both) UserPreferences.singleton.leapHandModelControl.leftHandEnabled = !UserPreferences.singleton.leapHandModelControl.leftHandEnabled;

            if (leapHandsControlJSE.val == LeftRight.right || leapHandsControlJSE.val == LeftRight.both) UserPreferences.singleton.leapHandModelControl.rightHandEnabled = !UserPreferences.singleton.leapHandModelControl.rightHandEnabled;
        }
        public void ToggleVRHands()
        {
            if (toggleShowLeftVRHandJSB.val) SuperController.singleton.commonHandModelControl.leftHandEnabled = !SuperController.singleton.commonHandModelControl.leftHandEnabled;
            if (toggleShowRightVRHandJSB.val) SuperController.singleton.commonHandModelControl.rightHandEnabled = !SuperController.singleton.commonHandModelControl.rightHandEnabled;
        }

        public int GetShowVRHandButtonState()
        {
            if (toggleShowLeftVRHandJSB.val && !SuperController.singleton.commonHandModelControl.leftHandEnabled) return ButtonState.inactive;
            if (toggleShowRightVRHandJSB.val && !SuperController.singleton.commonHandModelControl.rightHandEnabled) return ButtonState.inactive;
            if (!toggleShowRightVRHandJSB.val && !toggleShowRightVRHandJSB.val) return ButtonState.inactive;

            return ButtonState.active;
        }
        public int GetToggleLeapMotionButtonState()
        {
            if (leapHandsControlJSE.val == LeftRight.left || leapHandsControlJSE.val == LeftRight.both)
            {
                if (!UserPreferences.singleton.leapHandModelControl.leftHandEnabled) return ButtonState.inactive;
            }
            if (leapHandsControlJSE.val == LeftRight.right || leapHandsControlJSE.val == LeftRight.both)
            {
                if (!UserPreferences.singleton.leapHandModelControl.rightHandEnabled) return ButtonState.inactive;
            }
            return ButtonState.active;
        }
    }

    public class SwitchUIAGridComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public JSONStorableInt switchUIAGridTargetJSI;

        private UIAButtonGrid gridRef = null;

        private static HashSet<SwitchUIAGridComponent> switchUIAGridComponents = new HashSet<SwitchUIAGridComponent>();

        public static void RefreshAllGridIndicies()
        {
            foreach (var switchGridComponent in switchUIAGridComponents ) switchGridComponent.RefreshGridIndex();
        }

        public static void RefreshAllGridReferences()
        {
            foreach (var switchGridComponent in switchUIAGridComponents) switchGridComponent.RefreshGridRef();
        }


        public SwitchUIAGridComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;

            switchUIAGridTargetJSI = new JSONStorableInt("swtichUIAScreenTarget", 1, GridTargetUpdatedCallback, 1, 1000);
            if (UIAStorables.buttonGridsList.Count>0) gridRef = UIAStorables.buttonGridsList[0];

            RegisterParam(switchUIAGridTargetJSI);

            switchUIAGridComponents.Add(this);
        }

        ~SwitchUIAGridComponent()
        {
            switchUIAGridComponents.Remove(this);
        }

        private void GridTargetUpdatedCallback(int value)
        {
            RefreshGridRef();
        }

        private void RefreshGridIndex()
        {
            if (gridRef == null) switchUIAGridTargetJSI.val = 0 ;

            switchUIAGridTargetJSI.valNoCallback = UIAStorables.buttonGridsList.IndexOf(gridRef)+1;
        }

        private void RefreshGridRef()
        {
            if (UIAStorables.buttonGridsList==null || UIAStorables.buttonGridsList.Count < switchUIAGridTargetJSI.val) gridRef = null;
            else gridRef = UIAStorables.buttonGridsList[switchUIAGridTargetJSI.val - 1];
        }
    }

    public class HairColorComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public JSONClass hairScalpControlJC;
        public JSONClass hairSimControlJC { get; set; }

        public HairColorComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;

        }

        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            JSONClass jc = base.GetJSON(jspLoadExclusions);
            if (hairScalpControlJC != null) jc["hairScalpControl"] = hairScalpControlJC;
            if (hairSimControlJC != null) jc["hairSimControl"] = hairSimControlJC;

            return jc;
        }

        public override void RestoreFromJSON(JSONClass jc, string _paramName = null)
        {
            base.RestoreFromJSON(jc, _paramName);

            if (jc["hairSimControl"] != null) hairSimControlJC = jc["hairSimControl"].AsObject;
            else hairSimControlJC = null;

            if (jc["hairScalpControl"] != null) hairScalpControlJC = jc["hairScalpControl"].AsObject;
            else hairScalpControlJC = null;

        }

        public void CopyFrom(HairColorComponent source)
        {
            base.CopyFrom(source);

            if (source.hairScalpControlJC != null) hairScalpControlJC = (JSONClass)JSONClass.Parse(source.hairScalpControlJC.ToString());
            else hairScalpControlJC = null;

            if (source.hairSimControlJC != null) hairSimControlJC = (JSONClass)JSONClass.Parse(source.hairSimControlJC.ToString());
            else hairSimControlJC = null;

        }

        public void SetHairColor(Atom atom)
        {
            if (hairScalpControlJC != null) UpdateScalpMaterials(atom, hairScalpControlJC);
            if (hairSimControlJC != null) UpdateHairMaterials(atom, hairSimControlJC);
        }

        public static void UpdateHairMaterialColorJC(JSONClass sourceJC, JSONClass targetJC)
        {
            targetJC["primarySpecularSharpness"].AsFloat = sourceJC["primarySpecularSharpness"].AsFloat;
            targetJC["secondarySpecularSharpness"].AsFloat = sourceJC["secondarySpecularSharpness"].AsFloat;
            targetJC["specularShift"].AsFloat = sourceJC["specularShift"].AsFloat;
            targetJC["randomColorPower"].AsFloat = sourceJC["randomColorPower"].AsFloat;
            targetJC["randomColorOffset"].AsFloat = sourceJC["randomColorOffset"].AsFloat;
            targetJC["diffuseSoftness"].AsFloat = sourceJC["diffuseSoftness"].AsFloat;
            targetJC["fresnelPower"].AsFloat = sourceJC["fresnelPower"].AsFloat;
            targetJC["fresnelAttenuation"].AsFloat = sourceJC["fresnelAttenuation"].AsFloat;
            targetJC["IBLFactor"].AsFloat = sourceJC["primarySpecularSharpness"].AsFloat;
            targetJC["normalRandomize"].AsFloat = sourceJC["normalRandomize"].AsFloat;
            targetJC["colorRolloff"].AsFloat = sourceJC["colorRolloff"].AsFloat;

            targetJC["rootColor"]["h"].AsFloat = sourceJC["rootColor"]["h"].AsFloat;
            targetJC["rootColor"]["s"].AsFloat = sourceJC["rootColor"]["s"].AsFloat;
            targetJC["rootColor"]["v"].AsFloat = sourceJC["rootColor"]["v"].AsFloat;

            targetJC["tipColor"]["h"].AsFloat = sourceJC["tipColor"]["h"].AsFloat;
            targetJC["tipColor"]["s"].AsFloat = sourceJC["tipColor"]["s"].AsFloat;
            targetJC["tipColor"]["v"].AsFloat = sourceJC["tipColor"]["v"].AsFloat;

            targetJC["specularColor"]["h"].AsFloat = sourceJC["specularColor"]["h"].AsFloat;
            targetJC["specularColor"]["s"].AsFloat = sourceJC["specularColor"]["s"].AsFloat;
            targetJC["specularColor"]["v"].AsFloat = sourceJC["specularColor"]["v"].AsFloat;
        }

        public static void UpdateHairScalpColorJC(JSONClass sourceJC, JSONClass targetJC)
        {
            targetJC["Specular Texture Offset"].AsFloat = sourceJC["Specular Texture Offset"].AsFloat;
            targetJC["Specular Intensity"].AsFloat = sourceJC["Specular Intensity"].AsFloat;
            targetJC["Gloss"].AsFloat = sourceJC["Gloss"].AsFloat;
            targetJC["Specular Fresnel"].AsFloat = sourceJC["Specular Fresnel"].AsFloat;
            targetJC["Gloss Texture Offset"].AsFloat = sourceJC["Gloss Texture Offset"].AsFloat;
            targetJC["Global Illumination Filter"].AsFloat = sourceJC["Global Illumination Filter"].AsFloat;
            targetJC["Alpha Adjust"].AsFloat = sourceJC["Alpha Adjust"].AsFloat;
            targetJC["Diffuse Texture Offset"].AsFloat = sourceJC["Diffuse Texture Offset"].AsFloat;

            targetJC["Diffuse Color"]["h"].AsFloat = sourceJC["Diffuse Color"]["h"].AsFloat;
            targetJC["Diffuse Color"]["s"].AsFloat = sourceJC["Diffuse Color"]["s"].AsFloat;
            targetJC["Diffuse Color"]["v"].AsFloat = sourceJC["Diffuse Color"]["v"].AsFloat;

            targetJC["Specular Color"]["h"].AsFloat = sourceJC["Specular Color"]["h"].AsFloat;
            targetJC["Specular Color"]["s"].AsFloat = sourceJC["Specular Color"]["s"].AsFloat;
            targetJC["Specular Color"]["v"].AsFloat = sourceJC["Specular Color"]["v"].AsFloat;

            targetJC["Subsurface Color"]["h"].AsFloat = sourceJC["Subsurface Color"]["h"].AsFloat;
            targetJC["Subsurface Color"]["s"].AsFloat = sourceJC["Subsurface Color"]["s"].AsFloat;
            targetJC["Subsurface Color"]["v"].AsFloat = sourceJC["Subsurface Color"]["v"].AsFloat;
        }

        public static void UpdateHairMaterials(Atom atom, JSONNode hairProperties)
        {
            foreach (DAZHairGroup hairGroup in atom.GetComponentsInChildren<DAZHairGroup>())
            {
                HairSimControl hairControl = hairGroup.GetComponentInChildren<HairSimControl>();
                if (hairControl != null)
                {
                    hairControl.SetFloatParamValue("primarySpecularSharpness", hairProperties["primarySpecularSharpness"].AsFloat);
                    hairControl.SetFloatParamValue("secondarySpecularSharpness", hairProperties["secondarySpecularSharpness"].AsFloat);
                    hairControl.SetFloatParamValue("specularShift", hairProperties["specularShift"].AsFloat);
                    hairControl.SetFloatParamValue("randomColorPower", hairProperties["randomColorPower"].AsFloat);
                    hairControl.SetFloatParamValue("randomColorOffset", hairProperties["randomColorOffset"].AsFloat);
                    hairControl.SetFloatParamValue("diffuseSoftness", hairProperties["diffuseSoftness"].AsFloat);
                    hairControl.SetFloatParamValue("fresnelPower", hairProperties["fresnelPower"].AsFloat);
                    hairControl.SetFloatParamValue("fresnelAttenuation", hairProperties["fresnelAttenuation"].AsFloat);
                    hairControl.SetFloatParamValue("IBLFactor", hairProperties["IBLFactor"].AsFloat);
                    hairControl.SetFloatParamValue("normalRandomize", hairProperties["normalRandomize"].AsFloat);

                    JSONNode rootColorNode = hairProperties["rootColor"];
                    JSONNode tipColorNode = hairProperties["tipColor"];
                    JSONNode specColorNode = hairProperties["specularColor"];

                    HSVColor rootColor; rootColor.H = rootColorNode["h"].AsFloat; rootColor.S = rootColorNode["s"].AsFloat; rootColor.V = rootColorNode["v"].AsFloat;
                    HSVColor tipColor; tipColor.H = tipColorNode["h"].AsFloat; tipColor.S = tipColorNode["s"].AsFloat; tipColor.V = tipColorNode["v"].AsFloat;
                    HSVColor specColor; specColor.H = specColorNode["h"].AsFloat; specColor.S = specColorNode["s"].AsFloat; specColor.V = specColorNode["v"].AsFloat;

                    hairControl.SetColorParamValue("rootColor", rootColor);
                    hairControl.SetColorParamValue("tipColor", tipColor);
                    hairControl.SetFloatParamValue("colorRolloff", hairProperties["colorRolloff"].AsFloat);
                    hairControl.SetColorParamValue("specularColor", specColor);
                }
            }
        }
        public static void UpdateScalpMaterials(Atom atom, JSONNode scalpProperties)
        {
            var hairGroups = atom.GetComponentsInChildren<DAZHairGroup>();
            bool scalpApplied = false;
            foreach (DAZHairGroup hairGroup in hairGroups)
            {
                DAZHairGroupControl hairControl = hairGroup.GetComponentInChildren<DAZHairGroupControl>();
                HairSimControl simControl = hairGroup.GetComponentInChildren<HairSimControl>();

                if (hairControl != null && simControl != null)
                {
                    DAZSkinWrapMaterialOptions scalpControl = hairGroup.GetComponentInChildren<DAZSkinWrapMaterialOptions>();

                    float currentScalpAlpha = scalpControl.GetFloatParamValue("Alpha Adjust");

                    // Don't update anything if the current alpha is almost transparent OR this is the last hair item and we havent applied scalp yet.
                    if (currentScalpAlpha > -0.97f || (!scalpApplied && hairGroup==hairGroups.LastOrDefault()))
                    {
                        scalpApplied = true;
                        float specularTextureOffset = scalpProperties["Specular Texture Offset"].AsFloat;
                        float specularIntensity = scalpProperties["Specular Intensity"].AsFloat;
                        float gloss = scalpProperties["Gloss"].AsFloat;
                        float specularFresnel = scalpProperties["Specular Fresnel"].AsFloat;
                        float glossTextureOffset = scalpProperties["Gloss Texture Offset"].AsFloat;
                        float giFilter = scalpProperties["Global Illumination Filter"].AsFloat;
                        float alphaAdjust = scalpProperties["Alpha Adjust"].AsFloat;
                        float diffuseTextureOffset = scalpProperties["Diffuse Texture Offset"].AsFloat;

                        JSONNode diffuseColorNode = scalpProperties["Diffuse Color"];
                        JSONNode specularColorNode = scalpProperties["Specular Color"];
                        JSONNode subsurfaceColorNode = scalpProperties["Subsurface Color"];

                        HSVColor diffuseColor; diffuseColor.H = diffuseColorNode["h"].AsFloat; diffuseColor.S = diffuseColorNode["s"].AsFloat; diffuseColor.V = diffuseColorNode["v"].AsFloat;
                        HSVColor specularColor; specularColor.H = specularColorNode["h"].AsFloat; specularColor.S = specularColorNode["s"].AsFloat; specularColor.V = specularColorNode["v"].AsFloat;
                        HSVColor subsurfaceColor; subsurfaceColor.H = subsurfaceColorNode["h"].AsFloat; subsurfaceColor.S = subsurfaceColorNode["s"].AsFloat; subsurfaceColor.V = subsurfaceColorNode["v"].AsFloat;

                        scalpControl.SetFloatParamValue("Specular Texture Offset", specularTextureOffset);
                        scalpControl.SetFloatParamValue("Specular Intensity", specularIntensity);
                        scalpControl.SetFloatParamValue("Gloss", gloss);
                        scalpControl.SetFloatParamValue("Specular Fresnel", specularFresnel);
                        scalpControl.SetFloatParamValue("Gloss Texture Offset", glossTextureOffset);
                        scalpControl.SetFloatParamValue("Global Illumination Filter", giFilter);
                        scalpControl.SetFloatParamValue("Alpha Adjust", alphaAdjust);
                        scalpControl.SetFloatParamValue("Diffuse Texture Offset", diffuseTextureOffset);

                        scalpControl.SetColorParamValue("Diffuse Color", diffuseColor);
                        scalpControl.SetColorParamValue("Specular Color", specularColor);
                        scalpControl.SetColorParamValue("Subsurface Color", subsurfaceColor);
                    }
                }
            }
        }
    }

    public class PresetLockComponent : ButtonOperationComponentBase
    {
        public JSONStorableBool generalPresetLockJSON;
        public JSONStorableBool appPresetLockJSON;
        public JSONStorableBool posePresetLockJSON;
        public JSONStorableBool animationPresetLockJSON;
        public JSONStorableBool glutePhysPresetLockJSON;
        public JSONStorableBool breastPhysPresetLockJSON;
        public JSONStorableBool pluginPresetLockJSON;
        public JSONStorableBool skinPresetLockJSON;
        public JSONStorableBool morphPresetLockJSON;
        public JSONStorableBool hairPresetLockJSON;
        public JSONStorableBool clothingPresetLockJSON;

        public JSONStorableBool toggleOffKeepPresetLockJSON;
        public JSONStorableBool toggleOnKeepPresetLockJSON;

        public JSONStorableEnumStringChooser buttonTypeJSEnum;

        public PresetLockComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;
            generalPresetLockJSON = new JSONStorableBool("generalPresetLock", false);
            appPresetLockJSON = new JSONStorableBool("appPresetLock", false);
            posePresetLockJSON = new JSONStorableBool("posePresetLock", false);
            animationPresetLockJSON = new JSONStorableBool("animationPresetLock", false);
            glutePhysPresetLockJSON = new JSONStorableBool("glutePhysPresetLock", true);
            breastPhysPresetLockJSON = new JSONStorableBool("breastPhysPresetLock", true);
            pluginPresetLockJSON = new JSONStorableBool("pluginPresetLock", false);
            skinPresetLockJSON = new JSONStorableBool("skinPresetLock", true);
            morphPresetLockJSON = new JSONStorableBool("morphPresetLock", true);
            hairPresetLockJSON = new JSONStorableBool("hairPresetLock", true);
            clothingPresetLockJSON = new JSONStorableBool("clothingPresetLock", false);

            toggleOffKeepPresetLockJSON = new JSONStorableBool("toggleOffKeepPresetLock", true);
            toggleOnKeepPresetLockJSON = new JSONStorableBool("toggleOnKeepPresetLock", true);

            RegisterParam(generalPresetLockJSON);
            RegisterParam(appPresetLockJSON);
            RegisterParam(posePresetLockJSON);
            RegisterParam(animationPresetLockJSON);
            RegisterParam(glutePhysPresetLockJSON);
            RegisterParam(breastPhysPresetLockJSON);
            RegisterParam(pluginPresetLockJSON);
            RegisterParam(skinPresetLockJSON);
            RegisterParam(morphPresetLockJSON);
            RegisterParam(hairPresetLockJSON);
            RegisterParam(clothingPresetLockJSON);

            RegisterParam(toggleOffKeepPresetLockJSON);
            RegisterParam(toggleOnKeepPresetLockJSON);
        }

        public int GetPresetLockAtomState(Atom atom)
        {
            int state = ButtonState.active;

            List<PresetManagerControl> pmControlList = atom.presetManagerControls;
            foreach (PresetManagerControl pmc in pmControlList)
            {
                if ((pmc.name == "geometry" && generalPresetLockJSON.val && !pmc.lockParams) ||
                    (pmc.name == "AppearancePresets" && appPresetLockJSON.val && !pmc.lockParams) ||
                    (pmc.name == "PosePresets" && posePresetLockJSON.val && !pmc.lockParams) ||
                    (pmc.name == "AnimationPresets" && animationPresetLockJSON.val && !pmc.lockParams) ||
                    (pmc.name == "FemaleGlutePhysicsPresets" && glutePhysPresetLockJSON.val && !pmc.lockParams) ||
                    (pmc.name == "FemaleBreastPhysicsPresets" && breastPhysPresetLockJSON.val && !pmc.lockParams) ||
                    (pmc.name == "PluginPresets" && pluginPresetLockJSON.val && !pmc.lockParams) ||
                    (pmc.name == "SkinPresets" && skinPresetLockJSON.val && !pmc.lockParams) ||
                    (pmc.name == "MorphPresets" && morphPresetLockJSON.val && !pmc.lockParams) ||
                    (pmc.name == "HairPresets" && hairPresetLockJSON.val && !pmc.lockParams) ||
                    (pmc.name == "ClothingPresets" && clothingPresetLockJSON.val && !pmc.lockParams)
                    )
                {
                    state = ButtonState.inactive;
                    break;
                }
            }
            if (state == ButtonState.active && toggleOnKeepPresetLockJSON.val && !atom.keepParamLocksWhenPuttingBackInPoolJSON.val) state = ButtonState.inactive;

            return (state);
        }
        public void ActionTogglePresetLock(Atom atom, int currentState)
        {
            bool switchOn = false;
            if (currentState == 1) switchOn = true;
            List<PresetManagerControl> pmControlList = atom.presetManagerControls;
            foreach (PresetManagerControl pmc in pmControlList)
            {
                if ((pmc.name == "geometry" && generalPresetLockJSON.val) ||
                    (pmc.name == "AppearancePresets" && appPresetLockJSON.val) ||
                    (pmc.name == "PosePresets" && posePresetLockJSON.val) ||
                    (pmc.name == "AnimationPresets" && animationPresetLockJSON.val) ||
                    (pmc.name == "FemaleGlutePhysicsPresets" && glutePhysPresetLockJSON.val) ||
                    (pmc.name == "FemaleBreastPhysicsPresets" && breastPhysPresetLockJSON.val) ||
                    (pmc.name == "PluginPresets" && pluginPresetLockJSON.val) ||
                    (pmc.name == "SkinPresets" && skinPresetLockJSON.val) ||
                    (pmc.name == "MorphPresets" && morphPresetLockJSON.val) ||
                    (pmc.name == "HairPresets" && hairPresetLockJSON.val) ||
                    (pmc.name == "ClothingPresets" && clothingPresetLockJSON.val)
                    )
                {
                    pmc.lockParams = switchOn;
                }
            }
            if (currentState == 1 && toggleOnKeepPresetLockJSON.val) atom.keepParamLocksWhenPuttingBackInPoolJSON.val = true;
            else if (currentState == 2 && toggleOffKeepPresetLockJSON.val) atom.keepParamLocksWhenPuttingBackInPoolJSON.val = false;
        }
    }

    public class SkinPresetDecalComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public JSONStorableBool decalFaceLoadJSBool;
        public JSONStorableBool decalBodyLoadJSBool;
        public JSONStorableBool decalLimbsLoadJSBool;
        public JSONStorableBool decalGenLoadJSBool;

        public SkinPresetDecalComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;

            decalFaceLoadJSBool = new JSONStorableBool("decalFaceLoad", false);
            RegisterParam(decalFaceLoadJSBool);
            decalBodyLoadJSBool = new JSONStorableBool("decalBodyLoad", false);
            RegisterParam(decalBodyLoadJSBool);
            decalLimbsLoadJSBool = new JSONStorableBool("decalLimbsLoad", false);
            RegisterParam(decalLimbsLoadJSBool);
            decalGenLoadJSBool = new JSONStorableBool("decalGenLoad", false);
            RegisterParam(decalGenLoadJSBool);
        }

        public bool AnyDecalLoadsActive()
        {
            return decalFaceLoadJSBool.val || decalBodyLoadJSBool.val || decalLimbsLoadJSBool.val || decalGenLoadJSBool.val;
        }
    }

    public class PluginsLoadComponent : ButtonOperationComponentBase
    {
        public readonly List<ButtonPluginLoad> pluginLoadList;

        public JSONStorableBool useLatestVARJSBool;
        public JSONStorableBool openPluginUIOnLoadJSB;
        public JSONStorableBool removePluginsPostLoadJSB;

        public JSONStorableStringChooser browserAssistResourceTypeJSSC;

        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public PluginsLoadComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;
            pluginLoadList = new List<ButtonPluginLoad>();

            useLatestVARJSBool = new JSONStorableBool("useLatestVAR", true);
            RegisterParam(useLatestVARJSBool);

            openPluginUIOnLoadJSB = new JSONStorableBool("openPluginUIOnLoad", false);
            RegisterParam(openPluginUIOnLoadJSB);

            removePluginsPostLoadJSB = new JSONStorableBool("removePluginsPostLoad", false);
            RegisterParam(removePluginsPostLoadJSB);

            browserAssistResourceTypeJSSC = new JSONStorableStringChooser("browserAssistResourceType", null, "Open UI: Scenes", "BA Resource Type");
            RegisterParam(browserAssistResourceTypeJSSC);

            ButtonPluginLoad tempPIL = new ButtonPluginLoad();
            pluginLoadList.Add(tempPIL);
        }

        public void RefreshBAResourceTypes()
        {
            var choices = new List<string>();

            foreach (var action in UIAPluginInterop.openBrowserAssistResourceTypeUIJSActions)
            {
                choices.Add(action.name);
            }

            if (choices.Count == 0) SuperController.LogMessage("UIA: Integration with BrowserAssist is not established - is it loaded as a session plugin?");

            browserAssistResourceTypeJSSC.choices = choices;
        }

        public void CopyFrom(PluginsLoadComponent sourcePluginsLoadComponent)
        {
            base.CopyFrom(sourcePluginsLoadComponent);
            pluginLoadList.Clear();
            sourcePluginsLoadComponent.pluginLoadList.ForEach((sourcePluginLoad) =>
            {
                ButtonPluginLoad pluginLoad = new ButtonPluginLoad();
                pluginLoad.CopyFrom(sourcePluginLoad);
                pluginLoadList.Add(pluginLoad);
            });

        }

        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            JSONClass jc = base.GetJSON(jspLoadExclusions);

            JSONArray pluginLoadArray = new JSONArray();
            pluginLoadList.ForEach((pluginLoad) =>
            {
                if (pluginLoad.filePathJSString.val != "") pluginLoadArray.Add(pluginLoad.GetJSON());
            });

            jc["pluginLoads"] = pluginLoadArray;

            return (jc);
        }

        public void LoadJSON(JSONClass pluginLoadsComponentJSON, string uiapPackageName)
        {
            base.RestoreFromJSON(pluginLoadsComponentJSON);

            JSONArray pluginLoadsArrayJSON = pluginLoadsComponentJSON["pluginLoads"].AsArray;

            for (int i = 0; i < pluginLoadsArrayJSON.Count; i++)
            {
                JSONClass pluginLoadJSON = pluginLoadsArrayJSON[i].AsObject;
                ButtonPluginLoad pluginLoad;
                if (i >= pluginLoadList.Count)
                {
                    pluginLoad = new ButtonPluginLoad();
                    pluginLoadList.Add(pluginLoad);
                }
                else pluginLoad = pluginLoadList[i];

                pluginLoad.LoadJSON(pluginLoadJSON, uiapPackageName);
            }
        }

        public void LoadPluginsAsPreset(Atom atom)
        {
            int pluginCount = 1;
            MVRPluginManager manager;

            if (atom.type == "SessionPluginManager") manager = UIAGlobals.mvrScript.manager;
            else manager = atom.GetStorableByID("PluginManager") as MVRPluginManager;

            JSONClass currentPlugins = manager.GetJSON(true, true, true);

            JSONClass pluginPresetJSON = new JSONClass();
            pluginPresetJSON["setUnlistedParamsToDefault"].AsBool = true;

            JSONArray pluginPresetStorablesJSON = new JSONArray();
            pluginPresetJSON["storables"] = pluginPresetStorablesJSON;

            JSONClass pluginMgrStorableJSON = new JSONClass();
            pluginPresetStorablesJSON.Add(pluginMgrStorableJSON);
            pluginMgrStorableJSON["id"] = "PluginManager";

            JSONClass pluginMgrPluginsListJSON = new JSONClass();
            pluginMgrStorableJSON["plugins"] = pluginMgrPluginsListJSON;

            List<string> pluginRefsOfFirstLoadedPlugin = new List<string>();
            string firstPluginFilePath = "";

            List<string> preLoadedPluginRefs =null;
            if (removePluginsPostLoadJSB.val) preLoadedPluginRefs = PluginUtils.GetAllPluginRefs(manager);

            foreach (ButtonPluginLoad buttonPluginToLoad in pluginLoadList)
            {
                if (buttonPluginToLoad.filePathJSString.val != "" && buttonPluginToLoad.filePathJSString.val != null)
                {
                    string normalFilePath = SuperController.singleton.NormalizePath(buttonPluginToLoad.filePathJSString.val);

                    bool isPluginLoaded = PluginUtils.IsPluginLoaded(currentPlugins, normalFilePath);

                    if (useLatestVARJSBool.val) normalFilePath = FileUtils.GetLatestVARPath(normalFilePath);

                    if (pluginLoadList.First() == buttonPluginToLoad)
                    {
                        pluginRefsOfFirstLoadedPlugin = PluginUtils.GetPluginRefsOfType(manager, normalFilePath);
                        firstPluginFilePath = normalFilePath;
                    }
                    

                    if (!buttonPluginToLoad.singleInstancePerAtomJSBool.val || buttonTypeJSEnum.val == UIAButtonOpType.spawnAtom || !isPluginLoaded)
                    {
                        pluginMgrPluginsListJSON["plugin#" + pluginCount.ToString()] = normalFilePath;

                        if (buttonPluginToLoad.pluginSaveDataJSString.val != "" && buttonPluginToLoad.pluginSaveDataJSString.val != null)
                        {
                            foreach (JSONClass newPluginStorable in buttonPluginToLoad.pluginSaveJSON)
                            {
                                string pluginStorableID = newPluginStorable["id"].Value;
                                int pluginIDEndIndex = pluginStorableID.IndexOf("_");
                                newPluginStorable["id"].Value = "plugin#" + pluginCount.ToString() + pluginStorableID.Substring(pluginIDEndIndex, pluginStorableID.Length - pluginIDEndIndex);
                                pluginPresetStorablesJSON.Add(newPluginStorable);
                            }
                        }
                        pluginCount++;
                    }
                    else if (isPluginLoaded && buttonPluginToLoad.singleInstancePerAtomJSBool.val && buttonPluginToLoad._restorePluginDataIfLoaded)
                    {
                        List<string> pluginRefsInAtom = PluginUtils.GetPluginRefsOfType(manager, normalFilePath);
                        if (pluginRefsInAtom.Count > 0)
                        {
                            if (buttonPluginToLoad.pluginSaveDataJSString.val != "" && buttonPluginToLoad.pluginSaveDataJSString.val != null)
                            {
                                foreach (JSONClass newPluginStorable in buttonPluginToLoad.pluginSaveJSON)
                                {
                                    string pluginStorableID = newPluginStorable["id"].Value;
                                    int pluginIDEndIndex = pluginStorableID.IndexOf("_");
                                    newPluginStorable["id"].Value = pluginRefsInAtom[0].ToString() + pluginStorableID.Substring(pluginIDEndIndex, pluginStorableID.Length - pluginIDEndIndex);
                                    JSONStorable pluginStorable = atom.GetStorableByID(newPluginStorable["id"].Value);
                                    pluginStorable.RestoreFromJSON(newPluginStorable, true, true);
                                }
                            }
                        }
                    }
                }

            }

            if (pluginCount > 1) MergePluginPreset(atom, pluginPresetJSON);

            if (firstPluginFilePath != "" && openPluginUIOnLoadJSB.val)
            {
                var finalPluginRefsOfFirstLoadedPlugin = PluginUtils.GetPluginRefsOfType(manager, firstPluginFilePath);

                int pluginSlot;

                if (finalPluginRefsOfFirstLoadedPlugin.Except(pluginRefsOfFirstLoadedPlugin).Count()==0) pluginSlot = int.Parse(finalPluginRefsOfFirstLoadedPlugin.First().Substring(7));
                else pluginSlot = int.Parse(finalPluginRefsOfFirstLoadedPlugin.Except(pluginRefsOfFirstLoadedPlugin).First().Substring(7));

                SelectAndOpenUI(atom, pluginSlot);
            }
            if (removePluginsPostLoadJSB.val) UIAGlobals.mvrScript.StartCoroutine( RemoveLoadedPlugins(manager, preLoadedPluginRefs));
        }
        static private IEnumerator RemoveLoadedPlugins(MVRPluginManager manager, List<string> preLoadedPluginRefs)
        {
            if (preLoadedPluginRefs==null) preLoadedPluginRefs = new List<string>();

            List<string> pluginRefs = PluginUtils.GetAllPluginRefs(manager).Except(preLoadedPluginRefs).ToList();

            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();

            foreach (string pluginRef in pluginRefs) manager.RemovePluginWithUID(pluginRef);
        }


        static public void SelectAndOpenUI(Atom atom, string pluginRef)
        {
            if (atom.name == "CoreControl" || atom.type == "SessionPluginManager")
            {
                SuperController.singleton.ShowMainHUDAuto();
                SuperController.singleton.activeUI = SuperController.ActiveUI.MainMenu;
            }
            else
            {
                SuperController.singleton.SelectController(atom.mainController, false, false, true);
                SuperController.singleton.ShowMainHUDAuto();
            }

            SuperController.singleton.StartCoroutine(WaitForUI(atom, pluginRef));
        }

        static public void SelectAndOpenUI(Atom atom, int pluginSlot)
        {
            string pluginRef = "plugin#" + pluginSlot.ToString() + "_";
            SelectAndOpenUI(atom, pluginRef);
        }

        static private IEnumerator WaitForUI(Atom atom, string pluginRef)
        {
            var expiration = Time.unscaledTime + 1f;
            UITabSelector selector;
            while (Time.unscaledTime < expiration)
            {
                yield return 0;
                if (atom.name == "CoreControl" || atom.type == "SessionPluginManager") selector = SuperController.singleton.mainMenuTabSelector;
                else selector = atom.gameObject.GetComponentInChildren<UITabSelector>();
                if (selector == null) continue;

                if (atom.type == "SessionPluginManager") SuperController.singleton.mainMenuTabSelector.SetActiveTab("TabSessionPlugins");
                else if (atom.name == "CoreControl") SuperController.singleton.mainMenuTabSelector.SetActiveTab("TabScenePlugins");
                else selector.SetActiveTab("Plugins");

                IEnumerator enumerator1 = selector.transform.GetEnumerator();
                while (enumerator1.MoveNext())
                {
                    UITab component = ((Component)enumerator1.Current).GetComponent<UITab>();

                    if ((UnityEngine.Object)component != (UnityEngine.Object)null)
                    {
                        foreach (var scriptUI in component.GetComponentsInChildren<MVRScriptUI>())
                        {
                            
                            scriptUI.closeButton?.onClick.Invoke();
                        }

                        foreach (var scriptController in component.GetComponentsInChildren<MVRScriptControllerUI>())
                        {
                            if (scriptController.label.text.StartsWith(pluginRef))
                            {
                                scriptController.openUIButton?.onClick?.Invoke();
                                break;
                            }
                        }

                    }
                }

                yield break;
            }
        }



        public static void MergePluginPreset(Atom atom, JSONClass pluginPresetJSON)
        {
            PresetLockStore tempPresetLockStore = new PresetLockStore();
            if (atom.type == "Person") tempPresetLockStore.StorePresetLocks(atom, PresetLoadSettings.suppressPresetLocksJSB.val);

            PresetManager pm;
            if (atom.name == "CoreControl")
            {
                if (atom.type == "SessionPluginManager") pm = atom.GetComponentInChildren<PresetManager>();
                else
                {
                    JSONStorable js = atom.GetStorableByID("PluginManagerPresets");
                    pm = js.GetComponentInChildren<PresetManager>();
                }
            }
            else
            {
                if (atom.type != "Person") pm = atom.GetComponentInChildren<PresetManager>();
                else
                {
                    JSONStorable js = atom.GetStorableByID("PluginPresets");
                    pm = js.GetComponentInChildren<PresetManager>();
                }
            }
            atom.SetLastRestoredData(pluginPresetJSON, true, true);
            pm.LoadPresetFromJSON(pluginPresetJSON, true);

            if (atom.type == "Person") tempPresetLockStore.RestorePresetLocks(atom);
        }

        public void UnloadPlugins(Atom atom)
        {
            if (atom.type != "SessionPluginManager")
            {
                MVRPluginManager manager = atom.GetStorableByID("PluginManager") as MVRPluginManager;

                JSONClass emptyManager = new JSONClass();
                emptyManager["id"] = "PluginManager";
                manager.LateRestoreFromJSON(emptyManager);
            }

        }
    }

    public class ButtonPluginLoad : JSONStorableObject
    {
        public bool _restorePluginDataIfLoaded
        {
            get { return restorePluginDataIfLoadedJSBool.val; }
            set { restorePluginDataIfLoadedJSBool.val = value; }
        }
        public string _pluginSaveData
        {
            get { return pluginSaveDataJSString.val; }
            set { pluginSaveDataJSString.val = value; }
        }
        public JSONArray pluginSaveJSON;

        public JSONStorableString filePathJSString;
        public JSONStorableBool singleInstancePerAtomJSBool;
        public JSONStorableBool restorePluginDataIfLoadedJSBool;
        public JSONStorableString pluginSaveDataJSString;
        
        public ButtonPluginLoad()
        {
            filePathJSString = new JSONStorableString("filePath", "");
            RegisterParam(filePathJSString);
            singleInstancePerAtomJSBool = new JSONStorableBool("singleInstancePerAtom", true);
            RegisterParam(singleInstancePerAtomJSBool);
            restorePluginDataIfLoadedJSBool = new JSONStorableBool("restorePluginDataIfLoaded", false);
            RegisterParam(restorePluginDataIfLoadedJSBool);

            pluginSaveDataJSString = new JSONStorableString("saveDataSummary", "");
            RegisterParam(pluginSaveDataJSString, false);
        }

        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            JSONClass node = base.GetJSON(jspLoadExclusions);

            if (pluginSaveJSON != null) node["pluginSaveJSON"] = pluginSaveJSON.AsArray;
            return node;
        }
        public void CopyFrom(ButtonPluginLoad bpl)
        {
            base.CopyFrom(bpl);
            pluginSaveJSON = bpl.pluginSaveJSON;
        }

        public void LoadJSON(JSONClass pluginLoadJSON, string uiapPackageName)
        {
            base.RestoreFromJSON(pluginLoadJSON);
            pluginSaveJSON = new JSONArray();
            pluginSaveDataJSString.val = "";

            if (uiapPackageName != "" && !filePathJSString.val.Contains(":") && FileManagerSecure.FileExists(uiapPackageName + ":/" + filePathJSString.val)) filePathJSString.val = uiapPackageName + ":/" + filePathJSString.val;

            if (pluginLoadJSON["pluginSaveJSON"] != null)
            {
                pluginSaveJSON = pluginLoadJSON["pluginSaveJSON"].AsArray;
                string tempString = pluginSaveJSON.ToString();
                pluginSaveDataJSString.val = tempString.Substring(0, Math.Min(tempString.Length, 500));
            }
        }
    }
    public class PluginSettingComponent : ButtonOperationComponentBase
    {
        public JSONStorableStringChooser targetParamNameOnJSSC;
        public JSONStorableStringChooser targetParamNameOffJSSC;
        public JSONStorableStringChooser pluginTypeJSSC;
        public JSONStorableEnumStringChooser pluginMultiSelectModeJSEnum;

        public readonly PluginVariables pluginVariablesButtonOn;
        public readonly PluginVariables pluginVariablesButtonOff;

        public JSONStorableEnumStringChooser buttonTypeJSEnum;

        public Dictionary<string, int> atomButtonToggleStates;

        public PluginSettingComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;

            pluginTypeJSSC = new JSONStorableStringChooser("pluginType", null, "", "", PluginTypeUpdated);
            RegisterParam(pluginTypeJSSC);

            targetParamNameOnJSSC = new JSONStorableStringChooser("targetParamNameOn", null, "","");
            RegisterParam(targetParamNameOnJSSC);

            targetParamNameOffJSSC = new JSONStorableStringChooser("targetParamNameOff", null, "", "");
            RegisterParam(targetParamNameOffJSSC);

            pluginMultiSelectModeJSEnum = new JSONStorableEnumStringChooser("pluginMultiSelectMode", MultiOccurSelect.enumManifestName,MultiOccurSelect.allOccurences,"Plugin instances");
            RegisterParam(pluginMultiSelectModeJSEnum);

            pluginVariablesButtonOn = new PluginVariables(this);
            pluginVariablesButtonOff = new PluginVariables(this);

            atomButtonToggleStates = new Dictionary<string, int>();
        }

        public void ButtonTypeUpdated()
        {
            pluginTypeJSSC.choices = GetPluginTypeChoices();
            if (pluginTypeJSSC.val =="") pluginTypeJSSC.val = pluginTypeJSSC.choices[0]; 
        }
        private void PluginTypeUpdated(string pluginType)
        {
            pluginVariablesButtonOn._pluginVarDict.Clear();
            pluginVariablesButtonOff._pluginVarDict.Clear();
            RefreshPluginParamNameChoices();
        }

        public void RefreshPluginParamNameChoices()
        {
            List<string> targetParamNameChoices;
            if (buttonTypeJSEnum.val == UIAButtonOpType.pluginBoolFalse || buttonTypeJSEnum.val == UIAButtonOpType.pluginBoolTrue || buttonTypeJSEnum.val == UIAButtonOpType.pluginBoolToggle) targetParamNameChoices = GetTargetPluginParamNameChoices(PluginVariableType.vBool);
            else if (buttonTypeJSEnum.val == UIAButtonOpType.pluginAction || buttonTypeJSEnum.val == UIAButtonOpType.pluginActionToggle) targetParamNameChoices = GetTargetPluginParamNameChoices(PluginVariableType.vAction);
            else return;

            targetParamNameOnJSSC.choices = targetParamNameChoices;
            targetParamNameOffJSSC.choices = targetParamNameChoices;

            if (targetParamNameOnJSSC.val=="") targetParamNameOnJSSC.val = targetParamNameChoices[0];
            if (buttonTypeJSEnum.val == UIAButtonOpType.pluginActionToggle || buttonTypeJSEnum.val == UIAButtonOpType.pluginBoolToggle)
            {
                if (targetParamNameOffJSSC.val == "" || targetParamNameOffJSSC.val =="No Actions available") targetParamNameOffJSSC.val = targetParamNameChoices[0];
            }
            else targetParamNameOffJSSC.val = "";
        }


        public void AtomNameUpdate(string oldName, string newName)
        {
            foreach (string key in atomButtonToggleStates.Keys.ToList())
            {
                if (key == oldName)
                {
                    atomButtonToggleStates[newName] = atomButtonToggleStates[key];
                    atomButtonToggleStates.Remove(key);
                }
            }
        }

        public void AtomRemovedUpdate(string oldName)
        {
            foreach (string key in atomButtonToggleStates.Keys.ToList())
            {
                if (key == oldName) atomButtonToggleStates.Remove(key);
            }
        }

        public void CopyFrom(PluginSettingComponent pluginSettingComponent)
        {
            base.CopyFrom(pluginSettingComponent);

            pluginVariablesButtonOn.CopyFrom(pluginSettingComponent.pluginVariablesButtonOn);
            pluginVariablesButtonOff.CopyFrom(pluginSettingComponent.pluginVariablesButtonOff);

            atomButtonToggleStates.Clear();
        }

        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            JSONClass jc = base.GetJSON(jspLoadExclusions);
            if (buttonTypeJSEnum.val != UIAButtonOpType.pluginOpenUI && buttonTypeJSEnum.val != UIAButtonOpType.pluginToggleOpenUI && buttonTypeJSEnum.val != UIAButtonOpType.unloadSpecificPlugin)
            {
                jc["pluginVariablesOn"] = pluginVariablesButtonOn.GetJSON();
                jc["pluginVariablesOff"] = pluginVariablesButtonOff.GetJSON();
            }
            
            return (jc);
        }

        public void RestoreFromJSON(JSONClass pluginSettingComponentJSON, int uiapFormatVersion)
        {
            base.RestoreFromJSON(pluginSettingComponentJSON, null);

            if (buttonTypeJSEnum.val != UIAButtonOpType.pluginOpenUI && buttonTypeJSEnum.val != UIAButtonOpType.pluginToggleOpenUI && buttonTypeJSEnum.val != UIAButtonOpType.unloadSpecificPlugin)
            {
                if (pluginSettingComponentJSON["pluginVariablesOn"] != null) pluginVariablesButtonOn.RestoreFromJSON(pluginSettingComponentJSON["pluginVariablesOn"].AsObject, uiapFormatVersion);
                if (pluginSettingComponentJSON["pluginVariablesOff"] != null) pluginVariablesButtonOff.RestoreFromJSON(pluginSettingComponentJSON["pluginVariablesOff"].AsObject, uiapFormatVersion);
            }
                

            atomButtonToggleStates.Clear();
        }
        public List<string> GetTargetPluginParamNameChoices(int paramType)
        {
            List<string> choices = new List<string>();
            bool pluginOfTypeFound = false;

            foreach (MVRScript script in PluginUtils.GetSceneAndSessionPlugins())
            {
                if (script.name.EndsWith(pluginTypeJSSC.val))
                {
                    UpdateParamsFromPluginStorable(script, paramType, choices);
                    pluginOfTypeFound = true;
                }
            }

            if (choices.Count < 1)
            {
                if (!pluginOfTypeFound) choices.Add("No Plugin of type loaded");
                else
                {
                    if (paramType == PluginVariableType.vAction) { choices.Add("No Actions available"); }
                    else { choices.Add("No Bools available"); }
                }
            }
 //           choices.Sort();
            return (choices);
        }
        private void UpdateParamsFromPluginStorable(JSONStorable storable, int paramType, List<string> choices)
        {
            if (paramType == PluginVariableType.vAction)
            {
                foreach (string actionName in storable.GetActionNames())
                {
                    if (!UIAConsts.VAMSaveRestoreActions.Contains(actionName) && !choices.Contains(actionName)) { choices.Add(actionName); }
                }
            }
            else if (paramType == PluginVariableType.vBool)
            {
                foreach (string boolName in storable.GetBoolParamNames())
                {
                    if (!choices.Contains(boolName)) { choices.Add(boolName); }
                }
            }
            else if (paramType == PluginVariableType.vAll)
            {
                var allParamNames = storable.GetAllParamAndActionNames();
                var supportedParamTypeNames = new List<string>();

                supportedParamTypeNames.AddRange(storable.GetBoolParamNames());
                supportedParamTypeNames.AddRange(storable.GetFloatParamNames());
                supportedParamTypeNames.AddRange(storable.GetStringParamNames());
                supportedParamTypeNames.AddRange(storable.GetStringChooserParamNames());
                supportedParamTypeNames.AddRange(storable.GetColorParamNames());
                supportedParamTypeNames.AddRange(storable.GetUrlParamNames());

                foreach(string paramName in allParamNames)
                {
                    if (supportedParamTypeNames.Contains(paramName) && !choices.Contains(paramName)) choices.Add(paramName);
                }
            }
        }


        public List<string> GetPluginTypeChoices()
        {
            List<string> popupChoices = new List<string>();

            foreach (MVRScript script in PluginUtils.GetSceneAndSessionPlugins())
            {
                string pluginType = script.name.Substring(script.name.IndexOf('_') + 1);
                if (!popupChoices.Contains(pluginType)) popupChoices.Add(pluginType);
            }

            if (popupChoices.Count == 0) { popupChoices.Add("No plugins in Scene"); }
            popupChoices.Sort();
            return (popupChoices);
        }

        public float GetMinFloatValue(string varName)
        {
            float minFloat = 100000000000f;

            foreach (MVRScript script in PluginUtils.GetSceneAndSessionPlugins())
            {
                if (script.GetFloatParamNames().Contains(varName))
                {
                    JSONStorableFloat floatJSON = script.GetFloatJSONParam(varName);
                    if (floatJSON.min < minFloat) minFloat = floatJSON.min;
                }
            }
            if (minFloat == 100000000000f) minFloat = 0f;
            return (minFloat);
        }
        public float GetMaxFloatValue(string varName)
        {
            float maxFloat = -100000000000f;

            foreach (MVRScript script in PluginUtils.GetSceneAndSessionPlugins())
            {
                if (script.GetFloatParamNames().Contains(varName))
                {
                    JSONStorableFloat floatJSON = script.GetFloatJSONParam(varName);
                    if (floatJSON.max > maxFloat) maxFloat = floatJSON.max;
                }
            }
            if (maxFloat == -100000000000f) maxFloat = 1f;
            return (maxFloat);
        }

        public List<string> GetStringChooserChoices(string varName)
        {
            List<string> choices = new List<string>();

            foreach (MVRScript script in PluginUtils.GetSceneAndSessionPlugins())
            {
                if (script.GetStringChooserParamNames().Contains(varName))
                {
                    List<string> pluginSCChoices = script.GetStringChooserJSONParamChoices(varName);
                    if (pluginSCChoices != null)
                    {
                        foreach (string choice in pluginSCChoices)
                        {
                            if (!choices.Contains(choice)) choices.Add(choice);
                        }
                    }
                }
            }
            return (choices);
        }

        private void PreSettings(JSONStorable pluginStorable, bool toggleParamMode)
        {
            PluginVariables preActionVariables;

            if (!toggleParamMode || parentButton.GetButtonState() == 1) preActionVariables = pluginVariablesButtonOn;
            else preActionVariables = pluginVariablesButtonOff;

            // Set any pre-action plugin parameters
            foreach (KeyValuePair<string, PluginVariable> kvp in preActionVariables._pluginVarDict)
            {
                if (pluginStorable.GetBoolParamNames().Contains(kvp.Key))
                {
                    if (kvp.Key != targetParamNameOnJSSC.val)
                    {
                        JSONStorableBool boolJSON = pluginStorable.GetBoolJSONParam(kvp.Key);
                        boolJSON.val = kvp.Value.pluginBoolValue.val;
                    }

                }
                else if (pluginStorable.GetFloatParamNames().Contains(kvp.Key))
                {
                    JSONStorableFloat floatJSON = pluginStorable.GetFloatJSONParam(kvp.Key);
                    floatJSON.val = kvp.Value.pluginFloatValue.val;
                }
                else if (pluginStorable.GetStringParamNames().Contains(kvp.Key))
                {
                    JSONStorableString stringJSON = pluginStorable.GetStringJSONParam(kvp.Key);
                    stringJSON.val = kvp.Value.pluginStringValue.val;
                }
                else if (pluginStorable.GetStringChooserParamNames().Contains(kvp.Key))
                {
                    JSONStorableStringChooser stringChooserJSON = pluginStorable.GetStringChooserJSONParam(kvp.Key);
                    stringChooserJSON.val = kvp.Value.pluginStringValue.val;

                }
                else if (pluginStorable.GetUrlParamNames().Contains(kvp.Key))
                {
                    JSONStorableUrl urlJSON = pluginStorable.GetUrlJSONParam(kvp.Key);
                    urlJSON.val = kvp.Value.pluginStringValue.val;
                }
                else if (pluginStorable.GetColorParamNames().Contains(kvp.Key))
                {
                    JSONStorableColor colorJSON = pluginStorable.GetColorJSONParam(kvp.Key);
                    colorJSON.val = kvp.Value.pluginColorValue.val;
                }

            }
        }

        private List<MVRScript> GetPluginStorables(Atom atom)
        {
            List<MVRScript> pluginStorables = new List<MVRScript>();
            if (atom.type == "SessionPluginManager")
            {
                foreach (MVRScript script in PluginUtils.GetSessionPlugins())
                {
                    if (script.name.EndsWith(pluginTypeJSSC.val)) pluginStorables.Add(script);
                }
            }
            else
            {
                foreach (MVRScript pluginStorable in PluginUtils.GetPluginsFromAtom(atom))
                {
                    if (pluginStorable != null)
                    {
                        string storableName = pluginStorable.name;
                        if (storableName.EndsWith(pluginTypeJSSC.val)) pluginStorables.Add(pluginStorable);
                    }
                }
            }

            return (pluginStorables);
        }

        public void PluginBool(Atom atom, bool boolState, bool toggleParamMode)
        {
            List<MVRScript> pluginStorables = GetPluginStorables(atom);

            foreach (MVRScript storable in pluginStorables)
            {
                if (storable.GetBoolParamNames().Contains(targetParamNameOnJSSC.val))
                {
                    PreSettings(storable, toggleParamMode);
                    storable.SetBoolParamValue(targetParamNameOnJSSC.val, boolState);
                }
            }
        }
        public void PluginAction(Atom atom, bool singleActionMode, bool toggleParamMode)
        {
            string actionName = "";

            if (!toggleParamMode || parentButton.GetButtonState() == 1) actionName = targetParamNameOnJSSC.val;
            else actionName = targetParamNameOffJSSC.val;

            List<MVRScript> pluginStorables = GetPluginStorables(atom);

            foreach (MVRScript storable in pluginStorables)
            {
                if (storable != null && (!singleActionMode || storable.GetActionNames().Contains(actionName)))
                {
                    PreSettings(storable, toggleParamMode);
                    if (singleActionMode) storable.CallAction(actionName);
                }
            }

            if (toggleParamMode)
            {
                if (atomButtonToggleStates[atom.name] == ButtonState.active) atomButtonToggleStates[atom.name] = ButtonState.inactive;
                else atomButtonToggleStates[atom.name] = ButtonState.active;
            }
        }

        public void PluginOpenUI(Atom atom)
        {
            List<MVRScript> pluginStorables = GetPluginStorables(atom);
            if (pluginStorables.Count > 0)
            {

                PluginsLoadComponent.SelectAndOpenUI(atom, pluginStorables.First().name);
            }
        }

        public void PluginUnloadSpecific(Atom atom)
        {
            MVRPluginManager manager = atom.GetStorableByID("PluginManager") as MVRPluginManager;
            if (atom.type == "SessionPluginManager") manager = UIAGlobals.mvrScript.manager;

            List<MVRScript> pluginScripts = GetPluginStorables(atom);

            if (pluginScripts.Count == 0) return;

            if (pluginMultiSelectModeJSEnum.val == MultiOccurSelect.firstOccurence) manager.RemovePluginWithUID(pluginScripts.First().name.Substring(0, pluginScripts.First().name.IndexOf('_')));
            if (pluginMultiSelectModeJSEnum.val == MultiOccurSelect.lastOccurence) manager.RemovePluginWithUID(pluginScripts.Last().name.Substring(0, pluginScripts.Last().name.IndexOf('_')));

            if (pluginMultiSelectModeJSEnum.val == MultiOccurSelect.allOccurences || pluginMultiSelectModeJSEnum.val == MultiOccurSelect.allButFirstOccurence || pluginMultiSelectModeJSEnum.val == MultiOccurSelect.allButLastOccurence)
            {
                if (pluginMultiSelectModeJSEnum.val == MultiOccurSelect.allButLastOccurence) pluginScripts.RemoveLast();
                if (pluginMultiSelectModeJSEnum.val == MultiOccurSelect.allButFirstOccurence) pluginScripts.RemoveAt(0);

                foreach (MVRScript pluginScript in pluginScripts)
                {
                    manager.RemovePluginWithUID(pluginScript.name.Substring(0, pluginScript.name.IndexOf('_')));
                }
            }
            
        }

    }
    public class PluginVariables : JSONStorableObject
    {
        public Dictionary<string, PluginVariable> _pluginVarDict;
        private PluginSettingComponent parentPluginSettingComponent;
        public PluginVariables(PluginSettingComponent _parentPluginSettingComponent)
        {
            try
            {
                parentPluginSettingComponent = _parentPluginSettingComponent;
                _pluginVarDict = new Dictionary<string, PluginVariable>();
            }
            catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
        }
        public void CopyFrom(PluginVariables pv)
        {
            if (pv != null)
            {
                base.CopyFrom(pv);

                _pluginVarDict = new Dictionary<string, PluginVariable>();
                foreach (KeyValuePair<string, PluginVariable> kvp in pv._pluginVarDict)
                {
                    PluginVariable newPV = new PluginVariable(kvp.Value.pluginVarName.val,kvp.Value.pluginVarType.val);
                    newPV.CopyFrom(kvp.Value);
                    _pluginVarDict.Add(kvp.Key, newPV);
                }
            }
        }

        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            JSONClass jc = base.GetJSON(jspLoadExclusions);
            JSONArray pluginVariablesArray = new JSONArray();

            foreach (KeyValuePair<string, PluginVariable> kvp in _pluginVarDict)
            {
                JSONClass variableNode = kvp.Value.GetJSON();
                pluginVariablesArray.Add(variableNode);
            }

            jc["pluginVariables"] = pluginVariablesArray;

            return jc;
        }

        public void RestoreFromJSON(JSONClass pluginVarJSON, int uiapFormatVersion)
        {
            base.RestoreFromJSON(pluginVarJSON);

            _pluginVarDict = new Dictionary<string, PluginVariable>();

            if (pluginVarJSON["pluginVariables"] != null)
            {
                JSONArray pluginVariablesArray = pluginVarJSON["pluginVariables"].AsArray;
                for (int i = 0; i < pluginVariablesArray.Count; i++)
                {
                    JSONClass variableNodeJSON = pluginVariablesArray[i].AsObject;
                    PluginVariable pluginVariable = new PluginVariable();
                    pluginVariable.RestoreFromJSON(variableNodeJSON, uiapFormatVersion);

                    _pluginVarDict.Add(variableNodeJSON["pluginVarName"], pluginVariable);
                }
            }
        }
        public List<string> GetPluginParamChoices()
        {
            List<string> displayParamChoices = new List<string>();
            List<string> paramChoices = parentPluginSettingComponent.GetTargetPluginParamNameChoices(PluginVariableType.vAll);

            foreach (string choice in paramChoices)
            {
                if (_pluginVarDict.ContainsKey(choice))
                {
                    displayParamChoices.Add("+ " + choice + " +");
                }
                else displayParamChoices.Add("  " + choice + "  ");

            }
            return (displayParamChoices);
        }

        public void SetPluginParamChoices(JSONStorableStringChooser jssc)
        {
            List<string> displayParamChoices = new List<string>();
            List<string> fullParamChoices = parentPluginSettingComponent.GetTargetPluginParamNameChoices(PluginVariableType.vAll);
            if (parentPluginSettingComponent.buttonTypeJSEnum.val == UIAButtonOpType.pluginBoolToggle || parentPluginSettingComponent.buttonTypeJSEnum.val == UIAButtonOpType.pluginBoolTrue || parentPluginSettingComponent.buttonTypeJSEnum.val == UIAButtonOpType.pluginBoolFalse)
            {
                if (fullParamChoices.Contains(parentPluginSettingComponent.targetParamNameOnJSSC.val)) fullParamChoices.Remove(parentPluginSettingComponent.targetParamNameOnJSSC.val);
            }

            List<string> notSetDisplayParamChoices = new List<string>();
            List<string> paramChoices = new List<string>();
            List<string> notSetParamChoices = new List<string>();

            foreach (string choice in fullParamChoices)
            {
                if (_pluginVarDict.ContainsKey(choice))
                {
                    paramChoices.Add(choice);
                    displayParamChoices.Add("+ " + choice + " +");
                }
                else
                {
                    notSetDisplayParamChoices.Add("  " + choice + "  ");
                    notSetParamChoices.Add(choice);
                }
            }
            foreach (KeyValuePair<string, PluginVariable> kvp in _pluginVarDict)
            {
                if (!paramChoices.Contains(kvp.Key) && !notSetParamChoices.Contains(kvp.Key))
                {
                    paramChoices.Add(kvp.Key);
                    displayParamChoices.Add("+ " + kvp.Key + " +");
                }
            }

            jssc.choices = paramChoices.Concat(notSetParamChoices).ToList();
            jssc.displayChoices = displayParamChoices.Concat(notSetDisplayParamChoices).ToList();
        }

        public int GetVarType(string varName)
        {
            int varType = PluginVariableType.vNone;

            if (_pluginVarDict.ContainsKey(varName))
            {
                varType = _pluginVarDict[varName].pluginVarType.val;
            }

            return (varType);
        }
        public bool GetVarSet(string varName)
        {
            return (_pluginVarDict.ContainsKey(varName));
        }

        public int GetVarTypeFromPlugin(string varName)
        {
            int varType = PluginUtils.GetVarTypeFromPlugin(varName, parentPluginSettingComponent.pluginTypeJSSC.val);
            return (varType);
        }
        public float GetMinFloatValue(string varName)
        {
            float minFloat = parentPluginSettingComponent.GetMinFloatValue(varName);
            if (_pluginVarDict.ContainsKey(varName))
            {
                float varValue = _pluginVarDict[varName].pluginFloatValue.val;
                if (varValue < minFloat) minFloat = varValue;
            }
            return (minFloat);
        }
        public float GetMaxFloatValue(string varName)
        {
            float maxFloat = parentPluginSettingComponent.GetMaxFloatValue(varName);
            if (_pluginVarDict.ContainsKey(varName))
            {
                float varValue = _pluginVarDict[varName].pluginFloatValue.val;
                if (varValue > maxFloat) maxFloat = varValue;
            }
            return (maxFloat);
        }
        public List<string> GetStringChooserChoices(string varName)
        {
            List<string> choices = parentPluginSettingComponent.GetStringChooserChoices(varName);
            if (choices.Count < 1) choices.Add("NO VALUES AVAILABLE");
            return (choices);
        }
    }
    public class PluginVariable : JSONStorableObject
    {
        public JSONStorableString pluginVarName;
        public JSONStorableEnumStringChooser pluginVarType;

        public JSONStorableFloat pluginFloatValue;
        public JSONStorableBool pluginBoolValue;
        public JSONStorableString pluginStringValue;
        public JSONStorableColor pluginColorValue;

        public PluginVariable()
        {            
            pluginVarName = new JSONStorableString("pluginVarName", "");
            RegisterParam(pluginVarName);

            pluginVarType = new JSONStorableEnumStringChooser("pluginVarType", PluginVariableType.enumManifestName, PluginVariableType.vBool, "", null);
            RegisterParam(pluginVarType);
        }

        public PluginVariable(string varName, int varType)
        {
            pluginVarName = new JSONStorableString("pluginVarName", "");
            pluginVarName.val = varName;
            RegisterParam(pluginVarName, true,false);

            pluginVarType = new JSONStorableEnumStringChooser("pluginVarType", PluginVariableType.enumManifestName, PluginVariableType.vBool, "", null);
            pluginVarType.val = varType;
            RegisterParam(pluginVarType, true, false);

            RegisterValueJSONParam();
        }

        private void RegisterValueJSONParam()
        {
            if (pluginVarType.val == PluginVariableType.vFloat)
            {
                pluginFloatValue = new JSONStorableFloat("pluginVarFloatValue", 0f, -1f, 1f,false);
                RegisterParam(pluginFloatValue);
            }
            else if (pluginVarType.val == PluginVariableType.vBool)
            {
                pluginBoolValue = new JSONStorableBool("pluginVarBoolValue", false);
                RegisterParam(pluginBoolValue);
            }
            else if (pluginVarType.val == PluginVariableType.vColor)
            {
                pluginColorValue = new JSONStorableColor("pluginVarColorValue", new HSVColor());
                RegisterParam(pluginColorValue);
            }
            else if (pluginVarType.val == PluginVariableType.vString || pluginVarType.val == PluginVariableType.vURL || pluginVarType.val == PluginVariableType.vStringChooser)
            {
                pluginStringValue = new JSONStorableString("pluginVarStringValue", "");
                RegisterParam(pluginStringValue);
            }
        }

        public void RestoreFromJSON(JSONClass jc, int uiapFormatVersion)
        {
            ClearAllRegisteredParams();
            RegisterParam(pluginVarName);
            RegisterParam(pluginVarType);
            base.RestoreFromJSON(jc, "pluginVarType");
            RegisterValueJSONParam();
            base.RestoreFromJSON(jc);
            if (uiapFormatVersion == 1 && pluginVarType.val == PluginVariableType.vColor)
            {
                HSVColor hsvColor = new HSVColor();
                if (jc["pluginVarColorHValue"] != null) hsvColor.H = jc["pluginVarColorHValue"].AsFloat;
                if (jc["pluginVarColorSValue"] != null) hsvColor.S = jc["pluginVarColorSValue"].AsFloat;
                if (jc["pluginVarColorVValue"] != null) hsvColor.V = jc["pluginVarColorVValue"].AsFloat;
                pluginColorValue.val = hsvColor;
            }
        }
    }

    public class DecalMakerComponent : ButtonOperationComponentBase
    {
        protected JSONStorableEnumStringChooser buttonTypeJSEnum;

        public DecalMakerTextureCollection decalMakerTextureCollectionFace;
        public DecalMakerTextureCollection decalMakerTextureCollectionTorso;
        public DecalMakerTextureCollection decalMakerTextureCollectionLimbs;
        public DecalMakerTextureCollection decalMakerTextureCollectionGen;

        private int decalMakerSaveVersion = 1;

        public DecalMakerComponent(JSONStorableEnumStringChooser _buttonTypeJSEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonTypeJSEnum = _buttonTypeJSEnum;
            decalMakerTextureCollectionFace = new DecalMakerTextureCollection();
            decalMakerTextureCollectionTorso = new DecalMakerTextureCollection();
            decalMakerTextureCollectionLimbs = new DecalMakerTextureCollection();
            decalMakerTextureCollectionGen = new DecalMakerTextureCollection();
        }

        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            JSONClass jc = base.GetJSON(jspLoadExclusions);

            jc["decalMakerTextureCollectionFace"] = decalMakerTextureCollectionFace.GetJSON();
            jc["decalMakerTextureCollectionTorso"] = decalMakerTextureCollectionTorso.GetJSON();
            jc["decalMakerTextureCollectionLimbs"] = decalMakerTextureCollectionLimbs.GetJSON();
            jc["decalMakerTextureCollectionGen"] = decalMakerTextureCollectionGen.GetJSON();

            return jc;
        }

        public void LoadJSON(JSONClass jc, int uiapFormatVersion)
        {
            base.RestoreFromJSON(jc);
            if (jc["decalMakerTextureCollectionFace"] != null) decalMakerTextureCollectionFace.LoadJSON((JSONClass)jc["decalMakerTextureCollectionFace"], uiapFormatVersion);
            if (jc["decalMakerTextureCollectionTorso"] != null) decalMakerTextureCollectionTorso.LoadJSON((JSONClass)jc["decalMakerTextureCollectionTorso"], uiapFormatVersion);
            if (jc["decalMakerTextureCollectionLimbs"] != null) decalMakerTextureCollectionLimbs.LoadJSON((JSONClass)jc["decalMakerTextureCollectionLimbs"], uiapFormatVersion);
            if (jc["decalMakerTextureCollectionGen"] != null) decalMakerTextureCollectionGen.LoadJSON((JSONClass)jc["decalMakerTextureCollectionGen"], uiapFormatVersion);
        }

        public void CopyFrom(DecalMakerComponent sourceDMComponent)
        {
            base.CopyFrom(sourceDMComponent);

            decalMakerTextureCollectionFace = new DecalMakerTextureCollection();
            decalMakerTextureCollectionFace.CopyFrom(sourceDMComponent.decalMakerTextureCollectionFace);
            decalMakerTextureCollectionTorso = new DecalMakerTextureCollection();
            decalMakerTextureCollectionTorso.CopyFrom(sourceDMComponent.decalMakerTextureCollectionTorso);
            decalMakerTextureCollectionLimbs = new DecalMakerTextureCollection();
            decalMakerTextureCollectionLimbs.CopyFrom(sourceDMComponent.decalMakerTextureCollectionLimbs);
            decalMakerTextureCollectionGen = new DecalMakerTextureCollection();
            decalMakerTextureCollectionGen.CopyFrom(sourceDMComponent.decalMakerTextureCollectionGen);
        }


        public string GetDecalMakerPluginKey(Atom atom)
        {
            MVRPluginManager manager = atom.GetStorableByID("PluginManager") as MVRPluginManager;

            JSONClass currentPlugins = manager.GetJSON(true, true, false);
            string pluginKey = "";

            if (currentPlugins["plugins"] != null)
            {
                foreach (KeyValuePair<string, JSONNode> kvp in (JSONClass)currentPlugins["plugins"])
                {
                    string pluginPath = kvp.Value.ToString().TrimStart('"').TrimEnd('"');
                    if (pluginPath.StartsWith("Chokaphi.DecalMaker."))
                    {
                        JSONStorable pluginStorable = atom.GetStorableByID(kvp.Key + "_VAM_Decal_Maker.Decal_Maker");
                        if (pluginStorable != null && pluginStorable.GetActionNames().Contains("ClearAll"))
                        {
                            pluginKey = kvp.Key;
                            break;
                        }
                    }
                }
            }


            return (pluginKey);
        }

        public MVRScript GetDecalMakerScript(Atom atom)
        {
            return atom.GetStorableByID(GetDecalMakerPluginKey(atom) + "_VAM_Decal_Maker.Decal_Maker") as MVRScript;
        }
        private List<string> GetLoadedDecalTextures(JSONArray decalsJSONArray)
        {
            List<string> decalsList = new List<string>();

            foreach (JSONClass decalJSON in decalsJSONArray)
            {
                decalsList.Add(decalJSON["Path"].Value);
            }

            return (decalsList);
        }
        public int GetDecalMakerButtonState(Atom atom)
        {
            MVRScript dmStorable = GetDecalMakerScript(atom);
            if (dmStorable != null)
            {
                JSONClass decalMakerJSON;
                try
                {
                    // Causes an exception if DecalMaker is loading - so we just catch and ignore.
                    decalMakerJSON = dmStorable.GetJSON(true, true);
                }
                catch (Exception e) { decalMakerJSON = null; }
                if (decalMakerJSON != null)
                {
                    string decalNodeName = "Decal";
                    if (decalMakerJSON["SaveVersion"].AsInt > 1)
                    {
                        decalNodeName = "_DecalTex";
                        decalMakerSaveVersion = decalMakerJSON["SaveVersion"].AsInt;
                    }
                    List<string> loadedFaceDecalTextures = GetLoadedDecalTextures((JSONArray)decalMakerJSON[decalNodeName]["face"]);
                    List<string> loadedTorsoDecalTextures = GetLoadedDecalTextures((JSONArray)decalMakerJSON[decalNodeName]["torso"]);
                    List<string> loadedGenDecalTextures = GetLoadedDecalTextures((JSONArray)decalMakerJSON[decalNodeName]["genitals"]);
                    List<string> loadedLimbsDecalTextures = GetLoadedDecalTextures((JSONArray)decalMakerJSON[decalNodeName]["limbs"]);

                    foreach (DecalMakerTexture dmt in decalMakerTextureCollectionFace.decalMakerTexturesList)
                    {
                        if (dmt._decalTexturePath != "" && !loadedFaceDecalTextures.Contains(dmt._decalTexturePath)) return ButtonState.inactive;
                    }

                    foreach (DecalMakerTexture dmt in decalMakerTextureCollectionTorso.decalMakerTexturesList)
                    {
                        if (dmt._decalTexturePath != "" && !loadedTorsoDecalTextures.Contains(dmt._decalTexturePath)) return ButtonState.inactive;
                    }

                    foreach (DecalMakerTexture dmt in decalMakerTextureCollectionGen.decalMakerTexturesList)
                    {
                        if (dmt._decalTexturePath != "" && !loadedGenDecalTextures.Contains(dmt._decalTexturePath)) return ButtonState.inactive;
                    }

                    foreach (DecalMakerTexture dmt in decalMakerTextureCollectionLimbs.decalMakerTexturesList)
                    {
                        if (dmt._decalTexturePath != "" && !loadedLimbsDecalTextures.Contains(dmt._decalTexturePath)) return ButtonState.inactive;
                    }
                }
                else return ButtonState.inactive;
            }
            else return ButtonState.inactive;

            return ButtonState.active;
        }

        private static void DecalMakerToggleBodyRegion(Atom atom, string bodyRegion, DecalMakerTextureCollection dmtc, int state, string decalNodeName, JSONClass decalStorableJSON)
        {
            var texturesJS = atom.GetStorableByID("textures");
            if (texturesJS != null)
            {
                JSONStorableUrl bodyRegionDecalUrl = texturesJS.GetUrlJSONParam(bodyRegion + "DecalUrl");
                Dictionary<string, int> loadedBodyRegionDecalIndicies;
                foreach (DecalMakerTexture dmt in dmtc.decalMakerTexturesList)
                {
                    loadedBodyRegionDecalIndicies = GetLoadedDecalArrayIndicies(decalStorableJSON[decalNodeName][bodyRegion].AsArray);

                    if (dmt._decalTexturePath != "" && dmt._decalTexturePath != bodyRegionDecalUrl.val)
                    {
                        if (state == 1) AddDMDecal((JSONArray)decalStorableJSON[decalNodeName][bodyRegion], dmt, loadedBodyRegionDecalIndicies);
                        else RemoveDMDecal((JSONArray)decalStorableJSON[decalNodeName][bodyRegion], dmt, loadedBodyRegionDecalIndicies);
                    }

                }
                loadedBodyRegionDecalIndicies = GetLoadedDecalArrayIndicies(decalStorableJSON[decalNodeName][bodyRegion].AsArray);
                if (bodyRegionDecalUrl.val != "")
                {
                    if (!loadedBodyRegionDecalIndicies.ContainsKey(bodyRegionDecalUrl.val))
                    {
                        JSONClass newDecalSaveJSON = new JSONClass();
                        newDecalSaveJSON["H"].AsFloat = 0f;
                        newDecalSaveJSON["S"].AsFloat = 0f;
                        newDecalSaveJSON["V"].AsFloat = 1f;
                        newDecalSaveJSON["Alpha"].AsFloat = 1f;
                        newDecalSaveJSON["Path"] = bodyRegionDecalUrl.val;
                        JSONArray decalArray = (JSONArray)decalStorableJSON[decalNodeName][bodyRegion];
                        decalArray.Add(newDecalSaveJSON);
                    }
                }
            }
            
        }
        public void DecalMakerToggle(Atom atom)
        {
            string pluginKey = GetDecalMakerPluginKey(atom);
            JSONStorable decalPluginStorable = null;
            JSONClass decalStorableJSON = null;
            if (pluginKey == "") decalStorableJSON = GetDecalMakerBlankStorable();
            else
            {
                decalPluginStorable = atom.GetStorableByID(pluginKey + "_VAM_Decal_Maker.Decal_Maker");
                decalStorableJSON = decalPluginStorable.GetJSON(true, true);
            }

            string decalNodeName = "Decal";
            if (decalMakerSaveVersion > 1)   decalNodeName = "_DecalTex";

            if (decalStorableJSON != null)
            {
                int state = parentButton.GetButtonState();

                DecalMakerToggleBodyRegion(atom, "face", decalMakerTextureCollectionFace, state, decalNodeName, decalStorableJSON);

                DecalMakerToggleBodyRegion(atom, "torso", decalMakerTextureCollectionTorso, state, decalNodeName, decalStorableJSON);

                DecalMakerToggleBodyRegion(atom, "limbs", decalMakerTextureCollectionLimbs, state, decalNodeName, decalStorableJSON);

                DecalMakerToggleBodyRegion(atom, "genitals", decalMakerTextureCollectionGen, state, decalNodeName, decalStorableJSON);

                if (pluginKey == "") MergeLoadDecalMakerPlugin(atom, decalStorableJSON);
                else
                {
                    if (decalPluginStorable.GetActionNames().Contains("PerformLoad"))
                    {

                        JSONStorableAction clearAllAction = decalPluginStorable.GetAction("ClearAll");
                        JSONStorableAction performLoadAction = decalPluginStorable.GetAction("PerformLoad");
                        if (clearAllAction != null)
                        {
                            decalPluginStorable.CallAction("ClearAll");
                            decalPluginStorable.RestoreFromJSON(decalStorableJSON);
                            decalPluginStorable.CallAction("PerformLoad"); 
                        }
                    }
                    else UIAGlobals.mvrScript.StartCoroutine(DMToggleByCharSkinChange(atom, decalPluginStorable, decalStorableJSON, false));
                }
            }
        }

        private IEnumerator DMLateLoad(JSONStorable decalPluginStorable, JSONClass decalJSON)
        {
            yield return new WaitForSeconds(1f);
            if (decalPluginStorable.GetActionNames().Contains("PerformLoad"))
            {
                JSONStorableAction clearAllAction = decalPluginStorable.GetAction("ClearAll");
                JSONStorableAction performLoadAction = decalPluginStorable.GetAction("PerformLoad");
                if (clearAllAction != null)
                {
                    decalPluginStorable.CallAction("ClearAll");
                    decalPluginStorable.RestoreFromJSON(decalJSON);
                    decalPluginStorable.CallAction("PerformLoad");
                }
            }
        }
        private IEnumerator DMToggleByCharSkinChange(Atom atom, JSONStorable decalPluginStorable, JSONClass decalJSON, bool waitForDMLoad)
        {
            DAZCharacterSelector dazCharacterSelector = atom.GetComponentInChildren<DAZCharacterSelector>();
            DAZCharacter dazCharacter = dazCharacterSelector.selectedCharacter;
            JSONStorable receiver = atom.GetStorableByID("geometry");
            JSONStorableStringChooser characterJSON = receiver.GetStringChooserJSONParam("characterSelection");

            string currentCharacterSkinName = characterJSON.val;
            string tempCharacterSkinName = "";

            switch (dazCharacter.UVname)
            {
                case "UV: Base Female":
                    if (currentCharacterSkinName == "Female 1") tempCharacterSkinName = "Female 7";
                    else tempCharacterSkinName = "Female 1";
                    break;
                case "UV: Victoria 6":
                    if (currentCharacterSkinName == "Female 2") tempCharacterSkinName = "Female 4";
                    else tempCharacterSkinName = "Female 2";
                    break;
                case "UV: Base Male":
                    if (currentCharacterSkinName == "Male 4") tempCharacterSkinName = "Male 5";
                    else tempCharacterSkinName = "Male 4";
                    break;
                case "UV: Michael 6":
                    tempCharacterSkinName = "Male 4";
                    break;
                case "UV: Gianni 6":
                    tempCharacterSkinName = "Male 4";
                    break;
                case "UV: Darius 6":
                    tempCharacterSkinName = "Male 4";
                    break;
                case "UV: Lee 6":
                    tempCharacterSkinName = "Male 4";
                    break;
                case "UV: Scott 6":
                    tempCharacterSkinName = "Male 4";
                    break;
                default:
                    tempCharacterSkinName = "Female 1";
                    break;
            }

            if (waitForDMLoad) yield return new WaitForSeconds(3f);
            JSONStorableAction clearAllAction = decalPluginStorable.GetAction("ClearAll");
            if (clearAllAction != null)
            {
                decalPluginStorable.CallAction("ClearAll");
                yield return new WaitForSeconds(0.5f);
                yield return new WaitUntil(() => dazCharacter.ready);
                decalPluginStorable.RestoreFromJSON(decalJSON);
                yield return new WaitForSeconds(0.5f);
                yield return new WaitUntil(() => dazCharacter.ready);
                characterJSON.val = tempCharacterSkinName;
                yield return new WaitForSeconds(0.5f);
                yield return new WaitUntil(() => dazCharacter.ready);
                characterJSON.val = currentCharacterSkinName;
            }
        }
        private static Dictionary<string, int> GetLoadedDecalArrayIndicies(JSONArray decalsJSONArray)
        {
            Dictionary<string, int> decalsDict = new Dictionary<string, int>();
            int index = 0;
            foreach (JSONClass decalJSON in decalsJSONArray)
            {
                string path = decalJSON["Path"].Value;
                if (!decalsDict.ContainsKey(path)) decalsDict.Add(path, index);
                index++;
            }

            return (decalsDict);
        }


        private static void AddDMDecal(JSONArray decalArray, DecalMakerTexture dmt, Dictionary<string, int> loadedDecalIndicies)
        {
            if (loadedDecalIndicies.ContainsKey(dmt._decalTexturePath))
            {
                JSONClass existingDecalSaveJSON = (JSONClass)decalArray[loadedDecalIndicies[dmt._decalTexturePath]];
                existingDecalSaveJSON["H"].AsFloat = dmt._decalTextureColor.H;
                existingDecalSaveJSON["S"].AsFloat = dmt._decalTextureColor.S;
                existingDecalSaveJSON["V"].AsFloat = dmt._decalTextureColor.V;
                existingDecalSaveJSON["Alpha"].AsFloat = dmt._decalTextureAlpha;
                existingDecalSaveJSON["Path"] = dmt._decalTexturePath;
            }
            else
            {

                JSONClass newDecalSaveJSON = new JSONClass();
                newDecalSaveJSON["H"].AsFloat = dmt._decalTextureColor.H;
                newDecalSaveJSON["S"].AsFloat = dmt._decalTextureColor.S;
                newDecalSaveJSON["V"].AsFloat = dmt._decalTextureColor.V;
                newDecalSaveJSON["Alpha"].AsFloat = dmt._decalTextureAlpha;
                newDecalSaveJSON["Path"] = dmt._decalTexturePath;
                decalArray.Add(newDecalSaveJSON);
            }
        }
        private static void RemoveDMDecal(JSONArray decalArray, DecalMakerTexture dmt, Dictionary<string, int> loadedDecalIndicies)
        {
            if (loadedDecalIndicies.ContainsKey(dmt._decalTexturePath))
            {
                decalArray.Remove(loadedDecalIndicies[dmt._decalTexturePath]);
            }
        }

        private JSONClass GetDecalMakerBlankStorable()
        {
            JSONClass decalJSON = new JSONClass();

            string decalNodeName = "Decal";
            if (decalMakerSaveVersion > 1)
            {
                decalNodeName = "_DecalTex";
                decalJSON["SaveVersion"].AsInt = 2;
            }
            else decalJSON["SaveVersion"].AsInt = 1;

            decalJSON["Nipple Cutouts ON"] = "true";
            decalJSON["Genital Cutouts ON"] = "true";
            decalJSON["enabled"] = "true";        

            decalJSON[decalNodeName] = new JSONClass();
            decalJSON[decalNodeName]["face"] = new JSONArray();
            decalJSON[decalNodeName]["torso"] = new JSONArray();
            decalJSON[decalNodeName]["genitals"] = new JSONArray();
            decalJSON[decalNodeName]["limbs"] = new JSONArray();
            decalJSON["_SpecTex"] = new JSONClass();
            decalJSON["_SpecTex"]["face"] = new JSONArray();
            decalJSON["_SpecTex"]["torso"] = new JSONArray();
            decalJSON["_SpecTex"]["genitals"] = new JSONArray();
            decalJSON["_SpecTex"]["limbs"] = new JSONArray();

            decalJSON["_GlossTex"] = new JSONClass();
            decalJSON["_GlossTex"]["face"] = new JSONArray();
            decalJSON["_GlossTex"]["torso"] = new JSONArray();
            decalJSON["_GlossTex"]["genitals"] = new JSONArray();
            decalJSON["_GlossTex"]["limbs"] = new JSONArray();

            decalJSON["_BumpMap"] = new JSONClass();
            decalJSON["_BumpMap"]["face"] = new JSONArray();
            decalJSON["_BumpMap"]["torso"] = new JSONArray();
            decalJSON["_BumpMap"]["genitals"] = new JSONArray();
            decalJSON["_BumpMap"]["limbs"] = new JSONArray();

            return (decalJSON);
        }

        private void MergeLoadDecalMakerPlugin(Atom atom, JSONClass decalMakerJSON)
        {
            JSONClass pluginPresetJSON = new JSONClass();
            pluginPresetJSON["setUnlistedParamsToDefault"].AsBool = true;

            JSONArray pluginPresetStorablesJSON = new JSONArray();
            pluginPresetJSON["storables"] = pluginPresetStorablesJSON;

            JSONClass pluginMgrStorableJSON = new JSONClass();
            pluginPresetStorablesJSON.Add(pluginMgrStorableJSON);
            pluginMgrStorableJSON["id"] = "PluginManager";

            JSONClass pluginMgrPluginsListJSON = new JSONClass();
            pluginMgrStorableJSON["plugins"] = pluginMgrPluginsListJSON;

            pluginMgrPluginsListJSON["plugin#1"] = "Chokaphi.DecalMaker.latest:/Custom/Scripts/Chokaphi/VAM_Decal_Maker/VAM_Decal_Maker.cs";

            if (decalMakerJSON == null) decalMakerJSON = GetDecalMakerBlankStorable();
            decalMakerJSON["id"] = "plugin#1_VAM_Decal_Maker.Decal_Maker";
            //            pluginPresetStorablesJSON.Add(decalMakerJSON);

            PluginsLoadComponent.MergePluginPreset(atom, pluginPresetJSON);
            string pluginKey = GetDecalMakerPluginKey(atom);
            if (pluginKey != "")
            {
                JSONStorable decalPluginStorable = atom.GetStorableByID(pluginKey + "_VAM_Decal_Maker.Decal_Maker");
                UIAGlobals.mvrScript.StartCoroutine(DMLateLoad(decalPluginStorable, decalMakerJSON));
            }
        }

    }

    public class DecalMakerTextureCollection : JSONStorableObject
    {
        public List<DecalMakerTexture> decalMakerTexturesList;

        public DecalMakerTextureCollection()
        {
            decalMakerTexturesList = new List<DecalMakerTexture>();
        }
        public void CopyFrom(DecalMakerTextureCollection dmtc)
        {
            if (dmtc != null)
            {
                decalMakerTexturesList = new List<DecalMakerTexture>();
                foreach (DecalMakerTexture dmt in dmtc.decalMakerTexturesList)
                {
                    DecalMakerTexture newDMT = new DecalMakerTexture();
                    newDMT.CopyFrom(dmt);
                    decalMakerTexturesList.Add(newDMT);
                }
            }
        }

        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            JSONClass jc = base.GetJSON(jspLoadExclusions);
            JSONArray decalMakerTexturesArray = new JSONArray();

            foreach (DecalMakerTexture dmt in decalMakerTexturesList)
            {
                decalMakerTexturesArray.Add(dmt.GetJSON());
            }

            jc["decalTexturesCollection"] = decalMakerTexturesArray;

            return jc;
        }
        public void LoadJSON(JSONClass dmtcJSON, int uiapVersion)
        {
            decalMakerTexturesList = new List<DecalMakerTexture>();

            if (dmtcJSON["decalTexturesCollection"] != null)
            {
                JSONArray decalTexturesArray = dmtcJSON["decalTexturesCollection"].AsArray;
                for (int i = 0; i < decalTexturesArray.Count; i++)
                {
                    JSONClass dmtNode = decalTexturesArray[i].AsObject;
                    if (uiapVersion == 1)
                    {
                        HSVColor hsvColor = new HSVColor();
                        hsvColor.H = dmtNode["decalTextureColorHValue"].AsFloat;
                        hsvColor.S = dmtNode["decalTextureColorSValue"].AsFloat;
                        hsvColor.V = dmtNode["decalTextureColorVValue"].AsFloat;
                        JSONStorableColor tempColJSC = new JSONStorableColor("decalTextureColor", hsvColor);
                        tempColJSC.StoreJSON(dmtNode, true, true, true);
                    }
                    DecalMakerTexture newDMT = new DecalMakerTexture();
                    newDMT.RestoreFromJSON(dmtNode);
                    decalMakerTexturesList.Add(newDMT);
                }
            }
        }
        public List<string> GetDecalSelectChoices(string currentSelection)
        {
            List<string> choices = new List<string>();
            int index = 1;
            foreach (DecalMakerTexture dm in decalMakerTexturesList)
            {
                choices.Add(index.ToString());
                index++;
            }
            choices.Add("Add Decal");
            if (currentSelection != "Select Texture...") choices.Add("Remove Decal " + currentSelection);
            return (choices);
        }
        public void AddDecal()
        {
            DecalMakerTexture newDMT = new DecalMakerTexture();
            decalMakerTexturesList.Add(newDMT);
        }
        public void RemoveDecal(int index)
        {
            decalMakerTexturesList.RemoveAt(index);
        }
    }
    public class DecalMakerTexture : JSONStorableObject
    {
        public string _decalTexturePath
        {
            get { return decalTexturePathJSS.val; }
            set { decalTexturePathJSS.val = value; }
        }
        public HSVColor _decalTextureColor
        {
            get { return decalTextureColorJSC.val; }
            set { decalTextureColorJSC.val = value; }
        }
        public float _decalTextureAlpha
        {
            get { return decalTextureAlphaJSF.val; }
            set { decalTextureAlphaJSF.val = value; }
        }

        public JSONStorableString decalTexturePathJSS;
        public JSONStorableColor decalTextureColorJSC;
        public JSONStorableFloat decalTextureAlphaJSF;
        public DecalMakerTexture()
        {
            HSVColor defaultColor = new HSVColor();
            defaultColor.H = 0f;
            defaultColor.S = 0f;
            defaultColor.V = 1f;

            decalTextureColorJSC = new JSONStorableColor("decalTextureColor", defaultColor);
            decalTexturePathJSS = new JSONStorableString("decalTexturePath", "");
            decalTextureAlphaJSF = new JSONStorableFloat("decalTextureAlpha", 0f, -1f, 1f);
            RegisterParam(decalTextureColorJSC);
            RegisterParam(decalTexturePathJSS);
            RegisterParam(decalTextureAlphaJSF);

        }

    }


    public class TargetComponent : ButtonOperationComponentBase
    {
        public JSONStorableEnumStringChooser targetCategoryJSEnum;
        public JSONStorableMultiEnumStringChooser targetNameJSMultiEnum { get; set; }
        public JSONStorableString specificAtomTypeJSS;

        public JSONStorableEnumStringChooser altTargetCategoryJSEnum;
        public JSONStorableMultiEnumStringChooser altTargetNameJSMultiEnum;
        public JSONStorableString altSpecificAtomTypeJSS;

        private List<int> altTargetCategoryExclusions = new List<int>() { TargetCategory.scenePlugins, TargetCategory.sessionPlugins, TargetCategory.atomGroup,  /*TargetCategory.gazeSelectedAtom, TargetCategory.vamSelectedAtom,*/ TargetCategory.triggerAtom };

        public JSONStorableBool spawnAtomIfTargetMissingJSBool;
        
        public JSONStorableString changeAtomNameJSS;
        public JSONStorableBool keepOpenAtomSelectorJSB;
        public JSONStorableBool keepOpenOptionAtomSelectorJSB;

        public JSONStorableBool includeHiddenAtomsJSB;
        public JSONStorableBool excludeWindowsCameraAtomJSB;
        public JSONStorableBool excludePNPAtomJSB;

        public JSONStorableBool includeOffAtomsJSB;

        public JSONStorableEnumStringChooser aceModeJSEnum;
        public JSONStorableBool aceRealClothingFilterJSB;

        public string lastUserChosenAtomName { get; protected set; }
        public string lastAltUserChosenAtomName { get; protected set; }

        public List<string> currentActionTargetAtomNames =null;
        public List<string> currentActionAltTargetAtomNames = null;

        protected JSONStorableEnumStringChooser buttonOrSliderTypeJSEnum;
        protected int buttonCategory
        {
            get
            {
                return UIAButtonOpType.GetButtonCategory(buttonOrSliderTypeJSEnum.val);
            }
        }

        public bool isAltTargetType
        {
            get
            {
                if (buttonOrSliderTypeJSEnum.val == UIAButtonOpType.parentAtom) return true;
                if (buttonOrSliderTypeJSEnum.val == UIAButtonOpType.teleportAtom) return true;
                return false;
            }
        }

        public bool isPersonTargetType
        {
            get
            {
                if (targetCategoryJSEnum.val != TargetCategory.specificAtom && targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.mainValFlag && CustomTargetGroupSettings.GetCGTFromName(targetNameJSMultiEnum.mainVal).isPersonCGT ) return true;

                if (targetCategoryJSEnum.val == TargetCategory.atomGroup)
                {
                    if (targetNameJSMultiEnum.valType== JSONStorableMultiEnumStringChooser.topEnumValFlag)
                    {
                        if (targetNameJSMultiEnum.valTopEnum == AllAtomsTargetType.allFemaleAtoms || targetNameJSMultiEnum.valTopEnum == AllAtomsTargetType.allMaleAtoms || targetNameJSMultiEnum.valTopEnum == AllAtomsTargetType.allPersonAtoms) return true;
                    }
                }              
                if (targetCategoryJSEnum.val == TargetCategory.gazeSelectedAtom)
                {
                    if (targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.topEnumValFlag)
                    {
                        if (targetNameJSMultiEnum.valTopEnum == LastViewedTargetType.lastViewedFemale || targetNameJSMultiEnum.valTopEnum == LastViewedTargetType.lastViewedMale || targetNameJSMultiEnum.valTopEnum == LastViewedTargetType.lastViewedPerson) return true;
                    }                    
                }


                if (targetCategoryJSEnum.val == TargetCategory.vamSelectedAtom)
                {
                    if (targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.topEnumValFlag)
                    {
                        if (targetNameJSMultiEnum.valTopEnum == LastSelectedTargetType.lastSelectedFemale || targetNameJSMultiEnum.valTopEnum == LastSelectedTargetType.lastSelectedMale || targetNameJSMultiEnum.valTopEnum == LastSelectedTargetType.lastSelectedPerson) return true;
                    }
                }
                if (targetCategoryJSEnum.val == TargetCategory.userChosenAtom || targetCategoryJSEnum.val == TargetCategory.triggerAtom)
                {
                    if (targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.topEnumValFlag)
                    {
                        if (targetNameJSMultiEnum.valTopEnum == UserChosenTargetType.femaleAtoms || targetNameJSMultiEnum.valTopEnum == UserChosenTargetType.maleAtoms || targetNameJSMultiEnum.valTopEnum == UserChosenTargetType.personAtoms) return true;
                    }
                    else if (targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.mainValFlag)
                    {
                        if (CustomTargetGroupSettings.GetCGTFromName(targetNameJSMultiEnum.mainVal).cgtAtomTypeJSEnum.val == AtomTypes.person) return true;
                    }
                }
                if (targetCategoryJSEnum.val == TargetCategory.specificAtom && specificAtomTypeJSS.val == "Person") return true;
                return false;
            }
        }

        public string targetAtomType
        {
            get
            {
                if (targetCategoryJSEnum.val == TargetCategory.specificAtom) return specificAtomTypeJSS.val;
                if (targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.mainValFlag) return CustomTargetGroupSettings.GetCGTFromName(targetNameJSMultiEnum.mainVal).cgtAtomTypeJSEnum.displayVal;
                if (isPersonTargetType) return "Person";

                return null;
            }
        }

        public TargetComponent(JSONStorableEnumStringChooser sliderTypeEnum, UIASlider parent) : base(parent)
        {
            buttonOrSliderTypeJSEnum = sliderTypeEnum;
            Init();
        }

        private void Init()
        {        
            targetCategoryJSEnum = new JSONStorableEnumStringChooser("targetCategory", TargetCategory.enumManifestName, TargetCategory.gazeSelectedAtom, "", TargetCategoryUpdated, GetTargetCatExclusions());
            RegisterParam(targetCategoryJSEnum);

            targetNameJSMultiEnum = new JSONStorableMultiEnumStringChooser("targetName", null, null, null, null, TargetNameMainEnumChanged, LastViewedTargetType.enumManifestName, TargetNameTopEnumChanged);
            RegisterParam(targetNameJSMultiEnum);

            specificAtomTypeJSS = new JSONStorableString("specificAtomType", "");
            RegisterParam(specificAtomTypeJSS);

            spawnAtomIfTargetMissingJSBool = new JSONStorableBool("spawnAtomIfTargetMissing", false, SpawnAtomCallback);
            RegisterParam(spawnAtomIfTargetMissingJSBool);

            changeAtomNameJSS = new JSONStorableString("changeAtomNewName", "");
            RegisterParam(changeAtomNameJSS);

            keepOpenAtomSelectorJSB = new JSONStorableBool("keepOpen", false);
            keepOpenOptionAtomSelectorJSB = new JSONStorableBool("keepOpenOption", true);
            RegisterParam(keepOpenAtomSelectorJSB);
            RegisterParam(keepOpenOptionAtomSelectorJSB);

            includeOffAtomsJSB = new JSONStorableBool("includeOffAtoms", false);
            RegisterParam(includeOffAtomsJSB);

            includeHiddenAtomsJSB = new JSONStorableBool("includeHiddenAtoms", true);
            RegisterParam(includeHiddenAtomsJSB);
            excludeWindowsCameraAtomJSB = new JSONStorableBool("excludeWindowsCameraAtom", false);
            RegisterParam(excludeWindowsCameraAtomJSB);
            excludePNPAtomJSB = new JSONStorableBool("excludePNPAtom", false);
            RegisterParam(excludePNPAtomJSB);

            currentActionTargetAtomNames = null;
        }

        public TargetComponent(JSONStorableEnumStringChooser _buttonTypeEnum, UIAButtonOperation parent) : base(parent)
        {
            buttonOrSliderTypeJSEnum = _buttonTypeEnum;

            Init();

            altTargetCategoryJSEnum = new JSONStorableEnumStringChooser("altTargetCategory", TargetCategory.enumManifestName, TargetCategory.gazeSelectedAtom, "", AltTargetCategoryUpdated, altTargetCategoryExclusions);
            RegisterParam(altTargetCategoryJSEnum);

            altTargetNameJSMultiEnum = new JSONStorableMultiEnumStringChooser("altTargetName", null, null, null, null, AltTargetNameMainEnumChanged, UserChosenTargetType.enumManifestName, AltTargetNameTopEnumChanged);
            RegisterParam(altTargetNameJSMultiEnum);
            RefreshAltTargetNameChoices();

            altSpecificAtomTypeJSS = new JSONStorableString("altSpecificAtomType", "");
            RegisterParam(altSpecificAtomTypeJSS);

            aceModeJSEnum = new JSONStorableEnumStringChooser("aceMode", ACEMode.enumManifestName, ACEMode.enhanced, "Acitve Clothing Editor Mode");
            RegisterParam(aceModeJSEnum);
            aceRealClothingFilterJSB = new JSONStorableBool("aceRealClothingFilter", true);
            RegisterParam(aceRealClothingFilterJSB);        
        }

        private void SpawnAtomCallback(bool value)
        {
            parentButtonOperation.CreateButtonComponents();
        }
        public void ChangeTargetAtomNameAction(Atom atom)
        {
            if (changeAtomNameJSS.val != "" && atom.uid!= changeAtomNameJSS.val)
            {
                atom.SetUID(changeAtomNameJSS.val);
            }
        }

        public void ParentAtom(Atom atom)
        {
            if (currentActionAltTargetAtomNames.Count==1)
            {
                var parentAtom = SuperController.singleton.GetAtomByUid(currentActionAltTargetAtomNames[0]);
                if (parentAtom != null)
                {
                    atom.parentAtom= parentAtom;
                }
            }
        }
        public void UnparentAtom(Atom atom)
        {
            atom.parentAtom = null;
        }

        public IEnumerator CloneAtom(Atom sourceAtom)
        {
            var sourceAtomName = sourceAtom.name;

            int index = sourceAtomName.LastIndexOf('#');

            if (index > 0)
            {
                string numeric = sourceAtomName.Substring(index+1);

                int result;
                if (int.TryParse(numeric, out result)) sourceAtomName = sourceAtomName.Substring(0, index);
            }

            var newAtomName = AtomUtils.GetNextAvailableAtomName(sourceAtomName);

            UIAGlobals.uiaAtomSpawnInProgress = true;
            yield return SuperController.singleton.StartCoroutine(SuperController.singleton.AddAtomByType(sourceAtom.type, newAtomName));
            UIAGlobals.uiaAtomSpawnInProgress = false;

            Atom cloned = SuperController.singleton.GetAtomByUid(newAtomName);

            JSONClass sourceSceneJSON = SuperController.singleton.GetSaveJSON(sourceAtom, true, true);
            JSONNode sourceJSON = sourceSceneJSON["atoms"].AsArray[0];

            cloned.PreRestore();
            cloned.Restore(sourceJSON as JSONClass, true, true, true);
            cloned.LateRestore(sourceJSON as JSONClass, true, true, true);
            cloned.PostRestore();

            parentButtonOperation.relativePositionComponent.TeleportAtomToSpawnPoint(newAtomName);
        }


        private void TargetNameTopEnumChanged(int targetNameTopEnum)
        {
            if (targetCategoryJSEnum.val == TargetCategory.userChosenAtom)
            {
                if (!GetUserTargetAtomChoices().Contains(lastUserChosenAtomName)) lastUserChosenAtomName = "";
            }
            if (parentButton!=null) parentButton.parentGrid.RecalcGazeSelections();
            else if (parentSlider!=null) parentSlider.parentGrid.RecalcGazeSelections();
        }

        private void AltTargetNameTopEnumChanged(int targetNameTopEnum)
        {
            if (altTargetCategoryJSEnum.val == TargetCategory.userChosenAtom)
            {
                if (!GetUserTargetAtomChoices(altTargetCategoryJSEnum,altTargetNameJSMultiEnum,true).Contains(lastAltUserChosenAtomName)) lastAltUserChosenAtomName = "";
            }
            if (parentButton != null) parentButton.parentGrid.RecalcGazeSelections();
            else if (parentSlider != null) parentSlider.parentGrid.RecalcGazeSelections();
        }

        private void TargetNameMainEnumChanged(string targetName)
        {
            if (targetCategoryJSEnum.val == TargetCategory.userChosenAtom)
            {
                if (!GetUserTargetAtomChoices().Contains(lastUserChosenAtomName)) lastUserChosenAtomName = "";
            }
            if (targetCategoryJSEnum.val == TargetCategory.specificAtom)
            {
                Atom atom = SuperController.singleton.GetAtomByUid(targetNameJSMultiEnum.displayVal);
                if (atom != null) specificAtomTypeJSS.val = atom.type;
                else specificAtomTypeJSS.val = "";
            }
            if (parentButton!=null) parentButton.parentGrid.RecalcGazeSelections();
            else if (parentSlider!=null) parentSlider.parentGrid.RecalcGazeSelections();
        }

        private void AltTargetNameMainEnumChanged(string targetName)
        {
            if (altTargetCategoryJSEnum.val == TargetCategory.userChosenAtom)
            {
                if (!GetUserTargetAtomChoices(altTargetCategoryJSEnum,altTargetNameJSMultiEnum,true).Contains(lastAltUserChosenAtomName)) lastAltUserChosenAtomName = "";
            }
            if (altTargetCategoryJSEnum.val == TargetCategory.specificAtom)
            {
                Atom atom = SuperController.singleton.GetAtomByUid(altTargetNameJSMultiEnum.displayVal);
                if (atom != null) altSpecificAtomTypeJSS.val = atom.type;
                else altSpecificAtomTypeJSS.val = "";
            }
        }
        public void RefreshTargetCatChoices()
        {
            targetCategoryJSEnum.SetEnumChoices(TargetCategory.enumManifestName, GetTargetCatExclusions(), true, true);
        }
        public void RefreshTargetNameChoices(bool resetVal = true)
        {
            RefreshTargetNameChoices(targetCategoryJSEnum, targetNameJSMultiEnum, resetVal);
        }
        public void RefreshAltTargetNameChoices(bool resetVal = true)
        {
            RefreshTargetNameChoices(altTargetCategoryJSEnum, altTargetNameJSMultiEnum, resetVal,true);
        }

        private void RefreshTargetNameChoices(JSONStorableEnumStringChooser targetCategoryJSEnum, JSONStorableMultiEnumStringChooser targetNameJSMultiEnum, bool resetVal = true, bool altTarget = false)
        {
            if (targetCategoryJSEnum.val == TargetCategory.gazeSelectedAtom)
            {
                targetNameJSMultiEnum.SetTopEnum(LastViewedTargetType.enumManifestName, GetTargetNameExclusions(altTarget));
                
                targetNameJSMultiEnum.SetMainChoices(GetCGTNames());
                targetNameJSMultiEnum.SetDisplayChoicePreFix("Last viewed ");
                targetNameJSMultiEnum.SetDisplayChoicePostFix("");
                targetNameJSMultiEnum.SetBottomEnum(null);
                if (targetNameJSMultiEnum.choices.Count==0) targetNameJSMultiEnum.SetBottomEnum(NoneAvailable.enumManifestName);
            }
            if (targetCategoryJSEnum.val == TargetCategory.vamSelectedAtom)
            {
                targetNameJSMultiEnum.SetTopEnum(LastSelectedTargetType.enumManifestName, GetTargetNameExclusions(altTarget));
                
                targetNameJSMultiEnum.SetMainChoices(null);
                targetNameJSMultiEnum.SetDisplayChoicePreFix("Last selected ");
                targetNameJSMultiEnum.SetDisplayChoicePostFix("");
                targetNameJSMultiEnum.SetBottomEnum(null);
                if (targetNameJSMultiEnum.choices.Count == 0) targetNameJSMultiEnum.SetBottomEnum(NoneAvailable.enumManifestName);
            }
            if (targetCategoryJSEnum.val == TargetCategory.atomGroup)
            {
                targetNameJSMultiEnum.SetTopEnum(AllAtomsTargetType.enumManifestName, GetTargetNameExclusions(altTarget));
                targetNameJSMultiEnum.SetMainChoices(GetCGTNames());
                targetNameJSMultiEnum.SetDisplayChoicePreFix("All ");
                targetNameJSMultiEnum.SetDisplayChoicePostFix(" Atoms");
                targetNameJSMultiEnum.SetBottomEnum(null);
                if (targetNameJSMultiEnum.choices.Count == 0) targetNameJSMultiEnum.SetBottomEnum(NoneAvailable.enumManifestName);
            }
            if (targetCategoryJSEnum.val == TargetCategory.specificAtom)
            {              
                List<string> targetNames = GetSpecificAtomTargetChoices(altTarget);
                
                if (!targetNames.Contains(targetNameJSMultiEnum.displayVal) && !resetVal && targetNameJSMultiEnum.valType!=JSONStorableMultiEnumStringChooser.topEnumValFlag) targetNames.Add(targetNameJSMultiEnum.displayVal);
                if (targetNames.Count == 0) {

                    targetNameJSMultiEnum.SetTopEnum(NoneAvailable.enumManifestName);
                    targetNameJSMultiEnum.SetMainChoices(null);
                    targetNameJSMultiEnum.valTopEnum = NoneAvailable.noneAvailable;
                }
                else
                {
                    targetNameJSMultiEnum.SetTopEnum(null);
                    targetNameJSMultiEnum.SetMainChoices(targetNames);
                }

                targetNameJSMultiEnum.SetDisplayChoicePreFix("");
                targetNameJSMultiEnum.SetDisplayChoicePostFix("");
                targetNameJSMultiEnum.SetBottomEnum(null);
                if (targetNameJSMultiEnum.choices.Count == 0) targetNameJSMultiEnum.SetBottomEnum(NoneAvailable.enumManifestName);
            }
            if (targetCategoryJSEnum.val == TargetCategory.userChosenAtom || targetCategoryJSEnum.val == TargetCategory.triggerAtom)
            {
                targetNameJSMultiEnum.SetTopEnum(UserChosenTargetType.enumManifestName, GetTargetNameExclusions(altTarget));
                targetNameJSMultiEnum.SetMainChoices(GetCGTNames());
                targetNameJSMultiEnum.SetDisplayChoicePreFix("");
                targetNameJSMultiEnum.SetDisplayChoicePostFix(" Atoms");
                targetNameJSMultiEnum.SetBottomEnum(null);
                if (targetNameJSMultiEnum.choices.Count == 0) targetNameJSMultiEnum.SetBottomEnum(NoneAvailable.enumManifestName);
            }
            if (resetVal)
            {
                if (targetCategoryJSEnum.val== TargetCategory.gazeSelectedAtom && targetNameJSMultiEnum.displayChoices.Contains(GazeTargetSettings.defaultGazeTargetJSEnum.displayVal))targetNameJSMultiEnum.valTopEnum = GazeTargetSettings.defaultGazeTargetJSEnum.val;
                else targetNameJSMultiEnum.ResetValFromChoices();
            }
        }

        protected void AltTargetCategoryUpdated(int targetCat)
        {
            RefreshAltTargetNameChoices();
        }

        protected void TargetCategoryUpdated(int targetCat)
        {
            if (parentButtonOperation!=null) parentButtonOperation.RefreshFileRefTypes();
            RefreshTargetNameChoices();
        }

        public void ResetLastUserChosenAtom()
        {
            lastUserChosenAtomName = "";
            lastAltUserChosenAtomName = "";
        }

        protected List<string> GetSpecificAtomTargetChoices(bool altTarget)
        {
            List<string> targetAtoms = new List<string>();
            foreach (string atomUID in SuperController.singleton.GetAtomUIDs())
            {
                Atom atom = SuperController.singleton.GetAtomByUid(atomUID);
                if (atomUID != "[CameraRig]" && (atomUID != "CoreControl" || buttonOrSliderTypeJSEnum.val == UIAButtonOpType.loadPlugins || buttonOrSliderTypeJSEnum.val == UIAButtonOpType.triggerVAMAction) && (altTarget || atom.type == "Person" || UIAButtonOpType.IsNonPersonAtomTargetableType(buttonOrSliderTypeJSEnum.val) || parentSlider!=null))
                {
                    if (buttonOrSliderTypeJSEnum.val != UIAButtonOpType.loadSubScene || atom.type == "SubScene") targetAtoms.Add(atomUID);
                }
            }
            return (targetAtoms);
        }

        protected List<int> GetTargetCatExclusions()
        {
            List<int> targetCatExclusions = new List<int>();

            if (buttonOrSliderTypeJSEnum.val == UIAButtonOpType.moveAtom || buttonOrSliderTypeJSEnum.val == UIAButtonOpType.teleportAtom || buttonOrSliderTypeJSEnum.val == UIAButtonOpType.changeAtomName || buttonOrSliderTypeJSEnum.val == UIAButtonOpType.selectAtom || buttonOrSliderTypeJSEnum.val == UIAButtonOpType.activeClothingEditor) targetCatExclusions.Add(TargetCategory.atomGroup);
            if (parentButton == null || (buttonCategory != UIAButtonCategory.pluginControl && buttonCategory != UIAButtonCategory.pluginSettings && buttonOrSliderTypeJSEnum.val != UIAButtonOpType.loadPluginsPreset))
            {
                targetCatExclusions.Add(TargetCategory.scenePlugins);
                targetCatExclusions.Add(TargetCategory.sessionPlugins);
            }
            if (parentButton==null || (!parentButton.isTriggerFunctionsGrid || parentButton.triggerFunctionTypeJSEnum.val !=TriggerFuncType.onAtomAdded)) targetCatExclusions.Add(TargetCategory.triggerAtom);

            return (targetCatExclusions);
        }

        protected List<int> GetTargetNameExclusions(bool altTarget)
        {
            List<int> targetNameExclusions = new List<int>();

            if (altTarget) return targetNameExclusions;

            if (buttonOrSliderTypeJSEnum.val == UIAButtonOpType.loadSubScene)
            {
                if (targetCategoryJSEnum.val == TargetCategory.gazeSelectedAtom)
                {
                    targetNameExclusions.Add(LastViewedTargetType.lastViewedFemale);
                    targetNameExclusions.Add(LastViewedTargetType.lastViewedMale);
                    targetNameExclusions.Add(LastViewedTargetType.lastViewedPerson);
                }
                else if (targetCategoryJSEnum.val == TargetCategory.vamSelectedAtom)
                {
                    targetNameExclusions.Add(LastSelectedTargetType.lastSelectedFemale);
                    targetNameExclusions.Add(LastSelectedTargetType.lastSelectedMale);
                    targetNameExclusions.Add(LastSelectedTargetType.lastSelectedPerson);
                }
                else if (targetCategoryJSEnum.val == TargetCategory.atomGroup)
                {
                    targetNameExclusions.Add(AllAtomsTargetType.allPersonAtoms);
                    targetNameExclusions.Add(AllAtomsTargetType.allFemaleAtoms);
                    targetNameExclusions.Add(AllAtomsTargetType.allMaleAtoms);
                }
                else if (targetCategoryJSEnum.val == TargetCategory.userChosenAtom || targetCategoryJSEnum.val == TargetCategory.triggerAtom)
                {
                    targetNameExclusions.Add(UserChosenTargetType.femaleAtoms);
                    targetNameExclusions.Add(UserChosenTargetType.maleAtoms);
                    targetNameExclusions.Add(UserChosenTargetType.personAtoms);
                }

            }
            else if (buttonOrSliderTypeJSEnum.val == UIASliderType.vamTriggerParam  || (parentButton!=null && buttonCategory != UIAButtonCategory.pluginControl && buttonCategory != UIAButtonCategory.pluginSettings && buttonCategory != UIAButtonCategory.atomControl && buttonOrSliderTypeJSEnum.val != UIAButtonOpType.loadGeneralPreset && buttonCategory != UIAButtonCategory.nodeControl && buttonCategory != UIAButtonCategory.audio) || buttonOrSliderTypeJSEnum.val == UIAButtonOpType.togglePresetLocks || buttonOrSliderTypeJSEnum.val == UIAButtonOpType.triggerVAMAction)
            {
                if (targetCategoryJSEnum.val == TargetCategory.gazeSelectedAtom)
                {
                    targetNameExclusions.Add(LastViewedTargetType.lastViewedAtom);
                    targetNameExclusions.Add(LastViewedTargetType.lastViewedNonPerson);
                }
                else if (targetCategoryJSEnum.val == TargetCategory.vamSelectedAtom)
                {
                    targetNameExclusions.Add(LastSelectedTargetType.lastSelectedAtom);
                    targetNameExclusions.Add(LastSelectedTargetType.lastSelectedNonPerson);
                }
                else if (targetCategoryJSEnum.val == TargetCategory.atomGroup)
                {
                    targetNameExclusions.Add(AllAtomsTargetType.allAtoms);
                    targetNameExclusions.Add(AllAtomsTargetType.allNonPersonAtoms);
                }
                else if (targetCategoryJSEnum.val == TargetCategory.userChosenAtom || targetCategoryJSEnum.val == TargetCategory.triggerAtom)
                {
                    targetNameExclusions.Add(UserChosenTargetType.anyAtoms);
                    targetNameExclusions.Add(UserChosenTargetType.nonPersonAtoms);
                }                         
            }
            else if (parentButton != null && buttonCategory == UIAButtonCategory.nodeControl && buttonOrSliderTypeJSEnum.val != UIAButtonOpType.detachAtomRoot)
            {
                if (targetCategoryJSEnum.val == TargetCategory.gazeSelectedAtom)
                {
                    targetNameExclusions.Add(LastViewedTargetType.lastViewedAtom);
                }
                else if (targetCategoryJSEnum.val == TargetCategory.vamSelectedAtom)
                {
                    targetNameExclusions.Add(LastSelectedTargetType.lastSelectedAtom);
                }
                else if (targetCategoryJSEnum.val == TargetCategory.atomGroup)
                {
                    targetNameExclusions.Add(AllAtomsTargetType.allAtoms);
                }
                else if (targetCategoryJSEnum.val == TargetCategory.userChosenAtom || targetCategoryJSEnum.val == TargetCategory.triggerAtom)
                {
                    targetNameExclusions.Add(UserChosenTargetType.anyAtoms);
                }
            }
            return (targetNameExclusions);
        }

        protected List<string> GetCGTNames()
        {
            List<string> cgtNames = new List<string>();
            foreach (CustomTargetGroupType cgt in CustomTargetGroupSettings.customTargetGroupTypeList)
            {
                if (buttonOrSliderTypeJSEnum.val == UIAButtonOpType.loadSubScene && cgt.cgtAtomTypeJSEnum.val != AtomTypes.subScene) continue;
                if (parentButton != null && buttonCategory == UIAButtonCategory.audio && cgt.cgtAtomTypeJSEnum.val != AtomTypes.audioSource && cgt.cgtAtomTypeJSEnum.val != AtomTypes.person) continue;
                if (UIAButtonOpType.IsOnlyPersonAtomTargetable(buttonOrSliderTypeJSEnum.val) && cgt.cgtAtomCategoryJSEnum.val != AtomCategories.people) continue;

                cgtNames.Add(cgt.cgtNameJSS.val);
            }

            return cgtNames;
        }
        public void ButtonTypeUpdated()
        {
            RefreshTargetCatChoices();
            RefreshTargetNameChoices();
        }
        public void AtomNameUpdate(string oldName, string newName)
        {
            if (lastUserChosenAtomName == oldName) lastUserChosenAtomName = newName;
            if (lastAltUserChosenAtomName == oldName) lastAltUserChosenAtomName = newName;
            if (currentActionTargetAtomNames != null && currentActionTargetAtomNames.Contains(oldName))
            {
                currentActionTargetAtomNames.Remove(oldName);
                currentActionTargetAtomNames.Add(newName);
            }

            if (currentActionAltTargetAtomNames != null && currentActionAltTargetAtomNames.Contains(oldName))
            {
                currentActionAltTargetAtomNames.Remove(oldName);
                currentActionAltTargetAtomNames.Add(newName);
            }
        }

        public void AtomRemovedUpdate(string oldName)
        {
            if (lastUserChosenAtomName == oldName) lastUserChosenAtomName = "";
            if (lastAltUserChosenAtomName == oldName) lastAltUserChosenAtomName = "";

            if (currentActionTargetAtomNames!=null && currentActionTargetAtomNames.Contains(oldName)) currentActionTargetAtomNames.Remove(oldName);
            if (currentActionAltTargetAtomNames!=null && currentActionAltTargetAtomNames.Contains(oldName)) currentActionAltTargetAtomNames.Remove(oldName);
        }

        public void UpdateCGTName(string oldName, string newName)
        {
            if ((targetCategoryJSEnum.val == TargetCategory.gazeSelectedAtom || targetCategoryJSEnum.val == TargetCategory.userChosenAtom || targetCategoryJSEnum.val == TargetCategory.atomGroup || targetCategoryJSEnum.val == TargetCategory.triggerAtom) && targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.mainValFlag && targetNameJSMultiEnum.mainVal == oldName)
            {
                targetNameJSMultiEnum.mainVal = newName;
            }

            if ((altTargetCategoryJSEnum.val == TargetCategory.gazeSelectedAtom || altTargetCategoryJSEnum.val == TargetCategory.userChosenAtom || altTargetCategoryJSEnum.val == TargetCategory.atomGroup || targetCategoryJSEnum.val == TargetCategory.triggerAtom) && altTargetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.mainValFlag && altTargetNameJSMultiEnum.mainVal == oldName)
            {
                altTargetNameJSMultiEnum.mainVal = newName;
            }
        }

        public void RemoveCGTName(string cgtName)
        {
            if ((targetCategoryJSEnum.val == TargetCategory.gazeSelectedAtom || targetCategoryJSEnum.val == TargetCategory.userChosenAtom || targetCategoryJSEnum.val == TargetCategory.atomGroup || targetCategoryJSEnum.val == TargetCategory.triggerAtom) && targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.mainValFlag && targetNameJSMultiEnum.mainVal == cgtName)
            {
                RefreshTargetNameChoices();
            }

            if ((altTargetCategoryJSEnum.val == TargetCategory.gazeSelectedAtom || altTargetCategoryJSEnum.val == TargetCategory.userChosenAtom || altTargetCategoryJSEnum.val == TargetCategory.atomGroup || targetCategoryJSEnum.val == TargetCategory.triggerAtom) && altTargetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.mainValFlag && altTargetNameJSMultiEnum.mainVal == cgtName)
            {
                RefreshTargetNameChoices(altTargetCategoryJSEnum, altTargetNameJSMultiEnum,true,true);
            }
        }
        public List<string> GetUserTargetAtomChoices()
        {
            return GetUserTargetAtomChoices(targetCategoryJSEnum, targetNameJSMultiEnum);
        }
        public List<string> GetUserAltTargetAtomChoices()
        {
            return GetUserTargetAtomChoices(altTargetCategoryJSEnum, altTargetNameJSMultiEnum, true);
        }
        private List<string> GetUserTargetAtomChoices(JSONStorableEnumStringChooser targetCategoryJSEnum, JSONStorableMultiEnumStringChooser targetNameJSMultiEnum, bool altTarget = false)
        {
            List<string> choices = new List<string>();
            if (targetCategoryJSEnum.val == TargetCategory.userChosenAtom)
            {
                foreach (Atom atom in AtomUtils.GetAtoms(includeOffAtomsJSB.val))
                {
                    bool isMale = AtomUtils.IsMale(atom);
                    bool isFemale = AtomUtils.IsFemale(atom);

                    bool atomAvailable = false;
                    if (!altTarget && (parentButton == null|| buttonCategory == UIAButtonCategory.pluginSettings))
                    {
                        foreach (MVRScript plugin in PluginUtils.GetPluginsFromAtom(atom))
                        {
                            if (plugin.name.EndsWith(parentButtonOperation.pluginSettingComponent.pluginTypeJSSC.val))
                            {
                                atomAvailable = true;
                                break;
                            }
                        }
                    }
                    else if (parentButton != null && buttonCategory == UIAButtonCategory.audio)
                    {
                        if (atom.category =="Sound" || isMale || isFemale) atomAvailable = true;
                    }
                    else atomAvailable = true;
                    if (atomAvailable)
                    {
                        if (targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.topEnumValFlag)
                        {
                            var userChosenTargetType = targetNameJSMultiEnum.valTopEnum;
                            if (userChosenTargetType == UserChosenTargetType.anyAtoms || userChosenTargetType == UserChosenTargetType.nonPersonAtoms)
                            {
                                if (!includeHiddenAtomsJSB.val && atom.hidden) continue;
                                if (excludePNPAtomJSB.val && atom.type == "PlayerNavigationPanel") continue;
                                if (excludeWindowsCameraAtomJSB.val && atom.type == "WindowCamera") continue;
                            }
                            switch (userChosenTargetType)
                            {
                                case UserChosenTargetType.personAtoms:
                                    if (isMale || isFemale) choices.Add(atom.name);
                                    break;
                                case UserChosenTargetType.femaleAtoms:
                                    if (isFemale) choices.Add(atom.name);
                                    break;
                                case UserChosenTargetType.maleAtoms:
                                    if (isMale) choices.Add(atom.name);
                                    break;
                                case UserChosenTargetType.anyAtoms:
                                    choices.Add(atom.name);
                                    break;
                                case UserChosenTargetType.nonPersonAtoms:
                                    if (!isMale && !isFemale) choices.Add(atom.name);
                                    break;
                            }
                        }
                        else {
                            CustomTargetGroupType cgt = CustomTargetGroupSettings.GetCGTFromName(targetNameJSMultiEnum.mainVal);
                            if (cgt.IsInScope(atom, isMale, isFemale)) choices.Add(atom.name);
                        }
                    }
                }
            }
            return (choices);
        }

        private string SetCurrentActionUserChosenTargetAtoms(ref List<string> currentActionTargetAtomNames, UIAButton.UserAtomSelectedCallback TASSelected, bool altTarget = false)
        {
            if (currentActionTargetAtomNames != null && currentActionTargetAtomNames.Count == 1) return currentActionTargetAtomNames[0];
            else if (currentActionTargetAtomNames == null)
            {
                if (buttonOrSliderTypeJSEnum.val != UIAButtonOpType.activeClothingEditor)
                {
                    GridsDisplay._uiTargetAtomSelector.SetTargetChoices(this, TASSelected, keepOpenOptionAtomSelectorJSB.val, altTarget);
                    if (GridsDisplay._uiTargetAtomSelector.tasAtomChoiceNames.Count == 1 && parentButtonOperation.buttonOpTypeJSEnum.val!=UIAButtonOpType.deleteAtom) currentActionTargetAtomNames = new List<string> { GridsDisplay._uiTargetAtomSelector.tasAtomChoiceNames[0] };
                    else if (GridsDisplay._uiTargetAtomSelector.tasAtomChoiceNames.Count == 0) currentActionTargetAtomNames = new List<string>();
                    else
                    {
                        GameControlUI.gameControlDisplayMode = GameControlDisplayModes.atomSelect;
                        GridsDisplay._uiTargetAtomSelector.keepOpenJSB.val = keepOpenAtomSelectorJSB.val;
                        GameControlUI.RefreshWristUIButtonGrid();
                    }
                }
                else
                {
                    var personAtoms = AtomUtils.GetPersonAtoms();
                    if (personAtoms.Count > 0) currentActionTargetAtomNames = new List<string> { personAtoms[0].name };
                    else currentActionTargetAtomNames = new List<string>();
                }
                return "";
            }
            else return "";

        }

        public void SetCurrentActionTargetAtoms()
        {
            if (targetCategoryJSEnum.val != TargetCategory.userChosenAtom) currentActionTargetAtomNames = GetTargetAtomNames();
            else if (targetCategoryJSEnum.val == TargetCategory.userChosenAtom) lastUserChosenAtomName = SetCurrentActionUserChosenTargetAtoms(ref currentActionTargetAtomNames, TASSelected);
            else lastUserChosenAtomName = "";

            if (isAltTargetType)
            {
                if (altTargetCategoryJSEnum.val != TargetCategory.userChosenAtom) currentActionAltTargetAtomNames = GetAltTargetAtomNames();
                else if (altTargetCategoryJSEnum.val == TargetCategory.userChosenAtom) lastAltUserChosenAtomName = SetCurrentActionUserChosenTargetAtoms(ref currentActionAltTargetAtomNames, AltTASSelected,true);
                else lastAltUserChosenAtomName = "";
            }
        }

        public List<string> GetTargetAtomNames()
        {
            return GetTargetAtomNames(targetCategoryJSEnum, targetNameJSMultiEnum);
        }

        public List<string> GetAltTargetAtomNames()
        {
            return GetTargetAtomNames(altTargetCategoryJSEnum, altTargetNameJSMultiEnum, true);
        }

        private List<string> GetTargetAtomNames(JSONStorableEnumStringChooser targetCategoryJSEnum, JSONStorableMultiEnumStringChooser targetNameJSMultiEnum, bool altTarget = false)
        {
            List<string> targetAtomNames = new List<string>();                   
            if (parentSlider!=null || parentButtonOperation.IsComponentInButtonOpType(ButtonComponentTypes.targetComponent))
            {
                if (targetCategoryJSEnum.val == TargetCategory.gazeSelectedAtom || targetCategoryJSEnum.val == TargetCategory.vamSelectedAtom || targetCategoryJSEnum.val == TargetCategory.userChosenAtom || targetCategoryJSEnum.val == TargetCategory.specificAtom)
                {
                    string targetAtomName = GetTargetAtomName(targetCategoryJSEnum, targetNameJSMultiEnum,altTarget);
                    if (targetAtomName!="") targetAtomNames.Add(targetAtomName);
                }
                else if (targetCategoryJSEnum.val == TargetCategory.scenePlugins || targetCategoryJSEnum.val == TargetCategory.sessionPlugins) targetAtomNames.Add("CoreControl");
                else if (targetCategoryJSEnum.val == TargetCategory.atomGroup)
                {
                    foreach (Atom atomFromGroup in AtomUtils.GetAtoms(includeOffAtomsJSB.val))
                    {
                        bool isMale = AtomUtils.IsMale(atomFromGroup);
                        bool isFemale = AtomUtils.IsFemale(atomFromGroup);

                        if (targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.topEnumValFlag)
                        {
                            switch (targetNameJSMultiEnum.valTopEnum)
                            {
                                case AllAtomsTargetType.allAtoms:
                                    if (parentButton == null || buttonCategory != UIAButtonCategory.audio || atomFromGroup.category=="People" || atomFromGroup.category == "Sound") targetAtomNames.Add(atomFromGroup.name);
                                    break;
                                case AllAtomsTargetType.allPersonAtoms:
                                    if (isMale || isFemale) targetAtomNames.Add(atomFromGroup.name);
                                    break;
                                case AllAtomsTargetType.allFemaleAtoms:
                                    if (isFemale) targetAtomNames.Add(atomFromGroup.name);
                                    break;
                                case AllAtomsTargetType.allMaleAtoms:
                                    if (isMale) targetAtomNames.Add(atomFromGroup.name);
                                    break;
                                case AllAtomsTargetType.allNonPersonAtoms:
                                    if (!isMale && !isFemale && (parentButton == null || buttonCategory != UIAButtonCategory.audio || atomFromGroup.category == "Sound")) targetAtomNames.Add(atomFromGroup.name);
                                    break;
                            }
                        }
                        else if (targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.mainValFlag)
                        {
                            string cgtName = targetNameJSMultiEnum.mainVal;
                            CustomTargetGroupType cgt = CustomTargetGroupSettings.GetCGTFromName(cgtName);

                            if (cgt.IsInScope(atomFromGroup, isMale, isFemale)) targetAtomNames.Add(atomFromGroup.name);
                        }
                    }
                }
                else if (targetCategoryJSEnum.val == TargetCategory.triggerAtom)
                {
                    var targetAtom = parentButton.triggerEventAtom;
                    if (targetAtom != null)
                    {
                        bool isMale = AtomUtils.IsMale(targetAtom);
                        bool isFemale = AtomUtils.IsFemale(targetAtom);

                        if (targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.topEnumValFlag)
                        {
                            switch (targetNameJSMultiEnum.valTopEnum)
                            {
                                case UserChosenTargetType.anyAtoms:
                                    if (parentButton == null || buttonCategory != UIAButtonCategory.audio || targetAtom.category == "People" || targetAtom.category == "Sound") targetAtomNames.Add(targetAtom.name);
                                    break;
                                case UserChosenTargetType.personAtoms:
                                    if (isMale || isFemale) targetAtomNames.Add(targetAtom.name);
                                    break;
                                case UserChosenTargetType.femaleAtoms:
                                    if (isFemale) targetAtomNames.Add(targetAtom.name);
                                    break;
                                case UserChosenTargetType.maleAtoms:
                                    if (isMale) targetAtomNames.Add(targetAtom.name);
                                    break;
                                case UserChosenTargetType.nonPersonAtoms:
                                    if (!isMale && !isFemale && (parentButton == null || buttonCategory != UIAButtonCategory.audio || targetAtom.category == "Sound")) targetAtomNames.Add(targetAtom.name);
                                    break;
                            }
                        }
                        else if (targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.mainValFlag)
                        {
                            string cgtName = targetNameJSMultiEnum.mainVal;
                            CustomTargetGroupType cgt = CustomTargetGroupSettings.GetCGTFromName(cgtName);

                            if (cgt.IsInScope(targetAtom, isMale, isFemale)) targetAtomNames.Add(targetAtom.name);
                        }
                    }
                }
            }
            return targetAtomNames;
        }

        public string GetTargetAtomName()
        {
            return GetTargetAtomName(targetCategoryJSEnum, targetNameJSMultiEnum);
        }
        
        private string GetTargetAtomName(JSONStorableEnumStringChooser targetCategoryJSEnum, JSONStorableMultiEnumStringChooser targetNameJSMultiEnum, bool altTarget = false)
        {           
            switch (targetCategoryJSEnum.val)
            {
                case TargetCategory.gazeSelectedAtom:
                    if (targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.topEnumValFlag)
                    {
                        if (targetNameJSMultiEnum.valTopEnum == LastViewedTargetType.lastViewedPerson) return TargetControl.lastViewedPerson;
                        if (targetNameJSMultiEnum.valTopEnum == LastViewedTargetType.lastViewedFemale) return TargetControl.lastViewedFemale;
                        if (targetNameJSMultiEnum.valTopEnum == LastViewedTargetType.lastViewedMale) return TargetControl.lastViewedMale;
                        if (targetNameJSMultiEnum.valTopEnum == LastViewedTargetType.lastViewedAtom) return TargetControl.lastViewedAtom;
                        if (targetNameJSMultiEnum.valTopEnum == LastViewedTargetType.lastViewedNonPerson) return TargetControl.lastViewedNonPerson;
                    }
                    else if (TargetControl.lastViewedCTGDic.ContainsKey(targetNameJSMultiEnum.mainVal)) return TargetControl.lastViewedCTGDic[targetNameJSMultiEnum.mainVal];
                    break;
                case TargetCategory.vamSelectedAtom:
                    if (targetNameJSMultiEnum.valType == JSONStorableMultiEnumStringChooser.topEnumValFlag)
                    {
                        if (targetNameJSMultiEnum.valTopEnum == LastSelectedTargetType.lastSelectedPerson) return TargetControl.lastSelectedPerson;
                        if (targetNameJSMultiEnum.valTopEnum == LastSelectedTargetType.lastSelectedFemale) return TargetControl.lastSelectedFemale;
                        if (targetNameJSMultiEnum.valTopEnum == LastSelectedTargetType.lastSelectedMale) return TargetControl.lastSelectedMale;
                        if (targetNameJSMultiEnum.valTopEnum == LastSelectedTargetType.lastSelectedAtom) return TargetControl.lastSelectedAtom;
                        if (targetNameJSMultiEnum.valTopEnum == LastSelectedTargetType.lastSelectedNonPerson) return TargetControl.lastSelectedNonPerson;
                    }
                    break;
                case TargetCategory.userChosenAtom:
                    string lastUserChosenAtomName = this.lastUserChosenAtomName;
                    if (altTarget) lastUserChosenAtomName = lastAltUserChosenAtomName;
                    List<string> choices = GetUserTargetAtomChoices(targetCategoryJSEnum, targetNameJSMultiEnum,altTarget);
                    if (choices.Count == 1) return choices[0];
                    if ((parentSlider!=null || parentButton.maxButtonStates >1 || parentButton.forceToggleButtonOpsJSB.val ) && choices.Contains(lastUserChosenAtomName)) return lastUserChosenAtomName;
                    break;
                case TargetCategory.specificAtom:
                    return targetNameJSMultiEnum.mainVal;
                case TargetCategory.atomGroup:
                    if (UIAConsts.debugLogging) SuperController.LogMessage("UIA.TargetButtonComponent.GetTargetAtomName: attempt to get atom name for button with Group target");
                    break;
                case TargetCategory.scenePlugins:
                case TargetCategory.sessionPlugins:
                    if (UIAConsts.debugLogging) SuperController.LogMessage("UIA.TargetButtonComponent.GetTargetAtomName: attempt to get atom name for button with Scene/Session plugin target");
                    break;
                case TargetCategory.triggerAtom:
                    if (UIAConsts.debugLogging) SuperController.LogMessage("UIA.TargetButtonComponent.GetTargetAtomName: attempt to get atom name for button with Trigger Atom target");
                    break;
            }

            return "";
        }
        public void TASSelected(List<Atom> atoms)
        {
            currentActionTargetAtomNames = new List<string>();
            if (atoms.Count == 1)
            {
                Atom atom = atoms[0];
                if (atom != null)
                {
                    currentActionTargetAtomNames.Add(atom.name);
                    int buttonState = parentButton.GetButtonState();
                    if ((parentButton.maxButtonStates > 1 || parentButton.forceToggleButtonOpsJSB.val) && buttonState==ButtonState.inactive) lastUserChosenAtomName = atom.name;
                    else lastUserChosenAtomName = "";
                
                }
            }
            else if (atoms.Count==0)
            {
                lastUserChosenAtomName = "";            
            }
            else if (UIAConsts.debugLogging) SuperController.LogMessage("UIA.TargetButtonComponent.TASSelected: Unexpected number of atoms supplied as param");

            parentButton.CheckActionTargets();
        }

        public void AltTASSelected(List<Atom> atoms)
        {
            currentActionAltTargetAtomNames = new List<string>();
            if (atoms.Count == 1)
            {
                Atom atom = atoms[0];
                if (atom != null)
                {
                    currentActionAltTargetAtomNames.Add(atom.name);
                    int buttonState = parentButton.GetButtonState();
                    if ((parentButton.maxButtonStates > 1 || parentButton.forceToggleButtonOpsJSB.val) && buttonState == ButtonState.inactive) lastAltUserChosenAtomName = atom.name;
                    else lastAltUserChosenAtomName = "";
                    
                }
            }
            if (atoms.Count == 0) lastAltUserChosenAtomName = "";
            else if (UIAConsts.debugLogging) SuperController.LogMessage("UIA.TargetButtonComponent.AltTASSelected: Unexpected number of atoms supplied as param");

            parentButton.CheckActionTargets();
        }


        public bool SpawnAtomIfTargetMissingToggleAvailable()
        {
            if (buttonOrSliderTypeJSEnum.val == UIAButtonOpType.loadGeneralPreset && targetCategoryJSEnum.val == TargetCategory.specificAtom && specificAtomTypeJSS.val != "Person" && specificAtomTypeJSS.val != "PlayerNavigationPanel" && specificAtomTypeJSS.val != "WindowCamera") return true;
            return (false);
        }
        public bool SpawnAtomIfTargetMissing()
        {
            if (spawnAtomIfTargetMissingJSBool.val && SpawnAtomIfTargetMissingToggleAvailable()) return true;

            return (false);
        }
    }


    public class ButtonSkinComponent : ButtonComponentBase
    {
        public ButtonFontSizeRange fontSize;
        public ButtonFontSizeRange defaultMicroFontSize;
        public ButtonFontSizeRange defaultMiniFontSize;
        public ButtonFontSizeRange defaultSmallFontSize;
        public ButtonFontSizeRange defaultMediumFontSize;
        public ButtonFontSizeRange defaultLargeFontSize;

        public ButtonSkinToggleParam buttonSkinOnParams ;
        public ButtonSkinToggleParam buttonSkinOffParams;


        public JSONStorableBool fontSizeDefault = new JSONStorableBool("fontSizeDefault", true);

        public JSONStorableStringChooser textAlignment = new JSONStorableStringChooser("textAlignment",null, "MiddleCenter","");

        public JSONStorableBool textAlignmentDefault = new JSONStorableBool("textAlignmentDefault", true);

        public JSONStorableStringChooser buttonFont = new JSONStorableStringChooser("buttonFont",null, "Arial","");

        public JSONStorableBool buttonFontDefault = new JSONStorableBool("buttonFontDefault", true);

        public int maxButtonStates
        {
            get
            {
                if (defaultSkin) return 2;
                return parentButton.maxButtonStates;
            }
        }

        public JSONStorableBool usePresetButtonTexture ;
        private bool defaultSkin ;

        public ButtonSkinComponent(UIAButton parent) : base(parent)
        {
            try
            {
                usePresetButtonTexture = new JSONStorableBool("usePresetButtonTexture", true, UsePresetThumbnailUpdated);
                defaultSkin = parent==null;

                if (defaultSkin)
                {
                    defaultMicroFontSize = new ButtonFontSizeRange(3f, 12f);
                    defaultMiniFontSize = new ButtonFontSizeRange(3f, 15f);
                    defaultSmallFontSize = new ButtonFontSizeRange(4f, 15f);
                    defaultMediumFontSize = new ButtonFontSizeRange(4f, 25f);
                    defaultLargeFontSize = new ButtonFontSizeRange(4f, 35f);
                }
                else
                {
                    fontSize = new ButtonFontSizeRange(4f, 25f);
                    RegisterParam(fontSizeDefault);
                    RegisterParam(textAlignmentDefault);
                    RegisterParam(buttonFontDefault);
                    RegisterParam(usePresetButtonTexture);
                }

                List<string> fontPopupChoices = new List<string>();
                fontPopupChoices.Add("Arial");
                fontPopupChoices.Add("chintzy");
                fontPopupChoices.Add("DroidSansMono");
                fontPopupChoices.Add("Laffayette_Comic_Pro");
                buttonFont.choices = fontPopupChoices;

                List<string> alignmentPopupChoices = new List<string>();
                alignmentPopupChoices.Add("MiddleCenter");
                alignmentPopupChoices.Add("LowerCenter");
                alignmentPopupChoices.Add("UpperCenter");
                alignmentPopupChoices.Add("MiddleLeft");
                alignmentPopupChoices.Add("LowerLeft");
                alignmentPopupChoices.Add("UpperLeft");
                alignmentPopupChoices.Add("MiddleRight");
                alignmentPopupChoices.Add("LowerRight");
                alignmentPopupChoices.Add("UpperRight");
                textAlignment.choices = alignmentPopupChoices;

                buttonSkinOnParams = new ButtonSkinToggleParam(this);
                buttonSkinOffParams = new ButtonSkinToggleParam(this);
                buttonSkinOnParams.buttonColor.val = new HSVColor() { V = 0.84f };
                buttonSkinOffParams.buttonColor.val = new HSVColor() { V = 0.4f };

                RegisterParam(textAlignment);                
                RegisterParam(buttonFont);                
            }
            catch (Exception e) { SuperController.LogError("Exception caught: " + e); }
        }
        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            JSONClass jc = base.GetJSON(jspLoadExclusions);
            if (defaultSkin)
            {
                jc["defaultMicroFontSize"] = defaultMicroFontSize.GetJSON();
                jc["defaultMiniFontSize"] = defaultMiniFontSize.GetJSON();
                jc["defaultSmallFontSize"] = defaultSmallFontSize.GetJSON();
                jc["defaultMediumFontSize"] = defaultMediumFontSize.GetJSON();
                jc["defaultLargeFontSize"] = defaultLargeFontSize.GetJSON();
            }
            else jc["fontSize"] = fontSize.GetJSON();

            jc["buttonSkinOnParams"] = buttonSkinOnParams.GetJSON();
            if (maxButtonStates > 1 || parentButton.forceToggleButtonOpsJSB.val) jc["buttonSkinOffParams"] = buttonSkinOffParams.GetJSON();

            return jc;
        }

        public void LoadJSON(JSONClass jc, string uiapPackageName, int uiapFormatVersion)
        {
            base.RestoreFromJSON(jc);

            if (uiapFormatVersion == 1)
            {
                if (defaultSkin)
                {
                    if (jc["minMicroFontSize"] != null) jc["defaultMicroFontSize"]["minFontSize"] = jc["minMicroFontSize"];
                    if (jc["maxMicroFontSize"] != null) jc["defaultMicroFontSize"]["maxFontSize"] = jc["maxMicroFontSize"];

                    if (jc["minMiniFontSize"] != null) jc["defaultMiniFontSize"]["minFontSize"] = jc["minMiniFontSize"];
                    if (jc["maxMiniFontSize"] != null) jc["defaultMiniFontSize"]["maxFontSize"] = jc["maxMiniFontSize"];

                    if (jc["minSmallFontSize"] != null) jc["defaultSmallFontSize"]["minFontSize"] = jc["minSmallFontSize"];
                    if (jc["maxSmallFontSize"] != null) jc["defaultSmallFontSize"]["maxFontSize"] = jc["maxSmallFontSize"];

                    if (jc["minMediumFontSize"] != null) jc["defaultMediumFontSize"]["minFontSize"] = jc["minMediumFontSize"];
                    if (jc["maxMediumFontSize"] != null) jc["defaultMediumFontSize"]["maxFontSize"] = jc["maxMediumFontSize"];

                    if (jc["minLargeFontSize"] != null) jc["defaultLargeFontSize"]["minFontSize"] = jc["minLargeFontSize"];
                    if (jc["maxLargeFontSize"] != null) jc["defaultLargeFontSize"]["maxFontSize"] = jc["maxLargeFontSize"];
                }
                else
                {
                    if (jc["minFontSize"] != null) jc["fontSize"]["minFontSize"] = jc["minFontSize"];
                    if (jc["maxFontSize"] != null) jc["fontSize"]["maxFontSize"] = jc["maxFontSize"];
                }

                if (jc["textOnColor"] != null)
                {
                    jc["buttonSkinOnParams"]["textColor"]["h"] = jc["textOnColor"]["H"];
                    jc["buttonSkinOnParams"]["textColor"]["s"] = jc["textOnColor"]["S"];
                    jc["buttonSkinOnParams"]["textColor"]["v"] = jc["textOnColor"]["V"];
                }
                if (jc["textOnColorDefault"] != null) jc["buttonSkinOnParams"]["textColorUseDefault"] = jc["textOnColorDefault"];
                if (jc["textOffColor"] != null && (maxButtonStates > 1|| defaultSkin || parentButton.forceToggleButtonOpsJSB.val))
                {
                    jc["buttonSkinOffParams"]["textColor"]["h"] = jc["textOffColor"]["H"];
                    jc["buttonSkinOffParams"]["textColor"]["s"] = jc["textOffColor"]["S"];
                    jc["buttonSkinOffParams"]["textColor"]["v"] = jc["textOffColor"]["V"];
                }
                if (jc["textOffColorDefault"] != null && maxButtonStates > 1 || parentButton.forceToggleButtonOpsJSB.val) jc["buttonSkinOffParams"]["textColorUseDefault"] = jc["textOffColorDefault"];

                if (jc["buttonOnColor"] != null)
                {
                    jc["buttonSkinOnParams"]["buttonColor"]["h"] = jc["buttonOnColor"]["H"];
                    jc["buttonSkinOnParams"]["buttonColor"]["s"] = jc["buttonOnColor"]["S"];
                    jc["buttonSkinOnParams"]["buttonColor"]["v"] = jc["buttonOnColor"]["V"];
                }
                if (jc["buttonOnColorDefault"] != null) jc["buttonSkinOnParams"]["buttonColorUseDefault"] = jc["buttonOnColorDefault"];
                if (jc["buttonOffColor"] != null && (maxButtonStates > 1 || parentButton.forceToggleButtonOpsJSB.val || defaultSkin))
                {
                    jc["buttonSkinOffParams"]["buttonColor"]["h"] = jc["buttonOffColor"]["H"];
                    jc["buttonSkinOffParams"]["buttonColor"]["s"] = jc["buttonOffColor"]["S"];
                    jc["buttonSkinOffParams"]["buttonColor"]["v"] = jc["buttonOffColor"]["V"];
                }
                if (jc["buttonOffColorDefault"] != null && maxButtonStates > 1 || parentButton.forceToggleButtonOpsJSB.val) jc["buttonSkinOffParams"]["buttonColorUseDefault"] = jc["buttonOffColorDefault"];

                if (jc["customButtonOnTextureURL"] != null) jc["buttonSkinOnParams"]["customTextureURL"] = jc["customButtonOnTextureURL"];
                if (jc["customButtonOnTextureDefault"] != null) jc["buttonSkinOnParams"]["customTextureURLUseDefault"] = jc["customButtonOnTextureDefault"];
                if (jc["customButtonOffTextureURL"] != null) jc["buttonSkinOffParams"]["customTextureURL"] = jc["customButtonOffTextureURL"];
                if (jc["customButtonOffTextureDefault"] != null) jc["buttonSkinOffParams"]["customTextureURLUseDefault"] = jc["customButtonOffTextureDefault"];
            }

            if (defaultSkin)
            {
                defaultMicroFontSize.RestoreFromJSON(jc["defaultMicroFontSize"].AsObject);
                defaultMiniFontSize.RestoreFromJSON(jc["defaultMiniFontSize"].AsObject);
                defaultSmallFontSize.RestoreFromJSON(jc["defaultSmallFontSize"].AsObject);
                defaultMediumFontSize.RestoreFromJSON(jc["defaultMediumFontSize"].AsObject);
                defaultLargeFontSize.RestoreFromJSON(jc["defaultLargeFontSize"].AsObject);
            }
            else fontSize.RestoreFromJSON(jc["fontSize"].AsObject);

            buttonSkinOnParams.LoadJSON(jc["buttonSkinOnParams"].AsObject, uiapPackageName);
            if (jc["buttonSkinOffParams"] != null) buttonSkinOffParams.LoadJSON(jc["buttonSkinOffParams"].AsObject, uiapPackageName);
        }

        public void CopyFrom(ButtonSkinComponent sourceBS)
        {
            base.CopyFrom(sourceBS);

            if (defaultSkin)
            {
                defaultMicroFontSize.CopyFrom(sourceBS.defaultMicroFontSize);
                defaultMiniFontSize.CopyFrom(sourceBS.defaultMiniFontSize);
                defaultSmallFontSize.CopyFrom(sourceBS.defaultSmallFontSize);
                defaultMediumFontSize.CopyFrom(sourceBS.defaultMediumFontSize);
                defaultLargeFontSize.CopyFrom(sourceBS.defaultLargeFontSize);
            }
            else fontSize.CopyFrom(sourceBS.fontSize);

            buttonSkinOnParams.CopyFrom(sourceBS.buttonSkinOnParams);
            if (sourceBS.maxButtonStates > 1 || sourceBS.parentButton.forceToggleButtonOpsJSB.val) buttonSkinOffParams.CopyFrom(sourceBS.buttonSkinOffParams);


        }

        private void UsePresetThumbnailUpdated(bool usePreset)
        {
            parentButton.UpdateThumbnailImage(parentButton.buttonTexture);
        }
    }
    public class ButtonFontSizeRange : JSONStorableObject
    {
        public JSONStorableFloat minFontSize;
        public JSONStorableFloat maxFontSize;

        public ButtonFontSizeRange(float defaultMinFontSize, float defaultMaxFontSize)
        {
            minFontSize = new JSONStorableFloat("minFontSize", defaultMinFontSize, 2f, 40f);
            maxFontSize = new JSONStorableFloat("maxFontSize", defaultMaxFontSize, 2f, 40f);

            RegisterParam(minFontSize);
            RegisterParam(maxFontSize);
        }
    }

    public class ButtonSkinToggleParam : JSONStorableObject
    {
        public JSONStorableColor textColor = new JSONStorableColor("textColor", new HSVColor());
        public JSONStorableColor buttonColor = new JSONStorableColor("buttonColor", new HSVColor());
        public JSONStorableString customThumbnailURL;

        public JSONStorableBool textColorUseDefault = new JSONStorableBool("textColorUseDefault", true);
        public JSONStorableBool buttonColorUseDefault = new JSONStorableBool("buttonColorUseDefault", true);
        public JSONStorableBool customTextureURLUseDefault;
        public Texture2D buttonTexture;

        private ButtonSkinComponent parentSkinComponent;

        public ButtonSkinToggleParam(ButtonSkinComponent parent)
        {
            customThumbnailURL = new JSONStorableString("customTextureURL", "", QueueLoadTexture);
            customTextureURLUseDefault = new JSONStorableBool("customTextureURLUseDefault", true, TextureUseDefault);


            parentSkinComponent = parent;

            RegisterParam(textColor);
            RegisterParam(buttonColor);
            RegisterParam(customThumbnailURL);

            RegisterParam(textColorUseDefault);
            RegisterParam(buttonColorUseDefault);
            RegisterParam(customTextureURLUseDefault);
        }

        public void CopyFrom(ButtonSkinToggleParam source)
        {
            base.CopyFrom(source);
            buttonTexture = source.buttonTexture;
            RefreshButtonTexture();
        }

        public void LoadJSON(JSONClass jc, string uiapPackageName)
        {
            base.RestoreFromJSON(jc);

            if (customThumbnailURL.val != "")
            {
                string normalizedPath = FileManagerSecure.NormalizePath(customThumbnailURL.val);
                if (uiapPackageName != "" && !normalizedPath.Contains(":") && FileManagerSecure.FileExists(uiapPackageName + ":/" + normalizedPath)) customThumbnailURL.val = uiapPackageName + ":/" + normalizedPath;
            }
        }

        private void QueuedImageLoadCallback(ImageLoaderThreaded.QueuedImage qi)
        {
            Texture2D tex = qi.tex;
            buttonTexture = tex;
            RefreshButtonTexture();
        }

        public void QueueLoadTexture(string url)
        {
            var fileExists = false;
            if (!string.IsNullOrEmpty(url))
            {
                var normalizedPath = SuperController.singleton.NormalizeLoadPath(url);
                try
                {
                    // This will cause an exception if the path is in unsecure locations
                    fileExists = FileManagerSecure.FileExists(normalizedPath);
                }
                catch
                {
                    fileExists = false;
                }
                    
            }

            if (string.IsNullOrEmpty(url) || !fileExists)
            {
                buttonTexture = null;
                RefreshButtonTexture();
                return;
            }
            ImageUtils.QueueLoadTexture(SuperController.singleton.NormalizeLoadPath(url), QueuedImageLoadCallback);
        }

        private void TextureUseDefault(bool useDefault)
        {
            RefreshButtonTexture();
        }
        private void RefreshButtonTexture()
        {
            if (parentSkinComponent.parentButton == null) GameControlUI.RefreshWristUIButtonGrid();
            else parentSkinComponent.parentButton.UpdateThumbnailImage(buttonTexture);  
        }
    }

    public class UIASlider : JSONStorableObject
    {
        public UIAButtonGrid parentGrid { get; private set; }

        public JSONStorableFloat sliderValueJSF { get; private set; }

        private JSONStorableFloat linkedSliderValueJSF { get; set; }

        public JSONStorableEnumStringChooser sliderTypeJSEnum { get; private set; }

        public TargetComponent targetComponent { get; private set; }

        private UIDynamicSlider uiDynamicSlider { get; set; } = null;

        public VAMTriggerActionComponent vamTriggerActionComponent { get; private set; }
        public PluginSettingComponent pluginSettingComponent { get; private set; }

        public bool isVAMTriggerParamSlider { get { return sliderTypeJSEnum.val == UIASliderType.vamTriggerParam; } }
        public bool isPluginSettingSlider { get { return sliderTypeJSEnum.val == UIASliderType.pluginSettingParam; } }
        public UIASlider(UIAButtonGrid _parent)
        {
            parentGrid = _parent;
            sliderValueJSF = new JSONStorableFloat("sliderValue", 0f, UIASliderValueChange, 0f, 1f, true);
            sliderTypeJSEnum = new JSONStorableEnumStringChooser("sliderType", UIASliderType.enumManifestName, UIASliderType.vamTriggerParam, "Slider Type", SliderTypeChanged);

            targetComponent = new TargetComponent(sliderTypeJSEnum, this);

            RegisterParam(sliderTypeJSEnum);
//            RegisterParam(sliderValueJSF);

            vamTriggerActionComponent = new VAMTriggerActionComponent(this);
// TBD
//            pluginSettingComponent = new PluginSettingComponent(this);
        }

        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {

            JSONClass jc = base.GetJSON(jspLoadExclusions);

            jc["targetComponent"] = targetComponent.GetJSON(jspLoadExclusions);
            if (isVAMTriggerParamSlider) jc["vamTriggerComponent"] = vamTriggerActionComponent.GetJSON(jspLoadExclusions);
            if (isPluginSettingSlider) jc["pluginSettingComponent"] = pluginSettingComponent.GetJSON(jspLoadExclusions);
            return jc;
        }

        public override void RestoreFromJSON(JSONClass jc, string _paramName = null)
        {
                base.RestoreFromJSON(jc, _paramName);

            targetComponent.RestoreFromJSON(jc["targetComponent"].AsObject, _paramName);
            if (jc["vamTriggerComponent"] != null) vamTriggerActionComponent.RestoreFromJSON(jc["vamTriggerComponent"].AsObject, _paramName);
            if (jc["pluginSettingComponent"] != null) pluginSettingComponent.RestoreFromJSON(jc["pluginSettingComponent"].AsObject, _paramName);
        }

        public void CopyFrom(UIASlider slider)
        {
            base.CopyFrom(slider);
            targetComponent.CopyFrom(slider.targetComponent);

            if (isVAMTriggerParamSlider) vamTriggerActionComponent.CopyFrom(slider.vamTriggerActionComponent);
            if (isPluginSettingSlider) pluginSettingComponent.CopyFrom(slider.pluginSettingComponent);

        }

        public void RegisterUIDynamicSlider(UIDynamicSlider uds)
        {
            uiDynamicSlider = uds;
            if (linkedSliderValueJSF != null) uiDynamicSlider.label = linkedSliderValueJSF.name;
        }

        protected void SliderTypeChanged(int sliderType)
        {

            targetComponent.ButtonTypeUpdated();

            // TBD remove?
            // if (IsComponentInButtonOpType(ButtonComponentTypes.pluginSettingComponent)) pluginSettingComponent.ButtonTypeUpdated();
            // parentButton.UpdateThumbnailImage(parentButton.buttonTexture);
        }

        public void CheckLinkedSliderChange()
        {
            var targetAtomNames = targetComponent.GetTargetAtomNames();

            if (targetAtomNames.Count > 0)
            {
                var targetAtomName = targetAtomNames.First();
                Atom atom = SuperController.singleton.GetAtomByUid(targetAtomName);
                if (atom != null)
                {
                    JSONStorableFloat targetJSF = null;
                    if (isVAMTriggerParamSlider)
                    {
                        targetJSF = vamTriggerActionComponent.GetTargetFloatParam(atom);
                    }
                    else if (isPluginSettingSlider)
                    {
                        //TBD
                    }

                    if (targetJSF != null)
                    {
                        

                        if (linkedSliderValueJSF == targetJSF) return;
                        if (linkedSliderValueJSF != null) linkedSliderValueJSF.setJSONCallbackFunction -= LinkedSliderValueChanged;
                        linkedSliderValueJSF = targetJSF;
                        LinkedSliderValueChanged(linkedSliderValueJSF);
                        if (uiDynamicSlider!=null) uiDynamicSlider.label = linkedSliderValueJSF.name;
                        linkedSliderValueJSF.setJSONCallbackFunction += LinkedSliderValueChanged;
                        return;
                    }
                }
                
            }
            if (linkedSliderValueJSF != null) linkedSliderValueJSF.setJSONCallbackFunction -= LinkedSliderValueChanged;
            linkedSliderValueJSF = null;

            sliderValueJSF.val = 0f;
            sliderValueJSF.min = -1f;
            sliderValueJSF.max = 1f;
            sliderValueJSF.defaultVal = 0f;
            sliderValueJSF.constrained = true;
            if (sliderValueJSF.slider!=null) sliderValueJSF.slider.interactable = false;
        }

        public void ForceValRefresh()
        {
            if (linkedSliderValueJSF != null)
            {
                LinkedSliderValueChanged(linkedSliderValueJSF);
            }
        }

        protected void LinkedSliderValueChanged(JSONStorableFloat jsf)
        {
            if (sliderValueJSF != null)
            {
                sliderValueJSF.valNoCallback = jsf.val;
                sliderValueJSF.min = jsf.min;
                sliderValueJSF.max = jsf.max;
                sliderValueJSF.defaultVal = jsf.defaultVal;
                sliderValueJSF.constrained = jsf.constrained;
                if (sliderValueJSF.slider != null) sliderValueJSF.slider.interactable = true;
            }
            
        }

        protected void UIASliderValueChange(float val)
        {
            if (linkedSliderValueJSF != null)
            {
                linkedSliderValueJSF.val = val;
            }
        }
    }

    public class UIAButton : JSONStorableObject
    {

        public static Dictionary<string, List<string>> presetMergeClothingGeometryIDs = new Dictionary<string, List<string>>();
        public static Dictionary<string, JSONClass> cachedClothingPresetFiles = new Dictionary<string, JSONClass>();

        public static Dictionary<string, Dictionary<string,string>> presetUndressClothingGeometryIDsToSimStorables = new Dictionary<string, Dictionary<string, string>>();

        public List<UIAButtonOperation> buttonOperations = new List<UIAButtonOperation>();

        public static bool hudOnPreButtonAction = false;
        public delegate void UserAtomSelectedCallback(List<Atom> atoms);

        public int vrHand { get; private set; } = LeftRight.neither;
        public bool leapActivated { get; private set; }

        public JSONStorableEnumStringChooser triggerFunctionTypeJSEnum;
        public bool isTriggerFunctionsGrid { get { return parentGrid != null && parentGrid.isTriggerFunctionsGrid; } }
        private HashSet<UIAButtonOperation> parentGlobalFunctionOps = new HashSet<UIAButtonOperation>();
        private List<string> phrases = new List<string>();
        private KeywordRecognizer phraseRecognizer;

        public string recognizedPhrases { get { return string.Join(",", phrases.ToArray()); }
            set {
                phrases = value.Split(',').ToList();
                StartPhraseRecognizer();
            }
        }

        public void StartPhraseRecognizer()
        {
            StopPhraseRecognizer();
            if (phrases == null || phrases.Count == 0)
                return;
            List<string> trimmedKeywords = new List<string>(phrases);
            trimmedKeywords.RemoveAll(k => string.IsNullOrEmpty(k));
            if (trimmedKeywords.Count == 0)
                return;

            try
            {
                if (PhraseRecognitionSystem.Status != SpeechSystemStatus.Running)
                    PhraseRecognitionSystem.Restart();         

                phraseRecognizer = new KeywordRecognizer(trimmedKeywords.ToArray(), ConfidenceLevel.Low);
                phraseRecognizer.OnPhraseRecognized += OnPhraseRecognized;
                phraseRecognizer.Start();
            }
            catch (Exception e)
            {
                SuperController.LogError($"UIA.UIAButton.recognizedPhrases: Failed to load Keyword phrases for {buttonRef}.\n"+e);
            }
        }

        public void StopPhraseRecognizer()
        {
            if (phraseRecognizer != null)
            {
                phraseRecognizer.Stop();
                phraseRecognizer.OnPhraseRecognized -= OnPhraseRecognized;
                phraseRecognizer.Dispose();
                phraseRecognizer = null;
            }
        }
        private void OnPhraseRecognized(PhraseRecognizedEventArgs args)
        {
            parentGrid.OnTriggerEvent(TriggerFuncType.onPhraseRecognized, null,args.text);
        }

        public JSONStorableBool disableOnAtomAddedTriggerDuringSceneLoadJSB ;
        public JSONStorableBool disableOnAtomAddedTriggerDuringUIASpawnJSB;
        public Atom triggerEventAtom { get; private set; }

        private HashSet<UIAButton> parentGlobalFunctions
        {
            get
            {
                var _parentGlobalFunctions = new HashSet<UIAButton>();
                foreach (var op in parentGlobalFunctionOps) _parentGlobalFunctions.Add(op.parentButton);
                return _parentGlobalFunctions;
            }
        }
        public void OnTriggerEvent(int triggerEventType, Atom atom = null, string phrase = "")
        {
            IEnumerable<string> trimmedPhrases = phrases.Select(s => s.Trim());
            if (isTriggerFunctionsGrid && triggerFunctionTypeJSEnum.val == triggerEventType && (triggerEventType != TriggerFuncType.onAtomAdded || !UIAGlobals.uiaAtomSpawnInProgress || !disableOnAtomAddedTriggerDuringUIASpawnJSB.val) && (triggerEventType!= TriggerFuncType.onAtomAdded || !SuperController.singleton.isLoading || !disableOnAtomAddedTriggerDuringSceneLoadJSB.val) &&(triggerEventType!=TriggerFuncType.onPhraseRecognized || trimmedPhrases.Contains(phrase)) && !actionInProgress)
            {
                triggerEventAtom = atom;
                if (SuperController.singleton.mainHUD == null) hudOnPreButtonAction = false;
                else hudOnPreButtonAction = SuperController.singleton.mainHUD.gameObject.activeInHierarchy;   
                ActionInit(LeftRight.neither, false);
            }
        }


        public void AddParentGFOps(UIAButtonOperation gfo)
        {
            parentGlobalFunctionOps.Add(gfo);
        }

        public void RemoveParentGFOps(UIAButtonOperation gfo)
        {
            parentGlobalFunctionOps.Remove(gfo);
        }

        public HashSet<UIAButton> GetAllAncestorGFs()
        {
            HashSet<UIAButton> allActiveAncestorGFs = new HashSet<UIAButton> ();

            foreach (var gf in parentGlobalFunctions)
            {
                if (gf.GetActiveChildGFs().Contains(this))
                {
                    allActiveAncestorGFs.Add(gf);
                    allActiveAncestorGFs.UnionWith(gf.GetAllAncestorGFs());                    
                }                
            }

            return allActiveAncestorGFs;
        }

        public HashSet<UIAButton> GetAllDescendentGFs()
        {
            HashSet<UIAButton> allDescendentGFs = new HashSet<UIAButton>();

            allDescendentGFs.Add(this);
            foreach (var buttonOperation in buttonOperations)
            {
                allDescendentGFs.UnionWith( buttonOperation.GetAllDescendentGFs());
            }
            
            return allDescendentGFs;
        }

        public HashSet<UIAButton> GetActiveChildGFs()
        {
            HashSet<UIAButton> allChildGFs = new HashSet<UIAButton>();

            foreach (var buttonOperation in buttonOperations)
            {
                if (buttonOperation.linkedGlobalFunction != null) allChildGFs.Add(buttonOperation.linkedGlobalFunction);
            }
            return allChildGFs;
        }



        public Texture2D buttonTexture {
            get
            {
                FileReference fileRef = GetFirstThumbnailFileReferences();
                if (fileRef != null && buttonSkinComponent.usePresetButtonTexture.val)
                {
                    return fileRef.buttonTexture;
                }
                else 
                {
                    ButtonSkinToggleParam toggleParam = buttonSkinComponent.buttonSkinOffParams;
                    if (GetButtonState() == ButtonState.inactive) toggleParam = buttonSkinComponent.buttonSkinOnParams;

                    if (!toggleParam.customTextureURLUseDefault.val) return toggleParam.buttonTexture;
                }
                return null;
            }
        }

        public bool ContainsOneBlankOperations()
        {
            if (buttonOperations.Count == 1 && ContainsAllBlankOperations()) return true;
            return false;
        }

        public bool ContainsAllBlankOperations()
        {
            foreach (UIAButtonOperation buttonOp in buttonOperations)
            {
                if (buttonOp.buttonOpTypeJSEnum.val != UIAButtonOpType.blank) return (false);
            }
            return true;
        }

        public bool treeBrowserCollapsed = false;

        public JSONStorableString buttonLabelOnJSString;
        public JSONStorableString buttonLabelOffJSString;
        public JSONStorableBool autoLabelJSBool;
        public JSONStorableBool hideLabelJSB;  
        public ButtonSkinComponent buttonSkinComponent;
        public JSONStorableBool forceToggleButtonOpsJSB;
        public int maxButtonStates
        {
            get
            {
                foreach (UIAButtonOperation buttonOp in buttonOperations)
                {
                    if (buttonOp.maxButtonOpStates == 2) return 2;
                }
                return 1;
            }
        }
        private int lastButtonState = ButtonState.inactive;
        public int GetButtonState()
        {
            if (maxButtonStates == 1)
            {
                if (forceToggleButtonOpsJSB.val) return lastButtonState;
                return ButtonState.inactive;
            }
            foreach (UIAButtonOperation buttonOp in buttonOperations)
            {
                if (buttonOp.GetButtonOpState() == ButtonState.active) return ButtonState.active;
            }
            return ButtonState.inactive;
        }

        private void ForceToggleButtonOpsModified(bool value)
        {
            lastButtonState = ButtonState.inactive;
        }

        public UIAButtonGrid parentGrid { get; set; }
        public string buttonRef { get
            {
                return parentGrid.GetButtonRCRef(this);
            }
        }

        public int buttonColumn
        {
            get
            {
                return parentGrid.GetButtonColRefInGrid(this);
            }
        }
        public int buttonRow
        {
            get
            {
                return parentGrid.GetButtonRowRefInGrid(this);
            }
        }

        public int buttonGridIndex
        {
            get
            {
                return parentGrid.GetButtonIndexInGrid(this);
            }
        }

        public int GetButtonOperationIndex(UIAButtonOperation buttonOp)
        {
            return buttonOperations.IndexOf(buttonOp);
        }

        public UIAButton(UIAButtonGrid _parent)
        {
            parentGrid = _parent;
                       
            autoLabelJSBool = new JSONStorableBool("autoLabel", true);
            buttonLabelOnJSString = new JSONStorableString("buttonLabelOn", "");
            buttonLabelOffJSString = new JSONStorableString("buttonLabelOff", "");
            hideLabelJSB = new JSONStorableBool("hideLabel", false);
            forceToggleButtonOpsJSB = new JSONStorableBool("forceToggleButtonOps", false,ForceToggleButtonOpsModified);
            triggerFunctionTypeJSEnum = new JSONStorableEnumStringChooser("triggerFuncType", TriggerFuncType.enumManifestName, TriggerFuncType.onSceneLoaded,"Trigger Event", callback: TriggerFunctionTypeCallback);
            disableOnAtomAddedTriggerDuringSceneLoadJSB = new JSONStorableBool("disableOnAtomAddedTriggerDuringSceneLoad", true);
            disableOnAtomAddedTriggerDuringUIASpawnJSB = new JSONStorableBool("disableOnAtomAddedTriggerDuringUIASpawn", true);

            RegisterParam(triggerFunctionTypeJSEnum);
            RegisterParam(disableOnAtomAddedTriggerDuringSceneLoadJSB);
            RegisterParam(disableOnAtomAddedTriggerDuringUIASpawnJSB);
            RegisterParam(autoLabelJSBool);
            RegisterParam(buttonLabelOnJSString);
            RegisterParam(buttonLabelOffJSString);
            RegisterParam(hideLabelJSB);
            RegisterParam(forceToggleButtonOpsJSB);

            buttonOperations.Add(new UIAButtonOperation(this));
            buttonSkinComponent = new ButtonSkinComponent(this);             
        }

        private void TriggerFunctionTypeCallback(int value)
        {
            if (value == TriggerFuncType.onPhraseRecognized)
            {
                StartPhraseRecognizer();
            }
            else
            {
                StopPhraseRecognizer();
            }
        }
        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            if (!isTriggerFunctionsGrid)
            {
                if (jspLoadExclusions == null) jspLoadExclusions= new HashSet<JSONStorableParam>();
                jspLoadExclusions.Add(triggerFunctionTypeJSEnum);
                jspLoadExclusions.Add(disableOnAtomAddedTriggerDuringSceneLoadJSB);
                jspLoadExclusions.Add(disableOnAtomAddedTriggerDuringUIASpawnJSB);
                
            }
            JSONClass jc = base.GetJSON(jspLoadExclusions);

            if (isTriggerFunctionsGrid && triggerFunctionTypeJSEnum.val == TriggerFuncType.onPhraseRecognized)
            {
                JSONArray phrasesJA = new JSONArray();
                foreach (string phrase in phrases)
                {
                    if (!string.IsNullOrEmpty(phrase)) phrasesJA.Add(phrase);
                }
                if (phrasesJA.Count > 0) jc["RecognizedPhrases"] = phrasesJA;
            }

            if (!ContainsAllBlankOperations() && parentGrid.isVisibleGrid)
            {
                jc["ButtonSkin"] = buttonSkinComponent.GetJSON();
            }

            JSONArray buttonOperationJA = new JSONArray();
            foreach (UIAButtonOperation buttonOperation in buttonOperations)
            {
                buttonOperationJA.Add(buttonOperation.GetJSON());
            }
            jc["ButtonOperations"] = buttonOperationJA;
            return jc;
        }
        public void PostLoadJSON()
        {
            foreach (UIAButtonOperation buttonOperation in buttonOperations) buttonOperation.PostLoadJSON();
        }
        public void LoadJSON(JSONClass buttonJSON, string uiapPackageName, int uiapFormatVersion)
        {
            RestoreFromJSON(buttonJSON);

            if (isTriggerFunctionsGrid && triggerFunctionTypeJSEnum.val == TriggerFuncType.onPhraseRecognized && buttonJSON["RecognizedPhrases"]!=null)
            {
                JSONArray phrasesJA = buttonJSON["RecognizedPhrases"].AsArray;

                phrases.Clear();
                foreach (JSONNode phrase in phrasesJA)
                {
                    phrases.Add(phrase.Value);
                }
                StartPhraseRecognizer();
            }

            buttonOperations.Clear();
            
            if (uiapFormatVersion == 1)
            {
                buttonOperations.Add(new UIAButtonOperation(this));
                buttonOperations[0].LoadJSON(buttonJSON, uiapPackageName, uiapFormatVersion);

                if (buttonJSON["useButtonTexture"] != null && buttonJSON["ButtonSkin"] != null) buttonJSON["ButtonSkin"]["usePresetButtonTexture"] = buttonJSON["useButtonTexture"];
                if (buttonJSON["ButtonSkin"] != null) buttonSkinComponent.LoadJSON((JSONClass)buttonJSON["ButtonSkin"], uiapPackageName, 1);

            }
            else
            {
                JSONArray buttonOperationsJA = buttonJSON["ButtonOperations"].AsArray;
                for (int i = 0; i < buttonOperationsJA.Count; i++)
                {
                    JSONClass buttonOperationJC = buttonOperationsJA[i].AsObject;
                    UIAButtonOperation buttonOperation = new UIAButtonOperation(this);
                    buttonOperations.Add(buttonOperation);
                    buttonOperation.LoadJSON(buttonOperationJC, uiapPackageName,uiapFormatVersion);
                    
                }

                if (buttonJSON["ButtonSkin"] != null && parentGrid.isVisibleGrid) buttonSkinComponent.LoadJSON((JSONClass)buttonJSON["ButtonSkin"], uiapPackageName, uiapFormatVersion);
                else buttonSkinComponent = new ButtonSkinComponent(this);
            }
           
        }
        public void CopyFrom(UIAButton bn)
        {
            if (bn != null)
            {
                base.CopyFrom(bn);
                
                buttonOperations.Clear();
                foreach (UIAButtonOperation sourceBO in bn.buttonOperations)
                {
                    UIAButtonOperation newBO = new UIAButtonOperation(this);
                    newBO.CopyFrom(sourceBO);
                    buttonOperations.Add(newBO);
                }

                if (isTriggerFunctionsGrid && triggerFunctionTypeJSEnum.val == TriggerFuncType.onPhraseRecognized) recognizedPhrases = bn.recognizedPhrases;
                else recognizedPhrases = "";

                buttonSkinComponent.CopyFrom(bn.buttonSkinComponent);
            }
        }
        public void AtomNameUpdate(string oldName, string newName)
        {
            foreach (UIAButtonOperation bo in buttonOperations)
            {
                bo.AtomNameUpdate(oldName, newName);
            }            
        }

        public void AtomRemovedUpdate(string oldName)
        { 
            foreach (UIAButtonOperation bo in buttonOperations)
            {
                bo.AtomRemovedUpdate(oldName);                
            }          
        }

        public void UpdateCTGName(string oldName, string newName)
        {
            foreach (UIAButtonOperation bo in buttonOperations)
            {
                bo.targetComponent.UpdateCGTName(oldName, newName);
            }

        }

        public void RemoveCTGName(string cgtName)
        {
            foreach (UIAButtonOperation bo in buttonOperations)
            {
                bo.targetComponent.RemoveCGTName(cgtName);
            }
        }

        public string GetManualLabel()
        {
            string rawLabel ;
            if (GetButtonState() == ButtonState.inactive) rawLabel = buttonLabelOnJSString.val;
            else rawLabel = buttonLabelOffJSString.val;

            if (buttonOperations.Count==1) return buttonOperations[0].LabelKeyWordsReplace(rawLabel);

            return (rawLabel);
        }
        public string GetAutoLabel()
        {
            if (buttonOperations.Count == 1)
            {
                return buttonOperations[0].GetAutoLabel();
            }

            return "";
        }

        public List<FileReference> GetAllThumbnailFileReferences()
        {
            List<FileReference> fileRefList = new List<FileReference>();
            foreach (UIAButtonOperation buttonOp in buttonOperations)
            {
                buttonOp.AppendThumbnailFileReferences(fileRefList);
            }

            return fileRefList;
        }

        public FileReference GetFirstThumbnailFileReferences()
        {
            foreach (FileReference fileRef in GetAllThumbnailFileReferences())
            {
                return fileRef;
            }

            return null;
        }


        public void UpdateThumbnailImage(Texture2D tex)
        {
            if (buttonTexture== tex)GridsDisplay.UpdateButtonTextures(this);
        }

        private bool actionInProgress { get; set; } = false;

        public void ActionInit(int _vrHand, bool _leapActivated)
        {
            if (!actionInProgress)
            {
                actionInProgress = true;
                vrHand = _vrHand;
                leapActivated = _leapActivated;

                try
                {
                    TargetResest();
                    FileRefReset();
                    CheckActionTargets();
                }
                catch (Exception e)
                {
                    SuperController.LogError("UIA.UIAButton.ActionInit exception caught: " + e);
                    actionInProgress = false;
                }
            }
            else
            {
                SuperController.LogMessage("UIAssist: Attempt to trigger button action that is already in progress has been blocked.");
            }
        }

        private void TargetResest()
        {
            foreach (UIAButtonOperation buttonOp in buttonOperations)
            {
                buttonOp.TargetReset();
            }
        }

        private void FileRefReset()
        {
            foreach (UIAButtonOperation buttonOp in buttonOperations)
            {
                buttonOp.FileRefReset();
            }           
        }

        public void CheckActionTargets()
        {
            int i = 1;
            foreach (UIAButtonOperation buttonOp in buttonOperations)
            {
                if (!buttonOp.CheckActionTarget()) return;
                i++;
            }
            CheckActionFileReferences();
        }

        public void CheckActionFileReferences()
        {
            try
            {
                foreach (UIAButtonOperation buttonOp in buttonOperations)
                {
                    if (!buttonOp.CheckActionFileReferences()) return;
                }

                uFileBrowser.FileBrowser browser = SuperController.singleton.mediaFileBrowserUI;
                if (!hudOnPreButtonAction && browser.IsHidden()) SuperController.singleton.HideMainHUD();

                CheckVARMgmtPrePlayActions();
            }
            catch (Exception e)
            {
                SuperController.LogError("UIA.UIAButton.ActionInit exception caught: " + e);
                actionInProgress = false;
            }           
        }

        private void CheckVARMgmtPrePlayActions()
        {
            if (BAInterop.isBAVARManagementEnabled)
            {
                var fileReferences = new List<string>();
                foreach (UIAButtonOperation buttonOp in buttonOperations)
                {
                    var fileRefTypes = buttonOp.GetFileReferenceTypes();
                    foreach (var fileRefType in fileRefTypes)
                    {
                        if (buttonOp.fileReferenceDict.ContainsKey(fileRefType) && fileRefType!=FileReferenceTypes.audio && fileRefType != FileReferenceTypes.addonPackagesFolder && fileRefType != FileReferenceTypes.cua)
                        {
                            var currentActionSelectedFile = buttonOp.fileReferenceDict[fileRefType].currentActionSelectedFile;
                            if (currentActionSelectedFile != null && currentActionSelectedFile != "") fileReferences.Add(currentActionSelectedFile);
                        }                       
                    }
                    if (buttonOp.buttonOpTypeJSEnum.val == UIAButtonOpType.loadPlugins)
                    {
                        foreach (var pluginLoad in buttonOp.pluginsLoadComponent.pluginLoadList)
                        {
                            if (pluginLoad.filePathJSString.val != "")
                            {
                                string pluginFilePath = pluginLoad.filePathJSString.val;
                                if (buttonOp.pluginsLoadComponent.useLatestVARJSBool.val) pluginFilePath= FileUtils.GetLatestVARPath(pluginFilePath);
                                fileReferences.Add(pluginFilePath);
                            }
                        }
                    }
                }
                if (fileReferences.Count > 0)
                {
                    BAInterop.EnableVARDependencies(fileReferences, PlayActions, delegate (string failureMessage)
                    {
                        if (failureMessage != null)
                        {
                            SuperController.LogMessage("UIAssist: Failure enabling VARs with BrowserAssist - " + failureMessage);
                            actionInProgress = false;
                        }
                    },
                    delegate (string exceptionMessage)
                    {
                        SuperController.LogError("UIAssist.UIAButton.CheckVARMgmtPrePlayActions: Exception reported when enabling VARs with BrowserAssist - "+ exceptionMessage);
                        actionInProgress = false;
                    });
                    return;
                }
            }
            PlayActions();
        }

        private void PlayActions()
        {
            int preButtonState = ButtonState.inactive;
            try
            {
                preButtonState = GetButtonState();
                foreach (UIAButtonOperation buttonOp in buttonOperations)
                {
                    buttonOp.PlayAction(preButtonState);
                }
            }
            catch (Exception e)
            {
                SuperController.LogError("UIA.UIAButton.PlayActions exception caught: " + e);
            }
            if (maxButtonStates==1 && forceToggleButtonOpsJSB.val)
            {
                if (preButtonState == ButtonState.active) lastButtonState = ButtonState.inactive;
                else lastButtonState = ButtonState.active;
            }

            if (maxButtonStates ==2 || forceToggleButtonOpsJSB.val)
            {
                GridsDisplay.UpdateButtonTextures(this);
            }
            actionInProgress = false;
        }
       
    }


    public class UIAButtonOperation : JSONStorableObject
    {
        public UIAButton parentButton { get; set; }

        private int vrHandActionInit { get { return parentButton.vrHand; } } 
        private bool leapActivatedAction { get { return parentButton.leapActivated; } }
        public TargetComponent targetComponent { get; protected set; }
        public JSONStorableEnumStringChooser buttonOpCategoryJSEnum { get; protected set; }
        public JSONStorableEnumStringChooser buttonOpTypeJSEnum { get; protected set; }

        public JSONStorableBool savePresetJSB;
        public JSONStorableEnumStringChooser buttonStateTransitionActivationJSE { get; protected set; }

        public Dictionary<int, FileReference> fileReferenceDict { get; protected set; }
        public PluginSettingComponent pluginSettingComponent { get; protected set; }
        public PluginsLoadComponent pluginsLoadComponent { get; protected set; }
        public AppearancePresetComponent appearancePresetComponent { get; protected set; }
        public ClothingComponent clothingComponent { get; protected set; }

        public VAMPlayEditModeComponent vamPlayEditModeComponent { get; protected set; }
        public SkinPresetDecalComponent skinPresetDecalComponent { get; protected set; }
        public RelativePositionComponent relativePositionComponent { get; protected set; }
        public SpawnAtomComponent spawnAtomComponent { get; protected set; }
        public MoveAtomComponent moveAtomComponent { get; protected set; }
        public MotionCaptureComponent motionCaptureComponent { get; protected set; }
        public WorldScaleComponent worldScaleComponent { get; protected set; }

        public BAFilterComponent baFilterComponent { get; protected set; }
        public BARulesetSelectionComponent baVARRulesetComponent { get; protected set; }

        public MorphControlComponent morphControlComponent { get; protected set; }
        public ShowVRHandsComponent showVRHandsComponent { get; protected set; }
        public SwitchUIAGridComponent switchUIAGridComponent { get; protected set; }
        public HairColorComponent hairColorComponent { get; protected set; }
        public DecalMakerComponent decalMakerComponent { get; protected set; }
        public PresetLockComponent presetLockComponent { get; protected set; }

        public UserPreferencesComponent userPreferencesComponent { get; protected set; }

        public NodeControlComponent nodeControlOnComponent { get; protected set; }
        public NodeControlComponent nodeControlOffComponent { get; protected set; }

        public NodePhysicsComponent nodePhysicsOnComponent { get; protected set; }
        public NodePhysicsComponent nodePhysicsOffComponent { get; protected set; }
        public NodeSelectionComponent nodeSelectionComponent { get; protected set; }

        public VAMTriggerActionComponent vamTriggerActionComponent { get; protected set; }

        public UIAButton linkedGlobalFunction
        {
            get
            {
                if (buttonOpTypeJSEnum.val == UIAButtonOpType.activateGlobalFunction && PatreonFeatures.patreonContentEnabled) return _linkedGlobalFunction;
                else return null;
            }
            set
            {
                if (_linkedGlobalFunction != null) _linkedGlobalFunction.RemoveParentGFOps(this);
                
                _linkedGlobalFunction = value;
                if (_linkedGlobalFunction != null) _linkedGlobalFunction.AddParentGFOps(this);
            }
        }
        private UIAButton _linkedGlobalFunction =null;
        private string _linkedGlobalFunctionRef =null;

        public HashSet<UIAButton> GetAllDescendentGFs()
        {
            HashSet<UIAButton> allDescendentGFs = new HashSet<UIAButton>();

            if (linkedGlobalFunction!= null) allDescendentGFs = linkedGlobalFunction.GetAllDescendentGFs();

            return allDescendentGFs;
        }

        public string buttonOpRef
        {
            get
            {
                return (parentButton.GetButtonOperationIndex(this) + 1).ToString();
            }
        }

        public int buttonOpIndex
        {
            get
            {
                return parentButton.GetButtonOperationIndex(this);
            }
        }

        public UIAButtonOperation(UIAButton parent) : base()
        {
            parentButton = parent;            

            buttonOpCategoryJSEnum = new JSONStorableEnumStringChooser("buttonCategory", UIAButtonCategory.enumManifestName, UIAButtonCategory.misc, "", ButtonCategoryChanged);
            buttonOpTypeJSEnum = new JSONStorableEnumStringChooser("buttonType", UIAButtonOpType.enumManifestName, UIAButtonOpType.blank, "", ButtonTypeChanged);

            savePresetJSB = new JSONStorableBool("savePreset", false, SavePresetChanged);
            buttonStateTransitionActivationJSE = new JSONStorableEnumStringChooser("buttonStateTransitionActivation", ButtonStateTranistion.enumManifestName, ButtonStateTranistion.onBoth, "Execute Operation", null);

            RegisterParam(buttonOpTypeJSEnum);
            RegisterParam(savePresetJSB);
            RegisterParam(buttonStateTransitionActivationJSE);

            targetComponent = new TargetComponent(buttonOpTypeJSEnum, this);
            fileReferenceDict = new Dictionary<int, FileReference>();

            CreateButtonComponents();
        }
        public void CreateButtonComponents()
        {
            List<int> components = GetComponentTypes();

            if (components.Contains(ButtonComponentTypes.pluginSettingComponent) && pluginSettingComponent==null) pluginSettingComponent = new PluginSettingComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.pluginsLoadComponent) && pluginsLoadComponent == null) pluginsLoadComponent = new PluginsLoadComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.appearancePresetComponent) && appearancePresetComponent == null) appearancePresetComponent = new AppearancePresetComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.clothingComponent) && clothingComponent == null) clothingComponent = new ClothingComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.vamTriggerActionComponent) && vamTriggerActionComponent == null) vamTriggerActionComponent = new VAMTriggerActionComponent(this);
            if (components.Contains(ButtonComponentTypes.vamPlayEditModeComponent) && vamPlayEditModeComponent == null) vamPlayEditModeComponent = new VAMPlayEditModeComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.skinPresetDecalComponent) && skinPresetDecalComponent == null) skinPresetDecalComponent = new SkinPresetDecalComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.relativePositionComponent) && relativePositionComponent == null) relativePositionComponent = new RelativePositionComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.spawnAtomComponent) && spawnAtomComponent == null) spawnAtomComponent = new SpawnAtomComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.moveAtomComponent) && moveAtomComponent == null) moveAtomComponent = new MoveAtomComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.motionCaptureComponent) && motionCaptureComponent == null) motionCaptureComponent = new MotionCaptureComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.worldScaleComponent) && worldScaleComponent == null) worldScaleComponent = new WorldScaleComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.baFilterComponent) && baFilterComponent == null) baFilterComponent = new BAFilterComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.baVARRulesetComponent) && baVARRulesetComponent == null) baVARRulesetComponent = new BARulesetSelectionComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.morphControlComponent) && morphControlComponent == null) morphControlComponent = new MorphControlComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.showVRHandsComponent) && showVRHandsComponent == null) showVRHandsComponent = new ShowVRHandsComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.switchUIAGridComponent) && switchUIAGridComponent == null) switchUIAGridComponent = new SwitchUIAGridComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.hairColorComponent) && hairColorComponent == null) hairColorComponent = new HairColorComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.decalMakerComponent) && decalMakerComponent == null) decalMakerComponent = new DecalMakerComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.presetLockComponent) && presetLockComponent == null) presetLockComponent = new PresetLockComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.nodeControlOnComponent) && nodeControlOnComponent == null) nodeControlOnComponent = new NodeControlComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.nodeControlOffComponent) && nodeControlOffComponent == null) nodeControlOffComponent = new NodeControlComponent(buttonOpTypeJSEnum, this);

            if (components.Contains(ButtonComponentTypes.nodePhysicsOnComponent) && nodePhysicsOnComponent == null) nodePhysicsOnComponent = new NodePhysicsComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.nodePhysicsOffComponent) && nodePhysicsOffComponent == null) nodePhysicsOffComponent = new NodePhysicsComponent(buttonOpTypeJSEnum, this);
            if (components.Contains(ButtonComponentTypes.nodeSelectionComponent) && nodeSelectionComponent == null) nodeSelectionComponent = new NodeSelectionComponent(this);

            if (components.Contains(ButtonComponentTypes.userPreferencesComponent) && userPreferencesComponent == null) userPreferencesComponent = new UserPreferencesComponent(buttonOpTypeJSEnum, this);
        }
        public string GetAutoLabel()
        {
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.offsetWorldScale)
            {
                if (worldScaleComponent.worldScaleIncrementJSF.val < 0) return "Offset World Scale by -" + worldScaleComponent.worldScaleIncrementJSF.val.ToString("0.##");
                return "Offset World Scale by +" + worldScaleComponent.worldScaleIncrementJSF.val.ToString("0.##");
            }
            else
            {
                string rawLabel;
                if (parentButton.GetButtonState() == ButtonState.inactive) rawLabel = UIAButtonOpType.GetInactiveLabel(buttonOpTypeJSEnum.val);
                else rawLabel = UIAButtonOpType.GetActiveLabel(buttonOpTypeJSEnum.val);

                return LabelKeyWordsReplace(rawLabel);
            }
        }
        public override JSONClass GetJSON(HashSet<JSONStorableParam> jspLoadExclusions = null)
        {
            JSONClass jc = base.GetJSON(jspLoadExclusions);

            if (buttonOpTypeJSEnum.val == UIAButtonOpType.activateGlobalFunction && PatreonFeatures.patreonContentEnabled && _linkedGlobalFunction != null) jc["globalFunctionRef"] = _linkedGlobalFunction.buttonRef;

            foreach (int fileRefType in GetFileReferenceTypes())
            {
                if (fileReferenceDict.ContainsKey(fileRefType)) jc[EnumManifest.GetStoreVal(FileReferenceTypes.enumManifestName, fileRefType)] = fileReferenceDict[fileRefType].GetJSON();
            }

            if (IsComponentInButtonOpType(ButtonComponentTypes.targetComponent))
            {
                HashSet<JSONStorableParam> jspExclusions = new HashSet<JSONStorableParam>();
                jspExclusions.Add(targetComponent.targetNameJSMultiEnum);
                if (targetComponent.targetCategoryJSEnum.val == TargetCategory.scenePlugins || targetComponent.targetCategoryJSEnum.val == TargetCategory.sessionPlugins)
                    jc["TargetComponent"] = targetComponent.GetJSON(jspExclusions);
                else jc["TargetComponent"] = targetComponent.GetJSON();
            }
            if (IsComponentInButtonOpType(ButtonComponentTypes.appearancePresetComponent)) jc["AppearancePresetComponent"] = appearancePresetComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.clothingComponent)) jc["ClothingComponent"] = clothingComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.vamTriggerActionComponent)) jc["VAMTriggerActionComponent"] = vamTriggerActionComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.vamPlayEditModeComponent)) jc["VAMPlayEditModeComponent"] = vamPlayEditModeComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.skinPresetDecalComponent)) jc["SkinPresetDecalComponent"] = skinPresetDecalComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.pluginsLoadComponent)) jc["PluginLoadComponent"] = pluginsLoadComponent.GetJSON();

            if (IsComponentInButtonOpType(ButtonComponentTypes.pluginSettingComponent)) jc["PluginSettingComponent"] = pluginSettingComponent.GetJSON();

            if (IsComponentInButtonOpType(ButtonComponentTypes.spawnAtomComponent)) jc["SpawnAtomComponent"] = spawnAtomComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.relativePositionComponent)) jc["RelativePositionComponent"] = relativePositionComponent.GetJSON();

            if (IsComponentInButtonOpType(ButtonComponentTypes.moveAtomComponent)) jc["MoveAtomComponent"] = moveAtomComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.motionCaptureComponent)) jc["MotionCaptureComponent"] = motionCaptureComponent.GetJSON();

            if (IsComponentInButtonOpType(ButtonComponentTypes.worldScaleComponent)) jc["WorldScaleComponent"] = worldScaleComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.baFilterComponent)) jc["BAFilterComponent"] = baFilterComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.baVARRulesetComponent)) jc["BAVARRulesetComponent"] = baVARRulesetComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.morphControlComponent)) jc["MorphControlComponent"] = morphControlComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.showVRHandsComponent)) jc["ShowVRHandsComponent"] = showVRHandsComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.switchUIAGridComponent)) jc["SwitchUIAScreenComponent"] = switchUIAGridComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.hairColorComponent)) jc["HairColorComponent"] = hairColorComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.decalMakerComponent)) jc["DecalMakerComponent"] = decalMakerComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.presetLockComponent)) jc["PresetLockComponent"] = presetLockComponent.GetJSON();

            if (IsComponentInButtonOpType(ButtonComponentTypes.nodeControlOnComponent)) jc["nodeControlONComponent"] = nodeControlOnComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.nodeControlOffComponent)) jc["nodeControlOFFComponent"] = nodeControlOffComponent.GetJSON();

            if (IsComponentInButtonOpType(ButtonComponentTypes.nodePhysicsOnComponent)) jc["nodePhysicsOnComponent"] = nodePhysicsOnComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.nodePhysicsOffComponent)) jc["nodePhysicsOffComponent"] = nodePhysicsOffComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.nodeSelectionComponent)) jc["nodeSelectionComponent"] = nodeSelectionComponent.GetJSON();
            if (IsComponentInButtonOpType(ButtonComponentTypes.userPreferencesComponent)) jc["UserPreferencesComponent"] = userPreferencesComponent.GetJSON();


            return jc;
        }
        public void PostLoadJSON()
        {
            if (_linkedGlobalFunctionRef != null) linkedGlobalFunction = UIAStorables.globalFunctionsGrid.GetButtonFromRCRef(_linkedGlobalFunctionRef);
        }
        public void LoadJSON(JSONClass buttonOperationJSON, string uiapPackageName, int uiapFormatVersion)
        {           
            RestoreFromJSON(buttonOperationJSON);

            if (buttonOpTypeJSEnum.val == UIAButtonOpType.triggerVAMAction && !UIAGlobals.storableCataloguesExist) {
                buttonOpTypeJSEnum.val = UIAButtonOpType.blank;
                SuperController.LogMessage("WARNING: A 'Trigger VAM Action' button operation was detected but these actions are dependent on the VAR 'JayJayWon.AtomStorablesCatalogue'. The dependent operation has not been loaded. Please ensure the dependent VAR is available (see VAMHub) and reload UIAssist. If you are using BrowserAssist VARManagment check that the VAR is kept permanenetly Enabled using a custom Ruleset.");
                return;
            }

            if (buttonOperationJSON["globalFunctionRef"] != null) _linkedGlobalFunctionRef = buttonOperationJSON["globalFunctionRef"];
            else _linkedGlobalFunctionRef = null;

            buttonOpCategoryJSEnum.val = UIAButtonOpType.GetButtonCategory(buttonOpTypeJSEnum.val);
            if (uiapFormatVersion == 1)
            {
                if (IsComponentInButtonOpType(ButtonComponentTypes.spawnAtomComponent)) spawnAtomComponent.RestoreFromJSON(buttonOperationJSON);

                foreach (int fileRefType in GetFileReferenceTypes())
                {
                    string legacyFileStoreNumber = UIAButtonOpType.GetLegacyUIAPFileStoreNumber(buttonOpTypeJSEnum.val, fileRefType);
                    string legacyFilePathStoreNumber = legacyFileStoreNumber;
                    if (legacyFilePathStoreNumber == "-1") break;
                    if (legacyFilePathStoreNumber == "1") legacyFilePathStoreNumber = "";

                    if (buttonOperationJSON["preset" + legacyFilePathStoreNumber + "FilePath"] != null)
                    {
                        JSONClass jc = new JSONClass();
                        jc["filePath"] = buttonOperationJSON["preset" + legacyFilePathStoreNumber + "FilePath"];
                        if (buttonOperationJSON["preset" + legacyFileStoreNumber + "Type"] != null) jc["fileSelectionMode"] = buttonOperationJSON["preset" + legacyFileStoreNumber + "Type"];
                        if (buttonOperationJSON["useLatestVARforPresets"] != null) jc["useLatestVAR"] = buttonOperationJSON["useLatestVARforPresets"];
                        if (buttonOperationJSON["mergeLoadPreset"] != null) jc["mergeLoadPreset"] = buttonOperationJSON["mergeLoadPreset"];
                        buttonOperationJSON[EnumManifest.GetStoreVal(FileReferenceTypes.enumManifestName, fileRefType)] = jc;
                    }
                }
            }
            else
            {
                if (buttonOperationJSON["SpawnAtomComponent"] != null) spawnAtomComponent.RestoreFromJSON(buttonOperationJSON["SpawnAtomComponent"].AsObject);
                else spawnAtomComponent = new SpawnAtomComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["TargetComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.targetComponent))
                {
                    targetComponent.RestoreFromJSON(buttonOperationJSON["TargetComponent"].AsObject);
                    targetComponent.RefreshTargetNameChoices(false);
                }
                else if (IsComponentInButtonOpType(ButtonComponentTypes.targetComponent)) targetComponent = new TargetComponent(buttonOpTypeJSEnum, this);
            }

            foreach (int fileRefType in GetFileReferenceTypes())
            {
                string fileRefStoreID = EnumManifest.GetStoreVal(FileReferenceTypes.enumManifestName, fileRefType);
                if (!fileReferenceDict.ContainsKey(fileRefType)) fileReferenceDict.Add(fileRefType, new FileReference(fileRefType, this));
                if (buttonOperationJSON[fileRefStoreID] != null)
                {
                    fileReferenceDict[fileRefType].RestoreFromJSON(buttonOperationJSON[fileRefStoreID].AsObject, uiapPackageName);
                }
                else if ((buttonOperationJSON["pluginsPreset"] != null))
                {
                    // Correcting an error where Session & Scene plugin presets were being storted as an Atom Plugin Preset File Ref
                    if (targetComponent.targetCategoryJSEnum.val == TargetCategory.scenePlugins)
                    {
                        fileReferenceDict[FileReferenceTypes.scenePluginPreset].RestoreFromJSON(buttonOperationJSON["pluginsPreset"].AsObject, uiapPackageName);
                    }
                    if (targetComponent.targetCategoryJSEnum.val == TargetCategory.sessionPlugins)
                    {
                        fileReferenceDict[FileReferenceTypes.sessionPluginPreset].RestoreFromJSON(buttonOperationJSON["pluginsPreset"].AsObject, uiapPackageName);
                    }
                }
            }

            if (uiapFormatVersion == 1)
            {
                targetComponent.RestoreFromJSON(buttonOperationJSON, "targetCategory");
                string legacyVal = buttonOperationJSON["targetName"];
              
                if (legacyVal != null && legacyVal != "" && legacyVal != "<None available>")
                {
                    if (UserChosenTargetType.storeValList.Contains(legacyVal) || LastViewedTargetType.storeValList.Contains(legacyVal) || LastSelectedTargetType.storeValList.Contains(legacyVal) || AllAtomsTargetType.storeValList.Contains(legacyVal) || legacyVal == "") buttonOperationJSON["targetName"] = "T" + legacyVal;
                    else
                    {
                        int preFixLength = targetComponent.targetNameJSMultiEnum.displayChoicePreFix.Length;
                        int posFixLength = targetComponent.targetNameJSMultiEnum.displayChoicePostFix.Length;
                        buttonOperationJSON["targetName"] = "M" + legacyVal.Substring(preFixLength, legacyVal.Length - preFixLength - posFixLength);
                    }
                    targetComponent.RestoreFromJSON(buttonOperationJSON);
                }
                else if (legacyVal == "") targetComponent.RestoreFromJSON(buttonOperationJSON);

                if (buttonOpTypeJSEnum.val == UIAButtonOpType.loadGeneralPreset &&  targetComponent.targetCategoryJSEnum.val== TargetCategory.specificAtom && targetComponent.spawnAtomIfTargetMissingJSBool.val && buttonOperationJSON["atomType"]!=null)
                {
                    targetComponent.specificAtomTypeJSS.val = buttonOperationJSON["atomType"];
                }

                if (IsComponentInButtonOpType(ButtonComponentTypes.pluginSettingComponent)) pluginSettingComponent.RestoreFromJSON(buttonOperationJSON, uiapFormatVersion);
                if (IsComponentInButtonOpType(ButtonComponentTypes.pluginsLoadComponent))
                {
                    if (buttonOperationJSON["pluginLoads"] != null && buttonOpTypeJSEnum.val != UIAButtonOpType.blank) pluginsLoadComponent.LoadJSON(buttonOperationJSON, uiapPackageName);
                    else pluginsLoadComponent = new PluginsLoadComponent(buttonOpTypeJSEnum, this);
                }
                    
                if (IsComponentInButtonOpType(ButtonComponentTypes.appearancePresetComponent)) appearancePresetComponent.RestoreFromJSON(buttonOperationJSON);
                if (IsComponentInButtonOpType(ButtonComponentTypes.clothingComponent)) clothingComponent.LoadJSON(buttonOperationJSON, uiapFormatVersion);
                if (IsComponentInButtonOpType(ButtonComponentTypes.vamTriggerActionComponent)) vamTriggerActionComponent.RestoreFromJSON(buttonOperationJSON);
                if (IsComponentInButtonOpType(ButtonComponentTypes.vamPlayEditModeComponent)) vamPlayEditModeComponent.RestoreFromJSON(buttonOperationJSON);
                if (IsComponentInButtonOpType(ButtonComponentTypes.skinPresetDecalComponent)) skinPresetDecalComponent.RestoreFromJSON(buttonOperationJSON);

                if (IsComponentInButtonOpType(ButtonComponentTypes.relativePositionComponent)) relativePositionComponent.RestoreFromJSON(buttonOperationJSON);
                if (IsComponentInButtonOpType(ButtonComponentTypes.moveAtomComponent)) moveAtomComponent.RestoreFromJSON(buttonOperationJSON);
                if (IsComponentInButtonOpType(ButtonComponentTypes.motionCaptureComponent)) motionCaptureComponent.RestoreFromJSON(buttonOperationJSON);
                if (IsComponentInButtonOpType(ButtonComponentTypes.decalMakerComponent)) decalMakerComponent.LoadJSON(buttonOperationJSON, uiapFormatVersion);
                if (IsComponentInButtonOpType(ButtonComponentTypes.presetLockComponent)) presetLockComponent.RestoreFromJSON(buttonOperationJSON);
                if (IsComponentInButtonOpType(ButtonComponentTypes.hairColorComponent)) hairColorComponent.RestoreFromJSON(buttonOperationJSON);

                if (IsComponentInButtonOpType(ButtonComponentTypes.worldScaleComponent)) worldScaleComponent.RestoreFromJSON(buttonOperationJSON);
                if (IsComponentInButtonOpType(ButtonComponentTypes.showVRHandsComponent)) showVRHandsComponent.RestoreFromJSON(buttonOperationJSON);
                if (IsComponentInButtonOpType(ButtonComponentTypes.switchUIAGridComponent)) switchUIAGridComponent.RestoreFromJSON(buttonOperationJSON,null);
            

            }
            else
            {
                if (buttonOperationJSON["TargetComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.targetComponent))
                {
                    targetComponent.RestoreFromJSON(buttonOperationJSON["TargetComponent"].AsObject);
                    targetComponent.RefreshTargetNameChoices(false);
                }               
                else if (IsComponentInButtonOpType(ButtonComponentTypes.targetComponent))targetComponent = new TargetComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["PluginSettingComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.pluginSettingComponent)) pluginSettingComponent.RestoreFromJSON(buttonOperationJSON["PluginSettingComponent"].AsObject, uiapFormatVersion);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.pluginSettingComponent)) pluginSettingComponent = new PluginSettingComponent(buttonOpTypeJSEnum, this);
                if (buttonOperationJSON["PluginLoadComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.pluginsLoadComponent)) pluginsLoadComponent.LoadJSON(buttonOperationJSON["PluginLoadComponent"].AsObject, uiapPackageName);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.pluginsLoadComponent)) pluginsLoadComponent = new PluginsLoadComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["AppearancePresetComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.appearancePresetComponent)) appearancePresetComponent.RestoreFromJSON(buttonOperationJSON["AppearancePresetComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.appearancePresetComponent)) appearancePresetComponent = new AppearancePresetComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["ClothingComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.clothingComponent)) clothingComponent.LoadJSON(buttonOperationJSON["ClothingComponent"].AsObject, uiapFormatVersion);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.clothingComponent)) clothingComponent = new ClothingComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["VAMPlayEditModeComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.vamPlayEditModeComponent)) vamPlayEditModeComponent.RestoreFromJSON(buttonOperationJSON["VAMPlayEditModeComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.vamPlayEditModeComponent)) vamPlayEditModeComponent = new VAMPlayEditModeComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["SkinPresetDecalComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.skinPresetDecalComponent)) skinPresetDecalComponent.RestoreFromJSON(buttonOperationJSON["SkinPresetDecalComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.skinPresetDecalComponent)) skinPresetDecalComponent = new SkinPresetDecalComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["RelativePositionComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.relativePositionComponent)) relativePositionComponent.RestoreFromJSON(buttonOperationJSON["RelativePositionComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.relativePositionComponent)) relativePositionComponent = new RelativePositionComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["MoveAtomComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.moveAtomComponent)) moveAtomComponent.RestoreFromJSON(buttonOperationJSON["MoveAtomComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.moveAtomComponent)) moveAtomComponent = new MoveAtomComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["MoveAtomComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.moveAtomComponent)) moveAtomComponent.RestoreFromJSON(buttonOperationJSON["MoveAtomComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.moveAtomComponent)) moveAtomComponent = new MoveAtomComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["MotionCaptureComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.motionCaptureComponent)) motionCaptureComponent.RestoreFromJSON(buttonOperationJSON["MotionCaptureComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.motionCaptureComponent)) motionCaptureComponent = new MotionCaptureComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["DecalMakerComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.decalMakerComponent)) decalMakerComponent.LoadJSON(buttonOperationJSON["DecalMakerComponent"].AsObject, uiapFormatVersion);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.decalMakerComponent)) decalMakerComponent = new DecalMakerComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["PresetLockComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.presetLockComponent)) presetLockComponent.RestoreFromJSON(buttonOperationJSON["PresetLockComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.presetLockComponent)) presetLockComponent = new PresetLockComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["HairColorComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.hairColorComponent)) hairColorComponent.RestoreFromJSON(buttonOperationJSON["HairColorComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.hairColorComponent)) hairColorComponent = new HairColorComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["WorldScaleComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.worldScaleComponent)) worldScaleComponent.RestoreFromJSON(buttonOperationJSON["WorldScaleComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.worldScaleComponent)) worldScaleComponent = new WorldScaleComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["BAFilterComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.baFilterComponent)) baFilterComponent.RestoreFromJSON(buttonOperationJSON["BAFilterComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.baFilterComponent)) baFilterComponent = new BAFilterComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["BAVARRulesetComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.baVARRulesetComponent)) baVARRulesetComponent.RestoreFromJSON(buttonOperationJSON["BAVARRulesetComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.baVARRulesetComponent)) baVARRulesetComponent = new BARulesetSelectionComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["MorphControlComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.morphControlComponent)) morphControlComponent.RestoreFromJSON(buttonOperationJSON["MorphControlComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.morphControlComponent)) morphControlComponent = new MorphControlComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["VAMTriggerActionComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.vamTriggerActionComponent)) vamTriggerActionComponent.RestoreFromJSON(buttonOperationJSON["VAMTriggerActionComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.vamTriggerActionComponent)) vamTriggerActionComponent = new VAMTriggerActionComponent(this);

                if (buttonOperationJSON["UserPreferencesComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.userPreferencesComponent)) userPreferencesComponent.RestoreFromJSON(buttonOperationJSON["UserPreferencesComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.userPreferencesComponent)) userPreferencesComponent = new UserPreferencesComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["ShowVRHandsComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.showVRHandsComponent)) showVRHandsComponent.RestoreFromJSON(buttonOperationJSON["ShowVRHandsComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.showVRHandsComponent)) showVRHandsComponent = new ShowVRHandsComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["SwitchUIAScreenComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.switchUIAGridComponent)) switchUIAGridComponent.RestoreFromJSON(buttonOperationJSON["SwitchUIAScreenComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.switchUIAGridComponent)) switchUIAGridComponent = new SwitchUIAGridComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["nodeControlONComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.nodeControlOnComponent)) nodeControlOnComponent.RestoreFromJSON(buttonOperationJSON["nodeControlONComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.nodeControlOnComponent)) nodeControlOnComponent = new NodeControlComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["nodeControlOFFComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.nodeControlOffComponent)) nodeControlOffComponent.RestoreFromJSON(buttonOperationJSON["nodeControlOFFComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.nodeControlOffComponent)) nodeControlOffComponent = new NodeControlComponent(buttonOpTypeJSEnum, this);

                if (buttonOperationJSON["nodePhysicsOnComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.nodePhysicsOnComponent)) nodePhysicsOnComponent.LoadJSON(buttonOperationJSON["nodePhysicsOnComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.nodePhysicsOnComponent)) nodePhysicsOnComponent = new NodePhysicsComponent(buttonOpTypeJSEnum, this);
                if (buttonOperationJSON["nodePhysicsOffComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.nodePhysicsOffComponent)) nodePhysicsOffComponent.LoadJSON(buttonOperationJSON["nodePhysicsOffComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.nodePhysicsOffComponent)) nodePhysicsOffComponent = new NodePhysicsComponent(buttonOpTypeJSEnum, this);
                if (buttonOperationJSON["nodeSelectionComponent"] != null && IsComponentInButtonOpType(ButtonComponentTypes.nodeSelectionComponent)) nodeSelectionComponent.RestoreFromJSON(buttonOperationJSON["nodeSelectionComponent"].AsObject);
                else if (IsComponentInButtonOpType(ButtonComponentTypes.nodeSelectionComponent)) nodeSelectionComponent = new NodeSelectionComponent(this);
            }
        }

        public void CopyFrom(UIAButtonOperation sourceBO)
        {
            if (sourceBO != null)
            {
                base.CopyFrom(sourceBO);

                buttonOpCategoryJSEnum.val = UIAButtonOpType.GetButtonCategory(buttonOpTypeJSEnum.val);

                if (buttonOpTypeJSEnum.val == UIAButtonOpType.activateGlobalFunction) linkedGlobalFunction = sourceBO.linkedGlobalFunction;
                else linkedGlobalFunction=null;

                fileReferenceDict.Clear();

                foreach (KeyValuePair<int, FileReference> kvp in sourceBO.fileReferenceDict)
                {
                    fileReferenceDict.Add(kvp.Key, new FileReference(kvp.Value.fileReferenceType, this));
                    fileReferenceDict[kvp.Key].CopyFrom(sourceBO.fileReferenceDict[kvp.Key]);
                }
                if (IsComponentInButtonOpType(ButtonComponentTypes.targetComponent))targetComponent.CopyFrom(sourceBO.targetComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.pluginSettingComponent)) pluginSettingComponent.CopyFrom(sourceBO.pluginSettingComponent);

                if (IsComponentInButtonOpType(ButtonComponentTypes.appearancePresetComponent)) appearancePresetComponent.CopyFrom(sourceBO.appearancePresetComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.clothingComponent)) clothingComponent.CopyFrom(sourceBO.clothingComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.vamTriggerActionComponent)) vamTriggerActionComponent.CopyFrom(sourceBO.vamTriggerActionComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.vamPlayEditModeComponent)) vamPlayEditModeComponent.CopyFrom(sourceBO.vamPlayEditModeComponent);

                if (IsComponentInButtonOpType(ButtonComponentTypes.spawnAtomComponent)) spawnAtomComponent.CopyFrom(sourceBO.spawnAtomComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.relativePositionComponent)) relativePositionComponent.CopyFrom(sourceBO.relativePositionComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.skinPresetDecalComponent)) skinPresetDecalComponent.CopyFrom(sourceBO.skinPresetDecalComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.pluginsLoadComponent)) pluginsLoadComponent.CopyFrom(sourceBO.pluginsLoadComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.moveAtomComponent)) moveAtomComponent.CopyFrom(sourceBO.moveAtomComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.motionCaptureComponent)) motionCaptureComponent.CopyFrom(sourceBO.motionCaptureComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.worldScaleComponent)) worldScaleComponent.CopyFrom(sourceBO.worldScaleComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.baFilterComponent)) baFilterComponent.CopyFrom(sourceBO.baFilterComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.baVARRulesetComponent)) baVARRulesetComponent.CopyFrom(sourceBO.baVARRulesetComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.showVRHandsComponent)) showVRHandsComponent.CopyFrom(sourceBO.showVRHandsComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.switchUIAGridComponent)) switchUIAGridComponent.CopyFrom(sourceBO.switchUIAGridComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.hairColorComponent)) hairColorComponent.CopyFrom(sourceBO.hairColorComponent);

                if (IsComponentInButtonOpType(ButtonComponentTypes.decalMakerComponent)) decalMakerComponent.CopyFrom(sourceBO.decalMakerComponent);

                if (IsComponentInButtonOpType(ButtonComponentTypes.nodeControlOnComponent)) nodeControlOnComponent.CopyFrom(sourceBO.nodeControlOnComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.nodeControlOffComponent)) nodeControlOffComponent.CopyFrom(sourceBO.nodeControlOffComponent);

                if (IsComponentInButtonOpType(ButtonComponentTypes.nodePhysicsOnComponent)) nodePhysicsOnComponent.CopyFrom(sourceBO.nodePhysicsOnComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.nodePhysicsOffComponent)) nodePhysicsOffComponent.CopyFrom(sourceBO.nodePhysicsOffComponent);
                if (IsComponentInButtonOpType(ButtonComponentTypes.nodeSelectionComponent)) nodeSelectionComponent.CopyFrom(sourceBO.nodeSelectionComponent);
            }
        }

        public int maxButtonOpStates
        {
            get
            {
                return UIAButtonOpType.GetMaxStates(buttonOpTypeJSEnum.val);
            }
        }

        private int GetBoolToggleButtonState(bool toggle)
        {
            if (toggle) return ButtonState.active;
            else return ButtonState.inactive;
        }

        public int GetButtonOpState()
        {
            if (maxButtonOpStates == 1) return ButtonState.inactive;

            if (buttonOpTypeJSEnum.val == UIAButtonOpType.suppressScaleLoad) return GetBoolToggleButtonState(PresetLoadSettings.suppressScaleLoadJSB.val);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.suppressClothingLoad) return GetBoolToggleButtonState(PresetLoadSettings.suppressClothingLoadJSB.val);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.onlyLoadClothing) return GetBoolToggleButtonState(PresetLoadSettings.onlyLoadClothingFromAppPresetJSB.val);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.toggleLoopPlayback) return GetBoolToggleButtonState(SuperController.singleton.motionAnimationMaster.loop);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.toggleMocapPlay)
            {
                if (SuperController.singleton.motionAnimationMaster.activeWhilePlaying != null) return GetBoolToggleButtonState(SuperController.singleton.motionAnimationMaster.activeWhilePlaying.activeInHierarchy);
                else if (SuperController.singleton.motionAnimationMaster.activeWhileStopped == null) return GetBoolToggleButtonState(!SuperController.singleton.motionAnimationMaster.activeWhileStopped.activeInHierarchy);
                return GetBoolToggleButtonState(false);
            }
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.freezeMotionSound) return GetBoolToggleButtonState(SuperController.singleton.freezeAnimation);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.togglePlayEdit) return GetBoolToggleButtonState(SuperController.singleton.gameMode == SuperController.GameMode.Edit);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.moveAtom) return GetBoolToggleButtonState(MoveAtomComponent.atomVRMoveActive && MoveAtomComponent.atomVRMoveUIAButton == this);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.softBodyPhysicsToggle) return GetBoolToggleButtonState(UserPreferences.singleton.softPhysics);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.desktopVSyncToggle) return GetBoolToggleButtonState(UserPreferences.singleton.desktopVsync);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.vrHeadCollider) return GetBoolToggleButtonState(UserPreferences.singleton.useHeadCollider);

            if (buttonOpTypeJSEnum.val == UIAButtonOpType.realtimeReflectionProbesToggle) return GetBoolToggleButtonState(UserPreferences.singleton.realtimeReflectionProbes);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.mirrorReflectionsToggle) return GetBoolToggleButtonState(UserPreferences.singleton.mirrorReflections);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.hqPhysicsToggle) return GetBoolToggleButtonState(UserPreferences.singleton.physicsHighQuality);

            if (buttonOpTypeJSEnum.val == UIAButtonOpType.showVRHands) return showVRHandsComponent.GetShowVRHandButtonState();
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.leapMotionToggle) return showVRHandsComponent.GetToggleLeapMotionButtonState();
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.vrHandCollision) return  GetBoolToggleButtonState(SuperController.singleton.commonHandModelControl.useCollision);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.showHiddenAtoms) return GetBoolToggleButtonState(SuperController.singleton.showHiddenAtoms);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.gazeAssistedSelect) return GetBoolToggleButtonState(GazeAssistedSelectTool.gazeAssistActiveJSB.val);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.hideInactiveTargets) return GetBoolToggleButtonState(UserPreferences.singleton.hideInactiveTargets);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.suppressPresetLocks) return GetBoolToggleButtonState(PresetLoadSettings.suppressPresetLocksJSB.val);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.activeClothingEditor) return GetBoolToggleButtonState(GameControlUI.gameControlDisplayMode == GameControlDisplayModes.ace || GameControlUI.gameControlDisplayMode == GameControlDisplayModes.clothItemPresetSelect);

            if (buttonOpTypeJSEnum.val == UIAButtonOpType.raiseByHAHeight) return GetBoolToggleButtonState(HeelAdjustTool.heelAdjustRaisePeopleJSB.val);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.perfMon)
            {
                PerfMon pm = UserPreferences.singleton.transform.Find("PerfMon").GetComponent<PerfMon>();
                return GetBoolToggleButtonState(pm.onToggle.isOn);
            }
            
            // If the button target is a User Chosen Atom and no atom has been chosen yet and there are multiple choices available, then the button state must be inactive.
            if (targetComponent.targetCategoryJSEnum.val == TargetCategory.userChosenAtom && targetComponent.lastUserChosenAtomName == "" && targetComponent.GetUserTargetAtomChoices().Count > 1) return ButtonState.inactive;

            if (IsComponentInButtonOpType(ButtonComponentTypes.targetComponent) && maxButtonOpStates > 1)
            {
                List<string> atomNames = targetComponent.GetTargetAtomNames();
                bool sessionPluginsTarget = (targetComponent.targetCategoryJSEnum.val == TargetCategory.sessionPlugins);
                bool scenePluginsTarget = (targetComponent.targetCategoryJSEnum.val == TargetCategory.scenePlugins);

                int activeAtomsCount = 0;
                foreach (string atomName in atomNames)
                {
                    Atom atom = SuperController.singleton.GetAtomByUid(atomName);
                    if (atom != null)
                    {
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.detachAtomRoot)
                        {
                            JSONStorable storable = atom.GetStorableByID("control");
                            if (storable != null)
                            {
                                JSONStorableBool detach = storable.GetBoolJSONParam("detachControl");
                                if (detach != null && GetBoolToggleButtonState(detach.val) == ButtonState.inactive) return ButtonState.inactive;
                                else activeAtomsCount++;
                            }
                        }
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.hideAtom)
                        {
                            if (GetBoolToggleButtonState(atom.hidden) == ButtonState.inactive) return ButtonState.inactive;
                            else activeAtomsCount++;
                        }
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.toggleAtomOn)
                        {
                            if (GetBoolToggleButtonState(atom.on) == ButtonState.inactive) return ButtonState.inactive;
                            else activeAtomsCount++;
                        }
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.toggleAtomCollision)
                        {
                            if (GetBoolToggleButtonState(atom.collisionEnabledJSON.val) == ButtonState.inactive) return ButtonState.inactive;
                            else activeAtomsCount++;
                        }
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.pluginActionToggle || buttonOpTypeJSEnum.val == UIAButtonOpType.pluginMultiSettingsToggle)
                        {
                            if (pluginSettingComponent.atomButtonToggleStates.ContainsKey(atomName)) return pluginSettingComponent.atomButtonToggleStates[atomName];
                            pluginSettingComponent.atomButtonToggleStates.Add(atomName, ButtonState.inactive);
                            return ButtonState.inactive;
                        }
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.togglePresetLocks)
                        {
                            if (presetLockComponent.GetPresetLockAtomState(atom) == ButtonState.inactive) return ButtonState.inactive;
                            else activeAtomsCount++;
                        }
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.pluginBoolToggle)
                        {
                            List<MVRScript> plugins;
                            if (sessionPluginsTarget) plugins = PluginUtils.GetSessionPlugins(pluginSettingComponent.pluginTypeJSSC.val);
                            else if (scenePluginsTarget) plugins = PluginUtils.GetScenePlugins(pluginSettingComponent.pluginTypeJSSC.val);
                            else plugins = PluginUtils.GetPluginsFromAtom(atom);

                            int activeBools = 0;
                            foreach (MVRScript script in plugins)
                            {
                                if (script.name.EndsWith(pluginSettingComponent.pluginTypeJSSC.val))
                                {
                                    if (script.GetBoolParamNames().Contains(pluginSettingComponent.targetParamNameOnJSSC.val))
                                    {
                                        if (!script.GetBoolParamValue(pluginSettingComponent.targetParamNameOnJSSC.val)) return ButtonState.inactive;
                                        else activeBools++;
                                    }
                                }
                            }
                            if (activeBools > 0) activeAtomsCount++;
                        }
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.decalMakerToggle)
                        {
                            if (decalMakerComponent.GetDecalMakerButtonState(atom) == ButtonState.inactive) return ButtonState.inactive;
                            else activeAtomsCount++;
                        }
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.mergeClothingPreset)
                        {
                            if (PatreonFeatures.GetMergeClothingButtonState(atom, fileReferenceDict[FileReferenceTypes.clothingPreset]) == ButtonState.inactive) return ButtonState.inactive;
                            else activeAtomsCount++;
                        }
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.undressClothingPreset)
                        {
                            if (PatreonFeatures.GetUndressClothingButtonState(atom, fileReferenceDict[FileReferenceTypes.clothingPreset]) == ButtonState.inactive) return ButtonState.inactive;
                            else activeAtomsCount++;
                        }
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.undressAllClothing)
                        {
                            if (clothingComponent.GetUndressAllClothingButtonState(atom) == ButtonState.inactive) return ButtonState.inactive;
                            else activeAtomsCount++;
                        }
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.toggleNodePositionState || buttonOpTypeJSEnum.val == UIAButtonOpType.toggleNodeRotationState)
                        {
                            if (!nodeControlOnComponent.IsMatchingState(atom, buttonOpTypeJSEnum.val == UIAButtonOpType.toggleNodeRotationState)) return ButtonState.inactive;
                            else activeAtomsCount++;
                        }
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.toggleNodePhysics)
                        {
                            if (!nodePhysicsOnComponent.IsMatchingState(atom, nodeSelectionComponent.GetActiveNodeSelections())) return ButtonState.inactive;
                            else activeAtomsCount++;
                        }
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.toggleClothingItem)
                        {
                            FileReference fileRef = null;
                            if (clothingComponent.clothingGenderJSE.val == ClothingGenderTypes.femaleClothing) fileRef = fileReferenceDict[FileReferenceTypes.femaleClothingItem];
                            else if (clothingComponent.clothingGenderJSE.val == ClothingGenderTypes.maleClothing) fileRef = fileReferenceDict[FileReferenceTypes.maleClothingItem];
                            if (fileRef != null)
                            {
                                string clothingItemFilename = fileRef.GetCurrentFile(atomName);
                                if (clothingItemFilename == "") clothingItemFilename = fileRef.currentActionSelectedFile;
                                if (clothingItemFilename != "")
                                {                                      
                                    var jsb = AtomUtils.GetClothingActive(atom, clothingItemFilename);
                                    if (jsb != null)
                                    {
                                        if (jsb.val) activeAtomsCount++;
                                        else return ButtonState.inactive;
                                    }
                                }
                                
                            }
                        }
                    }
                }
                if (activeAtomsCount > 0 && activeAtomsCount==atomNames.Count) return ButtonState.active;
                else return ButtonState.inactive;
            }
            else
            {
                if (UIAConsts.debugLogging) SuperController.LogMessage("UIA.UIAButton.GetButtonState: Potentially unhandled Button type (" + buttonOpTypeJSEnum.displayVal + ") that is not multistate or does not have a target component");
                return ButtonState.inactive;
            }
        }

        public List<int> GetFileReferenceTypes()
        {
            List<int> fileRefTypes = new List<int>();

            foreach (int frt in UIAButtonOpType.GetFileReferenceTypes(buttonOpTypeJSEnum.val)) fileRefTypes.Add(frt);

            if (buttonOpTypeJSEnum.val == UIAButtonOpType.spawnAtom)
            {
                if (spawnAtomComponent.atomTypeJSEnum.val == AtomTypes.person)
                {
                    if (spawnAtomComponent.onlyGeneralPresetJSB.val)
                    {
                        fileRefTypes.Remove(FileReferenceTypes.subScene);
                        fileRefTypes.Remove(FileReferenceTypes.appearancePreset);
                        fileRefTypes.Remove(FileReferenceTypes.posePreset);
                        fileRefTypes.Remove(FileReferenceTypes.pluginsPreset);
                        fileRefTypes.Remove(FileReferenceTypes.cua);
                    }
                    else
                    {
                        fileRefTypes.Remove(FileReferenceTypes.generalPreset);
                        fileRefTypes.Remove(FileReferenceTypes.subScene);
                        fileRefTypes.Remove(FileReferenceTypes.cua);
                    }
                    
                }
                else if (spawnAtomComponent.atomTypeJSEnum.val == AtomTypes.subScene)
                {
                    fileRefTypes.Remove(FileReferenceTypes.generalPreset);
                    fileRefTypes.Remove(FileReferenceTypes.appearancePreset);
                    fileRefTypes.Remove(FileReferenceTypes.posePreset);
                    fileRefTypes.Remove(FileReferenceTypes.pluginsPreset);
                    fileRefTypes.Remove(FileReferenceTypes.cua);
                }
                else if (spawnAtomComponent.atomTypeJSEnum.val == AtomTypes.cua)
                {
                    fileRefTypes.Remove(FileReferenceTypes.subScene);
                    fileRefTypes.Remove(FileReferenceTypes.appearancePreset);
                    fileRefTypes.Remove(FileReferenceTypes.posePreset);
                    fileRefTypes.Remove(FileReferenceTypes.pluginsPreset);
                }
                else
                {
                    fileRefTypes.Remove(FileReferenceTypes.subScene);
                    fileRefTypes.Remove(FileReferenceTypes.appearancePreset);
                    fileRefTypes.Remove(FileReferenceTypes.posePreset);
                    fileRefTypes.Remove(FileReferenceTypes.pluginsPreset);
                    fileRefTypes.Remove(FileReferenceTypes.cua);
                }
            }

            if (UIAButtonOpType.IsClothingItemType( buttonOpTypeJSEnum.val))
            {
                if (clothingComponent.clothingGenderJSE.val == ClothingGenderTypes.femaleClothing) fileRefTypes.Remove(FileReferenceTypes.maleClothingItem);
                else fileRefTypes.Remove(FileReferenceTypes.femaleClothingItem);
            }

            if (buttonOpTypeJSEnum.val == UIAButtonOpType.loadPluginsPreset)
            {
                if (targetComponent.targetCategoryJSEnum.val == TargetCategory.scenePlugins || targetComponent.targetCategoryJSEnum.val == TargetCategory.sessionPlugins) fileRefTypes.Remove(FileReferenceTypes.pluginsPreset);
                if (targetComponent.targetCategoryJSEnum.val == TargetCategory.scenePlugins) fileRefTypes.Add(FileReferenceTypes.scenePluginPreset);
                if (targetComponent.targetCategoryJSEnum.val == TargetCategory.sessionPlugins) fileRefTypes.Add(FileReferenceTypes.sessionPluginPreset);
            }

            return fileRefTypes;
        }

        public void AppendThumbnailFileReferences(List<FileReference> fileRefList)
        {
            foreach (int fileRefType in GetFileReferenceTypes())
            {
                if (fileReferenceDict[fileRefType].IsThumbnailFileRefType()) fileRefList.Add(fileReferenceDict[fileRefType]);
            }
        }

        public bool IsComponentInButtonOpType(int componentType)
        {
            if (targetComponent.SpawnAtomIfTargetMissing())
            {
                if (componentType == ButtonComponentTypes.relativePositionComponent) return true;
            }
            return UIAButtonOpType.IsComponentInButtonType(buttonOpTypeJSEnum.val, componentType);
        }

        public List<int> GetComponentTypes()
        {
            List<int> componentTypes = new List<int> (UIAButtonOpType.GetComponentTypes(buttonOpTypeJSEnum.val));
          
            if (targetComponent.SpawnAtomIfTargetMissing()) componentTypes.Add(ButtonComponentTypes.relativePositionComponent);

            return componentTypes;
        }


        public void ButtonCategoryChanged(int categoryEnum)
        {
            buttonOpTypeJSEnum.SetEnumChoices(UIAButtonOpType.enumManifestName, UIAButtonOpType.GetButtonTypeExclusions(buttonOpCategoryJSEnum.val));
        }

        public void RefreshFileRefTypes()
        {
            foreach (int fileRefType in GetFileReferenceTypes())
            {
                if (!fileReferenceDict.ContainsKey(fileRefType)) fileReferenceDict.Add(fileRefType, new FileReference(fileRefType, this));
            }
        }

        protected void ButtonTypeChanged(int buttonType)
        {
            CreateButtonComponents();
            RefreshFileRefTypes();
            
            if (IsComponentInButtonOpType(ButtonComponentTypes.targetComponent)) targetComponent.ButtonTypeUpdated();
            if (IsComponentInButtonOpType(ButtonComponentTypes.relativePositionComponent)) relativePositionComponent.ButtonTypeUpdated();
            if (IsComponentInButtonOpType(ButtonComponentTypes.pluginSettingComponent)) pluginSettingComponent.ButtonTypeUpdated();

            if (buttonOpCategoryJSEnum.val != UIAButtonCategory.clothing && pluginSettingComponent!=null)
            {
                pluginSettingComponent.atomButtonToggleStates.Clear();
            }
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.activeClothingEditor) targetComponent.targetCategoryJSEnum.val = TargetCategory.gazeSelectedAtom;
            parentButton.UpdateThumbnailImage(parentButton.buttonTexture);
        }
        protected void SavePresetChanged(bool enabled)
        {
            var fileRefs = GetFileReferenceTypes();
            if (fileRefs.Count>0) fileReferenceDict[fileRefs[0]].UpdateFileSelectionModeExclusions();

        }

        public void AtomNameUpdate(string oldName, string newName)
        {

            foreach (FileReference fileRef in fileReferenceDict.Values.ToList())
            {
                fileRef.AtomNameUpdate(oldName, newName);
            }

            if (targetComponent!=null)targetComponent.AtomNameUpdate(oldName, newName);
            if (pluginSettingComponent != null) pluginSettingComponent.AtomNameUpdate(oldName, newName);

        }

        public void AtomRemovedUpdate(string oldName)
        {
            foreach (FileReference fileRef in fileReferenceDict.Values.ToList())
            {
                fileRef.AtomRemovedUpdate(oldName);
            }
            if (targetComponent != null) targetComponent.AtomRemovedUpdate(oldName);
            if (pluginSettingComponent != null) pluginSettingComponent.AtomRemovedUpdate(oldName);
        }

        public string LabelKeyWordsReplace(string label)
        {
            if (IsComponentInButtonOpType(ButtonComponentTypes.targetComponent) && label.IndexOf("<TARGET>")>=0)
            {
                string targetName = "";              

                if (targetComponent.targetCategoryJSEnum.val == TargetCategory.atomGroup || targetComponent.targetCategoryJSEnum.val == TargetCategory.userChosenAtom) targetName = targetComponent.targetNameJSMultiEnum.displayVal;
                else if (targetComponent.targetCategoryJSEnum.val == TargetCategory.scenePlugins) targetName = "Scene Plugins";
                else if (targetComponent.targetCategoryJSEnum.val == TargetCategory.sessionPlugins) targetName = "Session Plugins";
                else if (targetComponent.targetCategoryJSEnum.val == TargetCategory.triggerAtom) targetName = "Trigger Atom";
                else
                {
                    targetName = targetComponent.GetTargetAtomName();
                    if (targetName == "") targetName = targetComponent.targetNameJSMultiEnum.displayVal;
                }

                if (targetName != "")
                {
                    var labelSB = new StringBuilder(label);
                    labelSB.Replace("<TARGET>", targetName);
                    label = labelSB.ToString();
                }
            }

            if (IsComponentInButtonOpType(ButtonComponentTypes.spawnAtomComponent) && label.IndexOf("<ATOMTYPE>") >= 0)
            {
                var labelSB = new StringBuilder(label);
                labelSB.Replace("<ATOMTYPE>", spawnAtomComponent.atomTypeJSEnum.displayVal);
                label = labelSB.ToString();
            }

            if (IsComponentInButtonOpType(ButtonComponentTypes.relativePositionComponent) && label.IndexOf("<RELATIVETARGET>") >= 0)
            {
                string relPosAtomName = relativePositionComponent.GetRelativePositionAtomName();
                if (relPosAtomName == "") relPosAtomName = relativePositionComponent.atomPositionRelativeToJSEnum.displayVal;

                var labelSB = new StringBuilder(label);
                labelSB.Replace("<RELATIVETARGET>", relPosAtomName);
                label = labelSB.ToString();
            }

            if (label.IndexOf("<FILEREF1>") >= 0)
            {
                List<int> fileRefTypes = GetFileReferenceTypes();
                if (fileRefTypes.Count > 0)
                {
                    FileReference fileRef = fileReferenceDict[fileRefTypes.First()];

                    var labelSB = new StringBuilder(label);
                    
                    string fileRefName = "<FILEREF1>";
                    var fileRefNameSB = new StringBuilder("<FILEREF1>");

                    if (fileRef.fileSelectionModeJSEnum.val == FileSelectionMode.singleFile)
                    {
                        string filePath = fileRef.filePathJSString.val;
                        if (filePath != "")
                        {
                            int filenameIndex = filePath.LastIndexOf('/');
                            if (filenameIndex == -1) fileRefNameSB = new StringBuilder( filePath);
                            else
                            {
                                fileRefNameSB = new StringBuilder();
                                fileRefNameSB.Append(filePath, filenameIndex, filePath.Length - filenameIndex);
                                fileRefName = fileRefNameSB.ToString();
                                if (fileRefName.IndexOf("Preset_")==0) fileRefName = fileRefName.Substring(8);
                                if (fileRefName.IndexOf(".")>=0) fileRefName = fileRefName.Substring(0, fileRefName.LastIndexOf('.'));
                                fileRefName = "'" + fileRefName + "'";
                            }
                        }
                    }
                    else fileRefName = "";

                    labelSB.Replace("<FILEREF1>", fileRefName);
                    label = labelSB.ToString();
                }
            }
            

            if (IsComponentInButtonOpType(ButtonComponentTypes.pluginSettingComponent))
            {
                if (label.IndexOf("<PLUGINTYPE>") >= 0)
                {
                    string pluginType = pluginSettingComponent.pluginTypeJSSC.val;
                    if (pluginType != "<None available>")
                    {
                        var labelSB = new StringBuilder(label);
                        labelSB.Replace("<PLUGINTYPE>", pluginType);
                        label = labelSB.ToString();
                    }
                }

                if (label.IndexOf("<PARAMON>") >= 0)
                {
                    string paramOn = pluginSettingComponent.targetParamNameOnJSSC.val;
                    if (paramOn != "<None available>")
                    {
                        var labelSB = new StringBuilder(label);
                        labelSB.Replace("<PARAMON>", paramOn);
                        label = labelSB.ToString();
                    }
                }

                if (label.IndexOf("<PARAMOFF>") >= 0)
                {
                    string paramOff = pluginSettingComponent.targetParamNameOffJSSC.val;
                    if (paramOff != "<None available>")
                    {
                        var labelSB = new StringBuilder(label);
                        labelSB.Replace("<PARAMOFF>", paramOff);
                        label = labelSB.ToString();
                    }
                }
                
            }
            if (IsComponentInButtonOpType(ButtonComponentTypes.switchUIAGridComponent) && label.IndexOf("<UIASCREEN>") >= 0)
            {
                var labelSB = new StringBuilder(label);
                labelSB.Replace("<UIASCREEN>", switchUIAGridComponent.switchUIAGridTargetJSI.val.ToString());
                label = labelSB.ToString();
            }

            if (IsComponentInButtonOpType(ButtonComponentTypes.worldScaleComponent) && label.IndexOf("<WORLDSCALE>") >= 0)
            {
                var labelSB = new StringBuilder(label);
                labelSB.Replace("<WORLDSCALE>", worldScaleComponent.worldScaleAbsJSF.val.ToString());
                label = labelSB.ToString();
            }

            return label;
        }

        public void TargetReset()
        {
            targetComponent.currentActionTargetAtomNames = null;
            targetComponent.currentActionAltTargetAtomNames = null;
        }
        public void FileRefReset()
        {
            foreach (int fileRefType in GetFileReferenceTypes()) fileReferenceDict[fileRefType].Reset();
        }

        public bool CheckActionTarget()
        {
            if (!IsComponentInButtonOpType(ButtonComponentTypes.targetComponent)) return true;

            if (targetComponent.currentActionTargetAtomNames!=null && (!targetComponent.isAltTargetType || targetComponent.currentActionAltTargetAtomNames != null)) return true;

            targetComponent.SetCurrentActionTargetAtoms();

            if (targetComponent.currentActionTargetAtomNames != null && (!targetComponent.isAltTargetType || targetComponent.currentActionAltTargetAtomNames != null)) return true;

            return false;
        }

        public bool CheckActionFileReferences()
        {
            if (IsComponentInButtonOpType(ButtonComponentTypes.targetComponent) && targetComponent.currentActionTargetAtomNames.Count == 0) return true;          

            foreach (int fileRefType in GetFileReferenceTypes())
            {
                
                FileReference fileRef = fileReferenceDict[fileRefType];

                if (!fileRef.currentActionFileSelectComplete && fileRef.fileSelectionModeJSEnum.val != FileSelectionMode.none)
                {
                    bool fileSelectionComplete = fileRef.GetNextFileSelection();
                    if (!fileSelectionComplete)  return false;
                }
            }
            return true;
        }
      
        public void PlayAction(int buttonState)
        {
            if(maxButtonOpStates==1 && parentButton.forceToggleButtonOpsJSB.val)
            {
                if ((buttonStateTransitionActivationJSE.val == ButtonStateTranistion.onActivate && buttonState==ButtonState.active)||(buttonStateTransitionActivationJSE.val == ButtonStateTranistion.onDeactivate && buttonState == ButtonState.inactive))
                {
                    if (!IsComponentInButtonOpType(ButtonComponentTypes.targetComponent) && buttonState == ButtonState.active) targetComponent.ResetLastUserChosenAtom();
                    return;
                }              
            }
            if (!IsComponentInButtonOpType(ButtonComponentTypes.targetComponent))
            {
                if (GetFileReferenceTypes().Count == 0) PlayDiscreteAction();
                else PlayNoTargetFileRefAction();
            }
            else PlayTargetableAction(buttonState);


        }

        private void PlayNoTargetFileRefAction()
        {
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.loadScene) SuperController.singleton.Load(fileReferenceDict[FileReferenceTypes.scene].currentActionSelectedFile);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.mergeLoadScene) SuperController.singleton.LoadMerge(fileReferenceDict[FileReferenceTypes.scene].currentActionSelectedFile);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.loadUIAProfile) UIAPProfile.LoadUIAPSelected(fileReferenceDict[FileReferenceTypes.uiap].currentActionSelectedFile, fileReferenceDict[FileReferenceTypes.uiap].closeGridOnLoadUIAPJSB.val);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.spawnAtom) spawnAtomComponent.SpawnAtomAction();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.loadSceneAudio) LoadSceneAudio();
            else if (UIAConsts.debugLogging) SuperController.LogMessage("UIA.UIAButton.PlayNoTargetFileRefAction: Unhandled button type '" + buttonOpTypeJSEnum.displayVal + "'");
        }

        private void PlayDiscreteAction()
        {
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.suppressScaleLoad) PresetLoadSettings.suppressScaleLoadJSB.val = !PresetLoadSettings.suppressScaleLoadJSB.val;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.baSwitchVARMgmtRuleset) baVARRulesetComponent.SwitchBARuleset();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.baApplyVARMgmtRuleset) BARulesetSelectionComponent.ApplyCurrentRuleset();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.suppressClothingLoad) PresetLoadSettings.suppressClothingLoadJSB.val = !PresetLoadSettings.suppressClothingLoadJSB.val;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.onlyLoadClothing) PresetLoadSettings.onlyLoadClothingFromAppPresetJSB.val = !PresetLoadSettings.onlyLoadClothingFromAppPresetJSB.val;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.startPlayback) SuperController.singleton.motionAnimationMaster.StartPlayback();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.stopPlayback) SuperController.singleton.motionAnimationMaster.StopPlayback();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.toggleMocapPlay)
            {
                if (SuperController.singleton.motionAnimationMaster.activeWhilePlaying != null)
                {
                    if (SuperController.singleton.motionAnimationMaster.activeWhilePlaying.activeInHierarchy) SuperController.singleton.motionAnimationMaster.StopPlayback();
                    else SuperController.singleton.motionAnimationMaster.StartPlayback();
                }
                else if (SuperController.singleton.motionAnimationMaster.activeWhileStopped == null)
                {
                    if (!SuperController.singleton.motionAnimationMaster.activeWhileStopped.activeInHierarchy) SuperController.singleton.motionAnimationMaster.StopPlayback();
                    else SuperController.singleton.motionAnimationMaster.StartPlayback();
                }
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.resetAnimation) SuperController.singleton.motionAnimationMaster.ResetAnimation();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.beginRecordMode) SuperController.singleton.motionAnimationMaster.StartRecordMode();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.stopRecording) SuperController.singleton.motionAnimationMaster.StopRecordMode();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.toggleLoopPlayback) SuperController.singleton.motionAnimationMaster.loop = !SuperController.singleton.motionAnimationMaster.loop;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.freezeMotionSound) SuperController.singleton.SetFreezeAnimation(!SuperController.singleton.freezeAnimation);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.togglePlayEdit)
            {
                if (SuperController.singleton.gameMode == SuperController.GameMode.Play)
                {
                    SuperController.singleton.gameMode = SuperController.GameMode.Edit;
                    if (vamPlayEditModeComponent.openGameUIonEditModeJSBool.val) SuperController.singleton.ShowMainHUDAuto();
                }
                else
                {
                    SuperController.singleton.gameMode = SuperController.GameMode.Play;
                    if (vamPlayEditModeComponent.closeGameUIonPlayModeJSBool.val) SuperController.singleton.HideMainHUD();
                }
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.setEditMode)
            {
                SuperController.singleton.gameMode = SuperController.GameMode.Edit;
                if (vamPlayEditModeComponent.openGameUIonEditModeJSBool.val) SuperController.singleton.ShowMainHUDAuto();
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.setPlayMode)
            {
                SuperController.singleton.gameMode = SuperController.GameMode.Play;
                if (vamPlayEditModeComponent.closeGameUIonPlayModeJSBool.val) SuperController.singleton.HideMainHUD();
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.showVRHands) showVRHandsComponent.ToggleVRHands();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.leapMotionToggle) showVRHandsComponent.ToggleLeapMotion();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.vrHandCollision) SuperController.singleton.commonHandModelControl.ToggleCollision();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.playerHeightAdjust) userPreferencesComponent.SetPlayerHeightAdjust();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.lockHeightDuringNavigate) userPreferencesComponent.SetNavigateLockHeight();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.freeMoveFollowFloor) userPreferencesComponent.SetFreeMoveFollowFloor();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.grabNavPosMult) userPreferencesComponent.SetGrabNavPosMult();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.grabNavRotMult) userPreferencesComponent.SetGrabNavRotMult();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.disableGrabNav) userPreferencesComponent.SetDisableGrabNav();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.disableAllNav) userPreferencesComponent.SetDisableAllNav();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.disableTeleport) userPreferencesComponent.SetDisableTeleport();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.teleportAllowRot) userPreferencesComponent.SetTeleportAllowRot();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.freeMoveMultiplier) userPreferencesComponent.SetFreeMoveMultiplier();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.optimizeMemory) MemoryOptimizer.singleton.TriggerOptimize();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.toggleTargets) SuperController.singleton.ToggleTargetsOnWithButton();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.hubBrowser) SuperController.singleton.OpenHub();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.mainVAMMenu)
            {
                SuperController.singleton.ShowMainHUDAuto();
                SuperController.singleton.activeUI = SuperController.ActiveUI.MainMenu;
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.exitVAM) SuperController.singleton.Quit();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.leapMotionEnable) showVRHandsComponent.SetLeapMotion();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.setWorldScale) SuperController.singleton.worldScale = worldScaleComponent.worldScaleAbsJSF.val;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.offsetWorldScale) SuperController.singleton.worldScale += worldScaleComponent.worldScaleIncrementJSF.val;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.browserAssistSetFilters) baFilterComponent.SetFilters();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.softBodyPhysicsToggle) UserPreferences.singleton.softPhysics = !UserPreferences.singleton.softPhysics;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.desktopVSyncToggle) UserPreferences.singleton.desktopVsync = !UserPreferences.singleton.desktopVsync;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.realtimeReflectionProbesToggle) UserPreferences.singleton.realtimeReflectionProbes = !UserPreferences.singleton.realtimeReflectionProbes;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.mirrorReflectionsToggle) UserPreferences.singleton.mirrorReflections = !UserPreferences.singleton.mirrorReflections;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.hqPhysicsToggle) UserPreferences.singleton.physicsHighQuality = !UserPreferences.singleton.physicsHighQuality;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.vrHeadCollider) UserPreferences.singleton.useHeadCollider = !UserPreferences.singleton.useHeadCollider;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.vrHeadCollider) UserPreferences.singleton.useHeadCollider = !UserPreferences.singleton.useHeadCollider;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.shaderQaulity)
            {
                if (userPreferencesComponent.shaderQualityJSEnum.val == UserPrefShaderQuality.high) UserPreferences.singleton.shaderLOD = UserPreferences.ShaderLOD.High;
                else if (userPreferencesComponent.shaderQualityJSEnum.val == UserPrefShaderQuality.medium) UserPreferences.singleton.shaderLOD = UserPreferences.ShaderLOD.Medium;
                else if (userPreferencesComponent.shaderQualityJSEnum.val == UserPrefShaderQuality.low) UserPreferences.singleton.shaderLOD = UserPreferences.ShaderLOD.Low;
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.msaaLevel)
            {
                if (userPreferencesComponent.msaaLevelJSEnum.val == UserPrefMSAALevel.off) UserPreferences.singleton.msaaLevel = 0;
                else if (userPreferencesComponent.msaaLevelJSEnum.val == UserPrefMSAALevel.x2) UserPreferences.singleton.msaaLevel = 2;
                else if (userPreferencesComponent.msaaLevelJSEnum.val == UserPrefMSAALevel.x4) UserPreferences.singleton.msaaLevel = 4;
                else if (userPreferencesComponent.msaaLevelJSEnum.val == UserPrefMSAALevel.x8) UserPreferences.singleton.msaaLevel = 8;
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.pixelLightCount)
            {
                if (userPreferencesComponent.pixelLightCountJSEnum.val == UserPrefPixelLightCount.zero) UserPreferences.singleton.pixelLightCount = 0;
                else if (userPreferencesComponent.pixelLightCountJSEnum.val == UserPrefPixelLightCount.one) UserPreferences.singleton.pixelLightCount = 1;
                else if (userPreferencesComponent.pixelLightCountJSEnum.val == UserPrefPixelLightCount.two) UserPreferences.singleton.pixelLightCount = 2;
                else if (userPreferencesComponent.pixelLightCountJSEnum.val == UserPrefPixelLightCount.three) UserPreferences.singleton.pixelLightCount = 3;
                else if (userPreferencesComponent.pixelLightCountJSEnum.val == UserPrefPixelLightCount.four) UserPreferences.singleton.pixelLightCount = 4;
                else if (userPreferencesComponent.pixelLightCountJSEnum.val == UserPrefPixelLightCount.five) UserPreferences.singleton.pixelLightCount = 5;
                else if (userPreferencesComponent.pixelLightCountJSEnum.val == UserPrefPixelLightCount.six) UserPreferences.singleton.pixelLightCount = 6;
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.smoothPasses)
            {
                if (userPreferencesComponent.smoothPassesJSEnum.val == UserPrefSmoothPasses.zero) UserPreferences.singleton.smoothPasses = 0;
                else if (userPreferencesComponent.smoothPassesJSEnum.val == UserPrefSmoothPasses.one) UserPreferences.singleton.smoothPasses = 1;
                else if (userPreferencesComponent.smoothPassesJSEnum.val == UserPrefSmoothPasses.two) UserPreferences.singleton.smoothPasses = 2;
                else if (userPreferencesComponent.smoothPassesJSEnum.val == UserPrefSmoothPasses.three) UserPreferences.singleton.smoothPasses = 3;
                else if (userPreferencesComponent.smoothPassesJSEnum.val == UserPrefSmoothPasses.four) UserPreferences.singleton.smoothPasses = 4;
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.glowEffects)
            {
                if (userPreferencesComponent.glowEffectsJSEnum.val == UserPrefGlowEffects.off) UserPreferences.singleton.glowEffects = UserPreferences.GlowEffectsLevel.Off;
                else if (userPreferencesComponent.glowEffectsJSEnum.val == UserPrefGlowEffects.low) UserPreferences.singleton.glowEffects = UserPreferences.GlowEffectsLevel.Low;
                else if (userPreferencesComponent.glowEffectsJSEnum.val == UserPrefGlowEffects.high) UserPreferences.singleton.glowEffects = UserPreferences.GlowEffectsLevel.High;

            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.physicsRate)
            {
                if (userPreferencesComponent.physicsRateJSEnum.val == UserPrefPhysicsRate.auto) UserPreferences.singleton.physicsRate = UserPreferences.PhysicsRate.Auto;
                else if (userPreferencesComponent.physicsRateJSEnum.val == UserPrefPhysicsRate.hz45) UserPreferences.singleton.physicsRate = UserPreferences.PhysicsRate._45;
                else if (userPreferencesComponent.physicsRateJSEnum.val == UserPrefPhysicsRate.hz60) UserPreferences.singleton.physicsRate = UserPreferences.PhysicsRate._60;

                else if (userPreferencesComponent.physicsRateJSEnum.val == UserPrefPhysicsRate.hz72) UserPreferences.singleton.physicsRate = UserPreferences.PhysicsRate._72;
                else if (userPreferencesComponent.physicsRateJSEnum.val == UserPrefPhysicsRate.hz80) UserPreferences.singleton.physicsRate = UserPreferences.PhysicsRate._80;
                else if (userPreferencesComponent.physicsRateJSEnum.val == UserPrefPhysicsRate.hz90) UserPreferences.singleton.physicsRate = UserPreferences.PhysicsRate._90;
                else if (userPreferencesComponent.physicsRateJSEnum.val == UserPrefPhysicsRate.hz120) UserPreferences.singleton.physicsRate = UserPreferences.PhysicsRate._120;
                else if (userPreferencesComponent.physicsRateJSEnum.val == UserPrefPhysicsRate.hz144) UserPreferences.singleton.physicsRate = UserPreferences.PhysicsRate._144;
                else if (userPreferencesComponent.physicsRateJSEnum.val == UserPrefPhysicsRate.hz240) UserPreferences.singleton.physicsRate = UserPreferences.PhysicsRate._240;
                else if (userPreferencesComponent.physicsRateJSEnum.val == UserPrefPhysicsRate.hz288) UserPreferences.singleton.physicsRate = UserPreferences.PhysicsRate._288;
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.physicsUpdateCap)
            {
                if (userPreferencesComponent.physicsUpdateCapJSEnum.val == UserPrefPhysicsUpdateCap.one) UserPreferences.singleton.physicsUpdateCap = 1;
                else if (userPreferencesComponent.physicsUpdateCapJSEnum.val == UserPrefPhysicsUpdateCap.two) UserPreferences.singleton.physicsUpdateCap = 2;
                else if (userPreferencesComponent.physicsUpdateCapJSEnum.val == UserPrefPhysicsUpdateCap.three) UserPreferences.singleton.physicsUpdateCap = 3;

            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.softBodyPhysics) UserPreferences.singleton.softPhysics = userPreferencesComponent.softbodyPhysicsJSB.val;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.desktopVSync) UserPreferences.singleton.desktopVsync = userPreferencesComponent.desktopVSyncJSB.val;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.realtimeReflectionProbes) UserPreferences.singleton.realtimeReflectionProbes = userPreferencesComponent.realtimeReflectionProbesJSB.val;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.mirrorReflections) UserPreferences.singleton.mirrorReflections = userPreferencesComponent.mirrorReflectionsJSB.val;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.hqPhysics) UserPreferences.singleton.physicsHighQuality = userPreferencesComponent.hqPhysicsJSB.val;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.renderScale) UserPreferences.singleton.renderScale = userPreferencesComponent.renderScaleJSF.val;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.perfMon)
            {
                PerfMon pm = UserPreferences.singleton.transform.Find("PerfMon").GetComponent<PerfMon>();
                pm.onToggle.isOn = !pm.onToggle.isOn;
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.showHiddenAtoms) SuperController.singleton.showHiddenAtoms = !SuperController.singleton.showHiddenAtoms;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.switchUIAGrid)
            {
                if (UIAStorables.buttonGridsList.Count >= switchUIAGridComponent.switchUIAGridTargetJSI.val && UIAStorables.buttonGridsList[switchUIAGridComponent.switchUIAGridTargetJSI.val - 1]._activeButtonCount > 0)
                {
                    GridsControlDisplay.SetCurrentGrid(switchUIAGridComponent.switchUIAGridTargetJSI.val - 1);
                }
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.gazeAssistedSelect)
            {
                if (GazeAssistedSelectTool.gazeAssistActiveJSB.val == false)
                {
                    GazeAssistedSelectTool.gazeAssistActiveJSB.val = true;
                    SuperController.singleton.gameMode = SuperController.GameMode.Edit;
                }
                else GazeAssistedSelectTool.gazeAssistActiveJSB.val = false;
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.suppressPresetLocks) PresetLoadSettings.suppressPresetLocksJSB.val = !PresetLoadSettings.suppressPresetLocksJSB.val;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.hideInactiveTargets) UserPreferences.singleton.hideInactiveTargets = !UserPreferences.singleton.hideInactiveTargets;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.raiseByHAHeight) HeelAdjustTool.heelAdjustRaisePeopleJSB.val = !HeelAdjustTool.heelAdjustRaisePeopleJSB.val;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.hideUGameControlUI) UIAGlobals.hideGameControlUI = true;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.activateGlobalFunction && linkedGlobalFunction != null)
            {
                if (SuperController.singleton.mainHUD == null) UIAButton.hudOnPreButtonAction = false;
                else UIAButton.hudOnPreButtonAction = SuperController.singleton.mainHUD.gameObject.activeInHierarchy;
                linkedGlobalFunction.ActionInit(vrHandActionInit, leapActivatedAction);
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.pluginAssistLoad && UIAPluginInterop._loadPluginsUIJSON != null) UIAPluginInterop._loadPluginsUIJSON.actionCallback();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.pluginAssistFind && UIAPluginInterop._findPluginsUIJSON != null) UIAPluginInterop._findPluginsUIJSON.actionCallback();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.browserAssistOpen && UIAPluginInterop.openBrowserAssistUIJSAction != null) UIAPluginInterop.openBrowserAssistUIJSAction.actionCallback();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.browserAssistClose && UIAPluginInterop.closeBrowserAssistUIJSAction != null) UIAPluginInterop.closeBrowserAssistUIJSAction.actionCallback();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.browserAssistToggle && UIAPluginInterop.toggleBrowserAssistUIJSAction != null) UIAPluginInterop.toggleBrowserAssistUIJSAction.actionCallback();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.browserAssistOpenResourceType && UIAPluginInterop.openBrowserAssistResourceTypeUIJSActions.Count > 0)
            {
                var baAction = UIAPluginInterop.openBrowserAssistResourceTypeUIJSActions.Where(x => x.name == pluginsLoadComponent.browserAssistResourceTypeJSSC.val).FirstOrDefault();
                if (baAction != null) baAction.actionCallback();
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.teleportPlayer) relativePositionComponent.TeleportPlayer();
            

            else if (UIAConsts.debugLogging) SuperController.LogMessage("UIA.UIAButton.PlayDiscreteAction: Unhandled button type '" + buttonOpTypeJSEnum.displayVal + "'");
        }

        private void PlayTargetableAction(int buttonState)
        {
            var targetAtomNames = new List<string>(targetComponent.currentActionTargetAtomNames);
            if (buttonOpTypeJSEnum.val == UIAButtonOpType.deleteAtomsExcept)
            {
                foreach (var atom in AtomUtils.GetAtoms(true))
                {
                    if (targetAtomNames.Contains(atom.name)) continue;
                    if (atom.name == "PlayerNavigationPanel") continue;
                    if (atom.name == "CoreControl") continue;
                    if (atom.name == "WindowCamera") continue;
                    atom.Remove();
                }
            }
            else
            {
                foreach (string atomName in targetAtomNames)
                {
                    Atom atom = SuperController.singleton.GetAtomByUid(atomName);
                    if (buttonOpTypeJSEnum.val == UIAButtonOpType.loadGeneralPreset && targetComponent.SpawnAtomIfTargetMissing() && atom == null) spawnAtomComponent.SpawnAtomAction();
                    else
                    {
                        if (atomName == "CoreControl" && targetComponent.targetCategoryJSEnum.val == TargetCategory.sessionPlugins) atom = UIAGlobals.mvrScript.containingAtom;
                        if (atom != null) PlayActionTargetAtom(atom, buttonState);
                    }
                }
            }
        }

        private URLAudioClip LoadSceneAudio()
        {
            if (fileReferenceDict[FileReferenceTypes.audio].currentActionSelectedFile != "")
            {
                string audioFullPathReference = SuperController.singleton.NormalizePath(fileReferenceDict[FileReferenceTypes.audio].currentActionSelectedFile);

                if (audioFullPathReference != "")
                {
                    URLAudioClip urlNAC = null;
                    bool nacPreLoaded = false;

                    try
                    {
                        urlNAC = URLAudioClipManager.singleton.GetClip(SuperController.singleton.NormalizeMediaPath(audioFullPathReference)) as URLAudioClip;
                        nacPreLoaded = urlNAC != null;
                        if (!nacPreLoaded) urlNAC = URLAudioClipManager.singleton.QueueClip(SuperController.singleton.NormalizeMediaPath(audioFullPathReference));

                        return urlNAC;
                    }
                    catch (Exception e)
                    {
                        SuperController.LogError("BA.ResourceVersionGroupEntry.ActivateInternal: Exception caught queing Audio:\n" + e);
                    }
                }
            }
            
            return null;
        }

        private IEnumerator PlayAudioPostLoad(URLAudioClip urlNAC, Atom targetAtom, int buttonOpType)
        {
            while (!urlNAC.ready) yield return new WaitForEndOfFrame();

            AudioSourceControl audioSourceControl = AudioUtils.GetAudioSourceControl(targetAtom);
            if (audioSourceControl != null)
            {
                if (buttonOpType == UIAButtonOpType.playAudio) audioSourceControl.PlayNow(urlNAC);
                else if (buttonOpType == UIAButtonOpType.queueAudio) audioSourceControl.QueueClip(urlNAC);
            }                  
        }

        private void PlayActionTargetAtom(Atom atom, int buttonState)
        {
            bool revertAtomOn = false;
            if (!atom.on && buttonOpTypeJSEnum.val != UIAButtonOpType.toggleAtomOn && buttonOpTypeJSEnum.val !=UIAButtonOpType.triggerVAMAction )
            {
                revertAtomOn = true;
                atom.ToggleOn();
            }

            if (buttonOpCategoryJSEnum.val == UIAButtonCategory.audio && (atom.category=="Sound" || atom.type=="Person"))
            {
                if (buttonOpTypeJSEnum.val == UIAButtonOpType.playAudio || buttonOpTypeJSEnum.val == UIAButtonOpType.queueAudio)
                {
                    var urlNAC = LoadSceneAudio();
                    SuperController.singleton.StartCoroutine(PlayAudioPostLoad(urlNAC, atom, buttonOpTypeJSEnum.val));
                }
                else
                {
                    AudioSourceControl audioSourceControl = AudioUtils.GetAudioSourceControl(atom);
                    if (audioSourceControl != null)
                    {
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.stopAudio) audioSourceControl.Stop();
                        else if (buttonOpTypeJSEnum.val == UIAButtonOpType.pauseAudio) audioSourceControl.Pause();
                        else if (buttonOpTypeJSEnum.val == UIAButtonOpType.unPauseAudio) audioSourceControl.UnPause();
                        else if (buttonOpTypeJSEnum.val == UIAButtonOpType.clearQueueAudio) audioSourceControl.ClearQueue();
                        else if (buttonOpTypeJSEnum.val == UIAButtonOpType.togglePauseAudio) audioSourceControl.TogglePause();
                    }
                }
            }
            else if (buttonOpCategoryJSEnum.val == UIAButtonCategory.legacyPresets) appearancePresetComponent.PlayLegacyPresetAction(atom);
            else if (buttonOpCategoryJSEnum.val == UIAButtonCategory.presets) appearancePresetComponent.PlayPresetAction(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.activeClothingEditor && PatreonFeatures.patreonContentEnabled)
            {
                if (GameControlUI.gameControlDisplayMode != GameControlDisplayModes.ace && GameControlUI.gameControlDisplayMode != GameControlDisplayModes.clothItemPresetSelect)
                {
                    GameControlUI.gameControlDisplayMode = GameControlDisplayModes.ace;
                    var ace = GridsDisplay._uiActiveClothingEditor;
                    ace.aceMode = targetComponent.aceModeJSEnum.val;
                    ace.aceRealClothingFilterActive = targetComponent.aceRealClothingFilterJSB.val;
                    if (targetComponent.aceModeJSEnum.val == ACEMode.original)
                    {
                        ace.aceRealClothingFilterActive = false;
                        ace.isGazeSelectedMode = true;
                        if (targetComponent.targetCategoryJSEnum.val == TargetCategory.gazeSelectedAtom)
                            ace.gazeSelectedTargetType = targetComponent.targetNameJSMultiEnum.valTopEnum;
                        else ace.gazeSelectedTargetType = LastViewedTargetType.lastViewedPerson;
                    }
                    else
                    {
                        ace.isGazeSelectedMode = false;
                        if (targetComponent.targetCategoryJSEnum.val == TargetCategory.gazeSelectedAtom)
                        {
                            ace.isGazeSelectedMode = true;
                            ace.gazeSelectedTargetType = targetComponent.targetNameJSMultiEnum.valTopEnum;
                        }
                        else
                        {
                            ace.RefreshPersonAtomNames();
                            if (ace.personAtomNamesJSSC.choices.Contains(atom.name)) ace.personAtomNamesJSSC.val = atom.name;
                        }
                    }
                }
                else GameControlUI.gameControlDisplayMode = GameControlDisplayModes.standard;
                GameControlUI.RefreshWristUIButtonGrid();
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.toggleAtomOn) atom.ToggleOn();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.hideAtom) atom.hidden = !atom.hidden;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.deleteAtom) atom.Remove();
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.toggleAtomCollision) atom.collisionEnabledJSON.val = !atom.collisionEnabledJSON.val;
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.detachAtomRoot)
            {
                JSONStorableBool detach = atom.GetStorableByID("control").GetBoolJSONParam("detachControl");
                if (detach != null) detach.val = !detach.val;
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.selectAtom)
            {
                FreeControllerV3 fcV3 = atom.freeControllers.First(fc => fc.name == "control");
                SuperController.singleton.SelectController(fcV3);
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.togglePresetLocks) presetLockComponent.ActionTogglePresetLock(atom, buttonState);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.changeAtomName) targetComponent.ChangeTargetAtomNameAction(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.parentAtom) targetComponent.ParentAtom(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.unParentAtom) targetComponent.UnparentAtom(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.cloneAtom) SuperController.singleton.StartCoroutine(targetComponent.CloneAtom(atom));
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.teleportAtom) relativePositionComponent.TeleportAtomToSpawnPoint(atom.name);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.moveAtom) moveAtomComponent.PlayMoveAtomAction(atom, vrHandActionInit, leapActivatedAction);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.loadSubScene && atom.type == "SubScene" && fileReferenceDict[FileReferenceTypes.subScene].currentActionSelectedFile != "")
            {
                JSONStorable js = atom.GetStorableByID("SubScene");
                JSONStorableUrl subScenePathJSON = js.GetUrlJSONParam("browsePath");
                string atomName = atom.uid;
                if (subScenePathJSON.val != SuperController.singleton.NormalizePath(fileReferenceDict[FileReferenceTypes.subScene].currentActionSelectedFile)) subScenePathJSON.val = SuperController.singleton.NormalizePath(fileReferenceDict[FileReferenceTypes.subScene].currentActionSelectedFile);
                else js.CallAction("LoadSubScene");
                atom.SetUID(atomName);
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.loadPlugins) pluginsLoadComponent.LoadPluginsAsPreset(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.unLoadPlugins) pluginsLoadComponent.UnloadPlugins(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.decalMakerToggle) decalMakerComponent.DecalMakerToggle(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.setHairColor) hairColorComponent.SetHairColor(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.setMorphValue) morphControlComponent.SetMorphValue(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.adjustMorphValue) morphControlComponent.AdjustMorphValue(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.resetMorphValue) morphControlComponent.ResetMorphValue(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.zeroMorphValues) morphControlComponent.ZeroMorphValue(atom);

            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.resetAppearance) appearancePresetComponent.ResetAppearance(atom, false, true, false);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.resetScale) appearancePresetComponent.ResetAppearance(atom, true, false, false);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.resetPose) appearancePresetComponent.ResetAppearance(atom, false, false, true);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.pluginAction) pluginSettingComponent.PluginAction(atom, true, false);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.pluginActionToggle) pluginSettingComponent.PluginAction(atom, true, true);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.pluginMultiSettings) pluginSettingComponent.PluginAction(atom, false, false);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.pluginMultiSettingsToggle) pluginSettingComponent.PluginAction(atom, false, true);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.pluginBoolToggle) pluginSettingComponent.PluginBool(atom, buttonState == ButtonState.inactive, true);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.pluginBoolTrue) pluginSettingComponent.PluginBool(atom, true, false);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.pluginBoolFalse) pluginSettingComponent.PluginBool(atom, false, false);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.pluginOpenUI) pluginSettingComponent.PluginOpenUI(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.unloadSpecificPlugin) pluginSettingComponent.PluginUnloadSpecific(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.pluginToggleOpenUI) pluginSettingComponent.PluginOpenUI(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.loadMotionCapture)
            {
                motionCaptureComponent.LoadMocapFromSceneToAtom(atom, fileReferenceDict[FileReferenceTypes.scene].currentActionSelectedFile);
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.loadELMotionCapture)
            {
                motionCaptureComponent.LoadMocapFromMocapFile(atom, fileReferenceDict[FileReferenceTypes.mocap].currentActionSelectedFile);
            }
            else if (UIAButtonOpType.IsClothingItemType(buttonOpTypeJSEnum.val))
            {
                FileReference fileRef = null;
                if (clothingComponent.clothingGenderJSE.val == ClothingGenderTypes.femaleClothing) fileRef = fileReferenceDict[FileReferenceTypes.femaleClothingItem];
                else if (clothingComponent.clothingGenderJSE.val == ClothingGenderTypes.maleClothing) fileRef = fileReferenceDict[FileReferenceTypes.maleClothingItem];
                if (fileRef != null)
                {
                    var jsb = AtomUtils.GetClothingActive(atom, fileRef.currentActionSelectedFile);
                    if (jsb != null)
                    {
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.wearClothingItem) jsb.val = true;
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.removeClothingItem) jsb.val = false;
                        if (buttonOpTypeJSEnum.val == UIAButtonOpType.toggleClothingItem) jsb.val = !jsb.val;
                    }
                }

            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.removeAllClothing) ClothingComponent.ClothingActions(atom, ClothingActionMode.remove, false, clothingComponent.onlyRemoveRealClothingJSB.val);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.removeClothingByTag) clothingComponent.RemoveClothingByTag(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.lockActiveClothing) ClothingComponent.LockActiveClothing(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.unlockAllClothing) ClothingComponent.UnlockAllClothing(atom);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.triggerVAMAction) vamTriggerActionComponent.TriggerAction(atom);

            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.undressAllClothing) ClothingComponent.ClothingActions(atom, ClothingActionMode.undress, buttonState == ButtonState.active);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.resetSimAllClothing) ClothingComponent.ClothingActions(atom, ClothingActionMode.resetSim, false);

            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.removeClothingPreset) clothingComponent.ClothingPresetActions(atom, ClothingActionMode.remove, false, fileReferenceDict[FileReferenceTypes.clothingPreset], targetComponent);

            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.mergeClothingPreset) clothingComponent.ClothingPresetActions(atom, ClothingActionMode.merge, buttonState == ButtonState.active, fileReferenceDict[FileReferenceTypes.clothingPreset], targetComponent);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.undressClothingPreset) clothingComponent.ClothingPresetActions(atom, ClothingActionMode.undress, buttonState == ButtonState.active, fileReferenceDict[FileReferenceTypes.clothingPreset], targetComponent);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.resetSimClothingPreset) clothingComponent.ClothingPresetActions(atom, ClothingActionMode.resetSim, false, fileReferenceDict[FileReferenceTypes.clothingPreset], targetComponent);
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.toggleNodePositionState || buttonOpTypeJSEnum.val == UIAButtonOpType.toggleNodeRotationState || buttonOpTypeJSEnum.val == UIAButtonOpType.nodeRotationState || buttonOpTypeJSEnum.val == UIAButtonOpType.nodePositionState)
            {
                if (buttonState == ButtonState.active) nodeControlOffComponent.SetControlStateAction(atom, buttonOpTypeJSEnum.val == UIAButtonOpType.toggleNodeRotationState);
                else nodeControlOnComponent.SetControlStateAction(atom, buttonOpTypeJSEnum.val == UIAButtonOpType.toggleNodeRotationState || buttonOpTypeJSEnum.val == UIAButtonOpType.nodeRotationState);
            }
            else if (buttonOpTypeJSEnum.val == UIAButtonOpType.toggleNodePhysics || buttonOpTypeJSEnum.val == UIAButtonOpType.nodePhysics)
            {
                if (buttonState == ButtonState.active) nodePhysicsOffComponent.SetNodePhysicsAction(atom, nodeSelectionComponent.GetActiveNodeSelections());
                else nodePhysicsOnComponent.SetNodePhysicsAction(atom, nodeSelectionComponent.GetActiveNodeSelections());
            }
            if (revertAtomOn) atom.ToggleOn();
            if (buttonState==ButtonState.active) targetComponent.ResetLastUserChosenAtom();
        }

    }
}

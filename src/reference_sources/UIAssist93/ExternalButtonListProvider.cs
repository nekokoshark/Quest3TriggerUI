using UnityEngine;
using System.Collections.Generic;
using System.Linq;



namespace JayJayWon
{

    public class ExternalButtonProvider
    {
        private static List<ExternalButtonProvider> providers= new List<ExternalButtonProvider>();
        private static Dictionary<MVRScript, ExternalButtonProvider> providersByMVRScript = new Dictionary<MVRScript, ExternalButtonProvider>();

        private static List<ExternalButtonProvider> activeProviders { get { return providers.Where(x => x.alwaysAvailable || x.buttonNameList.Count > 0).ToList(); } }
        public static ExternalButtonProvider currentProvider { get; private set; } = null;

        public static bool available { get { return activeProviders.Count > 0; } }

        public static void RegisterProvider(MVRScript script, Dictionary<string, object> buttonListData)
        {
            UnregisterProvider(script);

            if (buttonListData.ContainsKey("ButtonNameList") && buttonListData.ContainsKey("ButtonPressCallbackJSS"))
            {
                var provider = new ExternalButtonProvider(script, buttonListData);
                providers.Add(provider);
                providersByMVRScript.Add(script, provider);

                if ((currentProvider==null || !activeProviders.Contains(currentProvider)) && activeProviders.Contains(provider)) currentProvider =provider;           
            }

            if (GameControlUI.gameControlDisplayMode == GameControlDisplayModes.externalButtonProviders) GameControlUI.Update();
        }

        public static bool UnregisterProvider(MVRScript script)
        {
            if (providersByMVRScript.ContainsKey(script))
            {
                var provider = providersByMVRScript[script];
                providers.Remove(provider);
                providersByMVRScript.Remove(script);

                if (currentProvider == provider)
                {
                    if (activeProviders.Count>0) currentProvider= activeProviders.First();
                    else currentProvider = null;
                }

                provider.DeregisterExternalButtonProvider();
                return true;
                
            }
            return false;
        }

        public static bool leftProvidersAvailable { get
            {
                if (activeProviders.IndexOf(currentProvider)>0) return true;
                return false;
            } }

        public static bool rightProvidersAvailable
        {
            get
            {
                if (activeProviders.IndexOf(currentProvider) < providers.Count-1 ) return true;
                return false;
            }
        }

        public static void SelectFirstProvider()
        {
            if (activeProviders.Count>0) currentProvider = activeProviders.First();
            else currentProvider=null;
        }

        public static void SelectLastProvider()
        {
            if (activeProviders.Count > 0) currentProvider = activeProviders.Last();
            else currentProvider = null;
        }

        public static void SelectPreviousProvider()
        {
            if (activeProviders.Count > 1 && currentProvider!= activeProviders.First()) currentProvider = activeProviders[activeProviders.IndexOf(currentProvider)-1];
            else currentProvider = activeProviders.FirstOrDefault();
        }

        public static void SelectNextProvider()
        {
            if (activeProviders.Count > 1 && currentProvider != activeProviders.Last()) currentProvider = activeProviders[activeProviders.IndexOf(currentProvider) + 1];
            else currentProvider = activeProviders.LastOrDefault();
        }

        public static List<string> currentOptionNames { get
            {
                if (currentProvider == null) return new List<string>();
                return currentProvider.optionNames;
            } }

        public static string currentOptionNamePin1
        {
            get
            {
                if (currentProvider == null) return null;
                return currentProvider.optionNamePin1;
            }
        }

        public static float currentOptionsScrollBarPosition
        {
            get {
                if (currentProvider ==null) return 1f;
                return currentProvider.scrollBarPosition;
            }
            set { currentProvider.scrollBarPosition = value;}
        }

        public static void GazeOpenActivated()
        {
            if (currentProvider == null) return;
            currentProvider.GazeOpenActivatedInstance();
        }

        public static void GazeCloseActivated()
        {
            if (currentProvider == null) return;
            currentProvider.GazeCloseActivatedInstance();
        }



        private JSONStorableString buttonPressCallbackJSS;
        private JSONStorableAction buttonRefreshRequestCallbackJSA;
        private JSONStorableAction gazeOpenCallbackJSA;
        private JSONStorableAction gazeCloseCallbackJSA;
        private List<string> buttonNameList;
        private float scrollBarPosition = 1f;
        public string optionNamePin1 { get; private set; }
        private Dictionary<string, Color> buttonColorsByName;
        private Dictionary<string, Color> buttonTextColorsByName;
        private JSONStorableBool alwaysAvailableJSB;
        public MVRScript script { get; private set; }
        public string externalProviderName { get; private set; }
        public bool alwaysAvailable { get { return alwaysAvailableJSB.val; } }
        public ExternalButtonProvider(MVRScript script, Dictionary<string, object> buttonListData)
        {
            
            this.script = script;
            ProcessButtonListData(buttonListData);
        }

        public void DeregisterExternalButtonProvider()
        {
            if (alwaysAvailableJSB != null) alwaysAvailableJSB.setCallbackFunction -= AlwaysAvailableCallback;
            script = null;
            buttonRefreshRequestCallbackJSA = null;
            buttonPressCallbackJSS = null;
            buttonNameList = null;
            buttonColorsByName = null;
            buttonTextColorsByName = null;

            optionNamePin1 = null;
        }

        public void SetButtonColors(UIDynamicButton button, string optionName)
        {
            if (buttonColorsByName != null && buttonColorsByName.ContainsKey(optionName))
            {
                button.buttonColor = buttonColorsByName[optionName];
            }
            else button.buttonColor = ActiveClothingEditorDisplay.grey;

            if (buttonTextColorsByName != null && buttonTextColorsByName.ContainsKey(optionName))
            {
                button.textColor = buttonTextColorsByName[optionName];
            }
            else button.textColor = Color.black;
        }


        private void ProcessButtonListData(Dictionary<string, object> buttonListData)
        {
            if (buttonListData.ContainsKey("ButtonPressCallbackJSS")) buttonPressCallbackJSS = buttonListData["ButtonPressCallbackJSS"] as JSONStorableString;
            else buttonPressCallbackJSS = null;      

            if (buttonListData.ContainsKey("ForceButtonListDataRefresh")) buttonRefreshRequestCallbackJSA = buttonListData["ForceButtonListDataRefresh"] as JSONStorableAction;
            else buttonRefreshRequestCallbackJSA = null;

            if (buttonListData.ContainsKey("ButtonNameList")) buttonNameList = buttonListData["ButtonNameList"] as List<string>;
            else buttonNameList = null;

            if (buttonListData.ContainsKey("ButtonListProviderName")) externalProviderName = buttonListData["ButtonListProviderName"] as string;
            else externalProviderName = script.containingAtom.uid +": "+script.storeId;

            if (buttonListData.ContainsKey("ButtonColorsByName")) buttonColorsByName = buttonListData["ButtonColorsByName"] as Dictionary<string, Color>;
            else buttonColorsByName = null;
            if (buttonListData.ContainsKey("ButtonTextColorsByName")) buttonTextColorsByName = buttonListData["ButtonTextColorsByName"] as Dictionary<string, Color>;
            else buttonTextColorsByName = null;
            if (buttonListData.ContainsKey("ButtonNamePin1"))
            {
                optionNamePin1 = buttonListData["ButtonNamePin1"] as string;
                if (!buttonNameList.Contains(optionNamePin1)) optionNamePin1 = null;
            }
            else optionNamePin1 = null;

            if (buttonListData.ContainsKey("GazeOpenCallback")) gazeOpenCallbackJSA = buttonListData["GazeOpenCallback"] as JSONStorableAction;
            else gazeOpenCallbackJSA = null;
            if (buttonListData.ContainsKey("GazeCloseCallback")) gazeCloseCallbackJSA = buttonListData["GazeCloseCallback"] as JSONStorableAction;
            else gazeCloseCallbackJSA = null;

            if (buttonRefreshRequestCallbackJSA!=null) buttonRefreshRequestCallbackJSA.actionCallback = RefreshButtonListData;

            if (alwaysAvailableJSB!=null) alwaysAvailableJSB.setCallbackFunction -= AlwaysAvailableCallback;
            if (buttonListData.ContainsKey("AlwaysAvailable")) alwaysAvailableJSB = buttonListData["AlwaysAvailable"] as JSONStorableBool;
            else alwaysAvailableJSB = new JSONStorableBool("alwaysAvailable", true);
            alwaysAvailableJSB.setCallbackFunction += AlwaysAvailableCallback;
        }

        private void AlwaysAvailableCallback(bool val)
        {
            GameControlUI.RefreshWristUIButtonGrid();
        }

        private void RefreshButtonListData()
        {
            var buttonListData = new Dictionary<string, object>();

            bool refresh = currentProvider == this;
            bool resetScrollBar = false;

            if (script!= null)
            {
                script.SendMessage("OnUIAButtonListDataRequested", buttonListData, SendMessageOptions.DontRequireReceiver);
                ProcessButtonListData(buttonListData);

                if (buttonListData.ContainsKey("ResetScrollBar")) resetScrollBar = (bool) buttonListData["ResetScrollBar"];
                

                if (activeProviders.Count == 0) currentProvider = null;
                else if (!activeProviders.Contains(currentProvider))
                {
                    currentProvider = activeProviders[0];
                    refresh = true;
                }
            }          

            if (refresh && GameControlUI.gameControlDisplayMode == GameControlDisplayModes.externalButtonProviders && GameControlUI.gridsActivated)
            {
                GridsDisplay.uiExternalOptionSelector.DestroyUI();
                if (resetScrollBar) scrollBarPosition = 1f;
                GridsDisplay.uiExternalOptionSelector.CreateUI();
            }
            else if (resetScrollBar) scrollBarPosition = 1f;

            if (GameControlUI.gameControlDisplayMode == GameControlDisplayModes.externalButtonProviders && currentProvider==null)
            {
                GameControlUI.gameControlDisplayMode = GameControlDisplayModes.standard;
                GameControlUI.RefreshWristUIButtonGrid();
            }
/*
            if (SuperController.singleton.isOpenVR && UIAGlobals.vrActive)
            {
                if (VRSettings.vrHandControlJSEnum.val == VRHandControl.leftHand) OVRInput.SetControllerVibration(1f, 1f, OVRInput.Controller.LTouch);
                if (VRSettings.vrHandControlJSEnum.val == VRHandControl.rightHand) OVRInput.SetControllerVibration(1f, 1f, OVRInput.Controller.RTouch);
            }
*/
        }

        public List<string> optionNames { get {
                return new List<string> (buttonNameList); } }


        public void OptionPressed (string optionName)
        {
            if (buttonPressCallbackJSS!=null) buttonPressCallbackJSS.val = optionName;
        }

        public void GazeOpenActivatedInstance()
        {
            if (gazeOpenCallbackJSA != null) gazeOpenCallbackJSA.actionCallback.Invoke();
        }

        public void GazeCloseActivatedInstance()
        {
            if (gazeCloseCallbackJSA != null) gazeCloseCallbackJSA.actionCallback.Invoke();
        }
    }
}

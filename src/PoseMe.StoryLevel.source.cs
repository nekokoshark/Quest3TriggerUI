using System.Collections.Generic;
using System.Linq;
using MacGruber;
using SimpleJSON;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace CheesyFX
{
    public class StoryLevel
    {
	    public IEnumerable<Pose> poses => PoseMe.poses.Where(x => x.id >= minId && x.id <= maxId);
	    private IEnumerable<Pose> complement => PoseMe.poses.Where(x => x.id < minId || x.id > maxId);
	    public int poseCount => maxId - minId + 1;
	    public string currentPoseId
	    {
		    get
		    {
			    if (PoseMe.currentPose == null) return "-";
			    return (PoseMe.currentPose.id - minId).ToString();
		    }
	    }

	    private bool _active;
	    public UIDynamicStoryLevel uid;
	    public RectTransform buttonPanel;
	    public static bool configureUIOpen;
	    public List<StoryButton> buttons = new List<StoryButton>();
	    private JSONStorableFloat buttonCount = new JSONStorableFloat("Button Count", 3f, 1f, 6f);
	    
	    public JSONStorableString name = new JSONStorableString("name", "Level");
	    // private JSONStorableBool showLevelNav = new JSONStorableBool("Show Level Navigation", true);
	    private JSONStorableBool showPoseNav = new JSONStorableBool("Show Pose Navigation", true);
	    private JSONStorableBool showCamNav = new JSONStorableBool("Show Cam Navigation", true);
	    // private static List<object> UIElements = new List<object>();

	    
	    
	    public bool active
	    {
		    get { return _active;}
		    set
		    {
			    if(_active == value) return;
			    _active = value;
			    buttonPanel.gameObject.SetActive(value);
			    if (uid != null)
			    {
				    if (!value)
				    {
					    uid.playButton.image.color = Color.white;
				    }
				    else uid.playButton.image.color = PoseMe.navColor;
			    }

			    Story.currentLevel = null;
		    }
	    }
        private int mode;
        // private int _minId;
        public int minId;

        // public int minId
        // {
	       //  get { return _minId; }
	       //  set
	       //  {
		      //   if(value == minId) return;
		      //   _minId = value;
		      //   Story.levels.OrderBy(x => x._minId);
	       //  }
        // }
        
        public int maxId;

        public static GameObject uidPrefab;

        public StoryLevel()
        {
            name.val = $"Level";
            minId = 0;
            maxId = PoseMe.poses.Count-1;
            var panelGO = new GameObject("StoryButtonPanel");
            panelGO.transform.SetParent(PoseMe.buttonGroup.transform, false);
            buttonPanel = panelGO.AddComponent<RectTransform>();
            buttonPanel.anchorMax = buttonPanel.anchorMin = Vector2.one;
            buttonPanel.offsetMax = buttonPanel.offsetMin = new Vector2(-1000, 0);
            buttonPanel.gameObject.SetActive(false);
            buttons.Add(new StoryButton(this,2));
            buttons.Insert(0, new StoryButton(this,1));
            buttons.Insert(0, new StoryButton(this,0));
            buttons[1].rtMiddle.SetParent(buttons[0].rtMiddle);
            buttons[2].rtMiddle.SetParent(buttons[0].rtMiddle);
            name.setCallbackFunction = val =>
            {
	            var text = buttons[0].SyncText();
	            if(WorldCanvas.levelNav) PoseMe.worldCanvas.SyncLevelNavText(0, text);
            };
            showCamNav.setCallbackFunction += val => buttons[2].rtMiddle.gameObject.SetActive(val);
            showPoseNav.setCallbackFunction += val => buttons[1].rtMiddle.gameObject.SetActive(val);
        }

        public void SyncButtons()
        {
	        bool syncWorld = WorldCanvas.levelNav;
	        for (int i = 0; i < 3; i++)
	        {
		        var text = buttons[i].SyncText();
		        if(syncWorld) PoseMe.worldCanvas.SyncLevelNavText(i, text);
	        }

	        buttons[2].rtMiddle.gameObject.SetActive(PoseMe.screenCanvasActive && PoseMe.currentPose?.camAngles.Count > 1);
        }

        public bool ContainsPose(int id) => id >= minId && id <= maxId;
        public bool ContainsPose(Pose pose) => ContainsPose(pose.id);

        public void First() => PoseMe.poses[minId].Apply();

        public void Next()
        {
	        if(PoseMe.currentPose == null || !ContainsPose(PoseMe.currentPose)) First();
	        else
	        {
		        if(PoseMe.currentPose.id < maxId) PoseMe.poses[PoseMe.currentPose.id + 1].Apply();
		        else
		        {
			        "End of level reached.".Print();
		        }
	        }
        }

        public void Previous()
        {
	        if(PoseMe.currentPose == null || !ContainsPose(PoseMe.currentPose)) First();
	        else
	        {
		        if(PoseMe.currentPose.id > minId) PoseMe.poses[PoseMe.currentPose.id - 1].Apply();
		        else
		        {
			        "Beginning of level reached.".Print();
		        }
	        }
        }

        public void SetPoseRange(int min, int max)
        {
            // Clear();
            minId = min < 0? 0 : min;
            maxId = max >= PoseMe.poses.Count? PoseMe.poses.Count-1 : max;
            buttons[1].SyncText();
        }

        public void SetActive()
        {
	        Story.levels.ForEach(x =>
	        {
		        if (x != this) x.active = false;
	        });
	        active = true;
	        Story.currentLevel = this;
	        if(PoseMe.worldCanvas != null) PoseMe.worldCanvas.SyncLevelNav(this);
	        PoseMe.LayoutPoseButtons(this);
        }

        public JSONClass Store()
        {
            var jc = new JSONClass();
            name.Store(jc);
            jc["active"].AsBool = active;
            jc["minId"].AsInt = minId;
            jc["maxId"].AsInt = maxId;
            return jc;
        }
        
        public void Load(JSONClass jc)
        {
	        name.Load(jc);
	        minId = jc["minId"].AsInt;
	        maxId = jc["maxId"].AsInt;
	        if(jc["active"].AsBool) SetActive();
        }

        public void Destroy()
        {
	        if(buttonPanel) Object.DestroyImmediate(buttonPanel.gameObject);
	        PoseMe.singleton.RemoveUIElement(uid);
        }

        public void CreateConfigureUI()
        {
	        PoseMe.singleton.ClearUI();
	        var button = PoseMe.singleton.CreateButton("Return");
	        button.buttonColor = PoseMe.navColor;
	        button.button.onClick.AddListener(CloseConfigureUI);
	        Utils.SetupTextInput(PoseMe.singleton, "Name", name, true);
	        showCamNav.CreateUI();
	        showPoseNav.CreateUI();
	        // showLevelNav.CreateUI();
        }
        
        public static void CloseConfigureUI()
        {
	        // if(!PoseMe.singleton.UITransform.gameObject.activeSelf) return;
	        configureUIOpen = false;
	        PoseMe.singleton.ClearUI();
	        if (PoseMe.singleton.UITransform.gameObject.activeSelf) PoseMe.singleton.CreateUI();
	        else PoseMe.needsUIRefresh = true;
        }
        
        public void RegisterUid(UIDynamicStoryLevel uid)
        {
	        this.uid = uid;
	        // name.inputField = uid.nameInputField;
	        uid.slider.SetValues(minId, maxId, 0f, PoseMe.poses.Count-1);
	        uid.configureButton.transform.Find("Text").GetComponent<Text>().text = name.val;
        }

        public static void CreateUidPrefab()
        {
            if (uidPrefab == null)
			{
				uidPrefab = new GameObject("StoryLevel");
				uidPrefab.SetActive(false);
				RectTransform rt = uidPrefab.AddComponent<RectTransform>();
				rt.anchorMax = new Vector2(0, 1);
				rt.anchorMin = new Vector2(0, 1);
				rt.offsetMax = new Vector2(535, -500);
				rt.offsetMin = new Vector2(10, -600);
				LayoutElement le = uidPrefab.AddComponent<LayoutElement>();
				le.flexibleWidth = 1;
				le.minHeight = 100;
				le.minWidth = 350;
				le.preferredHeight = 100;
				le.preferredWidth = 500;

				RectTransform backgroundTransform = PoseMe.singleton.manager.configurableScrollablePopupPrefab.transform.Find("Background") as RectTransform;
				backgroundTransform = Object.Instantiate(backgroundTransform, uidPrefab.transform);
				backgroundTransform.name = "Background";
				backgroundTransform.anchorMax = new Vector2(1, 1);
				backgroundTransform.anchorMin = new Vector2(0, 0);
				backgroundTransform.offsetMax = new Vector2(0, 0);
				backgroundTransform.offsetMin = new Vector2(0, -5);

				RectTransform buttonTransform = PoseMe.singleton.manager.configurableScrollablePopupPrefab.transform.Find("Button") as RectTransform;
    //             buttonTransform = Object.Instantiate(buttonTransform, uidPrefab.transform);
    //             buttonTransform.name = "Button";
    //             buttonTransform.anchorMax = new Vector2(1, 1);
    //             buttonTransform.anchorMin = new Vector2(0, 0);
    //             buttonTransform.offsetMax = new Vector2(-50, 0);
    //             buttonTransform.offsetMin = new Vector2(160, 100);
    //             var configureButton = buttonTransform.GetComponent<Button>();
    //             var buttonText = buttonTransform.Find("Text").GetComponent<Text>();

			    var rect = Object.Instantiate(buttonTransform, uidPrefab.transform);
			    rect.name = "Configure Button";
			    rect.anchorMax = new Vector2(1, 1);
			    rect.anchorMin = new Vector2(0, 1);
			    rect.offsetMax = new Vector2(-170, 0);
			    rect.offsetMin = new Vector2(170, -50);
			    var configureButton = rect.GetComponent<Button>();
			    var buttonText = rect.Find("Text").GetComponent<Text>();
			    buttonText.text = "";
			    buttonText.fontSize = 28;
			    // var cb = configureButton.colors;
			    // cb.pressedColor = cb.normalColor;
			    // cb.highlightedColor = cb.normalColor;
			    // configureButton.colors = cb;
			    
			    rect = Object.Instantiate(buttonTransform, uidPrefab.transform);
			    rect.name = "Play Button";
			    rect.anchorMax = new Vector2(0, 1);
			    rect.anchorMin = new Vector2(0, 1);
			    rect.offsetMax = new Vector2(170, 0);
			    rect.offsetMin = new Vector2(120, -50);
			    var playButton = rect.GetComponent<Button>();
			    buttonText = rect.Find("Text").GetComponent<Text>();
			    buttonText.text = "▶";
			    buttonText.fontSize = 28;
			    var cb = playButton.colors;
			    cb.pressedColor = cb.normalColor;
			    cb.highlightedColor = cb.normalColor;
			    playButton.colors = cb;
			    
			    
			    rect = Object.Instantiate(buttonTransform, uidPrefab.transform);
			    rect.name = "Increase Low Button";
			    rect.anchorMax = new Vector2(0, 1);
			    rect.anchorMin = new Vector2(0, 1);
			    rect.offsetMax = new Vector2(50, 0);
			    rect.offsetMin = new Vector2(0, -50);
			    var increaseLowButton = rect.GetComponent<Button>();
			    buttonText = rect.Find("Text").GetComponent<Text>();
			    buttonText.text = "<b>▲</b>";
			    buttonText.fontSize = 28;
			    
			    rect = Object.Instantiate(buttonTransform, uidPrefab.transform);
			    rect.name = "Decrease Low Button";
			    rect.anchorMax = new Vector2(0, 1);
			    rect.anchorMin = new Vector2(0, 1);
			    rect.offsetMax = new Vector2(50, -50);
			    rect.offsetMin = new Vector2(0, -100);
			    var decreaseLowButton = rect.GetComponent<Button>();
			    buttonText = rect.Find("Text").GetComponent<Text>();
			    buttonText.text = "<b>▼</b>";
			    buttonText.fontSize = 28;
			    
			    rect = Object.Instantiate(buttonTransform, uidPrefab.transform);
			    rect.name = "Button";
			    rect.anchorMax = new Vector2(1, 1);
			    rect.anchorMin = new Vector2(1, 1);
			    rect.offsetMax = new Vector2(0, 0);
			    rect.offsetMin = new Vector2(-50, -50);
			    var increaseHighButton = rect.GetComponent<Button>();
			    buttonText = rect.Find("Text").GetComponent<Text>();
			    buttonText.text = "<b>▲</b>";
			    buttonText.fontSize = 28;
			    
			    rect = Object.Instantiate(buttonTransform, uidPrefab.transform);
			    rect.name = "Button";
			    rect.anchorMax = new Vector2(1, 1);
			    rect.anchorMin = new Vector2(1, 1);
			    rect.offsetMax = new Vector2(0, -50);
			    rect.offsetMin = new Vector2(-50, -100);
			    var decreaseHighButton = rect.GetComponent<Button>();
			    buttonText = rect.Find("Text").GetComponent<Text>();
			    buttonText.text = "<b>▼</b>";
			    buttonText.fontSize = 28;
			    // cb = playButton.colors;
			    // cb.pressedColor = cb.normalColor;
			    // cb.highlightedColor = cb.normalColor;
			    // playButton.colors = cb;
                
				var xTR = Object.Instantiate(buttonTransform, uidPrefab.transform);
				xTR.name = "Button";
				xTR.anchorMax = new Vector2(1, 1);
				xTR.anchorMin = new Vector2(1, 0);
				xTR.offsetMax = new Vector2(-120, 0);
				xTR.offsetMin = new Vector2(-170, 50);
				var deleteButton = xTR.GetComponent<Button>();
				buttonText = xTR.Find("Text").GetComponent<Text>();
                buttonText.fontSize = 28;
				buttonText.text = "<b>X</b>";
				buttonTransform.GetComponent<Image>().color = Color.white;
				xTR.GetComponent<Image>().color = PoseMe.severeWarningColor;
                
				RectTransform inputPrefab = PoseMe.singleton.manager.scriptControllerPanelPrefab.transform.Find("NameInputField") as RectTransform;
				var inputTR = Object.Instantiate(inputPrefab, uidPrefab.transform);
				inputTR.name = "Text";
				inputTR.anchorMax = new Vector2(0, 1);
				inputTR.anchorMin = new Vector2(0, 1);
				inputTR.offsetMax = new Vector2(120, 0);
				inputTR.offsetMin = new Vector2(50, -50);
				var lowInputField = inputTR.GetComponentInChildren<InputField>();
				lowInputField.textComponent.text = "0";
				lowInputField.placeholder.GetComponent<Text>().text = "";
				lowInputField.contentType = InputField.ContentType.IntegerNumber;
				lowInputField.textComponent.alignment = TextAnchor.MiddleCenter;

                inputTR = Object.Instantiate(inputPrefab, uidPrefab.transform);
                inputTR.name = "Text";
                inputTR.anchorMax = new Vector2(1, 1);
                inputTR.anchorMin = new Vector2(1, 1);
                inputTR.offsetMax = new Vector2(-50, 0);
                inputTR.offsetMin = new Vector2(-120, -50);
                var highInputField = inputTR.GetComponentInChildren<InputField>();
                highInputField.textComponent.text = "0";
                highInputField.placeholder.GetComponent<Text>().text = "";
                highInputField.contentType = InputField.ContentType.IntegerNumber;
                highInputField.textComponent.alignment = TextAnchor.MiddleCenter;

                var sliderTransform = PoseMe.singleton.manager.configurableSliderPrefab.transform.Find("Slider") as RectTransform;
                sliderTransform = Object.Instantiate(sliderTransform, uidPrefab.transform);
                sliderTransform.name = "Slider";
                sliderTransform.anchorMax = new Vector2(1, 1);
                sliderTransform.anchorMin = new Vector2(0, 1);
                sliderTransform.offsetMax = new Vector2(-60, -50);
                sliderTransform.offsetMin = new Vector2(60, -100);
                // var slider = sliderTransform.GetComponent<Slider>();
                Object.DestroyImmediate(sliderTransform.GetComponent<Slider>());
                var upperHandle = sliderTransform.Find("Handle Slide Area") as RectTransform;
                var fill = sliderTransform.Find("Fill Area/Fill") as RectTransform;
                upperHandle.anchorMax = new Vector2(1f, .5f);
                upperHandle.anchorMin = new Vector2(.5f, .5f);
                upperHandle.pivot = new Vector2(0, 0);
                upperHandle.localScale = new Vector3(1f, 2.5f, 1f);
                var lowerHandle = Object.Instantiate(upperHandle, sliderTransform);
                
				var slider = sliderTransform.gameObject.AddComponent<DualSlider>();
                slider.sliderBounds = sliderTransform;
                slider.minHandle = lowerHandle;
                slider.maxHandle = upperHandle;
                slider.middleGraphic = fill;
                slider.minText = lowInputField;
                slider.maxText = highInputField;
                

                UIDynamicStoryLevel levelUid = uidPrefab.AddComponent<UIDynamicStoryLevel>();
				levelUid.playButton = playButton;
				levelUid.configureButton = configureButton;
                levelUid.deleteButton = deleteButton;
                levelUid.slider = slider;
                levelUid.increaseHighButton = increaseHighButton;
                levelUid.decreaseHighButton = decreaseHighButton;
                levelUid.increaseLowButton = increaseLowButton;
                levelUid.decreaseLowButton = decreaseLowButton;
                levelUid.highInputField = highInputField;
                levelUid.lowInputField = lowInputField;

                // levelUid.increaseHighButton.onClick.AddListener(() =>
                // {
                //  if(slider.maxValue == slider.maxLimit) return;
                //  slider.SetValues(slider.minValue, slider.maxValue + 1f);
                // });
			}
        }

        private RectTransform CreateButton(int id)
        {
	        var rt = Object.Instantiate(PoseMe.singleton.manager.configurableButtonPrefab, buttonPanel, false) as RectTransform;
	        rt.anchorMax = Vector2.one;
	        rt.anchorMin = Vector2.one;
	        rt.offsetMax = new Vector2(0, 0);
	        rt.offsetMin = new Vector2(-200,-200);

	        var button = rt.GetComponent<UIDynamicButton>();
	        button.label = "«»";//‹ › » «
	        button.buttonText.fontSize = 160;
	        button.buttonText.color = Color.white;
	        button.button.image.color = Color.clear;
	        button.buttonText.alignment = TextAnchor.MiddleCenter;
	        // uiButton.button.onClick.AddListener(ApplyWaitForDoubleClick);
	        var cb = button.button.colors;
	        cb.highlightedColor = cb.normalColor;
	        cb.pressedColor = Color.cyan;
	        button.button.colors = cb;
	        return rt;
        }
    }
}
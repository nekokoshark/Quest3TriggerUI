using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MacGruber;
using MeshVR;
using MVR.FileManagementSecure;
using SimpleJSON;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Button = UnityEngine.UI.Button;
using Image = UnityEngine.UI.Image;
using Object = UnityEngine.Object;

namespace CheesyFX
{
    public class PoseMe : MVRScript
    {
        public static PoseMe singleton;
        public static bool initialPoseLoaded;
        public static string libraryPath = $"Saves/PluginData/CheesyFX/BodyLanguage/PoseMe/Library/";
        public static string backUpStorePath = $"Saves/PluginData/CheesyFX/BodyLanguage/PoseMe/Backups/";
        public static string tmpPath = "Saves/PluginData/CheesyFX/BodyLanguage/PoseMe/tmp/";
        public static Pose currentPose;
        public static Gaze gaze;
        public static JSONClass poseCache;
        // private static string scenename;

        // public static JSONStorableBool manageForces = new JSONStorableBool("Store & Restore Forces", true);
        public static JSONStorableBool restoreRoot = new JSONStorableBool("Restore Root", true);
        public static JSONStorableBool restoreHeadRotation = new JSONStorableBool("Restore Head Rotation", true);
        public static JSONStorableBool applyIdles = new JSONStorableBool("Apply Idles (Global)", false, val =>
        {
            if(currentPose == null) return;
            currentPose.poseIdle.enabled = val && currentPose.poseIdle.applyIdles.val;
        });
        public static JSONStorableBool copyIdlesToNewPose = new JSONStorableBool("Copy Idles To New Pose", true);
        public static JSONStorableBool copySlapsToNewPose = new JSONStorableBool("Copy Slaps To New Pose", true);

        public static bool applyingHandjobPoseLeft;
        public static bool applyingHandjobPoseRight;
        
        private static JSONStorableBool printInputConfigOnFirstHover =
            new JSONStorableBool("Print Input Config On First Hover", true);
        private static JSONStorableBool disableNativeShortcuts =
            new JSONStorableBool("Disable Native Shortcuts", false);
        public static JSONStorableStringChooser canvasChooser = new JSONStorableStringChooser("EditCanvas", new List<string>{"Screen (Desktop)", "HUD (VR)", "World"}, "Screen (Desktop)", "Edit Canvas", SelectCanvas);
        public static JSONStorableBool worldButtonsFacePlayer = new JSONStorableBool("World Canvas Face Player", false, (bool val) => SyncWorldCanvasRotation(worldCanvasRotation.val));
        
        public static JSONStorableStringChooser leftClickChooser =
            new JSONStorableStringChooser("leftClickChooser", new List<string>{"Apply Pose", "Next Cam", "Previous Cam", "Random Cam", "None"}, "Next Cam", "Left Click", val => SyncInput(val, ref onLeftClick));
        public static JSONStorableStringChooser rightClickChooser =
            new JSONStorableStringChooser("rightClickChooser", new List<string>{"Apply Pose", "Next Cam", "Previous Cam", "Random Cam", "None"}, "Previous Cam", "Right Click", val => SyncInput(val, ref onRightClick));
        public static JSONStorableStringChooser middleClickChooser =
            new JSONStorableStringChooser("middleClickChooser", new List<string>{"Apply Pose", "Next Cam", "Previous Cam", "Random Cam", "None"}, "Apply Pose", "Middle Click", val => SyncInput(val, ref onMiddleClick));
        public static JSONStorableStringChooser dragUpChooser =
            new JSONStorableStringChooser("dragUpChooser", new List<string>{"Apply Pose", "Next Cam", "Previous Cam", "Random Cam", "None"}, "Previous Cam", "Drag Up", val => SyncInput(val, ref onDragUp));
        public static JSONStorableStringChooser dragDownChooser =
            new JSONStorableStringChooser("dragDownChooser", new List<string>{"Apply Pose", "Next Cam", "Previous Cam", "Random Cam", "None"}, "Next Cam", "Drag Down", val => SyncInput(val, ref onDragDown));
        public static JSONStorableStringChooser dragLeftChooser =
            new JSONStorableStringChooser("dragLeftChooser", new List<string>{"Apply Pose", "Next Cam", "Previous Cam", "Random Cam", "None"}, "Random Cam", "Drag Left", val => SyncInput(val, ref onDragLeft));
        public static JSONStorableStringChooser dragRightChooser =
            new JSONStorableStringChooser("drarRightChooser", new List<string>{"Apply Pose", "Next Cam", "Previous Cam", "Random Cam", "None"}, "Random Cam", "Drag Right", val => SyncInput(val, ref onDragRight));
        
        public static JSONStorableStringChooser camMode = new JSONStorableStringChooser("camMode", new List<string>{"None", "Bezier", "Exponential", "Linear", "Snap"}, "Bezier", "Cam Mode");
        public static JSONStorableFloat smoothCamSpeedJ = new JSONStorableFloat("Smooth Cam Speed", 1f, .2f, 4f, true);
        public static JSONStorableFloat smoothCamBezierStrength = new JSONStorableFloat("Smooth Cam Bezier Strength", 1f, 0f, 5f, false);
        // public static JSONStorableFloat doubleClickTimeout = new JSONStorableFloat("Double Click Timeout", .3f, 0f, 1f, true);
        
        private static JSONStorableBool autoBackUpDeleted = new JSONStorableBool("Auto BackUp Deleted", false);
        public static JSONStorableBool showScreeenAndHUDCanvas = new JSONStorableBool("Show Screen/HUD Canvas", true, val => buttonGroup.gameObject.SetActive(val));
        public static JSONStorableBool showScreenButtons = new JSONStorableBool("showScreenButtons", true);
        public static JSONStorableBool showWorldButtons = new JSONStorableBool("showWorldButtons", true);
        public static JSONStorableBool worldLevelNavOnTop = new JSONStorableBool("World Level Navigation On Top", true);
        public static JSONStorableBool hudLevelNavOnTop = new JSONStorableBool("HUD Level Navigation On Top", true);
        public static JSONStorableBool showThumbnails = new JSONStorableBool("Show Pose Thumbnails", true, OnShowThumbnailChanged);
        public static MyUIDynamicToggle poseApplyIdlesUid;

        public static JSONStorableBool cinematicEnabled = new JSONStorableBool("Cinematic Enabled", false);
        public static int cinematicCamMode = 0;
        private static int cinematicPoseMode = 0;
        public static JSONStorableStringChooser cinematicCamModeChooser =
            new JSONStorableStringChooser("CinematicCamMode", new List<string>{"None", "Cycle Cams", "Shuffle Cams"}, "None", "Cinematic Cam", val => cinematicCamMode = cinematicCamModeChooser.choices.IndexOf(val));
        public static JSONStorableStringChooser cinematicPoseModeChooser =
            new JSONStorableStringChooser("CinematicPoseMode", new List<string>{"None", "Cycle Poses", "Shuffle Poses"}, "None", "Cinematic Pose", val => cinematicPoseMode = cinematicPoseModeChooser.choices.IndexOf(val));
        private static JSONStorableFloat poseTimeMean = new JSONStorableFloat("Pose Time Mean", 30f, 1f, 30f);
        private static JSONStorableFloat poseTimeDelta = new JSONStorableFloat("Pose Time Delta", 10f, 0f, 30f);
        private static JSONStorableFloat camTimeMean = new JSONStorableFloat("Cam Time Mean", 10f, 2f, 30f);
        private static JSONStorableFloat camTimeDelta = new JSONStorableFloat("Cam Time Delta", 5f, 0f, 30f);
        private static JSONStorableFloat camSpeedMean = new JSONStorableFloat("Cam Speed Mean", .5f, .3f, 2f);
        private static JSONStorableFloat camSpeedDelta = new JSONStorableFloat("Cam Speed Delta", .3f, 0f, 1f);
        public static JSONStorableBool randomizeCamSpeed = new JSONStorableBool("Random Cam Speed", true);

        // public static JSONStorableFloat bubbleLifeTime = new JSONStorableFloat("Speech Bubble Life Time", 5f, 1f, 20f);

        public static JSONStorableBool restoreOtherPersons = new JSONStorableBool("Restore Other Persons", true);
        public static JSONStorableBool restoreDildos = new JSONStorableBool("Restore Dildos", true);
        public static JSONStorableBool restoreToys = new JSONStorableBool("Restore BP & AH", true);

        public static JSONStorableBool fistCamOnPoseSwitch = new JSONStorableBool("First Cam On Pose Switch", false);
        
        public static List<Pose> poses = new List<Pose>();

        public static JSONStorableStringChooser poseChooser =
            new JSONStorableStringChooser("Apply Pose", new List<string>(), "", "Pose", ApplyPose);
        public static JSONStorableStringChooser camChooser =
            new JSONStorableStringChooser("Apply Angle", new List<string>{"0"}, "0", "Cam", ApplyCam);
        private static JSONStorableAction reapplyCurrent;
        private static JSONStorableAction nextPose;
        private static JSONStorableAction previousPose;
        private static JSONStorableAction randomPose;
        private static JSONStorableAction previousCam;
        private static JSONStorableAction nextCam;
        private static JSONStorableAction randomCam;
        private static JSONStorableAction printInputConfig;
        private JSONStorableAction invokeSlap = new JSONStorableAction("Invoke Slap (if current pose has at least 1 slap)", () => currentPose?.InvokeRandomSlapAction(1));
        private JSONStorableAction invokeBackslap = new JSONStorableAction("Invoke Backslap (if current pose has at least 1 slap)", () => currentPose?.InvokeRandomSlapAction(2));
        private JSONStorableAction invokePush = new JSONStorableAction("Invoke Push (if current pose has at least 1 slap)", () => currentPose?.InvokeRandomSlapAction(-1));
        private JSONStorableAction invokeRandom = new JSONStorableAction("Invoke Random Event (if current pose has at least 1 slap)", () => currentPose?.InvokeRandomSlapAction(0));

        private Transform confirmCanvas;
        public static List<Atom> dildos = new List<Atom>();

        public static string sceneType;

        public static IEnumerator applyPosePost;
        public static List<int> softPhysicsStates = new List<int>();
        public static List<Person> persons => FillMeUp.persons;
        private UnityEventsListener uiListener;

        private float screenWidth;
        private float screenHeight;

        private static UIDynamicLabelInput sceneNameInput;

        private static CanvasSettings[] canvasSettings =
        {
            new CanvasSettings("screenCanvas"),
            new CanvasSettings("hudCanvas"),
            new WorldCanvas("worldCanvas")
        };

        public static WorldCanvas worldCanvas;
        public static bool screenCanvasActive = true;
        public static JSONStorableBool keepHudCanvasWhenUIClosed = new JSONStorableBool("Keep HUD Canvas Open", true);

        public static int currentCanvas;

        private IOSystem ioSystem = new IOSystem();
        public static Atom atom;

        public static UIDynamicButton onPoseEnterTriggerButton;
        public static UIDynamicButton onPoseExitTriggerButton;
        public static UIDynamicButton onCamEnterTriggerButton;
        public static UIDynamicButton onCamExitTriggerButton;

        // public static MyUIDynamicToggle poseUseBubblePoolUid;
        // public static MyUIDynamicToggle camUseBubblePoolUid;
        
        public static GameObject buttonGroup;
        private JSONStorableFloat posX = new JSONStorableFloat("Pos X", 1f, 0f, 1f);
        private JSONStorableFloat posY = new JSONStorableFloat("Pos Y", 1f, 0f, 1f);
        public static JSONStorableFloat buttonSizeJ = new JSONStorableFloat("Button Size", 200f, SyncButtonSize,0f, 1000f);
        public static JSONStorableFloat buttonTransparency = new JSONStorableFloat("Button Transparency", .5f, OnButtonTransparencyChanged, 0f, 1f);
        private static float buttonSize;
        public static JSONStorableFloat maxRows = new JSONStorableFloat("MaxRows", 20f, (float val) => OnButtonSettingsChanged(), 1f, 50f, true);
        public static JSONStorableFloat buttonSpacing = new JSONStorableFloat("Button Spacing", 0f, (float val) => OnButtonSettingsChanged(), 0f, 100f);

        public static JSONStorableBool showWorldCanvas = new JSONStorableBool("Show World Canvas", false);
        public static JSONStorableBool worldCanvasInFront = new JSONStorableBool("World Canvas In Front", false);
        
        public static Image UIImage;
        public static UIDynamicButton UIImageButton;
        public static JSONStorableString actorsJ = new JSONStorableString("actors", "");

        public static List<Rigidbody> forceTargets = new List<Rigidbody>();

        public static Transform cameraRig;
        public static Transform camera;
        public static bool isVR;

        public static List<object> UIElements = new List<object>();
        public static UIDynamicTabBar tabbar;
        public static int currentTab;
        
        public static Dictionary<Atom, MyUIDynamicToggle> actorTogglesUid = new Dictionary<Atom, MyUIDynamicToggle>();
        public static IEnumerable<Atom> actors => persons.Select(x => x.atom).Concat(dildos).Concat(toys);

        public static bool buttonHovered;

        public static Color navColor = new Color(0.55f, 0.90f, 1f);
        public static Color warningColor = new Color(1f, 0.60f, 0.60f);
        public static Color severeWarningColor = new Color(1f, 0.13f, 0.09f);
        
        private static MyUIDynamicToggle poseCumMalesToggle;
        private static MyUIDynamicToggle poseCumFemaleToggle;
        private static MyUIDynamicToggle camCumMalesToggle;
        private static MyUIDynamicToggle camCumFemaleToggle;
        private static MyUIDynamicToggle disableAnatomyToggle;
        
        public static JSONStorableFloat dialogPoolLevelPose = new JSONStorableFloat("Pose Dialog Pool Level", 0f, val => currentPose.dialogPoolLevel.val = val,-1f, 2f);
        public static JSONStorableFloat dialogPoolLevelCam = new JSONStorableFloat("Cam Dialog Pool Level", 0f, val => currentPose.currentCam.dialogPoolLevel.val = val, -1f, 2f);
        
        public static JSONStorableFloat dialogColorGain = new JSONStorableFloat("Color Gain", 10f, 1f, 50f);

        // private static JSONStorableString dialogInfo = new JSONStorableString("dialogInfo",
        private JSONStorableString shortCutInfo = new JSONStorableString("", 
            "RightCtrl+p: Add pose\nRightCtrl+u: Update pose\nAlt+g: Temp disable gaze (not stored)\nAlt+x: Toggle X-Ray");
        private string dialogInfo =
            "Each pose or cam can have specific dialogs:\n" +
            "Add and manage them in this window. These are only played if the 'Dialog Pool Level' of the pose or cam is -1.\n" +
            "If two or more dialogs have the same mean delay they will form a group. Upon invocation, a random member of each dialog group is played.\n" +
            "Furthermore, you can add generalized dialogs to the different levels of the 'Dialog Pool'. If e.g. the Dialog Pool Level of a pose is 2 and Lvl2 of the pool contains dialogs, one of those is played on a random basis.\n" +
            "Poses and cams make use of the same Dialog Pool, but you can add as many levels as you like and make e.g. Lvl3 to Lvl5 exclusive to cams. Same goes if you want a separate pool of thought bubbles.\n" +
            "For further information please contact your system administrator.";
            // );

        public static bool needsUIRefresh;
        
        public static JSONStorableBool disableAnatomy = new JSONStorableBool("Disable Anatomy", false);
        public static MVRScript timeline;
        public static JSONStorableAction.ActionCallback timelineStop;
        public static JSONStorableBool timelineLock;
        private static JSONStorableStringChooser poseTimelineOnEnter =
            new JSONStorableStringChooser("PoseTimelineClip", null, null, "Timeline Clip");
        private static JSONStorableStringChooser camTimelineOnEnter =
            new JSONStorableStringChooser("CamTimelineClip", null, null, "Timeline Clip");

        private UnityEventsListener worldUIListener;
        
        private static WaitForFixedUpdate waitForFixedUpdate = new WaitForFixedUpdate();
        
        private void OnEnable()
        {
            if(buttonGroup != null) buttonGroup.gameObject.SetActive(true);
            if(currentPose != null) currentPose.selected = true;
        }

        public override void Init()
        {
            try
            {
                // $"{name} {FillMeUp.abort}".Print();
                if (FillMeUp.abort) return;
                singleton = this;
                atom = containingAtom;
                worldCanvas = (WorldCanvas)canvasSettings[2];
                canvasSettings[0].maxRows.val = 7f;
                canvasSettings[1].maxRows.val = 7f;
                SetCameraRig();
                gaze = FillMeUp.containingPerson.gaze = new Gaze(atom);
                FillMeUp.containingPerson.penisGazeTarget = Gaze.RegisterPerson(FillMeUp.containingPerson, gaze);
                // scenename = GetSceneName();
                UIManager.SetScript(this, CreateUIElement, leftUIElements, rightUIElements);
                Utils.OnInitUI(CreateUIElement);
                // FileManagerSecure.CreateDirectory(managedStorePath);
                FileManagerSecure.CreateDirectory(libraryPath);
                FileManagerSecure.CreateDirectory(backUpStorePath);
                FileManagerSecure.CreateDirectory(tmpPath);
                
                sceneType = GetSceneContent(true);

                posX.setCallbackFunction += val => PositionCanvas(posX.val, posY.val);
                posY.setCallbackFunction += val => PositionCanvas(posX.val, posY.val);

                disableAnatomy.setCallbackFunction += val =>
                {
                    foreach (var person in FillMeUp.persons)
                    {
                        if (person.dcs.gender == DAZCharacterSelector.Gender.Male)
                        {
                            person.disableAnatomy.val = val;
                        }
                    }
                };
                
                
                if (isVR) camMode.val = "Snap";
                // SuperController.singleton.foc
                // SuperController.singleton.FocusOnController(containingAtom.mainController);
                
                SuperController.singleton.onAtomAddedHandlers += OnAtomAdded;
                SuperController.singleton.onAtomRemovedHandlers += OnAtomRemoved;
                SuperController.singleton.onAtomUIDRenameHandlers += OnAtomRename;

                screenWidth = Screen.width;
                screenHeight = Screen.height;
                InitCanvas();

                ioSystem.saveDir = libraryPath;
                // ioSystem.defaultName = $"/{sceneType}/{scenename}/MyPose";
                ioSystem.store = () =>
                {
                    currentPose.UpdateJSON();
                    return currentPose.pose;
                };
                ioSystem.onSaved += val =>
                {
                    currentPose.StoreImage(val);
                };
                ioSystem.onLoaded += (path, jc) =>
                {
                    LoadPoseFromJSON(jc);
                };
                

                reapplyCurrent = new JSONStorableAction("Reapply Current Pose", () => currentPose.Apply());
                nextPose = new JSONStorableAction("Next Pose", () =>
                {
                    var pose = poses.FirstOrDefault(x => x.id > currentPose.id);
                    pose?.Apply();
                });
                previousPose = new JSONStorableAction("Previous Pose", () => {
                    var pose = poses.LastOrDefault(x => x.id < currentPose.id);
                    pose?.Apply();
                });
                randomPose = new JSONStorableAction("Random Pose", () =>
                {
                    poses.TakeRandom(currentPose.id).Apply();
                });
                nextCam = new JSONStorableAction("Next Cam", () =>
                {
                    currentPose.ApplyNextCamAngle();
                });
                previousCam = new JSONStorableAction("Previous Cam", () =>
                {
                    currentPose.ApplyPreviousCamAngle();
                });
                randomCam = new JSONStorableAction("Random Cam", () =>
                {
                    currentPose.ApplyRandomCamAngle();
                });
                printInputConfig = new JSONStorableAction("Print Input Config", PrintInputConfig);
                worldCanvasInFront.setCallbackFunction += val => worldCanvas?.SetInFront(val);
                showWorldCanvas.setCallbackFunction += worldCanvas.SetActive;
                worldLevelNavOnTop.setCallbackFunction += val => worldCanvas?.LayoutButtons(Story.currentLevel);
                hudLevelNavOnTop.setCallbackFunction += val => LayoutPoseButtons(Story.currentLevel);

                worldUIListener = SuperController.singleton.worldUI.gameObject.AddComponent<UnityEventsListener>();
                worldUIListener.onEnabled.AddListener(() =>
                {
                    buttonGroup.gameObject.SetActive(false);
                });
                worldUIListener.onDisabled.AddListener(() =>
                {
                    buttonGroup.gameObject.SetActive(true);
                });
                

                poseTimelineOnEnter.setCallbackFunction += val =>
                {
                    if(currentPose == null) return;
                    if (val == "")
                    {
                        currentPose.timelineClip = null;
                        return;
                    }
                    if (GetTimeline())
                    {
                        currentPose.timelineClip = timeline.GetAction(val);
                        currentPose.timelineClip.actionCallback.Invoke();
                    }
                };
                camTimelineOnEnter.setCallbackFunction += val =>
                {
                    if(currentPose.currentCam == null) return;
                    if (val == "")
                    {
                        currentPose.currentCam.timelineClip = null;
                        return;
                    }
                    if (GetTimeline())
                    {
                        currentPose.currentCam.timelineClip = timeline.GetAction(val);
                        currentPose.currentCam.timelineClip.actionCallback.Invoke();
                    }
                };
                

                foreach (var rb in atom.rigidbodies)
                {
                    if(
                        rb.name == "hip" || 
                       rb.name == "chest" || 
                       rb.name == "head" || 
                       rb.name == "lThigh" || 
                       rb.name == "rThigh" ||
                       rb.name == "lShin" || 
                       rb.name == "rShin" ||
                       rb.name == "lShldr" ||
                       rb.name == "rShldr" ||
                       rb.name == "lHand" ||
                       rb.name == "rHand" ||
                       rb.name == "lForeArm" ||
                       rb.name == "rForeArm" ||
                       rb.name == "lFoot" ||
                       rb.name == "rFoot"
                       ) 
                        forceTargets.Add(rb);
                }

                var rBody = forceTargets.First(x => x.name == "head");
                forceTargets.Remove(rBody);
                forceTargets.Add(rBody);
                rBody = forceTargets.First(x => x.name == "chest");
                forceTargets.Remove(rBody);
                forceTargets.Add(rBody);
                rBody = forceTargets.First(x => x.name == "hip");
                forceTargets.Remove(rBody);
                forceTargets.Add(rBody);
                rBody = forceTargets.First(x => x.name == "lShldr");
                forceTargets.Remove(rBody);
                forceTargets.Add(rBody);
                rBody = forceTargets.First(x => x.name == "rShldr");
                forceTargets.Remove(rBody);
                forceTargets.Add(rBody);
                rBody = forceTargets.First(x => x.name == "lForeArm");
                forceTargets.Remove(rBody);
                forceTargets.Add(rBody);
                rBody = forceTargets.First(x => x.name == "rForeArm");
                forceTargets.Remove(rBody);
                forceTargets.Add(rBody);
                rBody = forceTargets.First(x => x.name == "lHand");
                forceTargets.Remove(rBody);
                forceTargets.Add(rBody);
                rBody = forceTargets.First(x => x.name == "rHand");
                forceTargets.Remove(rBody);
                forceTargets.Add(rBody);
                rBody = forceTargets.First(x => x.name == "lThigh");
                forceTargets.Remove(rBody);
                forceTargets.Add(rBody);
                rBody = forceTargets.First(x => x.name == "rThigh");
                forceTargets.Remove(rBody);
                forceTargets.Add(rBody);
                rBody = forceTargets.First(x => x.name == "lShin");
                forceTargets.Remove(rBody);
                forceTargets.Add(rBody);
                rBody = forceTargets.First(x => x.name == "rShin");
                forceTargets.Remove(rBody);
                forceTargets.Add(rBody);
                rBody = forceTargets.First(x => x.name == "lFoot");
                forceTargets.Remove(rBody);
                forceTargets.Add(rBody);
                rBody = forceTargets.First(x => x.name == "rFoot");
                forceTargets.Remove(rBody);
                forceTargets.Add(rBody);
                
                
                // GetNeighbors();
                // neighbors[atom.rigidbodies.First(x => x.name == "hip")].ToList().ForEach(x => x.name.Print());
                
                // forceTargets.ForEach(x => x.Print());
                poseChooser.RegisterNonRestore();
                camChooser.RegisterNonRestore();

                camMode.RegisterNonRestore();
                cinematicCamModeChooser.RegisterNonRestore();
                cinematicPoseModeChooser.RegisterNonRestore();
                
                reapplyCurrent.RegisterWithKeybingings(keyBindings);
                nextCam.RegisterWithKeybingings(keyBindings);
                previousCam.RegisterWithKeybingings(keyBindings);
                randomCam.RegisterWithKeybingings(keyBindings);
                nextPose.RegisterWithKeybingings(keyBindings);
                previousPose.RegisterWithKeybingings(keyBindings);
                randomPose.RegisterWithKeybingings(keyBindings);
                printInputConfig.RegisterWithKeybingings(keyBindings);
                invokeRandom.RegisterWithKeybingings(keyBindings);
                invokeSlap.RegisterWithKeybingings(keyBindings);
                invokeBackslap.RegisterWithKeybingings(keyBindings);
                invokePush.RegisterWithKeybingings(keyBindings);
                
                cinematicEnabled.RegisterWithKeybingings(keyBindings);
                applyIdles.RegisterWithKeybingings(keyBindings);
                restoreRoot.RegisterWithKeybingings(keyBindings);
                restoreHeadRotation.RegisterWithKeybingings(keyBindings);
                restoreOtherPersons.RegisterWithKeybingings(keyBindings);
                restoreDildos.RegisterWithKeybingings(keyBindings);
                restoreToys.RegisterWithKeybingings(keyBindings);
                showScreeenAndHUDCanvas.RegisterWithKeybingings(keyBindings);
                showThumbnails.RegisterWithKeybingings(keyBindings);
                showWorldCanvas.RegisterWithKeybingings(keyBindings);
                
                Gaze.tempDisable.RegisterWithKeybingings(keyBindings);
                Gaze.focus.RegisterWithKeybingings(keyBindings);
                RegisterStringChooser(Gaze.gazeSettings.focusTargetChooser);
                RegisterFloat(Gaze.gazeSettings.focusDuration);

                PoseIdle.InitPresetSystem();
                PoseExtractor.Init();
                Dialog.InitLoadURL();
                DialogPool.Init();
                Story.Init();
                CreateUI();
                GetTimeline();
                DeferredLoadFromScene().Start();


                // SuperController.singleton.fixedMonitorUIScale
                // gameObject.AddComponent<EllipticalForce>().Init();


                // var uib = SuperController.singleton.GetAtomByUid("UIButton");
                //
                // var ca = uib.GetComponentInChildren<Canvas>();
                // ca.renderOrder.Print();
                // ca.gameObject.layer.Print();
                // ca.renderMode.Print();
                // foreach (Transform c in uib.transform.GetAllChildren())
                // {
                //     $"{c.name} {c.gameObject.layer}".Print();
                // }

                // Story.AddLevel();

                // var hc = SuperController.singleton.GetAtomByUid("Person#2").rigidbodies.First(x => x.name == "hip");
                // hc.NullCheck();
                // cj = hc.GetComponent<ConfigurableJoint>();
                // cj.targetRotation.eulerAngles.Print();

                // UnityWebRequest request = new UnityWebRequest();
                // Network.Connect("127.0.0.1", 25000);
                // (choices == poseChooser.choices).Print();

                // atom.rigidbodies.First(x => x.name == "lMid2").GetComponent<DAZBone>().baseJointRotation = new Vector3(0f,0f,0f);
                // cameraRig.rotation = Quaternion.identity;
                // cameraRig.position = Vector3.zero;
                // camera.localPosition = new Vector3(0f, camera.localPosition.y, 0f);
                // camera.localRotation = Quaternion.identity;

                // test1 = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                // test2 = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                // test3 = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                // test1.GetComponent<Collider>().enabled = false;
                // test2.GetComponent<Collider>().enabled = false;
                // test3.GetComponent<Collider>().enabled = false;
                // test1.transform.localScale = test2.transform.localScale = test3.transform.localScale = .05f * Vector3.one;
                // test1.GetComponent<Renderer>().material.shader = test2.GetComponent<Renderer>().material.shader = test3.GetComponent<Renderer>().material.shader = FillMeUp.debugShader;
                // test1.GetComponent<Renderer>().material.color = test3.GetComponent<Renderer>().material.color = Color.magenta;
                // test2.GetComponent<Renderer>().material.color = Color.green;
                // speechBubbleControl = atom.GetStorableByID("SpeechBubble") as SpeechBubbleControl;
                // thoughtBubbleControl = atom.GetStorableByID("ThoughtBubble") as SpeechBubbleControl;
                // speechBubbleControl.UpdateText("hah?", 3f);
                // thoughtBubbleControl.UpdateText("huh?", 3f);
            }
            catch (Exception e)
            {
                SuperController.LogError(e.ToString());
            }
        }

        public static bool GetTimeline()
        {
            if (timeline) return true;
            timeline = singleton.gameObject.transform.parent
                .GetComponentsInChildren<MVRScript>().FirstOrDefault(x => x.name.EndsWith("VamTimeline.AtomPlugin"));
            if(!timeline) return false;
            timelineStop = timeline.GetAction("Stop").actionCallback;
            timelineLock = timeline.GetBoolJSONParam("Locked");
            return true;
        }

        private void SyncTimeline()
        {
            if (!GetTimeline()) return;
            var choices = new List<string>{""}; 
            choices.AddRange(timeline.GetActionNames());
            poseTimelineOnEnter.SetChoices(choices);
            camTimelineOnEnter.SetChoices(choices);
        }

        public static List<object> keyBindings = new List<object>();
        public void OnBindingsListRequested(List<object> bindings)
        {
            bindings.Add(new Dictionary<string, string>
            {
                { "CheesyFX", "PoseMe" }
            });

            bindings.Add(new JSONStorableAction("Toggle Pose Buttons", TogglePoseButtons));
            
            bindings.AddRange(keyBindings);
        }

        public void CreateUI()
        {
            tabbar = UIManager.CreateTabBar(new[] { "Pose", "Dialogs", "Actions", "Movement","Gaze", "Levels", "Cinematic", "Extract", "General", "Input" }, SelectTab, 5);
            tabbar.SelectTab(currentTab);

        }

        private void SelectTab(int id)
        {
            currentTab = id;
            RemoveUIElements(UIElements);
            RemoveUIElements(poseBubbleItems);
            RemoveUIElements(camBubbleItems);
            RemoveUIElements(Gaze.UIElements);
            UIElements.Clear();
            Gaze.SetDebugMode(false);
            
            if(id == 0) CreatePoseUI();
            else if (id == 1) CreateDialogsUI();
            else if (id == 2) CreateActionsUI();
            else if (id == 3) CreateMovementUI();
            else if (id == 4) Gaze.CreateUI();
            else if (id == 5)
            {
                if (poses.Count == 0 || currentPose == null)
                {
                    UIElements.Add(CreateTextField(new JSONStorableString("bla", "Create or select a pose first.")));
                }
                else Story.CreateUI();
            }
            else if (id == 6) CreateCinematicUI();
            else if (id == 7) PoseExtractor.CreateUI();
            else if (id == 8) CreateGeneralUI();
            else if (id == 9) CreateInputUI();
        }

        private void CreateCinematicUI()
        {
            cinematicPoseModeChooser.CreateUI(UIElements, false, chooserType:0);
            cinematicCamModeChooser.CreateUI(UIElements, true, chooserType:0);
            camTimeMean.CreateUI(UIElements);
            camTimeDelta.CreateUI(UIElements, true);
            poseTimeMean.CreateUI(UIElements);
            poseTimeDelta.CreateUI(UIElements, true);
            camSpeedMean.CreateUI(UIElements);
            camSpeedDelta.CreateUI(UIElements, true);
            
            randomizeCamSpeed.CreateUI(UIElements, true);
            cinematicEnabled.CreateUI(UIElements);
        }

        public static MyJSONStorableVector3 worldCanvasPosition =
            new MyJSONStorableVector3("WorldCanvasPos", Vector3.zero, -2 * Vector3.one, 2 * Vector3.one, val =>
            {
                WorldCanvas.go.transform.position = val;
            });

        public static MyJSONStorableVector3 worldCanvasRotation =
            new MyJSONStorableVector3("WorldCanvasRot", Vector3.zero, -180 * Vector3.one, 180 * Vector3.one, SyncWorldCanvasRotation);

        private static void SyncWorldCanvasRotation(Vector3 val)
        {
            val.y += 180f;
            WorldCanvas.go.transform.eulerAngles = val;
        }

        private static UIDynamicV3Slider worldCanvasPosSlider;
        private static UIDynamicV3Slider worldCanvasRotSlider;
        private static UIDynamicToggle worldCanvasFacePlayerToggle;

        private void CreateInputUI()
        {
            UIElements.Add(this.SetupButton("Configure Button Interaction", false, CreateButtonInteractionUINewPage));
            canvasChooser.CreateUI(UIElements, true);
            showWorldCanvas.CreateUI(UIElements, true);

            showScreeenAndHUDCanvas.CreateUI(UIElements, true);
            showThumbnails.CreateUI(UIElements, true);
            ((UIDynamicSlider)maxRows.CreateUI(UIElements, rightSide: false)).slider.wholeNumbers = true;
            buttonSizeJ.CreateUI(UIElements, rightSide:false);
            buttonTransparency.CreateUI(UIElements, rightSide: false);
            buttonSpacing.CreateUI(UIElements, true);
            
            if (currentCanvas < 2)
            {
                UIElements.Add(CreateSpacer(true).ForceHeight(30f));
                UIElements.Add(CreateTextField(canvasInfo, false).ForceHeight(300f));
                if (currentCanvas == 1)
                {
                    UIElements.Add(CreateTextField(hudCanvasInfo, true).ForceHeight(300f));
                    hudLevelNavOnTop.CreateUI(UIElements);
                    keepHudCanvasWhenUIClosed.CreateUI(UIElements, true);
                }
            }
            if(currentCanvas == 2)
            {
                worldCanvasPosSlider = UIManager.CreateV3Slider(worldCanvasPosition, this);
                UIElements.Add(worldCanvasPosSlider);
                worldCanvasRotSlider = UIManager.CreateV3Slider(worldCanvasRotation, this);
                UIElements.Add(worldCanvasRotSlider);
                worldCanvasFacePlayerToggle = (UIDynamicToggle)worldButtonsFacePlayer.CreateUI(UIElements, false);
                UIElements.Add(CreateSpacer(true).ForceHeight(235f));
                worldCanvasInFront.CreateUI(UIElements, true);
                worldLevelNavOnTop.CreateUI(UIElements);
            }
        }

        private void CreateButtonInteractionUINewPage()
        {
            ClearUI();
            UIElements.Clear();
            var button = CreateButton("Return");
            button.buttonColor = PoseMe.navColor;
            button.button.onClick.AddListener(
                () =>
                {
                    ClearUI();
                    CreateUI();
                    SelectTab(currentTab);
                });
            printInputConfigOnFirstHover.CreateUI(UIElements, true);
            leftClickChooser.CreateUI(UIElements, rightSide: false, chooserType: 0);
            rightClickChooser.CreateUI(UIElements, rightSide: false, chooserType: 0);
            middleClickChooser.CreateUI(UIElements, rightSide: false, chooserType: 0);
            
            dragDownChooser.CreateUI(UIElements, rightSide: true, chooserType: 0);
            dragUpChooser.CreateUI(UIElements, rightSide: true, chooserType: 0);
            dragLeftChooser.CreateUI(UIElements, rightSide: true, chooserType: 0);
            dragRightChooser.CreateUI(UIElements, rightSide: true, chooserType: 0);
            
            disableNativeShortcuts.CreateUI(UIElements);
            UIElements.Add(CreateTextField(shortCutInfo));
        }

        private void CreateGeneralUI()
        {
            camMode.CreateUI(UIElements, chooserType:0);
            smoothCamSpeedJ.CreateUI(UIElements);
            smoothCamBezierStrength.CreateUI(UIElements);
            applyIdles.CreateUI(UIElements);
            restoreHeadRotation.CreateUI(UIElements);
            restoreRoot.CreateUI(UIElements);
            restoreOtherPersons.CreateUI(UIElements);
            restoreDildos.CreateUI(UIElements);
            restoreToys.CreateUI(UIElements);
            
            var button = this.SetupButton("Reload From Last Saved Scene", false, () => ReloadFromScene());
            button.GetComponentInChildren<Image>().color = severeWarningColor;
            UIElements.Add(button);
            button = this.SetupButton("Clear All Poses", false, ClearPoses);
            button.GetComponentInChildren<Image>().color = severeWarningColor;
            UIElements.Add(button);
            autoBackUpDeleted.CreateUI(UIElements);
            ignoreTriggers.CreateUI(UIElements);
            ignoreDialogs.CreateUI(UIElements);
            
            
            
            // UIElements.Add(CreateSpacer(true).ForceHeight(10f));

            fistCamOnPoseSwitch.CreateUI(UIElements, true);
            copyIdlesToNewPose.CreateUI(UIElements, true);
            copySlapsToNewPose.CreateUI(UIElements, true);
        }

        public static void RegisterActorToggles()
        {
            try
            {
                if (currentTab > 0 || IdleUIProvider.uiOpen || currentPose == null) return;
                
                foreach (var actor in actors)
                {
                    actorTogglesUid[actor].RegisterBool(currentPose.actorToggles[actor]);
                    actorTogglesUid[actor].SetColor(!currentPose.pose["pose"].AsObject.HasKey(actor.uid) ? warningColor : Color.white);
                }
                poseApplyIdlesUid.RegisterBool(currentPose.poseIdle.applyIdles);
            }
            catch (Exception e)
            {
                SuperController.LogError(e.ToString());
            }
        }

        private void CreatePoseUI()
        {
            UIDynamicButton button;
            poseChooser.CreateUI(UIElements, rightSide: false, chooserType: 3);
            var addPoseButton = this.SetupButton("Add Pose", false, () => AddPose(), UIElements);
            var cb = addPoseButton.button.colors;
            cb.pressedColor = cb.highlightedColor = cb.normalColor;
            addPoseButton.button.colors = cb;
            var pointerHandler = addPoseButton.button.gameObject.AddComponent<PointerHandler>();
            pointerHandler.onPointerEnter.AddListener(() =>
            {
                addPoseButton.button.image.color = new Color(0.38f, 1f, 0.47f);
            });
            pointerHandler.onPointerExit.AddListener(() =>
            {
                addPoseButton.button.image.color = Color.white;
            });
            var updatePoseButton = this.SetupButton("Update Pose", false, UpdatePose, UIElements);
            updatePoseButton.button.colors = cb;
            pointerHandler = updatePoseButton.button.gameObject.AddComponent<PointerHandler>();
            pointerHandler.onPointerEnter.AddListener(() =>
            {
                updatePoseButton.button.image.color = warningColor;
            });
            pointerHandler.onPointerExit.AddListener(() =>
            {
                updatePoseButton.button.image.color = Color.white;
                updatePoseButton.textColor = Color.black;
            });
            UIElements.Add(Utils.SetupTwinButton(this,
                "Move Pose Up", () => MovePose(true),
                "Move Pose Down", () => MovePose(false), false)
            );

            var delPosebutton = this.SetupButton("Delete Pose", false, DeletePose);
            delPosebutton.button.colors = cb;
            delPosebutton.buttonColor = warningColor;
            pointerHandler = delPosebutton.button.gameObject.AddComponent<PointerHandler>();
            pointerHandler.onPointerEnter.AddListener(() =>
            {
                delPosebutton.button.image.color = severeWarningColor;
                delPosebutton.textColor = Color.white;
            });
            pointerHandler.onPointerExit.AddListener(() =>
            {
                delPosebutton.button.image.color = warningColor;
                delPosebutton.textColor = Color.black;
            });
            // button.button.image.color = severeWarningColor;
            UIElements.Add(delPosebutton);

            restoreRoot.CreateUI(UIElements);
            
            UIElements.Add(this.SetupButton("Save Pose To Library", false, () =>
            {
                if(currentPose == null) return;
                var scenename = sceneName;
                if(scenename == "") ioSystem.defaultName = $"/{sceneType}/MyScene/{currentPose.id:000}";
                else ioSystem.defaultName = $"/{sceneType}/{scenename}/{currentPose.id:000}";
                ioSystem.UISaveJSONDialog();
            }));
            button = this.SetupButton("Import Pose From Library");
            UIElements.Add(button);
            ioSystem.RegisterLoadButton(button.button);

            // if (poses.Count == 0) return;
            UIElements.Add(this.SetupButton("Configure Idles", false, () => {
                IdleUIProvider.CreateUI();
            }));
            applyIdles.CreateUI(UIElements);
            poseApplyIdlesUid = UIManager.CreateReusableUIDynamicToggle();
            UIElements.Add(poseApplyIdlesUid);
            // poseRandomizeCamUid = UIManager.CreateReusableUIDynamicToggle();
            // UIElements.Add(poseRandomizeCamUid);
            // if (currentPose != null) poseRandomizeCamUid.RegisterBool(currentPose.randomizeAngles);
            
            var textfield = CreateTextField(actorsJ);
            textfield.ForceHeight(75f);
            UIElements.Add(textfield);

            foreach (var actor in actors)
            {
                var uid = UIManager.CreateReusableUIDynamicToggle(false);
                actorTogglesUid[actor] = uid;
                // if(currentPose != null) actorTogglesUid[actor].RegisterBool(currentPose.actorToggles[actor]);
                UIElements.Add(uid);
            }
            RegisterActorToggles();

            
            camChooser.CreateUI(UIElements, rightSide: true, chooserType: 3);
            var addCamButton = this.SetupButton("Add Cam", true, () => currentPose.AddCamAngle(), UIElements);
            addCamButton.button.colors = cb;
            pointerHandler = addCamButton.button.gameObject.AddComponent<PointerHandler>();
            pointerHandler.onPointerEnter.AddListener(() =>
            {
                addCamButton.button.image.color = new Color(0.38f, 1f, 0.47f);
            });
            pointerHandler.onPointerExit.AddListener(() =>
            {
                addCamButton.button.image.color = Color.white;
            });
            var updateCamButton = this.SetupButton("Update Cam", true, () => currentPose.UpdateAngle(), UIElements);
            updateCamButton.button.colors = cb;
            pointerHandler = updateCamButton.button.gameObject.AddComponent<PointerHandler>();
            pointerHandler.onPointerEnter.AddListener(() =>
            {
                updateCamButton.button.image.color = warningColor;
            });
            pointerHandler.onPointerExit.AddListener(() =>
            {
                updateCamButton.button.image.color = Color.white;
            });
            UIElements.Add(Utils.SetupTwinButton(this, 
                "Move Cam Up", () => currentPose.MoveAngle(true),
                "Move Cam Down", () => currentPose.MoveAngle(false), true)
            );


            var delCamButton = this.SetupButton("Delete Cam", true, () => currentPose.DeleteAngle());
            delCamButton.buttonColor = warningColor;
            delCamButton.button.colors = cb;
            pointerHandler = delCamButton.button.gameObject.AddComponent<PointerHandler>();
            pointerHandler.onPointerEnter.AddListener(() =>
            {
                delCamButton.button.image.color = severeWarningColor;
                delCamButton.textColor = Color.white;
            });
            pointerHandler.onPointerExit.AddListener(() =>
            {
                delCamButton.button.image.color = warningColor;
                delCamButton.textColor = Color.black;
            });
            UIElements.Add(delCamButton);

            UIImageButton = this.SetupButton("", true);
            UIImageButton.ForceHeight(512f);
            // var cb = UIImageButton.button.colors;
            // cb.pressedColor = cb.highlightedColor = cb.normalColor;
            UIImageButton.button.colors = cb;
            UIImageButton.buttonText.alignment = TextAnchor.LowerRight;
            UIImageButton.buttonText.fontSize = 25;
            UIImageButton.buttonText.color = Color.white;
            UIImage = UIImageButton.GetComponentInChildren<Image>();
            UIImage.color = Color.white;
            UIElements.Add(UIImageButton);
            var clickListener = UIImageButton.gameObject.AddComponent<ClickListener>();
            // clickListener.onLeftClick.AddListener(() => currentPose.ApplyNextCamAngle());
            clickListener.onLeftClick.AddListener(onLeftClick);
            clickListener.onRightClick.AddListener(() =>
            {
                currentPose.ApplyPreviousCamAngle();
            });
            clickListener.onMiddleClick.AddListener(() => currentPose.Apply(false));
            clickListener.onPointerEnter.AddListener(() => {
                GlobalSceneOptions.singleton.disableNavigation = true;
                SuperController.singleton.disableNavigation = true;
                buttonHovered = true;
            });
            clickListener.onPointerExit.AddListener(() => {
                GlobalSceneOptions.singleton.disableNavigation = false;
                SuperController.singleton.disableNavigation = false;
                buttonHovered = false;
            });
            
            if (currentPose != null)
            {
                currentPose.SetUIImage();
            }
            
            textfield = CreateTextField(
                new JSONStorableString("warning",
                    "Use <b>ONLY ONE (1)</b> instance of PoseMe per scene! All actors will be posed automatically."),
                true);
            textfield.ForceHeight(100f);
            UIElements.Add(textfield);
            this.SetupButton("Restore last deleted/updated pose", true, () =>
            {
                if(poseCache != null) AddPose(poseCache);
            }, UIElements);
            // UIManager.CreateToggleWithButton(new JSONStorableBool("Huhu", false));
            // this.SetupButton("Reset Finger Springs", true, Pose.ResetFingerSprings);
        }
        
        // static void ExecuteCommand(string command)
        // {
        //     int exitCode;
        //     ProcessStartInfo processInfo;
        //     Process process;
        //
        //     processInfo = new ProcessStartInfo("cmd.exe", "/c " + command);
        //     processInfo.CreateNoWindow = true;
        //     processInfo.UseShellExecute = false;
        //     // *** Redirect the output ***
        //     processInfo.RedirectStandardError = true;
        //     processInfo.RedirectStandardOutput = true;
        //
        //     process = Process.Start(processInfo);
        //     process.WaitForExit();
        //
        //     // *** Read the streams ***
        //     // Warning: This approach can lead to deadlocks, see Edit #2
        //     string output = process.StandardOutput.ReadToEnd();
        //     string error = process.StandardError.ReadToEnd();
        //
        //     exitCode = process.ExitCode;
        //
        //     ("output>>" + (String.IsNullOrEmpty(output) ? "(none)" : output)).Print();
        //     ("error>>" + (String.IsNullOrEmpty(error) ? "(none)" : error)).Print();
        //     ("ExitCode: " + exitCode.ToString(), "ExecuteCommand")).Print();
        //     process.Close();
        // }

        private void CreateDialogsUI()
        {
            if (poses.Count == 0 || currentPose == null)
            {
                UIElements.Add(CreateTextField(new JSONStorableString("bla", "Create or select a pose first.")));
                return;
            }
            this.SetupButton("Configure Dialog Pool", false, DialogPool.CreateUI, UIElements);
            this.SetupButton("Help", true, () => dialogInfo.Print(), UIElements);
            
            var popup = poseChooser.CreateUI(UIElements, rightSide: false, chooserType: 3) as UIDynamicPopup;
            camChooser.CreateUI(UIElements, rightSide: true, chooserType: 3);
            if (currentPose?.currentCam == null)
            {
                UIElements.Add(CreateTextField(new JSONStorableString("", "Add/Select a pose and cam first.")));
                return;
            }
            dialogPoolLevelPose.CreateUI(UIElements, false);
            dialogPoolLevelPose.slider.wholeNumbers = true;
            dialogPoolLevelCam.CreateUI(UIElements, true);
            dialogPoolLevelCam.slider.wholeNumbers = true;
            // this.SetupButton("Add Pose Dialog", false, () => AddDialog(), UIElements);
            // this.SetupButton("Add Cam Dialog", true, () => AddDialog(true), UIElements);
            UIElements.Add(Utils.SetupTwinButton(this, "Add Pose Dialog", () => AddDialog(),"Paste Dialog", () => AddDialogFromJSON(Dialog.cache), false));
            UIElements.Add(Utils.SetupTwinButton(this, "Add Cam Dialog", () => AddDialog(true),"Paste Dialog", () => AddDialogFromJSON(Dialog.cache, true), true));
            currentPose.CreateDialogItems();
            currentPose.currentCam.CreateBubbleItems();
            SyncPoseActionsUI();
            SyncCamActionsUI();
        }

        private void CreateActionsUI()
        {
            if (poses.Count == 0 || currentPose == null)
            {
                UIElements.Add(CreateTextField(new JSONStorableString("bla", "Create or select a pose first.")));
                return;
            }
            var popup = poseChooser.CreateUI(UIElements, rightSide: false, chooserType: 3) as UIDynamicPopup;
            camChooser.CreateUI(UIElements, rightSide: true, chooserType: 3);
            if (currentPose?.currentCam == null)
            {
                UIElements.Add(CreateTextField(new JSONStorableString("", "Create or select a pose and cam first.")));
                return;
            }
            onPoseEnterTriggerButton = CreateButton("On Pose Enter", false);
            onPoseEnterTriggerButton.buttonColor = new Color(0.45f, 1f, 0.45f);
            onPoseExitTriggerButton = CreateButton("On Pose Exit", false);
            onPoseExitTriggerButton.buttonColor = new Color(0.45f, 1f, 0.45f);
            UIElements.Add(onPoseEnterTriggerButton);
            UIElements.Add(onPoseExitTriggerButton);
            
            poseCumMalesToggle = UIManager.CreateReusableUIDynamicToggle(false, currentPose.cumMales);
            poseCumFemaleToggle = UIManager.CreateReusableUIDynamicToggle(false, currentPose.cumFemale);
            disableAnatomyToggle = UIManager.CreateReusableUIDynamicToggle(false, currentPose.disableAnatomy);
            disableAnatomyToggle.toggle.onValueChanged.AddListener(val => disableAnatomy.val = val);
            UIElements.Add(poseCumMalesToggle);
            UIElements.Add(poseCumFemaleToggle);
            UIElements.Add(disableAnatomyToggle);
            
            // poseTimelineOnEnter.SetChoices(timeline.GetActionNames());
            poseTimelineOnEnter.CreateUI(UIElements, chooserType:2);
            
            onCamEnterTriggerButton = CreateButton("On Cam Enter", true);
            onCamEnterTriggerButton.buttonColor = new Color(0.45f, 1f, 0.45f);
            onCamExitTriggerButton = CreateButton("On Cam Exit", true);
            onCamExitTriggerButton.buttonColor = new Color(0.45f, 1f, 0.45f);
            UIElements.Add(onCamEnterTriggerButton);
            UIElements.Add(onCamExitTriggerButton);
            
            camCumMalesToggle = UIManager.CreateReusableUIDynamicToggle(true, currentPose.currentCam.cumMales);
            camCumFemaleToggle = UIManager.CreateReusableUIDynamicToggle(true, currentPose.currentCam.cumFemale);
            UIElements.Add(camCumMalesToggle);
            UIElements.Add(camCumFemaleToggle);

            camTimelineOnEnter.CreateUI(UIElements, true, chooserType:2);

            // this.SetupButton("Left Slap", false, () => persons[1].lSlap.Invoke().Start());
            // this.SetupButton("Right Slap", false, () => persons[1].rSlap.Invoke().Start());
            // this.SetupButton("Toggle Left Slap", false, () => persons[1].lSlap.enabled = !persons[1].lSlap.enabled);
            // this.SetupButton("Toggle Right Slap", false, () => persons[1].rSlap.enabled = !persons[1].rSlap.enabled);
            // this.SetupButton("Configure Right Slap", false, () => persons[1].rSlap.CreateConfigureUI());

            // CreateSlapUIItem(persons[1].rSlap);

            SyncPoseActionsUI();
            SyncCamActionsUI();
        }

        private void CreateMovementUI()
        {
            if (poses.Count == 0 || currentPose == null)
            {
                UIElements.Add(CreateTextField(new JSONStorableString("bla", "Create or select a pose first.")));
                return;
            }
            this.SetupButton("Add Slap & Caress", false, () => currentPose.AddSlap(), UIElements);
            var button = Utils.SetupTwinButton(this, "Copy Slaps", () => currentPose.CopySlaps(), "Paste Slaps", () => currentPose.PasteSlaps(), false);
            UIElements.Add(button);
            Slap.autoLinkHand.CreateUI(UIElements);
            this.SetupButton("Add Misc Movement", true, () => currentPose.AddMovement(), UIElements);
            button = Utils.SetupTwinButton(this, "Copy Movements", () => currentPose.CopyMovements(), "Paste Movements", () => currentPose.PasteMovements(), true);
            UIElements.Add(button);
            currentPose.CreateSlapItems();
            currentPose.CreateMovementItems();
        }

        private void AddDialog(bool addToCam = false)
        {
            if(currentPose == null) return;
            if(!addToCam)
            {
                var bubble = new Dialog();
                currentPose.dialogs.Add(bubble);
                // if (currentPose.usePoolBubbles.val) currentPose.usePoolBubbles.val = false;
                currentPose.dialogPoolLevel.val = -1f;
                dialogPoolLevelPose.valNoCallback = currentPose.dialogPoolLevel.val;
                poseBubbleItems.Add(CreateDialogUIItem(bubble));
            }
            else
            {
                if(currentPose.currentCam == null) return;
                var bubble = new Dialog(true);
                currentPose.currentCam.dialogs.Add(bubble);
                // if (currentPose.currentCam.usePoolBubbles.val) currentPose.currentCam.usePoolBubbles.val = false;
                currentPose.currentCam.dialogPoolLevel.val = -1f;
                dialogPoolLevelCam.valNoCallback = currentPose.currentCam.dialogPoolLevel.val;
                camBubbleItems.Add(CreateDialogUIItem(bubble, true));
            }
        }
        
        private void AddDialogFromJSON(JSONClass jc, bool addToCam = false)
        {
            if(currentPose == null) return;
            if (jc == null)
            {
                "Copy a dialog first.".Print();
                return;
            }
            if(!addToCam)
            {
                var bubble = new Dialog(jc);
                currentPose.dialogs.Add(bubble);
                // if (currentPose.usePoolBubbles.val) currentPose.usePoolBubbles.val = false;
                currentPose.dialogPoolLevel.val = -1f;
                dialogPoolLevelPose.valNoCallback = currentPose.dialogPoolLevel.val;
                poseBubbleItems.Add(CreateDialogUIItem(bubble));
            }
            else
            {
                if(currentPose.currentCam == null) return;
                var dialog = new Dialog(jc)
                {
                    isCamDialog = { val = true }
                };
                currentPose.currentCam.dialogs.Add(dialog);
                // if (currentPose.currentCam.usePoolBubbles.val) currentPose.currentCam.usePoolBubbles.val = false;
                currentPose.currentCam.dialogPoolLevel.val = -1f;
                dialogPoolLevelCam.valNoCallback = currentPose.currentCam.dialogPoolLevel.val;
                camBubbleItems.Add(CreateDialogUIItem(dialog, true));
            }
        }

        private static GameObject bubbleUIItemPrefab;
        public static List<object> poseBubbleItems = new List<object>();
        public static List<object> camBubbleItems = new List<object>();

        public static List<object> slapItems = new List<object>();
        public static List<object> movementItems = new List<object>();
        

        public static void SyncPoseActionsUI()
        {
            if(currentPose == null) return;
            if (currentTab == 1)
            {
                dialogPoolLevelPose.valNoCallback = currentPose.dialogPoolLevel.val;
            }
            else if(currentTab == 2)
            {
                onPoseEnterTriggerButton.button.onClick.RemoveAllListeners();
                onPoseExitTriggerButton.button.onClick.RemoveAllListeners();
                onPoseEnterTriggerButton.button.onClick.AddListener(currentPose.onPoseEnter.OpenPanel);
                onPoseExitTriggerButton.button.onClick.AddListener(currentPose.onPoseExit.OpenPanel);

                poseCumMalesToggle.RegisterBool(currentPose.cumMales);
                poseCumFemaleToggle.RegisterBool(currentPose.cumFemale);
                disableAnatomyToggle.RegisterBool(currentPose.disableAnatomy);
                poseTimelineOnEnter.valNoCallback =
                    currentPose.timelineClip != null ? currentPose.timelineClip.name : "";
                camTimelineOnEnter.valNoCallback =
                    currentPose.currentCam?.timelineClip != null ? currentPose.currentCam.timelineClip.name : "";
            }
        }
        
        public static void SyncCamActionsUI(CamAngle camAngle = null)
        {
            if(currentTab == 1)
            {
                dialogPoolLevelCam.valNoCallback = currentPose.currentCam.dialogPoolLevel.val;
            }
            else if(currentTab == 2)
            {
                if (camAngle == null)
                {
                    if (currentPose?.currentCam == null) return;
                    camAngle = currentPose.currentCam;
                }

                onCamEnterTriggerButton.button.onClick.RemoveAllListeners();
                onCamExitTriggerButton.button.onClick.RemoveAllListeners();
                onCamEnterTriggerButton.button.onClick.AddListener(camAngle.onCamEnter.OpenPanel);
                onCamExitTriggerButton.button.onClick.AddListener(camAngle.onCamExit.OpenPanel);

                camCumMalesToggle.RegisterBool(currentPose.currentCam.cumMales);
                camCumFemaleToggle.RegisterBool(currentPose.currentCam.cumFemale);
                camTimelineOnEnter.valNoCallback =
                    currentPose.currentCam?.timelineClip != null ? currentPose.currentCam.timelineClip.name : "";
            }
            
        }

        public void ClearUI()
        {
            RemoveUIElements(leftUIElements.Select(x => (object) x.GetComponent<UIDynamic>()).ToList());
            RemoveUIElements(rightUIElements.Select(x => (object) x.GetComponent<UIDynamic>()).ToList());
            UIElements.Clear();
        }

        private IEnumerator DeferredLoadFromScene()
        {
            yield return FillMeUp.waitForPose;
            ReloadFromScene(true);
        }

        public override void InitUI()
        {
            base.InitUI();
            if(UITransform == null || uiListener != null || FillMeUp.abort) return;
            uiListener = UITransform.gameObject.AddComponent<UnityEventsListener>();
            uiListener.onEnabled.AddListener(() => UIManager.SetScript(this, CreateUIElement, leftUIElements, rightUIElements));
            uiListener.onEnabled.AddListener(() => Utils.OnInitUI(CreateUIElement));
            uiListener.onEnabled.AddListener(() =>
            {
                
                if(currentTab == 4) Gaze.SetDebugMode(true);
                if (needsUIRefresh)
                {
                    ClearUI();
                    CreateUI();
                }
                SyncTimeline();
                needsUIRefresh = false;
                
            });
            uiListener.onDisabled.AddListener(() =>
            {
                IdleUIProvider.AxisSetActive(false);
                Gaze.SetDebugMode(false);
            });
        }

        private float camTimer;
        private float poseTimer;
        public static float currentCamSpeed;
        private readonly WaitForEndOfFrame waitForEndOfFrame = new WaitForEndOfFrame();
        
        private void Update()
        {
            // JoystickControl.GetAxis(JoystickControl.Axis.RightStickX).Print();
            // "popo".Print();
            // OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick).x.Print();
            // OVRInput.controllers.Count.Print();
            if(!disableNativeShortcuts.val)
            {
                if (Input.GetKey(KeyCode.RightControl))
                {
                    if (Input.GetKeyDown(KeyCode.U) && currentPose != null) UpdatePose();
                    if (Input.GetKeyDown(KeyCode.P)) AddPose();
                }
                else if (Input.GetKey(KeyCode.LeftAlt))
                {
                    if (Input.GetKeyDown(KeyCode.G)) Gaze.tempDisable.actionCallback.Invoke();
                }
            }
            if (poses.Count > 0)
            {
                if (showWorldCanvas.val && worldButtonsFacePlayer.val)
                {
                    var position = WorldCanvas.go.transform.position;
                    WorldCanvas.go.transform.rotation = Quaternion.LookRotation(position - camera.position.SetComponent(1, position.y));
                }
                else if(screenWidth != Screen.width || screenHeight != Screen.height)
                {
                    PositionCanvas(posX.val, posY.val);
                    screenWidth = Screen.width;
                    screenHeight = Screen.height;
                }
                for (int i = 0; i < poses.Count; i++)
                {
                    var pose = poses[i];
                    pose.onPoseEnter.Update();
                    pose.onPoseExit.Update();
                    foreach (var dialog in Dialog.dialogs)
                    {
                        dialog.onDialogEnter.Update();
                        // dialog.onDialogExit.Update();
                    }
                }
                if(!cinematicEnabled.val) return;
                if (cinematicPoseMode > 0 && poses.Count > 1)// && currentPose.randomizeAngles.val)
                {
                    poseTimer -= Time.deltaTime;
                    if (poseTimer < 0)
                    {
                        poseTimer = NormalDistribution.GetValue(poseTimeMean.val, poseTimeDelta.val, 2);
                        if(cinematicPoseMode == 1) {nextPose.actionCallback.Invoke();}
                        else randomPose.actionCallback.Invoke();
                        camTimer += 5f;
                    }
                }

                if (cinematicCamMode > 0 && currentPose.camAngles.Count > 1)// && currentPose.randomizeAngles.val)
                {
                    camTimer -= Time.deltaTime;
                    if (camTimer < 0)
                    {
                        camTimer = NormalDistribution.GetValue(camTimeMean.val, camTimeDelta.val, 2);
                        currentCamSpeed = NormalDistribution.GetValue(camSpeedMean.val, camSpeedDelta.val, 2f);
                        DeferredApplyAngle(cinematicCamMode == 2).Start();
                        // currentPose.ApplyRandomCamAngle();
                    }
                }
            }
        }

        private void FixedUpdate()
        {
            if(Pose.isApplying || SuperController.singleton.freezeAnimation) return;
            for (int i = 0; i < persons.Count; i++)
            {
                persons[i].gaze?.FixedUpdate();
            }
        }

        private IEnumerator DeferredApplyAngle(bool random)
        {
            yield return waitForEndOfFrame;
            if(random) currentPose.ApplyRandomCamAngle();
            else currentPose.ApplyNextCamAngle();
        }

        private void OnDisable()
        {
            if(buttonGroup != null) buttonGroup.SetActive(false);
            Pose.smoothCam.Stop();
            if(currentPose != null) currentPose.selected = false;
            disableAnatomy.val = false;
        }

        private void OnDestroy()
        {
            Destroy(uiListener);
            Destroy(hudEnabledListener);
            Destroy(worldUIListener);
            SuperController.singleton.onAtomAddedHandlers -= OnAtomAdded;
            SuperController.singleton.onAtomRemovedHandlers -= OnAtomRemoved;
            SuperController.singleton.onAtomUIDRenameHandlers -= OnAtomRename;
            Destroy(buttonGroup);
            poses.ForEach(x => x.Destroy());
            SuperController.singleton.BroadcastMessage("OnActionsProviderDestroyed", this, SendMessageOptions.DontRequireReceiver);
            disableAnatomy.val = false;
            Story.Destroy();
            worldCanvas.Destroy();
        }

        private JSONArray GetPoses()
        {
            var ja = new JSONArray();
            for (int i = 0; i < poses.Count; i++)
            {
                poses[i].UpdateJSON();
                ja.Add(poses[i].pose);
            }
            return ja;
        }

        public static JSONStorableBool ignoreTriggers = new JSONStorableBool("Ignore Triggers", false);
        public static JSONStorableBool ignoreDialogs = new JSONStorableBool("Ignore Dialogs", false);
        
        private void Store(JSONClass jc)
        {
            poseChooser.Store(jc);
            camChooser.Store(jc);
            camMode.Store(jc);
            cinematicCamModeChooser.Store(jc);
            cinematicPoseModeChooser.Store(jc);
            smoothCamSpeedJ.Store(jc);
            smoothCamBezierStrength.Store(jc);
            
            camTimeMean.Store(jc);
            camTimeDelta.Store(jc);
            poseTimeMean.Store(jc);
            poseTimeDelta.Store(jc);
            camSpeedMean.Store(jc);
            camSpeedDelta.Store(jc);

            keepHudCanvasWhenUIClosed.Store(jc);
            showThumbnails.Store(jc);
            showScreeenAndHUDCanvas.Store(jc);
            maxRows.Store(jc);
            buttonSizeJ.Store(jc);
            buttonTransparency.Store(jc);
            buttonSpacing.Store(jc);
            // canvasChooser.Store(jc);
            showWorldCanvas.Store(jc);
            worldCanvasPosition.Store(jc);
            worldCanvasRotation.Store(jc);
            worldButtonsFacePlayer.Store(jc);
            worldCanvasInFront.Store(jc);
            worldLevelNavOnTop.Store(jc);
            hudLevelNavOnTop.Store(jc);
            
            printInputConfigOnFirstHover.Store(jc);
            applyIdles.Store(jc);
            restoreRoot.Store(jc);
            restoreHeadRotation.Store(jc);
            restoreOtherPersons.Store(jc);
            restoreDildos.Store(jc);
            restoreToys.Store(jc);

            leftClickChooser.Store(jc);
            rightClickChooser.Store(jc);
            middleClickChooser.Store(jc);
            dragDownChooser.Store(jc);
            dragLeftChooser.Store(jc);
            dragUpChooser.Store(jc);
            dragRightChooser.Store(jc);

            fistCamOnPoseSwitch.Store(jc);
            dialogColorGain.Store(jc);
            
            ignoreTriggers.Store(jc);
            ignoreDialogs.Store(jc);

            copyIdlesToNewPose.Store(jc);
            copySlapsToNewPose.Store(jc);
            
            Story.Store(jc);
            Gaze.StoreGlobals(jc);

            for (int i = 0; i < 3; i++)
            {
                canvasSettings[i].Store(jc);
            }

            jc["DialogPool"] = DialogPool.Store();
        }
        
        private void Load(JSONClass jc)
        {
            worldCanvasInFront.Load(jc);
            worldLevelNavOnTop.Load(jc);
            worldCanvasPosition.Load(jc);
            worldCanvasRotation.Load(jc);
            worldButtonsFacePlayer.Load(jc);
            showWorldCanvas.Load(jc);
            hudLevelNavOnTop.Load(jc);
            Story.Load(jc);
            for (int i = 0; i < 3; i++)
            {
                canvasSettings[i].Load(jc);
            }
            ignoreTriggers.val = ignoreDialogs.val = true;
            poseChooser.Load(jc);
            camChooser.Load(jc);
            camMode.Load(jc);
            cinematicCamModeChooser.Load(jc);
            cinematicPoseModeChooser.Load(jc);
            smoothCamSpeedJ.Load(jc);
            smoothCamBezierStrength.Load(jc);
            
            camTimeMean.Load(jc);
            camTimeDelta.Load(jc);
            poseTimeMean.Load(jc);
            poseTimeDelta.Load(jc);
            camSpeedMean.Load(jc);
            camSpeedDelta.Load(jc);

            keepHudCanvasWhenUIClosed.Load(jc);
            showThumbnails.Load(jc);
            showScreeenAndHUDCanvas.Load(jc);
            maxRows.Load(jc);
            buttonSizeJ.Load(jc);
            buttonTransparency.Load(jc);
            buttonSpacing.Load(jc);
            // canvasChooser.Load(jc);
            
            
            printInputConfigOnFirstHover.Load(jc);
            applyIdles.Load(jc);
            restoreRoot.Load(jc);
            restoreHeadRotation.Load(jc);
            restoreOtherPersons.Load(jc);
            restoreDildos.Load(jc);
            restoreToys.Load(jc);

            leftClickChooser.Load(jc);
            rightClickChooser.Load(jc);
            middleClickChooser.Load(jc);
            dragDownChooser.Load(jc);
            dragLeftChooser.Load(jc);
            dragUpChooser.Load(jc);
            dragRightChooser.Load(jc);

            fistCamOnPoseSwitch.Load(jc);
            dialogColorGain.Load(jc);
            
            ignoreTriggers.Load(jc);
            ignoreDialogs.Load(jc);
            
            copyIdlesToNewPose.Load(jc);
            copySlapsToNewPose.Load(jc);
            
            

            if(jc.HasKey("DialogPool")) DialogPool.Load(jc["DialogPool"].AsObject, false);
            if (SuperController.singleton.isOVR || SuperController.singleton.isOpenVR)
            {
                camMode.val = "None";
                canvasChooser.val = "HUD (VR)";
            }
            else canvasChooser.val = "Screen (Desktop)";
            // canvasChooser.val = "HUD (VR)";
        }
        
        public override JSONClass GetJSON(bool includePhysical = true, bool includeAppearance = true, bool forceStore = false)
        {
            JSONClass jc = base.GetJSON(includePhysical, includeAppearance, true);
            Store(jc);
            jc["poses"] = GetPoses();
            return jc;
        }

        private bool restored;
        public override void LateRestoreFromJSON(JSONClass jc, bool restorePhysical = true,
            bool restoreAppearance = true, bool setMissingToDefault = true)
        {
            try
            {
                if(restored) return;
                base.LateRestoreFromJSON(jc, restorePhysical, restoreAppearance, setMissingToDefault);
                if (!physicalLocked && restorePhysical && !IsCustomPhysicalParamLocked("trigger"))
                {
                    needsStore = true;
                    if (jc.HasKey("poses"))
                    {
                        // ClearPoses();
                        var ja = jc["poses"].AsArray;
                        Gaze.LoadGlobals(jc);
                        foreach (JSONClass poseJc in ja.Childs)
                        {
                            LoadPoseFromJSON(poseJc, false);
                        }
                    }
                    Load(jc);
                    // OnShowThumbnailChanged(showThumbnails.val);
                    SyncCamChooser();
                }
                SuperController.singleton.BroadcastMessage("OnActionsProviderAvailable", this, SendMessageOptions.DontRequireReceiver);
                restored = true;
                // foreach (var pose in poses)
                // {
                //     pose.id.Print();
                //     foreach (var slap in pose.slaps)
                //     {
                //         slap.caressForceZ.enabled.Print();
                //     }
                // }
                // useWorldCanvas.val = true;
                // UITransform.PrintParents();
            }
            catch (Exception e)
            {
                SuperController.LogError(e.ToString());
            }
        }

        private void ReloadFromScene(bool init = false)
        {
            var sceneName = SuperController.singleton.LoadedSceneName;
            if (string.IsNullOrEmpty(sceneName))
            {
                if(!init) "Scene not loaded from a json.".Print();
                return;
            }
            
            var sceneJson = SuperController.singleton.LoadJSON(sceneName)["atoms"].Childs
                .FirstOrDefault(x => x["id"].Value == containingAtom.uid);
            if (sceneJson == null)
            {
                if(!init) $"Atom {containingAtom.uid} not found in the loaded scene json. Did you rename the person?".Print();
                return;
            }
            sceneJson = sceneJson["storables"].Childs.
                FirstOrDefault(x => x["id"].Value.EndsWith("CheesyFX.PoseMe"));
            if (sceneJson == null)
            {
                if(!init) $"Atom {containingAtom.uid} doesn't have PoseMe stored in the loaded scene json.".Print();
                return;
            }
            // SuperController.singleton.LoadedSceneName.Print();
            var pluginJSON = sceneJson.AsObject;
            if (!pluginJSON.HasKey("poses") || !pluginJSON["poses"].Childs.Any())
            {
                if(!init) $"Scene {sceneName} doesn't have poses.".Print();
                return;
            }
            LateRestoreFromJSON(pluginJSON, true, false, true);
        }

        public void OnAtomRename(string oldUid, string newUid)
        {
            try
            {
                for (int i = 0; i < poses.Count; i++)
                {
                    var pose = poses[i];
                    pose.SyncAtomNames(oldUid, newUid);
                    pose.SyncActorToggleNames();
                    for (int j = 0; j < pose.movements.Count; j++)
                    {
                        pose.movements[j].OnAtomRenamed(oldUid, newUid);
                    }
                }
                for (int i = 0; i < Dialog.dialogs.Count; i++)
                {
                    Dialog.dialogs[i].OnPersonRenamed(oldUid, newUid);
                }
                if (currentPose == null) return;
                RegisterActorToggles();
                actorsJ.val = currentPose.actors;
                Gaze.OnAtomRenamed(oldUid, newUid);
            }
            catch (Exception e)
            {
                SuperController.LogError(e.ToString());
                throw;
            }
        }

        private void OnAtomAdded(Atom atom)
        {
            try
            {
                Gaze.OnAtomAdded(atom);
                if (atom.type == "Person" || atom.IsToyOrDildo())
                {
                    sceneType = GetSceneContent();
                    ioSystem.defaultName = $"/{sceneType}/MyPose";
                    for (int i = 0; i < poses.Count; i++)
                    {
                        var pose = poses[i];
                        pose.actorToggles[atom] = new JSONStorableBool(atom.uid, true);
                        for (int j = 0; j < pose.movements.Count; j++)
                        {
                            pose.movements[j].OnAtomAdded(atom);
                        }
                    }

                    if (currentTab == 0 && !IdleUIProvider.uiOpen)
                    {
                        actorTogglesUid[atom] = UIManager.CreateReusableUIDynamicToggle();
                        UIElements.Add(actorTogglesUid[atom]);
                        RegisterActorToggles();
                    }
                }
                
            }
            catch (Exception e)
            {
                SuperController.LogError(e.ToString());
                throw;
            }
        }
        
        private void OnAtomRemoved(Atom atom)
        {
            try
            {
                Gaze.OnAtomRemoved(atom);
                if (atom.type == "Person" || atom.IsToyOrDildo())
                {
                    sceneType = GetSceneContent();
                    ioSystem.defaultName = $"/{sceneType}/MyPose";
                    for (int i = 0; i < poses.Count; i++)
                    {
                        var pose = poses[i];
                        pose.actorToggles.Remove(atom);
                        for (int j = 0; j < pose.movements.Count; j++)
                        {
                            pose.movements[j].OnAtomRemoved(atom);
                        }
                        pose.movements.RemoveAll(x => x.atom == atom);
                    }

                    if (currentTab == 0 && !IdleUIProvider.uiOpen)
                    {
                        RemoveUIElement(actorTogglesUid[atom]);
                        UIElements.Remove(actorTogglesUid[atom]);
                    }

                    actorTogglesUid.Remove(atom);
                    RegisterActorToggles();
                }
            }
            catch (Exception e)
            {
                SuperController.LogError(e.ToString());
            }
        }

        public static UnityAction onLeftClick = () => currentPose.ApplyNextCamAngle();
        public static UnityAction onRightClick = () => currentPose.ApplyPreviousCamAngle();
        public static UnityAction onMiddleClick = () => currentPose.Apply();
        public static UnityAction onDragUp = () => currentPose.ApplyPreviousCamAngle();
        public static UnityAction onDragDown = () => currentPose.ApplyNextCamAngle();
        public static UnityAction onDragLeft = () => currentPose.ApplyRandomCamAngle();
        public static UnityAction onDragRight = () => currentPose.ApplyRandomCamAngle();

        private static void SyncInput(string val, ref UnityAction action)
        {
            switch (val)
            {
                case "None":
                {
                    action = delegate {};
                    break;
                }
                case "Next Cam":
                {
                    action = () => currentPose.ApplyNextCamAngle();
                    break;
                }
                case "Previous Cam":
                {
                    action = () => currentPose.ApplyPreviousCamAngle();
                    break;
                }
                case "Random Cam":
                {
                    action = () => currentPose.ApplyRandomCamAngle();
                    break;
                }
                case "Apply Pose":
                {
                    action = () => currentPose.Apply();
                    break;
                }
            }
        }

        public static void SyncCamChooser()
        {
            if(currentPose == null) return;
            camChooser.choices = currentPose.camAngles.Select(x => x.id.ToString()).ToList();
        }

        public static List<Atom> toys = new List<Atom>();
        private static string GetSceneContent(bool init = false)
        {
            int femCount = 0;
            int futaCount = 0;
            int maleCount = 0;
            dildos.Clear();
            toys.Clear();
            foreach (var atom in SuperController.singleton.GetAtoms())
            {
                if (atom.type == "Person")
                {
                    var person = persons.First(x => x.uid == atom.uid);
                    if (person.characterListener.dcs.gender == DAZCharacterSelector.Gender.Female)
                    {
                        if (person.characterListener.isFuta) futaCount++;
                        else femCount++;
                    }
                    else maleCount++;
                }
                else if (atom.type == "Dildo")
                {
                    dildos.Add(atom);
                }
                else if (atom.IsToy())
                {
                    toys.Add(atom);
                }
            }
            var newsceneType = new string('F', femCount)+new string('M', maleCount)+new string('D', dildos.Count)+new string('T', toys.Count);
            if (!init && newsceneType != sceneType && poses.Count > 0)
            {
                "PoeseMe: Actors have changed. Please update your poses.".Print();
            }
            return newsceneType;
        }

        private void LoadPoseFromJSON(JSONClass jc, bool apply = true)
        {
            var pose = AddPose(jc);
            if(apply) pose.Apply();
        }

        public static Pose AddPose(JSONClass jc)
        {
            int id;
            // if (poses.Count == 0) id = 0;
            // else
            // {
            //     id = poses.Select(x => x.id).Max() + 1;
            // }

            id = poses.Count;
            Pose pose;
            if (jc != null) pose = new Pose(id, jc);
            else pose = new Pose(id);
            poses.Add(pose);
            SyncPoseIds();
            var choices = poseChooser.choices;
            choices.Add(id.ToString());
            poseChooser.choices = null;
            poseChooser.choices = choices;
            LayoutPoseButtons();
            RegisterActorToggles();
            return pose;
        }
        
        public static Pose AddPose()
        {
            int id;
            JSONClass cachedIdles = null;
            if (currentPose == null) id = poses.Count;
            else
            {
                id = currentPose.id + 1;
                currentPose.CopySlaps();
                cachedIdles = currentPose.poseIdle.Store();
            }
            Pose pose;
            pose = new Pose(id);
            if(cachedIdles != null)
            {
                if(copySlapsToNewPose.val) pose.PasteSlaps();
                if(copyIdlesToNewPose.val) pose.poseIdle.Load(cachedIdles);
            }
            
            poses.Insert(id, pose);
            // pose.buttonRT.SetSiblingIndex(id);
            SyncPoseIds();
            var choices = poseChooser.choices;
            choices.Clear();
            for (int i = 0; i < poses.Count; i++)
            {
                choices.Add(i.ToString());
            }
            poseChooser.choices = null;
            poseChooser.choices = choices;
            poseChooser.valNoCallback = id.ToString();
            LayoutPoseButtons(Story.currentLevel);
            Story.SyncPoses();
            // worldCanvas.Sync();
            return pose;
        }
        
        private static void MovePose(bool up)
        {
            if(currentPose == null) return;
            int i = currentPose.id;
            int minId = 0;
            int maxId = poses.Count - 1;
            int step = Input.GetKey(KeyCode.LeftShift)? 10 : 1;
            if(Story.currentLevel != null)
            {
                minId = Story.currentLevel.minId;
                maxId = Story.currentLevel.maxId;
            }
            if (up)
            {
                if (i == minId) return;
                var targetId = Math.Max(i - step, minId);
                poses.Remove(currentPose);
                poses.Insert(targetId, currentPose);
            }
            else
            {
                if(i == maxId) return;
                var targetId = Math.Min(i + step, maxId);
                poses.Remove(currentPose);
                poses.Insert(targetId, currentPose);
            }
            SyncPoseIds();
            LayoutPoseButtons(Story.currentLevel);
            poseChooser.valNoCallback = currentPose.id.ToString();
            worldCanvas.Sync();
        }

        // private static void MovePose(bool up)
        // {
        //     if(currentPose == null) return;
        //     int i = currentPose.id;
        //     int minId = 0;
        //     int maxId = poses.Count - 1;
        //     int step = Input.GetKey(KeyCode.LeftShift)? 10 : 1;
        //     if(Story.currentLevel != null)
        //     {
        //         minId = Story.currentLevel.minId;
        //         maxId = Story.currentLevel.maxId;
        //         // if (up)
        //         // {
        //         //     if (i == Story.currentLevel.minId) return;
        //         // }
        //         // else if (i == Story.currentLevel.maxId) return;
        //     }
        //     if (up)
        //     {
        //         if (i == minId) return;
        //         var upperPose = poses.LastOrDefault(x => x.id < i);
        //         if (upperPose != null)
        //         {
        //             poses[i-1] = currentPose;
        //             poses[i] = upperPose;
        //             upperPose.id++;
        //             currentPose.id--;
        //         }
        //         // poseChooser.valNoCallback = (i - 1).ToString();
        //     }
        //     else
        //     {
        //         if(i == maxId) return;
        //         var lowerPose = poses.FirstOrDefault(x => x.id > i);
        //         if (lowerPose != null)
        //         {
        //             poses[i+1] = currentPose;
        //             poses[i] = lowerPose;
        //             lowerPose.id--;
        //             currentPose.id++;
        //         }
        //     }
        //     RenamePoses();
        //     LayoutButtons(Story.currentLevel);
        //     poseChooser.valNoCallback = currentPose.id.ToString();
        // }

        private void ClearPoses()
        {
            if (poses.Count == 0) return;
            foreach (var pose in poses)
            {
                pose.Destroy();
            }
            poseChooser.valNoCallback = "";
            poseChooser.choices = new List<string>();
            currentPose = null;
            poses.Clear();
            worldCanvas.Sync();
        }

        private static void DeletePose()
        {
            if (currentPose == null) return;
            poseCache = currentPose.pose;
            currentPose.Destroy();
            poses.Remove(currentPose);
            SyncPoseIds();
            poseChooser.valNoCallback = "";
            var choices = poseChooser.choices;
            choices.Clear();
            for (int i = 0; i < poses.Count; i++)
            {
                choices.Add(poses[i].id.ToString());
            }
            poseChooser.choices = null;
            poseChooser.choices = choices;
            
            if(autoBackUpDeleted.val) currentPose.StoreBackup();

            foreach (var level in Story.levels)
            {
                if(currentPose.id > level.maxId) continue;
                if (level.ContainsPose(currentPose.id)) level.maxId--;
                else
                {
                    level.minId--;
                    level.maxId--;
                }
            }
            UIImage.sprite = null;
            currentPose = null;
            worldCanvas.Sync();
            LayoutPoseButtons(Story.currentLevel);
        }

        private static void SyncPoseIds()
        {
            for (int i = 0; i < poses.Count; i++)
            {
                poses[i].id = i;
                poses[i].buttonRT.SetSiblingIndex(i);
            }
        }
        
        private static void UpdatePose()
        {
            int i;
            if(int.TryParse(poseChooser.val, out i))
            {
                var pose = poses[i];
                poseCache = pose.pose;
                pose.UpdatePose();
            }
        }

        private static void ApplyPose(string id)
        {
            int i;
            if(int.TryParse(id, out i) && i < poses.Count){
                poses[i].Apply();
            }
        }

        private static void ApplyCam(string id)
        {
            if (currentPose == null) return;
            int i;
            if (int.TryParse(id, out i) && i < currentPose.camAngles.Count)
            {
                currentPose.camAngles[i].Apply();
            }
        }

        private static void OnShowThumbnailChanged(bool val)
        {
            for (int i = 0; i < poses.Count; i++)
            {
                poses[i].SetButtonImage();
            }
            SyncButtonSize(buttonSizeJ.val);
        }

        private static void OnButtonSettingsChanged()
        {
            canvasSettings[currentCanvas].Update();
            LayoutPoseButtons(Story.currentLevel);
        }

        private static void SyncButtonSize(float val)
        {
            canvasSettings[currentCanvas].Update();
            if(val < buttonSizeJ.defaultVal)
            {
                for (int i = 0; i < poses.Count; i++)
                {
                    poses[i].SetButtonText();
                }
            }
            LayoutPoseButtons(Story.currentLevel);
        }

        private static void OnButtonTransparencyChanged(float val)
        {
            if (currentCanvas < 2)
            {
                Pose.deselectedColor.a = val;
            }
            else
            {
                worldCanvas.deselectedColor.a = val;
            }
            poses.ForEach(x => x.selected = x.selected);
            canvasSettings[currentCanvas].Update();
        }

        public static string sceneName
        {
            get
            {
                var loadedScenePath = SuperController.singleton.LoadedSceneName;
                if (loadedScenePath == null) return "";
                var idx = loadedScenePath.LastIndexOf("/", StringComparison.Ordinal);
                if (idx == -1) return "";
                return loadedScenePath.Substring(idx + 1).Replace(".json", "");
            }
        }
        
        public static bool inputConfigPrinted;
        public static void PrintInputConfig()
        {
            if (inputConfigPrinted || !printInputConfigOnFirstHover.val) return;
            inputConfigPrinted = true;
            string info = "PoseMe: Input config for the buttons\n\n"+
                          $"Left click: {leftClickChooser.val}\n" +
                          $"Right click: {rightClickChooser.val}\n" +
                          $"Middle click: {rightClickChooser.val}\n" +
                          $"Drag left: {dragLeftChooser.val}\n" +
                          $"Drag right: {dragRightChooser.val}\n" +
                          $"Drag up: {dragUpChooser.val}\n" +
                          $"Drag down: {dragDownChooser.val}\n\n"+
                          $"Go to '{atom.name}/BodyLanguage/PoseMe/Input' to review or change.";
            info.Print();
        }
        
        private void SetCameraRig()
        {
            if (SuperController.singleton.isOpenVR)
            {
                camera = SuperController.singleton.ViveCenterCamera.transform;
                isVR = true;
            }
            else if (SuperController.singleton.isOVR)
            {
                camera = SuperController.singleton.OVRCenterCamera.transform;
                isVR = true;
            }
            else
            {
                camera = SuperController.singleton.MonitorCenterCamera.transform;
            }
            cameraRig = camera.parent.parent;

            // SuperController.singleton.MonitorCenterCamera.transform.PrintParents();
            // SuperController.singleton.OVRCenterCamera.transform.PrintParents();
            // SuperController.singleton.ViveCenterCamera.transform.PrintParents();
            // var ho = SuperController.singleton.GetAtomByUid("[CameraRig]").transform.Find("HeightOffset");
            // foreach (var col in ho.GetComponentsInChildren<Collider>(true))
            // {
            //     if (col.name.ToLower().Contains("head"))
            //     {
            //         col.name.Print();
            //         col.enabled.Print();
            //         col.transform.PrintParents();
            //         var rb = col.GetComponent<Rigidbody>();
            //         if(rb) rb.Print();
            //     }
            // }
            // Camera.main.name.Print();
            // SuperController.singleton.ViveCenterCamera.transform.parent.parent.GetComponentsInChildren<Collider>(true).ToList().ForEach(x => x.name.Print());
        }

        private static string GetScenePackageUid()
        {
            var loadedScenePath = SuperController.singleton.LoadedSceneName;
            if (loadedScenePath == null) return "";
            var idx = loadedScenePath.IndexOf(":/", StringComparison.Ordinal);
            if (idx == -1) return "";
            return loadedScenePath.Substring(0, idx+2);
        }
        
        public static IEnumerator CreateAtomCo(string type, string basename, Action<Atom> onAtomCreated)
        {
            string uid = NewUID(basename);
            yield return SuperController.singleton.AddAtomByType(type, uid, true);
            var atom = SuperController.singleton.GetAtomByUid(uid);
            if(atom == null)
            {
                throw new NullReferenceException("atom was not created");
            }
            SuperController.singleton.RenameAtom(atom, basename);
            onAtomCreated(atom);
        }

        public static string NewUID(string basename)
        {
            var uids = new HashSet<string>(SuperController.singleton.GetAtomUIDs());
            if(!uids.Contains(basename))
            {
                return basename;
            }

            for(int i = 2; i < 1000; i++)
            {
                string uid = $"{basename}#{i}";
                if(!uids.Contains(uid))
                {
                    return uid;
                }
            }

            // you don't really want to be here 
            return basename + Guid.NewGuid();
        }
        
        public void ForceSaveJSON(JSONClass jc, string path)
        {
            var confirmButtonTransform = SuperController.singleton.transform
                .Find("WorldScaleAdjust/HUDForLogs/UserConfirmCanvas/UserConfirmPluginActionPanel(Clone)/Panel/ConfirmButton");
            if(confirmButtonTransform == null) return;
            confirmButtonTransform.GetComponent<Button>().onClick.Invoke();
        }

        private UnityEventsListener hudEnabledListener;
        private void InitCanvas()
        {
            Transform MainWindowUI = SuperController.singleton.MonitorModeAuxUI.gameObject.transform.parent;
            if (MainWindowUI == null)
            {
                SuperController.LogError("MainWindowUI not found.");
                return;
            }
            
            screenCanvas = MainWindowUI.GetChild(0);
            if (screenCanvas == null)
            {
                SuperController.LogError("Panel not found.");
                return;
            }
            hudCanvas = GameObject.Find("SceneAtoms/CoreControl/WorldScaleAdjust/HUD/LowerHUDPivot/LowerHUDFlip/Scene Control Canvas").transform;
            buttonGroup = new GameObject("BL_ButtonGroup");
            buttonGroup.transform.SetParent(screenCanvas, false);
            var rt = buttonGroup.AddComponent<RectTransform>();
            rt.anchorMax = new Vector2(0,0);
            rt.anchorMin = new Vector2(0, 0);
            rt.offsetMax = new Vector2(0, 0);
            rt.offsetMin = rt.offsetMax;
            buttonGroup.gameObject.layer = screenCanvas.gameObject.layer;
            PositionCanvas(1, 1);
            hudEnabledListener = hudCanvas.gameObject.AddComponent<UnityEventsListener>();
            hudEnabledListener.onDisabled.AddListener(() =>
            {
                if (!keepHudCanvasWhenUIClosed.val || screenCanvasActive || poses.Count == 0) return;
                ParentHudPanelToWorldCo().Start();
            });
            hudEnabledListener.onEnabled.AddListener(() =>
            {
                if (!keepHudCanvasWhenUIClosed.val || screenCanvasActive || poses.Count == 0) return;
                var canvas = buttonGroup.GetComponent<Canvas>();
                SuperController.singleton.RemoveCanvas(canvas);
                Destroy(buttonGroup.GetComponent<GraphicRaycaster>());
                Destroy(canvas);
                SelectCanvas("HUD (VR)");
            });
        }
        
        private static void ParentHudPanelToWorld()
        {
            buttonGroup.transform.parent = null;
            buttonGroup.SetActive(true);
            buttonGroup.AddComponent<GraphicRaycaster>();
            var canvas = buttonGroup.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            SuperController.singleton.AddCanvas(canvas);
        }

        private static IEnumerator ParentHudPanelToWorldCo()
        {
            yield return null;
            ParentHudPanelToWorld();
        }

        private static Transform screenCanvas;
        private static Transform hudCanvas;

        private JSONStorableString canvasInfo = new JSONStorableString("",
            "The canvas selected here doesn't matter. The canvas shown on load depends if the user is in VR or not.\n" +
            "The world canvas is only shown if you toggled it on upon saving.\n" +
            "You can layout any of the three canvasses with individual settings. Those will be restored for the user.");
        
        private JSONStorableString hudCanvasInfo = new JSONStorableString("",
            "The button panel to the right is just a preview for you to inspect and design in desktop mode.\n" +
            "It will be mirrored and attached to the left side of the main HUD in VR, right where the button array currently is.\n" +
            "The scale relative to the UI will be the same.");

        private static void SelectCanvas(string val)
        {
            if (poses.Count == 0)
            {
                "Add a pose first to see a canvas.".Print();
                return;
            }
            CanvasSettings.allowUpdates = false;
            switch (val)
            {
                case "Screen (Desktop)":
                {
                    screenCanvasActive = true;
                    if(showScreeenAndHUDCanvas.val) buttonGroup.gameObject.SetActive(true);
                    currentCanvas = 0;
                    buttonGroup.transform.localScale = Vector3.one;
                    buttonGroup.gameObject.layer = screenCanvas.gameObject.layer;
                    buttonGroup.transform.SetParent(screenCanvas, false);
                    buttonGroup.transform.localRotation = Quaternion.identity;
                    canvasSettings[currentCanvas].Apply();
                    PositionCanvas(1, 1);
                    // if(worldCanvas) SuperController.singleton.RemoveCanvas(worldCanvas.GetComponent<Canvas>());
                    if(worldCanvasPosSlider) worldCanvasPosSlider.SetVisible(false);
                    if(worldCanvasRotSlider) worldCanvasRotSlider.SetVisible(false);
                    if(worldCanvasFacePlayerToggle) worldCanvasFacePlayerToggle.SetVisible(false);
                    break;
                }
                case "HUD (VR)":
                {
                    screenCanvasActive = false;
                    if(showScreeenAndHUDCanvas.val) buttonGroup.gameObject.SetActive(true);
                    currentCanvas = 1;
                    buttonGroup.transform.localScale = Vector3.one;
                    buttonGroup.gameObject.layer = hudCanvas.gameObject.layer;
                    buttonGroup.transform.SetParent(hudCanvas, false);
                    buttonGroup.transform.SetAsFirstSibling();
                    canvasSettings[currentCanvas].Apply();
                    if(SuperController.singleton.isOpenVR || SuperController.singleton.isOVR)
                    {
                        buttonGroup.transform.localRotation = Quaternion.AngleAxis(-30f, Vector3.right) *
                                                              Quaternion.AngleAxis(-30, Vector3.up);
                        PositionCanvas(0, 5);
                    }
                    else
                    {
                        PositionCanvas(1120, 5);
                        buttonGroup.transform.localScale = new Vector3(-1f, 1f, 1f);
                        buttonGroup.transform.localRotation = Quaternion.identity;
                    }
                    LayoutPoseButtons(Story.currentLevel);
                    // if(worldCanvas) SuperController.singleton.RemoveCanvas(worldCanvas.GetComponent<Canvas>());
                    if(worldCanvasPosSlider) worldCanvasPosSlider.SetVisible(false);
                    if(worldCanvasRotSlider) worldCanvasRotSlider.SetVisible(false);
                    if(worldCanvasFacePlayerToggle) worldCanvasFacePlayerToggle.SetVisible(false);
                    break;
                }
                case "World":
                {
                    currentCanvas = 2;
                    canvasSettings[currentCanvas].Apply();
                    buttonGroup.gameObject.SetActive(false);
                    showWorldCanvas.val = true;
                    if(worldCanvasPosSlider) worldCanvasPosSlider.SetVisible(true);
                    if(worldCanvasRotSlider) worldCanvasRotSlider.SetVisible(true);
                    if(worldCanvasFacePlayerToggle) worldCanvasFacePlayerToggle.SetVisible(true);
                    break;
                }
            }
            CanvasSettings.allowUpdates = true;
            if(singleton.UITransform.gameObject.activeSelf && currentTab == 9) singleton.SelectTab(currentTab);
        }

        private void GetNextShot()
        {
            Pose pose;
            CamAngle angle;
            if (currentPose.currentCam.id < currentPose.camAngles.Count - 1)
            {
                pose = currentPose;
                angle = currentPose.camAngles[currentPose.currentCam.id + 1];
            }
            else if (currentPose.id < poses.Count - 1)
            {
                pose = poses[currentPose.id + 1];
                angle = pose.camAngles[0];
            }
        }

        // public static void LayoutPoseButtons(StoryLevel level)
        // {
        //     if (level == null)
        //     {
        //         LayoutPoseButtons();
        //         return;
        //     }
        //     if(showPoseButtons.val)
        //     {
        //         maxRows.max = (int)(2f * Screen.height / (buttonHeight + buttonSpacing.val));
        //         maxRows.min = Mathf.Ceil(.5f * (level.maxId - level.minId) / Screen.width *
        //                                  (buttonWidth + buttonSpacing.val));
        //         int columnCount = (int)Math.Ceiling((float)(level.maxId - level.minId + 1) / maxRows.val);
        //         int row = 0, column;
        //         for (int i = 0; i < level.minId; i++)
        //         {
        //             poses[i].buttonRT.gameObject.SetActive(false);
        //         }
        //
        //         for (int i = level.maxId + 1; i < poses.Count; i++)
        //         {
        //             poses[i].buttonRT.gameObject.SetActive(false);
        //         }
        //
        //         for (var i = 0; i <= level.maxId - level.minId; i++)
        //         {
        //             var rt = poses[i + level.minId].buttonRT;
        //             rt.gameObject.SetActive(true);
        //             column = i % columnCount;
        //             row = i / columnCount;
        //             rt.offsetMax = new Vector2(-(columnCount - column - 1) * (buttonWidth + buttonSpacing.val),
        //                 -row * (buttonHeight + buttonSpacing.val));
        //             rt.offsetMin = rt.offsetMax - new Vector2(buttonWidth, buttonHeight);
        //         }
        //         level.buttonPanel.offsetMax = level.buttonPanel.offsetMin = poses[level.minId].buttonRT.offsetMax - new Vector2(buttonWidth, 0);
        //     }
        //     else
        //     {
        //         level.buttonPanel.offsetMax = Vector2.zero;
        //     }
        //     level.SyncButtons();
        //     // if(currentPose != null) level.buttons[2].rtMiddle.gameObject.SetActive(currentPose.camAngles.Count > 1);
        // }
        
        public static void LayoutPoseButtons(StoryLevel level)
        {
            if (level == null)
            {
                LayoutPoseButtons();
                return;
            }
            if(showScreenButtons.val)
            {
                float buttonW, buttonH;
                CanvasSettings canvas = canvasSettings[currentCanvas];
                buttonW = buttonH = canvas.buttonSize.val;
                var spacing = canvas.buttonSpacing.val;
                if (screenCanvasActive)
                {
                    maxRows.max = (int)(2f * Screen.height / (buttonH + spacing));
                    maxRows.min = Mathf.Ceil(.5f * (level.maxId - level.minId) / Screen.width *
                                             (buttonW + spacing));
                    int columnCount = (int)Math.Ceiling((float)(level.maxId - level.minId + 1) / canvas.maxRows.val);
                    int row = 0, column;
                    for (int i = 0; i < level.minId; i++)
                    {
                        poses[i].buttonRT.gameObject.SetActive(false);
                    }

                    for (int i = level.maxId + 1; i < poses.Count; i++)
                    {
                        poses[i].buttonRT.gameObject.SetActive(false);
                    }

                    for (var i = 0; i <= level.maxId - level.minId; i++)
                    {
                        var rt = poses[i + level.minId].buttonRT;
                        rt.gameObject.SetActive(true);
                        column = i % columnCount;
                        row = i / columnCount;
                        rt.offsetMax = new Vector2(-(columnCount - column - 1) * (buttonW + spacing),
                            -row * (buttonH + spacing));
                        rt.offsetMin = rt.offsetMax - new Vector2(buttonW, buttonH);
                    }

                    level.buttonPanel.offsetMax = level.buttonPanel.offsetMin =
                        poses[level.minId].buttonRT.offsetMax - new Vector2(buttonW, 0);
                }
                else
                {
                    int columnCount = (int)Math.Ceiling((float)(level.maxId - level.minId + 1) / canvas.maxRows.val);
                    var totalHeight = ((level.maxId - level.minId) / columnCount+1) * (buttonH + spacing);
                    int row = 0, column;
                    for (int i = 0; i < level.minId; i++)
                    {
                        poses[i].buttonRT.gameObject.SetActive(false);
                    }

                    for (int i = level.maxId + 1; i < poses.Count; i++)
                    {
                        poses[i].buttonRT.gameObject.SetActive(false);
                    }

                    for (var i = 0; i <= level.maxId - level.minId; i++)
                    {
                        var rt = poses[i + level.minId].buttonRT;
                        rt.gameObject.SetActive(true);
                        column = i % columnCount;
                        row = i / columnCount;
                        rt.offsetMax = new Vector2(-(columnCount - column - 1) * (buttonW + spacing),
                            totalHeight-row * (buttonH + spacing));
                        rt.offsetMin = rt.offsetMax - new Vector2(buttonW, buttonH);
                    }
                    if(!hudLevelNavOnTop.val)
                    {
                        level.buttonPanel.offsetMax = level.buttonPanel.offsetMin =
                            poses[level.minId + row * columnCount].buttonRT.offsetMax - new Vector2(buttonW, -22);
                    }
                    else
                    {
                        if (columnCount % 2 == 0)
                        {
                            level.buttonPanel.offsetMax = level.buttonPanel.offsetMin = 
                                poses[level.minId + columnCount / 2].buttonRT.offsetMin + new Vector2(185, 250+buttonW);
                        }
                        else
                        {
                            level.buttonPanel.offsetMax = level.buttonPanel.offsetMin = 
                                poses[level.minId + columnCount / 2].buttonRT.offsetMin + new Vector2(185+.5f*buttonW, 250+buttonW);
                        }
                    }
                }
            }
            else
            {
                if(screenCanvasActive) level.buttonPanel.offsetMax = Vector2.zero;
                else level.buttonPanel.offsetMax = level.buttonPanel.offsetMin = new Vector2(-400, -800);
            }
            if (worldCanvas.active)
            {
                worldCanvas.LayoutButtons(level);
            }
            level.SyncButtons();
            
            // if(currentPose != null) level.buttons[2].rtMiddle.gameObject.SetActive(currentPose.camAngles.Count > 1);
        }

        
        public static void LayoutPoseButtons()
        {
            CanvasSettings canvas = canvasSettings[currentCanvas];
            float buttonH, buttonW;
            buttonW = buttonH = canvas.buttonSize.val;
            var spacing = canvas.buttonSpacing.val;
            int columnCount = (int)Math.Ceiling((float)poses.Count / canvas.maxRows.val);
            int row = 0, column = 0;
            if (screenCanvasActive)
            {
                maxRows.max = (int)(2f * Screen.height / (buttonH + spacing));
                maxRows.min = Mathf.Ceil(.5f * poses.Count / Screen.width * (buttonW + spacing));
                for (var i = 0; i < poses.Count; i++)
                {
                    var rt = poses[i].buttonRT;
                    if (!rt.gameObject.activeSelf) rt.gameObject.SetActive(true);
                    column = i % columnCount;
                    row = i / columnCount;
                    rt.offsetMax = new Vector2(-(columnCount - column - 1) * (buttonW + spacing),
                        -row * (buttonH + spacing));
                    rt.offsetMin = rt.offsetMax - new Vector2(buttonW, buttonH);
                }
            }
            else
            {
                
                var totalHeight = ((poses.Count - 1) / columnCount+1) * (buttonH + spacing);
                for (var i = 0; i < poses.Count; i++)
                {
                    var rt = poses[i].buttonRT;
                    if (!rt.gameObject.activeSelf) rt.gameObject.SetActive(true);
                    column = i % columnCount;
                    row = i / columnCount;
                    rt.offsetMax = new Vector2(-(columnCount - column - 1) * (buttonW + spacing),
                        totalHeight-row * (buttonH + spacing));
                    rt.offsetMin = rt.offsetMax - new Vector2(buttonW, buttonH);
                }
            }

            if (worldCanvas.active)
            {
                worldCanvas.LayoutButtons();
            }
        }

        

        public static void TogglePoseButtons()
        {
            showScreenButtons.val = !showScreenButtons.val;
            if(!showScreenButtons.val)
            {
                for (var i = 0; i < poses.Count; i++)
                {
                    var rt = poses[i].buttonRT;
                    if (rt.gameObject.activeSelf) rt.gameObject.SetActive(false);
                }
                
                var level = Story.currentLevel;
                if(level == null) return;
                if(screenCanvasActive) level.buttonPanel.offsetMax = Vector2.zero;
                else level.buttonPanel.offsetMax = new Vector2(0, 225);
            }
            else
            {
                LayoutPoseButtons(Story.currentLevel);
            }
        }
        
        private static void PositionCanvas(float width, float height)
        {
            RectTransform rt = buttonGroup.GetComponent<RectTransform>();
            switch (currentCanvas)
            {
                case 0:
                {
                    rt.anchorMax = new Vector2(0,0);
                    rt.anchorMin = new Vector2(0, 0);
                    // var spacing = canvasSettings[0].buttonSpacing.val;
                    SuperController.singleton.MonitorUI.position = new Vector3(0f, 0f,  -8.5f);
                    rt.anchoredPosition = new Vector2((width) * Screen.width,
                        (height) * Screen.height) *2f;
                    LayoutPoseButtons(Story.currentLevel);
                    break;
                }
                case 1:
                {
                    rt.anchorMax = new Vector2(0,1);
                    rt.anchorMin = new Vector2(0, 1);
                    // rt.offsetMax = rt.offsetMin = new Vector2(0,50);
                    rt.anchoredPosition = new Vector2(width, height);
                    LayoutPoseButtons(Story.currentLevel);
                    break;
                }
                // case 2:
                // {
                //     rt.anchoredPosition = new Vector2(width, height);
                //     break;
                // }
            }
        }
        
        // public Dictionary<Rigidbody, IEnumerable<FreeControllerV3>> neighbors = new Dictionary<Rigidbody, IEnumerable<FreeControllerV3>>();
        // public Dictionary<Rigidbody, IEnumerable<FreeControllerV3>> children = new Dictionary<Rigidbody, IEnumerable<FreeControllerV3>>();
        // private void GetNeighbors()
        // {
        //     foreach (var target in forceTargets)
        //     {
        //         switch (target.name)
        //         {
        //             case "hip":
        //             {
        //                 neighbors[target] = containingAtom.freeControllers.Where(x =>
        //                     x.name.Contains("Thigh")||
        //                     x.name.Contains("abdomen")||
        //                     x.name.Contains("pelvis"));
        //                 break;
        //             }
        //             case "lThigh":
        //             {
        //                 neighbors[target] = containingAtom.freeControllers.Where(x =>
        //                     x.name.Contains("rThigh")||
        //                     x.name.Contains("abdomen")||
        //                     x.name.Contains("pelvis")||
        //                     x.name.Contains("hip"));
        //                 children[target] = containingAtom.freeControllers.Where(x =>
        //                     x.name.Contains("lKnee"));
        //                 break;
        //             }
        //             case "rThigh":
        //             {
        //                 neighbors[target] = containingAtom.freeControllers.Where(x =>
        //                     x.name.Contains("lThigh")||
        //                     x.name.Contains("abdomen")||
        //                     x.name.Contains("pelvis")||
        //                     x.name.Contains("hip"));
        //                 children[target] = containingAtom.freeControllers.Where(x =>
        //                     x.name.Contains("rKnee"));
        //                 break;
        //             }
        //             case "lShin":
        //             {
        //                 children[target] = containingAtom.freeControllers.Where(x =>
        //                     x.name.Contains("lFoot"));
        //                 break;
        //             }
        //             case "rShin":
        //             {
        //                 children[target] = containingAtom.freeControllers.Where(x =>
        //                     x.name.Contains("rFoot"));
        //                 break;
        //             }
        //             case "chest":
        //             {
        //                 neighbors[target] = children[target] = containingAtom.freeControllers.Where(x =>
        //                     x.name.Contains("Arm"));
        //                 break;
        //             }
        //             case "lForeArm":
        //             {
        //                 children[target] = containingAtom.freeControllers.Where(x =>
        //                     x.name.Contains("lHand"));
        //                 break;
        //             }
        //             case "rForeArm":
        //             {
        //                 children[target] = containingAtom.freeControllers.Where(x =>
        //                     x.name.Contains("rHand"));
        //                 break;
        //             }
        //         }
        //     }
        // }
        
        public static UIDynamicBubbleItem CreateDialogUIItem(Dialog dialog, bool rightSide = false)
		{
			if (bubbleUIItemPrefab == null)
			{
				bubbleUIItemPrefab = new GameObject("DynamicDialogItem");
				bubbleUIItemPrefab.SetActive(false);
				RectTransform rt = bubbleUIItemPrefab.AddComponent<RectTransform>();
				rt.anchorMax = new Vector2(0, 1);
				rt.anchorMin = new Vector2(0, 1);
				rt.offsetMax = new Vector2(535, -500);
				rt.offsetMin = new Vector2(10, -600);
				LayoutElement le = bubbleUIItemPrefab.AddComponent<LayoutElement>();
				le.flexibleWidth = 1;
				le.minHeight = 150;
				le.minWidth = 350;
				le.preferredHeight = 150;
				le.preferredWidth = 500;

				RectTransform backgroundTransform = singleton.manager.configurableScrollablePopupPrefab.transform.Find("Background") as RectTransform;
				backgroundTransform = Instantiate(backgroundTransform, bubbleUIItemPrefab.transform);
				backgroundTransform.name = "Background";
				backgroundTransform.anchorMax = new Vector2(1, 1);
				backgroundTransform.anchorMin = new Vector2(0, 0);
				backgroundTransform.offsetMax = new Vector2(0, 0);
				backgroundTransform.offsetMin = new Vector2(0, 0);

				RectTransform buttonTransform = singleton.manager.configurableScrollablePopupPrefab.transform.Find("Button") as RectTransform;
                buttonTransform = Instantiate(buttonTransform, bubbleUIItemPrefab.transform);
                buttonTransform.name = "Button";
                buttonTransform.anchorMax = new Vector2(1, 1);
                buttonTransform.anchorMin = new Vector2(0, 0);
                buttonTransform.offsetMax = new Vector2(-50, 0);
                buttonTransform.offsetMin = new Vector2(160, 100);
                var configureButton = buttonTransform.GetComponent<Button>();
                var buttonText = buttonTransform.Find("Text").GetComponent<Text>();
                
                buttonTransform = Instantiate(buttonTransform, bubbleUIItemPrefab.transform);
                buttonTransform.name = "Button";
                buttonTransform.anchorMax = new Vector2(0, 1);
                buttonTransform.anchorMin = new Vector2(0, 1);
                buttonTransform.offsetMax = new Vector2(160, 0);
                buttonTransform.offsetMin = new Vector2(120f, -50);
                var copyButton = buttonTransform.GetComponent<Button>();
                var copyButtonText = buttonTransform.Find("Text").GetComponent<Text>();
                copyButtonText.text = "<b>C</b>";
                copyButtonText.fontSize = 28;

                buttonTransform = Instantiate(buttonTransform, bubbleUIItemPrefab.transform);
                buttonTransform.name = "Button";
                buttonTransform.anchorMax = new Vector2(0, 1);
                buttonTransform.anchorMin = new Vector2(0, 1);
                buttonTransform.offsetMax = new Vector2(120, 0);
                buttonTransform.offsetMin = new Vector2(80, -50);
                var occurenceButton = buttonTransform.GetComponent<Button>();
                var occurenceText = buttonTransform.Find("Text").GetComponent<Text>();
                occurenceText.fontSize = 32;
                occurenceText.alignment = TextAnchor.MiddleCenter;
                
                buttonTransform = Instantiate(buttonTransform, bubbleUIItemPrefab.transform);
                buttonTransform.name = "Button";
                buttonTransform.anchorMax = new Vector2(0, 1);
                buttonTransform.anchorMin = new Vector2(0, 1);
                buttonTransform.offsetMax = new Vector2(80, 0);
                buttonTransform.offsetMin = new Vector2(40, -50);
                var typeButton = buttonTransform.GetComponent<Button>();
                var typeText = buttonTransform.Find("Text").GetComponent<Text>();
                typeText.text = "<b>S</b>";
                typeText.fontSize = 28;
                
                buttonTransform = Instantiate(buttonTransform, bubbleUIItemPrefab.transform);
                buttonTransform.name = "Button";
                buttonTransform.anchorMax = new Vector2(0, 1);
                buttonTransform.anchorMin = new Vector2(0, 1);
                buttonTransform.offsetMax = new Vector2(40, 0);
                buttonTransform.offsetMin = new Vector2(0, -50);
                var personButton = buttonTransform.GetComponent<Button>();
                var personText = buttonTransform.Find("Text").GetComponent<Text>();
                personText.text = "<b>P</b>";
                personText.fontSize = 28;
                
                buttonTransform = Instantiate(buttonTransform, bubbleUIItemPrefab.transform);
                buttonTransform.name = "Button";
                buttonTransform.anchorMax = new Vector2(0, 1);
                buttonTransform.anchorMin = new Vector2(0, 1);
                buttonTransform.offsetMax = new Vector2(40, -50);
                buttonTransform.offsetMin = new Vector2(0, -90);
                var increaseDelayButton = buttonTransform.GetComponent<Button>();
                var text = buttonTransform.Find("Text").GetComponent<Text>();
                text.text = "+";
                text.fontSize = 28;
                
                buttonTransform = Instantiate(buttonTransform, bubbleUIItemPrefab.transform);
                buttonTransform.name = "Button";
                buttonTransform.anchorMax = new Vector2(0, 1);
                buttonTransform.anchorMin = new Vector2(0, 1);
                buttonTransform.offsetMax = new Vector2(40, -120);
                buttonTransform.offsetMin = new Vector2(0, -160);
                var decreaseDelayButton = buttonTransform.GetComponent<Button>();
                text = buttonTransform.Find("Text").GetComponent<Text>();
                text.text = "-";
                text.fontSize = 28;

                RectTransform t = Instantiate(buttonTransform, bubbleUIItemPrefab.transform);
                t.name = "Text";
                t.anchorMax = new Vector2(0, 1);
                t.anchorMin = new Vector2(0, 1);
                t.offsetMax = new Vector2(40, -90);
                t.offsetMin = new Vector2(0, -120);
                Button button = t.GetComponent<Button>();
                Destroy(button);
                var delayInfo = t.Find("Text").GetComponent<Text>();
                delayInfo.fontSize = 20;
                
				buttonTransform = Instantiate(buttonTransform, bubbleUIItemPrefab.transform);
				buttonTransform.name = "Button";
				buttonTransform.anchorMax = new Vector2(1, 1);
				buttonTransform.anchorMin = new Vector2(1, 0);
				buttonTransform.offsetMax = new Vector2(0, 0);
				buttonTransform.offsetMin = new Vector2(-50, 100);
				var deleteButton = buttonTransform.GetComponent<Button>();
                var deleteButtonText = buttonTransform.Find("Text").GetComponent<Text>();
                deleteButtonText.fontSize = 28;
                deleteButtonText.text = "<b>X</b>";
                deleteButtonText.color = Color.white;
                buttonTransform.GetComponent<Image>().color = severeWarningColor;

                RectTransform speechTransform = singleton.manager.configurableTextFieldPrefab.transform as RectTransform;
				speechTransform = Instantiate(speechTransform, bubbleUIItemPrefab.transform);
				speechTransform.name = "Text";
				speechTransform.anchorMax = new Vector2(1, 1);
				speechTransform.anchorMin = new Vector2(0, 0);
				speechTransform.offsetMax = new Vector2(0, -50);
				speechTransform.offsetMin = new Vector2(40, -10);
                var textfield = speechTransform.GetComponent<UIDynamicTextField>();
                textfield.UItext.alignment = TextAnchor.UpperCenter;
                var inputField = speechTransform.gameObject.AddComponent<InputField>();
                // var contentSizeFitter = speechTransform.gameObject.AddComponent<ContentSizeFitter>();
                // contentSizeFitter.verticalFit = ContentSizeFitter.FitMode.MinSize;
                // contentSizeFitter.horizontalFit = ContentSizeFitter.FitMode.MinSize;
                inputField.textComponent = textfield.UItext;
                inputField.textComponent.horizontalOverflow = HorizontalWrapMode.Wrap;
                inputField.textComponent.verticalOverflow = VerticalWrapMode.Truncate;
                // inputField.contentType = InputField.ContentType.Custom;
                inputField.lineType = InputField.LineType.MultiLineNewline;


                UIDynamicBubbleItem uid = bubbleUIItemPrefab.AddComponent<UIDynamicBubbleItem>();
				uid.speech = textfield.UItext;
                uid.inputField = inputField;
				uid.deleteButton = deleteButton;
                uid.configureButton = configureButton;
                uid.configureButtonLabel = configureButton.GetComponentInChildren<Text>();
                uid.copyButton = copyButton;
                uid.occurenceButton = occurenceButton;
                uid.typeButton = typeButton;
                uid.typeText = typeText;
                uid.personButton = personButton;
                uid.increaseDelayButton = increaseDelayButton;
                uid.decreaseDelayButton = decreaseDelayButton;
                uid.delayInfo = delayInfo;
                // uid.occurenceColor = occurenceColor;
            }

			{
				Transform t = singleton.CreateUIElement(bubbleUIItemPrefab.transform, rightSide);
                UIDynamicBubbleItem uid = t.gameObject.GetComponent<UIDynamicBubbleItem>();
                uid.rightSide = rightSide;
                t.gameObject.SetActive(true);
                dialog.RegisterUid(uid);
                return uid;
            }
		}

        public static UIDynamicStoryLevel CreateStoryLevelUid(StoryLevel level, bool rightSide)
        {
            if (StoryLevel.uidPrefab == null) StoryLevel.CreateUidPrefab();
            Transform t = singleton.CreateUIElement(StoryLevel.uidPrefab.transform, rightSide);
            UIDynamicStoryLevel uid = t.gameObject.GetComponent<UIDynamicStoryLevel>();
            t.gameObject.SetActive(true);
            level.RegisterUid(uid);
            UIElements.Add(uid);
            uid.slider.wholeNumbers = true;
            uid.configureButton.onClick.AddListener(() =>
            {
                level.SetActive();
                level.CreateConfigureUI();
            });
            uid.playButton.onClick.AddListener(() => {
                if(Story.currentLevel == level)
                {
                    level.active = false;
                    Story.currentLevel = null;
                    WorldCanvas.levelNav?.gameObject.SetActive(false);
                    LayoutPoseButtons();
                }
                else
                {
                    level.SetActive();
                    if (Story.applyFirstPoseOnLevelEnter.val) poses[level.minId].Apply();
                }
            });
            uid.deleteButton.onClick.AddListener(() => Story.DeleteLevel(level));
            uid.slider.onValueChanged.AddListener((u, v) => level.SetPoseRange((int)u, (int)v));
            uid.increaseHighButton.onClick.AddListener(() =>
            {
                uid.slider.SetValuesClamped(uid.slider.minValue, uid.slider.maxValue + 1f);
                level.SetActive();
                Story.Sort();
                // LayoutPoseButtons(level);
            });
            uid.decreaseHighButton.onClick.AddListener(() =>
            {
                uid.slider.SetValuesClamped(uid.slider.minValue, uid.slider.maxValue - 1f);
                level.SetActive();
                Story.Sort();
                // LayoutPoseButtons(level);
            });
            uid.increaseLowButton.onClick.AddListener(() =>
            {
                uid.slider.SetValuesClamped(uid.slider.minValue + 1f, uid.slider.maxValue);
                level.SetActive();
                Story.Sort();
                // LayoutPoseButtons(level);
            });
            uid.decreaseLowButton.onClick.AddListener(() =>
            {
                uid.slider.SetValuesClamped(uid.slider.minValue - 1f, uid.slider.maxValue);
                level.SetActive();
                Story.Sort();
                // LayoutPoseButtons(level);
            });
            uid.slider.onBeginDrag.AddListener(() =>
            {
                level.SetActive();
                LayoutPoseButtons();
                Pose.fadeColor = Color.white;
                Pose.fadeColor.a = 0.5f * buttonTransparency.val;
                var currentPoseId = currentPose?.id ?? -1;
                for (int i = 0; i < poses.Count; i++)
                {
                    if(i == currentPoseId) continue;
                    if(i < level.minId || i > level.maxId) poses[i].uiButton.button.image.color = Pose.fadeColor;
                    else poses[i].uiButton.button.image.color = Pose.deselectedColor;
                }
            });
            uid.slider.onEndDrag.AddListener(() =>
            {
                LayoutPoseButtons(level);
                for (int i = 0; i < poses.Count; i++)
                {
                    if(i == currentPose?.id) continue;
                    poses[i].uiButton.button.image.color = Pose.deselectedColor;
                }
                Story.Sort();
            });
            uid.slider.onValueChangedDuringDrag.AddListener((u, v) =>
            {
                var currentPoseId = currentPose?.id ?? -1;
                for (int i = 0; i < poses.Count; i++)
                {
                    if(i == currentPoseId) continue;
                    if(i < level.minId || i > level.maxId) poses[i].uiButton.button.image.color = Pose.fadeColor;
                    else poses[i].uiButton.button.image.color = Pose.deselectedColor;
                }
            });
            uid.highInputField.onEndEdit.AddListener(val =>
            {
                int i;
                if (int.TryParse(val, out i))
                {
                    uid.slider.SetValuesClamped(level.minId, i);
                }
                level.SetActive();
                Story.Sort();
                // LayoutPoseButtons(level);
            });
            uid.lowInputField.onEndEdit.AddListener(val =>
            {
                int i;
                if (int.TryParse(val, out i))
                {
                    uid.slider.SetValuesClamped(i, level.maxId);
                }
                level.SetActive();
                Story.Sort();
                // LayoutPoseButtons(level);
            });
            return uid;
        }

        public static UIDynamicSlapItem CreateUIDynamicSlapItem(Slap slap, bool rightSide = false)
        {
            if (Slap.slapUidPrefab == null) Slap.CreateSlapUidPrefab();
            Transform t = singleton.CreateUIElement(Slap.slapUidPrefab.transform, rightSide);
            UIDynamicSlapItem uid = t.gameObject.GetComponent<UIDynamicSlapItem>();
            t.gameObject.SetActive(true);
            UIElements.Add(uid);
            uid.activeToggle.onValueChanged.AddListener(val =>
            {
                uid.toggleText.text = val ? "✓" : "";
                slap.enabledJ.val = val;
            });
            uid.sideToggle.onValueChanged.AddListener(val =>
            {
                uid.sideText.text = val ? "<b>L</b>" : "<b>R</b>";
                slap.handChooser.val = val? "Left" : "Right";
            });
            uid.deleteButton.onClick.AddListener(() =>
            {
                slap.pose.slaps.Remove(slap);
                Destroy(slap);
                singleton.RemoveUIElement(uid);
            });
            uid.configureButton.onClick.AddListener(slap.CreateConfigureUI);
            uid.personButton.onClick.AddListener(() =>
            {
                int id = persons.IndexOf(slap.person);
                slap.personChooser.val = persons[(id+1) % persons.Count].atom.uid;
                uid.configureButtonText.text = slap.person.uid;
            });
            slap.RegisterUid(uid);
            return uid;
        }
        
        public static UIDynamicGazeItem CreateUIDynamicGazeTarget(Gaze.ObjectTarget target, bool rightSide = false, List<object> UIElements = null)
        {
            if (Gaze.targetUidPrefab == null) Gaze.CreateGazeUidPrefab();
            Transform t = singleton.CreateUIElement(Gaze.targetUidPrefab.transform, rightSide);
            UIDynamicGazeItem uid = t.gameObject.GetComponent<UIDynamicGazeItem>();
            // t.gameObject.SetActive(true);
            uid.label.text = target.atom.uid;
            uid.toggleText.text = target.enabled.val? "✓" : "";
            uid.activeToggle.onValueChanged.AddListener(val =>
            {
                uid.toggleText.text = val ? "✓" : "";
                target.enabled.val = val;
            });
            uid.deleteButton.onClick.AddListener(() =>
            {
                Gaze.DeregisterAtom(target.atom);
                singleton.RemoveUIElement(uid);
            });
            target.uid = uid;
            if(UIElements != null) UIElements.Add(uid);
            return uid;
        }
        
        public static Transform MyCreateUIElement(Transform prefab, bool rightSide = false)
        {
            return singleton.CreateUIElement(prefab, rightSide);
        }

        public void RemoveUIElements(List<object> UIElements)
		{
			for (int i=0; i<UIElements.Count; ++i)
            {
                RemoveUIElement(UIElements[i]);
            }
            UIElements.Clear();
		}
        
        public void RemoveUIElement(object element)
		{
            if(element == null) return;
			if (element is JSONStorableParam)
			{
				JSONStorableParam jsp = element as JSONStorableParam;
				if (jsp is JSONStorableFloat)
					RemoveSlider(jsp as JSONStorableFloat);
				else if (jsp is JSONStorableBool)
					RemoveToggle(jsp as JSONStorableBool);
				else if (jsp is JSONStorableColor)
					RemoveColorPicker(jsp as JSONStorableColor);
				else if (jsp is JSONStorableString)
					RemoveTextField(jsp as JSONStorableString);
				else if (jsp is JSONStorableStringChooser)
				{
					// Workaround for VaM not cleaning its panels properly.
					JSONStorableStringChooser jssc = jsp as JSONStorableStringChooser;
					RectTransform popupPanel = jssc.popup?.popupPanel;
					RemovePopup(jssc);
					if (popupPanel != null)
						UnityEngine.Object.Destroy(popupPanel.gameObject);
				}
			}
			else if (element is UIDynamic)
			{
				UIDynamic uid = element as UIDynamic;
				if (uid is UIDynamicButton)
					RemoveButton(uid as UIDynamicButton);
                else if (uid is MyUIDynamicSlider)
                {
                    var uidSlider = uid as MyUIDynamicSlider;
                    leftUIElements.Remove(uidSlider.transform);
                    rightUIElements.Remove(uidSlider.transform);
                    Destroy(uidSlider.gameObject);
                }
                else if (uid is UIDynamicSlider)
					RemoveSlider(uid as UIDynamicSlider);
                else if (uid is UIDynamicColorPicker)
					RemoveColorPicker(uid as UIDynamicColorPicker);
				else if (uid is UIDynamicTextField)
					RemoveTextField(uid as UIDynamicTextField);
				else if (uid is UIDynamicPopup)
				{
					// Workaround for VaM not cleaning its panels properly.
					UIDynamicPopup uidp = uid as UIDynamicPopup;
					RectTransform popupPanel = uidp.popup?.popupPanel;
					RemovePopup(uidp);
					if (popupPanel != null)
						UnityEngine.Object.Destroy(popupPanel.gameObject);
				}
                else if (uid is MyUIDynamicToggle)
                {
                    var uidToggle = uid as MyUIDynamicToggle;
                    leftUIElements.Remove(uidToggle.transform);
                    rightUIElements.Remove(uidToggle.transform);
                    Destroy(uidToggle.gameObject);
                }
                else if (uid is UIDynamicToggle)
                    RemoveToggle(uid as UIDynamicToggle);
                else if (uid is UIDynamicV3Slider)
                {
                    var v3Slider = uid as UIDynamicV3Slider;
                    leftUIElements.Remove(v3Slider.transform);
                    rightUIElements.Remove(v3Slider.spacer.transform);
                    Destroy(v3Slider.spacer.gameObject);
                    Destroy(v3Slider.gameObject);
                }
                else if (uid is UIDynamicTabBar)
                {
	                var tabbar = uid as UIDynamicTabBar;
                    leftUIElements.Remove(tabbar.transform);
                    rightUIElements.Remove(tabbar.spacer.transform);
                    Destroy(tabbar.spacer.gameObject);
                    Destroy(tabbar.gameObject);
                }
                else if(uid is UIDynamicToggleArray)
                {
                    if (uid == null) return;
                    var toggleArray = uid as UIDynamicToggleArray;
                    leftUIElements.Remove(toggleArray.transform);
                    rightUIElements.Remove(toggleArray.spacer.transform);
                    DestroyImmediate(toggleArray.spacer.gameObject);
                    DestroyImmediate(uid.gameObject);
                }
                else
                {
                    RemoveSpacer(uid);
                }
			}
        }
    }
}
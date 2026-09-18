using System.Collections.Generic;
using System.Linq;
using SimpleJSON;

namespace CheesyFX
{
    public static class Story
    {
        public static StoryLevel currentLevel;
        public static List<StoryLevel> levels = new List<StoryLevel>();
        // public static List<StoryLevel> sortedLevels = new List<StoryLevel>();
        private static bool uiOpen;
        public static JSONStorableAction nextLevel = new JSONStorableAction("Go to next Level", Next);
        public static JSONStorableAction previousLevel = new JSONStorableAction("Go to previous Level", Previous);
        public static JSONStorableAction firstLevel = new JSONStorableAction("Go to first Level", FirstLevel);
        public static JSONStorableAction lastLevel = new JSONStorableAction("Go to last Level", LastLevel);

        public static JSONStorableBool applyFirstPoseOnLevelEnter = new JSONStorableBool("Apply First Pose On Level Enter", true);

        public static void Init()
        {
            nextLevel.RegisterWithKeybingings(PoseMe.keyBindings);
            previousLevel.RegisterWithKeybingings(PoseMe.keyBindings);
            firstLevel.RegisterWithKeybingings(PoseMe.keyBindings);
            lastLevel.RegisterWithKeybingings(PoseMe.keyBindings);
        }

        public static void CreateUI()
        {
            PoseMe.singleton.SetupButton("Add Level", false, () => AddLevel(), PoseMe.UIElements);
            CreateLevelUids();
            PoseMe.singleton.SetupButton("Sort Levels", true, SortAndRefreshUids, PoseMe.UIElements);
            applyFirstPoseOnLevelEnter.CreateUI(PoseMe.UIElements, true);
            PoseMe.singleton.SetupButton("Toggle Pose Buttons", true, PoseMe.TogglePoseButtons, PoseMe.UIElements);
            PoseMe.singleton.SetupButton("Previous Level", true, PreviousLevel, PoseMe.UIElements);
            PoseMe.singleton.SetupButton("Next Level", true, Next, PoseMe.UIElements);
            PoseMe.singleton.SetupButton("First Level", true, FirstLevel, PoseMe.UIElements);
            PoseMe.singleton.SetupButton("Last Level", true, LastLevel, PoseMe.UIElements);
        }
        
        public static StoryLevel AddLevel()
        {
            // if (PoseMe.poses.Count == 0)
            // {
            //     "Create some poses first.".Print();
            //     return;
            // }
            var level = new StoryLevel();
            levels.Add(level);
            SyncLevelButtons();
            PoseMe.CreateStoryLevelUid(level, false);
            level.SetActive();
            Sort();
            return level;
        }

        private static void SyncLevelButtons()
        {
            for (int i = 0; i < levels.Count; i++)
            {
                var level = levels[i];
                level.SyncButtons();
            }
        }

        public static void DeleteLevel(StoryLevel level)
        {
            levels.Remove(level);
            level.Destroy();
            if(level == currentLevel)
            {
                currentLevel = null;
                PoseMe.worldCanvas?.Sync();
                PoseMe.LayoutPoseButtons();
            }
            Sort();
        }

        public static void Sort()
        {
            levels.Sort((x,y) => x.minId.CompareTo(y.minId));
            for (int i = 0; i < levels.Count; i++)
            {
                var level = levels[i];
                level.buttons[0].SyncText();
            }
        }

        private static void SortAndRefreshUids()
        {
            Sort();
            for (int i = 0; i < levels.Count; i++)
            {
                var level = levels[i];
                // level.buttons[0].SetText();
                PoseMe.singleton.RemoveUIElement(level.uid);
            }
            CreateLevelUids();
        }

        public static void FirstLevel()
        {
            if(levels.Count == 0) return;
            var next = levels.Aggregate((x, y) => x.minId < y.minId ? x : y);
            if(next != null)
            {
                next.SetActive();
                if (applyFirstPoseOnLevelEnter.val) PoseMe.poses[next.minId].Apply();
            }
        }
        
        public static void LastLevel()
        {
            if(levels.Count == 0) return;
            var next = levels.Aggregate((x, y) => x.minId > y.minId ? x : y);
            if(next != null)
            {
                next.SetActive();
                if (applyFirstPoseOnLevelEnter.val) PoseMe.poses[next.minId].Apply();
            }
        }

        public static void NthLevel(int n)
        {
            
        }

        public static void Previous()
        {
            if (PoseMe.currentPose == null || currentLevel == null)
            {
                FirstLevel();
                return;
            }
            if (!applyFirstPoseOnLevelEnter.val || PoseMe.currentPose.id == currentLevel.minId) PreviousLevel();
            else PoseMe.poses[currentLevel.minId].Apply();
        }
        
        public static void PreviousLevel()
        {
            if(levels.Count == 0) return;
            StoryLevel next = null;
            if (currentLevel == null) FirstLevel();
            else
            {
                int minId = 0;
                for (int i = 0; i < levels.Count; i++)
                {
                    var level = levels[i];
                    if(level.minId >= currentLevel.minId) continue;
                    if (level.minId >= minId)
                    {
                        minId = level.minId;
                        next = level;
                    }
                }
                // minId.Print();
            }
            if(next != null)
            {
                next.SetActive();
                if (applyFirstPoseOnLevelEnter.val) PoseMe.poses[next.minId].Apply();
            }
        }

        public static void Next()
        {
            if(levels.Count == 0) return;
            StoryLevel next = null;
            if (currentLevel == null) FirstLevel();
            else
            {
                int minId = PoseMe.poses.Count - 1;
                for (int i = 0; i < levels.Count; i++)
                {
                    var level = levels[i];
                    if(level.minId <= currentLevel.minId) continue;
                    if (level.minId <= minId)
                    {
                        minId = level.minId;
                        next = level;
                    }
                }
                // minId.Print();
            }
            if(next != null)
            {
                next.SetActive();
                if (applyFirstPoseOnLevelEnter.val) PoseMe.poses[next.minId].Apply();
            }
        }

        // public static void RenameLevels()
        // {
        //     for (int i = 0; i < levels.Count; i++)
        //     {
        //         var level = levels[i];
        //         level.SetId(i);
        //     }
        // }

        public static void CreateLevelUids()
        {
            uiOpen = true;
            for (int i = 0; i < levels.Count; i++)
            {
                var level = levels[i];
                PoseMe.CreateStoryLevelUid(level, false);
            }

            if(currentLevel  != null) currentLevel.uid.playButton.image.color = PoseMe.navColor;
        }

        public static void SyncPoses()
        {
            for (int i = 0; i < levels.Count; i++)
            {
                levels[i].uid.slider.maxLimit = PoseMe.poses.Count - 1;
            }
        }

        public static void Destroy()
        {
            levels.ForEach(x => x.Destroy());
        }

        public static void Store(JSONClass jc)
        {
            var ja = new JSONArray();
            foreach (var level in levels)
            {
                ja.Add(level.Store());
            }
            jc["levels"] = ja;
        }
        
        public static void Load(JSONClass jc)
        {
            if(!jc.HasKey("levels")) return;
            var ja = jc["levels"];
            foreach (var levelData in ja.Childs)
            {
                // var level = AddLevel();
                var level = new StoryLevel();
                levels.Add(level);
                SyncLevelButtons();
                // PoseMe.CreateStoryLevelUid(level, false);
                // level.SetActive();
                level.Load(levelData.AsObject);
            }
            // if(WorldCanvas.levelNav) PoseMe.worldCanvas.Sync();
            // SortLevels();
        }
    }
}
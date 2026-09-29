using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using SimpleJSON;
using UnityEngine;

namespace Quest3TriggerUI
{
    // Discovery consumes scene data, never executes a trigger to probe its effect.
    internal sealed class PlaybackRoute
    {
        internal string AtomId, StorableId, Next, Previous;
        internal int States, ReachableNodes;
    }

    internal static class PlaybackFlowDiscovery
    {
        private static readonly string[,] Pairs = {
            { "NextState", "PreviousState" },
            { "NextStage", "PreviousStage" },
            { "Next", "Previous" }
        };

        private sealed class Edge
        {
            internal string Target, Action;
        }

        internal static List<PlaybackRoute> Find(JSONClass scene)
        {
            Dictionary<string, JSONNode> nodes = new Dictionary<string, JSONNode>();
            Dictionary<string, List<Edge>> edges = new Dictionary<string, List<Edge>>();
            Dictionary<string, HashSet<string>> incoming = new Dictionary<string, HashSet<string>>();
            JSONArray atoms = scene == null ? null : scene["atoms"] as JSONArray;
            List<PlaybackRoute> results = new List<PlaybackRoute>();
            if (atoms == null) return results;
            foreach (JSONNode atom in atoms.Childs)
            {
                string atomId = atom["id"];
                JSONArray storables = atom["storables"] as JSONArray;
                if (storables == null) continue;
                foreach (JSONNode storable in storables.Childs)
                {
                    string id = storable["id"];
                    if (string.IsNullOrEmpty(id)) continue;
                    string key = atomId + "\n" + id;
                    nodes[key] = storable;
                    List<Edge> list = new List<Edge>();
                    if (id.StartsWith("plugin#", StringComparison.Ordinal) || id == "Trigger")
                        ReadEdges(storable, atomId, list);
                    edges[key] = list;
                    foreach (Edge edge in list)
                    {
                        HashSet<string> actions;
                        if (!incoming.TryGetValue(edge.Target, out actions))
                        {
                            actions = new HashSet<string>(StringComparer.Ordinal);
                            incoming.Add(edge.Target, actions);
                        }
                        // Internal self references alone do not establish a user entry point.
                        if (edge.Target != key) actions.Add(edge.Action);
                    }
                }
            }
            foreach (KeyValuePair<string, JSONNode> node in nodes)
            {
                JSONArray states = node.Value["States"] as JSONArray;
                HashSet<string> actions;
                if (states == null || states.Count < 2 ||
                    !incoming.TryGetValue(node.Key, out actions)) continue;
                for (int i = 0; i < Pairs.GetLength(0); i++)
                {
                    if (!actions.Contains(Pairs[i, 0]) || !actions.Contains(Pairs[i, 1])) continue;
                    string[] ids = node.Key.Split('\n');
                    HashSet<string> visited = new HashSet<string>();
                    Queue<string> queue = new Queue<string>();
                    queue.Enqueue(node.Key);
                    while (queue.Count > 0)
                    {
                        string key = queue.Dequeue();
                        if (!visited.Add(key)) continue;
                        List<Edge> list;
                        if (!edges.TryGetValue(key, out list)) continue;
                        foreach (Edge edge in list)
                            if (nodes.ContainsKey(edge.Target)) queue.Enqueue(edge.Target);
                    }
                    results.Add(new PlaybackRoute {
                        AtomId = ids[0], StorableId = ids[1], Next = Pairs[i, 0],
                        Previous = Pairs[i, 1], States = states.Count,
                        ReachableNodes = visited.Count
                    });
                    break;
                }
            }
            return results;
        }

        private static void ReadEdges(JSONNode node, string sourceAtom, List<Edge> edges)
        {
            if (node == null || (node.AsObject == null && node.AsArray == null)) return;
            if (node.AsObject != null)
            {
                string receiver = node["receiver"];
                string action = node["receiverTargetName"];
                if (!string.IsNullOrEmpty(receiver) && !string.IsNullOrEmpty(action))
                {
                    string atom = node["receiverAtom"];
                    if (string.IsNullOrEmpty(atom)) atom = sourceAtom;
                    edges.Add(new Edge { Target = atom + "\n" + receiver, Action = action });
                }
            }
            // Timeline keyframes can be tens of MB. Treat their player as an
            // opaque downstream node; the native stage command executes it.
            JSONNode animation = node.AsObject == null ? null : node["Animation"];
            foreach (JSONNode child in node.Childs)
                if (!object.ReferenceEquals(child, animation)) ReadEdges(child, sourceAtom, edges);
        }
    }

    // PoseMe/Story-style scenes expose a hierarchy instead of serialized
    // numbered buttons: a level contains one or more poses, and each pose can
    // own several Timeline clips.  The native plugin actions are still the
    // single execution endpoint; this adapter only decides whether the next
    // command stays inside the current level or enters the adjacent level.
    internal sealed class HierarchicalPlayback
    {
        internal sealed class Level
        {
            internal int MinId, MaxId;
            internal bool Active;
            // PoseMe stores pose ids in a global list.  Keep the actual ids
            // for this level instead of assuming that every integer between
            // minId and maxId is present; a level can contain gaps after a
            // pose is removed or imported.
            internal readonly List<int> PoseIds = new List<int>();
        }

        internal string AtomId, StorableId;
        internal JSONStorable Controller;
        internal string NextLevelAction, PreviousLevelAction;
        internal string NextPoseAction, PreviousPoseAction;
        internal readonly List<Level> Levels = new List<Level>();
        private int _cursor = -1;
        // User level = a pose collection entry; user pose = its authored
        // camera entry carrying a Timeline action, not StoryLevel.Next().
        internal readonly List<List<string>> AngleStages = new List<List<string>>();
        internal bool HasAngleStages { get { return AngleStages.Count > 0; } }

        private void ReadAngleStages(JSONArray poses)
        {
            bool hasSequence = false;
            foreach (JSONNode pose in poses.Childs)
            {
                JSONArray cams = pose["cams"] as JSONArray;
                if (cams == null || cams.Count == 0) { AngleStages.Clear(); return; }
                List<string> actions = new List<string>();
                foreach (JSONNode cam in cams.Childs)
                {
                    string action = cam["timelineClip"];
                    if (string.IsNullOrEmpty(action)) { AngleStages.Clear(); return; }
                    actions.Add(action);
                }
                if (new HashSet<string>(actions).Count > 1) hasSequence = true;
                AngleStages.Add(actions);
            }
            if (!hasSequence) AngleStages.Clear();
        }

        private void StepAngleHierarchy(SuperController sc, bool next, bool stage, Action<string> status)
        {
            Atom atom = sc.GetAtomByUid(AtomId);
            if (atom == null || atom.GetStorableByID(StorableId) != Controller)
                throw new InvalidOperationException("层级播放控制器已变化");
            Type type = Controller.GetType();
            IList poses = ReadMember(type, "poses", true) as IList;
            object pose = ReadMember(type, "currentPose", true);
            int group = poses == null ? -1 : poses.IndexOf(pose);
            if (group < 0 || poses.Count != AngleStages.Count)
                throw new InvalidOperationException("动作集合已变化，请重新初始化");
            // Saved ids may be duplicated or stale after author reordering.
            // Resolve the live collection by object identity, never saved id.
            IList cams = ReadMember(pose, "camAngles", false) as IList;
            object cam = ReadMember(pose, "currentCam", false);
            int index = cams == null ? -1 : cams.IndexOf(cam);
            if (stage)
            {
                if (index < 0 || cams.Count != AngleStages[group].Count)
                    throw new InvalidOperationException("动作内阶段已变化，请重新初始化");
                int target = index + (next ? 1 : -1);
                if (target < 0 || target >= cams.Count)
                { status("播放：当前动作已到 pose 边界；未切换动作。"); return; }
                JSONStorableAction clip = ReadMember(cams[target], "timelineClip", false) as JSONStorableAction;
                if (clip == null || clip.actionCallback == null || clip.name != AngleStages[group][target])
                    throw new InvalidOperationException("阶段动画入口未就绪");
                // The native camera action also runs entry/exit triggers and
                // the linked Timeline clip; do not separately play Timelines.
                Controller.GetAction(next ? "Next Cam" : "Previous Cam").actionCallback();
                status("播放：动作 " + (group + 1) + " / pose " + (target + 1) + "；动作组保持不变。");
            }
            else
            {
                int target = group + (next ? 1 : -1);
                if (target < 0 || target >= poses.Count)
                { status("播放：已到 level 边界。"); return; }
                Controller.GetAction(next ? "Next Pose" : "Previous Pose").actionCallback();
                status("播放：已切换至 level " + (target + 1) + "。");
            }
        }

        internal static List<HierarchicalPlayback> Find(JSONClass scene)
        {
            List<HierarchicalPlayback> result = new List<HierarchicalPlayback>();
            JSONArray atoms = scene == null ? null : scene["atoms"] as JSONArray;
            if (atoms == null) return result;
            foreach (JSONNode atom in atoms.Childs)
            {
                string atomId = atom["id"];
                JSONArray storables = atom["storables"] as JSONArray;
                if (storables == null) continue;
                foreach (JSONNode storable in storables.Childs)
                {
                    string storableId = storable["id"];
                    if (string.IsNullOrEmpty(storableId) ||
                        !storableId.StartsWith("plugin#", StringComparison.Ordinal)) continue;
                    JSONArray levels = ArrayFor(storable, "levels");
                    JSONArray poses = ArrayFor(storable, "poses");
                    if (levels == null || poses == null || poses.Count < 2) continue;
                    HierarchicalPlayback candidate = new HierarchicalPlayback {
                        AtomId = atomId, StorableId = storableId
                    };
                    for (int i = 0; i < levels.Count; i++)
                    {
                        JSONNode item = levels[i];
                        int minId, maxId;
                        if (!ReadInt(item["minId"], out minId) ||
                            !ReadInt(item["maxId"], out maxId) || minId > maxId) continue;
                        candidate.Levels.Add(new Level {
                            MinId = minId, MaxId = maxId, Active = ReadBool(item["active"])
                        });
                    }
                    if (candidate.Levels.Count == 0) continue;
                    candidate.ReadAngleStages(poses);
                    candidate.Levels.Sort(delegate(Level a, Level b) {
                        return a.MinId.CompareTo(b.MinId);
                    });
                    // Build the per-level id lists once during discovery.  The
                    // native PoseMe Next/Previous actions operate on the
                    // global pose collection, so their boundary is not the
                    // same as a level boundary when poses are nested.
                    foreach (JSONNode pose in poses.Childs)
                    {
                        int poseId;
                        if (!ReadInt(pose["id"], out poseId)) continue;
                        for (int levelIndex = 0; levelIndex < candidate.Levels.Count; levelIndex++)
                        {
                            Level level = candidate.Levels[levelIndex];
                            if (poseId >= level.MinId && poseId <= level.MaxId)
                            {
                                level.PoseIds.Add(poseId);
                                break;
                            }
                        }
                    }
                    foreach (Level level in candidate.Levels)
                        level.PoseIds.Sort();
                    candidate._cursor = candidate.ActiveLevelStart();
                    result.Add(candidate);
                }
            }
            return result;
        }

        private static JSONArray ArrayFor(JSONNode node, string name)
        {
            JSONNode value = node[name];
            if (value is JSONArray) return value as JSONArray;
            // A few script versions used a capitalized serialized key.  Keep
            // the protocol check explicit rather than walking arbitrary data.
            value = node[name.ToUpperInvariant()];
            return value is JSONArray ? value as JSONArray : null;
        }

        private static bool ReadInt(JSONNode value, out int result)
        {
            result = 0;
            if (value == null) return false;
            return int.TryParse((string)value, out result);
        }

        private static bool ReadBool(JSONNode value)
        {
            if (value == null) return false;
            string text = (string)value;
            bool result;
            return bool.TryParse(text, out result) && result;
        }

        private int ActiveLevelStart()
        {
            foreach (Level level in Levels) if (level.Active) return level.MinId;
            return Levels[0].MinId;
        }

        internal bool Validate(SuperController sc)
        {
            Atom atom = sc.GetAtomByUid(AtomId);
            Controller = atom == null ? null : atom.GetStorableByID(StorableId);
            if (Controller == null) return false;
            if (HasAngleStages)
                return HasCallback("Next Cam") && HasCallback("Previous Cam") &&
                    HasCallback("Next Pose") && HasCallback("Previous Pose");
            List<string> names = Controller.GetActionNames();
            NextLevelAction = FindAction(names, "Go to next Level", "next", "level");
            PreviousLevelAction = FindAction(names, "Go to previous Level", "previous", "level");
            NextPoseAction = FindAction(names, "Next Pose", "next", "pose");
            PreviousPoseAction = FindAction(names, "Previous Pose", "previous", "pose");
            if (!HasCallback(NextLevelAction) || !HasCallback(PreviousLevelAction)) return false;
            bool spansPoses = false;
            foreach (Level level in Levels) if (level.MaxId > level.MinId) { spansPoses = true; break; }
            if (spansPoses && (!HasCallback(NextPoseAction) || !HasCallback(PreviousPoseAction))) return false;
            _cursor = ActiveLevelStart();
            return true;
        }

        private bool HasCallback(string actionName)
        {
            return !string.IsNullOrEmpty(actionName) && Controller.GetAction(actionName) != null &&
                Controller.GetAction(actionName).actionCallback != null;
        }

        private static string FindAction(List<string> names, string exact, string first, string second)
        {
            if (names == null) return null;
            foreach (string name in names)
                if (string.Equals(name, exact, StringComparison.OrdinalIgnoreCase)) return name;
            foreach (string name in names)
            {
                string lower = (name ?? "").ToLowerInvariant();
                if (lower.IndexOf(first, StringComparison.Ordinal) >= 0 &&
                    lower.IndexOf(second, StringComparison.Ordinal) >= 0) return name;
            }
            return null;
        }

        private static object ReadMember(object target, string name, bool isStatic)
        {
            if (target == null) return null;
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            Type type = target as Type;
            if (type != null)
            {
                FieldInfo field = type.GetField(name, flags);
                if (field != null) return field.GetValue(null);
                PropertyInfo property = type.GetProperty(name, flags);
                return property == null ? null : property.GetValue(null, null);
            }
            FieldInfo instanceField = target.GetType().GetField(name, flags);
            if (instanceField != null) return instanceField.GetValue(target);
            PropertyInfo instanceProperty = target.GetType().GetProperty(name, flags);
            return instanceProperty == null ? null : instanceProperty.GetValue(target, null);
        }

        private static bool WriteMember(object target, string name, bool isStatic, object value)
        {
            if (target == null) return false;
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            Type type = target as Type;
            if (type != null)
            {
                FieldInfo field = type.GetField(name, flags);
                if (field != null && !field.IsInitOnly)
                {
                    field.SetValue(null, value);
                    return true;
                }
                PropertyInfo property = type.GetProperty(name, flags);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(null, value, null);
                    return true;
                }
                return false;
            }
            FieldInfo instanceField = target.GetType().GetField(name, flags);
            if (instanceField != null && !instanceField.IsInitOnly)
            {
                instanceField.SetValue(target, value);
                return true;
            }
            PropertyInfo instanceProperty = target.GetType().GetProperty(name, flags);
            if (instanceProperty != null && instanceProperty.CanWrite)
            {
                instanceProperty.SetValue(target, value, null);
                return true;
            }
            return false;
        }

        private static Type ResolveType(Assembly preferredAssembly, string fullName)
        {
            if (preferredAssembly != null)
            {
                Type preferred = preferredAssembly.GetType(fullName, false);
                if (preferred != null) return preferred;
            }
            return AssemblyCatalog.FindType(fullName);
        }

        private static bool IsStoryLevel(object value)
        {
            return value != null && value.GetType().FullName == "CheesyFX.StoryLevel";
        }

        private int ReadCurrentPoseId()
        {
            Type type = Controller == null ? null : Controller.GetType();
            object pose = ReadMember(type, "currentPose", true);
            if (pose == null) pose = ReadMember(type, "CurrentPose", true);
            object id = ReadMember(pose, "id", false);
            if (id == null) id = ReadMember(pose, "Id", false);
            try { return id == null ? -1 : Convert.ToInt32(id); }
            catch (Exception) { return -1; }
        }

        private int LevelForPose(int poseId)
        {
            for (int i = 0; i < Levels.Count; i++)
            {
                Level level = Levels[i];
                if (level.PoseIds.Count > 0)
                {
                    if (level.PoseIds.Contains(poseId)) return i;
                }
                else if (poseId >= level.MinId && poseId <= level.MaxId)
                    return i;
            }
            return -1;
        }

        private int PoseIndex(Level level, int poseId)
        {
            if (level == null) return -1;
            if (level.PoseIds.Count == 0)
                return poseId < level.MinId || poseId > level.MaxId ? -1 : poseId - level.MinId;
            return level.PoseIds.IndexOf(poseId);
        }

        private static object ReadStoryLevels(Type storyType)
        {
            return ReadMember(storyType, "levels", true);
        }

        private bool TryResolveStoryLevel(int current, out object storyLevel, out Type storyType)
        {
            storyLevel = null;
            storyType = null;
            if (Controller == null) return false;
            storyType = ResolveType(Controller.GetType().Assembly, "CheesyFX.Story");
            if (storyType == null) return false;

            object levels = ReadStoryLevels(storyType);
            IEnumerable enumerable = levels as IEnumerable;
            if (enumerable == null) return false;
            foreach (object candidate in enumerable)
            {
                if (!IsStoryLevel(candidate)) continue;
                object minValue = ReadMember(candidate, "minId", false);
                object maxValue = ReadMember(candidate, "maxId", false);
                int minId, maxId;
                try
                {
                    minId = minValue == null ? int.MinValue : Convert.ToInt32(minValue);
                    maxId = maxValue == null ? int.MaxValue : Convert.ToInt32(maxValue);
                }
                catch (Exception) { continue; }
                if (current >= minId && current <= maxId)
                {
                    storyLevel = candidate;
                    break;
                }
            }
            if (storyLevel == null) return false;

            // Story.Next/Previous use Story.currentLevel rather than the
            // currently selected pose.  Keep that static pointer synchronized
            // before invoking either the level or pose method.  SetActive is
            // preferred because it also updates PoseMe's navigation state;
            // direct assignment is the fallback for script revisions that do
            // not expose SetActive.
            object currentLevel = ReadMember(storyType, "currentLevel", true);
            if (!object.ReferenceEquals(currentLevel, storyLevel))
            {
                BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                MethodInfo activate = storyLevel.GetType().GetMethod("SetActive", flags,
                    null, Type.EmptyTypes, null);
                if (activate != null)
                    activate.Invoke(storyLevel, null);
                else
                    WriteMember(storyType, "currentLevel", true, storyLevel);
            }
            return true;
        }

        private bool TryInvokeStoryPose(
            int current, bool next, Level level, Action<string> status)
        {
            object storyLevel;
            Type storyType;
            if (!TryResolveStoryLevel(current, out storyLevel, out storyType)) return false;
            int index = PoseIndex(level, current);
            int targetIndex = next ? index + 1 : index - 1;
            if (index < 0 || targetIndex < 0 ||
                (level.PoseIds.Count > 0 && targetIndex >= level.PoseIds.Count) ||
                (level.PoseIds.Count == 0 &&
                 ((next && current >= level.MaxId) || (!next && current <= level.MinId))))
            {
                status("播放：当前 level 已到 pose 边界。");
                return true;
            }
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            MethodInfo method = storyLevel.GetType().GetMethod(next ? "Next" : "Previous", flags,
                null, Type.EmptyTypes, null);
            if (method == null) return false;
            method.Invoke(storyLevel, null);
            int target = level.PoseIds.Count > 0 ? level.PoseIds[targetIndex] : current + (next ? 1 : -1);
            _cursor = target;
            status(next
                ? "播放：已调用当前 level 的 pose 的下一个。"
                : "播放：已调用当前 level 的 pose 的上一个。");
            return true;
        }

        private bool TryInvokeStoryLevel(
            int current, int targetLevel, bool next, Action<string> status)
        {
            object storyLevel;
            Type storyType;
            if (!TryResolveStoryLevel(current, out storyLevel, out storyType)) return false;
            string methodName = next ? "Next" : "Previous";
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo method = storyType.GetMethod(methodName, flags, null, Type.EmptyTypes, null);
            if (method == null) return false;
            method.Invoke(null, null);
            _cursor = Levels[targetLevel].PoseIds.Count > 0
                ? Levels[targetLevel].PoseIds[0]
                : Levels[targetLevel].MinId;
            status(next
                ? "播放：已调用下一个 level。"
                : "播放：已调用上一个 level。");
            return true;
        }

        private int NextLevel(int current)
        {
            return current < 0 ? (Levels.Count == 0 ? -1 : 0) : current + 1 < Levels.Count ? current + 1 : -1;
        }

        private int PreviousLevel(int current)
        {
            return current > 0 ? current - 1 : -1;
        }

        private bool TryResolveCurrent(
            SuperController sc, out int current, out int levelIndex,
            out Level level, Action<string> status)
        {
            Atom atom = sc.GetAtomByUid(AtomId);
            if (atom == null || atom.GetStorableByID(StorableId) != Controller)
                throw new InvalidOperationException("层级播放控制器已变化");

            current = ReadCurrentPoseId();
            if (current < 0) current = _cursor;
            levelIndex = LevelForPose(current);
            if (levelIndex < 0)
            {
                level = null;
                status("播放：无法同步当前小场景阶段。");
                return false;
            }
            level = Levels[levelIndex];
            return true;
        }

        private void InvokeAction(
            string actionName, int target, bool next, string scope,
            Action<string> status)
        {
            JSONStorableAction action = Controller.GetAction(actionName);
            if (action == null || action.actionCallback == null)
                throw new InvalidOperationException("层级播放原生动作已变化");
            action.actionCallback();
            _cursor = target;
            status(next
                ? "播放：已调用当前 " + scope + " 的下一个。"
                : "播放：已调用当前 " + scope + " 的上一个。");
        }

        // Explicit pose navigation never crosses a level boundary.  This is
        // kept separate from Step so a hierarchical scene can expose both
        // controls without skipping the poses inside a level.
        internal void StepPose(SuperController sc, bool next, Action<string> status)
        {
            if (HasAngleStages) { StepAngleHierarchy(sc, next, true, status); return; }
            int current, levelIndex;
            Level level;
            if (!TryResolveCurrent(sc, out current, out levelIndex, out level, status))
                return;
            // PoseMe's StoryLevel owns the level-local Next/Previous methods.
            // Calling the global PoseMe action here would walk the global pose
            // list and can silently enter the adjacent level.  Prefer the
            // native level-local API whenever this scene exposes it.
            if (TryInvokeStoryPose(current, next, level, status))
                return;
            if (next)
            {
                int index = PoseIndex(level, current);
                if (index < 0 ||
                    (level.PoseIds.Count > 0 && index + 1 >= level.PoseIds.Count) ||
                    (level.PoseIds.Count == 0 && current >= level.MaxId) ||
                    !HasCallback(NextPoseAction))
                {
                    status("播放：当前 level 已到 pose 边界。");
                    return;
                }
                int target = level.PoseIds.Count > 0 ? level.PoseIds[index + 1] : current + 1;
                InvokeAction(NextPoseAction, target, true, "level 的 pose", status);
            }
            else
            {
                int index = PoseIndex(level, current);
                if (index <= 0 || !HasCallback(PreviousPoseAction))
                {
                    status("播放：当前 level 已到 pose 边界。");
                    return;
                }
                int target = level.PoseIds.Count > 0 ? level.PoseIds[index - 1] : current - 1;
                InvokeAction(PreviousPoseAction, target, false, "level 的 pose", status);
            }
        }

        // Explicit level navigation always uses the native level action.  It
        // never synthesizes or replays pose/timeline actions.
        internal void StepLevel(SuperController sc, bool next, Action<string> status)
        {
            if (HasAngleStages) { StepAngleHierarchy(sc, next, false, status); return; }
            int current, levelIndex;
            Level level;
            if (!TryResolveCurrent(sc, out current, out levelIndex, out level, status))
                return;

            int targetLevel = next ? NextLevel(levelIndex) : PreviousLevel(levelIndex);
            if (targetLevel < 0)
            {
                status("播放：已到 level 边界。");
                return;
            }
            // Story.Next/Previous consult Story.currentLevel.  Synchronize
            // that pointer from the current pose before invoking the native
            // level method; otherwise a freshly loaded scene with a null
            // currentLevel is reset to its first level and appears inert.
            if (TryInvokeStoryLevel(current, targetLevel, next, status))
                return;
            string actionName = next ? NextLevelAction : PreviousLevelAction;
            if (!HasCallback(actionName))
            {
                status("播放：当前场景没有可用的 level 切换动作。");
                return;
            }
            InvokeAction(actionName, Levels[targetLevel].MinId, next, "level", status);
        }

        internal void Step(SuperController sc, bool next, Action<string> status)
        {
            if (HasAngleStages) { StepAngleHierarchy(sc, next, true, status); return; }
            int current, levelIndex;
            Level level;
            if (!TryResolveCurrent(sc, out current, out levelIndex, out level, status))
                return;
            if (next)
            {
                if (current < level.MaxId && HasCallback(NextPoseAction))
                    StepPose(sc, true, status);
                else
                    StepLevel(sc, true, status);
            }
            else
            {
                if (current > level.MinId && HasCallback(PreviousPoseAction))
                    StepPose(sc, false, status);
                else
                    StepLevel(sc, false, status);
            }
        }
    }

    internal sealed class ScenePlaybackFlow
    {
        private HierarchicalPlayback _hierarchical;
        private NumberedPlayback _numbered;
        private NumberedPlayback.TimelineStepPlayback _timestep;
        private PlaybackRoute _route;
        private JSONStorable _controller;
        private JSONNode _scene;
        private float _nextAllowed;
        internal bool IsHierarchical
        {
            get { return _hierarchical != null; }
        }

        internal bool Ready
        {
            get { return ((_controller != null && _route != null) || _numbered != null || _hierarchical != null || _timestep != null) && SuperController.singleton != null &&
                object.ReferenceEquals(_scene, SuperController.singleton.loadJson); }
        }

        internal void Initialize(Action<string> status)
        {
            _hierarchical = null;
            _route = null;
            _controller = null;
            _numbered = null; _timestep = null;
            SuperController sc = SuperController.singleton;
            if (sc == null || sc.isLoading)
            {
                status("播放：请在场景加载完成后初始化。");
                return;
            }
            _scene = sc.loadJson;
            try
            {
                List<PlaybackRoute> candidates = PlaybackFlowDiscovery.Find(_scene as JSONClass);
                List<PlaybackRoute> valid = new List<PlaybackRoute>();
                foreach (PlaybackRoute route in candidates)
                {
                    Atom atom = sc.GetAtomByUid(route.AtomId);
                    JSONStorable storable = atom == null ? null : atom.GetStorableByID(route.StorableId);
                    if (storable != null && storable.GetAction(route.Next) != null &&
                        storable.GetAction(route.Previous) != null) valid.Add(route);
                }
                if (valid.Count != 1)
                {
                    if (candidates.Count == 0)
                    {
                        List<NumberedPlayback> groups = NumberedPlayback.Find(_scene as JSONClass);
                        if (groups.Count == 1 && groups[0].Validate(sc))
                        {
                            // One Timeline can be reachable both through numbered
                            // buttons ("Play Segment BR/Stand1"…) and through named
                            // ones ("Play Segment Sofa doggy"). The numbered family is
                            // then only the subset whose names end in a digit, so every
                            // other segment is unreachable and a running one that is not
                            // in the list restarts at its first entry (measured: a living
                            // room segment jumped straight into the bathroom group).
                            // Same players = the two lists describe one authored
                            // sequence; the longer one is that sequence.
                            NumberedPlayback richer = NumberedPlayback.RicherNamed(
                                _scene as JSONClass, groups[0], sc);
                            _numbered = richer == null ? groups[0] : richer;
                            status("播放初始化：识别到 " + _numbered.Stages.Count + " 个阶段" +
                                (richer == null ? "（编号）；" : "（编号+命名合并）；") +
                                "沿用原按钮完整触发链。");
                            return;
                        }
                        List<NumberedPlayback> direct = NumberedPlayback.FindTimelineDirect(sc);
                        if (direct.Count == 1 && direct[0].Validate(sc))
                        {
                            // Direct control only sees the literal "Segment N"
                            // actions.  A named/story button list covering more
                            // stages on a subset of the same players is the
                            // authored sequence (vamsoy.spycam: 22 authored
                            // stages vs the 2 literal Segment-N clips).
                            NumberedPlayback richer =
                                NumberedPlayback.RicherNamedSubset(_scene as JSONClass, direct[0], sc);
                            _numbered = richer == null ? direct[0] : richer;
                            status("播放初始化：识别到 " + _numbered.Stages.Count +
                                (richer == null
                                    ? " 个 Timeline 阶段（直控 " + _numbered.PlayerCount + " 个角色）；沿用原动作链。"
                                    : " 个命名阶段（直控仅见子集）；沿用原按钮完整触发链。"));
                            return;
                        }
                        // Named-segment scenes: stage buttons invoking
                        // "Play Segment <name>" are the ordered stage list.
                        // Auxiliary groups (e.g. a 2-entry language/story
                        // mode switcher on another Timeline) must not veto
                        // the real sequence — adopt the unique largest
                        // group; a tie stays ambiguous as before.
                        List<NumberedPlayback> named = NumberedPlayback.FindNamed(_scene as JSONClass);
                        NumberedPlayback namedBest = null;
                        if (named.Count == 1) namedBest = named[0];
                        else
                        {
                            foreach (NumberedPlayback c in named)
                            {
                                if (namedBest != null &&
                                    c.Stages.Count <= namedBest.Stages.Count) continue;
                                namedBest = c;
                            }
                            if (namedBest != null)
                                foreach (NumberedPlayback c in named)
                                    if (c != namedBest &&
                                        c.Stages.Count == namedBest.Stages.Count)
                                    { namedBest = null; break; }
                        }
                        if (namedBest != null && namedBest.Validate(sc))
                        {
                            _numbered = namedBest;
                            status("播放初始化：识别到 " + _numbered.Stages.Count +
                                " 个命名阶段" + (named.Count > 1
                                    ? "（另有 " + (named.Count - 1) + " 个备选按钮组未接管）"
                                    : "") + "；沿用原按钮完整触发链。");
                            return;
                        }
                        // No stage buttons either: fall back to Timeline's own
                        // Next/Previous Animation stepper.
                        _timestep = NumberedPlayback.TimelineStepPlayback.Find(sc);
                        if (_timestep != null)
                        {
                            status("播放初始化：识别到 " + _timestep.PlayerCount +
                                " 个 Timeline 播放器；命名段场景，按 Timeline 原生前后步进。");
                            return;
                        }
                    }
                    List<HierarchicalPlayback> hierarchical = HierarchicalPlayback.Find(_scene as JSONClass);
                    List<HierarchicalPlayback> validHierarchical = new List<HierarchicalPlayback>();
                    foreach (HierarchicalPlayback candidate in hierarchical)
                        if (candidate.Validate(sc)) validHierarchical.Add(candidate);
                    if (validHierarchical.Count == 1)
                    {
                        _hierarchical = validHierarchical[0];
                        if (_hierarchical.HasAngleStages)
                        {
                            status("播放初始化：识别到 " + _hierarchical.AngleStages.Count +
                                " 个动作组；level 切换动作，pose 切换动作内阶段。");
                            return;
                        }
                        int levels = _hierarchical.Levels.Count;
                        int poses = 0;
                        foreach (HierarchicalPlayback.Level level in _hierarchical.Levels)
                            poses += level.MaxId - level.MinId + 1;
                        status("播放初始化：识别到 " + levels + " 个小场景、" + poses +
                            " 个动作阶段；沿用原场景层级触发链。");
                        return;
                    }
                    ReportDiscovery(sc, candidates, valid.Count);
                    status(valid.Count == 0
                        ? "播放：未识别到已支持的双向阶段总控；未修改场景。"
                        : "播放：识别到多个独立阶段总控，需先明确入口；未修改场景。");
                    return;
                }
                _route = valid[0];
                _controller = sc.GetAtomByUid(_route.AtomId).GetStorableByID(_route.StorableId);
                status("播放初始化：" + _route.States + " 个阶段，关联 " +
                    _route.ReachableNodes + " 个节点；沿用原场景完整切换。总控：" + _route.AtomId);
            }
            catch (Exception e)
            {
                _hierarchical = null;
                _route = null;
                _controller = null;
                _numbered = null; _timestep = null;
                status("播放初始化失败：" + e.Message);
            }
        }

        internal void Step(bool next, Action<string> status)
        {
            SuperController sc = SuperController.singleton;
            if (sc == null || sc.isLoading) { status("播放：场景正在加载。"); return; }
            if (!Ready || !object.ReferenceEquals(_scene, sc.loadJson))
            {
                _hierarchical = null;
                _route = null;
                _controller = null;
                _numbered = null; _timestep = null;
                status("播放：请先初始化当前场景。");
                return;
            }
            if (_hierarchical != null)
            {
                if (Time.unscaledTime < _nextAllowed) return;
                _nextAllowed = Time.unscaledTime + 0.5f;
                try { _hierarchical.Step(sc, next, status); }
                catch (Exception e) { _hierarchical = null; status("播放切换失败，请重新初始化：" + e.Message); }
                return;
            }
            if (_numbered != null)
            {
                if (Time.unscaledTime < _nextAllowed) return;
                _nextAllowed = Time.unscaledTime + 0.5f;
                try { _numbered.Step(sc, next, status); }
                catch (Exception e) { _numbered = null; _timestep = null; status("播放切换失败，请重新初始化：" + e.Message); }
                return;
            }
            if (_timestep != null)
            {
                if (Time.unscaledTime < _nextAllowed) return;
                _nextAllowed = Time.unscaledTime + 0.5f;
                try { _timestep.Step(sc, next, status); }
                catch (Exception e) { _timestep = null; status("播放切换失败，请重新初始化：" + e.Message); }
                return;
            }
            Atom atom = sc.GetAtomByUid(_route.AtomId);
            if (atom == null || atom.GetStorableByID(_route.StorableId) != _controller)
            {
                _controller = null;
                status("播放：总控已变化，请重新初始化。");
                return;
            }
            if (Time.unscaledTime < _nextAllowed) return;
            _nextAllowed = Time.unscaledTime + 0.5f;
            try
            {
                // One native command only. The author owns exit/entry triggers,
                // delays, camera moves, lights and inactive actors. Never play all Timelines.
                JSONStorableAction action = _controller.GetAction(next ? _route.Next : _route.Previous);
                if (action == null) { _controller = null; status("播放：切换动作已移除，请重新初始化。"); return; }
                action.actionCallback();
                status(next ? "播放：已调用原场景下一阶段。" : "播放：已调用原场景上一阶段。");
            }
            catch (Exception e) { status("播放切换失败：" + e.Message); }
        }

        private void StepHierarchical(
            bool next, bool pose, Action<string> status)
        {
            SuperController sc = SuperController.singleton;
            if (sc == null || sc.isLoading)
            {
                status("播放：场景正在加载。");
                return;
            }
            if (!Ready || !object.ReferenceEquals(_scene, sc.loadJson))
            {
                _hierarchical = null;
                _route = null;
                _controller = null;
                _numbered = null; _timestep = null;
                status("播放：请先初始化当前场景。");
                return;
            }
            if (_hierarchical == null)
            {
                status("播放：当前场景没有 level/pose 层级；请使用下一个/上一个。");
                return;
            }
            if (Time.unscaledTime < _nextAllowed)
                return;
            _nextAllowed = Time.unscaledTime + 0.5f;
            try
            {
                if (pose) _hierarchical.StepPose(sc, next, status);
                else _hierarchical.StepLevel(sc, next, status);
            }
            catch (Exception e)
            {
                _hierarchical = null;
                status("播放切换失败，请重新初始化：" + e.Message);
            }
        }

        internal void StepPose(bool next, Action<string> status)
        {
            StepHierarchical(next, true, status);
        }

        internal void StepLevel(bool next, Action<string> status)
        {
            StepHierarchical(next, false, status);
        }

        private static void ReportDiscovery(SuperController sc, List<PlaybackRoute> candidates, int validCount)
        {
            // Only on failed initialization; never probe by executing a scene action.
            var log = Quest3TriggerUIPlugin.Log;
            if (log == null) return;
            JSONArray savedAtoms = sc.loadJson == null ? null : sc.loadJson["atoms"] as JSONArray;
            log.LogInfo("Playback discovery: savedAtoms=" + (savedAtoms == null ? -1 : savedAtoms.Count) +
                "; liveAtoms=" + sc.GetAtoms().Count + "; candidates=" + candidates.Count + "; valid=" + validCount);
            foreach (PlaybackRoute route in candidates)
                log.LogInfo("Playback candidate: " + route.AtomId + "/" + route.StorableId +
                    " actions=" + route.Next + "/" + route.Previous);
            foreach (Atom atom in sc.GetAtoms())
                foreach (string id in atom.GetStorableIDs())
                {
                    if (!id.StartsWith("plugin#", StringComparison.Ordinal) && id != "Trigger") continue;
                    JSONStorable storable = atom.GetStorableByID(id);
                    if (storable == null) continue;
                    List<string> actions = storable.GetActionNames();
                    log.LogInfo("Playback live: " + atom.uid + "/" + id + " type=" + storable.GetType().FullName +
                        " actions=" + (actions == null ? "<none>" : string.Join("|", actions.ToArray())));
                }
        }
    }

    internal sealed class NumberedPlayback
    {
        internal sealed class Stage
        {
            internal int Number;
            internal int TriggerScore;
            internal string ButtonId, Animation, SegmentName, Signature, ButtonStorable, ButtonAction;
            internal string DirectAction;
            internal JSONStorable Button;
        }
        internal readonly List<Stage> Stages = new List<Stage>();
        internal bool DirectMode;
        private List<int> _directNumbers;
        private readonly List<string> _targets = new List<string>();
        private readonly List<JSONStorable> _players = new List<JSONStorable>();
        private readonly HashSet<int> _conflictingNumbers = new HashSet<int>();
        // Remembers the last stepped stage so a finished (no longer playing)
        // clip does not collapse the position back to the first stage.
        private int _cursor = -1;
        private static readonly Regex Numbered = new Regex(@"^Play (.+?)(\d+)$");

        internal static List<NumberedPlayback> Find(JSONClass scene)
        {
            var groups = new Dictionary<string, NumberedPlayback>();
            JSONArray atoms = scene == null ? null : scene["atoms"] as JSONArray;
            if (atoms == null) return new List<NumberedPlayback>();
            foreach (JSONNode atom in ButtonEntries(atoms))
            {
                if ((string)atom["type"] != "UIButton") continue;
                JSONArray storables = atom["storables"] as JSONArray;
                if (storables == null) continue;
                foreach (JSONNode storable in storables.Childs)
                {
                    if ((string)storable["id"] != "Trigger") continue;
                    JSONNode trigger = storable["trigger"];
                    JSONArray actions = trigger["startActions"] as JSONArray;
                    if (actions == null) continue;
                    var targets = new List<string>();
                    string animation = null, prefix = null;
                    int number = 0;
                    bool mismatch = false;
                    foreach (JSONNode action in actions.Childs)
                    {
                        string receiver = action["receiver"];
                        if (receiver == null || !receiver.EndsWith("_VamTimeline.AtomPlugin", StringComparison.Ordinal)) continue;
                        string name = action["receiverTargetName"];
                        Match match = Numbered.Match(name ?? "");
                        int parsed;
                        if (!match.Success || !int.TryParse(match.Groups[2].Value, out parsed)) { mismatch = true; break; }
                        if (animation != null && animation != name.Substring(5)) { mismatch = true; break; }
                        animation = name.Substring(5); prefix = match.Groups[1].Value; number = parsed;
                        string targetAtom = action["receiverAtom"];
                        if (string.IsNullOrEmpty(targetAtom)) targetAtom = atom["id"];
                        string target = targetAtom + "\n" + receiver;
                        if (!targets.Contains(target)) targets.Add(target);
                    }
                    if (mismatch || targets.Count == 0) continue;
                    targets.Sort(StringComparer.Ordinal);
                    string key = prefix + "\n" + string.Join("\n", targets.ToArray());
                    bool story = ((string)atom["sourceAction"] ?? "").StartsWith("story:", StringComparison.Ordinal);
                    if (story) key = "story\n" + (string)atom["id"] + "\n" + (string)atom["sourceStorable"] + "\n" + prefix;
                    NumberedPlayback group;
                    if (!groups.TryGetValue(key, out group))
                    {
                        group = new NumberedPlayback(); group._targets.AddRange(targets); groups.Add(key, group);
                    }
                    else if (story)
                        group._targets.RemoveAll(delegate(string target) { return !targets.Contains(target); });
                    Stage existing = group.Stages.Find(delegate(Stage s) { return s.Number == number; });
                    // Only the invoked action chain identifies a stage; cosmetic
                    // fields such as displayName make identical duplicates look
                    // different and would reject the whole group as a conflict.
                    string signature = ActionSignature(trigger);
                    int triggerScore = ActionCoverage(trigger);
                    if (existing != null)
                    {
                        if (existing.Signature == signature) continue;

                        // A scene can expose the same numbered stage through a
                        // compact page and through a full start button.  Both
                        // entries target the same Timeline stage, but the full
                        // button may also carry audio, lights, or other author
                        // actions.  Keep the candidate with the larger native
                        // action coverage instead of rejecting the whole group.
                        // Equal coverage with different chains remains
                        // ambiguous and is intentionally not auto-selected.
                        if (triggerScore > existing.TriggerScore)
                        {
                            existing.ButtonId = atom["id"];
                            existing.Animation = animation;
                            existing.SegmentName = animation.StartsWith("Segment ", StringComparison.Ordinal)
                                ? animation.Substring(8) : null;
                            existing.Signature = signature;
                            existing.TriggerScore = triggerScore;
                            existing.ButtonStorable = string.IsNullOrEmpty(atom["sourceStorable"])
                                ? "Trigger" : (string)atom["sourceStorable"];
                            existing.ButtonAction = atom["sourceAction"];
                            group._conflictingNumbers.Remove(number);
                        }
                        else if (triggerScore == existing.TriggerScore)
                        {
                            group._conflictingNumbers.Add(number);
                        }
                        continue;
                    }
                    group.Stages.Add(new Stage { Number = number, ButtonId = atom["id"], Animation = animation,
                        SegmentName = animation.StartsWith("Segment ", StringComparison.Ordinal) ? animation.Substring(8) : null,
                        Signature = signature, TriggerScore = triggerScore,
                        ButtonStorable = string.IsNullOrEmpty(atom["sourceStorable"]) ? "Trigger" : (string)atom["sourceStorable"],
                        ButtonAction = atom["sourceAction"] });
                }
            }
            var result = new List<NumberedPlayback>();
            foreach (NumberedPlayback group in groups.Values)
            {
                if (group.Stages.Count < 2) continue;
                group.Stages.Sort(delegate(Stage a, Stage b) { return a.Number.CompareTo(b.Number); });
                result.Add(group); // Ambiguous groups must not silently disappear.
            }
            return result;
        }

        // Scenes without stage buttons: Timeline plugins exposing
        // "Play Segment Segment N" are themselves the controller. Players with
        // identical segment sets form one synchronized group.
        private static readonly Regex DirectSegment = new Regex(@"^Play Segment Segment (\d+)$");

        // Named-segment scenes: UIButtons whose trigger invokes "Play Segment
        // <name>" are themselves the ordered stage list — the scene's atom
        // order is the author's progression.  Clicking the real button keeps
        // the full chain (audio, clothing, overlays); stepping Timeline's own
        // Next/Previous Animation list does not, because that list also
        // contains non-segment overlay clips.
        internal static List<NumberedPlayback> FindNamed(JSONClass scene)
        {
            var groups = new Dictionary<string, NumberedPlayback>();
            JSONArray atoms = scene == null ? null : scene["atoms"] as JSONArray;
            if (atoms == null) return new List<NumberedPlayback>();
            foreach (JSONNode atom in ButtonEntries(atoms))
            {
                if ((string)atom["type"] != "UIButton") continue;
                JSONArray storables = atom["storables"] as JSONArray;
                if (storables == null) continue;
                foreach (JSONNode storable in storables.Childs)
                {
                    if ((string)storable["id"] != "Trigger") continue;
                    JSONNode trigger = storable["trigger"];
                    JSONArray actions = trigger["startActions"] as JSONArray;
                    if (actions == null) continue;
                    var targets = new List<string>();
                    string segment = null, directSegment = null;
                    bool mismatch = false;
                    foreach (JSONNode action in actions.Childs)
                    {
                        string receiver = action["receiver"];
                        if (receiver == null || !receiver.EndsWith("_VamTimeline.AtomPlugin", StringComparison.Ordinal)) continue;
                        string name = action["receiverTargetName"];
                        // Non-segment Timeline calls (Stop And Reset, Paused…)
                        // ride along in the same button chain — they don't
                        // make the stage ambiguous; only a second different
                        // segment name does.
                        if (name == null || !name.StartsWith("Play Segment ", StringComparison.Ordinal)) continue;
                        string seg = name.Substring(13);
                        // A literal "Segment N" is a real segment name — a chain
                        // whose only segment call is "Play Segment Segment 2"
                        // is still a stage button (VAMStoryAction menus do this).
                        // When the chain also carries a named segment, the named
                        // one wins and the Segment-N call rides along.
                        if (DirectSegment.Match(name).Success)
                        {
                            if (directSegment != null && directSegment != seg) { mismatch = true; break; }
                            directSegment = seg;
                        }
                        else
                        {
                            if (segment != null && segment != seg) { mismatch = true; break; }
                            segment = seg;
                        }
                        string targetAtom = action["receiverAtom"];
                        if (string.IsNullOrEmpty(targetAtom)) targetAtom = atom["id"];
                        string target = targetAtom + "\n" + receiver;
                        if (!targets.Contains(target)) targets.Add(target);
                    }
                    string finalSegment = segment ?? directSegment;
                    if (mismatch || finalSegment == null || targets.Count == 0) continue;
                    targets.Sort(StringComparer.Ordinal);
                    string key = string.Join("\n", targets.ToArray());
                    NumberedPlayback group;
                    if (!groups.TryGetValue(key, out group))
                    {
                        group = new NumberedPlayback(); group._targets.AddRange(targets); groups.Add(key, group);
                    }
                    string signature = ActionSignature(trigger);
                    int triggerScore = ActionCoverage(trigger);
                    Stage existing = group.Stages.Find(delegate(Stage s) { return s.SegmentName == finalSegment; });
                    if (existing != null)
                    {
                        if (existing.Signature == signature) continue;
                        // Duplicate stage entries (compact page vs full button):
                        // keep the chain with larger native action coverage.
                        if (triggerScore > existing.TriggerScore)
                        {
                            existing.ButtonId = atom["id"]; existing.Signature = signature;
                            existing.TriggerScore = triggerScore;
                            existing.ButtonStorable = string.IsNullOrEmpty(atom["sourceStorable"])
                                ? "Trigger" : (string)atom["sourceStorable"];
                            existing.ButtonAction = atom["sourceAction"];
                        }
                        continue;
                    }
                    // Number doubles as the ordinal: stages stay in the
                    // author's atom order, and sorting by it is a no-op.
                    group.Stages.Add(new Stage { Number = group.Stages.Count, ButtonId = atom["id"],
                        Animation = "Segment " + finalSegment, SegmentName = finalSegment,
                        Signature = signature, TriggerScore = triggerScore,
                        ButtonStorable = string.IsNullOrEmpty(atom["sourceStorable"]) ? "Trigger" : (string)atom["sourceStorable"],
                        ButtonAction = atom["sourceAction"] });
                }
            }
            var result = new List<NumberedPlayback>();
            foreach (NumberedPlayback group in groups.Values)
                if (group.Stages.Count >= 2) result.Add(group);
            return result;
        }

        // A scene can serialize one Timeline as a numbered family and as named
        // segments at the same time.  The numbered family is then only a subset,
        // and stepping it skips every segment whose name has no trailing digit.
        // Prefer the named list only when it drives exactly the same players,
        // still contains every stage the numbered family offered, and covers
        // strictly more of the authored sequence; anything else keeps the
        // numbered result untouched.
        internal static NumberedPlayback RicherNamed(JSONClass scene, NumberedPlayback numbered, SuperController sc)
        {
            if (scene == null || numbered == null) return null;
            List<NumberedPlayback> named = FindNamed(scene);
            NumberedPlayback best = null;
            foreach (NumberedPlayback candidate in named)
            {
                if (!numbered.SameTargets(candidate)) continue;
                if (candidate.Stages.Count <= numbered.Stages.Count) continue;
                if (best != null && candidate.Stages.Count <= best.Stages.Count) continue;
                if (!numbered.CoveredBy(candidate)) continue;
                if (!candidate.Validate(sc)) continue;
                best = candidate;
            }
            return best;
        }

        internal bool SameTargets(NumberedPlayback other)
        {
            if (other == null || _targets.Count != other._targets.Count) return false;
            foreach (string target in _targets)
                if (!other._targets.Contains(target)) return false;
            return true;
        }

        // A switch to the longer list is only safe when it still offers every
        // stage the numbered family had: a named list that drops one would
        // silently remove a step the user can currently reach.
        internal bool CoveredBy(NumberedPlayback other)
        {
            foreach (Stage stage in Stages)
                if (other.Stages.Find(delegate(Stage s) { return s.Animation == stage.Animation; }) == null)
                    return false;
            return true;
        }

        // Direct-mode variant: the named/story list may legitimately drive a
        // SUBSET of the synchronized players (peer atoms follow via the
        // plugin's own SyncWithPeers), and its stages cannot contain the
        // direct "Segment N" actions by construction — so adoption keys on
        // target containment plus strictly more stages instead of coverage.
        internal static NumberedPlayback RicherNamedSubset(JSONClass scene, NumberedPlayback direct, SuperController sc)
        {
            if (scene == null || direct == null) return null;
            NumberedPlayback best = null;
            foreach (NumberedPlayback candidate in FindNamed(scene))
            {
                if (candidate.Stages.Count <= direct.Stages.Count) continue;
                if (best != null && candidate.Stages.Count <= best.Stages.Count) continue;
                bool subset = candidate._targets.Count > 0;
                foreach (string target in candidate._targets)
                    if (!direct._targets.Contains(target)) { subset = false; break; }
                if (!subset || !candidate.Validate(sc)) continue;
                best = candidate;
            }
            return best;
        }

        private static bool SameNumbers(List<int> a, List<int> b)
        {
            if (a == null || a.Count != b.Count) return false;
            foreach (int n in a) if (!b.Contains(n)) return false;
            return true;
        }

        internal static List<NumberedPlayback> FindTimelineDirect(SuperController sc)
        {
            var result = new List<NumberedPlayback>();
            if (sc == null) return result;
            foreach (Atom atom in sc.GetAtoms())
            {
                foreach (string id in atom.GetStorableIDs())
                {
                    if (!id.EndsWith("_VamTimeline.AtomPlugin", StringComparison.Ordinal)) continue;
                    JSONStorable storable = atom.GetStorableByID(id);
                    List<string> names = storable == null ? null : storable.GetActionNames();
                    if (names == null) continue;
                    var numbers = new List<int>();
                    foreach (string actionName in names)
                    {
                        Match match = DirectSegment.Match(actionName ?? "");
                        int n;
                        if (match.Success && int.TryParse(match.Groups[1].Value, out n) &&
                            !numbers.Contains(n)) numbers.Add(n);
                    }
                    if (numbers.Count < 2) continue;
                    string target = atom.uid + "\n" + id;
                    NumberedPlayback group = null;
                    foreach (NumberedPlayback existing in result)
                        if (SameNumbers(existing._directNumbers, numbers)) { group = existing; break; }
                    if (group == null)
                    {
                        group = new NumberedPlayback();
                        group.DirectMode = true;
                        group._directNumbers = numbers;
                        result.Add(group);
                    }
                    if (!group._targets.Contains(target)) group._targets.Add(target);
                }
            }
            foreach (NumberedPlayback group in result)
            {
                group._targets.Sort(StringComparer.Ordinal);
                group._directNumbers.Sort();
                foreach (int n in group._directNumbers)
                    group.Stages.Add(new Stage { Number = n, Animation = "Segment " + n,
                        SegmentName = "Segment " + n, DirectAction = "Play Segment Segment " + n });
            }
            return result;
        }

        internal int PlayerCount { get { return _targets.Count; } }

        // Scenes with named Timeline segments (no numbered stages): every
        // Person's VamTimeline exposes "Next Animation"/"Previous Animation",
        // which is itself the ordered stepper — no stage index needed and
        // Timeline owns transitions. Restricted to Person atoms so background
        // Timelines (props, toys) are not dragged along.
        internal sealed class TimelineStepPlayback
        {
            internal readonly List<JSONStorable> Players = new List<JSONStorable>();
            internal readonly List<string> Targets = new List<string>();

            internal int PlayerCount { get { return Players.Count; } }

            internal static TimelineStepPlayback Find(SuperController sc)
            {
                if (sc == null) return null;
                var group = new TimelineStepPlayback();
                foreach (Atom atom in sc.GetAtoms())
                {
                    if ((string)atom.type != "Person") continue;
                    foreach (string id in atom.GetStorableIDs())
                    {
                        if (!id.EndsWith("_VamTimeline.AtomPlugin", StringComparison.Ordinal)) continue;
                        JSONStorable storable = atom.GetStorableByID(id);
                        if (storable == null) continue;
                        if (storable.GetAction("Next Animation") == null ||
                            storable.GetAction("Previous Animation") == null) continue;
                        group.Players.Add(storable);
                        group.Targets.Add(atom.uid + "\n" + id);
                    }
                }
                return group.Players.Count > 0 ? group : null;
            }

            internal void Step(SuperController sc, bool next, Action<string> status)
            {
                for (int i = 0; i < Players.Count; i++)
                {
                    string[] ids = Targets[i].Split('\n');
                    Atom atom = sc.GetAtomByUid(ids[0]);
                    if (atom == null || atom.GetStorableByID(ids[1]) != Players[i])
                        throw new InvalidOperationException("阶段播放器已变化");
                }
                string name = next ? "Next Animation" : "Previous Animation";
                foreach (JSONStorable player in Players)
                {
                    JSONStorableAction action = player.GetAction(name);
                    if (action != null) action.actionCallback();
                }
                status((next ? "播放：已推进 Timeline 下一动画（" : "播放：已回退 Timeline 上一动画（") +
                    Players.Count + " 个角色）。");
            }
        }

        // Normalize explicit widget-to-trigger mappings, not arbitrary numeric actions.
        // The original registered action remains the execution endpoint.
        private static IEnumerable<JSONNode> ButtonEntries(JSONArray atoms)
        {
            foreach (JSONNode atom in atoms.Childs)
            {
                if ((string)atom["type"] == "UIButton") yield return atom;
                JSONArray storables = atom["storables"] as JSONArray;
                if (storables == null) continue;
                foreach (JSONNode st in storables.Childs)
                {
                    string id = st["id"];
                    if (id == null || !id.StartsWith("plugin#", StringComparison.Ordinal) || st.AsObject == null) continue;
                    foreach (string key in new List<string>(st.AsObject.Keys))
                    {
                        if (id.EndsWith("_VAMStoryActionPlugin.VAMStoryAction", StringComparison.Ordinal) &&
                            key.StartsWith("Act_", StringComparison.Ordinal) && key.EndsWith("-Type", StringComparison.Ordinal))
                        {
                            int actionId;
                            string suffixId = key.Substring(4, key.Length - 9);
                            if (!int.TryParse(suffixId, out actionId) || actionId < 0 || (string)st[key] != "Button") continue;
                            JSONNode storytrigger = st["Act_" + actionId + "-TriggerOnClick"];
                            if (!(storytrigger["startActions"] is JSONArray)) continue;
                            var storyentry = new JSONClass(); storyentry["id"] = atom["id"]; storyentry["type"] = "UIButton";
                            storyentry["sourceStorable"] = id; storyentry["sourceAction"] = "story:" + actionId;
                            var storyitem = new JSONClass(); storyitem["id"] = "Trigger"; storyitem["trigger"] = storytrigger;
                            var storylist = new JSONArray(); storylist.Add(storyitem); storyentry["storables"] = storylist;
                            yield return storyentry;
                            continue;
                        }
                        if (!key.StartsWith("ButtonWidget", StringComparison.Ordinal)) continue;
                        string suffix = key.Substring("ButtonWidget".Length);
                        int index;
                        if (!int.TryParse(suffix, out index) || (string)st["classid" + suffix] != "ButtonWidget") continue;
                        string action = st[key]["name"];
                        if (string.IsNullOrEmpty(action)) continue;
                        JSONNode trigger = st[action + suffix];
                        if (!(trigger["startActions"] is JSONArray)) continue;
                        var entry = new JSONClass(); entry["id"] = atom["id"]; entry["type"] = "UIButton";
                        entry["sourceStorable"] = id; entry["sourceAction"] = action;
                        var item = new JSONClass(); item["id"] = "Trigger"; item["trigger"] = trigger;
                        var list = new JSONArray(); list.Add(item); entry["storables"] = list;
                        yield return entry;
                    }
                }
            }
        }

        internal bool Validate(SuperController sc)
        {
            if (_conflictingNumbers.Count > 0 || _targets.Count == 0) return false;
            _players.Clear();
            foreach (string target in _targets)
            {
                string[] ids = target.Split('\n');
                Atom atom = sc.GetAtomByUid(ids[0]);
                JSONStorable player = atom == null ? null : atom.GetStorableByID(ids[1]);
                if (player == null) return false;
                foreach (Stage stage in Stages)
                    if (player.GetAction(stage.DirectAction ?? ("Play " + stage.Animation)) == null) return false;
                _players.Add(player);
            }
            foreach (Stage stage in Stages)
            {
                if (stage.DirectAction != null) continue;
                Atom atom = sc.GetAtomByUid(stage.ButtonId);
                stage.Button = atom == null ? null : atom.GetStorableByID(stage.ButtonStorable);
                if (stage.Button == null) return false;
                if ((stage.ButtonAction ?? "").StartsWith("story:", StringComparison.Ordinal))
                { if (StoryButton(stage.Button, stage.ButtonAction) == null) return false; continue; }
                if (string.IsNullOrEmpty(stage.ButtonAction))
                { if (ClickMethod(stage.Button) == null) return false; }
                else if (stage.Button.GetAction(stage.ButtonAction) == null) return false;
            }
            return true;
        }

        // Count only serialized receiver actions.  The scene JSON can contain
        // large Timeline payloads, but this helper receives a button trigger,
        // which is small; it runs once during initialization and never per
        // frame.  Requiring receiver/target pairs keeps arbitrary display data
        // from influencing duplicate resolution.
        private static int ActionCoverage(JSONNode node)
        {
            if (node == null || (node.AsObject == null && node.AsArray == null)) return 0;
            int count = 0;
            if (node.AsObject != null &&
                !string.IsNullOrEmpty(node["receiver"]) &&
                !string.IsNullOrEmpty(node["receiverTargetName"])) count++;
            foreach (JSONNode child in node.Childs) count += ActionCoverage(child);
            return count;
        }

        private static string ActionSignature(JSONNode node)
        {
            var parts = new List<string>();
            CollectSignature(node, parts);
            return string.Join("\n", parts.ToArray());
        }

        private static void CollectSignature(JSONNode node, List<string> parts)
        {
            if (node == null || (node.AsObject == null && node.AsArray == null)) return;
            if (node.AsObject != null)
            {
                string receiver = node["receiver"];
                string target = node["receiverTargetName"];
                string recvAtom = node["receiverAtom"];
                if (!string.IsNullOrEmpty(receiver) && !string.IsNullOrEmpty(target))
                    parts.Add((recvAtom ?? "") + "\t" + receiver + "\t" + target);
            }
            foreach (JSONNode child in node.Childs) CollectSignature(child, parts);
        }

        private static MethodInfo ClickMethod(JSONStorable button)
        {
            return button.GetType().GetMethod("OnButtonClick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        private static object StoryButton(JSONStorable controller, string action)
        {
            int id;
            if (!int.TryParse(action.Substring(6), out id)) return null;
            FieldInfo field = controller.GetType().GetField("storyActions", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            IEnumerable list = field == null ? null : field.GetValue(controller) as IEnumerable;
            if (list == null) return null;
            foreach (object item in list)
                if (object.Equals(Read(item, "ActionID"), id) &&
                    item.GetType().GetMethod("DoClickTrigger", Type.EmptyTypes) != null) return item;
            return null;
        }
        private static object Read(object target, string name)
        {
            if (target == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            PropertyInfo property = target.GetType().GetProperty(name, flags);
            if (property != null) return property.GetValue(target, null);
            FieldInfo field = target.GetType().GetField(name, flags);
            return field == null ? null : field.GetValue(target);
        }
        private static int _rejectLogsLeft = 6;

        private void LogStepReject(SuperController sc, string detail)
        {
            if (_rejectLogsLeft <= 0) return;
            _rejectLogsLeft--;
            if (Quest3TriggerUIPlugin.Log == null) return;
            Quest3TriggerUIPlugin.Log.LogInfo("播放[reject] " + detail);
            bool segmentMode = Stages.Count > 0 && !string.IsNullOrEmpty(Stages[0].SegmentName);
            for (int p = 0; p < _players.Count; p++)
            {
                string uid = _targets[p].Split('\n')[0];
                System.Text.StringBuilder sb = new System.Text.StringBuilder("播放[map] " + uid + " ->");
                if (segmentMode)
                {
                    string segment = _players[p].GetStringChooserParamValue("Segment");
                    int index = Stages.FindIndex(delegate(Stage s) { return s.SegmentName == segment; });
                    sb.Append(" segment='").Append(segment).Append("':").Append(index);
                }
                {
                    IEnumerable clips = Read(Read(_players[p], "animation"), "clips") as IEnumerable;
                    if (clips == null) { sb.Append(" <no clips>"); }
                    else
                        foreach (object clip in clips)
                        {
                            if (!object.Equals(Read(clip, "playbackEnabled"), true)) continue;
                            string name = Read(clip, "animationName") as string;
                            int index = Stages.FindIndex(delegate(Stage s) { return s.Animation == name; });
                            sb.Append(' ').Append(name).Append(':').Append(index);
                        }
                }
                Quest3TriggerUIPlugin.Log.LogInfo(sb.ToString());
            }
        }
        internal void Step(SuperController sc, bool next, Action<string> status)
        {
            int current = -1;
            bool segmentMode = Stages.Count > 0 && !string.IsNullOrEmpty(Stages[0].SegmentName);
            HashSet<int> common = null;
            for (int p = 0; p < _players.Count; p++)
            {
                string[] ids = _targets[p].Split('\n');
                Atom atom = sc.GetAtomByUid(ids[0]);
                if (atom == null || atom.GetStorableByID(ids[1]) != _players[p]) throw new InvalidOperationException("阶段播放器已变化");
                HashSet<int> seen = new HashSet<int>();
                if (segmentMode)
                {
                    string segment = _players[p].GetStringChooserParamValue("Segment");
                    int index = Stages.FindIndex(delegate(Stage s) { return s.SegmentName == segment; });
                    if (index >= 0) seen.Add(index);
                }
                IEnumerable clips = Read(Read(_players[p], "animation"), "clips") as IEnumerable;
                if (clips == null)
                {
                    if (!segmentMode) throw new InvalidOperationException("未读取到 Timeline 当前播放状态");
                }
                else
                {
                    foreach (object clip in clips)
                    {
                        if (!object.Equals(Read(clip, "playbackEnabled"), true)) continue;
                        string name = Read(clip, "animationName") as string;
                        int index = Stages.FindIndex(delegate(Stage s) { return s.Animation == name; });
                        if (index >= 0) seen.Add(index);
                    }
                }
                if (seen.Count == 0) continue;
                if (common == null) common = seen;
                else common.IntersectWith(seen);
            }
            if (common != null)
            {
                if (common.Count == 0)
                {
                    status("播放：阶段正在混合或各角色阶段不同，请待转场结束。");
                    LogStepReject(sc, "no common stage across players");
                    return;
                }
                // Stray leftover clips (e.g. a stale 'Anim 1' still enabled on one
                // character) must not veto navigation: the shared stage wins.
                // Multiple shared stages mean a real cross-fade; keep the cursor
                // stage so next/prev does not skip past it.
                if (common.Contains(_cursor)) current = _cursor;
                else
                    foreach (int index in common)
                        if (index > current) current = index;
            }
            if (current < 0) current = _cursor;
            if (current < 0 && !next) { status("播放：尚无当前阶段，请用下一个启动首阶段。"); return; }
            int selected = current < 0 ? 0 : current + (next ? 1 : -1);
            if (selected < 0 || selected >= Stages.Count) { status("播放：已到阶段边界。"); return; }
            Stage target = Stages[selected];
            if (target.DirectAction != null)
            {
                foreach (JSONStorable player in _players)
                {
                    JSONStorableAction direct = player.GetAction(target.DirectAction);
                    if (direct != null && direct.actionCallback != null) direct.actionCallback();
                }
                _cursor = selected;
                status("播放：已调用 " + target.DirectAction + "，阶段 " + target.Number + "。");
                return;
            }
            Atom buttonAtom = sc.GetAtomByUid(target.ButtonId);
            if (buttonAtom == null || buttonAtom.GetStorableByID(target.ButtonStorable) != target.Button) throw new InvalidOperationException("原场景按钮已变化");
            if ((target.ButtonAction ?? "").StartsWith("story:", StringComparison.Ordinal))
            {
                object button = StoryButton(target.Button, target.ButtonAction);
                if (button == null) throw new InvalidOperationException("原场景按钮已变化");
                button.GetType().GetMethod("DoClickTrigger", Type.EmptyTypes).Invoke(button, null);
            }
            else if (string.IsNullOrEmpty(target.ButtonAction)) ClickMethod(target.Button).Invoke(target.Button, null);
            else
            {
                JSONStorableAction action = target.Button.GetAction(target.ButtonAction);
                if (action == null || action.actionCallback == null) throw new InvalidOperationException("原插件按钮动作已变化");
                action.actionCallback();
            }
            _cursor = selected;
            status("播放：已调用原按钮，阶段 " + (target.SegmentName ?? target.Number.ToString()) + "。");
        }
    }
}

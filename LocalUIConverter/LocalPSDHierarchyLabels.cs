using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Starlight.LocalUIConverter;
using UnityEditor;
using UnityEngine;

namespace Starlight.Game.Editor.LocalUIConverter
{
    [InitializeOnLoad]
    internal static class LocalPSDHierarchyLabels
    {
        private static GUIStyle labelStyle;
        private static LocalPSDNodeDescription[] markers;
        private static readonly Dictionary<int, WindowState> Windows = new Dictionary<int, WindowState>();
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Assembly EditorAssembly = typeof(EditorWindow).Assembly;
        private static readonly PropertyInfo CurrentView = EditorAssembly.GetType("UnityEditor.GUIView")?
            .GetProperty("current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo ActualWindow = EditorAssembly.GetType("UnityEditor.HostView")?
            .GetProperty("actualView", InstanceFlags);

        private sealed class WindowState
        {
            public float lastY = float.PositiveInfinity;
            public EventType lastEvent;
            public Dictionary<int, List<LocalPSDNodeDescription>> routes;
        }

        static LocalPSDHierarchyLabels()
        {
            EditorApplication.hierarchyWindowItemOnGUI += DrawLabel;
            EditorApplication.hierarchyChanged += Invalidate;
            Undo.undoRedoPerformed += Invalidate;
            ObjectChangeEvents.changesPublished += ObjectsChanged;
        }

        private static void ObjectsChanged(ref ObjectChangeEventStream changes)
        {
            for (int i = 0; i < changes.length; i++)
            {
                var kind = changes.GetEventType(i);
                if (kind == ObjectChangeKind.ChangeGameObjectStructure) { Invalidate(); return; }
                if (kind != ObjectChangeKind.ChangeGameObjectOrComponentProperties) continue;
                changes.GetChangeGameObjectOrComponentPropertiesEvent(i, out var change);
                if (EditorUtility.InstanceIDToObject(change.instanceId) is LocalPSDNodeDescription) { Invalidate(); return; }
            }
        }

        internal static void Invalidate()
        {
            markers = null;
            Windows.Clear();
            EditorApplication.RepaintHierarchyWindow();
        }

        // Use the window actually drawing, rather than the focused/hovered window:
        // separate Hierarchy windows can have completely different expansion states.
        private static EditorWindow DrawingWindow()
        {
            var view = CurrentView?.GetValue(null);
            return view != null && ActualWindow?.DeclaringType.IsInstanceOfType(view) == true
                ? ActualWindow.GetValue(view) as EditorWindow : null;
        }

        private static object Member(object target, string name) => target?.GetType().GetProperty(name, InstanceFlags)?.GetValue(target);

        internal static bool TryVisibleRows(EditorWindow window, out HashSet<int> visible, out bool searching)
        {
            visible = null;
            searching = false;
            if (window == null || window.GetType().FullName != "UnityEditor.SceneHierarchyWindow") return false;
            try
            {
                var hierarchy = Member(window, "sceneHierarchy");
                var data = Member(Member(hierarchy, "treeView"), "data");
                var getRows = data?.GetType().GetMethod("GetRows", InstanceFlags, null, Type.EmptyTypes, null);
                if (!(getRows?.Invoke(data, null) is IEnumerable rows)) return false;
                visible = new HashSet<int>();
                foreach (var item in rows)
                    if (Member(item, "id") is int id) visible.Add(id);
                searching = !string.IsNullOrEmpty(Member(window, "searchText") as string);
                return true;
            }
            catch (TargetInvocationException) { return false; }
            catch (MemberAccessException) { return false; }
        }

        internal static Dictionary<int, List<LocalPSDNodeDescription>> BuildRoutes(
            IEnumerable<LocalPSDNodeDescription> source, ISet<int> visible, bool searching)
        {
            var result = new Dictionary<int, List<LocalPSDNodeDescription>>();
            foreach (var marker in source)
            {
                if (marker == null || EditorUtility.IsPersistent(marker) || !marker.gameObject.scene.IsValid()) continue;
                Transform target = marker.transform;
                // Search results are a flat filtered list. Do not pull excluded children
                // into a parent that happens to match the search query.
                if (searching && !visible.Contains(target.gameObject.GetInstanceID())) continue;
                while (target != null && !visible.Contains(target.gameObject.GetInstanceID()))
                {
                    if ((target.gameObject.hideFlags & HideFlags.HideInHierarchy) != 0) { target = null; break; }
                    target = target.parent;
                }
                if (target == null) continue;
                int id = target.gameObject.GetInstanceID();
                if (!result.TryGetValue(id, out var list)) result[id] = list = new List<LocalPSDNodeDescription>();
                list.Add(marker);
            }
            return result;
        }

        private static List<LocalPSDNodeDescription> RowMarkers(GameObject node, Rect row)
        {
            var window = DrawingWindow();
            if (window != null)
            {
                int windowId = window.GetInstanceID();
                if (!Windows.TryGetValue(windowId, out var state)) Windows[windowId] = state = new WindowState();
                // Row y resets at the start of each GUI pass. Read the entire tree's
                // visible rows, including rows outside the viewport, only once per pass.
                if (state.routes == null || row.y <= state.lastY || state.lastEvent != Event.current.type)
                {
                    state.routes = null;
                    if (TryVisibleRows(window, out var visible, out bool searching))
                    {
                        markers ??= Resources.FindObjectsOfTypeAll<LocalPSDNodeDescription>();
                        state.routes = BuildRoutes(markers, visible, searching);
                    }
                }
                state.lastY = row.y;
                state.lastEvent = Event.current.type;
                if (state.routes != null) return state.routes.TryGetValue(node.GetInstanceID(), out var list) ? list : null;
            }
            // Unity internals may change in another editor version; retain own-node labels.
            return node.TryGetComponent<LocalPSDNodeDescription>(out var own) ? new List<LocalPSDNodeDescription> { own } : null;
        }

        private static string Tooltip(GameObject owner, IEnumerable<LocalPSDNodeDescription> source)
        {
            var text = new StringBuilder("节点描述（含折叠子节点）");
            foreach (var marker in source)
            {
                var names = new List<string>();
                for (var t = marker.transform; t != null && t != owner.transform; t = t.parent) names.Add(t.name);
                names.Reverse();
                Label(marker.type, out string type, out _);
                text.Append("\n\n").Append(names.Count == 0 ? "本节点" : string.Join("/", names)).Append(" · ").Append(type)
                    .Append('\n').Append(string.IsNullOrWhiteSpace(marker.description) ? "（未填写描述）" : marker.description);
            }
            return text.ToString();
        }

        private static void DrawLabel(int instanceId, Rect row)
        {
            var node = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
            if (node == null) return;
            var source = RowMarkers(node, row);
            if (source == null || source.Count == 0) return;
            // Leave the far right edge available for Unity's prefab controls.
            if (row.width < 80) return;
            labelStyle ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                clipping = TextClipping.Clip,
                padding = new RectOffset(),
                normal = { textColor = Color.white }
            };
            var groups = source.Where(m => m != null).GroupBy(m => m.type).OrderBy(g => g.Key).ToArray();
            var badges = new List<(GUIContent Content, Color Color, float Width)>();
            float used = 0, budget = Mathf.Min(320, row.width - 30);
            for (int i = 0; i < groups.Length; i++)
            {
                var group = groups[i];
                Label(group.Key, out string text, out Color color);
                if (group.Count() > 1 || group.Any(marker => marker.gameObject != node)) text += "×" + group.Count();
                var content = new GUIContent(text, Tooltip(node, group));
                float width = Mathf.Max(44, labelStyle.CalcSize(content).x + 10);
                if (used + width + (i < groups.Length - 1 ? 33 : 0) > budget)
                {
                    badges.Add((new GUIContent("+" + (groups.Length - i), Tooltip(node, groups.Skip(i).SelectMany(g => g))), new Color(.4f, .43f, .5f), 30));
                    break;
                }
                badges.Add((content, color, width));
                used += width + 3;
            }
            float x = row.xMax - 18;
            for (int i = badges.Count - 1; i >= 0; i--)
            {
                var badge = badges[i];
                var rect = new Rect(x - badge.Width, row.y + 1, badge.Width, row.height - 2);
                Color color = badge.Color;
                if (!node.activeInHierarchy) color.a = .65f;
                EditorGUI.DrawRect(rect, color);
                GUI.Label(rect, badge.Content, labelStyle);
                x -= badge.Width + 3;
            }
        }

        private static void Label(LocalPSDNodeType type, out string text, out Color color)
        {
            switch (type)
            {
                case LocalPSDNodeType.Button: text = "按钮"; color = new Color(0.18f, 0.46f, 0.78f); break;
                case LocalPSDNodeType.Text: text = "文字"; color = new Color(0.17f, 0.52f, 0.34f); break;
                case LocalPSDNodeType.Scroll: text = "滚动"; color = new Color(0.48f, 0.34f, 0.70f); break;
                case LocalPSDNodeType.Toggle: text = "开关"; color = new Color(0.12f, 0.51f, 0.55f); break;
                case LocalPSDNodeType.Slider: text = "滑条"; color = new Color(0.68f, 0.35f, 0.23f); break;
                case LocalPSDNodeType.Description: text = "备注"; color = new Color(0.40f, 0.45f, 0.53f); break;
                default: text = "待分类"; color = new Color(0.66f, 0.47f, 0.13f); break;
            }
        }
    }

    [CustomEditor(typeof(LocalPSDNodeDescription)), CanEditMultipleObjects]
    internal sealed class LocalPSDNodeDescriptionInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            if (DrawDefaultInspector()) LocalPSDHierarchyLabels.Invalidate();
        }
    }
}

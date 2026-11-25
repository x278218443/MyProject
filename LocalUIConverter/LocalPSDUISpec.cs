using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Starlight.Game.Editor.LocalUIConverter
{
    internal static class LocalPSDUISpec
    {
        public static string Export(string prefabPath, string bindPath, Type bindingType)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var binding = prefab != null ? prefab.GetComponent(bindingType) : null;
            if (binding == null) throw new InvalidOperationException("未找到已保存的 Prefab 绑定组件。");
            var variables = new SerializedObject(binding).FindProperty("_variables._variables");
            if (variables == null || !variables.isArray)
                throw new InvalidOperationException("无法读取 Prefab 的序列化绑定列表。");

            var nodes = prefab.GetComponentsInChildren<Transform>(true);
            var notes = LocalPSDRoundTrip.Load(prefabPath).nodes
                .Where(record => !string.IsNullOrEmpty(record.objectId) && !string.IsNullOrWhiteSpace(record.control?.note))
                .GroupBy(record => record.objectId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().control.note, StringComparer.Ordinal);
            var nodeNotes = new Dictionary<Transform, string>();
            foreach (var node in nodes)
                if (notes.Count > 0 && notes.TryGetValue(LocalPSDRoundTrip.ObjectId(node.gameObject), out string note))
                    nodeNotes[node] = note;

            var text = new StringBuilder();
            text.Append("# ").Append(Escape(Path.GetFileNameWithoutExtension(prefabPath))).AppendLine(" UI Spec");
            text.AppendLine();
            text.Append("- Prefab ").AppendLine(Escape(prefabPath));
            text.Append("- Bind 脚本 ").AppendLine(Escape(bindPath));
            text.AppendLine("- 下列节点、组件和引用取自已保存的 Prefab。备注用于说明用途，新增节点可没有备注。");
            text.AppendLine();
            text.AppendLine("## Bind 字段");
            text.AppendLine();
            text.AppendLine("| 字段 | 绑定类型 | 节点路径 | 默认状态 | 备注 |");
            text.AppendLine("| --- | --- | --- | --- | --- |");
            var boundNodes = new HashSet<Transform>();
            for (int index = 0; index < variables.arraySize; index++)
            {
                var variable = variables.GetArrayElementAtIndex(index);
                string name = variable.FindPropertyRelative("_name").stringValue;
                var value = variable.FindPropertyRelative("_objectValue").objectReferenceValue;
                var target = value is Component component ? component.transform : (value as GameObject)?.transform;
                bool inPrefab = target != null && target.IsChildOf(prefab.transform);
                if (inPrefab) boundNodes.Add(target);
                Row(text, name, value != null ? value.GetType().Name : "—",
                    inPrefab ? NodePath(prefab.transform, target) : value != null ? value.name : "—",
                    inPrefab ? State(prefab.transform, target) : value != null ? "外部引用" : "未绑定",
                    inPrefab && nodeNotes.TryGetValue(target, out string note) ? note : "");
            }

            var unboundNotes = nodeNotes.Where(pair => !boundNodes.Contains(pair.Key)).ToArray();
            if (unboundNotes.Length > 0)
            {
                text.AppendLine();
                text.AppendLine("## 有备注的未绑定节点");
                text.AppendLine();
                text.AppendLine("| 节点路径 | 备注 |");
                text.AppendLine("| --- | --- |");
                foreach (var pair in unboundNotes)
                    Row(text, NodePath(prefab.transform, pair.Key), pair.Value);
            }

            text.AppendLine();
            text.AppendLine("## 绑定控件的内部引用");
            text.AppendLine();
            text.AppendLine("| 节点路径 | 引用 | 目标 |");
            text.AppendLine("| --- | --- | --- |");
            foreach (var node in nodes.Where(boundNodes.Contains))
            {
                string nodePath = NodePath(prefab.transform, node);
                void WriteLink(string field, Component target) => Row(text, nodePath, field,
                    target == null ? "未设置" : NodePath(prefab.transform, target.transform));
                if (node.TryGetComponent<Selectable>(out var selectable))
                    WriteLink("targetGraphic", selectable.targetGraphic);
                if (node.TryGetComponent<ScrollRect>(out var scroll))
                {
                    WriteLink("viewport", scroll.viewport);
                    WriteLink("content", scroll.content);
                }
                if (selectable is Toggle toggle)
                {
                    WriteLink("graphic", toggle.graphic);
                    WriteLink("group", toggle.group);
                }
                if (selectable is Slider slider)
                {
                    WriteLink("fillRect", slider.fillRect);
                    WriteLink("handleRect", slider.handleRect);
                }
            }

            text.AppendLine();
            text.AppendLine("## Prefab 层级");
            text.AppendLine();
            void WriteNode(Transform node, int depth)
            {
                var components = node.GetComponents<Component>().Where(component =>
                        !(component is Transform) && !(component is CanvasRenderer))
                    .Select(component => component != null ? component.GetType().Name : "Missing Script");
                string types = string.Join(", ", components);
                text.Append(' ', depth * 2).Append("- ").Append(Escape(node.name));
                if (types.Length > 0) text.Append(" (").Append(types).Append(')');
                if (!node.gameObject.activeSelf) text.Append(" [默认关闭]");
                text.AppendLine();
                foreach (Transform child in node) WriteNode(child, depth + 1);
            }
            WriteNode(prefab.transform, 0);

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string guid = AssetDatabase.AssetPathToGUID(prefabPath);
            string directory = Path.Combine(projectRoot, "__Temp", "LocalUIConverter", "Specs", guid);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "UI-Spec.md");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".writing";
            try
            {
                File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return path;
        }

        private static string NodePath(Transform root, Transform node)
        {
            if (node == root) return root.name;
            if (!node.IsChildOf(root)) return node.name + "（外部引用）";
            return root.name + "/" + AnimationUtility.CalculateTransformPath(node, root);
        }

        private static string State(Transform root, Transform node)
        {
            if (!node.gameObject.activeSelf) return "默认关闭";
            for (Transform parent = node.parent; parent != null && parent.IsChildOf(root); parent = parent.parent)
                if (!parent.gameObject.activeSelf) return "随父节点关闭";
            return "默认开启";
        }

        private static void Row(StringBuilder text, params string[] cells) =>
            text.Append("| ").Append(string.Join(" | ", cells.Select(Escape))).AppendLine(" |");

        private static string Escape(string value) => (value ?? "").Replace("\\", "\\\\").Replace("|", "\\|")
            .Replace("`", "\\`").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", " ").Replace("\n", " ");
    }
}

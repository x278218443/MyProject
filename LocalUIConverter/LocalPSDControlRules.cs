using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using ControlType = Starlight.Game.Editor.LocalUIConverter.LocalPSDInteractionPreview.ComponentType;

namespace Starlight.Game.Editor.LocalUIConverter
{
    internal static class LocalPSDControlRules
    {
        private static readonly Regex BindingName = new Regex(@"^_[a-z][A-Za-z0-9_]*\z");
        private static readonly Regex Words = new Regex(@"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|[0-9]+");
        private static readonly HashSet<string> LayerPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "img", "image", "txt", "text", "btn", "button", "bg", "icon", "p", "g", "gm", "active", "common" };
        private static readonly (string Cue, string Name)[] NameCues =
        {
            ("今日不再提示", "hideReminderToday"), ("不再提示", "hideReminder"),
            ("任一货币", "paymentHint"), ("当前兑换项目名称", "exchangeItemTitle"),
            ("右侧货币", "payWithRightCurrency"), ("左侧货币", "payWithLeftCurrency"),
            ("前往活动", "openActivity"), ("打开活动", "openActivity"),
            ("关闭", "close"), ("返回", "back"), ("取消", "cancel"), ("确认", "confirm"),
            ("领取", "claim"), ("购买", "purchase"), ("支付", "pay"), ("兑换", "exchange"),
            ("标题", "title"), ("列表", "itemList"), ("搜索", "search")
        };

        public static bool IsBindingName(string name) => name != null && BindingName.IsMatch(name);
        internal static bool IsDescriptionOnly(string type) => type == "None" || type == "Description";

        public static Type ProjectTextType => LocalPSDProjectActions.FindType("Starlight.Game.UITextMeshProUGUI")
            ?? typeof(TextMeshProUGUI);

        public static Type ComponentType(string type)
        {
            switch (type)
            {
                case nameof(ControlType.Button): return LocalPSDProjectActions.FindType("Starlight.Core.UI.UIButton")
                    ?? throw new InvalidOperationException("当前项目未提供 Starlight.Core.UI.UIButton。");
                case nameof(ControlType.Text): return ProjectTextType;
                case nameof(ControlType.Scroll): return typeof(ScrollRect);
                case nameof(ControlType.Toggle): return typeof(Toggle);
                case nameof(ControlType.Slider): return typeof(Slider);
                default: throw new InvalidDataException("未知的 PSD 组件类型: " + type);
            }
        }

        public static string SuggestName(ControlType type, string layerName, string note)
        {
            if (IsBindingName(layerName)) return layerName;
            string suffix = type.ToString();
            string stem = NameCues.FirstOrDefault(item => (note ?? "").Contains(item.Cue)
                || (layerName ?? "").Contains(item.Cue)).Name;
            if (string.IsNullOrEmpty(stem) && !string.IsNullOrEmpty(layerName) && !Regex.IsMatch(layerName, @"^[A-Za-z]\d+$"))
            {
                var parts = Words.Matches(layerName).Cast<Match>().Select(word => word.Value)
                    .Where(word => !LayerPrefixes.Contains(word)).ToArray();
                int first = Math.Max(0, parts.Length - 4);
                for (int index = first; index < parts.Length; index++)
                {
                    string part = parts[index].ToLowerInvariant();
                    if (string.IsNullOrEmpty(stem))
                    {
                        if (char.IsLetter(part[0])) stem = part;
                    }
                    else stem += char.ToUpperInvariant(part[0]) + part.Substring(1);
                }
            }
            if (string.IsNullOrEmpty(stem)) return "_" + suffix.ToLowerInvariant();
            return "_" + (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? stem : stem + suffix);
        }

        public static void Apply(GameObject root, LocalPSDDocument document,
            IDictionary<GameObject, LocalPSDSourceRecord> records, bool rename)
        {
            var marked = new HashSet<LocalPSDMarkedControl>(document.artboard.Descendants()
                .Where(layer => layer.control != null).Select(layer => layer.control));
            if (marked.Count == 0) return;
            var nodes = root.GetComponentsInChildren<Transform>(true).Select(transform => transform.gameObject).ToArray();
            var controls = nodes.Where(node => records.TryGetValue(node, out var record) && record.control != null).ToArray();
            marked.ExceptWith(controls.Select(node => records[node].control));
            if (marked.Count != 0)
            {
                string layers = string.Join("、", document.artboard.Descendants().Where(layer => marked.Contains(layer.control)).Select(layer => layer.name));
                throw new InvalidOperationException("以下标记图层在生成或通用预设替换后没有独立节点，请改选保留的图层: " + layers);
            }

            var names = new HashSet<string>(nodes.Where(node => !records.TryGetValue(node, out var record) || record.control == null)
                .Select(node => node.name), StringComparer.Ordinal);
            foreach (GameObject node in controls)
            {
                var record = records[node];
                if (IsDescriptionOnly(record.control.type)) continue;
                EnsureComponent(node, record.control.type);
                if (!rename) continue;
                string baseName = record.control.name;
                if (!IsBindingName(baseName))
                    baseName = SuggestName((ControlType)Enum.Parse(typeof(ControlType), record.control.type), record.initialName, record.control.note);
                string name = baseName;
                for (int suffix = 2; !names.Add(name); suffix++) name = baseName + suffix;
                node.name = name;
                if (PrefabUtility.IsPartOfPrefabInstance(node)) PrefabUtility.RecordPrefabInstancePropertyModifications(node);
                record.control.name = name;
            }
        }

        internal static void EnsureComponent(GameObject node, string type)
        {
            Type expected = ComponentType(type);
            if (node.GetComponent(expected) != null) return;

            if (type == nameof(ControlType.Text))
            {
                var graphic = node.GetComponent<Graphic>();
                if (graphic == null) node.AddComponent(expected);
                else if (graphic.GetType() == typeof(TextMeshProUGUI)) UpgradeComponent(graphic, expected);
                else throw new InvalidOperationException($"图层「{node.name}」已有 {graphic.GetType().Name}，请将 Text 标记匹配到 PSD 文字层。");
                return;
            }

            var selectable = node.GetComponent<Selectable>();
            if (typeof(Selectable).IsAssignableFrom(expected) && selectable != null
                && !(type == nameof(ControlType.Button) && selectable.GetType() == typeof(Button)))
                throw new InvalidOperationException($"图层「{node.name}」已有 {selectable.GetType().Name}，无法同时添加 {expected.Name}。");

            if (type == nameof(ControlType.Button))
            {
                var button = (Button)(selectable == null ? node.AddComponent(expected) : UpgradeComponent(selectable, expected));
                if (button.targetGraphic == null)
                    button.targetGraphic = node.GetComponent<Image>() ?? node.GetComponentInChildren<Image>(true);
                if (button.targetGraphic == null)
                {
                    button.targetGraphic = node.GetComponent<Graphic>();
                    if (button.targetGraphic == null)
                    {
                        var image = node.AddComponent<Image>();
                        image.color = Color.clear;
                        button.targetGraphic = image;
                        button.transition = Selectable.Transition.None;
                    }
                }
                button.targetGraphic.raycastTarget = true;
                return;
            }

            var component = node.AddComponent(expected);
            switch (component)
            {
                case ScrollRect scroll:
                    scroll.viewport = FindRect(node.transform, "viewport");
                    scroll.content = FindRect((Transform)scroll.viewport ?? node.transform, "content");
                    break;
                case Toggle toggle:
                    toggle.targetGraphic = node.GetComponent<Image>();
                    toggle.graphic = FindRect(node.transform, "checkmark")?.GetComponent<Graphic>();
                    break;
                case Slider slider:
                    slider.targetGraphic = node.GetComponent<Image>();
                    slider.fillRect = FindRect(node.transform, "fill") ?? FindRect(FindRect(node.transform, "fill area"), "fill");
                    slider.handleRect = FindRect(node.transform, "handle") ?? FindRect(FindRect(node.transform, "handle slide area"), "handle");
                    break;
            }
        }

        private static RectTransform FindRect(Transform parent, string name)
        {
            if (parent == null) return null;
            foreach (Transform child in parent)
                if (child.name.TrimStart('_').Equals(name, StringComparison.OrdinalIgnoreCase)) return child as RectTransform;
            return null;
        }

        private static Component UpgradeComponent(Component source, Type expected)
        {
            var node = source.gameObject;
            var scratch = new GameObject("PSD component copy", typeof(RectTransform));
            SceneManager.MoveGameObjectToScene(scratch, node.scene);
            scratch.SetActive(false);
            try
            {
                var copy = scratch.AddComponent(source.GetType());
                EditorUtility.CopySerialized(source, copy);
                bool enabled = ((Behaviour)source).enabled;
                UnityEngine.Object.DestroyImmediate(source);
                var target = node.AddComponent(expected);
                EditorUtility.CopySerializedManagedFieldsOnly(copy, target);
                ((Behaviour)target).enabled = enabled;
                return target;
            }
            finally { UnityEngine.Object.DestroyImmediate(scratch); }
        }
    }
}

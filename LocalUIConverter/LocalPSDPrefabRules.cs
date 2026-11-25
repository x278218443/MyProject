using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Starlight.Game.Editor.LocalUIConverter
{
    internal static class LocalPSDPrefabRules
    {
        // Only source PSD nodes participate, so imported common prefabs are never
        // accidentally scanned again for placeholder names.
        public static void ShareImages(IEnumerable<GameObject> sourceNodes)
        {
            var nodes = sourceNodes.Where(n => n != null).ToList();
            var bases = new Dictionary<string, UnityEngine.UI.Image>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                var image = node.GetComponent<UnityEngine.UI.Image>();
                if (!node.name.StartsWith("G_", StringComparison.Ordinal) && image != null && image.sprite != null && !bases.ContainsKey(node.name)) bases.Add(node.name, image);
            }
            foreach (var node in nodes)
            {
                if (!node.name.StartsWith("G_", StringComparison.Ordinal)) continue;
                var image = node.GetComponent<UnityEngine.UI.Image>();
                if (image == null) continue;
                if (bases.TryGetValue(node.name.Substring(2), out var original)) image.sprite = original.sprite;
                else if (!node.name.StartsWith("G_P_", StringComparison.Ordinal)) Debug.LogWarning("[Local PSD UI] 未找到共享图片基节点: " + node.name);
            }
        }

        public static void ReplaceCommon(IList<GameObject> sourceNodes, LocalUIConverterSettings settings,
            IDictionary<GameObject, LocalPSDLayer> layers = null, Action<GameObject, GameObject, string> replaced = null)
        {
            foreach (var rule in settings.prefabRules)
            {
                if (rule == null || rule.prefab == null || string.IsNullOrEmpty(rule.nodeName)) continue;
                // Snapshot matches before mutating the hierarchy.
                foreach (var node in sourceNodes.Where(n => n != null && (layers != null && layers.TryGetValue(n, out var layer)
                    && !string.IsNullOrEmpty(layer.replacementKey) ? layer.replacementKey : n.name) == rule.nodeName).ToArray())
                    Replace(node, rule.prefab, true, !string.IsNullOrEmpty(layers != null && layers.TryGetValue(node, out var original) ? original.replacementKey : null),
                        (source, target) => replaced?.Invoke(source, target, rule.nodeName));
            }
        }

        private sealed class Entry
        {
            public GameObject node;
            public string name, path;
            public bool shared;
            public int state; // 0 pending, 1 in dependency stack, 2 saved
        }

        public static void ExtractPrefabs(IList<GameObject> sourceNodes, string folder, Action<GameObject, GameObject, string> replaced = null,
            IDictionary<GameObject, LocalPSDLayer> layers = null)
        {
            var entries = new List<Entry>();
            var definitions = new Dictionary<string, Entry>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in sourceNodes)
            {
                if (node == null) continue;
                string key = layers != null && layers.TryGetValue(node, out var original) && !string.IsNullOrEmpty(original.prefabKey) ? original.prefabKey : node.name;
                bool shared = key.StartsWith("G_P_", StringComparison.Ordinal);
                if (!shared && !key.StartsWith("P_", StringComparison.Ordinal)) continue;
                string baseName = shared ? key.Substring(2) : key;
                bool circular = false;
                for (Transform parent = node.transform.parent; parent != null; parent = parent.parent)
                    if (parent.name == baseName) { circular = true; break; }
                if (circular)
                {
                    Debug.LogWarning("[Local PSD UI] 跳过循环子预设体: " + node.name);
                    continue;
                }
                var entry = new Entry { node = node, name = baseName, shared = shared };
                if (!shared)
                {
                    counts.TryGetValue(baseName, out int occurrence);
                    counts[baseName] = occurrence + 1;
                    string filename = LocalPSDPrefabGenerator.SafeName(baseName) + (occurrence == 0 ? "" : "_" + (occurrence - 1));
                    entry.path = folder + "/" + filename + ".prefab";
                    if (!paths.Add(entry.path)) throw new InvalidDataException("子预设体文件名冲突: " + entry.path);
                    definitions[baseName] = entry;
                }
                entries.Add(entry);
            }

            bool Resolve(Entry entry)
            {
                if (entry.state == 2) return true;
                if (entry.state == 1)
                {
                    Debug.LogWarning("[Local PSD UI] 检测到循环共享引用，保留原节点: " + entry.name);
                    return false;
                }
                if (entry.node == null) return false;
                entry.state = 1;
                if (entry.shared)
                {
                    if (!definitions.TryGetValue(entry.name, out var definition) || !Resolve(definition))
                    {
                        Debug.LogWarning("[Local PSD UI] 未找到可用共享子预设体: G_" + entry.name);
                        entry.state = 2;
                        return false;
                    }
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(definition.path);
                    if (asset != null) Replace(entry.node, asset, true, true, (source, target) => replaced?.Invoke(source, target, ""));
                }
                else
                {
                    // Resolve descendants and their shared dependencies before saving
                    // the parent, preserving nested prefab references.
                    foreach (var child in entries.Where(e => e != entry && e.node != null && e.node.transform.IsChildOf(entry.node.transform)).ToArray()) Resolve(child);
                    var asset = PrefabUtility.SaveAsPrefabAsset(entry.node, entry.path);
                    if (asset == null) throw new IOException("无法保存子预设体: " + entry.path);
                    Replace(entry.node, asset, false, true, (source, target) => replaced?.Invoke(source, target, ""));
                }
                entry.state = 2;
                return true;
            }

            foreach (var entry in entries.Where(e => !e.shared)) Resolve(entry);
            foreach (var entry in entries.Where(e => e.shared)) Resolve(entry);
        }

        private static GameObject Replace(GameObject source, GameObject prefab, bool scaleToFit, bool keepName, Action<GameObject, GameObject> replaced = null)
        {
            var parent = source.transform.parent;
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            if (instance == null) throw new InvalidOperationException("无法创建预设体实例: " + prefab.name);
            instance.transform.SetSiblingIndex(source.transform.GetSiblingIndex());
            var original = source.GetComponent<RectTransform>();
            var rect = instance.GetComponent<RectTransform>();
            if (rect != null && original != null)
            {
                rect.anchorMin = original.anchorMin;
                rect.anchorMax = original.anchorMax;
                rect.pivot = original.pivot;
                rect.anchoredPosition3D = original.anchoredPosition3D;
                rect.localRotation = original.localRotation;
                if (scaleToFit && rect.sizeDelta.x != 0 && rect.sizeDelta.y != 0)
                {
                    var scale = rect.localScale;
                    rect.localScale = new Vector3(scale.x * original.sizeDelta.x / rect.sizeDelta.x,
                        scale.y * original.sizeDelta.y / rect.sizeDelta.y, keepName ? 1 : scale.z);
                }
                else
                {
                    rect.sizeDelta = original.sizeDelta;
                    rect.localScale = original.localScale;
                }
            }
            else
            {
                instance.transform.localPosition = source.transform.localPosition;
                instance.transform.localRotation = source.transform.localRotation;
                instance.transform.localScale = source.transform.localScale;
            }
            if (keepName) instance.name = source.name;
            instance.SetActive(source.activeSelf);
            replaced?.Invoke(source, instance);
            UnityEngine.Object.DestroyImmediate(source);
            return instance;
        }
    }
}

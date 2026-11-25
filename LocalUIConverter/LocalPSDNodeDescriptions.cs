using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Starlight.LocalUIConverter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Starlight.Game.Editor.LocalUIConverter
{
    [Serializable, DataContract] internal sealed class LocalPSDDescriptionArchive
    {
        [DataMember] public string prefabGuid;
        [DataMember] public bool editing;
        [DataMember] public List<LocalPSDDescriptionEntry> nodes = new List<LocalPSDDescriptionEntry>();
    }
    [Serializable, DataContract] internal sealed class LocalPSDDescriptionEntry
    {
        [DataMember] public string objectId, path, type, description;
    }

    internal static class LocalPSDNodeDescriptions
    {
        public static string ArchivePath(string prefabPath) => Path.Combine(Directory.GetParent(Application.dataPath).FullName,
            "__Temp/LocalUIConverter/NodeDescriptions", Path.GetFileNameWithoutExtension(prefabPath) + ".json");

        private static LocalPSDDescriptionArchive Read(string prefabPath)
        {
            string path = ArchivePath(prefabPath);
            var archive = File.Exists(path) ? LocalPSDJson.Deserialize<LocalPSDDescriptionArchive>(File.ReadAllText(path)) : null;
            string guid = AssetDatabase.AssetPathToGUID(prefabPath);
            if (archive != null && archive.prefabGuid != guid)
                throw new InvalidOperationException("节点描述存档属于另一个同名预设体，请先处理同名存档: " + path);
            return archive;
        }

        private static void Write(string prefabPath, LocalPSDDescriptionArchive archive)
        {
            string path = ArchivePath(prefabPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // Publish atomically so interrupted writes do not truncate the last archive.
            string temporary = path + ".writing";
            File.WriteAllText(temporary, LocalPSDJson.Serialize(archive));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        internal static void SaveStage(PrefabStage stage)
        {
            if (!stage.scene.isDirty) return;
            var save = typeof(PrefabStage).GetMethod("SavePrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (save == null) throw new InvalidOperationException("当前 Unity 版本未提供 PrefabStage.SavePrefab。");
            if (save.Invoke(stage, null) is bool success && !success) throw new IOException("保存预设体失败: " + stage.assetPath);
        }

        private static string NodePath(Transform root, Transform node)
        {
            var names = new List<string>();
            while (node != root) { names.Add(node.name); node = node.parent; }
            names.Reverse();
            return string.Join("/", names);
        }

        // root is either the saved asset itself or its saved Prefab stage with the same hierarchy.
        private static IEnumerable<(GameObject Asset, GameObject Node)> Pairs(GameObject asset, GameObject root)
        {
            yield return (asset, root);
            if (asset.transform.childCount != root.transform.childCount)
                throw new InvalidOperationException("预设体层级尚未保存，请保存后重试。");
            for (int i = 0; i < asset.transform.childCount; i++)
                foreach (var pair in Pairs(asset.transform.GetChild(i).gameObject, root.transform.GetChild(i).gameObject)) yield return pair;
        }

        public static void Initialize(string prefabPath, IDictionary<GameObject, LocalPSDSourceRecord> records)
        {
            foreach (var pair in records)
            {
                if (pair.Key == null || pair.Value.control == null) continue;
                var marker = pair.Key.GetComponent<LocalPSDNodeDescription>() ?? pair.Key.AddComponent<LocalPSDNodeDescription>();
                marker.type = (LocalPSDNodeType)Enum.Parse(typeof(LocalPSDNodeType), pair.Value.control.type);
                marker.description = pair.Value.control.note ?? "";
            }
        }

        public static void Capture(string prefabPath, bool force = false)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var prior = Read(prefabPath);
            if (!force && prior?.editing != true && asset.GetComponentsInChildren<LocalPSDNodeDescription>(true).Length == 0) return;
            var archive = new LocalPSDDescriptionArchive { prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath), editing = true };
            var map = LocalPSDRoundTrip.Load(prefabPath);
            foreach (var record in map.nodes) record.control = null;
            foreach (var marker in asset.GetComponentsInChildren<LocalPSDNodeDescription>(true))
            {
                string objectId = LocalPSDRoundTrip.ObjectId(marker.gameObject);
                var record = map.nodes.FirstOrDefault(r => r.objectId == objectId);
                if (record == null)
                {
                    record = new LocalPSDSourceRecord { objectId = objectId, stableId = Guid.NewGuid().ToString("N"),
                        prefabGuid = archive.prefabGuid, fileId = GlobalObjectId.GetGlobalObjectIdSlow(marker.gameObject).targetObjectId,
                        initialName = marker.name };
                    map.nodes.Add(record);
                }
                record.control = new LocalPSDMarkedControl { type = marker.type.ToString(), name = marker.name, note = marker.description ?? "" };
                archive.nodes.Add(new LocalPSDDescriptionEntry { objectId = objectId, path = NodePath(asset.transform, marker.transform),
                    type = record.control.type, description = record.control.note });
            }
            Write(prefabPath, archive);
            LocalPSDRoundTrip.Save(prefabPath, map);
        }

        public static string Restore(string prefabPath)
        {
            var current = PrefabStageUtility.GetCurrentPrefabStage();
            if (current != null && current.assetPath != prefabPath && current.scene.isDirty)
                throw new InvalidOperationException("请先保存当前打开的其他预设体。");
            var stage = current != null && current.assetPath == prefabPath ? current : PrefabStageUtility.OpenPrefab(prefabPath);
            SaveStage(stage);
            var archive = Read(prefabPath);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            // Older results already contain descriptions in the source map.
            var stored = archive != null ? archive.nodes : LocalPSDRoundTrip.Load(prefabPath).nodes
                .Where(r => r.control != null).Select(r => new LocalPSDDescriptionEntry { objectId = r.objectId,
                    type = r.control.type, description = r.control.note }).ToList();
            var entries = stored.GroupBy(e => e.objectId).ToDictionary(g => g.Key, g => g.Last());
            int restored = 0, kept = 0;
            foreach (var pair in Pairs(asset, stage.prefabContentsRoot))
            {
                if (pair.Node.GetComponent<LocalPSDNodeDescription>() != null) { kept++; continue; }
                if (!entries.TryGetValue(LocalPSDRoundTrip.ObjectId(pair.Asset), out var entry)) continue;
                if (!Enum.TryParse(entry.type, out LocalPSDNodeType type)) throw new InvalidDataException("未知的存档节点类型: " + entry.type);
                var marker = Undo.AddComponent<LocalPSDNodeDescription>(pair.Node);
                marker.type = type; marker.description = entry.description ?? "";
                EditorUtility.SetDirty(marker);
                if (PrefabUtility.IsPartOfPrefabInstance(marker)) PrefabUtility.RecordPrefabInstancePropertyModifications(marker);
                restored++;
            }
            EditorSceneManager.MarkSceneDirty(stage.scene);
            SaveStage(stage);
            // Retain unmatched entries until the user explicitly generates Bind to commit deletions.
            archive ??= new LocalPSDDescriptionArchive { prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath), nodes = stored };
            archive.editing = true;
            Write(prefabPath, archive);
            return $"已恢复 {restored} 个节点描述，保留 {kept} 个现有标记。存档：{ArchivePath(prefabPath)}";
        }

        public static void PrepareForBind(string prefabPath)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.assetPath == prefabPath) SaveStage(stage);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var prior = Read(prefabPath);
            var markers = asset.GetComponentsInChildren<LocalPSDNodeDescription>(true);
            if (markers.Length == 0 && prior?.editing != true) return;
            stage = PrefabStageUtility.OpenPrefab(prefabPath);
            var root = stage.prefabContentsRoot;
            var live = root.GetComponentsInChildren<LocalPSDNodeDescription>(true);
            var names = new HashSet<string>(root.GetComponentsInChildren<Transform>(true).Select(t => t.name), StringComparer.Ordinal);
            // Resolve all types and names before mutating the hierarchy.
            foreach (var marker in live)
                if (!LocalPSDControlRules.IsDescriptionOnly(marker.type.ToString())) LocalPSDControlRules.ComponentType(marker.type.ToString());
            foreach (var marker in live)
            {
                if (LocalPSDControlRules.IsDescriptionOnly(marker.type.ToString())) continue;
                LocalPSDControlRules.EnsureComponent(marker.gameObject, marker.type.ToString());
                string name = marker.name;
                if (!LocalPSDControlRules.IsBindingName(name))
                {
                    string stem = LocalPSDControlRules.SuggestName((LocalPSDInteractionPreview.ComponentType)marker.type, name, marker.description);
                    name = stem;
                    for (int suffix = 2; !names.Add(name); suffix++) name = stem + suffix;
                    marker.gameObject.name = name;
                }
                if (PrefabUtility.IsPartOfPrefabInstance(marker.gameObject)) PrefabUtility.RecordPrefabInstancePropertyModifications(marker.gameObject);
            }
            EditorSceneManager.MarkSceneDirty(stage.scene);
            SaveStage(stage);
            Capture(prefabPath, force: true);
        }

        public static void Strip(GameObject root)
        {
            foreach (var marker in root.GetComponentsInChildren<LocalPSDNodeDescription>(true))
                UnityEngine.Object.DestroyImmediate(marker);
        }

        public static void CompleteBind(string prefabPath)
        {
            var archive = Read(prefabPath);
            if (archive == null) return;
            archive.editing = false;
            Write(prefabPath, archive);
        }
    }
}

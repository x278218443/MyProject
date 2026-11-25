using System;
using System.Runtime.Serialization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace Starlight.Game.Editor.LocalUIConverter
{
    [Serializable, DataContract] public sealed class LocalPSDOriginalLayer
    {
        [DataMember] public string name, type, bindingName, prefabKey;
        [DataMember] public int psdLayerId = -1, sourceLayerIndex;
        [DataMember] public float left, top, width, height;
        [DataMember] public LocalPSDText textInfo;
        [OnDeserializing] private void InitializeDefaults(StreamingContext context) { psdLayerId = -1; }
        public static LocalPSDOriginalLayer From(LocalPSDLayer layer) => layer == null ? null : new LocalPSDOriginalLayer
        {
            name = layer.name, type = layer.type, bindingName = layer.bindingName, prefabKey = layer.prefabKey,
            psdLayerId = layer.psdLayerId, sourceLayerIndex = layer.sourceLayerIndex,
            left = layer.left, top = layer.top, width = layer.width, height = layer.height, textInfo = layer.textInfo
        };
    }
    [Serializable, DataContract] public sealed class LocalPSDSourceRecord
    {
        [DataMember] public string objectId, stableId, replacementKey;
        [DataMember] public string prefabGuid, initialName;
        [DataMember] public ulong fileId;
        [DataMember] public LocalPSDOriginalLayer original;
        [DataMember(EmitDefaultValue = false)] public LocalPSDMarkedControl control;
    }
    [Serializable, DataContract] public sealed class LocalPSDSourceMap
    {
        [DataMember] public string sourcePsd;
        [DataMember] public List<LocalPSDSourceRecord> nodes = new List<LocalPSDSourceRecord>();
    }

    public static class LocalPSDRoundTrip
    {
        private static string Root => Directory.GetParent(Application.dataPath).FullName;
        internal static string MapPath(string guid) => Path.Combine(Root, "__Temp", "LocalPSDUI", guid + ".json");
        private static string LegacyMapPath(string guid) => Path.Combine(Root, "ProjectSettings/LocalPSDUI", guid + ".json");
        public static string ObjectId(GameObject node) => GlobalObjectId.GetGlobalObjectIdSlow(node).ToString();
        public static LocalPSDSourceMap Load(string prefabPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(prefabPath);
            string path = MapPath(guid);
            if (File.Exists(path)) return LocalPSDJson.Deserialize<LocalPSDSourceMap>(File.ReadAllText(path));
            string legacy = LegacyMapPath(guid);
            if (!File.Exists(legacy)) return new LocalPSDSourceMap();
            var map = LocalPSDJson.Deserialize<LocalPSDSourceMap>(File.ReadAllText(legacy));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.Move(legacy, path);
            return map;
        }
        public static void Save(string prefabPath, LocalPSDSourceMap map)
        {
            string guid = AssetDatabase.AssetPathToGUID(prefabPath);
            string path = MapPath(guid);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, LocalPSDJson.Serialize(map));
            string legacy = LegacyMapPath(guid);
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        public static string Archive(string source)
        {
            if (string.IsNullOrEmpty(source) || !File.Exists(source)) return source;
            string hash;
            using (var input = File.OpenRead(source)) using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
            string path = Path.Combine(Root, "__Temp/PSD/.sources", hash + ".psd");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!File.Exists(path)) File.Copy(source, path);
            return path;
        }

        public static void Capture(GameObject root, string prefabPath, string sourcePsd,
            IDictionary<GameObject, LocalPSDSourceRecord> records)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var map = new LocalPSDSourceMap { sourcePsd = Archive(sourcePsd) };
            foreach (var pair in records)
            {
                if (pair.Key == null) continue;
                var indices = new List<int>();
                Transform current = pair.Key.transform;
                while (current != root.transform && current != null) { indices.Add(current.GetSiblingIndex()); current = current.parent; }
                if (current == null) continue;
                Transform target = asset.transform;
                for (int i = indices.Count - 1; i >= 0; i--)
                {
                    if (indices[i] >= target.childCount) { target = null; break; }
                    target = target.GetChild(indices[i]);
                }
                if (target == null) continue;
                var record = pair.Value;
                record.objectId = ObjectId(target.gameObject);
                record.prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath);
                record.fileId = GlobalObjectId.GetGlobalObjectIdSlow(target.gameObject).targetObjectId;
                if (string.IsNullOrEmpty(record.initialName)) record.initialName = record.original?.name ?? target.name;
                map.nodes.Add(record);
            }
            Save(prefabPath, map);
        }

        public static void Transfer(IDictionary<GameObject, LocalPSDSourceRecord> records, GameObject source, GameObject target, string rule)
        {
            if (!records.TryGetValue(source, out var record)) return;
            records[target] = new LocalPSDSourceRecord { stableId = record.stableId, original = record.original,
                initialName = record.initialName, control = record.control,
                replacementKey = string.IsNullOrEmpty(rule) ? record.replacementKey : rule };
            if (!string.IsNullOrEmpty(rule)) return;
            // P_ extraction duplicates the same hierarchy. Transfer descendants too;
            // a G_P_ placeholder deliberately remains an atomic design node.
            for (int i = 0; i < Math.Min(source.transform.childCount, target.transform.childCount); i++)
                Transfer(records, source.transform.GetChild(i).gameObject, target.transform.GetChild(i).gameObject, "");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Starlight.Game.Editor.LocalUIConverter
{
    /// <summary>Independent, previewable texture reuse and GUID-preserving organization.</summary>
    public static class LocalPSDAssetOrganizer
    {
        public enum Operation { ReuseCommon, ReuseArt, Move, Keep }
        public sealed class Item
        {
            public string source, destination;
            public Operation operation;
        }
        public sealed class Plan
        {
            public string target, common, art, signature;
            public string[] prefabs;
            public bool explicitPrefabs;
            public readonly List<Item> items = new List<Item>();
            public readonly List<string> conflicts = new List<string>();
            public int Reused => items.Count(i => i.operation == Operation.ReuseCommon || i.operation == Operation.ReuseArt);
            public int Moved => items.Count(i => i.operation == Operation.Move);
            public int Kept => items.Count(i => i.operation == Operation.Keep);
            public string Summary => $"复用 {Reused} 张 · 移动 {Moved} 张 · 保留 {Kept} 张 · 冲突 {conflicts.Count} 项";
        }
        public sealed class Result
        {
            public int changedPrefabs, replacedReferences, moved, deleted;
            public readonly List<string> messages = new List<string>();
            public string Summary => $"整理完成：修改 {changedPrefabs} 个预设体，替换 {replacedReferences} 处引用，移动 {moved} 张，清理 {deleted} 张重复图。";
        }
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".psd", ".tga", ".tif", ".tiff", ".bmp" };
        private static bool running;

        private static int ImageSizeLimit()
        {
            // The tool can be checked out independently, so read the project's rule
            // when available without adding an assembly dependency on its validator.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("Starlight.Editor.ResourceValidator.TextureRuleSuite", false);
                var field = type?.GetField("MaxImageSize", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (field?.FieldType == typeof(int) && field.GetValue(null) is int limit && limit > 0) return limit;
            }
            return 512;
        }

        public static string AssetFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("目录不能为空。");
            string root = Directory.GetParent(Application.dataPath).FullName;
            string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path)).Replace('\\', '/').TrimEnd('/');
            string assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
            if (full.Equals(assets, StringComparison.OrdinalIgnoreCase)) return "Assets";
            if (!full.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("目录必须在当前项目 Assets 内: " + path);
            return "Assets/" + full.Substring(assets.Length + 1);
        }

        private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

        private static Dictionary<string, List<string>> Index(string root)
        {
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return index;
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!ImageExtensions.Contains(Path.GetExtension(file))) continue;
                string path = AssetFolder(file);
                string name = Path.GetFileName(path);
                if (!index.TryGetValue(name, out var list)) index[name] = list = new List<string>();
                list.Add(path);
            }
            return index;
        }

        public static Plan BuildPlan(string target, string common, string art, string[] prefabPaths = null)
        {
            var plan = new Plan { target = AssetFolder(target), common = string.IsNullOrWhiteSpace(common) ? "" : AssetFolder(common), art = AssetFolder(art) };
            foreach (string destination in new[] { plan.common, plan.art })
                if (!string.IsNullOrEmpty(destination) && (Within(destination, plan.target) || Within(plan.target, destination)))
                    throw new ArgumentException("整理目标和图片库目录不能互相包含，以免把导出图片当成复用资源。");
            if (!string.IsNullOrEmpty(plan.common) && !AssetDatabase.IsValidFolder(plan.common))
                throw new DirectoryNotFoundException("通用图片目录不存在: " + plan.common);
            string textures = plan.target + "/Textures";
            string prefabs = plan.target + "/Prefabs";
            if (!AssetDatabase.IsValidFolder(textures) || (prefabPaths == null && !AssetDatabase.IsValidFolder(prefabs)))
                throw new DirectoryNotFoundException("请选择包含 Textures 和 Prefabs 的导出目录。");
            plan.explicitPrefabs = prefabPaths != null;
            plan.prefabs = (prefabPaths ?? AssetDatabase.FindAssets("t:Prefab", new[] { prefabs }).Select(AssetDatabase.GUIDToAssetPath).ToArray())
                .Select(AssetFolder).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (plan.prefabs.Length == 0) throw new InvalidOperationException("导出目录中没有预设体。");
            foreach (string prefab in plan.prefabs)
                if (!prefab.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || AssetDatabase.LoadAssetAtPath<GameObject>(prefab) == null)
                    throw new FileNotFoundException("找不到待整理引用的预设体: " + prefab);
            var commonIndex = Index(plan.common);
            var artIndex = Index(plan.art);
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int imageSizeLimit = ImageSizeLimit();
            foreach (string file in Directory.GetFiles(textures, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!ImageExtensions.Contains(Path.GetExtension(file))) continue;
                string source = AssetFolder(file);
                string name = Path.GetFileName(source);
                var item = new Item { source = source, operation = Operation.Keep };
                plan.items.Add(item);
                List<string> matches = null;
                if (commonIndex.TryGetValue(name, out matches)) item.operation = Operation.ReuseCommon;
                else if (artIndex.TryGetValue(name, out matches)) item.operation = Operation.ReuseArt;
                if (matches != null)
                {
                    if (matches.Count != 1)
                    {
                        plan.conflicts.Add("同名图片不唯一: " + name + "\n" + string.Join("\n", matches));
                        continue;
                    }
                    item.destination = matches[0];
                    // Validate each source subasset independently, not by fileID across unrelated files.
                    var originals = AssetDatabase.LoadAllAssetsAtPath(source).Where(o => o is Sprite || o is Texture2D).ToArray();
                    if (originals.Length == 0) plan.conflicts.Add("无法加载图片资源: " + source);
                    foreach (var original in originals)
                        if (Replacement(original, item.destination) == null) plan.conflicts.Add("目标图片缺少唯一的 " + original.GetType().Name + " 子资源: " + source + " → " + item.destination);
                    continue;
                }
                string prefix = name.Split('_')[0];
                string category = prefix.Equals("img", StringComparison.OrdinalIgnoreCase) ? "IMG"
                    : prefix.Equals("btn", StringComparison.OrdinalIgnoreCase) ? "BTN" : null;
                if (category == null) continue;
                if (category == "IMG")
                {
                    var importer = AssetImporter.GetAtPath(source) as TextureImporter;
                    if (importer == null)
                    {
                        plan.conflicts.Add("无法读取新增图片的原始尺寸: " + source);
                        continue;
                    }
                    // Imported textures may have been downscaled by maxTextureSize.
                    importer.GetSourceTextureWidthAndHeight(out int width, out int height);
                    if (width <= 0 || height <= 0)
                    {
                        plan.conflicts.Add("新增图片的原始尺寸无效: " + source);
                        continue;
                    }
                    if (width > imageSizeLimit || height > imageSizeLimit / 2f) category = "BG";
                }
                item.operation = Operation.Move;
                item.destination = plan.art + "/" + category + "/" + name;
                if (!destinations.Add(item.destination)) plan.conflicts.Add("多个源文件会移动到同一位置: " + item.destination);
                if (File.Exists(item.destination)) plan.conflicts.Add("移动位置已存在文件: " + item.destination);
            }
            // Include hashes so an execution cannot silently use a stale preview.
            var signature = new StringBuilder(plan.target + "|" + plan.common + "|" + plan.art);
            foreach (string prefab in plan.prefabs) signature.Append('|').Append(prefab).Append(':').Append(AssetDatabase.GetAssetDependencyHash(prefab));
            foreach (var item in plan.items)
                signature.Append('|').Append(item.source).Append(':').Append(AssetDatabase.GetAssetDependencyHash(item.source))
                    .Append('>').Append(item.operation).Append(':').Append(item.destination)
                    .Append(':').Append(string.IsNullOrEmpty(item.destination) ? "" : AssetDatabase.GetAssetDependencyHash(item.destination).ToString());
            foreach (string conflict in plan.conflicts) signature.Append('|').Append(conflict);
            using (var sha = SHA256.Create()) plan.signature = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(signature.ToString())));
            return plan;
        }

        private static Object Replacement(Object original, string target)
        {
            var candidates = AssetDatabase.LoadAllAssetsAtPath(target)
                .Where(o => o != null && (original is Sprite ? o is Sprite : original is Texture2D && o is Texture2D)).ToArray();
            var sameName = candidates.Where(o => o.name == original.name).ToArray();
            if (sameName.Length == 1) return sameName[0];
            return candidates.Length == 1 ? candidates[0] : null;
        }

        public static async Task<Result> ApplyAsync(Plan plan, bool deleteUnused, CancellationToken token = default)
        {
            if (running) throw new InvalidOperationException("已有资源整理任务正在运行。");
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            running = true;
            try
            {
                token.ThrowIfCancellationRequested();
                var current = BuildPlan(plan.target, plan.common, plan.art, plan.explicitPrefabs ? plan.prefabs : null);
                if (current.conflicts.Count > 0) throw new InvalidOperationException("存在冲突，请处理后重新检查整理方案。");
                if (current.signature != plan.signature) throw new InvalidOperationException("资源已发生变化，请重新检查整理方案。");
                token.ThrowIfCancellationRequested();
                LocalPSDPrefabGenerator.RequireAssetFolder(plan.art);
                var result = new Result();
                var replacements = plan.items.Where(i => i.operation == Operation.ReuseCommon || i.operation == Operation.ReuseArt)
                    .ToDictionary(i => i.source, i => i.destination, StringComparer.OrdinalIgnoreCase);
                // Save child prefab assets before their generated parents.
                var ordered = new List<string>();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var prefabSet = new HashSet<string>(plan.prefabs, StringComparer.OrdinalIgnoreCase);
                void Visit(string path)
                {
                    if (!visited.Add(path)) return;
                    foreach (string dependency in AssetDatabase.GetDependencies(path, false))
                        if (dependency != path && prefabSet.Contains(dependency)) Visit(dependency);
                    ordered.Add(path);
                }
                foreach (string prefab in plan.prefabs) Visit(prefab);
                foreach (string path in ordered)
                {
                    token.ThrowIfCancellationRequested();
                    GameObject root = PrefabUtility.LoadPrefabContents(path);
                    try
                    {
                        int changed = 0;
                        foreach (var component in root.GetComponentsInChildren<Component>(true))
                        {
                            if (component == null) continue;
                            using (var serialized = new SerializedObject(component))
                            {
                                serialized.Update();
                                var property = serialized.GetIterator();
                                bool modified = false;
                                while (property.Next(true))
                                {
                                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                                    Object original = property.objectReferenceValue;
                                    if (!(original is Sprite) && !(original is Texture2D)) continue;
                                    if (!replacements.TryGetValue(AssetDatabase.GetAssetPath(original), out string destination)) continue;
                                    Object replacement = Replacement(original, destination);
                                    if (replacement == null) throw new InvalidOperationException("无法匹配图片引用: " + path + " / " + property.propertyPath);
                                    property.objectReferenceValue = replacement;
                                    modified = true;
                                    changed++;
                                }
                                if (modified)
                                {
                                    serialized.ApplyModifiedPropertiesWithoutUndo();
                                    if (PrefabUtility.IsPartOfPrefabInstance(component)) PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                                }
                            }
                        }
                        if (changed > 0)
                        {
                            if (PrefabUtility.SaveAsPrefabAsset(root, path) == null) throw new IOException("保存预设体失败: " + path);
                            result.changedPrefabs++;
                            result.replacedReferences += changed;
                        }
                    }
                    finally { PrefabUtility.UnloadPrefabContents(root); }
                    await Task.Yield();
                }
                foreach (var item in plan.items.Where(i => i.operation == Operation.Move))
                {
                    token.ThrowIfCancellationRequested();
                    LocalPSDPrefabGenerator.RequireAssetFolder(Path.GetDirectoryName(item.destination).Replace('\\', '/'));
                    string error = AssetDatabase.MoveAsset(item.source, item.destination);
                    if (!string.IsNullOrEmpty(error)) throw new IOException("移动图片失败: " + item.source + "\n" + error);
                    result.moved++;
                    await Task.Yield();
                }
                AssetDatabase.SaveAssets();
                UpdateLayout(plan, result);
                if (deleteUnused && replacements.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    // Exclude candidates themselves from the roots; otherwise every
                    // candidate trivially appears in GetDependencies as its own root.
                    var roots = AssetDatabase.GetAllAssetPaths().Where(p => !replacements.ContainsKey(p)
                        && (p.StartsWith("Assets/", StringComparison.Ordinal) || p.StartsWith("Packages/", StringComparison.Ordinal))
                        && !AssetDatabase.IsValidFolder(p)).ToArray();
                    var referenced = new HashSet<string>(AssetDatabase.GetDependencies(roots, true), StringComparer.OrdinalIgnoreCase);
                    // Unsaved scene objects do not appear in the asset dependency
                    // database. Include their live references before deleting files.
                    foreach (var component in Resources.FindObjectsOfTypeAll<Component>())
                    {
                        if (component == null || EditorUtility.IsPersistent(component) || !component.gameObject.scene.IsValid()) continue;
                        using (var serialized = new SerializedObject(component))
                        {
                            var property = serialized.GetIterator();
                            while (property.Next(true))
                            {
                                if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                                var reference = property.objectReferenceValue;
                                if (!(reference is Sprite) && !(reference is Texture2D)) continue;
                                string path = AssetDatabase.GetAssetPath(reference);
                                if (replacements.ContainsKey(path)) referenced.Add(path);
                            }
                        }
                    }
                    foreach (string source in replacements.Keys)
                    {
                        token.ThrowIfCancellationRequested();
                        if (referenced.Contains(source))
                        {
                            result.messages.Add("其他资源仍有引用，保留原图: " + source);
                            continue;
                        }
                        if (!AssetDatabase.DeleteAsset(source)) throw new IOException("清理未引用重复图失败: " + source);
                        result.deleted++;
                    }
                }
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                result.messages.AddRange(plan.items.Where(i => i.operation == Operation.Keep).Select(i => "无匹配且无 IMG_/BTN_ 前缀，保留: " + i.source));
                return result;
            }
            finally { running = false; }
        }

        private static void UpdateLayout(Plan plan, Result result)
        {
            string path = plan.target + "/layout.json";
            if (!File.Exists(path)) return;
            LocalPSDDocument document;
            try { document = LocalPSDDocument.Parse(File.ReadAllText(path)); }
            catch (Exception)
            {
                result.messages.Add("布局文件不是本工具格式，未修改: " + path);
                return;
            }
            var changed = plan.items.Where(i => i.operation != Operation.Keep && !string.IsNullOrEmpty(i.destination))
                .ToDictionary(i => i.source, i => i.destination, StringComparer.OrdinalIgnoreCase);
            foreach (var layer in document.artboard.Descendants())
            {
                if (!Uri.TryCreate(layer.images?.png_xxxhd, UriKind.Absolute, out var uri) || !uri.IsFile) continue;
                string source;
                try { source = AssetFolder(uri.LocalPath); }
                catch (ArgumentException) { continue; }
                if (changed.TryGetValue(source, out string target)) layer.images.png_xxxhd = new Uri(Path.GetFullPath(target)).AbsoluteUri;
            }
            File.WriteAllText(path, LocalPSDJson.Serialize(document));
            AssetDatabase.ImportAsset(path);
        }
    }
}

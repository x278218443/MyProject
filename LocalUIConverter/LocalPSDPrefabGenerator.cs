using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Starlight.Game.Editor.LocalUIConverter
{
    public static class LocalPSDPrefabGenerator
    {
        private static bool generating;
        public static string SafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("名称不能为空。");
            foreach (char character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
            name = name.Trim().Replace(" ", "_");
            if (name == "." || name == "..") throw new ArgumentException("名称不能是 . 或 ..。");
            return name;
        }

        public static string RequireAssetFolder(string folder)
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            string fullPath = Path.GetFullPath(Path.Combine(root, folder));
            string assets = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(assets, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("目录必须位于 Assets 内: " + folder);
            Directory.CreateDirectory(fullPath);
            string assetPath = "Assets/" + fullPath.Substring(assets.Length).Replace('\\', '/').TrimEnd('/');
            AssetDatabase.Refresh();
            return assetPath;
        }

        public static async Task<string> GenerateAsync(string json, string outputName, LocalUIConverterSettings settings,
            CancellationToken token = default, IProgress<LocalPSDGenerationProgress> progress = null)
        {
            if (generating) throw new InvalidOperationException("已有本地 PSD 生成任务正在运行。");
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var document = LocalPSDDocument.Parse(json);
            string name = SafeName(outputName);
            generating = true;
            Scene previewScene = default;
            GameObject canvas = null;
            try
            {
                token.ThrowIfCancellationRequested();
                string output = RequireAssetFolder(settings.outputRoot + "/" + name);
                string textures = RequireAssetFolder(output + "/Textures");
                string prefabs = RequireAssetFolder(output + "/Prefabs");
                string prefabPath = prefabs + "/" + name + ".prefab";
                // Build in a disposable preview scene. User scenes stay open and untouched.
                previewScene = EditorSceneManager.NewPreviewScene();
                canvas = new GameObject("Local PSD Canvas", typeof(RectTransform));
                SceneManager.MoveGameObjectToScene(canvas, previewScene);
                canvas.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = canvas.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(document.artboard.width, document.artboard.height);
                var root = new GameObject("Root", typeof(RectTransform));
                root.transform.SetParent(canvas.transform, false);
                ((RectTransform)root.transform).sizeDelta = scaler.referenceResolution;
                var context = new BuildContext(document, settings, root, textures, progress, token);
                foreach (var layer in document.artboard.layers) await context.Create(layer, root.transform);
                token.ThrowIfCancellationRequested();
                LocalPSDPrefabRules.ShareImages(context.nodes);
                LocalPSDPrefabRules.ReplaceCommon(context.nodes, settings, context.layers, context.Replaced);
                LocalPSDControlRules.Apply(root, document, context.records, rename: false);
                LocalPSDPrefabRules.ExtractPrefabs(context.nodes, prefabs, context.Replaced, context.layers);
                LocalPSDControlRules.Apply(root, document, context.records, rename: true);
                LocalPSDNodeDescriptions.Initialize(prefabPath, context.records);
                token.ThrowIfCancellationRequested();
                // Finish layout in PSD coordinates first, then make the delivered UI fill its parent.
                var rootRect = (RectTransform)root.transform;
                rootRect.anchorMin = Vector2.zero;
                rootRect.anchorMax = Vector2.one;
                rootRect.offsetMin = Vector2.zero;
                rootRect.offsetMax = Vector2.zero;
                if (PrefabUtility.SaveAsPrefabAsset(root, prefabPath) == null) throw new IOException("无法保存主预设体: " + prefabPath);
                LocalPSDRoundTrip.Capture(root, prefabPath, document.source, context.records);
                LocalPSDNodeDescriptions.Capture(prefabPath, force: true);
                // Keep notes in local archives and temporary markers, not the layout asset.
                foreach (var layer in document.artboard.Descendants())
                    if (layer.control != null)
                        layer.control = new LocalPSDMarkedControl { type = layer.control.type, name = layer.control.name };
                File.WriteAllText(output + "/layout.json", LocalPSDJson.Serialize(document));
                AssetDatabase.Refresh();
                // Delete only successfully replaced G_ textures which are no longer
                // referenced anywhere in this generated prefab's dependency graph.
                var dependencies = new HashSet<string>(AssetDatabase.GetDependencies(prefabPath, true), StringComparer.OrdinalIgnoreCase);
                foreach (string path in context.gTextures.Distinct())
                    if (!dependencies.Contains(path)) AssetDatabase.DeleteAsset(path);
                AssetDatabase.SaveAssets();
                return prefabPath;
            }
            finally
            {
                if (canvas != null) UnityEngine.Object.DestroyImmediate(canvas);
                if (previewScene.IsValid()) EditorSceneManager.ClosePreviewScene(previewScene);
                generating = false;
            }
        }

        private sealed class BuildContext
        {
            private readonly LocalPSDDocument document;
            private readonly LocalUIConverterSettings settings;
            private readonly GameObject root;
            private readonly string textures;
            private readonly IProgress<LocalPSDGenerationProgress> progress;
            private readonly CancellationToken token;
            private readonly LocalPSDFontResolver fonts;
            private readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();
            private readonly Dictionary<string, int> imageNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly int total;
            private int current;
            public readonly List<GameObject> nodes = new List<GameObject>();
            public readonly List<string> gTextures = new List<string>();
            public readonly Dictionary<GameObject, LocalPSDLayer> layers = new Dictionary<GameObject, LocalPSDLayer>();
            public readonly Dictionary<GameObject, LocalPSDSourceRecord> records = new Dictionary<GameObject, LocalPSDSourceRecord>();
            public void Replaced(GameObject source, GameObject target, string rule)
            {
                LocalPSDRoundTrip.Transfer(records, source, target, rule);
                if (layers.TryGetValue(source, out var layer)) layers[target] = layer;
            }

            public BuildContext(LocalPSDDocument document, LocalUIConverterSettings settings, GameObject root, string textures,
                IProgress<LocalPSDGenerationProgress> progress, CancellationToken token)
            {
                this.document = document; this.settings = settings; this.root = root; this.textures = textures;
                this.progress = progress; this.token = token;
                fonts = new LocalPSDFontResolver(settings);
                total = document.artboard.Descendants().Count();
            }

            public async Task Create(LocalPSDLayer layer, Transform parent)
            {
                token.ThrowIfCancellationRequested();
                if (!layer.isVisible && string.IsNullOrEmpty(layer.roundTripId)) return;
                bool children = layer.layers != null && layer.layers.Length > 0;
                bool image = !string.IsNullOrEmpty(layer.images?.png_xxxhd);
                bool text = layer.type == "textLayer" && layer.textInfo != null;
                if (!children && !image && !text && string.IsNullOrEmpty(layer.roundTripId)) return;
                if (layer.name != null && layer.name.IndexOf("Empty", StringComparison.OrdinalIgnoreCase) >= 0 && !children
                    && string.IsNullOrEmpty(layer.roundTripId)) return;
                var node = new GameObject(layer.name ?? "Layer", typeof(RectTransform));
                node.transform.SetParent(root.transform, false);
                int uiLayer = LayerMask.NameToLayer(settings.uiLayer);
                if (uiLayer >= 0) node.layer = uiLayer;
                var rect = (RectTransform)node.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(layer.width, layer.height);
                rect.anchoredPosition = new Vector2(layer.left + layer.width / 2 - document.artboard.width / 2,
                    document.artboard.height / 2 - layer.top - layer.height / 2);
                nodes.Add(node);
                layers[node] = layer;
                records[node] = new LocalPSDSourceRecord { stableId = string.IsNullOrEmpty(layer.roundTripId) ? Guid.NewGuid().ToString("N") : layer.roundTripId,
                    replacementKey = layer.replacementKey, initialName = layer.name, original = LocalPSDOriginalLayer.From(layer), control = layer.control };

                Graphic graphic = null;
                foreach (var rule in settings.componentRules)
                {
                    if (rule == null || string.IsNullOrEmpty(rule.prefix) || !(string.IsNullOrEmpty(layer.bindingName) ? node.name : layer.bindingName).StartsWith(rule.prefix, StringComparison.Ordinal) || rule.componentScript == null) continue;
                    Type type = rule.componentScript.GetClass();
                    if (text && type == typeof(TextMeshProUGUI)) type = LocalPSDControlRules.ProjectTextType;
                    if (type == null || type.IsAbstract || !typeof(Graphic).IsAssignableFrom(type))
                    {
                        Debug.LogWarning("[Local PSD UI] 组件映射必须是有效 Graphic 类型: " + rule.prefix);
                        continue;
                    }
                    graphic = node.AddComponent(type) as Graphic;
                    break;
                }
                if (graphic == null && image) graphic = node.AddComponent<Image>();
                if (graphic == null && text) graphic = (Graphic)node.AddComponent(LocalPSDControlRules.ProjectTextType);
                if (graphic is Image uiImage && image)
                {
                    uiImage.sprite = ImportSprite(layer);
                    if (layer.control == null && children && layer.layers.Any(l => l.type == "textLayer")) node.AddComponent<Button>();
                }
                else if (graphic is TMP_Text tmp)
                {
                    ApplyText(layer, tmp);
                    if (text) LocalPSDTextPostProcess.Fit(tmp, settings);
                }
                else if (graphic is Text legacy)
                {
                    legacy.text = layer.textInfo?.text ?? "";
                    legacy.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    legacy.fontSize = Mathf.RoundToInt(layer.textInfo?.size ?? 14);
                    legacy.color = layer.textInfo?.color?.Value ?? Color.white;
                    legacy.alignment = layer.textInfo?.justification == "center" ? TextAnchor.MiddleCenter
                        : layer.textInfo?.justification == "right" ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
                    legacy.horizontalOverflow = HorizontalWrapMode.Overflow;
                    legacy.verticalOverflow = VerticalWrapMode.Overflow;
                    rect.sizeDelta = new Vector2(Mathf.Max(rect.sizeDelta.x, Mathf.Ceil(legacy.preferredWidth) + 1),
                        Mathf.Max(rect.sizeDelta.y, legacy.preferredHeight));
                }
                if (parent != root.transform) node.transform.SetParent(parent, true);
                progress?.Report(new LocalPSDGenerationProgress { current = ++current, total = total, layerName = layer.name });
                await Task.Yield();
                if (layer.layers != null) foreach (var child in layer.layers) await Create(child, node.transform);
                node.SetActive(layer.isVisible);
            }

            private void ApplyText(LocalPSDLayer layer, TMP_Text text)
            {
                var info = layer.textInfo;
                text.text = (info?.text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
                text.richText = true;
                text.enableAutoSizing = false;
                text.fontSize = info != null && info.size > 0 ? info.size : 14;
                text.color = info?.color?.Value ?? Color.white;
                text.enableVertexGradient = info?.gradient?.top != null && info.gradient.bottom != null;
                text.colorGradientPreset = null;
                if (text.enableVertexGradient)
                {
                    // Keep the PSD fill in text.color and the independent RGBA gradient here.
                    Color top = info.gradient.top.Value, bottom = info.gradient.bottom.Value;
                    text.colorGradient = new VertexGradient(top, top, bottom, bottom);
                }
                text.alignment = info?.justification == "right" ? TextAlignmentOptions.MidlineRight
                    : info?.justification == "center" ? TextAlignmentOptions.Midline : TextAlignmentOptions.MidlineLeft;
                text.enableWordWrapping = true;
                text.overflowMode = TextOverflowModes.Overflow;
                text.lineSpacing = info != null && info.leading > 0 ? info.leading - text.fontSize : 0;
                text.characterSpacing = info?.tracking ?? 0;
                text.font = fonts.Resolve(info?.fontPostScriptName ?? info?.fontName);
                if (text.font.material != null) text.fontSharedMaterial = text.font.material;
                fonts.ApplyOutline(text, layer.layerEffects?.frameFX);
            }

            private Sprite ImportSprite(LocalPSDLayer layer)
            {
                string uriText = layer.images.png_xxxhd;
                if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || !uri.IsFile)
                    throw new InvalidDataException("仅支持本地图层图片: " + uriText);
                byte[] bytes = File.ReadAllBytes(uri.LocalPath);
                string signature;
                using (var sha = SHA256.Create()) signature = Convert.ToBase64String(sha.ComputeHash(bytes));
                if (sprites.TryGetValue(signature, out var cached))
                {
                    layer.images.png_xxxhd = new Uri(Path.GetFullPath(AssetDatabase.GetAssetPath(cached))).AbsoluteUri;
                    return cached;
                }
                string name = Regex.Replace(SafeName(layer.name), @"\s+", "_");
                imageNames.TryGetValue(name, out int count);
                imageNames[name] = count + 1;
                string path = textures + "/" + name + (count == 0 ? "" : "_" + (count - 1)) + ".png";
                File.WriteAllBytes(path, bytes);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = 100;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = true;
                importer.SaveAndReimport();
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null) throw new IOException("无法导入图层图片: " + path);
                sprites.Add(signature, sprite);
                layer.images.png_xxxhd = new Uri(Path.GetFullPath(path)).AbsoluteUri;
                if (layer.name.StartsWith("G_", StringComparison.Ordinal)) gTextures.Add(path);
                return sprite;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace Starlight.Game.Editor.LocalUIConverter
{
    internal sealed class LocalPSDFontResolver
    {
        private readonly LocalUIConverterSettings settings;
        private readonly Dictionary<string, TMP_FontAsset> fonts = new Dictionary<string, TMP_FontAsset>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Font> sourceFonts = new Dictionary<string, Font>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Material> outlineMaterials = new List<Material>();
        private readonly HashSet<string> missing = new HashSet<string>();

        public LocalPSDFontResolver(LocalUIConverterSettings settings)
        {
            this.settings = settings;
            if (AssetDatabase.IsValidFolder(settings.fontRoot))
            {
                foreach (string guid in AssetDatabase.FindAssets("t:TMP_FontAsset", new[] { settings.fontRoot }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                    if (font == null) continue;
                    Index(font, path);
                }
                foreach (string guid in AssetDatabase.FindAssets("t:Font", new[] { settings.fontRoot }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!new[] { ".ttf", ".otf" }.Contains(Path.GetExtension(path).ToLowerInvariant())) continue;
                    var font = AssetDatabase.LoadAssetAtPath<Font>(path);
                    if (font == null) continue;
                    foreach (string name in (font.fontNames ?? Array.Empty<string>()).Concat(new[] { font.name, Path.GetFileNameWithoutExtension(path) }))
                        foreach (string key in Keys(name)) if (!sourceFonts.ContainsKey(key)) sourceFonts.Add(key, font);
                }
            }
            if (AssetDatabase.IsValidFolder(settings.generatedFontFolder))
                foreach (string guid in AssetDatabase.FindAssets("t:TMP_FontAsset", new[] { settings.generatedFontFolder }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                    if (font != null) Index(font, path);
                }
            if (AssetDatabase.IsValidFolder(settings.outlineMaterialFolder))
                foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { settings.outlineMaterialFolder }))
                {
                    var material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                    if (material != null && material.HasProperty("_OutlineColor")) outlineMaterials.Add(material);
                }
        }

        private static IEnumerable<string> Keys(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) yield break;
            yield return name.Trim();
            string normalized = Regex.Replace(name.Trim(), @"[\s_.\-]+", "");
            yield return normalized;
            yield return Regex.Replace(normalized, "(?i)roman", "");
            yield return Regex.Replace(normalized, "(?i)(sdf|tmp)$", "");
        }

        private void Index(TMP_FontAsset font, string path)
        {
            foreach (string name in new[] { font.name, font.faceInfo.familyName,
                font.sourceFontFile != null ? font.sourceFontFile.name : "", Path.GetFileNameWithoutExtension(path) })
                foreach (string key in Keys(name)) if (!fonts.ContainsKey(key)) fonts.Add(key, font);
        }

        public TMP_FontAsset Resolve(string name)
        {
            foreach (var rule in settings.fontRules)
                if (rule != null && rule.fontAsset != null && string.Equals(rule.psdFontName?.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase)) return rule.fontAsset;
            foreach (string key in Keys(name)) if (fonts.TryGetValue(key, out var font)) return font;
            foreach (string key in Keys(name))
            {
                if (!sourceFonts.TryGetValue(key, out var source)) continue;
                string folder = LocalPSDPrefabGenerator.RequireAssetFolder(settings.generatedFontFolder);
                string path = folder + "/" + LocalPSDPrefabGenerator.SafeName(source.name) + ".asset";
                var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (font == null)
                {
                    font = TMP_FontAsset.CreateFontAsset(source);
                    if (font == null) throw new InvalidOperationException("无法生成 TMP 字体: " + source.name);
                    font.name = source.name;
                    AssetDatabase.CreateAsset(font, path);
                    foreach (var atlas in font.atlasTextures) if (atlas != null) AssetDatabase.AddObjectToAsset(atlas, font);
                    if (font.material != null) AssetDatabase.AddObjectToAsset(font.material, font);
                    EditorUtility.SetDirty(font);
                    AssetDatabase.SaveAssets();
                }
                Index(font, path);
                return font;
            }
            if (!string.IsNullOrWhiteSpace(name) && missing.Add(name)) Debug.LogWarning("[Local PSD UI] 未匹配字体，使用默认字体: " + name);
            var fallback = settings.defaultFont != null ? settings.defaultFont : TMP_Settings.defaultFontAsset;
            if (fallback == null) throw new InvalidOperationException("请在本地工具配置中指定默认 TMP 字体，或导入 TMP Essentials。");
            return fallback;
        }

        public void ApplyOutline(TMP_Text text, LocalPSDStroke stroke)
        {
            if (stroke == null || !stroke.enabled || stroke.size <= 0 || stroke.color == null) return;
            Color32 color = stroke.color.Value;
            Material baseMaterial = text.font.material;
            if (baseMaterial == null || !baseMaterial.HasProperty("_OutlineWidth") || !baseMaterial.HasProperty("_GradientScale"))
            {
                Debug.LogWarning("[Local PSD UI] 字体材质不支持 SDF 描边: " + text.font.name);
                return;
            }
            float requestedWidth = CalculateOutlineThickness(text.font, baseMaterial, text.fontSize, stroke.size);
            float width = Mathf.Clamp01(requestedWidth);
            foreach (var material in outlineMaterials)
            {
                if (material.mainTexture != text.font.atlasTexture) continue;
                Color32 candidate = material.GetColor("_OutlineColor");
                if (candidate.r == color.r && candidate.g == color.g && candidate.b == color.b)
                {
                    // Preserve the existing hand-authored material matching rule. Generated
                    // materials also require the same width so different sizes never share by color alone.
                    bool autoGenerated = material.name.StartsWith("LocalPSD_", StringComparison.Ordinal)
                        || AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(material))?.userData == "LocalPSDOutline:v1";
                    if (autoGenerated
                        && (!material.HasProperty("_OutlineWidth") || Mathf.Abs(material.GetFloat("_OutlineWidth") - width) > 0.0001f)) continue;
                    text.fontSharedMaterial = material;
                    text.UpdateMeshPadding();
                    return;
                }
            }
            string folder = LocalPSDPrefabGenerator.RequireAssetFolder(settings.outlineMaterialFolder);
            string stem = Regex.Replace(baseMaterial.name, @"[<>:""/\\|?*\x00-\x1F]", "_").Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(stem)) stem = "Font Material";
            string hex = ColorUtility.ToHtmlStringRGB(color);
            string path = folder + "/" + stem + "-" + hex + ".mat";
            // Keep the color at the end even when a different width already occupies this name.
            for (int suffix = 2; !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)) || File.Exists(path); suffix++)
                path = folder + "/" + stem + "-" + suffix + "-" + hex + ".mat";
            // Never alter a font's shared default or a manually authored preset.
            var generated = new Material(baseMaterial) { name = Path.GetFileNameWithoutExtension(path) };
            generated.SetColor("_OutlineColor", color);
            generated.SetFloat("_OutlineWidth", width);
            generated.EnableKeyword("OUTLINE_ON");
            AssetDatabase.CreateAsset(generated, path);
            var importer = AssetImporter.GetAtPath(path);
            importer.userData = "LocalPSDOutline:v1";
            importer.SaveAndReimport();
            EditorUtility.SetDirty(generated);
            AssetDatabase.SaveAssets();
            outlineMaterials.Add(generated);
            text.fontSharedMaterial = generated;
            text.UpdateMeshPadding();
            if (requestedWidth > 1)
                Debug.LogWarning("[Local PSD UI] 描边粗细超出字体 SDF 图集范围，已限制为最大值；可增加字体图集 Padding 后重新生成: " + text.name);
        }

        // Same pixel convention as the project's TmpCustomEditor.CalcThickness,
        // implemented locally to keep this tool independent of other Editor assemblies.
        internal static float CalculateOutlineThickness(TMP_FontAsset font, Material material, float fontSize, float pixels)
        {
            if (font == null || fontSize <= 0 || pixels <= 0 || font.faceInfo.pointSize <= 0 || font.atlasPadding <= 0) return 0;
            float ratio = (float)font.atlasPadding / font.faceInfo.pointSize;
            bool isMobile = material != null && material.shader != null
                && material.shader.name.IndexOf("Mobile", StringComparison.OrdinalIgnoreCase) >= 0;
            return pixels * (isMobile ? 1f : 0.5f) / (fontSize * ratio);
        }
    }
}

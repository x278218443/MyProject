using System;
using System.Runtime.Serialization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Starlight.Game.Editor.LocalUIConverter
{
    [Serializable, DataContract] public sealed class LocalPSDReversePayload
    {
        [DataMember] public string sourcePsd, prefabPath;
        [DataMember] public int width, height;
        [DataMember] public List<LocalPSDReverseNode> nodes = new List<LocalPSDReverseNode>();
        [DataMember] public List<string> warnings = new List<string>();
    }
    [Serializable, DataContract] public sealed class LocalPSDReverseNode
    {
        [DataMember] public string stableId, name, image, replacementKey, bindingName, prefabKey;
        [DataMember] public bool visible = true;
        [DataMember] public bool nativeTextCompatible;
        [DataMember] public float left, top, width, height;
        [DataMember] public LocalPSDOriginalLayer original;
        [DataMember(EmitDefaultValue = false)] public LocalPSDMarkedControl control;
        [DataMember] public LocalPSDText textInfo;
        [DataMember] public List<LocalPSDReverseNode> children = new List<LocalPSDReverseNode>();
    }

    public static class LocalPSDReverseExporter
    {
        public static string Prepare(string prefabPath, string originalPsd, string layoutJson, LocalUIConverterSettings settings,
            string cache, CancellationToken token)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.assetPath == prefabPath && stage.scene.isDirty)
                throw new InvalidOperationException("请先保存当前预设体的修改，再导出 PSD。");
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var rootRect = asset != null ? asset.GetComponent<RectTransform>() : null;
            if (rootRect == null) throw new InvalidOperationException("导出对象必须是具有 RectTransform 的 UI 预设体。");
            int width = Mathf.CeilToInt(rootRect.rect.width), height = Mathf.CeilToInt(rootRect.rect.height);
            // A full-stretch prefab has no intrinsic size without a parent. Recover its
            // design resolution from the import layout when rendering it back to PSD.
            if ((width <= 0 || height <= 0) && !string.IsNullOrWhiteSpace(layoutJson))
            {
                var artboard = LocalPSDDocument.Parse(layoutJson).artboard;
                if (width <= 0) width = Mathf.CeilToInt(artboard.width);
                if (height <= 0) height = Mathf.CeilToInt(artboard.height);
            }
            if (width <= 0 || height <= 0 || width > 8192 || height > 8192) throw new InvalidOperationException("导出画板尺寸必须在 1～8192 像素内。");
            Directory.CreateDirectory(cache);
            var sourceMap = LocalPSDRoundTrip.Load(prefabPath);
            if (!File.Exists(sourceMap.sourcePsd)) sourceMap.sourcePsd = File.Exists(originalPsd) ? LocalPSDRoundTrip.Archive(originalPsd) : "";
            var payload = new LocalPSDReversePayload { width = width, height = height, prefabPath = prefabPath, sourcePsd = sourceMap.sourcePsd };
            var originals = new List<LocalPSDLayer>();
            if (!string.IsNullOrWhiteSpace(layoutJson))
            {
                try { originals.AddRange(LocalPSDDocument.Parse(layoutJson).artboard.Descendants()); }
                catch (Exception exception) { payload.warnings.Add("无法读取旧布局映射: " + exception.Message); }
            }
            var byId = sourceMap.nodes.GroupBy(n => n.objectId).ToDictionary(g => g.Key, g => g.First());
            var newMap = new LocalPSDSourceMap { sourcePsd = sourceMap.sourcePsd };
            Scene scene = EditorSceneManager.NewPreviewScene();
            GameObject canvasObject = null, cameraObject = null;
            RenderTexture target = null;
            Texture2D readback = null;
            try
            {
                canvasObject = new GameObject("PSD Export Canvas", typeof(RectTransform));
                SceneManager.MoveGameObjectToScene(canvasObject, scene);
                cameraObject = new GameObject("PSD Export Camera", typeof(Camera));
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                var camera = cameraObject.GetComponent<Camera>();
                camera.scene = scene;
                camera.orthographic = true;
                camera.orthographicSize = height / 2f;
                camera.aspect = (float)width / height;
                camera.transform.position = new Vector3(0, 0, -1000);
                camera.nearClipPlane = 0.1f; camera.farClipPlane = 2000;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.enabled = false;
                target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                target.Create(); camera.targetTexture = target;
                var canvas = canvasObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 1000;
                var clone = Object.Instantiate(asset, canvasObject.transform, false);
                var renderers = clone.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length > 0) payload.warnings.Add("包含非 UGUI Renderer，这些内容暂未导出为独立图层。");
                foreach (var renderer in renderers) renderer.enabled = false;
                if (clone.GetComponentsInChildren<Mask>(true).Length > 0 || clone.GetComponentsInChildren<RectMask2D>(true).Length > 0)
                    payload.warnings.Add("包含 UI 遮罩，逐层导出的遮罩外观需要在 Photoshop 中检查。");
                var rect = (RectTransform)clone.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(width, height);
                rect.localScale = Vector3.one; rect.localRotation = Quaternion.identity;
                rect.anchoredPosition = new Vector2((rect.pivot.x - 0.5f) * width, (rect.pivot.y - 0.5f) * height);
                Canvas.ForceUpdateCanvases();
                var visibility = new Dictionary<Transform, bool>();
                foreach (var t in clone.GetComponentsInChildren<Transform>(true)) { visibility[t] = t.gameObject.activeSelf; t.gameObject.SetActive(true); }
                var graphics = clone.GetComponentsInChildren<Graphic>(true);
                var graphicEnabled = graphics.ToDictionary(g => g, g => g.enabled);
                foreach (var g in graphics) g.enabled = false;
                readback = new Texture2D(width, height, TextureFormat.RGBA32, false);
                int index = 0;
                int visited = 0;
                int total = Math.Max(1, asset.GetComponentsInChildren<Transform>(true).Length);

                LocalPSDSourceRecord Resolve(GameObject source)
                {
                    string id = LocalPSDRoundTrip.ObjectId(source);
                    if (byId.TryGetValue(id, out var existing)) return existing;
                    bool legacy = sourceMap.nodes.Count == 0;
                    var matches = legacy ? originals.Where(l => l.name == source.name).ToArray() : Array.Empty<LocalPSDLayer>();
                    string ruleKey = "";
                    if (matches.Length != 1 && settings != null && PrefabUtility.IsAnyPrefabInstanceRoot(source))
                    {
                        string prefabSource = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(source);
                        var rules = settings.prefabRules.Where(r => r?.prefab != null && AssetDatabase.GetAssetPath(r.prefab) == prefabSource).ToArray();
                        matches = originals.Where(l => rules.Any(r => r.nodeName == l.name || r.nodeName == l.replacementKey)).ToArray();
                        if (matches.Length == 1) ruleKey = string.IsNullOrEmpty(matches[0].replacementKey) ? matches[0].name : matches[0].replacementKey;
                    }
                    if (legacy && matches.Length != 1 && source.GetComponent<Image>()?.sprite != null)
                    {
                        string imageName = source.GetComponent<Image>().sprite.name;
                        matches = originals.Where(l => l.name == imageName).ToArray();
                    }
                    if (legacy && matches.Length != 1 && source.GetComponent<TMP_Text>() != null)
                    {
                        string text = source.GetComponent<TMP_Text>().text;
                        matches = originals.Where(l => l.type == "textLayer" && l.textInfo?.text == text).ToArray();
                    }
                    var original = matches.Length == 1 ? matches[0] : null;
                    if (original == null) payload.warnings.Add("节点缺少原始图层映射，按当前外观导出: " + source.name);
                    return new LocalPSDSourceRecord { objectId = id, stableId = Guid.NewGuid().ToString("N"), original = LocalPSDOriginalLayer.From(original), replacementKey = ruleKey,
                        initialName = original?.name ?? source.name, prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath),
                        fileId = GlobalObjectId.GetGlobalObjectIdSlow(source).targetObjectId };
                }

                LocalPSDReverseNode Visit(Transform source, Transform copy)
                {
                    token.ThrowIfCancellationRequested();
                    if (EditorUtility.DisplayCancelableProgressBar("导出 PSD", source.name, Mathf.Min(0.99f, (float)++visited / total)))
                        throw new OperationCanceledException();
                    if (copy.GetComponent<TMP_SubMeshUI>() != null) return null;
                    var record = Resolve(source.gameObject);
                    newMap.nodes.Add(record);
                    var node = new LocalPSDReverseNode { name = source.name, stableId = record.stableId, original = record.original,
                        replacementKey = record.replacementKey, bindingName = record.original?.bindingName ?? record.original?.name,
                        prefabKey = record.original?.prefabKey, visible = visibility.TryGetValue(copy, out bool active) && active };
                    if (record.control != null && !LocalPSDControlRules.IsDescriptionOnly(record.control.type))
                        node.control = new LocalPSDMarkedControl { type = record.control.type,
                            name = LocalPSDControlRules.IsBindingName(source.name) ? source.name : record.control.name };
                    if (string.IsNullOrEmpty(node.prefabKey) && record.original != null
                        && (record.original.name.StartsWith("P_", StringComparison.Ordinal) || record.original.name.StartsWith("G_P_", StringComparison.Ordinal))) node.prefabKey = record.original.name;
                    if (copy is RectTransform childRect)
                    {
                        var corners = new Vector3[4]; childRect.GetWorldCorners(corners);
                        var points = corners.Select(camera.WorldToViewportPoint).ToArray();
                        node.left = points.Min(p => p.x) * width; node.top = (1 - points.Max(p => p.y)) * height;
                        node.width = (points.Max(p => p.x) - points.Min(p => p.x)) * width;
                        node.height = (points.Max(p => p.y) - points.Min(p => p.y)) * height;
                    }
                    bool restored = !string.IsNullOrEmpty(node.replacementKey) && node.original != null && File.Exists(payload.sourcePsd);
                    bool shared = node.prefabKey != null && node.prefabKey.StartsWith("G_P_", StringComparison.Ordinal) && node.original != null && File.Exists(payload.sourcePsd);
                    if (!restored && !shared)
                    {
                        var graphic = copy.GetComponent<Graphic>();
                        if (graphic != null)
                        {
                            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                                throw new InvalidOperationException("导出当前 UI 外观需要图形设备，请在正常 Unity Editor 中运行。");
                            graphic.enabled = true;
                            if (graphic is TMP_Text text)
                            {
                                text.ForceMeshUpdate(true, true);
                                node.textInfo = new LocalPSDText { text = text.text, size = text.fontSize,
                                    color = new LocalPSDColor { r = text.color.r, g = text.color.g, b = text.color.b, a = text.color.a },
                                    fontName = text.font?.name, fontPostScriptName = record.original?.textInfo?.fontPostScriptName,
                                    justification = record.original?.textInfo?.justification ?? "center" };
                                float rotation = Mathf.Abs(Mathf.DeltaAngle(0, copy.eulerAngles.z));
                                var scale = copy.lossyScale;
                                var color = record.original?.textInfo?.color;
                                node.nativeTextCompatible = !text.enableVertexGradient && rotation < 0.01f && Mathf.Abs(scale.x-1) < 0.001f && Mathf.Abs(scale.y-1) < 0.001f
                                    && color != null && Mathf.Abs(color.r-text.color.r) < 0.001f && Mathf.Abs(color.g-text.color.g) < 0.001f
                                    && Mathf.Abs(color.b-text.color.b) < 0.001f && Mathf.Abs(color.a-text.color.a) < 0.001f;
                                foreach (var submesh in text.GetComponentsInChildren<TMP_SubMeshUI>(true)) submesh.enabled = true;
                            }
                            Canvas.ForceUpdateCanvases();
                            camera.Render();
                            var previous = RenderTexture.active;
                            try { RenderTexture.active = target; readback.ReadPixels(new Rect(0, 0, width, height), 0, 0); readback.Apply(); }
                            finally { RenderTexture.active = previous; }
                            node.image = Path.Combine(cache, (++index) + ".png");
                            File.WriteAllBytes(node.image, readback.EncodeToPNG());
                            graphic.enabled = false;
                            if (graphic is TMP_Text tmp) foreach (var submesh in tmp.GetComponentsInChildren<TMP_SubMeshUI>(true)) submesh.enabled = false;
                            if (!graphicEnabled[graphic]) node.visible = false;
                        }
                        for (int i = 0; i < Math.Min(source.childCount, copy.childCount); i++)
                        {
                            var child = Visit(source.GetChild(i), copy.GetChild(i));
                            if (child != null) node.children.Add(child);
                        }
                    }
                    return node;
                }

                for (int i = 0; i < Math.Min(asset.transform.childCount, clone.transform.childCount); i++)
                {
                    var node = Visit(asset.transform.GetChild(i), clone.transform.GetChild(i));
                    if (node != null) payload.nodes.Add(node);
                }
                if (payload.nodes.Count == 0) throw new InvalidOperationException("预设体没有可导出的子节点。");
                string path = Path.Combine(cache, "reverse.json");
                // JsonUtility registers recursive field graphs with a depth limit
                // that also counts provenance fields. This writer handles the
                // arbitrary group tree without truncating original text metadata.
                File.WriteAllText(path, LocalPSDJson.Serialize(payload));
                LocalPSDRoundTrip.Save(prefabPath, newMap);
                return path;
            }
            finally
            {
                if (cameraObject != null) Object.DestroyImmediate(cameraObject);
                if (canvasObject != null) Object.DestroyImmediate(canvasObject);
                if (readback != null) Object.DestroyImmediate(readback);
                if (target != null) { target.Release(); Object.DestroyImmediate(target); }
                EditorSceneManager.ClosePreviewScene(scene);
                EditorUtility.ClearProgressBar();
            }
        }
    }
}

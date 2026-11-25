using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Starlight.Game.Editor.LocalUIConverter
{
    internal sealed class LocalPSDInteractionPreview
    {
        //图片解析服务依赖本地模型 先都链到我本地
        public const string DefaultServerAddress = "http://10.236.12.112:8000";
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false
        }) { Timeout = TimeSpan.FromMinutes(3) };

        internal enum ComponentType
        {
            None = 0,
            Button = 1,
            Text = 2,
            Scroll = 3,
            Toggle = 4,
            Slider = 5
        }

        private static readonly int ComponentTypeCount = Enum.GetValues(typeof(ComponentType)).Length;

        [Serializable] internal sealed class Candidate
        {
            public Rect Bounds;
            public int LayerIndex;
            public ComponentType Type;
            public string Note = "";
            public string BindingName = "";
            public string DetectedContent;
            public string SuggestionReason;
            public bool PendingReview;
            public bool ManuallyEdited;

            public void CycleType(int mouseButton)
            {
                if (mouseButton == 2)
                {
                    ResetType();
                    return;
                }

                int direction = mouseButton == 0 ? 1 : -1;
                Type = (ComponentType)(((int)Type + direction + ComponentTypeCount) % ComponentTypeCount);
                MarkReviewed();
            }

            public void ResetType()
            {
                Type = ComponentType.None;
                MarkReviewed();
            }

            public void MarkReviewed()
            {
                PendingReview = false;
                ManuallyEdited = true;
            }
        }

        private sealed class Layer
        {
            public LocalPSDLayer Source;
            public Rect Bounds;
            public string Path;
        }

        [DataContract]
        private sealed class ParseResult
        {
            [DataMember] public Element[] parsed_content_list;
        }

        [DataContract]
        private sealed class Element
        {
            [DataMember] public bool interactivity;
            [DataMember] public float[] bbox;
            [DataMember] public string content;
        }

        [DataContract]
        private sealed class ProbeResult
        {
            [DataMember] public string message;
        }

        public Candidate[] Candidates { get; }
        public string[] LayerOptions { get; }
        public string[] LayerIds { get; }

        private LocalPSDInteractionPreview(Candidate[] candidates, string[] layerOptions, string[] layerIds)
        {
            Candidates = candidates;
            LayerOptions = layerOptions;
            LayerIds = layerIds;
        }

        public static Uri ParseServerAddress(string address)
        {
            string value = string.IsNullOrWhiteSpace(address) ? DefaultServerAddress : address.Trim();
            if (!Uri.TryCreate(value, UriKind.Absolute, out var server)
                || (server.Scheme != Uri.UriSchemeHttp && server.Scheme != Uri.UriSchemeHttps)
                || server.AbsolutePath != "/" || !string.IsNullOrEmpty(server.UserInfo)
                || !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment))
                throw new ArgumentException("请填写 OmniParser 服务根地址，例如 http://192.168.1.20:8000。");
            return server;
        }

        public static async Task ProbeAsync(Uri server, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                using (var response = await Client.GetAsync(new Uri(server, "probe/"), timeout.Token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (LocalPSDJson.Deserialize<ProbeResult>(json)?.message != "Omniparser API ready")
                        throw new InvalidDataException("服务响应与 OmniParser 的探测接口不一致。");
                }
            }
        }

        public static async Task<LocalPSDInteractionPreview> AnalyzeAsync(
            Uri server, string previewPath, string layoutJson, CancellationToken token)
        {
            string image = Convert.ToBase64String(File.ReadAllBytes(previewPath));
            using (var request = new StringContent("{\"base64_image\":\"" + image + "\"}", Encoding.UTF8, "application/json"))
            using (var response = await Client.PostAsync(new Uri(server, "parse/"), request, token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"OmniParser 请求失败（HTTP {(int)response.StatusCode}）。");
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var result = LocalPSDJson.Deserialize<ParseResult>(json);
                if (result?.parsed_content_list == null)
                    throw new InvalidDataException("OmniParser 返回的数据缺少 parsed_content_list。");
                return BuildPreview(LocalPSDDocument.Parse(layoutJson).artboard, result.parsed_content_list);
            }
        }

        private static LocalPSDInteractionPreview BuildPreview(LocalPSDArtboard board, Element[] elements)
        {
            var layers = new List<Layer>();
            CollectLayers(board.layers, board, "", layers);
            var pathCounts = layers.GroupBy(layer => layer.Path).ToDictionary(group => group.Key, group => group.Count());
            var layerOptions = new[] { "未匹配图层" }.Concat(layers.Select(layer =>
                pathCounts[layer.Path] > 1 ? $"{layer.Path}（图层 {layer.Source.id}）" : layer.Path)).ToArray();
            var layerIds = new[] { "" }.Concat(layers.Select(layer => layer.Source.id)).ToArray();

            var candidates = new List<Candidate>();
            foreach (Element element in elements)
            {
                if (element == null || !element.interactivity || !TryGetBounds(element.bbox, out Rect bounds)) continue;
                candidates.Add(new Candidate
                {
                    Bounds = bounds, LayerIndex = MatchLayer(bounds, layers), DetectedContent = element.content
                });
            }
            return new LocalPSDInteractionPreview(candidates.ToArray(), layerOptions, layerIds);
        }

        internal static LocalPSDInteractionPreview Restore(string layoutJson, Candidate[] candidates)
        {
            var options = BuildPreview(LocalPSDDocument.Parse(layoutJson).artboard, Array.Empty<Element>());
            foreach (var candidate in candidates ?? Array.Empty<Candidate>())
                if (candidate == null || candidate.LayerIndex < 0 || candidate.LayerIndex >= options.LayerIds.Length
                    || !Enum.IsDefined(typeof(ComponentType), candidate.Type))
                    throw new InvalidDataException("审核存档包含无效的候选或图层索引。");
            return new LocalPSDInteractionPreview(candidates ?? Array.Empty<Candidate>(), options.LayerOptions, options.LayerIds);
        }

        private static void CollectLayers(LocalPSDLayer[] source, LocalPSDArtboard board, string parent, List<Layer> layers)
        {
            if (source == null) return;
            foreach (LocalPSDLayer layer in source)
            {
                if (!layer.isVisible) continue;
                string path = string.IsNullOrEmpty(parent) ? layer.name : parent + " › " + layer.name;
                var bounds = ClipBounds(layer.left / board.width, layer.top / board.height,
                    (layer.left + layer.width) / board.width, (layer.top + layer.height) / board.height);
                layers.Add(new Layer { Source = layer, Bounds = bounds, Path = path });
                CollectLayers(layer.layers, board, path, layers);
            }
        }

        private static int MatchLayer(Rect box, List<Layer> layers)
        {
            int bestIndex = 0;
            float bestScore = 0;
            float boxArea = box.width * box.height;
            for (int index = 0; index < layers.Count; index++)
            {
                Layer layer = layers[index];
                if (layer.Source.type == "textLayer" || layer.Source.textInfo != null) continue;
                Rect bounds = layer.Bounds;
                float overlapWidth = Math.Min(box.xMax, bounds.xMax) - Math.Max(box.xMin, bounds.xMin);
                float overlapHeight = Math.Min(box.yMax, bounds.yMax) - Math.Max(box.yMin, bounds.yMin);
                if (overlapWidth <= 0 || overlapHeight <= 0) continue;

                float intersection = overlapWidth * overlapHeight;
                float iou = intersection / (boxArea + bounds.width * bounds.height - intersection);
                if (iou < 0.35f || intersection / boxArea < 0.55f) continue;
                // Prefer a sprite-bearing layer over a similarly sized group for the button's Image.
                float score = iou + (!string.IsNullOrEmpty(layer.Source.images?.png_xxxhd) ? 0.08f : 0f);
                if (score <= bestScore) continue;
                bestScore = score;
                bestIndex = index + 1;
            }
            return bestIndex;
        }

        private static bool TryGetBounds(float[] values, out Rect bounds)
        {
            bounds = default;
            if (values == null || values.Length != 4 || values.Any(value => float.IsNaN(value) || float.IsInfinity(value)))
                return false;
            bounds = ClipBounds(values[0], values[1], values[2], values[3]);
            return bounds.width > 0 && bounds.height > 0;
        }

        private static Rect ClipBounds(float left, float top, float right, float bottom)
        {
            return Rect.MinMaxRect(Mathf.Clamp01(left), Mathf.Clamp01(top), Mathf.Clamp01(right), Mathf.Clamp01(bottom));
        }
    }
}

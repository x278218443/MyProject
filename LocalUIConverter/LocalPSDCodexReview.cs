using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using ComponentType = Starlight.Game.Editor.LocalUIConverter.LocalPSDInteractionPreview.ComponentType;

namespace Starlight.Game.Editor.LocalUIConverter
{
    internal sealed class LocalPSDCodexReview
    {
        private readonly string requestId;
        private readonly string resultPath;
        private readonly string requestPath;
        public string Prompt { get; }
        public string OutputName { get; private set; }
        public string DirectoryPath => Path.GetDirectoryName(requestPath);

        [DataContract] private sealed class SavedReview
        {
            [DataMember] public string requestPath;
        }

        [DataContract]
        private sealed class Request
        {
            [DataMember] public string requestId;
            [DataMember] public string previewPath;
            [DataMember] public string layoutPath;
            [DataMember] public string resultPath;
            [DataMember] public InputCandidate[] candidates;
        }

        [DataContract]
        private sealed class InputCandidate
        {
            [DataMember] public int number;
            [DataMember] public float[] bbox;
            [DataMember] public string matchedLayerId;
            [DataMember] public string detectedContent;
        }

        [DataContract]
        private sealed class Result
        {
            [DataMember] public string requestId;
            [DataMember] public Suggestion[] suggestions;
        }

        [DataContract]
        private sealed class Suggestion
        {
            [DataMember] public int number;
            [DataMember] public string type;
            [DataMember] public string layerId;
            [DataMember] public string note;
            [DataMember] public string reason;
            [DataMember] public string bindingName;
        }

        private LocalPSDCodexReview(string requestId, string requestPath, string resultPath)
        {
            this.requestId = requestId;
            this.resultPath = resultPath;
            this.requestPath = requestPath;
            Prompt = $"请研判当前 Unity LocalUIConverter 的组件候选。读取任务文件 \"{requestPath}\"，"
                + "打开其中的 previewPath 合成图并结合 layoutPath 的 PSD 图层、相邻文字及 candidates 的 bbox 判断。"
                + "bbox 采用从左上角起算的归一化 [left, top, right, bottom]。"
                + "detectedContent 只能作为辅助线索。Button 优先关联带图片的按钮底图。"
                + "仅建议有足够依据的组件，可跳过可疑候选。备注写推测用途，理由简述画面或图层证据。"
                + "把结果写入任务文件指定的 resultPath，格式为 "
                + "{\"requestId\":\"原任务 ID\",\"suggestions\":[{\"number\":1,\"type\":\"Button\","
                + "\"layerId\":\"layout.json 中的 id\",\"bindingName\":\"_openActivityButton\",\"note\":\"用途\",\"reason\":\"理由\"}]}。"
                + "type 仅可用 Button、Text、Scroll、Toggle、Slider。layerId 留空会沿用现有匹配。"
                + "bindingName 用反映用途的英文小驼峰，以下划线开头，可留空。"
                + "完成后在对话里简述结果，项目代码和资产无需改动。";
        }

        private static string NamedDirectory(string projectRoot, string outputName)
        {
            if (string.IsNullOrWhiteSpace(outputName)) throw new InvalidOperationException("请先填写输出名称，用于保存和查找 Codex 建议。");
            return Path.Combine(projectRoot, "__Temp", "LocalUIConverter", "CodexReview", LocalPSDPrefabGenerator.SafeName(outputName));
        }

        public static LocalPSDCodexReview Export(string projectRoot, string previewPath, LocalPSDInteractionPreview preview, string outputName)
        {
            string cache = Path.GetDirectoryName(previewPath);
            string id = Guid.NewGuid().ToString("N");
            string directory = Path.Combine(NamedDirectory(projectRoot, outputName), id);
            Directory.CreateDirectory(directory);
            string requestPath = Path.Combine(directory, "request.json");
            string resultPath = Path.Combine(directory, "suggestions.json");
            string requestPreviewPath = Path.Combine(directory, "preview.png");
            string requestLayoutPath = Path.Combine(directory, "layout.json");
            File.Copy(previewPath, requestPreviewPath);
            File.Copy(Path.Combine(cache, "layout.json"), requestLayoutPath);
            var request = new Request
            {
                requestId = id,
                previewPath = requestPreviewPath,
                layoutPath = requestLayoutPath,
                resultPath = resultPath,
                candidates = preview.Candidates.Select((candidate, index) => new InputCandidate
                {
                    number = index + 1,
                    bbox = new[] { candidate.Bounds.xMin, candidate.Bounds.yMin, candidate.Bounds.xMax, candidate.Bounds.yMax },
                    matchedLayerId = preview.LayerIds[candidate.LayerIndex],
                    detectedContent = candidate.DetectedContent
                }).ToArray()
            };
            File.WriteAllText(requestPath, LocalPSDJson.Serialize(request), new UTF8Encoding(false));
            return new LocalPSDCodexReview(id, requestPath, resultPath) { OutputName = outputName };
        }

        private static bool Compatible(string savedLayout, string currentLayout)
        {
            var saved = LocalPSDDocument.Parse(savedLayout);
            var current = LocalPSDDocument.Parse(currentLayout);
            if (!string.Equals(saved.source, current.source, StringComparison.OrdinalIgnoreCase)
                || saved.artboard.width != current.artboard.width || saved.artboard.height != current.artboard.height) return false;
            // Ignore control marks and notes, which can change after the review; retain
            // layer identity and geometry checks so sequential PSD IDs cannot bind to another layer.
            string LayerKey(LocalPSDLayer layer) => LocalPSDJson.Serialize(new LocalPSDLayer { id = layer.id, name = layer.name,
                psdLayerId = layer.psdLayerId, type = layer.type, left = layer.left, top = layer.top,
                width = layer.width, height = layer.height, isVisible = layer.isVisible });
            return saved.artboard.Descendants().Select(LayerKey).SequenceEqual(current.artboard.Descendants().Select(LayerKey));
        }

        public static LocalPSDCodexReview LoadLatest(string projectRoot, string outputName, string layoutJson)
        {
            string directory = NamedDirectory(projectRoot, outputName);
            var paths = new List<string>();
            var legacyPaths = new List<string>();
            if (Directory.Exists(directory)) paths.AddRange(Directory.GetFiles(directory, "request.json", SearchOption.AllDirectories));
            string pointerPath = Path.Combine(directory, "last-success.json");
            if (File.Exists(pointerPath))
            {
                var pointer = LocalPSDJson.Deserialize<SavedReview>(File.ReadAllText(pointerPath));
                if (File.Exists(pointer?.requestPath)) paths.Add(pointer.requestPath);
            }
            // The previous version stored GUID directories without an output-name key.
            // Adopt only completed old requests whose PSD layout still matches exactly.
            string root = Path.Combine(projectRoot, "__Temp", "LocalUIConverter", "CodexReview");
            if (Directory.Exists(root))
                foreach (string legacy in Directory.GetDirectories(root))
                    if (Guid.TryParseExact(Path.GetFileName(legacy), "N", out _) && File.Exists(Path.Combine(legacy, "request.json")))
                        legacyPaths.Add(Path.Combine(legacy, "request.json"));
            DateTime CompletedAt(string path)
            {
                string result = Path.Combine(Path.GetDirectoryName(path), "suggestions.json");
                return File.Exists(result) ? File.GetLastWriteTimeUtc(result) : DateTime.MinValue;
            }
            // A named result always wins over an unassociated legacy result for the same PSD.
            foreach (string path in paths.Distinct().OrderByDescending(CompletedAt)
                .Concat(legacyPaths.OrderByDescending(CompletedAt)))
            {
                string folder = Path.GetDirectoryName(path);
                string result = Path.Combine(folder, "suggestions.json"), layout = Path.Combine(folder, "layout.json");
                if (!File.Exists(result) || !File.Exists(layout)) continue;
                if (!Compatible(File.ReadAllText(layout), layoutJson)) continue;
                var request = LocalPSDJson.Deserialize<Request>(File.ReadAllText(path));
                var reply = LocalPSDJson.Deserialize<Result>(File.ReadAllText(result));
                if (string.IsNullOrEmpty(request?.requestId) || request.candidates == null
                    || reply?.requestId != request.requestId || reply.suggestions == null) continue;
                Directory.CreateDirectory(directory);
                File.WriteAllText(pointerPath, LocalPSDJson.Serialize(new SavedReview { requestPath = path }));
                return new LocalPSDCodexReview(request.requestId, path, result) { OutputName = outputName };
            }
            throw new FileNotFoundException("没有找到「" + outputName + "」可复用的 Codex 建议。请确认已完成研判，且 PSD 图层与布局未变化。", directory);
        }

        public LocalPSDInteractionPreview RestorePreview(string layoutJson)
        {
            if (!Compatible(File.ReadAllText(Path.Combine(DirectoryPath, "layout.json")), layoutJson))
                throw new InvalidDataException("旧建议对应的 PSD 图层或布局已变化，请重新研判。");
            var request = LocalPSDJson.Deserialize<Request>(File.ReadAllText(requestPath));
            var options = LocalPSDInteractionPreview.Restore(layoutJson, Array.Empty<LocalPSDInteractionPreview.Candidate>());
            return LocalPSDInteractionPreview.Restore(layoutJson, request.candidates.Select(candidate =>
                new LocalPSDInteractionPreview.Candidate { Bounds = UnityEngine.Rect.MinMaxRect(candidate.bbox[0], candidate.bbox[1], candidate.bbox[2], candidate.bbox[3]),
                    LayerIndex = Math.Max(0, Array.IndexOf(options.LayerIds, candidate.matchedLayerId)), DetectedContent = candidate.detectedContent }).ToArray());
        }

        public (int Applied, int Preserved) Import(LocalPSDInteractionPreview preview)
        {
            var request = LocalPSDJson.Deserialize<Request>(File.ReadAllText(requestPath));
            if (request.candidates.Length != preview.Candidates.Length)
                throw new InvalidDataException("当前候选与旧建议不是同一组，请加载原工作流后读取，或重新研判。");
            for (int i = 0; i < preview.Candidates.Length; i++)
            {
                var box = request.candidates[i].bbox;
                var current = preview.Candidates[i].Bounds;
                if (box == null || box.Length != 4 || Math.Abs(box[0] - current.xMin) > 0.0001f || Math.Abs(box[1] - current.yMin) > 0.0001f
                    || Math.Abs(box[2] - current.xMax) > 0.0001f || Math.Abs(box[3] - current.yMax) > 0.0001f)
                    throw new InvalidDataException("当前候选顺序或位置与旧建议不一致，请加载原工作流后读取，或重新研判。");
            }
            if (!File.Exists(resultPath))
                throw new FileNotFoundException("Codex 结果还未写入，等对话完成后再读取。", resultPath);
            var result = LocalPSDJson.Deserialize<Result>(File.ReadAllText(resultPath, Encoding.UTF8));
            if (result?.requestId != requestId || result.suggestions == null)
                throw new InvalidDataException("Codex 结果缺少建议列表或任务 ID 与本次请求不一致。");

            var seen = new HashSet<int>();
            var updates = new List<(Suggestion Suggestion, ComponentType Type, int LayerIndex)>();
            foreach (Suggestion suggestion in result.suggestions)
            {
                if (suggestion == null || suggestion.number < 1 || suggestion.number > preview.Candidates.Length
                    || !seen.Add(suggestion.number))
                    throw new InvalidDataException("Codex 结果包含未知或重复的候选编号。");
                if (!Enum.TryParse(suggestion.type, out ComponentType type) || type == ComponentType.None
                    || Enum.GetName(typeof(ComponentType), type) != suggestion.type)
                    throw new InvalidDataException($"候选 {suggestion.number} 的组件类型无效。");
                int layerIndex = string.IsNullOrEmpty(suggestion.layerId) ? 0 : Array.IndexOf(preview.LayerIds, suggestion.layerId);
                if (layerIndex < 0)
                    throw new InvalidDataException($"候选 {suggestion.number} 的 PSD 图层 ID 无效。");
                updates.Add((suggestion, type, layerIndex));
            }

            int applied = 0;
            foreach (var update in updates)
            {
                var candidate = preview.Candidates[update.Suggestion.number - 1];
                if (candidate.ManuallyEdited) continue;
                candidate.Type = update.Type;
                if (update.LayerIndex != 0) candidate.LayerIndex = update.LayerIndex;
                candidate.Note = update.Suggestion.note ?? "";
                candidate.BindingName = LocalPSDControlRules.IsBindingName(update.Suggestion.bindingName)
                    ? update.Suggestion.bindingName : "";
                candidate.SuggestionReason = update.Suggestion.reason ?? "";
                candidate.PendingReview = true;
                applied++;
            }
            return (applied, updates.Count - applied);
        }
    }
}

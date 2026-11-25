using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;
using InteractionComponent = Starlight.Game.Editor.LocalUIConverter.LocalPSDInteractionPreview.ComponentType;

namespace Starlight.Game.Editor.LocalUIConverter
{
    /// <summary>Standalone local PSD import, preview and prefab generation.</summary>
    public sealed class LocalUIConverterWindow : EditorWindow
    {
        private const string Script = "Assets/Scripts/Editor/LocalUIConverter/psd_export.py";
        private const string PrefKey = "LocalUIConverter.Python";
        private const float PreviewHeight = 760f;
        private static readonly InteractionComponent[] InteractionTypes =
            (InteractionComponent[])Enum.GetValues(typeof(InteractionComponent));
        [SerializeField] private string psdPath = "";
        [SerializeField] private string referencePath = "";
        [SerializeField] private string outputName = "";
        [SerializeField] private string outputDirectory = "";
        [SerializeField] private string functionId = "";
        [SerializeField] private string prefabDirectory = "";
        [SerializeField] private string scriptDirectory = "";
        [SerializeField] private string currentPrefabGuid = "";
        [SerializeField] private string[] generatedPrefabGuids = Array.Empty<string>();
        [SerializeField] private string generatedExportDirectory = "";
        [SerializeField] private string psdExportDirectory = "__Temp/PSD";
        [SerializeField] private bool rasterText;
        [SerializeField] private bool showMarkedCandidateNumbers = true;
        [SerializeField] private LocalUIConverterSettings settings;
        private string python;
        private string omniParserAddress;
        private string status = "";
        private string report = "";
        private string layoutJson;
        private string analyzedSource;
        private DateTime analyzedWriteTime;
        private bool analyzedRasterText;
        private bool loadedImportedLayout;
        private Texture2D psdPreview;
        private string psdPreviewPath;
        private LocalPSDInteractionPreview interactionPreview;
        private LocalPSDCodexReview codexReview;
        private LocalPSDInteractionPreview listedInteractionPreview;
        private IGrouping<InteractionComponent, int>[] listedInteractionGroups =
            Array.Empty<IGrouping<InteractionComponent, int>>();
        private int selectedInteraction;
        private Texture2D referencePreview;
        private string loadedReference;
        private Vector2 referencePreviewOffset;
        private Vector2 scroll;
        private CancellationTokenSource cancellation;
        private bool busy;
        private bool optionsExpanded;
        private bool reportExpanded;
        private GUIStyle titleStyle;
        private GUIStyle cardStyle;
        private GUIStyle previewTitleStyle;
        private GUIStyle candidateNumberStyle;
        [SerializeField] private string organizerTarget = "";
        [SerializeField] private string commonTextures = "";
        [SerializeField] private string artTextures = "";
        [SerializeField] private bool deleteUnusedTextures = true;
        private bool organizerExpanded;
        private LocalPSDAssetOrganizer.Plan organizationPlan;
        private string organizationLog = "";

        [MenuItem("UIConvert/Local PSD Converter", priority = 101)]
        public static void Open()
        {
            GetWindow<LocalUIConverterWindow>("本地 PSD 转 UI");
        }

        private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
        private static string FunctionIdPrefKey => "LocalUIConverter.FunctionID." + Hash128.Compute(ProjectRoot.Replace('\\', '/').ToLowerInvariant());
        private static string PsdDirectoryPrefKey => "LocalUIConverter.PSDDirectory." + Hash128.Compute(ProjectRoot.Replace('\\', '/').ToLowerInvariant());
        private static string PsdPathPrefKey => "LocalUIConverter.PSDPath." + Hash128.Compute(ProjectRoot.Replace('\\', '/').ToLowerInvariant());
        private static string OmniParserPrefKey => "LocalUIConverter.OmniParserAddress." + Hash128.Compute(ProjectRoot.Replace('\\', '/').ToLowerInvariant());
        private static string Absolute(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ProjectRoot, path));

        private static string BrowseDirectory(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            try
            {
                string directory = Absolute(value.Trim().Trim('"'));
                if (File.Exists(directory)) directory = Path.GetDirectoryName(directory);
                while (!string.IsNullOrEmpty(directory))
                {
                    if (Directory.Exists(directory)) return directory;
                    directory = Path.GetDirectoryName(directory);
                }
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
            return fallback;
        }

        private void OnEnable()
        {
            minSize = new Vector2(700f, 430f);
            python = EditorPrefs.GetString(PrefKey, "python");
            omniParserAddress = EditorPrefs.GetString(OmniParserPrefKey, LocalPSDInteractionPreview.DefaultServerAddress);
            psdPath = EditorPrefs.GetString(PsdPathPrefKey, psdPath);
            functionId = EditorPrefs.GetString(FunctionIdPrefKey, functionId);
            if (settings == null) settings = LocalUIConverterSettings.Load();
            if (string.IsNullOrEmpty(outputDirectory)) outputDirectory = settings != null ? settings.outputRoot : "Assets/__Temp";
            optionsExpanded = false;
            reportExpanded = false;
            organizerExpanded = false;
            if (string.IsNullOrEmpty(commonTextures) && settings != null) commonTextures = settings.commonTextureRoot;
            if (string.IsNullOrEmpty(artTextures) && settings != null) artTextures = settings.artTextureRoot;
            try
            {
                LocalPSDProjectActions.FillFunctionDirectories(functionId, ref prefabDirectory, ref scriptDirectory, ref artTextures);
                SaveFunctionId();
            }
            catch (ArgumentException exception) { status = exception.Message; }
            AssemblyReloadEvents.beforeAssemblyReload += Cancel;
            LocalPSDProjectActions.StatusChanged += OnBindStatus;
            if (!string.IsNullOrEmpty(LocalPSDProjectActions.LastBindStatus)) status = LocalPSDProjectActions.LastBindStatus;
        }

        private void OnDisable()
        {
            try { SaveWorkflow(); }
            catch (Exception exception) { Debug.LogWarning("[Local PSD UI] 保存工作流进度失败: " + exception.Message); }
            Cancel();
            AssemblyReloadEvents.beforeAssemblyReload -= Cancel;
            LocalPSDProjectActions.StatusChanged -= OnBindStatus;
            if (psdPreview != null) DestroyImmediate(psdPreview);
            if (referencePreview != null) DestroyImmediate(referencePreview);
        }

        private void Cancel() => cancellation?.Cancel();

        private void OnBindStatus(string message)
        {
            status = message;
            if (this != null) Repaint();
        }

        private void EnsureStyles()
        {
            if (titleStyle != null) return;
            titleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 22, fixedHeight = 32 };
            cardStyle = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(16, 16, 14, 14) };
            previewTitleStyle = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter, fixedHeight = 26 };
            candidateNumberStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
                padding = new RectOffset(),
                normal = { textColor = Color.black }
            };
        }

        private void OnGUI()
        {
            EnsureStyles();
            float oldLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 90;
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.BeginVertical(new GUIStyle { padding = new RectOffset(20, 20, 18, 20) });
            EditorGUILayout.LabelField("本地 PSD 转 UI", titleStyle);
            EditorGUILayout.LabelField("选择设计稿，预览并生成 UI 预设体", EditorStyles.miniLabel);
            GUILayout.Space(16);
            bool analysisCurrent;

            using (new EditorGUI.DisabledScope(busy || LocalPSDProjectActions.HasPendingBind))
            {
                EditorGUILayout.BeginVertical(cardStyle);
                string previousPsdPath = psdPath;
                FileField("PSD 文件", ref psdPath, "psd", PsdDirectoryPrefKey);
                if (previousPsdPath != psdPath) EditorPrefs.SetString(PsdPathPrefKey, psdPath);
                GUILayout.Space(8);
                FileField("效果图", ref referencePath, "jpg,jpeg,png");
                GUILayout.Space(8);
                outputName = EditorGUILayout.TextField("输出名称", outputName, GUILayout.Height(24));
                GUILayout.Space(8);
                DrawOutputDirectory();
                GUILayout.Space(14);
                DrawFunctionId();
                GUILayout.Space(8);
                FolderField("预设体目录", ref prefabDirectory);
                GUILayout.Space(8);
                FolderField("脚本目录", ref scriptDirectory);
                EditorGUILayout.EndVertical();
                GUILayout.Space(12);

                EditorGUILayout.BeginVertical(cardStyle);
                optionsExpanded = EditorGUILayout.Foldout(optionsExpanded, "其他选项", true, EditorStyles.foldoutHeader);
                if (optionsExpanded)
                {
                    GUILayout.Space(12);
                    DrawOtherOptions();
                }
                EditorGUILayout.EndVertical();
                GUILayout.Space(16);

                GUILayout.Box("按序号为新生成预设体的标准流程 带？的为可选项");
                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(psdPath)))
                    if (GUILayout.Button("①解析 / 预览", GUILayout.Height(36))) Run(Analyze);
                GUILayout.Space(10);
                Color oldBackground = GUI.backgroundColor;
                GUI.backgroundColor = EditorGUIUtility.isProSkin ? new Color(0.3f, 0.65f, 1f) : new Color(0.5f, 0.75f, 1f);
                analysisCurrent = IsAnalysisCurrent();
                using (new EditorGUI.DisabledScope(!analysisCurrent || settings == null || string.IsNullOrWhiteSpace(outputName) || string.IsNullOrWhiteSpace(outputDirectory)))
                    if (GUILayout.Button("④生成预设体", GUILayout.Height(36))) Run(Generate);
                GUI.backgroundColor = oldBackground;
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(8);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("加载已导入的目录", GUILayout.Height(32))) Run(ChooseImportedDirectory);
                GUILayout.Space(10);
                if (GUILayout.Button("读取节点描述", GUILayout.Height(32))) Run(ReadNodeDescriptions);
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(8);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("⑤生成Bind脚本", GUILayout.Height(32))) Run(token => GenerateBind(token, false));
                GUILayout.Space(10);
                if (GUILayout.Button("⑤生成Bind脚本(组件)", GUILayout.Height(32))) Run(token => GenerateBind(token, true));
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(8);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("⑥移动预设体到工程目录", GUILayout.Height(32))) Run(MovePrefabsToProject);
                GUILayout.Space(10);
                if (GUILayout.Button("⑦整理切图", GUILayout.Height(32)))
                {
                    if (string.IsNullOrWhiteSpace(organizerTarget)) organizerTarget = Path.Combine(outputDirectory, SafeName(outputName)).Replace('\\', '/');
                    organizerExpanded = true;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (busy && GUILayout.Button("取消当前操作", GUILayout.Height(26))) Cancel();
            if (!string.IsNullOrEmpty(status))
            {
                GUILayout.Space(12);
                EditorGUILayout.HelpBox(status, MessageType.None);
            }
            if (organizerExpanded)
            {
                GUILayout.Space(16);
                DrawOrganizer();
            }
            // Keep the initial screen compact: preview and diagnostics appear only
            // after a PSD has been parsed, rather than showing empty preview panels.
            if (layoutJson != null)
            {
                GUILayout.Space(16);
                if (!busy && loadedReference != referencePath)
                {
                    loadedReference = referencePath;
                    referencePreviewOffset = Vector2.zero;
                    LoadPreview(ref referencePreview, referencePath);
                }
                if (analysisCurrent) DrawInteractionControls();
                EditorGUILayout.BeginHorizontal();
                DrawPsdPreview(analysisCurrent);
                GUILayout.Space(12);
                DrawMarkedInteractions(analysisCurrent ? interactionPreview : null);
                EditorGUILayout.EndHorizontal();
                if (referencePreview != null)
                {
                    GUILayout.Space(12);
                    DrawReferencePreview();
                }
                if (!string.IsNullOrEmpty(report))
                {
                    GUILayout.Space(10);
                    reportExpanded = EditorGUILayout.Foldout(reportExpanded, "查看解析报告", true);
                    if (reportExpanded) EditorGUILayout.SelectableLabel(report, EditorStyles.textArea, GUILayout.Height(150));
                }
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndScrollView();
            EditorGUIUtility.labelWidth = oldLabelWidth;
        }

        private void SaveFunctionId()
        {
            if (string.IsNullOrWhiteSpace(functionId)) EditorPrefs.DeleteKey(FunctionIdPrefKey);
            else EditorPrefs.SetString(FunctionIdPrefKey, functionId.Trim());
        }

        private void ClearFunctionId()
        {
            functionId = "";
            prefabDirectory = "";
            scriptDirectory = "";
            artTextures = "";
            EditorPrefs.DeleteKey(FunctionIdPrefKey);
            organizationPlan = null;
            organizationLog = "";
            status = "已清空 FunctionID 和关联路径。";
        }

        private void DrawFunctionId()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            functionId = EditorGUILayout.DelayedTextField("FunctionID", functionId, GUILayout.Height(24));
            if (EditorGUI.EndChangeCheck())
            {
                try
                {
                    LocalPSDProjectActions.FillFunctionDirectories(functionId, ref prefabDirectory, ref scriptDirectory, ref artTextures);
                    SaveFunctionId();
                }
                catch (ArgumentException exception) { status = exception.Message; }
            }
            if (GUILayout.Button("Clear", GUILayout.Width(68), GUILayout.Height(24)))
            {
                GUI.FocusControl(null);
                ClearFunctionId();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawOtherOptions()
        {
            settings = (LocalUIConverterSettings)EditorGUILayout.ObjectField("本地工具配置", settings, typeof(LocalUIConverterSettings), false);
            if (settings != null)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUIUtility.labelWidth + 2);
                if (GUILayout.Button("编辑配置 / 替换规则", GUILayout.Height(24))) Selection.activeObject = settings;
                EditorGUILayout.EndHorizontal();
            }
            GUILayout.Space(12);
            rasterText = EditorGUILayout.Toggle("文字作为图片", rasterText);
            EditorGUILayout.LabelField(rasterText ? "保留图片外观，文字不可编辑" : "默认生成可编辑文字", EditorStyles.miniLabel);
            GUILayout.Space(12);
            EditorGUILayout.LabelField("图像识别服务", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            string address = EditorGUILayout.TextField("服务地址", omniParserAddress);
            if (address != omniParserAddress)
            {
                omniParserAddress = address;
                EditorPrefs.SetString(OmniParserPrefKey, address);
            }
            if (GUILayout.Button("测试连接", GUILayout.Width(85f))) Run(ProbeOmniParser);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(12);
            EditorGUILayout.LabelField("PSD 反导（试用）", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            psdExportDirectory = EditorGUILayout.TextField("PSD 导出目录", psdExportDirectory, GUILayout.Height(24));
            if (GUILayout.Button("浏览…", GUILayout.Width(68), GUILayout.Height(24)))
            {
                string selected = EditorUtility.OpenFolderPanel("选择 PSD 导出目录", BrowseDirectory(psdExportDirectory, ProjectRoot), "");
                if (!string.IsNullOrEmpty(selected)) psdExportDirectory = selected;
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(6);
            if (GUILayout.Button("导出 PSD", GUILayout.Height(28))) Run(ExportPSD);
            GUILayout.Space(12);
            Rect separator = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(separator, EditorGUIUtility.isProSkin ? new Color(1, 1, 1, 0.1f) : new Color(0, 0, 0, 0.1f));
            GUILayout.Space(10);
            EditorGUILayout.LabelField("Python 环境", EditorStyles.boldLabel);
            python = EditorGUILayout.TextField("可执行文件", python, GUILayout.Height(24));
            GUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("检查环境", GUILayout.Height(26))) Run(async token => { status = await RunPython("--check", token); });
            if (GUILayout.Button("安装 / 补齐依赖", GUILayout.Height(26))) Run(async token =>
            {
                status = "正在安装 PSD 解析依赖…";
                await RunProcess("-m pip install \"psd-tools[composite]==1.17.1\"", token);
                status = await RunPython("--check", token);
            });
            EditorGUILayout.EndHorizontal();
        }

        private void DrawOrganizer()
        {
            EditorGUILayout.BeginVertical(cardStyle);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("资源整理", EditorStyles.boldLabel);
            if (GUILayout.Button("收起", EditorStyles.miniButton, GUILayout.Width(46))) organizerExpanded = false;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("优先复用同名图片，再按 IMG / BTN 分类移动", EditorStyles.miniLabel);
            GUILayout.Space(10);
            using (new EditorGUI.DisabledScope(busy))
            {
                EditorGUI.BeginChangeCheck();
                FolderField("导出目录", ref organizerTarget);
                GUILayout.Space(6);
                FolderField("通用图片", ref commonTextures);
                GUILayout.Space(6);
                FolderField("美术切图", ref artTextures);
                GUILayout.Space(8);
                deleteUnusedTextures = EditorGUILayout.Toggle("清理重复原图", deleteUnusedTextures);
                if (EditorGUI.EndChangeCheck()) { organizationPlan = null; organizationLog = ""; }
                GUILayout.Space(10);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("检查整理方案", GUILayout.Height(30))) Run(token =>
                {
                    token.ThrowIfCancellationRequested();
                    organizationPlan = LocalPSDAssetOrganizer.BuildPlan(organizerTarget, commonTextures, artTextures, OrganizerPrefabPaths());
                    organizationLog = OrganizationDetails(organizationPlan);
                    status = organizationPlan.Summary;
                    return Task.CompletedTask;
                });
                using (new EditorGUI.DisabledScope(organizationPlan == null || organizationPlan.conflicts.Count > 0))
                    if (GUILayout.Button("执行整理", GUILayout.Height(30))) Run(Organize);
                EditorGUILayout.EndHorizontal();
            }
            if (!string.IsNullOrEmpty(organizationLog))
            {
                GUILayout.Space(10);
                EditorGUILayout.SelectableLabel(organizationLog, EditorStyles.textArea, GUILayout.Height(180));
            }
            EditorGUILayout.EndVertical();
        }

        private static string OrganizationDetails(LocalPSDAssetOrganizer.Plan plan)
        {
            var text = new StringBuilder(plan.Summary + "\n");
            foreach (string conflict in plan.conflicts) text.AppendLine("[冲突] " + conflict);
            foreach (var item in plan.items)
            {
                string label = item.operation == LocalPSDAssetOrganizer.Operation.ReuseCommon ? "复用通用图"
                    : item.operation == LocalPSDAssetOrganizer.Operation.ReuseArt ? "复用美术图"
                    : item.operation == LocalPSDAssetOrganizer.Operation.Move ? "移动" : "保留";
                text.Append('[').Append(label).Append("] ").Append(Path.GetFileName(item.source));
                if (!string.IsNullOrEmpty(item.destination)) text.Append(" → ").Append(item.destination);
                text.AppendLine();
            }
            return text.ToString();
        }

        private async Task Organize(CancellationToken token)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            string reopenPath = null;
            if (stage != null)
            {
                if (stage.scene.isDirty) throw new InvalidOperationException("请先保存当前预设体的修改，再执行整理。");
                reopenPath = stage.assetPath;
                StageUtility.GoToMainStage();
            }
            try
            {
                status = "正在整理资源和检查引用…";
                var result = await LocalPSDAssetOrganizer.ApplyAsync(organizationPlan, deleteUnusedTextures, token);
                organizationLog = result.Summary + "\n" + string.Join("\n", result.messages);
                status = result.Summary;
            }
            finally
            {
                organizationPlan = null;
                if (!string.IsNullOrEmpty(reopenPath)) PrefabStageUtility.OpenPrefab(reopenPath);
            }
        }

        private void FolderField(string label, ref string value)
        {
            EditorGUILayout.BeginHorizontal();
            value = EditorGUILayout.TextField(label, value, GUILayout.Height(24));
            if (GUILayout.Button("浏览…", GUILayout.Width(68), GUILayout.Height(24)))
            {
                string selected = EditorUtility.OpenFolderPanel(label, BrowseDirectory(value, Application.dataPath), "");
                if (!string.IsNullOrEmpty(selected))
                {
                    try { value = LocalPSDAssetOrganizer.AssetFolder(selected); }
                    catch (ArgumentException exception) { status = exception.Message; }
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawOutputDirectory()
        {
            EditorGUILayout.BeginHorizontal();
            outputDirectory = EditorGUILayout.TextField("输出目录", outputDirectory, GUILayout.Height(24));
            if (GUILayout.Button("浏览…", GUILayout.Width(68), GUILayout.Height(24)))
            {
                string selected = EditorUtility.OpenFolderPanel("选择输出目录", BrowseDirectory(outputDirectory, Application.dataPath), "");
                if (!string.IsNullOrEmpty(selected))
                {
                    string assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
                    selected = Path.GetFullPath(selected).Replace('\\', '/').TrimEnd('/');
                    if (selected.Equals(assets, StringComparison.OrdinalIgnoreCase)) outputDirectory = "Assets";
                    else if (selected.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase)) outputDirectory = "Assets/" + selected.Substring(assets.Length + 1);
                    else status = "请选择项目 Assets 内的输出目录。";
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private static void FileField(string label, ref string value, string extension, string directoryPrefKey = null)
        {
            string previous = value;
            EditorGUILayout.BeginHorizontal();
            value = EditorGUILayout.TextField(label, value, GUILayout.Height(24));
            if (GUILayout.Button("浏览…", GUILayout.Width(68), GUILayout.Height(24)))
            {
                string initialDirectory = directoryPrefKey == null ? value : EditorPrefs.GetString(directoryPrefKey, value);
                string selected = EditorUtility.OpenFilePanel(label, BrowseDirectory(initialDirectory, ProjectRoot), extension);
                if (!string.IsNullOrEmpty(selected)) value = selected;
            }
            EditorGUILayout.EndHorizontal();
            Rect dropArea = GUILayoutUtility.GetLastRect();
            Event evt = Event.current;
            if (dropArea.Contains(evt.mousePosition) && (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (evt.type == EventType.DragPerform && DragAndDrop.paths.Length > 0)
                {
                    DragAndDrop.AcceptDrag();
                    value = DragAndDrop.paths[0];
                }
                evt.Use();
            }
            if (directoryPrefKey != null && value != previous && !string.IsNullOrWhiteSpace(value))
            {
                try
                {
                    string file = Absolute(value.Trim().Trim('"'));
                    if (File.Exists(file) && string.Equals(Path.GetExtension(file), ".psd", StringComparison.OrdinalIgnoreCase))
                        EditorPrefs.SetString(directoryPrefKey, Path.GetDirectoryName(file));
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (PathTooLongException) { }
            }
        }

        private void DrawInteractionControls()
        {
            EditorGUILayout.BeginVertical(cardStyle);
            using (new EditorGUI.DisabledScope(busy || psdPreview == null))
            {
                if (GUILayout.Button(interactionPreview == null ? "②？识别交互候选" : "②？重新识别交互候选"))
                    Run(AnalyzeInteractions);
            }
            if (psdPreview == null)
                EditorGUILayout.HelpBox("这个 PSD 未能生成合成预览。", MessageType.Info);
            if (interactionPreview != null)
            {
                DrawInteractionDetails();
            }
            DrawCodexReviewControls();
            EditorGUILayout.EndVertical();
            GUILayout.Space(8);
        }

        private void DrawCodexReviewControls()
        {
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(busy))
            {
                using (new EditorGUI.DisabledScope(interactionPreview == null || interactionPreview.Candidates.Length == 0))
                    if (GUILayout.Button("③？复制 Codex 研判请求")) Run(CopyCodexReviewRequest);
                if (GUILayout.Button("③？读取 Codex 建议")) Run(ImportCodexReview);
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawPsdPreview(bool showInteractions)
        {
            float cardWidth = position.width * 0.5f;
            EditorGUILayout.BeginVertical(cardStyle, GUILayout.Width(cardWidth));
            EditorGUILayout.LabelField("PSD 合成预览", previewTitleStyle);
            if (psdPreview != null)
            {
                float imageWidth = Mathf.Max(1f, cardWidth - cardStyle.padding.horizontal);
                float imageHeight = imageWidth * psdPreview.height / psdPreview.width;
                Rect image = GUILayoutUtility.GetRect(imageWidth, imageHeight);
                EditorGUI.DrawTextureTransparent(image, psdPreview, ScaleMode.StretchToFill);
                if (showInteractions && interactionPreview != null)
                    DrawInteractionBoxes(image);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawReferencePreview()
        {
            EditorGUILayout.BeginVertical(cardStyle, GUILayout.MinWidth(200));
            EditorGUILayout.LabelField("效果图对照", previewTitleStyle);
            GUIStyle horizontalScrollbar = GUI.skin.horizontalScrollbar;
            float scrollbarHeight = horizontalScrollbar.fixedHeight + horizontalScrollbar.margin.top;
            Rect rect = GUILayoutUtility.GetRect(180f, PreviewHeight + scrollbarHeight, GUILayout.ExpandWidth(true));
            float imageWidth = PreviewHeight * referencePreview.width / referencePreview.height;
            float contentWidth = Mathf.Max(rect.width, imageWidth);
            var content = new Rect(0f, 0f, contentWidth, PreviewHeight);
            referencePreviewOffset = GUI.BeginScrollView(rect, referencePreviewOffset, content,
                false, false, horizontalScrollbar, GUIStyle.none);
            var image = new Rect((contentWidth - imageWidth) / 2f, 0f, imageWidth, PreviewHeight);
            EditorGUI.DrawTextureTransparent(image, referencePreview, ScaleMode.StretchToFill);
            GUI.EndScrollView(false);
            EditorGUILayout.EndVertical();
        }

        private void DrawMarkedInteractions(LocalPSDInteractionPreview preview)
        {
            // Keep the row layout stable when a click or popup changes a candidate type.
            if (Event.current.type == EventType.Layout)
            {
                listedInteractionPreview = preview;
                listedInteractionGroups = preview == null
                    ? Array.Empty<IGrouping<InteractionComponent, int>>()
                    : Enumerable.Range(0, preview.Candidates.Length)
                        .Where(index => preview.Candidates[index].Type != InteractionComponent.None)
                        .GroupBy(index => preview.Candidates[index].Type)
                        .OrderBy(group => group.Key)
                        .ToArray();
            }

            EditorGUILayout.BeginVertical(cardStyle, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(false));
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("已标记组件", previewTitleStyle);
            showMarkedCandidateNumbers = EditorGUILayout.ToggleLeft("显示编号", showMarkedCandidateNumbers,
                GUILayout.Width(76f), GUILayout.Height(previewTitleStyle.fixedHeight));
            EditorGUILayout.EndHorizontal();
            if (listedInteractionGroups.Length == 0)
                EditorGUILayout.LabelField("在左侧预览中标记组件后，这里会显示列表。", EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUI.DisabledScope(busy))
            {
                foreach (var group in listedInteractionGroups)
                {
                    GUILayout.Space(6);
                    EditorGUILayout.LabelField(group.Key.ToString(), EditorStyles.boldLabel);
                    foreach (int index in group)
                    {
                        var candidate = listedInteractionPreview.Candidates[index];
                        string layer = listedInteractionPreview.LayerOptions[candidate.LayerIndex];
                        var label = new GUIContent($"候选 {index + 1}  ·  {layer}", layer);
                        EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.MinWidth(0f));
                        GUI.SetNextControlName("InteractionNote" + index);
                        string note = EditorGUILayout.TextField("备注", candidate.Note);
                        if (note != candidate.Note)
                        {
                            candidate.Note = note;
                            candidate.ManuallyEdited = true;
                        }
                        string bindingName = EditorGUILayout.TextField(new GUIContent("绑定名", "留空时根据图层名和备注自动命名。"),
                            candidate.BindingName);
                        if (bindingName != candidate.BindingName)
                        {
                            candidate.BindingName = bindingName;
                            candidate.ManuallyEdited = true;
                        }
                        if (candidate.SuggestionReason != null)
                        {
                            if (!string.IsNullOrWhiteSpace(candidate.SuggestionReason))
                                EditorGUILayout.LabelField(candidate.SuggestionReason, EditorStyles.wordWrappedMiniLabel);
                            EditorGUILayout.BeginHorizontal();
                            using (new EditorGUI.DisabledScope(!candidate.PendingReview))
                            {
                                if (GUILayout.Button(candidate.PendingReview ? "待确认 · 确认建议" : "已审核"))
                                    candidate.MarkReviewed();
                            }
                            if (GUILayout.Button("否决 · 切换回 None"))
                            {
                                GUI.FocusControl(null);
                                candidate.ResetType();
                                Repaint();
                            }
                            EditorGUILayout.EndHorizontal();
                        }
                        GUILayout.Space(4);
                    }
                }
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawInteractionBoxes(Rect image)
        {
            Event current = Event.current;
            var candidates = interactionPreview.Candidates;
            if (current.type == EventType.MouseDown && current.button >= 0 && current.button <= 2 && !busy
                && image.Contains(current.mousePosition))
            {
                int hit = -1;
                float smallestArea = float.PositiveInfinity;
                for (int i = 0; i < candidates.Length; i++)
                {
                    Rect bounds = InteractionRect(image, candidates[i].Bounds);
                    float area = bounds.width * bounds.height;
                    if (!bounds.Contains(current.mousePosition) || area >= smallestArea) continue;
                    smallestArea = area;
                    hit = i;
                }
                if (hit >= 0)
                {
                    GUI.FocusControl(null);
                    selectedInteraction = hit;
                    candidates[hit].CycleType(current.button);
                    current.Use();
                    Repaint();
                }
            }
            if (current.type != EventType.Repaint || candidates.Length == 0) return;

            for (int i = 0; i < candidates.Length; i++)
            {
                if (i != selectedInteraction) DrawInteractionBox(image, candidates[i], false);
            }
            DrawInteractionBox(image, candidates[selectedInteraction], true);

            if (!showMarkedCandidateNumbers) return;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i].Type != InteractionComponent.None)
                    DrawCandidateNumber(image, candidates[i].Bounds, i + 1);
            }
        }

        private void DrawCandidateNumber(Rect image, Rect normalizedBounds, int number)
        {
            Rect bounds = InteractionRect(image, normalizedBounds);
            float size = Mathf.Min(22f, Mathf.Min(image.width, image.height));
            if (size <= 0f) return;

            var badge = new Rect(
                Mathf.Clamp(bounds.xMin + 2f, image.xMin, image.xMax - size),
                Mathf.Clamp(bounds.yMin + 2f, image.yMin, image.yMax - size),
                size, size);
            GUI.DrawTexture(badge, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, Color.white, 0f, size / 2f);
            GUI.Label(badge, number.ToString(), candidateNumberStyle);
        }

        private static Rect InteractionRect(Rect image, Rect bounds)
        {
            return new Rect(image.x + bounds.x * image.width, image.y + bounds.y * image.height,
                bounds.width * image.width, bounds.height * image.height);
        }

        private static void DrawInteractionBox(Rect image, LocalPSDInteractionPreview.Candidate candidate, bool selected)
        {
            Rect bounds = InteractionRect(image, candidate.Bounds);
            Color color = InteractionColor(candidate.Type);
            if (selected) EditorGUI.DrawRect(bounds, new Color(color.r, color.g, color.b, 0.16f));
            float thickness = Mathf.Min(selected ? 3f : 1.5f, Mathf.Min(bounds.width, bounds.height));
            EditorGUI.DrawRect(new Rect(bounds.xMin, bounds.yMin, bounds.width, thickness), color);
            EditorGUI.DrawRect(new Rect(bounds.xMin, bounds.yMax - thickness, bounds.width, thickness), color);
            EditorGUI.DrawRect(new Rect(bounds.xMin, bounds.yMin, thickness, bounds.height), color);
            EditorGUI.DrawRect(new Rect(bounds.xMax - thickness, bounds.yMin, thickness, bounds.height), color);
        }

        private void DrawInteractionDetails()
        {
            var candidates = interactionPreview.Candidates;
            if (candidates.Length == 0)
            {
                EditorGUILayout.HelpBox("未识别到交互候选。", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField("左键下一类 · 右键上一类 · 中键重置", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            foreach (InteractionComponent type in InteractionTypes)
            {
                Rect swatch = GUILayoutUtility.GetRect(9f, 16f, GUILayout.ExpandWidth(false));
                if (Event.current.type == EventType.Repaint)
                    EditorGUI.DrawRect(new Rect(swatch.x, swatch.y + 4f, 8f, 8f), InteractionColor(type));
                GUILayout.Label(type.ToString(), EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(busy))
            {
                var labels = new string[candidates.Length];
                for (int i = 0; i < candidates.Length; i++)
                    labels[i] = $"{i + 1} / {candidates.Length} · {candidates[i].Type}";
                selectedInteraction = EditorGUILayout.Popup("交互候选", selectedInteraction, labels);
                var candidate = candidates[selectedInteraction];
                var type = (InteractionComponent)EditorGUILayout.EnumPopup("组件类型", candidate.Type);
                int layerIndex = EditorGUILayout.Popup("对应图层", candidate.LayerIndex, interactionPreview.LayerOptions);
                if (type != candidate.Type || layerIndex != candidate.LayerIndex)
                {
                    candidate.Type = type;
                    candidate.LayerIndex = layerIndex;
                    candidate.MarkReviewed();
                }
            }
        }

        private static Color InteractionColor(InteractionComponent type)
        {
            switch (type)
            {
                case InteractionComponent.Button: return new Color(0.25f, 0.95f, 0.50f);
                case InteractionComponent.Text: return new Color(0.27f, 0.72f, 1f);
                case InteractionComponent.Scroll: return new Color(1f, 0.49f, 0.13f);
                case InteractionComponent.Toggle: return new Color(0.79f, 0.49f, 1f);
                case InteractionComponent.Slider: return new Color(1f, 0.79f, 0.23f);
                default: return new Color(1f, 0.24f, 0.25f);
            }
        }

        private static void LoadPreview(ref Texture2D texture, string path)
        {
            if (texture != null) DestroyImmediate(texture);
            texture = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(Absolute(path))) return;
            var loaded = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
            if (loaded.LoadImage(File.ReadAllBytes(Absolute(path)))) texture = loaded;
            else DestroyImmediate(loaded);
        }

        private bool IsAnalysisCurrent()
        {
            return layoutJson != null && analyzedSource == psdPath && analyzedRasterText == rasterText
                && ((loadedImportedLayout && (string.IsNullOrWhiteSpace(psdPath) || !File.Exists(Absolute(psdPath))))
                    || (!string.IsNullOrWhiteSpace(psdPath) && File.Exists(Absolute(psdPath)) && File.GetLastWriteTimeUtc(Absolute(psdPath)) == analyzedWriteTime));
        }

        private void SaveWorkflow()
        {
            if (layoutJson == null || string.IsNullOrWhiteSpace(outputName) || string.IsNullOrWhiteSpace(outputDirectory)) return;
            string directory = LocalPSDAssetOrganizer.AssetFolder(Path.Combine(outputDirectory, SafeName(outputName)));
            // A parsed-but-not-yet-generated draft still needs a directory the user can select later.
            Directory.CreateDirectory(Absolute(directory));
            LocalPSDWorkflow.Save(Absolute(directory), new LocalPSDWorkflow
            {
                layoutJson = layoutJson, psdPath = psdPath, previewPath = psdPreviewPath, referencePath = referencePath,
                sourceWriteTicks = analyzedWriteTime.Ticks, rasterText = analyzedRasterText,
                hasCandidates = interactionPreview != null, candidates = interactionPreview?.Candidates,
                prefabGuid = currentPrefabGuid, prefabGuids = generatedPrefabGuids,
                functionId = functionId, prefabDirectory = prefabDirectory, scriptDirectory = scriptDirectory, artTextures = artTextures
            });
        }

        private Task ChooseImportedDirectory(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string selected = EditorUtility.OpenFolderPanel("加载已导入的目录", BrowseDirectory(generatedExportDirectory, Absolute(outputDirectory)), "");
            if (!string.IsNullOrEmpty(selected)) LoadImportedDirectory(selected);
            return Task.CompletedTask;
        }

        internal void LoadImportedDirectory(string selected)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.scene.isDirty) throw new InvalidOperationException("请先保存当前预设体，再加载其他目录。");
            string directory = LocalPSDAssetOrganizer.AssetFolder(selected);
            var state = LocalPSDWorkflow.Load(Absolute(directory));
            string savedLayout = Absolute(directory + "/layout.json");
            string json = File.Exists(savedLayout) ? File.ReadAllText(savedLayout) : state?.layoutJson;
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("目录中没有 layout.json 或本机工作流存档，请选择本工具的导出目录。");
            var document = LocalPSDDocument.Parse(json);
            // The draft layout preserves the exact indices used by saved OmniParser candidates.
            if (state?.hasCandidates == true) { json = state.layoutJson; document = LocalPSDDocument.Parse(json); }
            var restored = state?.hasCandidates == true ? LocalPSDInteractionPreview.Restore(json, state.candidates) : null;
            string name = Path.GetFileName(directory);
            string tracked = state != null ? AssetDatabase.GUIDToAssetPath(state.prefabGuid) : "";
            if (string.IsNullOrEmpty(tracked)) tracked = directory + "/Prefabs/" + name + ".prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(tracked) == null)
            {
                var matches = AssetDatabase.FindAssets(name + " t:Prefab").Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => Path.GetFileNameWithoutExtension(p) == name).ToArray();
                tracked = matches.Length == 1 ? matches[0] : "";
            }
            SaveWorkflow();
            outputName = name;
            outputDirectory = Path.GetDirectoryName(directory).Replace('\\', '/');
            generatedExportDirectory = organizerTarget = directory;
            currentPrefabGuid = string.IsNullOrEmpty(tracked) ? "" : AssetDatabase.AssetPathToGUID(tracked);
            var prefabs = new HashSet<string>(state?.prefabGuids ?? Array.Empty<string>());
            if (AssetDatabase.IsValidFolder(directory + "/Prefabs"))
                prefabs.UnionWith(AssetDatabase.FindAssets("t:Prefab", new[] { directory + "/Prefabs" }));
            if (!string.IsNullOrEmpty(currentPrefabGuid))
            {
                prefabs.Add(currentPrefabGuid);
                // Include moved generated P_ prefabs referenced by the main result, not common library prefabs.
                prefabs.UnionWith(AssetDatabase.GetDependencies(tracked, true).Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                    && (Path.GetFileName(p).StartsWith("P_", StringComparison.OrdinalIgnoreCase))).Select(AssetDatabase.AssetPathToGUID));
            }
            generatedPrefabGuids = prefabs.Where(g => !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(g))).ToArray();
            layoutJson = json;
            psdPath = state?.psdPath ?? document.source ?? "";
            EditorPrefs.SetString(PsdPathPrefKey, psdPath);
            referencePath = state?.referencePath ?? "";
            if (state != null)
            {
                functionId = state.functionId ?? ""; prefabDirectory = state.prefabDirectory ?? "";
                scriptDirectory = state.scriptDirectory ?? ""; artTextures = state.artTextures ?? "";
                SaveFunctionId();
            }
            rasterText = state?.rasterText ?? false;
            analyzedRasterText = rasterText;
            analyzedSource = psdPath;
            analyzedWriteTime = state != null ? new DateTime(state.sourceWriteTicks)
                : !string.IsNullOrWhiteSpace(psdPath) && File.Exists(Absolute(psdPath)) ? File.GetLastWriteTimeUtc(Absolute(psdPath)) : default;
            loadedImportedLayout = true;
            interactionPreview = restored; codexReview = null; selectedInteraction = 0;
            psdPreviewPath = state?.previewPath;
            if (string.IsNullOrEmpty(psdPreviewPath) || !File.Exists(psdPreviewPath)) psdPreviewPath = Absolute(directory + "/preview.png");
            LoadPreview(ref psdPreview, psdPreviewPath);
            organizationPlan = null; organizationLog = ""; organizerExpanded = !string.IsNullOrEmpty(currentPrefabGuid);
            report = string.Join("\n", document.warnings ?? Array.Empty<string>());
            status = "已加载：" + directory + (string.IsNullOrEmpty(currentPrefabGuid) ? "\n已恢复解析/审核进度，可继续生成预设体。" : "\n可继续编辑预设体、读取节点描述、整理切图和生成 Bind。");
            if (!string.IsNullOrEmpty(currentPrefabGuid)) PrefabStageUtility.OpenPrefab(tracked);
            SaveWorkflow();
        }

        private Task ReadNodeDescriptions(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            string prefab = stage != null ? stage.assetPath : CurrentPrefabPath();
            status = LocalPSDNodeDescriptions.Restore(prefab);
            currentPrefabGuid = AssetDatabase.AssetPathToGUID(prefab);
            return Task.CompletedTask;
        }

        private async void Run(Func<CancellationToken, Task> action)
        {
            if (busy) return;
            busy = true;
            cancellation = new CancellationTokenSource();
            EditorPrefs.SetString(PrefKey, python);
            try { await action(cancellation.Token); }
            catch (OperationCanceledException) { status = "已取消；已完成的操作会保留，可重新检查后继续。"; }
            catch (Exception exception)
            {
                status = exception.Message;
                Debug.LogException(exception);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                cancellation.Dispose();
                cancellation = null;
                busy = false;
                if (this != null) Repaint();
            }
        }

        private async Task Analyze(CancellationToken token)
        {
            layoutJson = null;
            interactionPreview = null;
            codexReview = null;
            psdPreviewPath = null;
            report = "";
            if (!File.Exists(Absolute(psdPath))) throw new FileNotFoundException("找不到 PSD", psdPath);
            if (!string.Equals(Path.GetExtension(psdPath), ".psd", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择 .psd 文件。");
            status = "正在本地解析 PSD 和导出图层…";
            // Separate runs avoid stale textures and permit cancellation without damaging the last output.
            string cache = Path.Combine(ProjectRoot, "Library/LocalUIConverter", Guid.NewGuid().ToString("N"));
            DateTime writeTime = File.GetLastWriteTimeUtc(Absolute(psdPath));
            await RunPython("--source " + Quote(Absolute(psdPath)) + " --output " + Quote(cache)
                            + (rasterText ? " --raster-text" : ""), token);
            token.ThrowIfCancellationRequested();
            string json = File.ReadAllText(Path.Combine(cache, "layout.json"), Encoding.UTF8);
            var document = LocalPSDDocument.Parse(json);
            var board = document.artboard;
            layoutJson = json;
            loadedImportedLayout = false;
            analyzedSource = psdPath;
            analyzedWriteTime = writeTime;
            analyzedRasterText = rasterText;
            report = File.ReadAllText(Path.Combine(cache, "report.json"), Encoding.UTF8);
            psdPreviewPath = Path.Combine(cache, "preview.png");
            LoadPreview(ref psdPreview, psdPreviewPath);
            status = $"解析完成：{board.width} × {board.height}，可生成预设体。";
            SaveWorkflow();
        }

        private async Task ProbeOmniParser(CancellationToken token)
        {
            Uri server = LocalPSDInteractionPreview.ParseServerAddress(omniParserAddress);
            status = $"正在测试 OmniParser（{server.Authority}）…";
            try { await LocalPSDInteractionPreview.ProbeAsync(server, token); }
            catch (HttpRequestException exception)
            {
                throw new InvalidOperationException($"无法连接 OmniParser（{server.Authority}），请检查服务地址和服务状态。", exception);
            }
            catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"OmniParser 连接测试超时（{server.Authority}）。", exception);
            }
            status = $"OmniParser 可用（{server.Authority}）。";
        }

        private async Task AnalyzeInteractions(CancellationToken token)
        {
            if (!IsAnalysisCurrent()) throw new InvalidOperationException("PSD 或选项已变化，请重新解析。");
            if (psdPreview == null || !File.Exists(psdPreviewPath))
                throw new InvalidOperationException("找不到 PSD 合成预览，请重新解析。");

            Uri server = LocalPSDInteractionPreview.ParseServerAddress(omniParserAddress);
            status = $"正在请求 OmniParser（{server.Authority}）识别交互候选…";
            string previewPath = psdPreviewPath;
            string json = layoutJson;
            LocalPSDInteractionPreview result;
            try
            {
                result = await Task.Run(() => LocalPSDInteractionPreview.AnalyzeAsync(server, previewPath, json, token), token);
            }
            catch (HttpRequestException exception)
            {
                throw new InvalidOperationException($"无法连接 OmniParser（{server.Authority}），可在「其他选项」中测试连接。", exception);
            }
            catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"OmniParser 请求超时（{server.Authority}），可以重新识别。", exception);
            }
            token.ThrowIfCancellationRequested();
            if (!IsAnalysisCurrent()) throw new InvalidOperationException("PSD 或选项已变化，请重新解析。");
            interactionPreview = result;
            codexReview = null;
            selectedInteraction = 0;
            status = $"识别完成，找到 {result.Candidates.Length} 个交互候选。";
            SaveWorkflow();
        }

        private Task CopyCodexReviewRequest(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsAnalysisCurrent() || interactionPreview == null)
                throw new InvalidOperationException("请先解析 PSD 并识别交互候选。");
            if (codexReview == null || codexReview.OutputName != outputName)
                codexReview = LocalPSDCodexReview.Export(ProjectRoot, psdPreviewPath, interactionPreview, outputName);
            EditorGUIUtility.systemCopyBuffer = codexReview.Prompt;
            status = "已复制研判请求。粘贴到当前 Codex 对话，完成后点击「读取 Codex 建议」。";
            return Task.CompletedTask;
        }

        private Task ImportCodexReview(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsAnalysisCurrent()) throw new InvalidOperationException("请先解析 PSD 或加载已有导入目录。");
            codexReview = LocalPSDCodexReview.LoadLatest(ProjectRoot, outputName, layoutJson);
            interactionPreview ??= codexReview.RestorePreview(layoutJson);
            var result = codexReview.Import(interactionPreview);
            status = $"已按输出名称「{outputName}」载入 {result.Applied} 条 Codex 建议，跳过 {result.Preserved} 条已手动修改的候选。\n" + codexReview.DirectoryPath;
            SaveWorkflow();
            return Task.CompletedTask;
        }

        private async Task Generate(CancellationToken token)
        {
            if (!IsAnalysisCurrent()) throw new InvalidOperationException("PSD 或选项已变化，请重新解析。");
            if (string.IsNullOrWhiteSpace(outputName)) throw new InvalidOperationException("请输入输出名称。");
            if (SafeName(outputName) == "." || SafeName(outputName) == "..") throw new InvalidOperationException("输出名称不能是 . 或 ..。");
            if (settings == null) throw new InvalidOperationException("请在“其他选项”中选择本地工具配置。");
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw new InvalidOperationException("请选择输出目录。");
            string generationLayout = BuildGenerationLayout();
            string name = SafeName(outputName);
            string prefab = Path.Combine(outputDirectory, name, "Prefabs", name + ".prefab").Replace('\\', '/');
            if (File.Exists(Absolute(prefab)) && !EditorUtility.DisplayDialog("覆盖本地生成结果", prefab + " 已存在，是否重新生成？", "重新生成", "取消")) return;
            status = "正在生成 UI 预设体…";
            // Per-run output selection must not mutate the shared settings asset.
            var runSettings = Instantiate(settings);
            try
            {
                runSettings.outputRoot = outputDirectory;
                string generated = await LocalPSDPrefabGenerator.GenerateAsync(generationLayout, name, runSettings, token,
                    new GenerationProgress(token, cancellation));
                PrefabStageUtility.OpenPrefab(generated);
                organizerTarget = Path.GetDirectoryName(Path.GetDirectoryName(generated)).Replace('\\', '/');
                generatedExportDirectory = organizerTarget;
                currentPrefabGuid = AssetDatabase.AssetPathToGUID(generated);
                generatedPrefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { generatedExportDirectory + "/Prefabs" });
                organizationPlan = null;
                organizationLog = "";
                organizerExpanded = true;
            }
            finally { DestroyImmediate(runSettings); }
            status = "生成完成：" + prefab + "\n节点描述存档：" + LocalPSDNodeDescriptions.ArchivePath(prefab)
                + "\n节点映射：" + LocalPSDRoundTrip.MapPath(currentPrefabGuid);
            SaveWorkflow();
        }

        private string BuildGenerationLayout()
        {
            if (interactionPreview == null) return layoutJson;
            var document = LocalPSDDocument.Parse(layoutJson);
            var layers = document.artboard.Descendants().ToDictionary(layer => layer.id);
            foreach (var candidate in interactionPreview.Candidates)
                if (candidate.Type == InteractionComponent.None && candidate.ManuallyEdited
                    && layers.TryGetValue(interactionPreview.LayerIds[candidate.LayerIndex], out var resetLayer))
                    resetLayer.control = null;
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < interactionPreview.Candidates.Length; index++)
            {
                var candidate = interactionPreview.Candidates[index];
                if (candidate.Type == InteractionComponent.None) continue;
                string id = interactionPreview.LayerIds[candidate.LayerIndex];
                if (!layers.TryGetValue(id, out var layer))
                    throw new InvalidOperationException($"候选 {index + 1} 尚未匹配 PSD 图层。");
                if (!used.Add(id))
                    throw new InvalidOperationException($"多个候选对应图层「{layer.name}」，请改选对应图层或将重复项切回 None。");
                string name = candidate.BindingName.Trim();
                if (name.Length != 0 && !LocalPSDControlRules.IsBindingName(name))
                    throw new InvalidOperationException($"候选 {index + 1} 的绑定名应以下划线和小写英文字母开头，例如 _backButton。");
                layer.control = new LocalPSDMarkedControl { type = candidate.Type.ToString(), name = name, note = candidate.Note };
            }
            return LocalPSDJson.Serialize(document);
        }

        private string CurrentPrefabPath()
        {
            string tracked = AssetDatabase.GUIDToAssetPath(currentPrefabGuid);
            if (!string.IsNullOrEmpty(tracked) && AssetDatabase.LoadAssetAtPath<GameObject>(tracked) != null) return tracked;
            if (string.IsNullOrWhiteSpace(outputName) || string.IsNullOrWhiteSpace(outputDirectory))
                throw new InvalidOperationException("请先生成预设体，或填写已有导出结果的输出名称和目录。");
            string expected = LocalPSDAssetOrganizer.AssetFolder(Path.Combine(outputDirectory, SafeName(outputName), "Prefabs", SafeName(outputName) + ".prefab"));
            if (AssetDatabase.LoadAssetAtPath<GameObject>(expected) == null && !string.IsNullOrWhiteSpace(prefabDirectory))
                expected = LocalPSDAssetOrganizer.AssetFolder(prefabDirectory) + "/" + SafeName(outputName) + ".prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(expected) == null) throw new FileNotFoundException("未找到生成的预设体: " + expected);
            currentPrefabGuid = AssetDatabase.AssetPathToGUID(expected);
            return expected;
        }

        private string[] TrackedPrefabPaths()
        {
            var paths = new System.Collections.Generic.List<string>();
            foreach (string guid in generatedPrefabGuids ?? Array.Empty<string>())
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
            }
            return paths.ToArray();
        }

        private string[] OrganizerPrefabPaths()
        {
            if (string.IsNullOrWhiteSpace(generatedExportDirectory)) return null;
            if (!string.Equals(LocalPSDAssetOrganizer.AssetFolder(organizerTarget), generatedExportDirectory, StringComparison.OrdinalIgnoreCase)) return null;
            var paths = TrackedPrefabPaths();
            return paths.Length > 0 ? paths : null;
        }

        private Task MovePrefabsToProject(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string mainPath = CurrentPrefabPath();
            var sources = TrackedPrefabPaths();
            if (sources.Length == 0)
            {
                string folder = Path.GetDirectoryName(mainPath).Replace('\\', '/');
                if (folder.EndsWith("/Prefabs", StringComparison.OrdinalIgnoreCase))
                {
                    generatedExportDirectory = Path.GetDirectoryName(folder).Replace('\\', '/');
                    organizerTarget = generatedExportDirectory;
                    sources = Array.ConvertAll(AssetDatabase.FindAssets("t:Prefab", new[] { folder }), AssetDatabase.GUIDToAssetPath);
                }
                else sources = new[] { mainPath };
            }
            var moved = LocalPSDProjectActions.MovePrefabs(sources, prefabDirectory);
            generatedPrefabGuids = Array.ConvertAll(moved, p => AssetDatabase.AssetPathToGUID(p));
            organizationPlan = null;
            status = "已移动 " + moved.Length + " 个预设体至 " + LocalPSDAssetOrganizer.AssetFolder(prefabDirectory) + "，GUID 和嵌套引用保持不变。";
            SaveWorkflow();
            return Task.CompletedTask;
        }

        private Task GenerateBind(CancellationToken token, bool component)
        {
            token.ThrowIfCancellationRequested();
            LocalPSDProjectActions.GenerateBind(CurrentPrefabPath(), scriptDirectory, component);
            return Task.CompletedTask;
        }

        private async Task ExportPSD(CancellationToken token)
        {
            string prefab = CurrentPrefabPath();
            if (string.IsNullOrWhiteSpace(psdExportDirectory)) psdExportDirectory = "__Temp/PSD";
            string destination = Absolute(psdExportDirectory);
            string name = SafeName(Path.GetFileNameWithoutExtension(prefab));
            string output = Path.Combine(destination, name + ".psd");
            int suffix = 1;
            while (File.Exists(output) || File.Exists(Path.ChangeExtension(output, ".roundtrip.json")))
                output = Path.Combine(destination, name + "_" + suffix++ + ".psd");
            string cache = Path.Combine(ProjectRoot, "Library/LocalUIConverter/Reverse", Guid.NewGuid().ToString("N"));
            string sourceLayout = layoutJson;
            if (string.IsNullOrWhiteSpace(sourceLayout) && !string.IsNullOrWhiteSpace(generatedExportDirectory))
            {
                string saved = Absolute(generatedExportDirectory + "/layout.json");
                if (File.Exists(saved)) sourceLayout = File.ReadAllText(saved);
            }
            // Older generated prefabs do not yet have a persistent provenance map.
            // Re-read the original design so unique sprite/text/prefab matches can
            // bootstrap that map without regenerating and losing Unity edits.
            if (string.IsNullOrWhiteSpace(sourceLayout) && !string.IsNullOrWhiteSpace(psdPath) && File.Exists(Absolute(psdPath)))
            {
                status = "正在读取原始 PSD，为已有预设体建立来源映射…";
                string originalCache = Path.Combine(cache, "original");
                await RunPython("--source " + Quote(Absolute(psdPath)) + " --output " + Quote(originalCache), token);
                sourceLayout = File.ReadAllText(Path.Combine(originalCache, "layout.json"));
            }
            status = "正在恢复来源图层并导出当前 UI 外观…";
            string input = LocalPSDReverseExporter.Prepare(prefab, string.IsNullOrWhiteSpace(psdPath) ? "" : Absolute(psdPath), sourceLayout, settings, cache, token);
            await RunProcess(Quote(Absolute("Assets/Scripts/Editor/LocalUIConverter/psd_reverse.py"))
                + " --input " + Quote(input) + " --output " + Quote(output), token);
            token.ThrowIfCancellationRequested();
            if (!File.Exists(output)) throw new IOException("没有生成预期的 PSD 文件: " + output);
            status = "PSD 导出完成：" + output + "。请将同名 roundtrip.json 映射文件与 PSD 一起保留。";
            EditorUtility.RevealInFinder(output);
        }

        private sealed class GenerationProgress : IProgress<LocalPSDGenerationProgress>
        {
            private readonly CancellationToken token;
            private readonly CancellationTokenSource source;
            public GenerationProgress(CancellationToken token, CancellationTokenSource source) { this.token = token; this.source = source; }
            public void Report(LocalPSDGenerationProgress progress)
            {
                token.ThrowIfCancellationRequested();
                if (EditorUtility.DisplayCancelableProgressBar("本地 PSD 转 UI", progress.layerName ?? "准备生成",
                    progress.total > 0 ? (float)progress.current / progress.total : 0)) source.Cancel();
            }
        }

        private static string SafeName(string name)
        {
            foreach (char character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
            return name.Trim().Replace(" ", "_");
        }

        private Task<string> RunPython(string arguments, CancellationToken token) => RunProcess(Quote(Absolute(Script)) + " " + arguments, token);

        private async Task<string> RunProcess(string arguments, CancellationToken token)
        {
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = python, Arguments = arguments, WorkingDirectory = ProjectRoot,
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                process.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                process.Start();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                try
                {
                    while (!process.HasExited)
                    {
                        token.ThrowIfCancellationRequested();
                        if (EditorUtility.DisplayCancelableProgressBar("本地 PSD 转 UI", status, 0.25f)) cancellation.Cancel();
                        await Task.Delay(100, token);
                    }
                    string result = await stdout;
                    string errors = await stderr;
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0) throw new InvalidOperationException("PSD 解析环境或导出失败。请在“其他选项 → Python 环境”中检查环境或补齐依赖。\n" + errors + "\n" + result);
                    if (!string.IsNullOrWhiteSpace(errors)) Debug.LogWarning(errors);
                    return result;
                }
                finally { if (!process.HasExited) process.Kill(); }
            }
        }

        // Windows command-line quoting, including trailing backslashes. No shell execution.
        private static string Quote(string value)
        {
            return "\"" + System.Text.RegularExpressions.Regex.Replace(value, "(\\\\*)\"", "$1$1\\\"")
                .TrimEnd('\\') + new string('\\', (value.Length - value.TrimEnd('\\').Length) * 2) + "\"";
        }
    }
}

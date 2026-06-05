using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

/// <summary>
/// PSD → Unity UI 一体化工具。
/// 菜单:
///   Tools/UI/1. 导出 PSD → Sprites   调 Python 切图
///   Tools/UI/2. 构建 Canvas            读 JSON 建层级（EditorWindow）
/// </summary>
public class PSDLayoutImporter : EditorWindow
{
    // ── 路径常量 ─────────────────────────────────────────────────

    /// 工程根目录下的 PSD 投放文件夹（与 Assets/ 平级）
    const string PsdDropFolder = "ps文件放到这里";

    /// Python 脚本相对 Assets/ 的路径
    const string PyScriptRelative = "Editor/AutoUI/psd_exporter.py";

    /// 生成的 PNG 和 JSON 写入 Assets/UI/
    const string UIAssetsRelative = "UI";

    // ── EditorPrefs key ──────────────────────────────────────────

    const string PrefPython = "PSUITool.PythonExe";

    static string PythonExe
    {
        get => EditorPrefs.GetString(PrefPython, "python");
        set => EditorPrefs.SetString(PrefPython, value);
    }

    // ── 菜单：导出 PSD ───────────────────────────────────────────

    [MenuItem("Tools/UI/1. 导出 PSD → Sprites")]
    static void MenuExportPSD()
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string psdFolder   = Path.Combine(projectRoot, PsdDropFolder);
        string pyScript    = Path.Combine(Application.dataPath, PyScriptRelative);
        string uiOut       = Path.Combine(Application.dataPath, UIAssetsRelative);

        if (!Directory.Exists(psdFolder))
        {
            Directory.CreateDirectory(psdFolder);
            EditorUtility.DisplayDialog("文件夹已创建",
                $"请将 PSD 文件放入：\n{psdFolder}\n\n然后再次执行此菜单。", "OK");
            return;
        }

        string[] psds = Directory.GetFiles(psdFolder, "*.psd", SearchOption.TopDirectoryOnly);
        if (psds.Length == 0)
        {
            EditorUtility.DisplayDialog("没有 PSD 文件",
                $"请将 PSD 文件放入：\n{psdFolder}", "OK");
            return;
        }

        var res = GetGameViewResolution();
        var log = new StringBuilder();
        int ok = 0, fail = 0;

        foreach (string psd in psds)
        {
            log.AppendLine($"▶ {Path.GetFileName(psd)}");
            bool success = RunPython(pyScript, psd, uiOut, res.x, res.y, out string output);
            log.AppendLine(output);
            if (success) ok++; else fail++;
        }

        AssetDatabase.Refresh();

        string summary = $"完成：成功 {ok} / 失败 {fail}\n\n{log}";
        if (fail > 0)
            EditorUtility.DisplayDialog("导出完成（有错误）", summary, "OK");
        else
            EditorUtility.DisplayDialog("导出完成", summary, "OK");

        Debug.Log("[PSDExport]\n" + summary);
    }

    // ── 菜单：打开构建窗口 ───────────────────────────────────────

    [MenuItem("Tools/UI/2. 构建 Canvas")]
    static void MenuBuildCanvas() => GetWindow<PSDLayoutImporter>("构建 Canvas");

    // ── EditorWindow UI ──────────────────────────────────────────

    string _jsonPath    = "";
    string _spritesRoot = "Assets/UI/Sprites";
    float  _matchWH     = 0.5f;

    void OnGUI()
    {
        // Python 路径设置
        GUILayout.Label("Python 可执行程序", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            string newExe = EditorGUILayout.TextField(PythonExe);
            if (newExe != PythonExe) PythonExe = newExe;
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string p = EditorUtility.OpenFilePanel("选择 Python 可执行程序", "", "exe");
                if (!string.IsNullOrEmpty(p)) PythonExe = p;
            }
        }
        EditorGUILayout.HelpBox("默认 \"python\"，若不在 PATH 中请填写完整路径，如 C:/Python311/python.exe", MessageType.None);

        GUILayout.Space(12);
        GUILayout.Label("选择 layout.json", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            _jsonPath = EditorGUILayout.TextField(_jsonPath);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string p = EditorUtility.OpenFilePanel("选择 layout.json", "Assets/UI/Layouts", "json");
                if (!string.IsNullOrEmpty(p)) _jsonPath = p;
            }
        }

        GUILayout.Space(4);
        GUILayout.Label("Sprites 根目录（相对 Assets）", EditorStyles.boldLabel);
        _spritesRoot = EditorGUILayout.TextField(_spritesRoot);

        GUILayout.Space(4);
        GUILayout.Label("CanvasScaler Match（0=宽适配  1=高适配  0.5=折中）", EditorStyles.boldLabel);
        _matchWH = EditorGUILayout.Slider(_matchWH, 0f, 1f);

        GUILayout.Space(12);
        GUI.enabled = !string.IsNullOrEmpty(_jsonPath) && File.Exists(_jsonPath);
        if (GUILayout.Button("Build Canvas", GUILayout.Height(32)))
            Build();
        GUI.enabled = true;
    }

    // ── 构建 Canvas ──────────────────────────────────────────────

    [Serializable] class Layout    { public string psd_name; public int design_width, design_height; public LayerData[] layers; }
    [Serializable] class LayerData { public string name, type, file, text; public float x, y, width, height; public int parent_index; }

    void Build()
    {
        var layout = JsonUtility.FromJson<Layout>(File.ReadAllText(_jsonPath));
        if (layout?.layers == null || layout.layers.Length == 0)
        {
            EditorUtility.DisplayDialog("错误", "JSON 解析失败或 layers 为空。", "OK");
            return;
        }

        string spriteDir = $"{_spritesRoot.TrimEnd('/')}/{layout.psd_name}";

        var canvasGO = new GameObject(string.IsNullOrEmpty(layout.psd_name) ? "UICanvas" : layout.psd_name);
        var canvas   = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(layout.design_width, layout.design_height);
        scaler.matchWidthOrHeight  = _matchWH;
        canvasGO.AddComponent<GraphicRaycaster>();

        var gos = new GameObject[layout.layers.Length];
        for (int i = 0; i < layout.layers.Length; i++)
        {
            var node = layout.layers[i];
            int pi   = node.parent_index;

            gos[i] = CreateNode(node, spriteDir);

            Transform parentTr = (pi >= 0 && pi < i) ? gos[pi].transform : canvasGO.transform;
            gos[i].transform.SetParent(parentTr, false);

            float parentCX = pi >= 0 ? layout.layers[pi].x + layout.layers[pi].width  * 0.5f : layout.design_width  * 0.5f;
            float parentCY = pi >= 0 ? layout.layers[pi].y + layout.layers[pi].height * 0.5f : layout.design_height * 0.5f;
            float childCX  = node.x + node.width  * 0.5f;
            float childCY  = node.y + node.height * 0.5f;

            gos[i].GetComponent<RectTransform>().anchoredPosition =
                new Vector2(childCX - parentCX, -(childCY - parentCY));
        }

        Undo.RegisterCreatedObjectUndo(canvasGO, "Build UI From Layout");
        Selection.activeGameObject = canvasGO;
        Debug.Log($"[PSDLayoutImporter] '{canvasGO.name}' 构建完成，共 {layout.layers.Length} 个节点。");
    }

    static GameObject CreateNode(LayerData node, string spriteDir)
    {
        var go = new GameObject(node.name);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot     = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(node.width, node.height);

        switch (node.type)
        {
            case "image":
                var img = go.AddComponent<Image>();
                img.raycastTarget = false;
                if (!string.IsNullOrEmpty(node.file))
                {
                    var sprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{spriteDir}/{node.file}");
                    if (sprite != null) img.sprite = sprite;
                    else Debug.LogWarning($"[PSDLayoutImporter] 找不到 Sprite: {spriteDir}/{node.file}");
                }
                break;

            case "text":
                var tmp = go.AddComponent<TextMeshProUGUI>();
                tmp.text          = string.IsNullOrEmpty(node.text) ? node.name : node.text;
                tmp.fontSize      = 24;
                tmp.color         = Color.white;
                tmp.raycastTarget = false;
                break;
        }
        return go;
    }

    // ── 工具方法 ─────────────────────────────────────────────────

    /// <summary>通过反射获取 Game 窗口当前分辨率。</summary>
    static Vector2Int GetGameViewResolution()
    {
        try
        {
            var gameViewType = Assembly.Load("UnityEditor").GetType("UnityEditor.GameView");
            var method = gameViewType?.GetMethod("GetSizeOfMainGameView",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (method != null)
            {
                var v = (Vector2)method.Invoke(null, null);
                return new Vector2Int((int)v.x, (int)v.y);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PSDExport] 无法读取 Game 窗口分辨率，使用 1920x1080。原因：{e.Message}");
        }
        return new Vector2Int(1920, 1080);
    }

    /// <summary>调用 Python 脚本，返回是否成功，输出写入 output。</summary>
    static bool RunPython(string script, string psd, string outDir, int w, int h, out string output)
    {
        var sb = new StringBuilder();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = PythonExe,
                Arguments              = $"\"{script}\" \"{psd}\" \"{outDir}\" --screen {w}x{h}",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };

            using var proc = Process.Start(psi);
            sb.Append(proc.StandardOutput.ReadToEnd());
            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (!string.IsNullOrWhiteSpace(err)) sb.AppendLine($"[stderr] {err}");

            output = sb.ToString();
            return proc.ExitCode == 0;
        }
        catch (Exception e)
        {
            output = $"启动 Python 失败：{e.Message}\n请在窗口 Tools/UI/2.构建Canvas 中检查 Python 可执行程序路径。";
            return false;
        }
    }
}

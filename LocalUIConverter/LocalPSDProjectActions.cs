using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Starlight.Game.Editor.LocalUIConverter
{
    /// <summary>Project delivery and optional integration with the project's binding generator.</summary>
    [InitializeOnLoad]
    public static class LocalPSDProjectActions
    {
        private const string JobKey = "LocalPSDUI.PendingBind";
        private const string StatusKey = "LocalPSDUI.BindStatus";
        private static bool resuming;
        public static event Action<string> StatusChanged;
        public static string LastBindStatus => SessionState.GetString(StatusKey, "");
        public static bool HasPendingBind => !string.IsNullOrEmpty(SessionState.GetString(JobKey, ""));

        [Serializable] private sealed class BindJob
        {
            public string prefabGuid, scriptPath, className, baseName;
            public bool awaitingCompilation, compilationFinished, compilationFailed;
            public double compilationCompletedAt;
            public double queuedAt;
        }

        static LocalPSDProjectActions()
        {
            EditorApplication.update += ResumePendingBind;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        public static void FillFunctionDirectories(string functionId, ref string prefabDirectory, ref string scriptDirectory)
        {
            if (string.IsNullOrWhiteSpace(functionId)) return;
            string id = functionId.Trim();
            if (id == "." || id == ".." || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("FunctionID 必须是一个有效的目录名，不能包含路径分隔符。");
            if (string.IsNullOrWhiteSpace(prefabDirectory)) prefabDirectory = "Assets/Bundles/UI/Prefab/" + id;
            if (string.IsNullOrWhiteSpace(scriptDirectory)) scriptDirectory = "Assets/Scripts/Game/Module/Function/" + id;
        }

        public static void FillFunctionDirectories(string functionId, ref string prefabDirectory, ref string scriptDirectory, ref string artDirectory)
        {
            FillFunctionDirectories(functionId, ref prefabDirectory, ref scriptDirectory);
            if (!string.IsNullOrWhiteSpace(functionId) && string.IsNullOrWhiteSpace(artDirectory))
                artDirectory = "Assets/Bundles/UI/UIImages/" + functionId.Trim();
        }

        public static string[] MovePrefabs(string[] prefabPaths, string destination)
        {
            if (prefabPaths == null || prefabPaths.Length == 0) throw new InvalidOperationException("没有找到生成的预设体，请先生成。");
            destination = LocalPSDAssetOrganizer.AssetFolder(destination);
            var sources = prefabPaths.Select(LocalPSDAssetOrganizer.AssetFolder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var moves = new List<KeyValuePair<string, string>>();
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string source in sources)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(source) == null ||
                    !source.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogError("预设体不存在: " + source);
                    continue;
                }
                
                string target = destination + "/" + Path.GetFileName(source);
                if (!destinations.Add(target)) throw new InvalidOperationException("多个预设体具有相同文件名: " + target);
                if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(target) || !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(target)))
                    throw new IOException("目标已存在同名预设体，未执行移动: " + target);
                moves.Add(new KeyValuePair<string, string>(source, target));
            }
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            string reopenGuid = null;
            if (stage != null && sources.Contains(stage.assetPath, StringComparer.OrdinalIgnoreCase))
            {
                if (stage.scene.isDirty) throw new InvalidOperationException("请先保存当前预设体的修改，再移动。");
                reopenGuid = AssetDatabase.AssetPathToGUID(stage.assetPath);
                StageUtility.GoToMainStage();
            }
            var completed = new List<KeyValuePair<string, string>>();
            try
            {
                if (moves.Count > 0) LocalPSDPrefabGenerator.RequireAssetFolder(destination);
                foreach (var move in moves)
                {
                    string validation = AssetDatabase.ValidateMoveAsset(move.Key, move.Value);
                    if (!string.IsNullOrEmpty(validation)) throw new IOException(validation);
                }
                foreach (var move in moves)
                {
                    string error = AssetDatabase.MoveAsset(move.Key, move.Value);
                    if (!string.IsNullOrEmpty(error)) throw new IOException(error);
                    completed.Add(move);
                }
                AssetDatabase.SaveAssets();
                return sources.Select(p => destination + "/" + Path.GetFileName(p)).ToArray();
            }
            catch (Exception exception)
            {
                var rollbackErrors = new List<string>();
                for (int i = completed.Count - 1; i >= 0; i--)
                {
                    string error = AssetDatabase.MoveAsset(completed[i].Value, completed[i].Key);
                    if (!string.IsNullOrEmpty(error)) rollbackErrors.Add(error);
                }
                if (rollbackErrors.Count > 0) throw new IOException("移动失败，部分文件未能移回原位:\n" + string.Join("\n", rollbackErrors), exception);
                throw;
            }
            finally
            {
                if (!string.IsNullOrEmpty(reopenGuid))
                {
                    string path = AssetDatabase.GUIDToAssetPath(reopenGuid);
                    if (!string.IsNullOrEmpty(path)) PrefabStageUtility.OpenPrefab(path);
                }
            }
        }

        public static void GenerateBind(string prefabPath, string scriptDirectory, bool component)
        {
            if (HasPendingBind) throw new InvalidOperationException("已有 Bind 生成任务正在等待编译，请先等待它完成。");
            prefabPath = LocalPSDAssetOrganizer.AssetFolder(prefabPath);
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null) throw new FileNotFoundException("找不到预设体: " + prefabPath);
            string className = Path.GetFileNameWithoutExtension(prefabPath);
            ValidateClassName(className);
            string baseName = component ? "UIComponent" : "UIFunction";
            Type baseType = FindType("Starlight.Game." + baseName);
            if (baseType == null || !typeof(Component).IsAssignableFrom(baseType)) throw new InvalidOperationException("当前项目未提供 Starlight.Game." + baseName);
            GeneratorMethod(); // Check the optional integration before writing any source.
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.scene.isDirty && stage.assetPath != prefabPath)
                throw new InvalidOperationException("请先保存当前打开的其他预设体，再生成 Bind。");
            string folder = LocalPSDAssetOrganizer.AssetFolder(scriptDirectory) + "/UI";
            if (folder.Split('/').Any(p => p.Equals("Editor", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("UI 脚本目录不能放在 Editor 目录内。");
            string scriptPath = folder + "/" + className + ".cs";
            Type existingType = FindType("Starlight.Game." + className);
            if (existingType != null && !baseType.IsAssignableFrom(existingType)) throw new InvalidOperationException("已有类 " + className + " 不是 " + baseName + "，不会覆盖现有脚本。");
            bool exists = File.Exists(scriptPath);
            if (!exists && existingType != null) throw new InvalidOperationException("项目其他目录已有同名类: " + existingType.FullName);
            LocalPSDNodeDescriptions.PrepareForBind(prefabPath);
            var job = new BindJob { prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath), scriptPath = scriptPath,
                className = className, baseName = baseName, awaitingCompilation = !exists || existingType == null,
                queuedAt = EditorApplication.timeSinceStartup };
            SessionState.SetString(JobKey, JsonUtility.ToJson(job));
            try
            {
                if (!exists)
                {
                    LocalPSDPrefabGenerator.RequireAssetFolder(folder);
                    File.WriteAllText(scriptPath, "namespace Starlight.Game\n{\n    public partial class " + className + " : " + baseName + "\n    {\n        \n    }\n}\n", new UTF8Encoding(false));
                }
                AssetDatabase.ImportAsset(scriptPath);
                if (job.awaitingCompilation)
                {
                    SetStatus("已准备 UI 脚本，等待 Unity 编译后自动生成 Bind：" + scriptPath);
                    CompilationPipeline.RequestScriptCompilation();
                }
                else SetStatus("正在为预设体生成 Bind：" + className);
            }
            catch
            {
                SessionState.EraseString(JobKey);
                throw;
            }
        }

        private static void ValidateClassName(string name)
        {
            const string keywords = " abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while ";
            if (!Regex.IsMatch(name, @"^[_\p{L}\p{Nl}][_\p{L}\p{Nl}\p{Nd}\p{Mn}\p{Mc}\p{Pc}\p{Cf}]*$") || keywords.Contains(" " + name + " "))
                throw new ArgumentException("预设体名称不能直接作为 C# 类名，请先修改输出名称: " + name);
        }

        internal static Type FindType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(name, false);
                if (type != null) return type;
            }
            return null;
        }

        private static MethodInfo GeneratorMethod()
        {
            Type type = FindType("Starlight.Core.Editor.ScriptGenerator");
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var method = type?.GetMethod("Generate", flags,
                null, new[] { typeof(bool), typeof(bool), typeof(bool) }, null);
            if (method == null) throw new InvalidOperationException("未找到项目 ScriptGenerator.Generate(bool, bool, bool)。");
            return method;
        }

        private static Dictionary<string, Component> MarkedBindComponents(string prefabPath, GameObject stageRoot, out HashSet<GameObject> descriptionOnly)
        {
            var controls = new Dictionary<string, Component>(StringComparer.Ordinal);
            var descriptions = new HashSet<GameObject>();
            descriptionOnly = descriptions;
            var marked = LocalPSDRoundTrip.Load(prefabPath).nodes.Where(record => record.control != null)
                .ToDictionary(record => record.objectId, record => record.control, StringComparer.Ordinal);
            if (marked.Count == 0) return controls;
            var duplicateNames = new HashSet<string>(stageRoot.GetComponentsInChildren<Transform>(true)
                .Where(node => node != stageRoot.transform && LocalPSDControlRules.IsBindingName(node.name))
                .GroupBy(node => node.name, StringComparer.Ordinal).Where(group => group.Skip(1).Any())
                .Select(group => group.Key), StringComparer.Ordinal);

            void Collect(Transform asset, Transform instance)
            {
                if (marked.TryGetValue(LocalPSDRoundTrip.ObjectId(asset.gameObject), out var mark))
                {
                    if (mark.type == "Description") descriptions.Add(instance.gameObject);
                    // Leave ambiguous names to the shared generator's normal traversal.
                    else if (!LocalPSDControlRules.IsDescriptionOnly(mark.type)
                        && LocalPSDControlRules.IsBindingName(instance.name) && !duplicateNames.Contains(instance.name))
                    {
                        var component = instance.GetComponent(LocalPSDControlRules.ComponentType(mark.type));
                        if (component == null)
                            throw new InvalidOperationException($"节点「{instance.name}」缺少标记的 {mark.type} 组件。");
                        controls.Add(instance.name, component);
                    }
                }
                for (int index = 0; index < asset.childCount; index++)
                    Collect(asset.GetChild(index), instance.GetChild(index));
            }

            var assetRoot = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Collect(assetRoot.transform, stageRoot.transform);
            return controls;
        }

        private static void GenerateScopedBind(string prefabPath, GameObject root, Component binding)
        {
            var generator = GeneratorMethod();
            var controls = MarkedBindComponents(prefabPath, root, out var descriptionOnly);
            var bindingType = binding.GetType();
            var variableType = bindingType.Assembly.GetType("Starlight.Core.Variable", true);
            var variableKind = bindingType.Assembly.GetType("Starlight.Core.VariableType", true);
            var componentKind = Enum.Parse(variableKind, "Component");
            var setValue = variableType.GetMethod("SetValue", new[] { typeof(object) });
            var setVariable = bindingType.GetMethod("Set", new[] { variableType });
            bindingType.GetMethod("Clear", Type.EmptyTypes).Invoke(binding, null);
            // The shared generator preserves existing entries with the same name.
            foreach (var pair in controls)
            {
                var variable = Activator.CreateInstance(variableType, pair.Key, componentKind);
                setValue.Invoke(variable, new object[] { pair.Value });
                setVariable.Invoke(binding, new[] { variable });
            }

            // Skip description-only and invalid names in the shared scan, then
            // restore the hierarchy names.
            var excluded = root.GetComponentsInChildren<Transform>(true)
                .Where(child => child != root.transform && (descriptionOnly.Contains(child.gameObject)
                    || (child.name.StartsWith("_", StringComparison.Ordinal) && !LocalPSDControlRules.IsBindingName(child.name))))
                .Select(child => (Node: child.gameObject, Name: child.name)).ToArray();
            try
            {
                foreach (var item in excluded) item.Node.name = "Unbound" + item.Name;
                generator.Invoke(null, new object[] { true, true, false });
            }
            finally
            {
                foreach (var item in excluded) item.Node.name = item.Name;
            }
        }

        private static void OnAssemblyCompiled(string assembly, CompilerMessage[] messages)
        {
            if (!HasPendingBind || !messages.Any(m => m.type == CompilerMessageType.Error)) return;
            var job = JsonUtility.FromJson<BindJob>(SessionState.GetString(JobKey, ""));
            job.compilationFailed = true;
            SessionState.SetString(JobKey, JsonUtility.ToJson(job));
        }

        private static void OnCompilationFinished(object context)
        {
            if (!HasPendingBind) return;
            var job = JsonUtility.FromJson<BindJob>(SessionState.GetString(JobKey, ""));
            job.compilationFinished = true;
            job.compilationCompletedAt = EditorApplication.timeSinceStartup;
            SessionState.SetString(JobKey, JsonUtility.ToJson(job));
        }

        private static void ResumePendingBind()
        {
            if (resuming || !HasPendingBind || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            var job = JsonUtility.FromJson<BindJob>(SessionState.GetString(JobKey, ""));
            if (job.awaitingCompilation && !job.compilationFinished) return;
            // Let the import/assembly-reload cycle settle before a second code
            // generation pass starts from an Editor update callback.
            double readyAt = job.awaitingCompilation ? job.compilationCompletedAt : job.queuedAt;
            if (EditorApplication.timeSinceStartup - readyAt < 1) return;
            // Compilation-finished can precede the assembly reload. Do not consume
            // the queued job while MonoScript still points to the previous domain.
            if (job.awaitingCompilation && !job.compilationFailed
                && AssetDatabase.LoadAssetAtPath<MonoScript>(job.scriptPath)?.GetClass() == null
                && EditorApplication.timeSinceStartup - job.compilationCompletedAt < 10) return;
            resuming = true;
            try
            {
                if (job.compilationFailed) throw new InvalidOperationException("Unity 编译存在错误。请修复 Console 中的编译错误后，再点击生成 Bind。");
                string prefabPath = AssetDatabase.GUIDToAssetPath(job.prefabGuid);
                if (string.IsNullOrEmpty(prefabPath)) throw new FileNotFoundException("等待编译期间，预设体已被删除。");
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(job.scriptPath);
                Type uiType = script != null ? script.GetClass() : null;
                Type expectedBase = FindType("Starlight.Game." + job.baseName);
                if (uiType == null || expectedBase == null || !expectedBase.IsAssignableFrom(uiType)
                    || uiType.FullName != "Starlight.Game." + job.className)
                    throw new InvalidOperationException("脚本未编译成预期的 UI 类型，请检查命名空间、类名和继承关系: " + job.scriptPath);
                var currentStage = PrefabStageUtility.GetCurrentPrefabStage();
                if (currentStage != null && currentStage.scene.isDirty) throw new InvalidOperationException("当前预设体有未保存修改，请保存后重新生成 Bind。");
                var stage = PrefabStageUtility.OpenPrefab(prefabPath);
                var root = stage.prefabContentsRoot;
                Type uiBase = FindType("Starlight.Game.UIBase");
                var existing = uiBase != null ? root.GetComponent(uiBase) : null;
                if (existing != null && existing.GetType() != uiType) throw new InvalidOperationException("预设体根节点已有其他 UI 类: " + existing.GetType().Name);
                if (root.GetComponent(uiType) == null) Undo.AddComponent(root, uiType);
                Type bindingType = FindType("Starlight.Core.VariableBinding");
                if (bindingType == null) throw new InvalidOperationException("未找到项目 VariableBinding 类型。");
                var binding = root.GetComponent(bindingType) ?? Undo.AddComponent(root, bindingType);
                var variables = bindingType.GetField("_variables", BindingFlags.Instance | BindingFlags.NonPublic);
                if (variables != null && variables.GetValue(binding) == null) variables.SetValue(binding, Activator.CreateInstance(variables.FieldType));
                Selection.activeGameObject = root;
                string generatedClassName = char.ToUpperInvariant(job.className[0]) + job.className.Substring(1);
                string bindPath = Path.GetDirectoryName(job.scriptPath).Replace('\\', '/') + "/Bind/" + generatedClassName + "Bind.cs";
                if (generatedClassName != job.className && File.Exists(bindPath))
                {
                    string existingCode = File.ReadAllText(bindPath);
                    if (!Regex.IsMatch(existingCode, @"\bpartial\s+class\s+" + Regex.Escape(job.className) + @"\b"))
                        throw new InvalidOperationException("生成器的大写命名会覆盖其他类的 Bind 文件: " + bindPath);
                }
                DateTime previous = File.Exists(bindPath) ? File.GetLastWriteTimeUtc(bindPath) : DateTime.MinValue;
                GenerateScopedBind(prefabPath, root, binding);
                if (!File.Exists(bindPath) || File.GetLastWriteTimeUtc(bindPath) == previous)
                    throw new IOException("ScriptGenerator 未生成或更新 Bind 文件，请检查 Console: " + bindPath);
                if (generatedClassName != job.className)
                {
                    string code = File.ReadAllText(bindPath);
                    code = Regex.Replace(code, @"(\bpartial\s+class\s+)" + Regex.Escape(generatedClassName) + @"\b", "$1" + job.className);
                    File.WriteAllText(bindPath, code, new UTF8Encoding(false));
                }
                LocalPSDNodeDescriptions.Strip(root);
                // Saving through the stage also clears its dirty flag. Saving only
                // with SaveAsPrefabAsset leaves the open stage marked as modified.
                var save = typeof(PrefabStage).GetMethod("SavePrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (save == null) throw new InvalidOperationException("当前 Unity 版本未提供 PrefabStage.SavePrefab。");
                object saved = save.Invoke(stage, null);
                if (saved is bool success && !success) throw new IOException("保存 UI 绑定组件失败: " + prefabPath);
                AssetDatabase.SaveAssets();
                LocalPSDNodeDescriptions.CompleteBind(prefabPath);
                if (generatedClassName != job.className) AssetDatabase.ImportAsset(bindPath);
                string bindStatus = "Bind 生成完成：" + bindPath;
                if (File.Exists(LocalPSDNodeDescriptions.ArchivePath(prefabPath)))
                    bindStatus += "\n节点描述存档：" + LocalPSDNodeDescriptions.ArchivePath(prefabPath);
                try
                {
                    string specPath = LocalPSDUISpec.Export(prefabPath, bindPath, bindingType);
                    SetStatus(bindStatus + "\nUI Spec：" + specPath);
                }
                catch (Exception exception)
                {
                    SetStatus(bindStatus + "\nUI Spec 导出失败：" + exception.Message);
                    Debug.LogException(exception);
                }
            }
            catch (Exception exception)
            {
                if (exception is TargetInvocationException invocation && invocation.InnerException != null) exception = invocation.InnerException;
                SetStatus("Bind 生成失败：" + exception.Message);
                Debug.LogException(exception);
            }
            finally
            {
                SessionState.EraseString(JobKey);
                resuming = false;
            }
        }

        private static void SetStatus(string message)
        {
            SessionState.SetString(StatusKey, message);
            StatusChanged?.Invoke(message);
        }
    }
}

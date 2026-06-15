using UnityEngine;
using UnityEditor;

namespace AIBehaviourTree
{
    /// <summary>
    /// 【行为树调试窗口 — 实时可视化查看AI状态】
    ///
    /// 使用方法：
    /// 菜单栏 → AI行为树 → 行为树调试器  (或快捷键 Ctrl+Shift+B)
    ///
    /// 兼容 BTGuardAI（v1）和 BTGuardAI_v2（v2），
    /// 通过 IBTGuard 接口统一处理。
    /// </summary>
    public class BTEditorWindow : EditorWindow
    {
        // 当前选中的 Guard AI（通过接口引用，兼容 v1 和 v2）
        private IBTGuard _selectedGuard;

        // 树的滚动位置
        private Vector2 _treeScrollPos;

        // 是否自动查找场景中的 Guard
        private bool _autoFindGuard = true;

        // 是否暂停显示（冻结当前帧的数据）
        private bool _freezeDisplay = false;

        // 折叠状态
        private bool _showBlackboard = true;
        private bool _showTree = true;
        private bool _showHelp = false;

        // 树节点是否展开（用节点名做key）
        private System.Collections.Generic.Dictionary<string, bool> _expandedNodes
            = new System.Collections.Generic.Dictionary<string, bool>();

        // 节点深度缩进像素
        private const float INDENT_PX = 24f;

        // ========== 菜单入口 ==========

        [MenuItem("AI行为树/行为树调试器 %#b")]
        public static void OpenWindow()
        {
            var window = GetWindow<BTEditorWindow>("行为树调试器");
            window.minSize = new Vector2(400, 500);
            window.Show();
        }

        // ========== GUI 绘制 ==========

        void OnGUI()
        {
            DrawToolbar();
            DrawHeader();

            if (_selectedGuard == null)
            {
                DrawNoSelectionHint();
                return;
            }

            if (!_freezeDisplay)
            {
                Repaint();
            }

            _treeScrollPos = EditorGUILayout.BeginScrollView(_treeScrollPos);

            DrawBlackboardSection();
            EditorGUILayout.Space(8);
            DrawTreeSection();
            DrawHelpSection();

            EditorGUILayout.EndScrollView();
        }

        // ========== 顶部工具栏 ==========

        void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            bool newAutoFind = EditorGUILayout.ToggleLeft("自动查找", _autoFindGuard, EditorStyles.toolbarButton, GUILayout.Width(60));
            if (newAutoFind != _autoFindGuard)
            {
                _autoFindGuard = newAutoFind;
                if (_autoFindGuard) AutoFindGuard();
            }

            // 手动选择（兼容 v1 和 v2）
            var newSelection = (IBTGuard)EditorGUILayout.ObjectField(
                _selectedGuard as Object, typeof(MonoBehaviour), true, GUILayout.Width(160));
            if (newSelection != _selectedGuard)
            {
                // 验证选中的对象是否实现了 IBTGuard
                if (newSelection is IBTGuard)
                {
                    _selectedGuard = newSelection;
                }
                else if (newSelection != null)
                {
                    _selectedGuard = null;
                }
                else
                {
                    _selectedGuard = null;
                }
            }

            GUILayout.FlexibleSpace();

            bool newFreeze = GUILayout.Toggle(_freezeDisplay, "冻结", EditorStyles.toolbarButton, GUILayout.Width(50));
            if (newFreeze != _freezeDisplay)
            {
                _freezeDisplay = newFreeze;
            }

            if (GUILayout.Button("刷新", EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                AutoFindGuard();
            }

            _showHelp = GUILayout.Toggle(_showHelp, "?", EditorStyles.toolbarButton, GUILayout.Width(24));

            EditorGUILayout.EndHorizontal();
        }

        // ========== 标题区域 ==========

        void DrawHeader()
        {
            EditorGUILayout.Space(4);

            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter
            };

            // 显示当前使用的版本
            string version = _selectedGuard is BTGuardAI_v2 ? "v2" : "v1";
            EditorGUILayout.LabelField($"🌳 行为树实时调试器 [{version}]", titleStyle);

            if (_freezeDisplay)
            {
                var freezeStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.yellow }
                };
                EditorGUILayout.LabelField("⏸ 已冻结 — 点击'解除冻结'恢复实时更新", freezeStyle);
            }

            EditorGUILayout.Space(4);
            DrawSeparator();
        }

        // ========== 黑板数据显示 ==========

        void DrawBlackboardSection()
        {
            if (_selectedGuard?.BehaviourTree == null) return;

            var board = _selectedGuard.BehaviourTree.Board;

            _showBlackboard = EditorGUILayout.Foldout(_showBlackboard, "📋 黑板数据 (Blackboard)", true, EditorStyles.foldoutHeader);
            if (_showBlackboard)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.LabelField("感知范围", $"{board.DetectRange:F1} m");
                EditorGUILayout.LabelField("攻击范围", $"{board.AttackRange:F1} m");
                EditorGUILayout.LabelField("移动速度", $"{board.MoveSpeed:F1}");
                EditorGUILayout.LabelField("旋转速度", $"{board.TurnSpeed:F0} deg/s");

                // v2 额外数据
                if (_selectedGuard is BTGuardAI_v2)
                {
                    EditorGUILayout.Space(4);
                    var guiStyle = new GUIStyle(EditorStyles.label);
                    guiStyle.normal.textColor = board.ChaseStamina > 30f ? Color.green :
                        (board.ChaseStamina > 10f ? Color.yellow : Color.red);
                    EditorGUILayout.LabelField("追击体力", $"{board.ChaseStamina:F0} / {board.MaxChaseStamina:F0}", guiStyle);

                    if (board.HasSeenPlayer)
                    {
                        EditorGUILayout.LabelField("最后目击位置", $"{board.LastKnownPlayerPosition}");
                    }
                    else
                    {
                        EditorGUILayout.LabelField("最后目击位置", "无");
                    }
                }

                // 玩家距离
                if (board.PlayerTransform != null)
                {
                    EditorGUILayout.Space(4);
                    Vector3 playerPos = board.PlayerTransform.position;
                    Vector3 npcPos = _selectedGuard.transform.position;
                    float dist = Vector3.Distance(npcPos, playerPos);

                    EditorGUILayout.LabelField("玩家位置", $"{playerPos}");
                    EditorGUILayout.LabelField("NPC与玩家距离", $"{dist:F1} m",
                        new GUIStyle(EditorStyles.label)
                        {
                            normal = { textColor = dist <= board.AttackRange ? Color.red : (dist <= board.DetectRange ? Color.yellow : Color.green) }
                        });
                }
                else
                {
                    EditorGUILayout.HelpBox("未找到玩家对象！", MessageType.Warning);
                }

                EditorGUI.indentLevel--;
            }
        }

        // ========== 树结构可视化 ==========

        void DrawTreeSection()
        {
            if (_selectedGuard?.BehaviourTree == null) return;

            var tree = _selectedGuard.BehaviourTree;
            var root = tree.Root;
            if (root == null) return;

            _showTree = EditorGUILayout.Foldout(_showTree, "🌳 行为树结构", true, EditorStyles.foldoutHeader);
            if (_showTree)
            {
                DrawSeparator();
                DrawNode(root, 0);
                DrawSeparator();
            }
        }

        void DrawNode(BTNode node, int depth)
        {
            if (node == null) return;

            float indent = depth * INDENT_PX;

            Color bgColor;
            Color textColor;
            string stateIcon;

            if (!node.TickedThisFrame)
            {
                bgColor = new Color(0.15f, 0.15f, 0.15f);
                textColor = new Color(0.5f, 0.5f, 0.5f);
                stateIcon = "○";
            }
            else
            {
                switch (node.ThisFrameResult)
                {
                    case NodeState.Success:
                        bgColor = new Color(0.1f, 0.3f, 0.1f);
                        textColor = new Color(0.3f, 1f, 0.3f);
                        stateIcon = "✓";
                        break;
                    case NodeState.Failure:
                        bgColor = new Color(0.3f, 0.1f, 0.1f);
                        textColor = new Color(1f, 0.3f, 0.3f);
                        stateIcon = "✗";
                        break;
                    case NodeState.Running:
                        bgColor = new Color(0.3f, 0.25f, 0.05f);
                        textColor = new Color(1f, 0.9f, 0.2f);
                        stateIcon = "▶";
                        break;
                    default:
                        bgColor = Color.clear;
                        textColor = Color.white;
                        stateIcon = "?";
                        break;
                }
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(indent);

            GUIStyle bgStyle = new GUIStyle();
            bgStyle.normal.background = MakeColorTex(bgColor);

            string typeIcon;
            if (node is BTSelector) typeIcon = "◇";
            else if (node is BTSequence) typeIcon = "□";
            else typeIcon = "●";

            string nodeLabel = $" {stateIcon} {typeIcon} {node.Name}";
            bool hasChildren = node is BTComposite composite && composite.Children.Count > 0;

            if (hasChildren)
            {
                string key = GetNodeKey(node);
                if (!_expandedNodes.ContainsKey(key))
                    _expandedNodes[key] = true;

                bool expanded = _expandedNodes[key];
                bool newExpanded = EditorGUILayout.Foldout(expanded, nodeLabel, true, EditorStyles.foldout);
                if (newExpanded != expanded)
                    _expandedNodes[key] = newExpanded;
            }
            else
            {
                GUIStyle labelStyle = new GUIStyle(EditorStyles.label)
                {
                    richText = false,
                    fontStyle = FontStyle.Bold
                };
                labelStyle.normal.textColor = textColor;
                EditorGUILayout.LabelField(nodeLabel, labelStyle, GUILayout.Height(24));
            }

            GUIStyle stateTagStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                fontStyle = FontStyle.Bold
            };
            stateTagStyle.normal.textColor = textColor;

            string stateText = node.TickedThisFrame ? node.ThisFrameResult.ToString() : "—";
            EditorGUILayout.LabelField(stateText, stateTagStyle, GUILayout.Width(70), GUILayout.Height(24));

            EditorGUILayout.EndHorizontal();

            if (hasChildren)
            {
                string key = GetNodeKey(node);
                if (_expandedNodes.TryGetValue(key, out bool isExpanded) && isExpanded)
                {
                    var compositeNode = (BTComposite)node;
                    foreach (var child in compositeNode.Children)
                    {
                        DrawNode(child, depth + 1);
                    }
                }
            }
        }

        // ========== 帮助信息 ==========

        void DrawHelpSection()
        {
            if (!_showHelp) return;

            EditorGUILayout.Space(8);
            DrawSeparator();
            EditorGUILayout.LabelField("📖 使用说明", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "节点状态颜色：\n" +
                "  🟢 绿色 = Success（节点执行成功）\n" +
                "  🔴 红色 = Failure（节点执行失败）\n" +
                "  🟡 黄色 = Running（节点正在执行中）\n" +
                "  ⬜ 灰色 = 未执行（本帧被跳过）\n\n" +
                "节点类型图标：\n" +
                "  ◇ Selector（选择节点 / OR逻辑）\n" +
                "  □ Sequence（顺序节点 / AND逻辑）\n" +
                "  ● 叶子节点（条件 或 行为）\n\n" +
                "兼容 v1（BTGuardAI）和 v2（BTGuardAI_v2）\n" +
                "v2 额外显示：追击体力、最后目击位置",
                MessageType.Info);
        }

        // ========== 工具方法 ==========

        void DrawNoSelectionHint()
        {
            EditorGUILayout.Space(20);
            var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                fontSize = 13,
                wordWrap = true
            };
            EditorGUILayout.LabelField(
                "场景中没有找到行为树组件\n\n" +
                "请确保：\n" +
                "1. 场景中有一个带 BTGuardAI 或 BTGuardAI_v2 的 GameObject\n" +
                "2. 游戏正在 Play 模式下运行\n\n" +
                "或者在上方工具栏手动拖入对象",
                style);
        }

        /// <summary>
        /// 自动查找场景中的 Guard（兼容 v1 和 v2）
        /// </summary>
        void AutoFindGuard()
        {
            // 优先找 v2，其次找 v1
            var guardV2 = FindObjectOfType<BTGuardAI_v2>();
            if (guardV2 != null)
            {
                _selectedGuard = guardV2;
                return;
            }

            var guardV1 = FindObjectOfType<BTGuardAI>();
            if (guardV1 != null)
            {
                _selectedGuard = guardV1;
                return;
            }

            _selectedGuard = null;
            Debug.Log("[行为树调试器] 场景中没有找到 BTGuardAI 或 BTGuardAI_v2 组件");
        }

        string GetNodeKey(BTNode node)
        {
            return $"{node.Name}_{node.GetHashCode()}";
        }

        void DrawSeparator()
        {
            Rect rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(1), GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0.3f, 0.3f, 0.3f, 1));
        }

        Texture2D MakeColorTex(Color color)
        {
            Texture2D tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        // ========== 编辑器生命周期 ==========

        void OnSelectionChange()
        {
            if (_autoFindGuard)
            {
                if (Selection.activeGameObject == null) return;

                var guardV2 = Selection.activeGameObject.GetComponent<BTGuardAI_v2>();
                if (guardV2 != null)
                {
                    _selectedGuard = guardV2;
                    Repaint();
                    return;
                }

                var guardV1 = Selection.activeGameObject.GetComponent<BTGuardAI>();
                if (guardV1 != null)
                {
                    _selectedGuard = guardV1;
                    Repaint();
                }
            }
        }

        void OnEnable()
        {
            AutoFindGuard();
        }
    }
}

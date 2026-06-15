using UnityEngine;
using UnityEditor;

namespace AIBehaviourTree
{
    /// <summary>
    /// 【自定义 Inspector — 兼容 v1 和 v2】
    ///
    /// 为 BTGuardAI 和 BTGuardAI_v2 都提供运行时行为树状态显示。
    /// 通过 IBTGuard 接口统一处理。
    /// </summary>

    // v1 的自定义 Inspector
    [CustomEditor(typeof(BTGuardAI))]
    public class BTGuardAIEditor : Editor
    {
        private bool _showRuntimeState = true;
        private float _lastRepaintTime = 0f;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            BTGuardAI guard = (BTGuardAI)target;
            DrawRuntimeState(guard as IBTGuard, "v1");
        }

        protected void DrawRuntimeState(IBTGuard guard, string version)
        {
            EditorGUILayout.Space(8);
            Rect separatorRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(2), GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(separatorRect, new Color(0.4f, 0.6f, 0.9f, 1));

            if (Application.isPlaying && guard?.BehaviourTree != null)
            {
                if (Time.realtimeSinceStartup - _lastRepaintTime > 0.1f)
                {
                    _lastRepaintTime = Time.realtimeSinceStartup;
                    Repaint();
                }

                _showRuntimeState = EditorGUILayout.Foldout(_showRuntimeState, $"🌳 行为树运行状态 [{version}]", true, EditorStyles.foldoutHeader);

                if (_showRuntimeState)
                {
                    var tree = guard.BehaviourTree;
                    var root = tree.Root;

                    if (root != null)
                    {
                        EditorGUI.indentLevel++;
                        DrawNodeInline(root, 0);
                        EditorGUI.indentLevel--;
                    }

                    EditorGUILayout.Space(4);
                    NodeState treeState = tree.GetState();
                    string stateText = treeState switch
                    {
                        NodeState.Success => "✅ 根节点: Success",
                        NodeState.Failure => "❌ 根节点: Failure",
                        NodeState.Running => "⏳ 根节点: Running",
                        _ => "❓ 未知"
                    };

                    GUIStyle stateStyle = new GUIStyle(EditorStyles.boldLabel);
                    stateStyle.normal.textColor = treeState switch
                    {
                        NodeState.Success => Color.green,
                        NodeState.Failure => Color.red,
                        NodeState.Running => Color.yellow,
                        _ => Color.white
                    };

                    EditorGUILayout.LabelField(stateText, stateStyle);
                }
            }
            else
            {
                EditorGUILayout.HelpBox("行为树状态仅在 Play 模式下显示", MessageType.Info);
            }
        }

        void DrawNodeInline(BTNode node, int depth)
        {
            if (node == null) return;

            GUILayout.BeginHorizontal();
            GUILayout.Space(depth * 20);

            string icon;
            Color color;

            if (!node.TickedThisFrame)
            {
                icon = "○";
                color = Color.gray;
            }
            else
            {
                switch (node.ThisFrameResult)
                {
                    case NodeState.Success:
                        icon = "✓";
                        color = Color.green;
                        break;
                    case NodeState.Running:
                        icon = "▶";
                        color = Color.yellow;
                        break;
                    default:
                        icon = "✗";
                        color = Color.red;
                        break;
                }
            }

            string prefix = node switch
            {
                BTSelector => "◇",
                BTSequence => "□",
                _ => "●"
            };

            GUIStyle labelStyle = new GUIStyle(EditorStyles.label);
            labelStyle.normal.textColor = color;
            if (node.TickedThisFrame && node.ThisFrameResult == NodeState.Running)
                labelStyle.fontStyle = FontStyle.Bold;

            EditorGUILayout.LabelField($"{icon} {prefix} {node.Name}", labelStyle);
            GUILayout.EndHorizontal();

            if (node is BTComposite composite && composite.Children.Count > 0)
            {
                foreach (var child in composite.Children)
                {
                    DrawNodeInline(child, depth + 1);
                }
            }
        }
    }

    /// <summary>
    /// v2 的自定义 Inspector — 继承 v1 的 Inspector，加上 v2 特有的黑板数据显示
    /// </summary>
    [CustomEditor(typeof(BTGuardAI_v2))]
    public class BTGuardAI_v2Editor : BTGuardAIEditor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            BTGuardAI_v2 guard = (BTGuardAI_v2)target;

            // 显示 v2 额外数据
            if (Application.isPlaying && guard.BehaviourTree != null)
            {
                var board = guard.BehaviourTree.Board;

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("─── v2 额外数据 ───", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;

                var staminaStyle = new GUIStyle(EditorStyles.label);
                staminaStyle.normal.textColor = board.ChaseStamina > 30f ? Color.green :
                    (board.ChaseStamina > 10f ? Color.yellow : Color.red);
                EditorGUILayout.LabelField("追击体力", $"{board.ChaseStamina:F0} / {board.MaxChaseStamina:F0}", staminaStyle);

                EditorGUILayout.LabelField("最后目击位置",
                    board.HasSeenPlayer ? $"{board.LastKnownPlayerPosition}" : "无");

                EditorGUI.indentLevel--;
            }

            DrawRuntimeState(guard as IBTGuard, "v2");
        }
    }
}

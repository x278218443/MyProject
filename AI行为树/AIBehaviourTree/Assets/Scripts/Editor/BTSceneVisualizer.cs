using UnityEngine;
using UnityEditor;

namespace AIBehaviourTree
{
    /// <summary>
    /// 【Scene 视图行为树可视化 — 兼容 v1 和 v2】
    ///
    /// 通过 IBTGuard 接口统一处理两种 AI 控制器。
    /// v2 额外显示：调查中、体力耗尽、休息等新状态。
    /// </summary>
    [InitializeOnLoad]
    public static class BTSceneVisualizer
    {
        private static bool _enabled = true;

        static BTSceneVisualizer()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        static void OnSceneGUI(SceneView sceneView)
        {
            if (!_enabled) return;
            if (!Application.isPlaying) return;

            // 查找 v2
            var guardsV2 = Object.FindObjectsOfType<BTGuardAI_v2>();
            foreach (var guard in guardsV2)
            {
                if (guard == null || guard.BehaviourTree == null) continue;
                DrawGuardStatus(guard as IBTGuard);
            }

            // 查找 v1
            var guardsV1 = Object.FindObjectsOfType<BTGuardAI>();
            foreach (var guard in guardsV1)
            {
                if (guard == null || guard.BehaviourTree == null) continue;
                // 跳过同时有 v2 的对象（避免重复绘制）
                if (guard.GetComponent<BTGuardAI_v2>() != null) continue;
                DrawGuardStatus(guard as IBTGuard);
            }
        }

        static void DrawGuardStatus(IBTGuard guard)
        {
            var tree = guard.BehaviourTree;
            Vector3 labelPos = guard.transform.position + Vector3.up * 2.5f;

            NodeState state = tree.GetState();

            string stateText;
            Color bgColor;

            if (!Application.isPlaying)
            {
                stateText = "未运行";
                bgColor = new Color(0.3f, 0.3f, 0.3f, 0.8f);
            }
            else
            {
                string activeNodeName = FindRunningNode(tree.Root);

                switch (state)
                {
                    case NodeState.Running:
                        if (activeNodeName.Contains("巡逻") || activeNodeName.Contains("Patrol"))
                        {
                            stateText = "🟢 巡逻中";
                            bgColor = new Color(0.1f, 0.5f, 0.1f, 0.85f);
                        }
                        else if (activeNodeName.Contains("追击") || activeNodeName.Contains("Chase"))
                        {
                            stateText = "🟡 追击中";
                            bgColor = new Color(0.5f, 0.4f, 0.05f, 0.85f);
                        }
                        else if (activeNodeName.Contains("攻击") || activeNodeName.Contains("Attack"))
                        {
                            stateText = "🔴 攻击中";
                            bgColor = new Color(0.6f, 0.1f, 0.1f, 0.85f);
                        }
                        else if (activeNodeName.Contains("调查") || activeNodeName.Contains("Investigate"))
                        {
                            stateText = "🔍 调查中";
                            bgColor = new Color(0.3f, 0.3f, 0.6f, 0.85f);
                        }
                        else if (activeNodeName.Contains("喘气") || activeNodeName.Contains("休息") || activeNodeName.Contains("Idle"))
                        {
                            stateText = "💤 休息中";
                            bgColor = new Color(0.4f, 0.4f, 0.4f, 0.85f);
                        }
                        else
                        {
                            stateText = "⏳ 运行中";
                            bgColor = new Color(0.4f, 0.4f, 0.1f, 0.85f);
                        }
                        break;
                    case NodeState.Success:
                        stateText = "✅ Success";
                        bgColor = new Color(0.1f, 0.4f, 0.1f, 0.85f);
                        break;
                    default:
                        stateText = "❌ Failure";
                        bgColor = new Color(0.4f, 0.1f, 0.1f, 0.85f);
                        break;
                }
            }

            Handles.BeginGUI();

            Camera cam = SceneView.currentDrawingSceneView.camera;
            Vector3 screenPos = cam.WorldToScreenPoint(labelPos);
            screenPos.y = cam.pixelHeight - screenPos.y;

            float dist = Vector3.Distance(cam.transform.position, labelPos);
            if (dist > 50f)
            {
                Handles.EndGUI();
                return;
            }

            float fontSize = Mathf.Clamp(14f / (dist * 0.05f), 10, 18);

            GUIStyle bgStyle = new GUIStyle();
            bgStyle.normal.background = MakeTex(bgColor);
            bgStyle.alignment = TextAnchor.MiddleCenter;
            bgStyle.fontSize = (int)fontSize;
            bgStyle.fontStyle = FontStyle.Bold;
            bgStyle.normal.textColor = Color.white;
            bgStyle.padding = new RectOffset(8, 8, 4, 4);

            GUIContent content = new GUIContent(stateText);
            Vector2 size = bgStyle.CalcSize(content);
            size.x += 16;
            size.y += 8;

            Rect rect = new Rect(screenPos.x - size.x * 0.5f, screenPos.y - size.y * 0.5f, size.x, size.y);
            GUI.Box(rect, content, bgStyle);

            // v2 额外显示体力条
            if (guard is BTGuardAI_v2)
            {
                var board = guard.BehaviourTree.Board;
                float barWidth = 60f;
                float barHeight = 6f;
                float barX = screenPos.x - barWidth * 0.5f;
                float barY = screenPos.y + size.y * 0.5f + 4f;

                // 背景
                EditorGUI.DrawRect(new Rect(barX, barY, barWidth, barHeight), new Color(0.2f, 0.2f, 0.2f, 0.8f));
                // 体力条
                float ratio = board.ChaseStamina / board.MaxChaseStamina;
                Color barColor = ratio > 0.5f ? Color.green : (ratio > 0.2f ? Color.yellow : Color.red);
                EditorGUI.DrawRect(new Rect(barX, barY, barWidth * ratio, barHeight), barColor);
            }

            Handles.EndGUI();
        }

        static string FindRunningNode(BTNode node)
        {
            if (node == null) return "";

            if (node is BTComposite composite)
            {
                foreach (var child in composite.Children)
                {
                    if (child.TickedThisFrame && child.ThisFrameResult == NodeState.Running)
                    {
                        if (child is BTComposite)
                        {
                            string deepResult = FindRunningNode(child);
                            if (!string.IsNullOrEmpty(deepResult))
                                return deepResult;
                        }
                        return child.Name;
                    }
                }
            }

            if (node.TickedThisFrame && node.ThisFrameResult == NodeState.Running)
                return node.Name;

            return "";
        }

        static Texture2D MakeTex(Color color)
        {
            Texture2D tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }
    }
}

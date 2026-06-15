using UnityEngine;

namespace AIBehaviourTree
{
    /// <summary>
    /// 【玩家控制器 — 简单的移动脚本】
    ///
    /// 这个脚本让你可以用键盘控制玩家移动，
    /// 方便测试守卫AI的行为（走近看它追击，走远看它巡逻）。
    ///
    /// 操作方式：
    /// - WASD 或方向键：移动
    /// - Space：跳跃（简单的y轴位移）
    ///
    /// ⚠️ 记得给这个对象设置 Tag = "Player"，
    /// 这样守卫AI可以自动找到它。
    /// </summary>
    public class Player : MonoBehaviour
    {
        [Header("=== 移动设置 ===")]
        [Tooltip("移动速度")]
        public float moveSpeed = 5f;

        [Tooltip("跳跃力度")]
        public float jumpForce = 5f;

        // 简单的Y轴速度（用于跳跃/重力模拟）
        private float _verticalVelocity = 0f;

        void Update()
        {
            // 读取输入
            float horizontal = Input.GetAxis("Horizontal"); // A/D 或 ←/→
            float vertical = Input.GetAxis("Vertical");     // W/S 或 ↑/↓

            // 计算移动方向（基于世界坐标）
            Vector3 moveDirection = new Vector3(horizontal, 0, vertical).normalized;

            // 跳跃
            if (Input.GetKeyDown(KeyCode.Space) && Mathf.Abs(_verticalVelocity) < 0.01f)
            {
                _verticalVelocity = jumpForce;
            }

            // 简单的重力模拟
            _verticalVelocity -= 9.81f * Time.deltaTime;

            // 应用移动
            Vector3 movement = (moveDirection * moveSpeed + Vector3.up * _verticalVelocity) * Time.deltaTime;
            transform.position += movement;

            // 碰到地面时停止下落
            if (transform.position.y < 0.5f)
            {
                transform.position = new Vector3(transform.position.x, 0.5f, transform.position.z);
                _verticalVelocity = 0;
            }

            // 面朝移动方向
            if (moveDirection != Vector3.zero)
            {
                transform.rotation = Quaternion.LookRotation(moveDirection);
            }

            // 显示操作提示
            DrawHelpInfo();
        }

        /// <summary>
        /// 在屏幕左上角显示操作提示
        /// </summary>
        private void DrawHelpInfo()
        {
            // 这里用 OnGUI 来显示简单的文字提示
            // 实际项目中应该用 UI 系统
        }

        void OnGUI()
        {
            // 屏幕左上角的操作提示
            GUI.color = Color.white;
            GUI.Label(new Rect(10, 10, 300, 200),
                "<color=yellow>【玩家控制】</color>\n" +
                "WASD / 方向键: 移动\n" +
                "Space: 跳跃\n\n" +
                "<color=cyan>走近守卫 → 观察追击</color>\n" +
                "<color=cyan>贴近守卫 → 观察攻击</color>\n" +
                "<color=cyan>远离守卫 → 观察巡逻</color>");
        }
    }
}

using UnityEngine;

namespace AIBehaviourTree
{
    /// <summary>
    /// 【巡逻行为节点】
    ///
    /// 让NPC在多个路径点之间来回走动。
    ///
    /// 行为节点的特点：
    /// - 可以返回 Running（移动是持续性的，不是一瞬间完成的）
    /// - 每次 Tick 都会推进一点进度（比如向前走一小步）
    /// - 当到达目标点后返回 Success
    ///
    /// 巡逻的逻辑：
    /// 1. 从黑板获取当前目标巡逻点
    /// 2. 向目标点移动
    /// 3. 到达后，切换到下一个巡逻点，返回 Success
    /// 4. 如果没有巡逻点，返回 Failure（让 Selector 去尝试其他方案）
    ///
    /// 类比：你沿着一条路线散步，走到一个点后转向下一个点继续走。
    /// </summary>
    public class BTPatrol : BTNode
    {
        // 所有巡逻路径点（在Inspector中设置）
        private Transform[] _waypoints;

        // 当前目标巡逻点的索引
        private int _currentWaypointIndex = 0;

        // 到达目标点的判定距离（小于这个距离就算"到了"）
        private float _arrivalThreshold = 0.5f;

        /// <summary>
        /// 创建巡逻行为
        /// </summary>
        /// <param name="waypoints">巡逻路径点数组</param>
        public BTPatrol(Transform[] waypoints, string name = "Patrol")
            : base(name)
        {
            _waypoints = waypoints;
        }

        public override NodeState Tick()
        {
            // 没有巡逻点 → 巡逻失败
            if (_waypoints == null || _waypoints.Length == 0)
            {
                Debug.Log("[巡逻] 没有设置巡逻点！");
                _state = NodeState.Failure;
                MarkTicked(_state);
                return _state;
            }

            Transform owner = _tree.Owner;
            Transform target = _waypoints[_currentWaypointIndex];

            // 如果目标点被销毁了，跳过
            if (target == null)
            {
                GoToNextWaypoint();
                _state = NodeState.Running;
                MarkTicked(_state);
                return _state;
            }

            // 1. 计算到目标点的距离
            Vector3 direction = target.position - owner.position;
            float distanceToTarget = direction.magnitude;

            // 2. 检查是否已经到达
            if (distanceToTarget <= _arrivalThreshold)
            {
                // 到达了！切换到下一个巡逻点
                Debug.Log($"[巡逻] 到达巡逻点 {_currentWaypointIndex}，前往下一个");
                GoToNextWaypoint();
                _state = NodeState.Success;
                MarkTicked(_state);
                return _state;
            }

            // 3. 朝目标点旋转（让NPC面朝移动方向）
            Quaternion targetRotation = Quaternion.LookRotation(direction);
            owner.rotation = Quaternion.RotateTowards(
                owner.rotation,
                targetRotation,
                _tree.Board.TurnSpeed * Time.deltaTime  // 平滑旋转
            );

            // 4. 向目标点移动
            owner.position = Vector3.MoveTowards(
                owner.position,
                target.position,
                _tree.Board.MoveSpeed * Time.deltaTime
            );

            // 还在移动中 → 返回 Running（下一帧会继续执行这里的逻辑）
            _state = NodeState.Running;
            MarkTicked(_state);
            return _state;
        }

        /// <summary>
        /// 切换到下一个巡逻点（循环）
        /// </summary>
        private void GoToNextWaypoint()
        {
            _currentWaypointIndex = (_currentWaypointIndex + 1) % _waypoints.Length;
        }

        /// <summary>
        /// 重置巡逻状态（从第一个巡逻点重新开始）
        /// </summary>
        public override void Reset()
        {
            base.Reset();
            _currentWaypointIndex = 0;
        }
    }
}

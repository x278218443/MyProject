using UnityEngine;

namespace AIBehaviourTree
{
    /// <summary>
    /// 【条件节点 — 判断玩家是否在指定范围内】
    ///
    /// 条件节点的特点：
    /// - 只返回 Success 或 Failure，不会返回 Running（判断是瞬间完成的）
    /// - 不执行任何动作，只做"检查"
    ///
    /// 这个节点可以复用：通过参数传入不同的范围值，
    /// 既可以当"感知范围"判断，也可以当"攻击范围"判断。
    ///
    /// 类比：你走到门口，往外面看了一眼 — 看到人了（Success）/ 没看到人（Failure）
    /// 看一眼是瞬间完成的，不需要"正在看"（Running）这个状态。
    /// </summary>
    public class BTIsPlayerInRange : BTNode
    {
        // 判断的距离阈值
        private float _range;

        // 是否需要写入黑板（记录玩家最近位置）
        private bool _updatePlayerPosition;

        /// <summary>
        /// 创建范围检测条件节点
        /// </summary>
        /// <param name="range">检测范围（米）</param>
        /// <param name="updatePlayerPosition">
        /// 是否把玩家当前位置写入黑板。
        /// 在"感知范围"检测时设为 true，这样后续的追击节点知道玩家在哪。
        /// 在"攻击范围"检测时可以设为 false（因为之前已经存过了）。
        /// </param>
        public BTIsPlayerInRange(float range, bool updatePlayerPosition = false, string name = "IsPlayerInRange")
            : base(name)
        {
            _range = range;
            _updatePlayerPosition = updatePlayerPosition;
        }

        public override NodeState Tick()
        {
            // 1. 先检查黑板上有没有玩家引用
            //    如果场景里没有设置玩家，直接返回失败
            if (_tree == null || _tree.Board.PlayerTransform == null)
            {
                _state = NodeState.Failure;
                return _state;
            }

            // 2. 计算NPC和玩家之间的距离
            Vector3 npcPos = _tree.Owner.position;
            Vector3 playerPos = _tree.Board.PlayerTransform.position;
            float distance = Vector3.Distance(npcPos, playerPos);

            // 3. 距离在范围内吗？
            if (distance <= _range)
            {
                // 在范围内！
                // 如果需要，把玩家当前位置存到黑板上
                // （这样追击节点就知道往哪走了）
                if (_updatePlayerPosition)
                {
                    _tree.Board.PlayerTransform = _tree.Board.PlayerTransform;
                    // 注意：PlayerTransform 已经是引用了，这里不需要额外赋值
                    // 如果你以后想存"最后看到玩家的位置"，可以加一个 Vector3 LastSeenPosition
                }

                Debug.Log($"[条件] {Name}: 玩家在{_range}米范围内 (距离: {distance:F1}m) ✓");
                _state = NodeState.Success;
            }
            else
            {
                // 不在范围内
                Debug.Log($"[条件] {Name}: 玩家不在{_range}米范围内 (距离: {distance:F1}m) ✗");
                _state = NodeState.Failure;
            }

            MarkTicked(_state);
            return _state;
        }
    }
}

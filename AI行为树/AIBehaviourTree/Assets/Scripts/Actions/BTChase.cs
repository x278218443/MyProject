using UnityEngine;

namespace AIBehaviourTree
{
    /// <summary>
    /// 【追击行为节点】
    ///
    /// 当NPC发现玩家（感知范围内），就朝玩家方向移动。
    ///
    /// 追击 vs 巡逻的区别：
    /// - 巡逻：目标是固定的路径点，到一个换下一个
    /// - 追击：目标是动态的玩家位置，玩家跑到哪追到哪
    ///
    /// 追击不会返回 Success（因为玩家一直在跑），
    /// 除非玩家跑出了感知范围 → 这时条件节点会失败，
    /// Selector 就会切换到巡逻。
    ///
    /// 类比：警察追小偷，小偷跑到哪追到哪，直到追上（攻击范围）或追丢了。
    /// </summary>
    public class BTChase : BTNode
    {
        // 是否在追击中（用于日志输出，避免每帧都打印）
        private bool _isChasing = false;

        public BTChase(string name = "Chase") : base(name) { }

        public override NodeState Tick()
        {
            // 1. 检查黑板上有没有玩家
            if (_tree == null || _tree.Board.PlayerTransform == null)
            {
                _state = NodeState.Failure;
                MarkTicked(_state);
                return _state;
            }

            Transform owner = _tree.Owner;
            Transform player = _tree.Board.PlayerTransform;

            // 2. 计算方向和距离
            Vector3 directionToPlayer = player.position - owner.position;
            float distance = directionToPlayer.magnitude;

            // 3. 如果已经进入攻击范围了，追击节点返回 Failure
            //    （让优先级更高的"攻击"节点来处理）
            //
            // ⚠️ 这是行为树的一个重要设计技巧：
            // 追击节点只负责"接近"，接近到攻击范围后交给攻击节点。
            // 这样每个节点职责单一，更容易维护。
            if (distance <= _tree.Board.AttackRange)
            {
                if (_isChasing)
                {
                    Debug.Log("[追击] 已进入攻击范围，交由攻击节点处理");
                    _isChasing = false;
                }
                _state = NodeState.Failure;
                MarkTicked(_state);
                return _state;
            }

            // 4. 开始追击！
            if (!_isChasing)
            {
                Debug.Log("[追击] 发现玩家！开始追击！");
                _isChasing = true;
            }

            // 5. 朝玩家方向旋转
            Quaternion targetRotation = Quaternion.LookRotation(directionToPlayer);
            owner.rotation = Quaternion.RotateTowards(
                owner.rotation,
                targetRotation,
                _tree.Board.TurnSpeed * Time.deltaTime
            );

            // 6. 向玩家方向移动
            owner.position = Vector3.MoveTowards(
                owner.position,
                player.position,
                _tree.Board.MoveSpeed * 1.2f * Time.deltaTime  // 追击时速度稍微快一点
            );

            // 追击是持续行为，返回 Running
            _state = NodeState.Running;
            MarkTicked(_state);
            return _state;
        }

        public override void Reset()
        {
            base.Reset();
            _isChasing = false;
        }
    }
}

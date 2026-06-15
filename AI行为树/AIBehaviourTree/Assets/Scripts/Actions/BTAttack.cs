using UnityEngine;

namespace AIBehaviourTree
{
    /// <summary>
    /// 【攻击行为节点】
    ///
    /// 当玩家在攻击范围内时，NPC面向玩家并"攻击"。
    ///
    /// 简化处理：
    /// - 每次 Tick 都返回 Running（攻击是持续的，只要玩家在范围内就一直攻击）
    /// - 用冷却时间控制攻击频率（不是每帧都攻击）
    /// - 攻击效果只是打日志，你可以扩展为扣血、播放动画等
    ///
    /// ⚠️ 进阶思考：为什么攻击返回 Running 而不是 Success？
    /// 因为攻击是一个"持续状态"，只要玩家在范围内就应该持续攻击。
    /// 如果返回 Success，Selector 会在下一帧重新评估所有分支，
    /// 可能导致不必要的切换。
    ///
    /// 类比：你正在和人握手，这是一个持续动作，不是一瞬间完成的。
    /// </summary>
    public class BTAttack : BTNode
    {
        // 攻击冷却时间（秒）— 两次攻击之间的间隔
        private float _attackCooldown = 1.0f;

        // 上次攻击的时间戳
        private float _lastAttackTime = -999f;

        // 攻击伤害值（你可以扩展为真实伤害系统）
        private int _damage = 10;

        public BTAttack(string name = "Attack") : base(name) { }

        public override NodeState Tick()
        {
            // 1. 检查玩家是否存在
            if (_tree == null || _tree.Board.PlayerTransform == null)
            {
                _state = NodeState.Failure;
                MarkTicked(_state);
                return _state;
            }

            Transform owner = _tree.Owner;
            Transform player = _tree.Board.PlayerTransform;

            // 2. 计算距离
            float distance = Vector3.Distance(owner.position, player.position);

            // 3. 如果玩家跑出了攻击范围 → 攻击失败（Selector会切换到追击或巡逻）
            if (distance > _tree.Board.AttackRange)
            {
                Debug.Log("[攻击] 玩家脱离攻击范围");
                _state = NodeState.Failure;
                MarkTicked(_state);
                return _state;
            }

            // 4. 面朝玩家（攻击时始终面对敌人）
            Vector3 direction = (player.position - owner.position).normalized;
            if (direction != Vector3.zero)
            {
                Quaternion lookRotation = Quaternion.LookRotation(direction);
                owner.rotation = Quaternion.RotateTowards(
                    owner.rotation,
                    lookRotation,
                    _tree.Board.TurnSpeed * Time.deltaTime
                );
            }

            // 5. 检查攻击冷却
            if (Time.time - _lastAttackTime >= _attackCooldown)
            {
                // 发起攻击！
                _lastAttackTime = Time.time;
                Debug.Log($"[攻击] ★ 对玩家造成 {_damage} 点伤害！★");

                // ========== 你可以在这里扩展攻击逻辑 ==========
                // 例如：
                // - player.GetComponent<PlayerHealth>().TakeDamage(_damage);
                // - 播放攻击动画: animator.SetTrigger("Attack");
                // - 播放攻击音效: AudioSource.PlayClipAtPoint(attackSound, owner.position);
                // - 生成攻击特效: Instantiate(attackEffect, ...);
            }
            else
            {
                // 还在冷却中，显示等待信息（每秒最多显示一次，避免刷屏）
                float cooldownLeft = _attackCooldown - (Time.time - _lastAttackTime);
                // 只在冷却刚开始时打印
                if (cooldownLeft > _attackCooldown - 0.1f)
                {
                    Debug.Log($"[攻击] 冷却中... 剩余 {cooldownLeft:F1}s");
                }
            }

            // 攻击持续进行 → 返回 Running
            _state = NodeState.Running;
            MarkTicked(_state);
            return _state;
        }

        public override void Reset()
        {
            base.Reset();
            _lastAttackTime = -999f;
        }
    }
}

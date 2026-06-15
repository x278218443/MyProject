namespace AIBehaviourTree
{
    /// <summary>
    /// 【冷却装饰节点 — BTCooldown】
    ///
    /// 装饰节点 = 只有一个子节点的节点，用来"修饰"子节点的行为。
    /// BTCooldown 的作用：让子节点执行完后，冷却一段时间内不再执行。
    ///
    /// 通俗理解："你做完了这件事，歇一会儿再说"
    ///
    /// 特性：
    /// - 子节点返回 Success 后，开始冷却计时
    /// - 冷却期间直接返回 Failure（不执行子节点）
    /// - 冷却结束后，可以再次执行子节点
    ///
    /// 使用场景：
    /// - 攻击冷却（打一下歇一秒）
    /// - 调查冷却（调查完等几秒再巡逻）
    /// - 防止行为频繁切换
    ///
    /// ⚠️ 这是行为树中"装饰节点"的最简实现。
    /// 更复杂的装饰节点还有：Inverter（反转结果）、Repeater（重复执行N次）等。
    /// </summary>
    public class BTCooldown : BTNode
    {
        // 被装饰的子节点
        private BTNode _child;

        // 冷却时间（秒）
        private float _cooldownTime;

        // 上次执行完毕的时间戳
        private float _lastExecutionTime = -999f;

        /// <summary>
        /// 创建冷却装饰节点
        /// </summary>
        /// <param name="child">被装饰的子节点</param>
        /// <param name="cooldownTime">冷却时间（秒）</param>
        public BTCooldown(BTNode child, float cooldownTime, string name = "Cooldown")
            : base(name)
        {
            _child = child;
            _cooldownTime = cooldownTime;
        }

        public override NodeState Tick()
        {
            // 检查是否还在冷却中
            if (UnityEngine.Time.time - _lastExecutionTime < _cooldownTime)
            {
                // 还在冷却 → 返回 Failure（不执行子节点）
                _state = NodeState.Failure;
                MarkTicked(_state);
                return _state;
            }

            // 冷却完毕（或第一次执行），执行子节点
            NodeState childState = _child.Tick();

            // 如果子节点成功或失败，记录时间戳开始冷却
            if (childState != NodeState.Running)
            {
                _lastExecutionTime = UnityEngine.Time.time;
            }

            _state = childState;
            MarkTicked(_state);
            return _state;
        }

        public override void Reset()
        {
            base.Reset();
            _lastExecutionTime = -999f;
            _child?.Reset();
        }
    }
}

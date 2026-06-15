using System.Collections.Generic;
using UnityEngine;

namespace AIBehaviourTree
{
    /// <summary>
    /// 【组合节点基类】
    ///
    /// 组合节点 = 有多个子节点的节点，负责决定"先执行谁、后执行谁"。
    /// 这是行为树中最关键的控制结构。
    ///
    /// 两种最重要的组合节点：
    /// - Sequence（顺序节点）：像 AND — 所有子节点都必须成功，整体才算成功
    /// - Selector（选择节点）：像 OR — 只要有一个子节点成功，整体就算成功
    /// </summary>
    public abstract class BTComposite : BTNode
    {
        // 子节点列表（行为树就是通过这些列表形成"树"的结构）
        protected List<BTNode> _children = new List<BTNode>();

        // 【调试用】公开子节点列表，让 Editor 窗口可以遍历树结构
        public IReadOnlyList<BTNode> Children => _children;

        public BTComposite(string name = "") : base(name) { }

        /// <summary>
        /// 添加子节点，并设置子节点所属的树
        /// </summary>
        public BTComposite AddChild(BTNode child)
        {
            _children.Add(child);
            // 如果这棵树已经绑定了，子节点也要绑定
            if (_tree != null)
            {
                child.SetTree(_tree);
            }
            return this; // 返回 this 允许链式调用，比如 .AddChild(a).AddChild(b)
        }

        /// <summary>
        /// 绑定树引用时，也要把树传给所有子节点
        /// </summary>
        public override void SetTree(BehaviourTree tree)
        {
            base.SetTree(tree);
            foreach (var child in _children)
            {
                child.SetTree(tree);
            }
        }

        /// <summary>
        /// 重置所有子节点
        /// </summary>
        public override void Reset()
        {
            base.Reset();
            foreach (var child in _children)
            {
                child.Reset();
            }
        }
    }

    /// <summary>
    /// 【Sequence 顺序节点 — "AND"逻辑】
    ///
    /// 执行规则：从左到右依次执行子节点
    /// - 如果某个子节点返回 Failure → 整个 Sequence 立即返回 Failure
    /// - 如果某个子节点返回 Running → 整个 Sequence 立即返回 Running
    /// - 只有所有子节点都返回 Success → 整个 Sequence 才返回 Success
    ///
    /// ⚡ 每帧从头开始评估（不记住上次执行到哪）。
    /// 这样条件变化时，Sequence 能立即响应。
    /// 子节点自己的状态（如巡逻点索引、攻击冷却时间）由子节点自己保存。
    /// </summary>
    public class BTSequence : BTComposite
    {
        public BTSequence(string name = "Sequence") : base(name) { }

        public new BTSequence AddChild(BTNode child)
        {
            base.AddChild(child);
            return this;
        }

        public override NodeState Tick()
        {
            // 每帧从第一个子节点开始检查
            for (int i = 0; i < _children.Count; i++)
            {
                NodeState childState = _children[i].Tick();

                switch (childState)
                {
                    case NodeState.Failure:
                        // 某个子节点失败了 → 整个序列失败
                        _state = NodeState.Failure;
                        MarkTicked(_state);
                        return _state;

                    case NodeState.Running:
                        // 某个子节点还在执行 → 整个序列还在执行
                        _state = NodeState.Running;
                        MarkTicked(_state);
                        return _state;

                    case NodeState.Success:
                        // 这个子节点成功了，继续执行下一个
                        break;
                }
            }

            // 所有子节点都成功了 → 整个序列成功
            _state = NodeState.Success;
            MarkTicked(_state);
            return _state;
        }
    }

    /// <summary>
    /// 【Selector 选择节点 — "OR"逻辑】
    ///
    /// 执行规则：从左到右依次尝试子节点
    /// - 如果某个子节点返回 Success/Running → 整个 Selector 立即返回该状态
    /// - 只有所有子节点都返回 Failure → 整个 Selector 才返回 Failure
    ///
    /// ⚡ 每帧从第一个子节点开始评估（不记住上次执行到哪）。
    /// 保证高优先级分支（越左越优先）随时能抢占低优先级分支。
    ///
    /// 举例：
    ///   Selector[
    ///     攻击分支,    ← 最高优先级，每帧都先检查
    ///     追击分支,    ← 其次
    ///     巡逻分支     ← 兜底
    ///   ]
    ///   = 每帧都问：能攻击吗？不能就问：能追吗？不能就巡逻。
    /// </summary>
    public class BTSelector : BTComposite
    {
        public BTSelector(string name = "Selector") : base(name) { }

        public new BTSelector AddChild(BTNode child)
        {
            base.AddChild(child);
            return this;
        }

        public override NodeState Tick()
        {
            // 每帧从第一个子节点（最高优先级）开始检查
            for (int i = 0; i < _children.Count; i++)
            {
                NodeState childState = _children[i].Tick();

                switch (childState)
                {
                    case NodeState.Success:
                    case NodeState.Running:
                        // 找到一个能用的方案了！立即返回
                        _state = childState;
                        MarkTicked(_state);
                        return _state;

                    case NodeState.Failure:
                        // 这个方案不行，尝试下一个
                        break;
                }
            }

            // 所有方案都失败了
            _state = NodeState.Failure;
            MarkTicked(_state);
            return _state;
        }
    }
}

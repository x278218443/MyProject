using UnityEngine;

namespace AIBehaviourTree
{
    /// <summary>
    /// 【行为树节点的返回状态】
    ///
    /// 这是行为树最核心的概念！每个节点执行完必须返回这三种状态之一：
    ///
    /// - Success: 这个节点执行成功了（比如"玩家在攻击范围内"这个条件满足了）
    /// - Failure: 这个节点执行失败了（比如"玩家不在感知范围内"）
    /// - Running: 这个节点还在执行中，需要在下一帧继续（比如"正在移动到目标点"）
    ///
    /// 类比生活中的例子：
    /// "去厨房拿水" → 正在走（Running）→ 到了拿到水了（Success）/ 厨房着火了（Failure）
    /// </summary>
    public enum NodeState
    {
        Success,
        Failure,
        Running
    }

    /// <summary>
    /// 【行为树节点基类】
    ///
    /// 行为树 = 一棵树，每个节点都是一个"判断"或"动作"。
    /// 所有节点都继承自这个基类，统一通过 Tick() 方法来执行。
    ///
    /// 节点分三大类：
    /// 1. 组合节点(Composite): 控制子节点的执行流程（如 Sequence、Selector）
    /// 2. 装饰节点(Decorator): 修饰单个子节点的行为（如反转、重复）
    /// 3. 叶子节点(Leaf): 实际的条件判断或动作执行
    /// </summary>
    public abstract class BTNode
    {
        // 节点的名字，方便调试时在日志里看到是哪个节点在执行
        public string Name;

        // 当前节点的状态，每次 Tick() 时更新
        protected NodeState _state;

        // 节点所属的行为树（通过 SetTree 方法绑定）
        protected BehaviourTree _tree;

        // ========== 调试追踪数据 ==========

        // 上一次 Tick 的返回状态（公开只读，供 Editor 读取）
        public NodeState LastTickResult => _state;

        // 本帧是否被 Tick 过（Editor 用这个来高亮当前激活的节点）
        public bool TickedThisFrame { get; private set; }

        // 本帧 Tick 的返回状态（只在本帧有效，下一帧会被清除）
        public NodeState ThisFrameResult { get; private set; }

        /// <summary>
        /// 【构造函数】创建一个行为树节点
        /// </summary>
        /// <param name="name">节点名称，用于调试显示</param>
        public BTNode(string name = "")
        {
            Name = name;
            _state = NodeState.Failure; // 默认状态是失败（未执行）
        }

        /// <summary>
        /// 清除本帧的调试标记（每帧 Tick 前调用）
        /// </summary>
        public void ClearFrameDebug()
        {
            TickedThisFrame = false;
        }

        /// <summary>
        /// 标记本帧已被 Tick（内部方法，Tick 后自动调用）
        /// </summary>
        protected void MarkTicked(NodeState result)
        {
            TickedThisFrame = true;
            ThisFrameResult = result;
        }

        /// <summary>
        /// 【Tick 方法 — 行为树的灵魂！】
        ///
        /// 每一帧（或每个逻辑帧），行为树会从根节点开始调用 Tick()，
        /// 递归地向下执行。这是行为树"运行"的方式。
        ///
        /// 类比：想象你在执行任务清单，每过一会儿就检查一下当前任务的状态。
        /// </summary>
        /// <returns>节点的执行状态：Success / Failure / Running</returns>
        public abstract NodeState Tick();

        /// <summary>
        /// 【重置节点状态】
        ///
        /// 当行为树需要重新开始执行时（比如从"追击"切换回"巡逻"），
        /// 需要把相关节点的状态重置为初始状态。
        /// </summary>
        public virtual void Reset()
        {
            _state = NodeState.Failure;
        }

        /// <summary>
        /// 【绑定行为树引用】
        ///
        /// 让每个节点知道自己属于哪棵树，
        /// 这样节点可以访问树上的共享数据（比如"玩家位置在哪"）
        /// </summary>
        public virtual void SetTree(BehaviourTree tree)
        {
            _tree = tree;
        }
    }
}

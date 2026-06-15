using UnityEngine;

namespace AIBehaviourTree
{
    /// <summary>
    /// 【行为树持有者接口】
    ///
    /// 所有挂载行为树的 MonoBehaviour 都实现这个接口，
    /// 让 Editor 调试工具可以统一处理，不需要知道具体是 v1 还是 v2。
    /// </summary>
    public interface IBTGuard
    {
        BehaviourTree BehaviourTree { get; }
        Transform transform { get; }
    }

    /// <summary>
    /// 【行为树本体 — BehaviourTree】
    ///
    /// 这是行为树的核心容器，包含：
    /// 1. 一棵树（root 节点及其所有子节点）
    /// 2. 共享数据（Blackboard，树上所有节点都能读写的"公告板"）
    /// 3. Tick 驱动（每帧/定时调用，让树"活"起来）
    ///
    /// 类比：行为树就像一个公司的组织架构
    /// - Root（CEO）负责向下传达指令
    /// - Blackboard（公告栏）是大家共享的信息板
    /// - Tick（工作节奏）决定多久检查一次工作进度
    /// </summary>
    public class BehaviourTree
    {
        /// <summary>
        /// 【Blackboard 黑板 — 共享数据存储】
        ///
        /// 黑板是行为树中最重要的设计模式之一！
        /// 它让不同节点之间可以共享数据，而不需要直接互相引用。
        ///
        /// 举例：
        /// - "玩家检测"节点发现玩家后，把玩家位置写到黑板上
        /// - "追击"节点从黑板读取玩家位置，向那个方向移动
        /// - 这样两个节点就通过黑板间接通信了
        ///
        /// 就像公司的公告栏：市场部写了客户信息，销售部去看就能用。
        /// </summary>
        public class Blackboard
        {
            // ===== 基础参数 =====

            // 玩家的Transform引用（用于追击、攻击等）
            public Transform PlayerTransform;

            // 感知范围：NPC能"看到"多远的敌人（单位：米）
            public float DetectRange = 10f;

            // 攻击范围：NPC在多近距离内会发起攻击（单位：米）
            public float AttackRange = 2f;

            // NPC的移动速度
            public float MoveSpeed = 3.5f;

            // NPC的旋转速度（转向目标的速度，单位：度/秒）
            public float TurnSpeed = 720f;

            // ===== v2 新增：记忆与状态数据 =====

            // 最后一次看到玩家的位置（用于追丢后去调查）
            public Vector3 LastKnownPlayerPosition;

            // 是否曾经看到过玩家（防止一开始就跑去错误位置调查）
            public bool HasSeenPlayer = false;

            // 追击剩余体力（每次追击时递减，耗尽则放弃）
            public float ChaseStamina = 100f;

            // 追击最大体力
            public float MaxChaseStamina = 100f;

            // 追击体力恢复速度（放弃追击后慢慢恢复）
            public float StaminaRecoveryRate = 20f;

            // 调查时在目标位置附近搜索的半径
            public float InvestigateSearchRadius = 3f;

            // 是否正在调查中（调查开始时=true，完成时=false）
            // 巡逻分支会检查这个标记，防止和调查互相抢
            public bool IsInvestigating = false;

            // 待机休息时间（秒）
            public float IdleDuration = 3f;
        }

        // 树的根节点（整个行为树从这里开始执行）
        private BTNode _root;

        // 【调试用】公开根节点，让 Editor 窗口可以遍历整棵树
        public BTNode Root => _root;

        // 共享黑板
        public Blackboard Board { get; private set; }

        // 行为树所属的NPC的Transform（用于移动等操作）
        public Transform Owner { get; private set; }

        // 当前正在Running的节点（用于调试显示）
        public BTNode CurrentRunningNode { get; private set; }

        /// <summary>
        /// 创建行为树
        /// </summary>
        /// <param name="owner">这棵树控制的NPC</param>
        /// <param name="root">根节点</param>
        public BehaviourTree(Transform owner, BTNode root)
        {
            Owner = owner;
            _root = root;
            Board = new Blackboard();

            // 把树的引用传递给所有节点
            _root.SetTree(this);

            Debug.Log($"[行为树] 初始化完成，NPC: {owner.name}");
        }

        /// <summary>
        /// 【Tick — 驱动行为树运行】
        ///
        /// 每帧调用一次，从根节点开始向下执行。
        /// 这就是行为树"思考"的过程！
        ///
        /// 类比：你在看任务清单，每过一会儿就从头到尾检查一遍，
        /// 看看当前应该做什么。
        /// </summary>
        public void Tick()
        {
            if (_root == null) return;

            // 【调试】先清除所有节点的本帧标记
            ClearDebugFlags(_root);

            NodeState state = _root.Tick();

            // 记录当前状态，方便调试
            _state = state;
        }

        /// <summary>
        /// 【调试用】递归清除整棵树所有节点的本帧调试标记
        ///
        /// 每次 Tick 前调用，确保 TickedThisFrame 只在本帧被 Tick 的节点上为 true。
        /// 这样 Editor 窗口就能准确显示"这一帧哪些节点被执行了"。
        /// </summary>
        private void ClearDebugFlags(BTNode node)
        {
            if (node == null) return;
            node.ClearFrameDebug();

            // 如果是组合节点，递归清除子节点
            if (node is BTComposite composite)
            {
                foreach (var child in composite.Children)
                {
                    ClearDebugFlags(child);
                }
            }
        }

        private NodeState _state;

        /// <summary>
        /// 获取树的当前状态（用于调试显示）
        /// </summary>
        public NodeState GetState() => _state;

        /// <summary>
        /// 重置整棵树（让树从头开始重新决策）
        ///
        /// 什么时候需要重置？
        /// 比如NPC被击杀后复活，需要重新从巡逻开始。
        /// </summary>
        public void ResetTree()
        {
            _root?.Reset();
        }
    }
}

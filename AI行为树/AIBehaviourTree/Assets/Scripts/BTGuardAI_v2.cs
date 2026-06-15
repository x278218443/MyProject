using UnityEngine;

namespace AIBehaviourTree
{
    /// <summary>
    /// 【哨兵守卫 v2 — 多层行为树 Demo】
    ///
    /// 相比 v1 的三大行为，v2 加入了：
    /// - 最后目击位置记忆 → 追丢后会去调查
    /// - 追击体力系统 → 追太久会累，需要休息
    /// - 调查行为 → 去最后看到人的位置搜索
    /// - 待机/休息行为 → 巡逻累了会歇一会儿
    /// - 冷却装饰器 → 防止行为频繁切换
    ///
    /// 📊 行为树结构（5层深度，3种节点类型）：
    ///
    ///                            Root (Selector)
    ///              ┌────────┬──────────┬──────────┬────────┐
    ///          攻击分支   追击分支     调查分支   巡逻分支   待机
    ///          (Seq)      (Seq)       (Seq)      (Seq)    (Idle)
    ///            |         |            |          |        |
    ///          攻击范围?  Selector   看到过?    有巡逻点?   恢复体力
    ///          攻击        |    |    不在感知?   巡逻
    ///                 正常追击  太累?  调查
    ///                 (Seq)   (Seq)
    ///                 |   |    |   |
    ///               感知? 体力? 太累 休息
    ///               追击   追击  休息
    ///
    /// 行为优先级：攻击 > 追击 > 调查 > 巡逻 > 待机
    ///
    /// 使用方法：
    /// 1. 把这个脚本替换原来 Guard 上的 BTGuardAI
    ///    （或新建一个 Cube 挂这个脚本）
    /// 2. 场景设置和 v1 一样
    /// </summary>
    public class BTGuardAI_v2 : MonoBehaviour, IBTGuard
    {
        // ========== Inspector 可配置参数 ==========

        [Header("=== 目标设置 ===")]
        public Transform playerTransform;

        [Header("=== 感知参数 ===")]
        public float detectRange = 10f;
        public float attackRange = 2f;

        [Header("=== 移动参数 ===")]
        public float moveSpeed = 3.5f;
        public float turnSpeed = 720f;

        [Header("=== 巡逻路径点 ===")]
        public Transform[] waypoints;

        [Header("=== v2: 追击体力 ===")]
        [Tooltip("追击时每秒消耗的体力")]
        public float chaseStaminaCost = 15f;

        [Tooltip("休息时每秒恢复的体力")]
        public float staminaRecovery = 20f;

        [Header("=== v2: 待机 ===")]
        [Tooltip("休息持续时间（秒）")]
        public float idleDuration = 3f;

        // 行为树实例
        private BehaviourTree _behaviourTree;
        public BehaviourTree BehaviourTree => _behaviourTree;

        void Start()
        {
            if (playerTransform == null)
            {
                GameObject playerObj = GameObject.FindWithTag("Player");
                if (playerObj != null)
                {
                    playerTransform = playerObj.transform;
                }
            }

            BuildBehaviourTree();
        }

        /// <summary>
        /// 【构建多层行为树 — 这是核心！】
        ///
        /// 注意看代码结构如何直接对应树的层级：
        /// - 最外层 Selector = 根节点，5个分支
        /// - 每个 Sequence = 一个分支，里面是条件+行为
        /// - 追击分支内嵌套了 Selector，实现了"正常追击 vs 体力耗尽"的选择
        /// </summary>
        private void BuildBehaviourTree()
        {
            // ==========================================
            //  叶子节点：条件
            // ==========================================

            BTIsPlayerInRange isPlayerInAttackRange =
                new BTIsPlayerInRange(attackRange, false, "攻击范围内?");

            BTIsPlayerInRange isPlayerInDetectRange =
                new BTIsPlayerInRange(detectRange, true, "感知范围内?");

            BTCondition hasWaypoints = new BTCondition("有巡逻点?",
                () => waypoints != null && waypoints.Length > 0);

            BTCondition hasSeenPlayer = new BTCondition("曾看到过玩家?",
                () => _behaviourTree != null && _behaviourTree.Board.HasSeenPlayer);

            // 调查用的"不在感知范围"条件 — 加了 2m 缓冲区防止边界抖动
            // 例如 detectRange=10，缓冲后实际阈值=12
            // 调查一旦开始，只有玩家跑到 12m 外才会中断，不会在 10m 边界反复切换
            BTCondition notInDetectRange = new BTCondition("不在感知范围?",
                () => Vector3.Distance(transform.position, playerTransform.position) > detectRange + 2f);

            BTCondition chaseStaminaEnough = new BTCondition("追击体力够?",
                () => _behaviourTree != null && _behaviourTree.Board.ChaseStamina > 0f);

            BTCondition chaseStaminaLow = new BTCondition("追击体力不足?",
                () => _behaviourTree != null && _behaviourTree.Board.ChaseStamina <= 0f);

            // ==========================================
            //  叶子节点：行为
            // ==========================================

            BTAttack attackAction = new BTAttack("攻击");
            BTChase chaseAction = new BTChase("追击");
            BTPatrol patrolAction = new BTPatrol(waypoints, "巡逻");
            BTInvestigate investigateAction = new BTInvestigate("调查");
            BTIdle idleAction = new BTIdle("休息", true);

            // ==========================================
            //  组合节点：构建各分支
            // ==========================================

            // --- 攻击分支 ---
            BTSequence attackBranch = new BTSequence("⚔ 攻击分支")
                .AddChild(isPlayerInAttackRange)
                .AddChild(attackAction);

            // --- 追击分支（内嵌 Selector！）---
            // 这是最复杂的分支，展示了多层嵌套：
            // Sequence 里包含一个 Selector，Selector 里又有自己的 Sequence
            BTSequence normalChase = new BTSequence("追击中")
                .AddChild(isPlayerInDetectRange)
                .AddChild(chaseStaminaEnough)
                .AddChild(new BTChaseAction("追击移动", chaseStaminaCost)); // 追击时消耗体力

            BTSequence staminaExhausted = new BTSequence("体力耗尽")
                .AddChild(isPlayerInDetectRange)
                .AddChild(chaseStaminaLow)
                .AddChild(new BTIdle("原地喘气", true)); // 喘气时也缓慢恢复体力

            BTSelector chaseDecision = new BTSelector("追击决策")
                .AddChild(normalChase)       // 优先：体力够就正常追
                .AddChild(staminaExhausted); // 兜底：体力不够就原地喘气

            // 追击分支整体 = 选择追击方式 + 更新记忆
            BTSequence chaseBranch = new BTSequence("🏃 追击分支")
                .AddChild(chaseDecision);

            // --- 调查分支 ---
            // 曾经看到过玩家 + 现在不在感知范围 = 追丢了，去调查
            BTSequence investigateBranch = new BTSequence("🔍 调查分支")
                .AddChild(hasSeenPlayer)
                .AddChild(notInDetectRange)
                .AddChild(investigateAction);

            // --- 巡逻分支 ---
            // 加了"不在调查中"条件：调查进行时，巡逻不能抢占
            BTCondition notInvestigating = new BTCondition("不在调查中?",
                () => !_behaviourTree.Board.IsInvestigating);

            BTSequence patrolBranch = new BTSequence("🚶 巡逻分支")
                .AddChild(hasWaypoints)
                .AddChild(notInvestigating)
                .AddChild(patrolAction);

            // ==========================================
            //  根节点：Selector（从左到右优先级递减）
            // ==========================================

            BTSelector rootSelector = new BTSelector("🌳 根选择器")
                .AddChild(attackBranch)       // P1: 能攻击就攻击
                .AddChild(chaseBranch)        // P2: 能追就追
                .AddChild(investigateBranch)  // P3: 追丢了去调查
                .AddChild(patrolBranch)       // P4: 都不行就巡逻
                .AddChild(idleAction);        // P5: 兜底休息

            // 创建行为树
            _behaviourTree = new BehaviourTree(transform, rootSelector);

            // 配置黑板
            _behaviourTree.Board.PlayerTransform = playerTransform;
            _behaviourTree.Board.DetectRange = detectRange;
            _behaviourTree.Board.AttackRange = attackRange;
            _behaviourTree.Board.MoveSpeed = moveSpeed;
            _behaviourTree.Board.TurnSpeed = turnSpeed;
            _behaviourTree.Board.MaxChaseStamina = 100f;
            _behaviourTree.Board.ChaseStamina = 100f;
            _behaviourTree.Board.StaminaRecoveryRate = staminaRecovery;
            _behaviourTree.Board.IdleDuration = idleDuration;

            Debug.Log("[守卫AI v2] 行为树构建完成！");
            PrintTreeStructure();
        }

        void Update()
        {
            if (_behaviourTree == null) return;

            // 每帧更新记忆：感知范围内就记录位置
            if (playerTransform != null &&
                Vector3.Distance(transform.position, playerTransform.position) <= detectRange)
            {
                _behaviourTree.Board.HasSeenPlayer = true;
                _behaviourTree.Board.LastKnownPlayerPosition = playerTransform.position;
            }

            _behaviourTree.Tick();

            // 调试：打印每个分支的状态
            DebugBranchStates();
        }

        /// <summary>
        /// 调试用：打印根 Selector 各子分支的状态
        /// </summary>
        void DebugBranchStates()
        {
            var root = _behaviourTree.Root;
            if (!(root is BTComposite rootComposite)) return;

            string line = "[分支状态] ";
            string[] names = { "攻击", "追击", "调查", "巡逻", "待机" };
            for (int i = 0; i < rootComposite.Children.Count && i < names.Length; i++)
            {
                var child = rootComposite.Children[i];
                string state = "—";
                if (child.TickedThisFrame)
                {
                    state = child.ThisFrameResult switch
                    {
                        NodeState.Success => "✓",
                        NodeState.Running => "▶",
                        NodeState.Failure => "✗",
                        _ => "?"
                    };
                }
                line += $"{names[i]}={state} ";
            }
            Debug.Log(line);
        }

        /// <summary>
        /// 打印行为树结构到控制台
        /// </summary>
        private void PrintTreeStructure()
        {
            Debug.Log("[守卫AI v2] 行为树结构:");
            Debug.Log("[守卫AI v2]   🌳 根选择器 (Selector)");
            Debug.Log("[守卫AI v2]   ├── ⚔ 攻击分支: 攻击范围内? → 攻击");
            Debug.Log("[守卫AI v2]   ├── 🏃 追击分支: (Selector)");
            Debug.Log("[守卫AI v2]   │   ├── 追击中: 感知范围? + 体力够? → 追击(耗体力)");
            Debug.Log("[守卫AI v2]   │   └── 体力耗尽: 感知范围? + 体力不足? → 原地喘气");
            Debug.Log("[守卫AI v2]   ├── 🔍 调查分支: 看到过? + 不在感知? → 去最后目击点搜索");
            Debug.Log("[守卫AI v2]   ├── 🚶 巡逻分支: 有巡逻点? → 巡逻");
            Debug.Log("[守卫AI v2]   └── 💤 待机: 休息并恢复体力");
        }

        // ========== Scene 视图绘制 ==========
        void OnDrawGizmosSelected()
        {
            Transform t = transform;

            // 感知范围（黄色）
            Gizmos.color = new Color(1f, 1f, 0f, 0.15f);
            Gizmos.DrawSphere(t.position, detectRange);
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(t.position, detectRange);

            // 攻击范围（红色）
            Gizmos.color = new Color(1f, 0f, 0f, 0.25f);
            Gizmos.DrawSphere(t.position, attackRange);
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(t.position, attackRange);

            // 巡逻路线
            if (waypoints != null && waypoints.Length > 1)
            {
                Gizmos.color = Color.cyan;
                for (int i = 0; i < waypoints.Length; i++)
                {
                    if (waypoints[i] == null) continue;
                    int next = (i + 1) % waypoints.Length;
                    if (waypoints[next] != null)
                        Gizmos.DrawLine(waypoints[i].position, waypoints[next].position);
                    Gizmos.DrawWireSphere(waypoints[i].position, 0.3f);
                }
            }

            // 最后目击位置（白色标记）
            if (_behaviourTree != null && _behaviourTree.Board.HasSeenPlayer)
            {
                Gizmos.color = Color.white;
                Gizmos.DrawWireSphere(_behaviourTree.Board.LastKnownPlayerPosition, 0.5f);
                Gizmos.DrawLine(t.position, _behaviourTree.Board.LastKnownPlayerPosition);
            }
        }
    }

    /// <summary>
    /// 【追击+体力消耗行为】
    ///
    /// 和普通追击一样，但每帧消耗追击体力。
    /// 体力耗尽后会被上层 Selector 切换到"喘气"行为。
    /// </summary>
    public class BTChaseAction : BTNode
    {
        private float _staminaCostPerSecond;

        public BTChaseAction(string name = "ChaseAction", float staminaCost = 15f) : base(name)
        {
            _staminaCostPerSecond = staminaCost;
        }

        public override NodeState Tick()
        {
            if (_tree == null || _tree.Board.PlayerTransform == null)
            {
                _state = NodeState.Failure;
                MarkTicked(_state);
                return _state;
            }

            Transform owner = _tree.Owner;
            Transform player = _tree.Board.PlayerTransform;

            // 消耗体力
            _tree.Board.ChaseStamina -= _staminaCostPerSecond * Time.deltaTime;

            // 朝玩家移动
            Vector3 direction = player.position - owner.position;
            float distance = direction.magnitude;

            // 面朝玩家
            if (direction.sqrMagnitude > 0.01f)
            {
                Quaternion targetRot = Quaternion.LookRotation(direction);
                owner.rotation = Quaternion.RotateTowards(
                    owner.rotation, targetRot,
                    _tree.Board.TurnSpeed * Time.deltaTime);
            }

            // 移动
            owner.position = Vector3.MoveTowards(
                owner.position, player.position,
                _tree.Board.MoveSpeed * 1.2f * Time.deltaTime);

            _state = NodeState.Running;
            MarkTicked(_state);
            return _state;
        }
    }
}

using UnityEngine;

namespace AIBehaviourTree
{
    /// <summary>
    /// 【守卫AI控制器 — 把所有节点组装成一棵行为树】
    ///
    /// 这个脚本挂载到 NPC 对象上，负责：
    /// 1. 在 Start() 中构建行为树（定义节点和层级关系）
    /// 2. 在 Update() 中每帧调用 Tick() 驱动行为树运行
    ///
    /// 📊 行为树结构图（优先级从高到低）：
    ///
    ///                     Root (Selector)
    ///                    /       |       \
    ///           攻击分支      追击分支     巡逻分支
    ///          (Sequence)    (Sequence)   (Sequence)
    ///          /      \      /      \     /       \
    ///       攻击范围?  攻击  感知范围? 追击 有巡逻点? 巡逻
    ///
    /// 执行逻辑：
    /// 每帧从Root开始 →
    /// 1. 先检查"攻击分支"：玩家在攻击范围内吗？→ 是就攻击
    /// 2. 否则检查"追击分支"：玩家在感知范围内吗？→ 是就追击
    /// 3. 否则执行"巡逻分支"：→ 在路径点之间走来走去
    ///
    /// 使用方法：
    /// 1. 创建一个Cube作为NPC，挂上这个脚本
    /// 2. 创建一个Sphere作为玩家，挂上 Player.cs 脚本
    /// 3. 在NPC场景中创建几个空物体作为巡逻点
    /// 4. 把巡逻点拖到 NPC Inspector 的 Waypoints 数组中
    /// 5. 把玩家对象拖到 Player Transform 槽位（或让它自动查找）
    /// </summary>
    public class BTGuardAI : MonoBehaviour, IBTGuard
    {
        // ========== Inspector 可配置参数 ==========

        [Header("=== 目标设置 ===")]
        [Tooltip("要追击的玩家对象（如果不设置，会自动查找带 Player 标签的对象）")]
        public Transform playerTransform;

        [Header("=== 感知参数 ===")]
        [Tooltip("NPC 能发现玩家的最大距离")]
        public float detectRange = 10f;

        [Tooltip("NPC 发起攻击的最大距离")]
        public float attackRange = 2f;

        [Header("=== 移动参数 ===")]
        [Tooltip("NPC 移动速度")]
        public float moveSpeed = 3.5f;

        [Tooltip("NPC 旋转速度（度/秒）")]
        public float turnSpeed = 720f;

        [Header("=== 巡逻路径点 ===")]
        [Tooltip("巡逻路径点列表（按顺序设置）")]
        public Transform[] waypoints;

        [Header("=== 调试 ===")]
        [Tooltip("是否在控制台输出行为树的日志")]
        public bool enableDebugLog = true;

        // 行为树实例
        private BehaviourTree _behaviourTree;

        // 【调试用】公开行为树引用，让 Editor 窗口可以读取树的状态
        public BehaviourTree BehaviourTree => _behaviourTree;

        void Start()
        {
            // 如果没有设置玩家，自动查找场景中标记为 "Player" 的对象
            if (playerTransform == null)
            {
                GameObject playerObj = GameObject.FindWithTag("Player");
                if (playerObj != null)
                {
                    playerTransform = playerObj.transform;
                }
                else
                {
                    Debug.LogWarning("[守卫AI] 没有找到玩家！请在Inspector中设置Player Transform，或给玩家对象设置Tag为Player");
                }
            }

            // ========== 🌳 构建行为树！==========
            // 这是最关键的部分 — 定义AI的"思考逻辑"
            BuildBehaviourTree();
        }

        /// <summary>
        /// 【构建行为树 — AI的"大脑"在这里定义】
        ///
        /// 代码结构直接对应行为树的树形结构：
        /// - AddChild() 创建父子关系
        /// - Sequence 代表"AND"（条件+动作的组合）
        /// - Selector 代表"OR"（多个方案中选一个）
        /// </summary>
        private void BuildBehaviourTree()
        {
            // ------ 第一层：创建三种行为 ------

            // 攻击行为节点
            BTAttack attackAction = new BTAttack("攻击");

            // 追击行为节点
            BTChase chaseAction = new BTChase("追击");

            // 巡逻行为节点
            BTPatrol patrolAction = new BTPatrol(waypoints, "巡逻");

            // ------ 第二层：为每种行为创建"前置条件" ------

            // 条件：玩家在攻击范围内？
            BTIsPlayerInRange isPlayerInAttackRange =
                new BTIsPlayerInRange(attackRange, false, "攻击范围内?");

            // 条件：玩家在感知范围内？
            BTIsPlayerInRange isPlayerInDetectRange =
                new BTIsPlayerInRange(detectRange, true, "感知范围内?");

            // 条件：玩家不在攻击范围内？（防止追击分支抢了攻击分支的活）
            // ⚠️ 这个条件非常关键！没有它，追击→追到→回到巡逻的bug就会出现
            // 原因：Selector的resume机制不会回头检查高优先级分支，
            // 如果追击分支在攻击范围内还返回Running，攻击分支永远没机会执行。
            // 加上这个条件后，追击分支在攻击范围内会直接Failure，
            // Selector重置_currentChildIndex=0，下一帧就能检查到攻击分支了。
            BTCondition notInAttackRange = new BTCondition("不在攻击范围?",
                () => Vector3.Distance(transform.position, playerTransform.position) > attackRange);

            // 条件：是否有巡逻点？（简单的非空检查）
            // 这里我们用一个简单的内联条件节点
            BTCondition hasWaypoints = new BTCondition("有巡逻点?",
                () => waypoints != null && waypoints.Length > 0);

            // ------ 第三层：组合成 Sequence（条件 AND 动作） ------

            // 攻击分支 = 条件(在攻击范围内) AND 动作(攻击)
            BTSequence attackBranch = new BTSequence("攻击分支")
                .AddChild(isPlayerInAttackRange)
                .AddChild(attackAction);

            // 追击分支 = 条件(在感知范围内 AND 不在攻击范围内) AND 动作(追击)
            // ⚠️ "不在攻击范围内"是关键：保证追到脸时追击分支失败，
            // Selector才能重新从头检查，让攻击分支有机会执行
            BTSequence chaseBranch = new BTSequence("追击分支")
                .AddChild(isPlayerInDetectRange)
                .AddChild(notInAttackRange)
                .AddChild(chaseAction);

            // 巡逻分支 = 条件(有巡逻点) AND 动作(巡逻)
            BTSequence patrolBranch = new BTSequence("巡逻分支")
                .AddChild(hasWaypoints)
                .AddChild(patrolAction);

            // ------ 第四层：用 Selector 组合所有分支（OR逻辑） ------
            //
            // ⚠️ 顺序很重要！越左边优先级越高：
            // 攻击 > 追击 > 巡逻

            BTSelector rootSelector = new BTSelector("根选择器")
                .AddChild(attackBranch)    // 优先级最高：能攻击就攻击
                .AddChild(chaseBranch)     // 其次：能追击就追击
                .AddChild(patrolBranch);   // 最低：都没辙就巡逻

            // ------ 创建行为树 ------
            _behaviourTree = new BehaviourTree(transform, rootSelector);

            // 配置黑板参数
            _behaviourTree.Board.PlayerTransform = playerTransform;
            _behaviourTree.Board.DetectRange = detectRange;
            _behaviourTree.Board.AttackRange = attackRange;
            _behaviourTree.Board.MoveSpeed = moveSpeed;
            _behaviourTree.Board.TurnSpeed = turnSpeed;

            Debug.Log("[守卫AI] 行为树构建完成！");
            Debug.Log("[守卫AI] 行为树结构:");
            Debug.Log("[守卫AI]   Root (Selector)");
            Debug.Log("[守卫AI]   ├── 攻击分支 (Sequence): 攻击范围内? → 攻击");
            Debug.Log("[守卫AI]   ├── 追击分支 (Sequence): 感知范围内? → 追击");
            Debug.Log("[守卫AI]   └── 巡逻分支 (Sequence): 有巡逻点? → 巡逻");
        }

        void Update()
        {
            // 每帧驱动行为树 — 这就是AI"思考"的时刻！
            if (_behaviourTree != null)
            {
                _behaviourTree.Tick();
            }
        }

        // ========== 调试用：在Scene视图中画出感知范围和攻击范围 ==========
        void OnDrawGizmosSelected()
        {
            if (_behaviourTree == null) return;

            Transform t = transform;

            // 画感知范围（黄色）
            Gizmos.color = new Color(1f, 1f, 0f, 0.2f); // 半透明黄色
            Gizmos.DrawSphere(t.position, detectRange);
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(t.position, detectRange);

            // 画攻击范围（红色）
            Gizmos.color = new Color(1f, 0f, 0f, 0.3f); // 半透明红色
            Gizmos.DrawSphere(t.position, attackRange);
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(t.position, attackRange);

            // 画巡逻路线
            if (waypoints != null && waypoints.Length > 1)
            {
                Gizmos.color = Color.cyan;
                for (int i = 0; i < waypoints.Length; i++)
                {
                    if (waypoints[i] == null) continue;
                    // 连线：当前点到下一个点
                    int next = (i + 1) % waypoints.Length;
                    if (waypoints[next] != null)
                    {
                        Gizmos.DrawLine(waypoints[i].position, waypoints[next].position);
                    }
                    // 画小球标记巡逻点
                    Gizmos.DrawWireSphere(waypoints[i].position, 0.3f);
                }
            }
        }
    }

    /// <summary>
    /// 【内联条件节点 — 简易版】
    ///
    /// 这是一个用 Lambda 表达式快速创建条件节点的方式。
    /// 不需要为每个简单条件都创建一个新类。
    ///
    /// 适合简单的判断，比如"列表是否为空"、"某个值是否大于X"等。
    ///
    /// 用法示例：
    ///   new BTCondition("有巡逻点?", () => waypoints.Length > 0)
    ///   new BTCondition("血量大于0?", () => health > 0)
    /// </summary>
    public class BTCondition : BTNode
    {
        // 条件判断函数：返回 true = Success，返回 false = Failure
        private System.Func<bool> _condition;

        public BTCondition(string name, System.Func<bool> condition) : base(name)
        {
            _condition = condition;
        }

        public override NodeState Tick()
        {
            if (_condition != null && _condition())
            {
                _state = NodeState.Success;
            }
            else
            {
                _state = NodeState.Failure;
            }

            Debug.Log($"[条件] {Name}: {(_state == NodeState.Success ? "✓" : "✗")}");
            MarkTicked(_state);
            return _state;
        }
    }
}

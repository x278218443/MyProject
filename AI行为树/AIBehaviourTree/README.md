# 🌳 AI行为树入门 Demo

Unity 游戏 AI 行为树学习项目，从零实现一个完整的行为树框架，附带详细中文注释和可视化调试工具。

## 📸 效果

| 巡逻 | 追击 | 攻击 | 调查 |
|:---:|:---:|:---:|:---:|
| 在路径点之间巡逻 | 发现玩家后追击 | 近距离攻击 | 追丢了去调查 |

## 🎮 快速开始

1. 用 Unity 2021.3+ 打开本项目
2. 打开 `Assets/Scenes/SampleScene.unity`（或新建场景）
3. 创建地面（Plane，Scale 5,1,5）
4. 创建 NPC（Cube）→ 挂 `BTGuardAI` 或 `BTGuardAI_v2`
5. 创建玩家（Sphere）→ 挂 `Player` → **Tag 设为 `Player`**
6. 创建 4 个空物体作为巡逻点 → 拖入 Guard 的 Waypoints 数组
7. Play！用 WASD 控制玩家移动

## 🧠 行为树是什么？

行为树是一种游戏 AI 决策系统。每帧从根节点开始，根据条件选择不同的行为：

```
Selector（选择）= OR：多个方案选一个，优先级从左到右
Sequence（顺序）= AND：条件+动作的组合，全部成功才算成功
```

### 核心概念

| 概念 | 说明 |
|------|------|
| **Success** ✅ | 节点执行成功 |
| **Failure** ❌ | 节点执行失败 |
| **Running** ⏳ | 节点正在执行中（持续性动作） |
| **Blackboard** | 共享数据存储，节点间通过它通信 |

## 📁 项目结构

```
Assets/Scripts/
├── Core/                          核心框架（可复用到其他项目）
│   ├── BTNode.cs                 节点基类 + 三种状态
│   ├── BTComposite.cs            Sequence(AND) + Selector(OR)
│   ├── BTCooldown.cs             冷却装饰节点
│   └── BTRoot.cs                 行为树容器 + Blackboard 黑板
│
├── Conditions/
│   └── BTIsPlayerInRange.cs      判断玩家是否在指定范围内
│
├── Actions/
│   ├── BTPatrol.cs               巡逻（路径点之间移动）
│   ├── BTChase.cs                追击（朝玩家移动）
│   ├── BTAttack.cs               攻击（持续造成伤害）
│   ├── BTInvestigate.cs          调查（去最后目击位置搜索）
│   └── BTIdle.cs                 待机（休息 + 恢复体力）
│
├── Editor/                        编辑器调试工具
│   ├── BTEditorWindow.cs         调试窗口 (Ctrl+Shift+B)
│   ├── BTGuardAIEditor.cs        Inspector 内嵌状态显示
│   └── BTSceneVisualizer.cs      Scene 视图头顶状态标签
│
├── BTGuardAI.cs                   v1 控制器（简单版：巡逻→追击→攻击）
├── BTGuardAI_v2.cs                v2 控制器（完整版：5层深度+体力+调查）
├── Player.cs                      玩家控制（WASD 移动）
└── 行为树学习指南.txt              完整知识总结文档
```

## 🔧 两个版本对比

### v1 — 入门版 `BTGuardAI`

```
Root (Selector)
├── 攻击分支: 攻击范围内? → 攻击
├── 追击分支: 感知范围内? → 追击
└── 巡逻分支: 有巡逻点? → 巡逻
```

适合理解行为树基础概念。

### v2 — 进阶版 `BTGuardAI_v2`

```
Root (Selector)
├── ⚔ 攻击分支:    攻击范围内? → 攻击
├── 🏃 追击分支:    (体力够?) → 追击(消耗体力) / (体力不足?) → 原地喘气
├── 🔍 调查分支:    看到过玩家? + 不在感知范围? → 去最后目击点搜索
├── 🚶 巡逻分支:    有巡逻点? → 巡逻
└── 💤 待机:        休息 + 恢复体力
```

展示了多层嵌套、装饰节点、记忆系统、体力系统。

## 🛠 调试工具

| 工具 | 打开方式 | 功能 |
|------|----------|------|
| **调试窗口** | `Ctrl+Shift+B` 或菜单 AI行为树 → 行为树调试器 | 实时树形结构 + 节点状态颜色 |
| **Inspector** | 选中 Guard 对象 | 底部显示行为树运行状态 |
| **Scene 标签** | Scene 视图 | NPC 头顶显示当前行为（🟢巡逻 🟡追击 🔴攻击） |

节点状态颜色：
- 🟢 绿色 = Success
- 🔴 红色 = Failure
- 🟡 黄色 = Running（当前正在执行）
- ⬜ 灰色 = 未执行（被高优先级分支跳过）

## ⚡ 运行时参数

### v1 参数（BTGuardAI）

| 参数 | 默认值 | 说明 |
|------|--------|------|
| Detect Range | 10 | 感知范围（米） |
| Attack Range | 2 | 攻击范围（米） |
| Move Speed | 3.5 | 移动速度 |
| Turn Speed | 720 | 旋转速度（度/秒） |

### v2 额外参数（BTGuardAI_v2）

| 参数 | 默认值 | 说明 |
|------|--------|------|
| Chase Stamina Cost | 15 | 追击每秒消耗体力 |
| Stamina Recovery | 20 | 休息时每秒恢复体力 |
| Idle Duration | 3 | 休息持续时间（秒） |

## 📚 学习路线

1. 阅读 `行为树学习指南.txt` 了解核心概念
2. 阅读 `Core/BTNode.cs` 理解节点基类
3. 阅读 `Core/BTComposite.cs` 理解 Sequence 和 Selector
4. 阅读 `BTGuardAI.cs` 理解 v1 行为树组装
5. 用 v1 跑场景，打开调试窗口观察
6. 阅读 `BTGuardAI_v2.cs` 理解多层嵌套
7. 尝试自己加一个新行为（比如：逃跑、打招呼）

## 🔜 可扩展方向

- [ ] 装饰节点：Inverter（反转）、Repeater（重复）
- [ ] Parallel 并行节点：同时执行多个行为
- [ ] 行为树可视化编辑器（拖拽连线）
- [ ] 动画集成（Animator 状态同步）
- [ ] 多 NPC 协作（共享黑板数据）
- [ ] 从 JSON/ScriptableObject 加载行为树配置

## 🛡 项目环境

- Unity 2021.3.14f1
- C# 9.0 / .NET Standard 2.1
- 无第三方依赖

## 📄 License

MIT

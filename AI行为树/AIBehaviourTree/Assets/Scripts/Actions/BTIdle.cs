using UnityEngine;

namespace AIBehaviourTree
{
    /// <summary>
    /// 【待机/休息行为节点】
    ///
    /// 休息一段时间后返回 Success。
    /// 作为 Selector 最后的兜底分支，只在其他分支都失败时才执行。
    /// </summary>
    public class BTIdle : BTNode
    {
        private float _timer = 0f;
        private bool _recoverStamina;

        public BTIdle(string name = "Idle", bool recoverStamina = true) : base(name)
        {
            _recoverStamina = recoverStamina;
        }

        public override NodeState Tick()
        {
            if (_tree == null) return NodeState.Failure;

            _timer += Time.deltaTime;

            // 恢复体力
            if (_recoverStamina && _tree.Board.ChaseStamina < _tree.Board.MaxChaseStamina)
            {
                _tree.Board.ChaseStamina += _tree.Board.StaminaRecoveryRate * Time.deltaTime;
                _tree.Board.ChaseStamina = Mathf.Min(
                    _tree.Board.ChaseStamina,
                    _tree.Board.MaxChaseStamina);
            }

            // 休息够了
            if (_timer >= _tree.Board.IdleDuration)
            {
                Debug.Log("[待机] 休息完毕");
                _timer = 0f;
                _state = NodeState.Success;
                MarkTicked(_state);
                return _state;
            }

            // 还在休息
            _state = NodeState.Running;
            MarkTicked(_state);
            return _state;
        }

        public override void Reset()
        {
            base.Reset();
            _timer = 0f;
        }
    }
}

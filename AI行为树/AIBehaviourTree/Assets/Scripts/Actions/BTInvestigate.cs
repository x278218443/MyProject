using UnityEngine;

namespace AIBehaviourTree
{
    /// <summary>
    /// 【调查行为节点】
    ///
    /// 追丢后去最后目击位置搜索。
    /// 用 IsInvestigating 标记防止巡逻抢占。
    /// </summary>
    public class BTInvestigate : BTNode
    {
        private enum InvestigatePhase
        {
            MovingTo,
            Searching
        }

        private InvestigatePhase _phase = InvestigatePhase.MovingTo;
        private float _arrivalThreshold = 0.5f;
        private float _searchTimer = 0f;
        private float _searchDuration = 3f;
        private float _searchTurnSpeed = 360f;

        // 是否已完成调查（Reset 时据此决定是否清除 IsInvestigating）
        private bool _completed = false;

        public BTInvestigate(string name = "Investigate") : base(name) { }

        public override NodeState Tick()
        {
            if (_tree == null)
            {
                Debug.LogWarning("[调查] _tree == null!");
                return NodeState.Failure;
            }

            Transform owner = _tree.Owner;
            Vector3 targetPos = _tree.Board.LastKnownPlayerPosition;

            NodeState result;
            switch (_phase)
            {
                case InvestigatePhase.MovingTo:
                    result = TickMovingTo(owner, targetPos);
                    break;
                case InvestigatePhase.Searching:
                    result = TickSearching(owner);
                    break;
                default:
                    result = NodeState.Failure;
                    break;
            }

            return result;
        }

        private NodeState TickMovingTo(Transform owner, Vector3 targetPos)
        {
            float distance = Vector3.Distance(owner.position, targetPos);

            if (distance <= _arrivalThreshold)
            {
                Debug.Log("[调查] 到达目击位置，开始搜索");
                _phase = InvestigatePhase.Searching;
                _tree.Board.IsInvestigating = true;
                _searchTimer = 0f;
                _state = NodeState.Running;
                MarkTicked(_state);
                return _state;
            }

            // 标记正在调查，防止巡逻抢占
            _tree.Board.IsInvestigating = true;

            Vector3 direction = targetPos - owner.position;
            Quaternion targetRotation = Quaternion.LookRotation(direction);
            owner.rotation = Quaternion.RotateTowards(
                owner.rotation, targetRotation,
                _tree.Board.TurnSpeed * Time.deltaTime);

            owner.position = Vector3.MoveTowards(
                owner.position, targetPos,
                _tree.Board.MoveSpeed * 0.8f * Time.deltaTime);

            _state = NodeState.Running;
            MarkTicked(_state);
            return _state;
        }

        private NodeState TickSearching(Transform owner)
        {
            // 搜索期间保持标记
            _tree.Board.IsInvestigating = true;
            _searchTimer += Time.deltaTime;

            owner.Rotate(Vector3.up, _searchTurnSpeed * Time.deltaTime);

            if (_searchTimer >= _searchDuration)
            {
                Debug.Log("[调查] 搜索完毕");
                _completed = true;
                _tree.Board.HasSeenPlayer = false;
                _tree.Board.IsInvestigating = false;
                _state = NodeState.Success;
                MarkTicked(_state);
                return _state;
            }

            _state = NodeState.Running;
            MarkTicked(_state);
            return _state;
        }

        /// <summary>
        /// Reset 时只有完成调查才清除 IsInvestigating。
        /// 如果是被外部打断（如 Selector 切换分支），标记保持，
        /// 下次调查恢复时继续生效，巡逻不会抢占。
        /// </summary>
        public override void Reset()
        {
            base.Reset();
            _phase = InvestigatePhase.MovingTo;
            _searchTimer = 0f;
            if (_completed && _tree != null)
            {
                _tree.Board.IsInvestigating = false;
                _completed = false;
            }
        }
    }
}

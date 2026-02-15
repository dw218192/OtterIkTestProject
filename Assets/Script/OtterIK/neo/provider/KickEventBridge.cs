using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    public class KickEventBridge : MonoBehaviour
    {
        public MovementControllerRB movementController;
        public ExpSpineHarmonicProvider harmonicProvider;

        private void OnEnable()
        {
            if (movementController != null)
                movementController.OnKickCycleStart += HandleKick;
        }

        private void OnDisable()
        {
            if (movementController != null)
                movementController.OnKickCycleStart -= HandleKick;
        }

        private void HandleKick(MovementControllerRB.KickCycleEvent evt)
        {
            if (movementController == null || harmonicProvider == null) return;

            // 获取当前运动区域
            MovementControllerRB.MoveZone currentZone = movementController.GetZone();

            // 逻辑判断：仅在 Swim 或 Sprint 模式下触发
            // 原地转圈 (Aim) 时不执行任何操作
            if (currentZone == MovementControllerRB.MoveZone.Swim || 
                currentZone == MovementControllerRB.MoveZone.Sprint)
            {
                harmonicProvider.TriggerHarmonic(evt.demand01);
            }
        }
    }
}
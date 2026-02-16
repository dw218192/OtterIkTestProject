using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    public class KickEventBridge : MonoBehaviour
    {
        public MovementControllerRB movementController;
        public ExpSpineHarmonicProvider harmonicProvider;

        private void OnEnable() { if (movementController != null) movementController.OnKickCycleStart += HandleKick; }
        private void OnDisable() { if (movementController != null) movementController.OnKickCycleStart -= HandleKick; }

        private void HandleKick(MovementControllerRB.KickCycleEvent evt)
        {
            if (movementController == null || harmonicProvider == null) return;

            // 只在有位移意图的 Zone 下触发
            var zone = movementController.GetZone();
            if (zone == MovementControllerRB.MoveZone.Swim || zone == MovementControllerRB.MoveZone.Sprint)
            {
                harmonicProvider.TriggerSyncedHarmonic(evt.cycleDuration, evt.demand01);
            }
        }
    }
}
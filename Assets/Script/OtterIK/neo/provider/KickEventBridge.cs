using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    public class KickEventBridge : MonoBehaviour
    {
        public CrestMovementControllerRB movementController;
        [Header("Kick Wave Packet (optional)")]
        public ExpSpineKickWavePacketProvider kickWaveProvider;

        public enum KickWaveDurationSource
        {
            CycleDuration,   // prepare + kick (from event)
            PrepareTime,     // from controller getter (seconds)
            KickTime,        // from controller getter (seconds)
        }

        [Tooltip("Which time value drives the wave packet duration (duration controls propagation speed).")]
        public KickWaveDurationSource kickWaveDurationSource = KickWaveDurationSource.CycleDuration;

        [Tooltip("duration = baseTime * scale + offset")]
        public float kickWaveDurationScale = 1.0f;
        public float kickWaveDurationOffset = 0.0f;

        [Tooltip("Clamp the final duration in seconds.")]
        public Vector2 kickWaveDurationClamp = new Vector2(0.05f, 1.20f);

        [Tooltip("Optional: remap baseTime (seconds) -> duration (seconds) using a curve. If enabled, scale/offset still apply after remap.")]
        public bool useKickWaveDurationRemapCurve = false;
        public AnimationCurve kickWaveDurationRemap = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        private void OnEnable() { if (movementController != null) movementController.OnKickCycleStart += HandleKick; }
        private void OnDisable() { if (movementController != null) movementController.OnKickCycleStart -= HandleKick; }

        private void HandleKick(CrestMovementControllerRB.KickCycleEvent evt)
        {
            if (movementController == null) return;

            // 只在有位移意图的 Zone 下触发
            var zone = movementController.GetZone();
            if (zone == CrestMovementControllerRB.MoveZone.Swim || zone == CrestMovementControllerRB.MoveZone.Sprint)
            {
                if (kickWaveProvider != null)
                {
                    float baseTime = evt.cycleDuration;
                    switch (kickWaveDurationSource)
                    {
                        case KickWaveDurationSource.PrepareTime: baseTime = movementController.GetPrepareTime(); break;
                        case KickWaveDurationSource.KickTime:    baseTime = movementController.GetKickTime();    break;
                        case KickWaveDurationSource.CycleDuration:
                        default: baseTime = evt.cycleDuration; break;
                    }

                    if (useKickWaveDurationRemapCurve && kickWaveDurationRemap != null)
                        baseTime = kickWaveDurationRemap.Evaluate(Mathf.Max(0f, baseTime));

                    float dur =
                        baseTime * kickWaveDurationScale
                        + kickWaveDurationOffset;

                    dur = Mathf.Clamp(
                        dur,
                        Mathf.Max(0.01f, kickWaveDurationClamp.x),
                        Mathf.Max(kickWaveDurationClamp.x, kickWaveDurationClamp.y)
                    );

                    kickWaveProvider.TriggerKick(evt.demand01, dur);
                }
            }
        }
    }
}
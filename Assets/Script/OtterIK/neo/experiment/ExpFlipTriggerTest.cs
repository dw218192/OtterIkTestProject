using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    [DisallowMultipleComponent]
    public class ExpFlipTriggerTest : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("Assign the ExpSpineFlipRollProvider you want to trigger.")]
        public ExpSpineFlipRollProvider flipProvider;

        [Header("Input")]
        public KeyCode triggerKey = KeyCode.F;

        [Tooltip("If true, triggers once on key down.")]
        public bool triggerOnKeyDown = true;

        [Tooltip("If true, also triggers repeatedly while key is held (not recommended for flips).")]
        public bool triggerWhileHeld = false;

        [Tooltip("Min seconds between triggers (prevents spamming).")]
        [Range(0f, 2f)]
        public float cooldownSeconds = 0.15f;

        [Header("Debug")]
        public bool drawTargetDot = true;

        [Range(0.005f, 0.25f)]
        public float targetDotSize = 0.06f;

        public Color targetDotColor = new Color(1f, 0.25f, 0.25f, 0.95f);

        private float _nextAllowedTime;

        private void Reset()
        {
            flipProvider = GetComponent<ExpSpineFlipRollProvider>();
            triggerKey = KeyCode.F;
            triggerOnKeyDown = true;
            triggerWhileHeld = false;
            cooldownSeconds = 0.15f;

            drawTargetDot = true;
            targetDotSize = 0.06f;
            targetDotColor = new Color(1f, 0.25f, 0.25f, 0.95f);
        }

        private void Awake()
        {
            if (flipProvider == null)
                flipProvider = GetComponent<ExpSpineFlipRollProvider>();
        }

        private void Update()
        {
            if (flipProvider == null) return;

            if (drawTargetDot)
                DrawDebugDot(flipProvider.transform.position, targetDotSize, targetDotColor);

            bool wantTrigger = false;

            if (triggerOnKeyDown && Input.GetKeyDown(triggerKey))
                wantTrigger = true;

            if (triggerWhileHeld && Input.GetKey(triggerKey))
                wantTrigger = true;

            if (!wantTrigger) return;

            if (Time.time < _nextAllowedTime) return;
            _nextAllowedTime = Time.time + Mathf.Max(0f, cooldownSeconds);

            flipProvider.TriggerFlip();
        }

        /// <summary>
        /// For UI Button OnClick or other scripts.
        /// </summary>
        public void Trigger()
        {
            if (flipProvider == null) return;

            if (Time.time < _nextAllowedTime) return;
            _nextAllowedTime = Time.time + Mathf.Max(0f, cooldownSeconds);

            flipProvider.TriggerFlip();
        }

        private static void DrawDebugDot(Vector3 p, float size, Color c)
        {
            // Debug.DrawPoint doesn't exist; approximate a dot with a tiny 3-axis cross.
            float s = Mathf.Max(0.0001f, size) * 0.5f;
            Debug.DrawLine(p - Vector3.right * s, p + Vector3.right * s, c);
            Debug.DrawLine(p - Vector3.up * s, p + Vector3.up * s, c);
            Debug.DrawLine(p - Vector3.forward * s, p + Vector3.forward * s, c);
        }
    }
}


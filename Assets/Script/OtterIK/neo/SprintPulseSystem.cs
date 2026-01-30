using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Event-driven spine pulse system (K/I from MovementControllerRB kick cycle).
/// - Each kick cycle triggers a pulse window:
///   * Active oscillation during K (prepare+kick)
///   * Return-to-zero during (I - residualTime)
///   * Optional residual idle gap of residualTime (0 => continuous)
/// - Still allows sine frequency via oscillationsPerCycle (default 1, set 2 for "double bump").
/// - NO rotational drift: we snapshot per-bone base rotation each LateUpdate and apply delta on top.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(450)] // after most motion controllers; before IK solvers that read final pose if needed
public class SprintPulseSystem : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MovementControllerRB movement;

    [Tooltip("Ordered from front to back. Example: spine1, spine2, spine3, subneck, neck.")]
    [SerializeField] private Transform[] spineBones;

    [Header("Pulse timing (event-driven)")]
    [Tooltip("How much 'quiet time' remains after the return-to-zero completes. 0 = next pulse connects immediately.")]
    [SerializeField] private float residualTime = 0.00f;

    [Tooltip("How many sine oscillations happen during ONE cycleDuration (K). Default 1.")]
    [SerializeField] private float oscillationsPerCycle = 1.0f;

    [Header("Pulse shape")]
    [Tooltip("Peak amplitude (degrees) when demand is 1 and alongChain is 1.")]
    [SerializeField] private float intensityDeg = 6.0f;

    [Tooltip("Minimum visible amplitude (degrees) even at low demand. 0 disables.")]
    [SerializeField] private float minIdleIntensityDeg = 0.8f;

    [Tooltip("Demand(0..1) -> intensity scale(0..1). Default: linear.")]
    [SerializeField] private AnimationCurve demandToIntensity = AnimationCurve.Linear(0, 0, 1, 1);

    [Tooltip("Along-chain gain. x=0(front) .. x=1(back). Default: slightly stronger at back.")]
    [SerializeField] private AnimationCurve alongChain = new AnimationCurve(
        new Keyframe(0f, 0.35f),
        new Keyframe(1f, 1.10f)
    );

    [Tooltip("Spatial phase travel along chain (cycles across the whole spine). 0 = all bones in-phase, 1 = one full wave across chain.")]
    [SerializeField] private float travelCycles = 0.75f;

    [Header("Axes (local)")]
    [SerializeField] private bool applyPitch = true;   // local X
    [SerializeField] private bool applyYaw = false;    // local Y
    [SerializeField] private bool applyRoll = false;   // local Z

    [Header("Blending")]
    [Tooltip("How strongly we apply the delta each frame (higher = tighter).")]
    [SerializeField] private float deltaSharpness = 18f;

    [Tooltip("Master multiplier for the whole system.")]
    [Range(0f, 2f)]
    [SerializeField] private float masterWeight = 1f;

    [Header("Debug")]
    [SerializeField] private bool debugLog = false;

    // ------------ runtime ------------

    private struct Pulse
    {
        public float startTime;
        public float K;          // cycleDuration
        public float I;          // intervalDuration
        public float demand01;
        public float oscPerCycle;
        public float travelCycles;
        public float residualTime;
    }

    private readonly List<Pulse> _pulses = new List<Pulse>(16);
    private Quaternion[] _baseLocalThisFrame;
    private Quaternion[] _bindLocalRot;  // Captured at Awake for safety recovery
    private float[] _debugDegPerBone;

    public IReadOnlyList<float> DebugDegreesPerBone => _debugDegPerBone;

    private void Reset()
    {
        movement = GetComponentInParent<MovementControllerRB>();
    }

    private void Awake()
    {
        EnsureArrays();
        CaptureBindPoses();
    }

    private void OnEnable()
    {
        EnsureArrays();
        CaptureBindPoses();
        if (movement != null)
            movement.OnKickCycleStart += OnKickCycleStart;
    }

    private void OnDisable()
    {
        if (movement != null)
            movement.OnKickCycleStart -= OnKickCycleStart;

        _pulses.Clear();

        // Safety recovery: restore bind poses to prevent runaway rotations
        RestoreBindPoses();
    }

    private void EnsureArrays()
    {
        int n = spineBones != null ? spineBones.Length : 0;
        if (n <= 0) return;

        if (_baseLocalThisFrame == null || _baseLocalThisFrame.Length != n)
            _baseLocalThisFrame = new Quaternion[n];

        if (_bindLocalRot == null || _bindLocalRot.Length != n)
            _bindLocalRot = new Quaternion[n];

        if (_debugDegPerBone == null || _debugDegPerBone.Length != n)
            _debugDegPerBone = new float[n];
    }

    private void CaptureBindPoses()
    {
        if (spineBones == null) return;
        EnsureArrays();

        for (int i = 0; i < spineBones.Length; i++)
        {
            var bone = spineBones[i];
            if (bone != null)
                _bindLocalRot[i] = bone.localRotation;
            else
                _bindLocalRot[i] = Quaternion.identity;
        }
    }

    private void RestoreBindPoses()
    {
        if (spineBones == null || _bindLocalRot == null) return;

        int n = Mathf.Min(spineBones.Length, _bindLocalRot.Length);
        for (int i = 0; i < n; i++)
        {
            if (spineBones[i] != null)
                spineBones[i].localRotation = _bindLocalRot[i];
        }
    }

    private void OnKickCycleStart(MovementControllerRB.KickCycleEvent e)
    {
        float K = Mathf.Max(1e-4f, e.cycleDuration);
        float I = Mathf.Max(0f, e.intervalDuration);

        // residualTime is constrained within [0, I]
        float res = Mathf.Clamp(residualTime, 0f, I);

        var p = new Pulse
        {
            startTime = Time.time,
            K = K,
            I = I,
            demand01 = Mathf.Clamp01(e.demand01),
            oscPerCycle = Mathf.Max(0.01f, oscillationsPerCycle),
            travelCycles = travelCycles,
            residualTime = res
        };

        _pulses.Add(p);

        if (debugLog)
            Debug.Log($"[ottersprintpulsesystem] KickCycleStart K={K:0.000}s I={I:0.000}s demand={p.demand01:0.00} osc={p.oscPerCycle:0.##} res={res:0.000}s");
    }

    /// <summary>
    /// Manual trigger for tests: specify K/I/demand and optionally override osc/travel/residual.
    /// </summary>
    public void TriggerManualPulse(
        float cycleDuration,
        float intervalDuration,
        float demand01 = 1f,
        float? oscPerCycleOverride = null,
        float? travelCyclesOverride = null,
        float? residualTimeOverride = null
    )
    {
        float K = Mathf.Max(1e-4f, cycleDuration);
        float I = Mathf.Max(0f, intervalDuration);
        float res = Mathf.Clamp(residualTimeOverride ?? residualTime, 0f, I);

        _pulses.Add(new Pulse
        {
            startTime = Time.time,
            K = K,
            I = I,
            demand01 = Mathf.Clamp01(demand01),
            oscPerCycle = Mathf.Max(0.01f, oscPerCycleOverride ?? oscillationsPerCycle),
            travelCycles = travelCyclesOverride ?? travelCycles,
            residualTime = res
        });
    }

    private void LateUpdate()
    {
        if (masterWeight <= 0f) return;
        if (spineBones == null || spineBones.Length == 0) return;

        EnsureArrays();

        int n = spineBones.Length;

        // Snapshot base rotations for THIS frame (prevents drift, preserves upstream systems).
        for (int i = 0; i < n; i++)
        {
            var b = spineBones[i];
            _baseLocalThisFrame[i] = (b != null) ? b.localRotation : Quaternion.identity;
            if (_debugDegPerBone != null) _debugDegPerBone[i] = 0f;
        }

        if (_pulses.Count == 0) return;

        float now = Time.time;
        float dt = Time.deltaTime;
        float blendK = 1f - Mathf.Exp(-Mathf.Max(0f, deltaSharpness) * Mathf.Max(1e-5f, dt));

        // Remove expired pulses (safe to do backwards).
        for (int p = _pulses.Count - 1; p >= 0; p--)
        {
            var pulse = _pulses[p];
            float age = now - pulse.startTime;
            float total = pulse.K + pulse.I;
            if (age >= total + 0.02f) // small grace
                _pulses.RemoveAt(p);
        }

        // Apply summed delta per bone (from all active pulses).
        for (int i = 0; i < n; i++)
        {
            Transform bone = spineBones[i];
            if (bone == null) continue;

            // === DEBUG FOR CHEST ===
            bool isChest = bone.name.ToLower().Contains("chest") || bone.name.ToLower().Contains("spine3");
            Quaternion preSnapshot = bone.localRotation;  // BEFORE any changes this frame (but after upstream systems)
            // === END DEBUG ===

            float x01 = (n <= 1) ? 0f : (i / (float)(n - 1)); // front->back
            float chainGain = Mathf.Max(0f, alongChain.Evaluate(x01));

            // Compute total angle for this bone (degrees).
            float totalDeg = 0f;

            for (int p = 0; p < _pulses.Count; p++)
            {
                var pulse = _pulses[p];
                float age = now - pulse.startTime;

                // Window:
                //  - oscillate during [0, K]
                //  - return to zero during [K, K + (I - residual)]
                //  - then hold at zero for residual time (if any)
                float K = pulse.K;
                float I = pulse.I;
                float res = pulse.residualTime;

                if (age < 0f) continue;

                float returnWindow = Mathf.Max(0f, I - res);
                float wReturn = 0f;

                if (age <= K)
                {
                    wReturn = 1f;
                }
                else if (returnWindow > 1e-4f && age <= K + returnWindow)
                {
                    float u = Mathf.Clamp01((age - K) / returnWindow);
                    // smoothstep down to zero
                    wReturn = 1f - (u * u * (3f - 2f * u));
                }
                else
                {
                    wReturn = 0f;
                }

                if (wReturn <= 1e-5f) continue;

                // Demand -> intensity scale
                float s = Mathf.Clamp01(demandToIntensity.Evaluate(pulse.demand01));
                float amp = Mathf.Lerp(minIdleIntensityDeg, intensityDeg, s);

                // Core sinusoid inside K (normalized time for phase)
                float t01 = Mathf.Clamp01(age / K);

                // Envelope to guarantee clean zero at ends even when osc not integer:
                // 0 -> 1 -> 0 across the cycle
                float env = Mathf.Sin(Mathf.PI * t01); // [0..1] bell-ish

                // Phase: oscillations in time + travel along chain
                float phase = 2f * Mathf.PI * (pulse.oscPerCycle * t01 - pulse.travelCycles * x01);
                float osc = Mathf.Sin(phase);

                totalDeg += amp * chainGain * env * osc * wReturn;
            }

            totalDeg *= masterWeight;

            if (_debugDegPerBone != null) _debugDegPerBone[i] = totalDeg;

            // Build delta rotation in local space
            Quaternion delta = Quaternion.identity;
            if (applyPitch) delta = Quaternion.AngleAxis(totalDeg, Vector3.right) * delta;
            if (applyYaw)   delta = Quaternion.AngleAxis(totalDeg, Vector3.up) * delta;
            if (applyRoll)  delta = Quaternion.AngleAxis(totalDeg, Vector3.forward) * delta;

            Quaternion baseLocal = _baseLocalThisFrame[i];
            Quaternion targetLocal = delta * baseLocal;

            // Apply directly (no slerp) - the snapshot already provides a stable base each frame.
            // This prevents accumulation drift while still respecting upstream systems.
            bone.localRotation = targetLocal;

            // === DEBUG FOR CHEST ===
            if (isChest)
            {
                Quaternion postWrite = bone.localRotation;
                Debug.Log($"[Chest Debug] Frame={Time.frameCount} Bone={bone.name} Index={i}/{n-1}\n" +
                    $"  PRE-snapshot (at LateUpdate start): {preSnapshot.eulerAngles}\n" +
                    $"  Snapshot (_baseLocalThisFrame): {_baseLocalThisFrame[i].eulerAngles}\n" +
                    $"  Bind (_bindLocalRot): {_bindLocalRot[i].eulerAngles}\n" +
                    $"  TotalDeg: {totalDeg:F3}°\n" +
                    $"  Delta quat: {delta.eulerAngles}\n" +
                    $"  Target local: {targetLocal.eulerAngles}\n" +
                    $"  POST-write: {postWrite.eulerAngles}\n" +
                    $"  Active pulses: {_pulses.Count}\n" +
                    $"  ChainGain (x01={x01:F2}): {chainGain:F3}");
            }
            // === END DEBUG ===
        }
    }
}

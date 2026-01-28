using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class SprintPulseSystem : MonoBehaviour
{
    [Serializable]
    public class Pulse
    {
        public float startTime;
        public float amplitudeDeg;  // degrees
        public float frequencyHz;   // Hz
        public float decayPerSec;   // 1/sec
        public float travelCycles;  // cycles per normalizedIndex (phase travel)
    }

    [Header("References")]
    public MovementControllerRB movement;   // event source
    public SpineDelayChain delayChain;      // chain nodes
    [Tooltip("Optional: your spine driver, if you later want to scale roll based on distance factor.")]
    public SoftSpineDriving spineDriver;

    [Header("Pulse Defaults")]
    public float baseAmplitudeDeg = 8f;
    public float baseFrequencyHz = 3.0f;
    public float baseDecayPerSec = 2.5f;
    public float baseTravelCycles = 1.2f;

    [Header("Event Mapping")]
    [Tooltip("Amplitude multiplier based on demand01 (0..1).")]
    public AnimationCurve demandToAmplitude = AnimationCurve.Linear(0f, 0.25f, 1f, 1f);

    [Tooltip("Frequency multiplier relative to 1/cycleDuration from KickCycleEvent.")]
    public float frequencyMul = 1.0f;

    [Tooltip("Extra decay when demand is low (makes weak kicks fade sooner).")]
    public float lowDemandExtraDecay = 1.0f;

    [Header("Apply Axes")]
    [Tooltip("Apply wave around local X (pitch) - recommended for dolphin/leech-like sprint.")]
    public bool applyPitch = true;

    [Tooltip("Apply wave around local Y (yaw).")]
    public bool applyYaw = false;

    [Tooltip("Apply wave around local Z (roll).")]
    public bool applyRoll = false;

    [Header("Along Chain Shaping")]
    [Tooltip("Scales wave by node normalizedIndex. Tail can get more by curve > 1.")]
    public AnimationCurve alongChain = AnimationCurve.Linear(0f, 0.35f, 1f, 1f);

    [Header("Lifetime")]
    [Tooltip("Remove pulses after this many seconds (safety cap).")]
    public float maxPulseLifetimeSec = 2.0f;

    [Header("Debug")]
    public bool debugDraw = false;

    private readonly List<Pulse> _pulses = new List<Pulse>(8);

    private void OnEnable()
    {
        if (movement != null)
            movement.OnKickCycleStart += OnKickCycleStart;
    }

    private void OnDisable()
    {
        if (movement != null)
            movement.OnKickCycleStart -= OnKickCycleStart;
    }

    private void OnKickCycleStart(MovementControllerRB.KickCycleEvent e)
    {
        TriggerFromKickEvent(e);
    }

    private void TriggerFromKickEvent(MovementControllerRB.KickCycleEvent e)
    {
        float now = Time.time;

        // Frequency: prefer physics rhythm from cycleDuration
        float baseFreqFromEvent = 1f / Mathf.Max(0.05f, e.cycleDuration);
        float freq = baseFreqFromEvent * Mathf.Max(0.1f, frequencyMul);

        // Amplitude: demand01 controls intensity
        float ampScale = Mathf.Clamp01(demandToAmplitude.Evaluate(e.demand01));
        float amp = baseAmplitudeDeg * ampScale;

        // Decay: weaker demand fades faster
        float decay = baseDecayPerSec + (1f - Mathf.Clamp01(e.demand01)) * lowDemandExtraDecay;

        // Travel: you can later tie this to speed if you want
        float travel = baseTravelCycles;

        _pulses.Add(new Pulse
        {
            startTime = now,
            amplitudeDeg = amp,
            frequencyHz = freq,
            decayPerSec = Mathf.Max(0.01f, decay),
            travelCycles = travel
        });
    }

    /// <summary>
    /// Optional manual trigger (for testing).
    /// </summary>
    public void TriggerManual(float? amplitudeDeg = null, float? frequencyHz = null, float? decayPerSec = null, float? travelCycles = null)
    {
        _pulses.Add(new Pulse
        {
            startTime = Time.time,
            amplitudeDeg = amplitudeDeg ?? baseAmplitudeDeg,
            frequencyHz = frequencyHz ?? baseFrequencyHz,
            decayPerSec = Mathf.Max(0.01f, decayPerSec ?? baseDecayPerSec),
            travelCycles = travelCycles ?? baseTravelCycles
        });
    }

    private void LateUpdate()
    {
        if (delayChain == null || delayChain.nodes == null || delayChain.nodes.Length == 0) return;

        float now = Time.time;

        // Clean expired pulses
        for (int i = _pulses.Count - 1; i >= 0; i--)
        {
            float age = now - _pulses[i].startTime;
            float softLife = 3f / Mathf.Max(0.1f, _pulses[i].decayPerSec);
            float hardLife = Mathf.Max(0.1f, maxPulseLifetimeSec);
            if (age > Mathf.Max(softLife, hardLife)) _pulses.RemoveAt(i);
        }

        // Apply pulse offsets (additive) on top of current bone.localRotation
        for (int i = 0; i < delayChain.nodes.Length; i++)
        {
            var node = delayChain.nodes[i];
            if (node == null || node.bone == null) continue;

            float x = Mathf.Clamp01(node.normalizedIndex);

            float waveDeg = EvaluateWaveDegrees(now, x);
            if (Mathf.Abs(waveDeg) < 1e-4f) continue;

            float chainScale = Mathf.Max(0f, alongChain.Evaluate(x));
            float finalDeg = waveDeg * chainScale;

            Quaternion q = node.bone.localRotation;

            // NOTE: This is intentionally simple and stable.
            // If you want to align with per-bone axes later, we can feed in your forwardAxis/upAxis mapping.
            if (applyPitch) q = Quaternion.AngleAxis(finalDeg, Vector3.right) * q;   // local X
            if (applyYaw)   q = Quaternion.AngleAxis(finalDeg, Vector3.up) * q;      // local Y
            if (applyRoll)  q = Quaternion.AngleAxis(finalDeg, Vector3.forward) * q; // local Z

            node.bone.localRotation = q;

            if (debugDraw)
            {
                Debug.DrawRay(node.bone.position, node.bone.forward * 0.25f, Color.yellow);
            }
        }
    }

    private float EvaluateWaveDegrees(float now, float normalizedIndex)
    {
        float sum = 0f;

        for (int i = 0; i < _pulses.Count; i++)
        {
            Pulse p = _pulses[i];
            float age = now - p.startTime;
            if (age < 0f) continue;

            float env = Mathf.Exp(-p.decayPerSec * age);

            // Phase: time increases phase; travel pushes phase along chain
            // sin(2π * (f*t - travel*index))
            float phase = 2f * Mathf.PI * (p.frequencyHz * age - p.travelCycles * normalizedIndex);

            sum += p.amplitudeDeg * env * Mathf.Sin(phase);
        }

        return sum;
    }
}

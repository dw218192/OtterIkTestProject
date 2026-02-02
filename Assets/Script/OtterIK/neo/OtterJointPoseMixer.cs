using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Central accumulator + applier for joint local pose.
/// Other scripts must SUBMIT pose intents here instead of writing transforms directly.
/// </summary>
[DefaultExecutionOrder(10000)] // Run after most LateUpdates
[DisallowMultipleComponent]
public class OtterJointPoseMixer : MonoBehaviour
{
    [Header("Bind pose")]
    [Tooltip("If true, capture bind local pose the first time a bone is seen.")]
    public bool captureBindOnFirstUse = true;

    [Tooltip("If set, only bones in this chain are allowed (others are ignored).")]
    public SpineChainDefinition allowedChain;

    [Header("Weight behavior")]
    [Tooltip("If false, total weight is clamped to 1 (recommended).")]
    public bool allowOverdriveWeight = false;

    [Tooltip("Minimum weight to consider a request active.")]
    public float minEffectiveWeight = 0.0001f;

    // ---- Internal state per bone ----
    private class BoneState
    {
        public Transform bone;

        // Bind pose
        public bool bindCaptured;
        public Quaternion bindLocalRot;
        public Vector3 bindLocalPos;

        // Accumulators (this frame)
        public Vector3 rotVecSum;      // axis-angle vector (radians)
        public float rotWeightSum;
        public float rotSharpnessMax;

        public Vector3 posSum;
        public float posWeightSum;
        public float posSharpnessMax;

        public bool touchedThisFrame;
    }

    private readonly Dictionary<Transform, BoneState> _states = new();
    private readonly HashSet<Transform> _allowed = new();

    private void Awake()
    {
        RebuildAllowedSet();
    }

    private void OnValidate()
    {
        RebuildAllowedSet();
    }

    private void RebuildAllowedSet()
    {
        _allowed.Clear();
        if (allowedChain == null || allowedChain.Count == 0) return;

        for (int i = 0; i < allowedChain.Count; i++)
        {
            var j = allowedChain.GetJoint(i);
            if (j != null && j.bone != null) _allowed.Add(j.bone);
        }
    }

    /// <summary>Call this from drivers: submit an absolute desired LOCAL rotation.</summary>
    public void SubmitLocalRotationTarget(Transform bone, Quaternion desiredLocalRot, float weight, float sharpness)
    {
        if (!IsBoneAllowed(bone)) return;
        if (bone == null) return;

        var st = GetState(bone);

        EnsureBind(st);

        weight = Mathf.Max(0f, weight);
        if (weight < minEffectiveWeight) return;

        // Delta from bind in local space
        Quaternion delta = desiredLocalRot * Quaternion.Inverse(st.bindLocalRot);
        Vector3 rotVec = ToAxisAngleVector(delta); // radians * axis

        st.rotVecSum += rotVec * weight;
        st.rotWeightSum += weight;
        st.rotSharpnessMax = Mathf.Max(st.rotSharpnessMax, Mathf.Max(0.01f, sharpness));
        st.touchedThisFrame = true;
    }

    /// <summary>Submit an absolute desired LOCAL position.</summary>
    public void SubmitLocalPositionTarget(Transform bone, Vector3 desiredLocalPos, float weight, float sharpness)
    {
        if (!IsBoneAllowed(bone)) return;
        if (bone == null) return;

        var st = GetState(bone);

        EnsureBind(st);

        weight = Mathf.Max(0f, weight);
        if (weight < minEffectiveWeight) return;

        Vector3 delta = desiredLocalPos - st.bindLocalPos;

        st.posSum += delta * weight;
        st.posWeightSum += weight;
        st.posSharpnessMax = Mathf.Max(st.posSharpnessMax, Mathf.Max(0.01f, sharpness));
        st.touchedThisFrame = true;
    }

    /// <summary>
    /// Clears per-frame accumulators early each frame.
    /// Drivers may submit during Update or LateUpdate; this ensures a clean frame.
    /// </summary>
    private void Update()
    {
        foreach (var kv in _states)
        {
            var st = kv.Value;
            st.rotVecSum = Vector3.zero;
            st.rotWeightSum = 0f;
            st.rotSharpnessMax = 0f;

            st.posSum = Vector3.zero;
            st.posWeightSum = 0f;
            st.posSharpnessMax = 0f;

            st.touchedThisFrame = false;
        }
    }

    /// <summary>Applies final accumulated transforms once per frame.</summary>
    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        foreach (var kv in _states)
        {
            var st = kv.Value;
            if (!st.touchedThisFrame || st.bone == null) continue;

            EnsureBind(st);

            // ---- Rotation mix ----
            if (st.rotWeightSum >= minEffectiveWeight)
            {
                float w = allowOverdriveWeight ? st.rotWeightSum : Mathf.Clamp01(st.rotWeightSum);
                Vector3 avgVec = (st.rotVecSum / Mathf.Max(1e-6f, st.rotWeightSum)) * w;

                Quaternion delta = FromAxisAngleVector(avgVec);
                Quaternion targetLocalRot = delta * st.bindLocalRot;

                float t = 1f - Mathf.Exp(-st.rotSharpnessMax * dt);
                st.bone.localRotation = Quaternion.Slerp(st.bone.localRotation, targetLocalRot, t);
            }
            else
            {
                // Optionally restore to bind here if you want “always recover” behavior:
                // st.bone.localRotation = Quaternion.Slerp(st.bone.localRotation, st.bindLocalRot, 1f - Mathf.Exp(-st.rotSharpnessMax * dt));
            }

            // ---- Position mix ----
            if (st.posWeightSum >= minEffectiveWeight)
            {
                float w = allowOverdriveWeight ? st.posWeightSum : Mathf.Clamp01(st.posWeightSum);
                Vector3 avgDelta = (st.posSum / Mathf.Max(1e-6f, st.posWeightSum)) * w;

                Vector3 targetLocalPos = st.bindLocalPos + avgDelta;

                float t = 1f - Mathf.Exp(-st.posSharpnessMax * dt);
                st.bone.localPosition = Vector3.Lerp(st.bone.localPosition, targetLocalPos, t);
            }
        }
    }

    // ---- helpers ----

    private bool IsBoneAllowed(Transform bone)
    {
        if (bone == null) return false;
        if (allowedChain == null) return true; // no restriction
        return _allowed.Contains(bone);
    }

    private BoneState GetState(Transform bone)
    {
        if (_states.TryGetValue(bone, out var st)) return st;

        st = new BoneState { bone = bone };
        _states.Add(bone, st);
        return st;
    }

    private void EnsureBind(BoneState st)
    {
        if (st.bindCaptured) return;
        if (!captureBindOnFirstUse) return;

        st.bindLocalRot = st.bone.localRotation;
        st.bindLocalPos = st.bone.localPosition;
        st.bindCaptured = true;
    }

    // Quaternion <-> axis-angle vector (log/exp map) in a practical form
    private static Vector3 ToAxisAngleVector(Quaternion q)
    {
        if (q.w > 1f) q.Normalize();
        q.ToAngleAxis(out float angleDeg, out Vector3 axis);

        if (float.IsNaN(axis.x) || axis.sqrMagnitude < 1e-12f) return Vector3.zero;

        // Map angle to [-180, 180] for stability
        if (angleDeg > 180f) angleDeg -= 360f;

        float angleRad = angleDeg * Mathf.Deg2Rad;
        axis.Normalize();
        return axis * angleRad;
    }

    private static Quaternion FromAxisAngleVector(Vector3 v)
    {
        float angleRad = v.magnitude;
        if (angleRad < 1e-8f) return Quaternion.identity;

        Vector3 axis = v / angleRad;
        float angleDeg = angleRad * Mathf.Rad2Deg;
        return Quaternion.AngleAxis(angleDeg, axis);
    }
}

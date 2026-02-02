using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class SpineChainDefinition : MonoBehaviour
{
    [Serializable]
    public class Joint
    {
        [Header("Bone (Hip -> Chest Order)")]
        public Transform bone;

        [Header("Position Along Chain (0=Hip, 1=Chest)")]
        [Range(0f, 1f)]
        public float normalizedFromHip01 = 0f;

        [Header("Aim / Bend")]
        [Range(0f, 1f)] public float weight = 0.2f;
        [Range(0f, 90f)] public float maxAngleDeg = 25f;
        [Range(0.1f, 5f)] public float sharpnessMul = 1f;

        [Header("Local Axes (Bone Space)")]
        public Vector3 forwardAxis = Vector3.forward;
        public Vector3 upAxis = Vector3.up;

        [Header("Turn Roll")]
        [Range(0f, 1f)] public float rollWeight = 0.2f;
        [Range(0f, 45f)] public float maxRollDeg = 12f;

        [Header("Delay")]
        [Range(0f, 0.5f)] public float extraDelay = 0f;

        [Header("Pulse")]
        [Range(0f, 1f)] public float pulseWeight = 1f;

        // Runtime-only cached bind pose
        [NonSerialized] public Quaternion bindLocalRot;
        [NonSerialized] public bool bindCaptured;
    }

    [Header("Authoritative chain")]
    [Tooltip("Ordered from HIP (tail-side) to CHEST/NECK (head-side).")]
    public Joint[] joints = Array.Empty<Joint>();

    [Header("Normalized positions")]
    [Tooltip("If true, normalizedFromHip01 is auto-filled evenly from joint index (0..1).")]
    public bool autoFillNormalizedFromHip = true;

    [Header("Validation")]
    public bool validateHierarchyContinuity = true;

    // Cached derived arrays
    private Transform[] _bonesHipToChest;
    private bool _cacheValid;

    public int Count => joints != null ? joints.Length : 0;

    public Joint GetJoint(int index)
    {
        if (joints == null || index < 0 || index >= joints.Length) return null;
        return joints[index];
    }

    public Transform GetBoneHipToChest(int index)
    {
        EnsureCache();
        if (_bonesHipToChest == null || index < 0 || index >= _bonesHipToChest.Length) return null;
        return _bonesHipToChest[index];
    }

    public float GetNormalizedFromHip01(int index)
    {
        EnsureCache();
        var j = GetJoint(index);
        if (j == null) return 0f;
        return Mathf.Clamp01(j.normalizedFromHip01);
    }

    public float GetNormalizedFromChest01(int index) => 1f - GetNormalizedFromHip01(index);

    public Quaternion GetBindLocalRot(int index, bool captureIfMissing = true)
    {
        var j = GetJoint(index);
        if (j == null || j.bone == null) return Quaternion.identity;

        if (captureIfMissing && !j.bindCaptured)
        {
            j.bindLocalRot = j.bone.localRotation;
            j.bindCaptured = true;
        }
        return j.bindLocalRot;
    }

    public void CaptureBindLocalRotations(bool force = false)
    {
        if (joints == null) return;
        for (int i = 0; i < joints.Length; i++)
        {
            var j = joints[i];
            if (j == null || j.bone == null) continue;
            if (force || !j.bindCaptured)
            {
                j.bindLocalRot = j.bone.localRotation;
                j.bindCaptured = true;
            }
        }
    }

    public Transform[] GetBonesHipToChest(bool excludeNull = true)
    {
        EnsureCache();
        if (_bonesHipToChest == null) return Array.Empty<Transform>();

        if (!excludeNull) return (Transform[])_bonesHipToChest.Clone();

        var list = new List<Transform>(_bonesHipToChest.Length);
        for (int i = 0; i < _bonesHipToChest.Length; i++)
            if (_bonesHipToChest[i] != null) list.Add(_bonesHipToChest[i]);
        return list.ToArray();
    }

    public Transform[] GetBonesChestToHip(bool excludeNull = true)
    {
        EnsureCache();
        if (_bonesHipToChest == null) return Array.Empty<Transform>();

        var list = new List<Transform>(_bonesHipToChest.Length);
        for (int i = _bonesHipToChest.Length - 1; i >= 0; i--)
        {
            var b = _bonesHipToChest[i];
            if (!excludeNull || b != null) list.Add(b);
        }
        return list.ToArray();
    }

    [ContextMenu("Auto-Fill normalizedFromHip01 (even 0..1)")]
    public void AutoFillNormalized()
    {
        if (joints == null) return;

        int n = joints.Length;
        if (n <= 0) return;
        if (n == 1)
        {
            if (joints[0] != null) joints[0].normalizedFromHip01 = 0f;
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                if (joints[i] != null)
                    joints[i].normalizedFromHip01 = i / (float)(n - 1);
            }
        }

        _cacheValid = false;
        EnsureCache(true);
    }

    [ContextMenu("Rebuild Cache (and Capture Bind Poses)")]
    public void RebuildCacheAndCaptureBind()
    {
        EnsureCache(force: true);
        CaptureBindLocalRotations(force: true);
    }

    private void Awake() => EnsureCache();
    private void OnEnable() => EnsureCache();

    private void OnValidate()
    {
        _cacheValid = false;
        EnsureCache();
        ValidateChain();
    }

    private void EnsureCache(bool force = false)
    {
        if (_cacheValid && !force) return;

        int n = Count;
        if (_bonesHipToChest == null || _bonesHipToChest.Length != n)
            _bonesHipToChest = new Transform[n];

        // Fill bone cache + sanitize axes
        for (int i = 0; i < n; i++)
        {
            var j = joints[i];
            _bonesHipToChest[i] = (j != null) ? j.bone : null;

            if (j == null) continue;

            if (j.forwardAxis.sqrMagnitude < 1e-6f) j.forwardAxis = Vector3.forward;
            if (j.upAxis.sqrMagnitude < 1e-6f) j.upAxis = Vector3.up;

            Vector3 f = j.forwardAxis.normalized;
            Vector3 u = j.upAxis.normalized;
            if (Vector3.Cross(f, u).sqrMagnitude < 1e-6f)
            {
                j.upAxis = (Mathf.Abs(Vector3.Dot(f, Vector3.up)) < 0.95f) ? Vector3.up : Vector3.right;
            }
        }

        // Auto-fill normalized positions if desired
        if (autoFillNormalizedFromHip && n > 0)
        {
            if (n == 1)
            {
                if (joints[0] != null) joints[0].normalizedFromHip01 = 0f;
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    if (joints[i] != null)
                        joints[i].normalizedFromHip01 = i / (float)(n - 1);
                }
            }
        }
        else
        {
            // Clamp any manually entered values
            for (int i = 0; i < n; i++)
            {
                if (joints[i] != null)
                    joints[i].normalizedFromHip01 = Mathf.Clamp01(joints[i].normalizedFromHip01);
            }
        }

        _cacheValid = true;
    }

    private void ValidateChain()
    {
        if (joints == null) return;

        var seen = new HashSet<Transform>();
        for (int i = 0; i < joints.Length; i++)
        {
            var j = joints[i];
            if (j == null || j.bone == null)
            {
                Debug.LogWarning($"[{nameof(SpineChainDefinition)}] Joint {i} is null or missing bone on {name}.", this);
                continue;
            }

            if (!seen.Add(j.bone))
                Debug.LogWarning($"[{nameof(SpineChainDefinition)}] Duplicate bone '{j.bone.name}' at joint {i} on {name}.", this);
        }

        if (!validateHierarchyContinuity) return;

        for (int i = 1; i < joints.Length; i++)
        {
            var prev = joints[i - 1]?.bone;
            var curr = joints[i]?.bone;
            if (prev == null || curr == null) continue;

            if (!curr.IsChildOf(prev))
            {
                Debug.LogWarning(
                    $"[{nameof(SpineChainDefinition)}] Chain continuity warning: '{curr.name}' is not a descendant of '{prev.name}'. " +
                    $"Order might be wrong (Hip->Chest), or rig has intermediate non-listed bones.",
                    this);
            }
        }
    }
}

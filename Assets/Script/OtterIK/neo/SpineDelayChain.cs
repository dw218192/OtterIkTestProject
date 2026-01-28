using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class SpineDelayChain : MonoBehaviour
{
    [Serializable]
    public class Node
    {
        [Tooltip("Bone transform that this node corresponds to.")]
        public Transform bone;

        [Tooltip("0..1 along chain. 0 = head-side, 1 = tail-side. If left all 0, it will auto fill evenly.")]
        [Range(0f, 1f)]
        public float normalizedIndex = 0f;

        [Tooltip("Extra delay seconds on top of baseDelay * normalizedIndex.")]
        [Range(0f, 0.5f)]
        public float extraDelay = 0f;

        [NonSerialized] public Quaternion bindLocalRot;
    }

    [Header("Source Pose (what we sample)")]
    [Tooltip("Driver transform to sample. Usually spineAnchor / otter_root.")]
    public Transform source;

    [Tooltip("Up reference for yaw plane & pose up. If null uses source.")]
    public Transform upReference;

    [Tooltip("Use Vector3.up instead of upReference.up")]
    public bool useWorldUp = false;

    [Header("Chain")]
    public Node[] nodes;

    [Header("Delay")]
    [Tooltip("Max delay at normalizedIndex=1 (seconds).")]
    [Range(0f, 0.6f)]
    public float baseDelay = 0.12f;

    [Tooltip("History buffer length (seconds). Must be > max delay.")]
    [Range(0.1f, 2f)]
    public float historySeconds = 0.6f;

    [Header("Sampling")]
    [Tooltip("Sample driver pose in LateUpdate (recommended, sees final IK/physics for the frame).")]
    public bool sampleInLateUpdate = true;

    [Header("Debug")]
    public bool drawDebug = false;
    public float debugAxisLen = 0.25f;

    private struct Sample
    {
        public float t;
        public Vector3 pos;
        public Quaternion rot;
        public Vector3 fwd;
        public Vector3 up;
        public Vector3 right;
    }

    private readonly List<Sample> _samples = new List<Sample>(256);
    private bool _initialized;

    private void Reset()
    {
        source = transform;
        upReference = transform;
    }

    private void Awake() => Initialize();

    private void Initialize()
    {
        if (_initialized) return;

        if (source == null) source = transform;
        if (upReference == null) upReference = source;
        if (nodes == null) nodes = Array.Empty<Node>();

        // Auto normalizedIndex if all 0 (and length > 1)
        bool allZero = true;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i] != null && nodes[i].normalizedIndex > 0f) { allZero = false; break; }
        }
        if (allZero && nodes.Length > 1)
        {
            for (int i = 0; i < nodes.Length; i++)
                if (nodes[i] != null) nodes[i].normalizedIndex = (float)i / (nodes.Length - 1);
        }

        for (int i = 0; i < nodes.Length; i++)
        {
            var n = nodes[i];
            if (n == null || n.bone == null) continue;
            n.bindLocalRot = n.bone.localRotation;
        }

        _samples.Clear();
        PushSample(Time.time);

        _initialized = true;
    }

    private void Update()
    {
        if (!_initialized) Initialize();

        if (!sampleInLateUpdate)
            PushSample(Time.time);

        TrimHistory(Time.time);
    }

    private void LateUpdate()
    {
        if (!_initialized) Initialize();

        if (sampleInLateUpdate)
            PushSample(Time.time);

        TrimHistory(Time.time);

        if (drawDebug) DrawDebug(Time.time);
    }

    private void PushSample(float now)
    {
        Vector3 up = useWorldUp ? Vector3.up : (upReference != null ? upReference.up : Vector3.up);
        Quaternion rot = source.rotation;

        Vector3 fwd = rot * Vector3.forward;
        Vector3 right = rot * Vector3.right;

        _samples.Add(new Sample
        {
            t = now,
            pos = source.position,
            rot = rot,
            fwd = fwd.normalized,
            up = up.normalized,
            right = right.normalized
        });
    }

    private void TrimHistory(float now)
    {
        float cutoff = now - Mathf.Max(0.1f, historySeconds);
        int removeCount = 0;

        for (int i = 0; i < _samples.Count; i++)
        {
            if (_samples[i].t < cutoff) removeCount++;
            else break;
        }

        if (removeCount > 0)
            _samples.RemoveRange(0, removeCount);
    }

    public float GetNodeDelaySeconds(int nodeIndex)
    {
        if (nodes == null || nodeIndex < 0 || nodeIndex >= nodes.Length) return 0f;
        var n = nodes[nodeIndex];
        float delay = baseDelay * Mathf.Clamp01(n.normalizedIndex) + Mathf.Max(0f, n.extraDelay);
        return delay;
    }

    /// <summary>
    /// Get delayed driver pose in world space for the given node.
    /// </summary>
    public void GetDelayedPose(int nodeIndex, float now,
        out Vector3 pos, out Quaternion rot, out Vector3 fwd, out Vector3 up, out Vector3 right)
    {
        pos = source.position;
        rot = source.rotation;
        fwd = rot * Vector3.forward;
        right = rot * Vector3.right;
        up = useWorldUp ? Vector3.up : (upReference != null ? upReference.up : Vector3.up);

        if (_samples.Count < 2 || nodes == null || nodeIndex < 0 || nodeIndex >= nodes.Length) return;

        float delay = GetNodeDelaySeconds(nodeIndex);
        float targetT = now - delay;

        // Find s0 <= targetT <= s1
        int idx = _samples.Count - 1;
        while (idx > 0 && _samples[idx].t > targetT) idx--;

        int i0 = Mathf.Clamp(idx, 0, _samples.Count - 2);
        int i1 = i0 + 1;

        Sample s0 = _samples[i0];
        Sample s1 = _samples[i1];

        float span = Mathf.Max(1e-4f, s1.t - s0.t);
        float t = Mathf.Clamp01((targetT - s0.t) / span);

        pos = Vector3.Lerp(s0.pos, s1.pos, t);
        rot = Quaternion.Slerp(s0.rot, s1.rot, t);

        fwd = Vector3.Slerp(s0.fwd, s1.fwd, t).normalized;
        up = Vector3.Slerp(s0.up, s1.up, t).normalized;
        right = Vector3.Slerp(s0.right, s1.right, t).normalized;
    }

    /// <summary>
    /// 0..1 helper: farther nodes return larger values.
    /// Useful for "the farther, the more roll".
    /// </summary>
    public float GetDistanceFactor01(int nodeIndex)
    {
        if (nodes == null || nodeIndex < 0 || nodeIndex >= nodes.Length) return 0f;
        return Mathf.Clamp01(nodes[nodeIndex].normalizedIndex);
    }

    private void DrawDebug(float now)
    {
        if (nodes == null) return;

        for (int i = 0; i < nodes.Length; i++)
        {
            GetDelayedPose(i, now, out var p, out var r, out var f, out var u, out var rt);

            Debug.DrawLine(p, p + f * debugAxisLen, Color.cyan);
            Debug.DrawLine(p, p + u * debugAxisLen * 0.8f, Color.magenta);
            Debug.DrawLine(p, p + rt * debugAxisLen * 0.6f, Color.yellow);
        }
    }
}

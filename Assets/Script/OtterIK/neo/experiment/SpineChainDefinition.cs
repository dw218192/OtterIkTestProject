using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// SpineChainDefinition (Cleaned)
/// - 注册有序的脊椎关节。
/// - 存储序列化的“静止/绑定姿态” (仅限本地旋转 local rotation)。
/// - 允许用户定义骨骼的本地 Forward / Up 轴向。
/// </summary>
[DisallowMultipleComponent]
public sealed class SpineChainDefinition : MonoBehaviour
{
    public enum AxisRef
    {
        PosX, NegX,
        PosY, NegY,
        PosZ, NegZ
    }

    public static Vector3 AxisToVector(AxisRef a)
    {
        switch (a)
        {
            case AxisRef.PosX: return Vector3.right;
            case AxisRef.NegX: return Vector3.left;
            case AxisRef.PosY: return Vector3.up;
            case AxisRef.NegY: return Vector3.down;
            case AxisRef.PosZ: return Vector3.forward;
            case AxisRef.NegZ: return Vector3.back;
            default: return Vector3.forward;
        }
    }

    [Serializable]
    public sealed class JointEntry
    {
        [Tooltip("脊椎关节 Transform (骨骼)。")]
        public Transform bone;

        [Header("Rest / Bind Pose (Local Rotation Only)")]
        [Tooltip("静止时的本地旋转 Euler 角度。")]
        public Vector3 restLocalEuler = Vector3.zero;

        [Tooltip("该关节是否已捕获/定义了静止姿态。")]
        public bool hasRestPose = false;

        [Header("Axis Override")]
        [Tooltip("是否覆盖全局轴向设置？(用于 Root 等坐标系特殊的骨骼)")]
        public bool overrideAxes = false;
        public AxisRef forwardAxis = AxisRef.PosZ;
        public AxisRef upAxis = AxisRef.PosY;

        public Quaternion RestLocalRotation => Quaternion.Euler(restLocalEuler);
    }

    [Header("Spine Joints")]
    [Tooltip("按顺序排列的脊椎关节 (从根部到末梢)。")]
    [SerializeField] private List<JointEntry> joints = new List<JointEntry>();

    [Header("Skeleton Axes (Local)")]
    [Tooltip("每个骨骼本地坐标系中代表“前方”的轴。")]
    [SerializeField] private AxisRef forwardAxis = AxisRef.PosZ;

    [Tooltip("每个骨骼本地坐标系中代表“上方”的轴。")]
    [SerializeField] private AxisRef upAxis = AxisRef.PosY;

    public int Count => joints?.Count ?? 0;
    public IReadOnlyList<JointEntry> Joints => joints;

    public AxisRef ForwardAxis => forwardAxis;
    public AxisRef UpAxis => upAxis;

    public Vector3 LocalForward => AxisToVector(forwardAxis);
    public Vector3 LocalUp => AxisToVector(upAxis);

    public Vector3 GetForwardForJoint(int index) =>
        (joints != null && index >= 0 && index < joints.Count && joints[index] != null && joints[index].overrideAxes)
            ? AxisToVector(joints[index].forwardAxis)
            : LocalForward;

    public Vector3 GetUpForJoint(int index) =>
        (joints != null && index >= 0 && index < joints.Count && joints[index] != null && joints[index].overrideAxes)
            ? AxisToVector(joints[index].upAxis)
            : LocalUp;

    public Vector3 LocalRight
    {
        get
        {
            var f = LocalForward;
            var u = LocalUp;
            var r = Vector3.Cross(u, f);
            if (r.sqrMagnitude < 1e-10f) return Vector3.right;
            return r.normalized;
        }
    }

    public Transform GetBone(int index)
    {
        if (joints == null || index < 0 || index >= joints.Count) return null;
        return joints[index]?.bone;
    }

    public bool TryGetWorldForward(Transform bone, out Vector3 worldForward)
    {
        worldForward = Vector3.forward;
        if (bone == null) return false;
        worldForward = bone.TransformDirection(LocalForward);
        return true;
    }

    public bool TryGetWorldUp(Transform bone, out Vector3 worldUp)
    {
        worldUp = Vector3.up;
        if (bone == null) return false;
        worldUp = bone.TransformDirection(LocalUp);
        return true;
    }

    public bool TryGetRestLocalRotation(int index, out Quaternion restLocalRot)
    {
        restLocalRot = Quaternion.identity;
        if (joints == null || index < 0 || index >= joints.Count) return false;

        var e = joints[index];
        if (e == null || e.bone == null) return false;

        restLocalRot = e.hasRestPose ? e.RestLocalRotation : e.bone.localRotation;
        return true;
    }

    public bool SetRestLocalEuler(int index, Vector3 eulerDeg, bool markHasRestPose = true)
    {
        if (joints == null || index < 0 || index >= joints.Count) return false;
        var e = joints[index];
        if (e == null) return false;

        e.restLocalEuler = eulerDeg;
        if (markHasRestPose) e.hasRestPose = true;
        return true;
    }

    [ContextMenu("Capture Rest Pose From Current (Rotation Only)")]
    public void CaptureRestPoseFromCurrent()
    {
        if (joints == null) return;

#if UNITY_EDITOR
        Undo.RecordObject(this, "Capture Rest Pose (SpineChainDefinition)");
#endif

        for (int i = 0; i < joints.Count; i++)
        {
            var e = joints[i];
            if (e == null || e.bone == null) continue;

            e.restLocalEuler = e.bone.localRotation.eulerAngles;
            e.hasRestPose = true;
        }

#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
#endif
    }

    [ContextMenu("Apply Rest Pose To Bones (Rotation Only)")]
    public void ApplyRestPoseToBones()
    {
        if (joints == null) return;

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            foreach (var e in joints)
            {
                if (e?.bone != null)
                    Undo.RecordObject(e.bone, "Apply Rest Pose (SpineChainDefinition)");
            }
        }
#endif

        for (int i = 0; i < joints.Count; i++)
        {
            var e = joints[i];
            if (e == null || e.bone == null || !e.hasRestPose) continue;

            e.bone.localRotation = e.RestLocalRotation;
        }
    }

    [ContextMenu("Clear Rest Pose Flags")]
    public void ClearRestPoseFlags()
    {
        if (joints == null) return;

#if UNITY_EDITOR
        Undo.RecordObject(this, "Clear Rest Pose Flags (SpineChainDefinition)");
#endif
        foreach (var e in joints)
        {
            if (e != null) e.hasRestPose = false;
        }
#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
#endif
    }

    public bool Validate(out string message)
    {
        if (joints == null || joints.Count == 0)
        {
            message = "No joints assigned.";
            return false;
        }

        var set = new HashSet<Transform>();
        for (int i = 0; i < joints.Count; i++)
        {
            var e = joints[i];
            if (e == null || e.bone == null)
            {
                message = $"Joint[{i}] is null / missing bone.";
                return false;
            }
            if (!set.Add(e.bone))
            {
                message = $"Duplicate bone detected: {e.bone.name}";
                return false;
            }
        }

        if (Vector3.Cross(LocalUp.normalized, LocalForward.normalized).sqrMagnitude < 1e-8f)
        {
            message = "ForwardAxis and UpAxis are colinear (parallel).";
            return false;
        }

        message = "OK";
        return true;
    }

    private void OnValidate()
    {
        if (joints == null) joints = new List<JointEntry>();
        for (int i = 0; i < joints.Count; i++)
        {
            if (joints[i] == null) joints[i] = new JointEntry();
        }
    }
}
using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    [DisallowMultipleComponent]
    public class ExpSpineAimToGuideProvider : ExpSpinePoseProviderBase
    {
        [Header("References")]
        public Transform spineAnchor;
        public Transform headIKGuide;
        public Transform upReference;
        public bool useWorldUp = true;

        [Header("Aim Mode")]
        public bool yawOnly = true;

        [Range(0f, 10f)]
        public float deadZoneDeg = 0.25f;

        [Range(0f, 180f)]
        public float maxTotalAngleDeg = 80f;

        [Tooltip("如果引用缺失，是否安全退出而不报错。")]
        public bool safeIfMissingRefs = true;

        public override bool Evaluate(
            int index,
            SpineChainDefinition spine,
            Transform jointOrShadow,
            Quaternion bindLocalRot,
            out Quaternion targetLocalRot,
            out float weight)
        {
            targetLocalRot = bindLocalRot;
            weight = 0f;

            if (spine == null || spine.Count == 0) return false;
            if (safeIfMissingRefs && (spineAnchor == null || headIKGuide == null)) return false;

            Transform bone = spine.GetBone(index);
            if (bone == null || bone.parent == null) return false;
            Transform parent = bone.parent;

            // 1. 确定世界空间的目标朝向
            Vector3 upWorld = useWorldUp ? Vector3.up : (upReference != null ? upReference.up : Vector3.up);
            upWorld = (upWorld.sqrMagnitude < 1e-6f) ? Vector3.up : upWorld.normalized;

            Vector3 desiredDirWorld = (headIKGuide.position - spineAnchor.position);
            if (desiredDirWorld.sqrMagnitude < 1e-6f) return false;
            desiredDirWorld.Normalize();

            Vector3 anchorFwdWorld = spineAnchor.forward;

            // 2. 限制 Aim 范围 (YawOnly 或角度限制)
            if (yawOnly)
            {
                Vector3 desiredPlane = Vector3.ProjectOnPlane(desiredDirWorld, upWorld);
                Vector3 anchorPlane = Vector3.ProjectOnPlane(anchorFwdWorld, upWorld);
                if (desiredPlane.sqrMagnitude > 1e-6f && anchorPlane.sqrMagnitude > 1e-6f)
                {
                    float yawDeg = Vector3.SignedAngle(anchorPlane.normalized, desiredPlane.normalized, upWorld);
                    if (Mathf.Abs(yawDeg) < deadZoneDeg) yawDeg = 0f;
                    yawDeg = Mathf.Clamp(yawDeg, -maxTotalAngleDeg, maxTotalAngleDeg);
                    desiredDirWorld = Quaternion.AngleAxis(yawDeg, upWorld) * anchorPlane.normalized;
                }
            }
            else
            {
                float ang = Vector3.Angle(anchorFwdWorld, desiredDirWorld);
                if (ang > maxTotalAngleDeg)
                {
                    Vector3 axis = Vector3.Cross(anchorFwdWorld, desiredDirWorld);
                    if (axis.sqrMagnitude > 1e-6f)
                        desiredDirWorld = Quaternion.AngleAxis(maxTotalAngleDeg, axis.normalized) * anchorFwdWorld;
                }
            }

            // 3. 关键改进：处理特殊关节的 Local Axis 和 BindPose
            
            // 获取该关节在 Definition 中覆盖的本地 Forward 轴
            Vector3 localFwd = spine.GetForwardForJoint(index);
            
            // 将目标方向转入 Parent 空间
            Vector3 desiredDirParent = parent.InverseTransformDirection(desiredDirWorld).normalized;
            
            // 重要：计算在 Bind 姿态下，该关节的本地 Forward 轴在 Parent 空间中指向哪里
            // 这考虑了 restLocalEuler (90, 0, 0) 带来的物理偏移
            Vector3 bindFwdParent = (bindLocalRot * localFwd).normalized;

            // 计算从 Bind 状态的朝向到目标朝向的 Swing 旋转
            // 这解决了最后一节关节由于 restLocalEuler 不同而导致的旋转基准错误
            Quaternion swingRotation = Quaternion.FromToRotation(bindFwdParent, desiredDirParent);

            // 4. 应用
            // swingRotation 描述的是 Parent 空间内的变换，因此左乘 bindLocalRot
            targetLocalRot = swingRotation * bindLocalRot;

            weight = 1f;
            return true;
        }
    }
}
using System.Collections.Generic;
using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    public class ExpShadowSpineHarmonizer : MonoBehaviour
    {
        [Header("References")]
        public SpineDecouplerWithShadows decoupler;
        public List<ExpSpinePoseProviderBase> providers = new();

        [Header("Whip / Constraint Chain (Yaw & Pitch)")]
        [Range(0f, 1f)] public float flexibility = 0.6f;
        [Range(0.01f, 1f)] public float dampingHalfLife = 0.08f;

        [Header("Roll Cascade (Flip & Tilt)")]
        [Tooltip("Roll 轴的传播柔软度，决定了翻转时的‘拧麻花’延迟感")]
        [Range(0f, 1f)] public float rollFlexibility = 0.5f;
        [Tooltip("Roll 轴的阻尼，控制翻转动作的重量感")]
        [Range(0.01f, 1f)] public float rollDampingHalfLife = 0.1f;

        private Quaternion[] _filteredYawPitchDeltas;
        private float[] _filteredRollValues; 
        private Vector3[] _initialLocalOffsets;

        private void Start()
        {
            if (decoupler == null || decoupler.spineJoints == null) return;
            int n = decoupler.spineJoints.Length;
            _filteredYawPitchDeltas = new Quaternion[n];
            _filteredRollValues = new float[n];
            _initialLocalOffsets = new Vector3[n];

            for (int i = 0; i < n; i++)
            {
                _filteredYawPitchDeltas[i] = Quaternion.identity;
                _filteredRollValues[i] = 0f;
                if (i < n - 1)
                {
                    _initialLocalOffsets[i] = decoupler.shadowNodes[i].position - decoupler.shadowNodes[i + 1].position;
                }
            }
        }

        private void LateUpdate()
        {
            if (decoupler == null || decoupler.shadowNodes.Count == 0) return;
            int n = decoupler.shadowNodes.Count;
            float dt = Time.deltaTime;

            // --- 阶段 1：智能混合意图 ---
            Quaternion[] yawPitchTargets = new Quaternion[n];
            float[] rollTargets = new float[n];
            
            var mover = decoupler.GetComponentInParent<MovementControllerRB>();
            float aimBlend = mover != null ? mover.GetAimBlend01() : 0f; // dragging=1, release-align decays to 0
            bool aimActive = aimBlend > 1e-4f;

            for (int i = 0; i < n; i++)
            {
                Vector3 ypRotVecSum = Vector3.zero;
                float ypWeightSum = 0f;
                float dominantRoll = 0f; // 用于记录当前关节最强的 Roll 意图

                foreach (var prov in providers)
                {
                    if (prov == null || !prov.isActiveAndEnabled) continue;

                    if (prov.Evaluate(i, null, decoupler.shadowNodes[i], Quaternion.identity, out Quaternion targetRot, out float w))
                    {
                        float finalW = Mathf.Clamp01(w * prov.globalWeight);
                        if (finalW <= 1e-5f) continue;

                        // 1. 提取并处理 Roll (Z轴)
                        Vector3 euler = targetRot.eulerAngles;
                        float r = Normalize180(euler.z);
                        
                        // 【优化 A】Roll 轴优先级策略：取绝对值最大的意图。
                        // 这能保证 Flip (180°) 不会被 AimProvider 的默认 0° 稀释。
                        if (Mathf.Abs(r) > Mathf.Abs(dominantRoll)) 
                        {
                            dominantRoll = r;
                        }

                        // 2. 提取并处理 Yaw/Pitch (转向)
                        Quaternion ypOnly = Quaternion.Euler(euler.x, euler.y, 0);

                        // 【优化 B】转向贡献检测：
                        // 只有当 Provider 真的产生了大于 0.1 度的转向时，才计入平均权重。
                        // 这样静止的 FlipProvider 就不会作为分母去削弱 AimProvider 的转向幅度。
                        if (Quaternion.Angle(Quaternion.identity, ypOnly) > 0.1f)
                        {
                            ypOnly.ToAngleAxis(out float angle, out Vector3 axis);
                            if (angle > 180) angle -= 360;
                            ypRotVecSum += axis.normalized * (angle * Mathf.Deg2Rad) * finalW;
                            ypWeightSum += finalW;
                        }
                    }
                }

                // 应用混合结果
                rollTargets[i] = dominantRoll;

              // --- 修改后的代码 ---
                if (ypWeightSum > 1e-6f)
                {
                    float totalW = Mathf.Clamp01(ypWeightSum);
                    
                    // 我们只让 AimProvider 产生的部分受到 aimBlend (拖拽状态) 的影响
                    // 而像简谐运动这种叠加效果，应该在 ypRotVecSum 中被直接应用
                    Vector3 meanVec = (ypRotVecSum / ypWeightSum) * totalW;
                    
                    yawPitchTargets[i] = Quaternion.AngleAxis(meanVec.magnitude * Mathf.Rad2Deg, meanVec.normalized);
                }
                else
                {
                    yawPitchTargets[i] = Quaternion.identity;
                }
            }

            // --- 阶段 2：双轨级联传播 ---
            for (int i = n - 1; i >= 0; i--)
            {
                // A. Yaw/Pitch 传播
                Quaternion ypTarget = yawPitchTargets[i];
                if (i < n - 1) ypTarget = Quaternion.Slerp(ypTarget, _filteredYawPitchDeltas[i + 1], flexibility);
                float ypAlpha = 1f - Mathf.Exp(-0.693f * dt / dampingHalfLife);
                _filteredYawPitchDeltas[i] = Quaternion.Slerp(_filteredYawPitchDeltas[i], ypTarget, ypAlpha);

                // B. Roll 传播
                float rTarget = rollTargets[i];
                float rAlpha = 1f - Mathf.Exp(-0.693f * dt / rollDampingHalfLife);
                
                if (i < n - 1)
                {
                    // 级联 Roll：将当前意图与上游状态进行插值
                    rTarget = Mathf.LerpAngle(rTarget, _filteredRollValues[i + 1], rollFlexibility);
                }
                _filteredRollValues[i] = Mathf.LerpAngle(_filteredRollValues[i], rTarget, rAlpha);

                // --- 阶段 3：合成与应用 ---
                Transform shadow = decoupler.shadowNodes[i];
                
                // 应用旋转：YawPitch * Roll (先旋转身体指向，再本地自转)
                shadow.localRotation = _filteredYawPitchDeltas[i] * Quaternion.Euler(0, 0, _filteredRollValues[i]);

                // 位置补偿（手动层级，防止变瘪）
                if (i < n - 1)
                {
                    Transform parentShadow = decoupler.shadowNodes[i + 1];
                    shadow.position = parentShadow.position + (parentShadow.rotation * _initialLocalOffsets[i]);
                }
            }
        }

        private static float Normalize180(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            if (deg < -180f) deg += 360f;
            return deg;
        }
    }
}
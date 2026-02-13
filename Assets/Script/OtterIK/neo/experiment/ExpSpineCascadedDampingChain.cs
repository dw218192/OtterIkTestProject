using System.Collections.Generic;
using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    [DefaultExecutionOrder(32000)]
    public class ExpSpineCascadedDampingChain : MonoBehaviour
    {
        public enum PropagationDirection { HipToChest, ChestToHip }

        [Header("Chain")]
        public SpineChainDefinition spine;

        [Header("Propagation")]
        public PropagationDirection propagation = PropagationDirection.HipToChest;
        [Range(0f, 1f)] public float propagationBlend = 0.75f;

        [Header("Damping")]
        [Range(0.01f, 2.0f)] public float rotationHalfLife = 0.12f;
        public AnimationCurve rotHalfLifeMul = AnimationCurve.Linear(0, 1, 1, 1);

        [Header("Providers")]
        public List<ExpSpinePoseProviderBase> providers = new();

        private Quaternion[] _bindLocalRot;
        private Quaternion[] _filteredDelta; 
        private bool _initialized;

        private void LateUpdate()
        {
            if (!Initialize()) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            int n = spine.Count;
            Quaternion[] rawTargetDeltas = new Quaternion[n];
            bool anyProviderNeedsContinuous = false;

            // 1. 收集增量 (Delta = Inv(Bind) * Target)
            for (int i = 0; i < n; i++)
            {
                Quaternion bindRot = _bindLocalRot[i];
                Vector3 rotVecSum = Vector3.zero;
                float rotWSum = 0f;

                foreach (var prov in providers)
                {
                    if (prov == null || !prov.isActiveAndEnabled) continue;
                    if (prov.RequiresContinuousPath) anyProviderNeedsContinuous = true;
                    Transform bone = spine.GetBone(i);
                    if (prov.Evaluate(i, spine, bone, bindRot, out Quaternion targetRot, out float w))
                    {
                        w = Mathf.Clamp01(w * prov.globalWeight);
                        if (w <= 1e-5f) continue;

                        Quaternion dRot = Quaternion.Inverse(bindRot) * targetRot;
                        if (dRot.w < 0f) dRot = new Quaternion(-dRot.x, -dRot.y, -dRot.z, -dRot.w);
                        rotVecSum += ToAxisAngleVector(dRot) * w;
                        rotWSum += w;
                    }
                }
                float totalW = Mathf.Clamp01(rotWSum);
                Vector3 meanVec = (rotWSum > 1e-6f) ? (rotVecSum / rotWSum * totalW) : Vector3.zero;
                rawTargetDeltas[i] = FromAxisAngleVector(meanVec);
            }

            // 2. 级联传播逻辑
            bool hipToChest = (propagation == PropagationDirection.HipToChest);
            int start = hipToChest ? 0 : (n - 1);
            int step = hipToChest ? 1 : -1;
            int end = hipToChest ? n : -1;

            for (int curr = start; curr != end; curr += step)
            {
                float hip01 = (float)curr / Mathf.Max(1, n - 1);
                int upstream = hipToChest ? (curr - 1) : (curr + 1);
                bool hasUpstream = upstream >= 0 && upstream < n;

                Quaternion targetDelta = rawTargetDeltas[curr];

                if (hasUpstream)
                {
                    Quaternion upDelta = _filteredDelta[upstream];
                    // 路径锁定
                    if (anyProviderNeedsContinuous && Quaternion.Dot(targetDelta, upDelta) < 0f)
                    {
                        targetDelta = new Quaternion(-targetDelta.x, -targetDelta.y, -targetDelta.z, -targetDelta.w);
                    }
                    // 混合传播姿态
                    targetDelta = Quaternion.Slerp(targetDelta, upDelta, propagationBlend);
                }

                // 阻尼滤波
                float hl = rotationHalfLife * rotHalfLifeMul.Evaluate(hip01);
                _filteredDelta[curr] = Quaternion.Slerp(_filteredDelta[curr], targetDelta, HalfLifeToAlpha(hl, dt));

                // 核心应用修复：
                Quaternion applyRotation = _filteredDelta[curr];
                
                if (hipToChest && hasUpstream)
                {
                    // 只有顺着 Unity 层级(Parent -> Child)传播时，才需要执行差分抵消。
                    // 否则子关节会叠加父关节的旋转。
                    Quaternion upDelta = _filteredDelta[upstream];
                    if (anyProviderNeedsContinuous && Quaternion.Dot(applyRotation, upDelta) < 0f)
                        applyRotation = new Quaternion(-applyRotation.x, -applyRotation.y, -applyRotation.z, -applyRotation.w);
                    
                    applyRotation = Quaternion.Inverse(upDelta) * applyRotation;
                }
                // 注意：在 ChestToHip (逆层级) 模式下，直接应用 applyRotation。
                // 因为父级(Hip)的旋转在物理层级上会自动向下传递，逻辑传播已经处理了平滑度。

                spine.GetBone(curr).localRotation = _bindLocalRot[curr] * applyRotation;
            }
        }

        private bool Initialize()
        {
            if (_initialized && spine != null && _bindLocalRot.Length == spine.Count) return true;
            if (spine == null || spine.Count == 0) return false;
            int n = spine.Count;
            _bindLocalRot = new Quaternion[n];
            _filteredDelta = new Quaternion[n];
            for (int i = 0; i < n; i++)
            {
                if (spine.TryGetRestLocalRotation(i, out Quaternion r)) _bindLocalRot[i] = r;
                _filteredDelta[i] = Quaternion.identity;
            }
            _initialized = true;
            return true;
        }

        private float HalfLifeToAlpha(float hl, float dt) => 1f - Mathf.Exp(-0.693f * dt / Mathf.Max(1e-6f, hl));
        private Vector3 ToAxisAngleVector(Quaternion q) { q.ToAngleAxis(out float a, out Vector3 v); return (v.sqrMagnitude < 1e-12f) ? Vector3.zero : v.normalized * (a * Mathf.Deg2Rad); }
        private Quaternion FromAxisAngleVector(Vector3 v) => v.magnitude < 1e-8f ? Quaternion.identity : Quaternion.AngleAxis(v.magnitude * Mathf.Rad2Deg, v.normalized);
    }
}
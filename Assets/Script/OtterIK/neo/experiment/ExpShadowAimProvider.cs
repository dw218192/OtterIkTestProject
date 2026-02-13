using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    public class ExpShadowAimProvider : ExpSpinePoseProviderBase
    {
        [Header("References")]
        public SpineDecouplerWithShadows decoupler; // 引用解耦脚本获取影子位置
        public Transform target;
        public Transform rootReference;
        
        [Header("Settings")]
        public AnimationCurve weightCurve = AnimationCurve.Linear(0, 0, 1, 1);
        [Range(0, 180)] public float maxAngle = 90f;

        [Header("Safety")]
        [Tooltip("当目标在身后超过此角度时，自动减弱瞄准权重，防止翻转突变。")]
        [Range(90, 180)] public float suppressionStartAngle = 110f;

        public override bool Evaluate(
            int index,
            SpineChainDefinition spine, // 允许此项为 null
            Transform jointOrShadow,    // Shadow 模式下会传入对应的 shadow Transform
            Quaternion bindLocalRot,
            out Quaternion targetLocalRot,
            out float weight)
        {
            targetLocalRot = bindLocalRot;
            weight = 0f;

            // 检查解耦脚本和必要的引用
            if (decoupler == null || target == null || rootReference == null) return false;

            // 优先使用外部传入的 shadow（由 Harmonizer 指定），否则回退到 decoupler.shadowNodes[index]
            Transform shadow = jointOrShadow;
            if (shadow == null)
            {
                if (index < 0 || index >= decoupler.shadowNodes.Count) return false;
                shadow = decoupler.shadowNodes[index];
            }
            Vector3 forwardBase = rootReference.forward;
            
            // 【关键点】直接从 shadow 节点获取世界坐标计算向量
            Vector3 deltaPos = target.position - shadow.position;
            if (deltaPos.sqrMagnitude < 1e-10f) return false;
            Vector3 toTarget = deltaPos.normalized;

            // --- 角度计算与抑制逻辑 (New) ---
            float rawAngle = Vector3.Angle(forwardBase, toTarget);
            float safetyWeight = 1.0f;

            // 如果目标角度过大（在身后），则衰减权重，让脊椎“放弃”扭曲，
            // 避免左右 179 度切换时产生的剧烈翻转 (Singularity Flip)。
            if (rawAngle > suppressionStartAngle)
            {
                float range = 180f - suppressionStartAngle;
                if (range > 0.01f)
                {
                    float t = (rawAngle - suppressionStartAngle) / range;
                    // t=0 (at start) -> weight=1
                    // t=1 (at 180)   -> weight=0
                    safetyWeight = Mathf.Clamp01(1.0f - t);
                }
            }

            // --- 角度限制逻辑 ---
            if (rawAngle > maxAngle)
            {
                Vector3 axis = Vector3.Cross(forwardBase, toTarget).normalized;
                // 防止正好 180 度时的万向节死锁或翻转
                if (axis.sqrMagnitude < 1e-6f) axis = Vector3.up; 
                toTarget = Quaternion.AngleAxis(maxAngle, axis) * forwardBase;
            }

            int total = decoupler.shadowNodes != null ? decoupler.shadowNodes.Count : 0;
            float normalizedIndex = (total > 1) ? ((float)index / (total - 1)) : 0f; // 0=臀部, 1=胸部

            // 关键优化：根据 index 施加不同的强度，避免所有骨骼返回完全一样的 targetRot（“百叶窗”感）
            // 胸部(index大)最先反应，臀部(index小)反应最慢且幅度更小
            float intensity = Mathf.Lerp(0.3f, 1.0f, normalizedIndex);

            Quaternion delta = Quaternion.FromToRotation(forwardBase, toTarget);
            targetLocalRot = Quaternion.Slerp(Quaternion.identity, delta, intensity) * bindLocalRot;

            // 应用基础曲线权重 * 安全抑制权重
            weight = weightCurve.Evaluate(normalizedIndex) * safetyWeight;
            
            return true;
        }
    }
}
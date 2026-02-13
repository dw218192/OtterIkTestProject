using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    public class ExpSpineRollFlipProvider : ExpSpinePoseProviderBase
    {
        [Header("Pattern 1: Flip Settings (Persistence)")]
        [Tooltip("当前是否为仰泳状态")]
        public bool isBackstroke = false;
        
        [Header("Pattern 2: Dynamic Roll (Temporary)")]
        [Range(-90f, 90f)] public float additiveRoll = 0f;
        
        [Header("Propagation Pattern")]
        [Tooltip("侧倾 (Tilt) 随脊椎向下游传递时的衰减系数 (1.0 = 不衰减)")]
        [Range(0f, 1f)] public float rollDecay = 0.85f;

        [Header("Smoothing")]
        [Tooltip("本地目标值的平滑速度")]
        public float smoothSpeed = 10f;

        private float _currentBaseRoll;
        private float _currentAdditiveRoll;

        public override bool Evaluate(
            int index, 
            SpineChainDefinition spine, 
            Transform jointOrShadow, 
            Quaternion bindLocalRot, 
            out Quaternion targetLocalRot, 
            out float weight)
        {
            // 1. 计算目标状态
            float targetBase = isBackstroke ? 180f : 0f;
            
            // 2. 平滑处理输入值
            float dt = Time.deltaTime;
            _currentBaseRoll = Mathf.LerpAngle(_currentBaseRoll, targetBase, dt * smoothSpeed);
            _currentAdditiveRoll = Mathf.LerpAngle(_currentAdditiveRoll, additiveRoll, dt * smoothSpeed);

            // 3. 计算当前节点的 Roll 总和
            // 【关键】翻转是不随 index 衰减的，保证尾部也能翻转到 180 度
            float flipComponent = _currentBaseRoll;

            // 侧倾 (Tilt) 部分根据位置衰减
            // 假设 index 0 是 Hip，index 3 是 Chest。我们通过 index 的差距计算衰减。
            int totalNodes = (spine != null && spine.Count > 0) ? spine.Count : 4;
            int maxIndex = totalNodes - 1; 
            
            // 这里的 (maxIndex - index) 确保了 Chest (index 3) 衰减为 0 次方（即 100%），Hip 衰减最重
            float decayFactor = Mathf.Pow(rollDecay, Mathf.Max(0, maxIndex - index));
            float tiltComponent = _currentAdditiveRoll * decayFactor;

            // 4. 合成最终旋转
            // 【注意】我们不再乘以 bindLocalRot，因为 Shadow 模式下我们只需要相对于“干净影子”的增量。
            // 这种纯 Z 轴输出将触发 Harmonizer 里的优化，不会稀释 Aim 系统的 Yaw/Pitch。
            float finalRoll = flipComponent + tiltComponent;
            targetLocalRot = Quaternion.Euler(0, 0, finalRoll);
            
            // 权重始终为 1，因为我们在 Harmonizer 中使用“最大值覆盖”策略处理 Roll
            weight = 1f;
            return true;
        }

        public void ToggleFlip() => isBackstroke = !isBackstroke;
        public void SetTilt(float angle) => additiveRoll = angle;
    }
}
using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    public class ExpSpineHarmonicProvider : ExpSpinePoseProviderBase
    {
        [Header("Classic Cos Wave Settings")]
        public float maxAmplitude = 35f;
        public float frequency = 2.0f;
        public float decayRate = 8.0f;
        [Tooltip("波从头传到尾的延迟系数")]
        public float waveOffset = 0.8f;

        private float _timer = 0f;
        private float _currentAmplitude = 0f;
        private float _currentFrequency = 2.0f;

        public override bool Evaluate(int index, SpineChainDefinition spine, Transform jointOrShadow, Quaternion bindLocalRot, out Quaternion targetLocalRot, out float weight)
        {
            targetLocalRot = bindLocalRot;
            weight = 0f;

            if (_currentAmplitude < 0.01f) return false;

            // 1. 计算相位：使用 Cos 产生从最大值开始的“蹬水”起始感
            // (total - 1 - index) 确保力从 Chest 传向 Hip
            int total = 4; // 假设脊椎 4 节
            float spatialOffset = (total - 1 - index) * waveOffset;
            float phase = (_timer * _currentFrequency * 2.0f * Mathf.PI) - spatialOffset;

            // 2. 经典 Cos 波形
            float cosWave = Mathf.Cos(phase);

            // 3. 空间增益：胸腔稳定逻辑
            // index 0 (Hip) -> 1.5倍振幅
            // index 3 (Chest) -> 0.4倍振幅 (尽可能稳定)
            float spatialGain = Mathf.Lerp(1.5f, 0.4f, (float)index / (total - 1));
            
            float angle = cosWave * _currentAmplitude * spatialGain;

            // 4. 垂直面运动 (绕 X 轴旋转)
            targetLocalRot = Quaternion.Euler(angle, 0, 0) * bindLocalRot;
            
            weight = globalWeight;
            return true;
        }

        private void Update()
        {
            if (_currentAmplitude > 0.01f)
            {
                _timer += Time.deltaTime;
                // 线性衰减，确保一次波动后迅速归零
                _currentAmplitude = Mathf.MoveTowards(_currentAmplitude, 0f, Time.deltaTime * decayRate);
            }
            else
            {
                _currentAmplitude = 0f;
                _timer = 0f;
            }
        }

        public void TriggerHarmonic(float intensity01) => TriggerSyncedHarmonic(0.3f, intensity01);

        public void TriggerSyncedHarmonic(float duration, float demand)
        {
            _timer = 0f;
            _currentFrequency = 1.0f / Mathf.Max(0.05f, duration);
            _currentAmplitude = maxAmplitude * demand;
        }
    }
}
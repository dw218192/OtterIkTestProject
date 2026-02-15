using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    public class ExpSpineHarmonicProvider : ExpSpinePoseProviderBase
    {
        [Header("Harmonic Settings")]
        public float maxAmplitude = 20f; // 调大初始振幅以便观察
        public float frequency = 2.5f;
        public float decayRate = 2.5f;
        public float waveOffset = 0.5f;

        private float _timer = 0f;
        private float _currentAmplitude = 0f;

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

            // 阈值保护，振幅太小时停止计算
            if (_currentAmplitude < 0.01f) return false;

            // 计算简谐波相位
            float phase = _timer * frequency * 2.0f * Mathf.PI;
            float spatialOffset = index * waveOffset;
            float angle = Mathf.Sin(phase - spatialOffset) * _currentAmplitude;

            // 【关键：轴向适配】如果 Y 是向上，X 通常是侧向
            // 这里我们使用 Euler 角，Harmonizer 会提取它并与其他意图混合
            targetLocalRot = Quaternion.Euler(angle, 0, 0) * bindLocalRot;
            
            // 确保权重足够大，不被 AimProvider 稀释
            weight = globalWeight;
            return true;
        }

        private void Update()
        {
            if (_currentAmplitude > 0f)
            {
                _timer += Time.deltaTime;
                // 线性+指数混合衰减，确保波形最后能平稳归零
                _currentAmplitude = Mathf.MoveTowards(_currentAmplitude, 0f, Time.deltaTime * decayRate);
            }
        }

        public void TriggerHarmonic(float intensity01)
        {
            _timer = 0f;
            _currentAmplitude = maxAmplitude * intensity01;
            // 调试打印：确认事件是否到达
            Debug.Log($"<color=orange>[Harmonic]</color> Triggered with intensity: {intensity01}");
        }
    }
}
using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    /// <summary>
    /// Provider 基类：仅输出旋转目标和权重。
    /// 不再处理 localPosition。
    /// </summary>
    public abstract class ExpSpinePoseProviderBase : MonoBehaviour
    {
        [Range(0f, 1f)]
        public float globalWeight = 1f;

        /// <summary>
        /// 告知 Chain 是否需要强制连续旋转路径（防止顺逆时针冲突）。
        /// 默认 false：使用标准最短路径。
        /// </summary>
        public virtual bool RequiresContinuousPath => false;

        /// <summary>
        /// 为指定索引的关节计算绝对本地目标旋转。
        /// </summary>
        /// <param name="index">关节索引</param>
        /// <param name="spine">脊椎定义引用（可为空；Shadow 模式可直接使用 joint/shadow）</param>
        /// <param name="jointOrShadow">当前正在评估的关节 Transform（或其 Shadow Transform；不要传 null）</param>
        /// <param name="bindLocalRot">该关节的静止本地旋转</param>
        /// <param name="targetLocalRot">输出：绝对本地目标旋转</param>
        /// <param name="weight">输出：该关节的局部权重 (0..1)</param>
        public abstract bool Evaluate(
            int index,
            SpineChainDefinition spine,
            Transform jointOrShadow,
            Quaternion bindLocalRot,
            out Quaternion targetLocalRot,
            out float weight);
    }
}
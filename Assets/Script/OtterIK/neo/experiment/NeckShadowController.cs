using UnityEngine;
using System.Collections.Generic;

namespace OtterIK.Neo.Experiment
{
    public class ExpShadowNeckController : MonoBehaviour
    {
        [Header("Infrastructure")]
        public SpineDecouplerWithShadows decoupler; 
        public int chestShadowIndex = 3;

        [Header("Neck Definition")]
        public Transform[] neckJoints; 
        public Transform aimTarget;

        [Header("Runtime Shadows (Read Only)")]
        public List<Transform> neckShadowNodes = new List<Transform>();

        [Header("Settings")]
        public ExpSpineRollFlipProvider flipProvider; 
        public float backstrokePitchBias = 35f;
        public float followSpeed = 20f;

        private Dictionary<Transform, Transform> _originalParents = new Dictionary<Transform, Transform>();
        private Vector3[] _initialLocalOffsets; // 【关键】存储原始骨骼长度和偏移

        void Awake()
        {
            if (neckJoints == null || neckJoints.Length == 0) return;

            GameObject neckRoot = new GameObject("Neck_Shadow_Root");
            neckRoot.transform.SetParent(this.transform, false);

            _initialLocalOffsets = new Vector3[neckJoints.Length];

            for (int i = 0; i < neckJoints.Length; i++)
            {
                Transform joint = neckJoints[i];
                _originalParents[joint] = joint.parent;

                // 计算相对于上一个节点的原始偏移 (用于锁定长度)
                if (i == 0) {
                    // 第一个脖子关节相对于胸腔的偏移由 SpineDecoupler 保证，我们这里记录它
                } else {
                    // 记录骨骼长度向量
                    _initialLocalOffsets[i] = neckJoints[i].position - neckJoints[i-1].position;
                }

                GameObject shadow = new GameObject(joint.name + "_Shadow");
                shadow.transform.SetParent(neckRoot.transform, false);
                shadow.transform.position = joint.position;
                shadow.transform.localRotation = Quaternion.identity;

                joint.SetParent(shadow.transform, true);
                neckShadowNodes.Add(shadow.transform);
            }
        }

        void LateUpdate()
        {
            if (neckShadowNodes.Count == 0 || decoupler == null || aimTarget == null) return;

            // 1. 获取影子胸腔底座
            Transform chestShadow = decoupler.shadowNodes[chestShadowIndex];
            bool isBack = flipProvider != null && flipProvider.isBackstroke;

            // 2. 在胸腔局部空间解算方向（完全不涉及位置）
            Vector3 localTargetPos = chestShadow.InverseTransformPoint(aimTarget.position);
            Vector3 finalLookDir = localTargetPos.normalized;

            if (isBack) {
                Vector3 chestLookDir = (Vector3.down + Vector3.forward * 0.3f).normalized;
                finalLookDir = Vector3.Slerp(chestLookDir, finalLookDir, 0.4f).normalized;
            }

            // 3. 级联更新影子节点：【先位置补偿，后应用旋转】
            for (int i = 0; i < neckShadowNodes.Count; i++)
            {
                // --- A. 严谨的位置锁定 (灵感来自 Harmonizer 阶段 3) ---
                if (i == 0) {
                    // 第一个影子关节永远钉在胸腔影子的位置上
                    neckShadowNodes[i].position = chestShadow.position;
                } else {
                    // 后续节点的位置 = 前一个节点的位置 + (前一个节点的旋转 * 原始长度偏移)
                    // 这样脖子永远不会被拉长或缩短
                    neckShadowNodes[i].position = neckShadowNodes[i-1].position + (neckShadowNodes[i-1].rotation * _initialLocalOffsets[i]);
                }

                // --- B. 旋转计算 ---
                float weight = (float)(i + 1) / neckShadowNodes.Count;
                Quaternion ikRot = Quaternion.FromToRotation(Vector3.forward, finalLookDir);
                Quaternion poseBias = isBack ? Quaternion.Euler(backstrokePitchBias * weight, 0, 0) : Quaternion.identity;

                // 计算目标旋转（基于胸腔旋转作为父级参考）
                // 这里的 * Quaternion.identity 是因为影子节点没有 bindPose 偏移
                Quaternion targetRot = chestShadow.rotation * poseBias * ikRot;

                // 平滑插值应用
                neckShadowNodes[i].rotation = Quaternion.Slerp(neckShadowNodes[i].rotation, targetRot, Time.deltaTime * followSpeed);
            }
        }

        void OnDisable()
        {
            foreach (var joint in neckJoints) {
                if (joint != null && _originalParents.ContainsKey(joint))
                    joint.SetParent(_originalParents[joint], true);
            }
        }
    }
}
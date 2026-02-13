using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    /// <summary>
    /// 基于脊椎影子系统的四肢划水控制器。
    /// 核心逻辑：检测脊椎（胸部）的角速度，自动将“外侧”或“向下侧”的肢体钉在水中（IK Target滞后），
    /// 从而产生划水推力的视觉效果。
    /// </summary>
    [DefaultExecutionOrder(110)] // 确保在 Decoupler 和 Harmonizer 之后运行
    public class ExpShadowLimbController : MonoBehaviour
    {
        [Header("References")]
        public SpineDecouplerWithShadows decoupler;
        [Tooltip("脊椎链中代表'胸部/肩膀'区域的索引。通常是靠近数组末尾的节点。")]
        public int chestShadowIndex = 5;

        [Header("Limbs Configuration")]
        public LimbEntry leftLimb;
        public LimbEntry rightLimb;

        [Header("Physics Sensing (Sensitivity)")]
        [Tooltip("判定阈值：左右得分差值超过此值时触发划水。")]
        public float sensitivityThreshold = 0.5f;
        [Tooltip("偏航权重 (Yaw)：身体转向时，外侧手划水。")]
        public float yawWeight = 1.2f;
        [Tooltip("横滚权重 (Roll)：身体翻滚时，下沉侧手划水（保持平衡）。")]
        public float rollWeight = 1.5f;
        [Tooltip("平滑时间：用于过滤角速度的噪点。")]
        public float sensingSmoothTime = 0.1f;

        [Header("Motion Settings")]
        [Tooltip("手臂自然伸展长度（Rest状态下的偏移量）。")]
        public float armLength = 0.5f;
        [Tooltip("最大划水角度：当IK点滞后超过此角度时，强制收回（Reset）。")]
        public float maxDragAngle = 60f;
        [Tooltip("回正速度：非划水状态下，手回到肩膀旁边的速度。")]
        public float recoverSpeed = 15f;
        [Tooltip("抓水力度 (0-1)：1=完全钉在世界空间不动，0=完全跟随身体。")]
        [Range(0f, 1f)] public float waterGrip = 0.95f;

        // Runtime State
        private Transform _chestShadow;
        private Quaternion _lastChestRot;
        private Vector3 _chestAngVel; // 估算的角速度向量
        private float _scoreDiff;     // 左分 - 右分
        private float _scoreVel;      // 用于 SmoothDamp

        [System.Serializable]
        public class LimbEntry
        {
            [Header("Setup")]
            public Transform shoulderBone; // 真实的肩膀骨骼（用于计算相对位置）
            public Transform ikTarget;     // 输出的 IK 目标点

            [Header("Runtime Debug")]
            public bool isPaddling;
            public Vector3 currentWorldPos;

            // 内部状态
            [HideInInspector] public Vector3 defaultOffsetLocal; // 初始化时计算
        }

        private void Start()
        {
            Initialize();
        }

        private void Initialize()
        {
            if (decoupler == null || decoupler.shadowNodes.Count == 0) return;

            // 1. 获取影子参考点 (Chest)
            // 确保索引安全
            int idx = Mathf.Clamp(chestShadowIndex, 0, decoupler.shadowNodes.Count - 1);
            _chestShadow = decoupler.shadowNodes[idx];
            _lastChestRot = _chestShadow.rotation;

            // 2. 初始化肢体偏移量
            // 我们假设 Start 时，IK Target 应该在肩膀的外侧
            SetupLimb(leftLimb, -_chestShadow.right);
            SetupLimb(rightLimb, _chestShadow.right);
        }

        private void SetupLimb(LimbEntry limb, Vector3 sideDir)
        {
            if (limb.shoulderBone == null || limb.ikTarget == null) return;
            
            // 计算 Rest 状态下，IK Target 应该在肩膀的什么相对位置
            // 这里简单地假设在肩膀的外侧 armLength 处
            // 注意：我们存储相对于 Shoulder Bone 的偏移，这样肩膀本身移动时也能跟随
            limb.defaultOffsetLocal = new Vector3(sideDir.x > 0 ? armLength : -armLength, 0, 0); 
            
            // 初始化位置
            limb.currentWorldPos = limb.shoulderBone.TransformPoint(limb.defaultOffsetLocal);
            limb.ikTarget.position = limb.currentWorldPos;
        }

        private void LateUpdate()
        {
            if (_chestShadow == null) return;

            float dt = Time.deltaTime;
            if (dt < 1e-5f) return;

            // --- 1. 计算脊椎(胸部)角速度 ---
            // 使用 Shadow Node 的旋转差值，这比 Rigidbody 更能反映"脊椎的弯曲意图"
            Quaternion deltaRot = _chestShadow.rotation * Quaternion.Inverse(_lastChestRot);
            deltaRot.ToAngleAxis(out float angle, out Vector3 axis);
            // 修正角度范围 (>180 变负)
            if (angle > 180f) angle -= 360f;
            
            Vector3 instantAngVel = axis * (angle * Mathf.Deg2Rad / dt);
            
            // 简单平滑一下，避免突变
            _chestAngVel = Vector3.Lerp(_chestAngVel, instantAngVel, dt * 20f);
            
            _lastChestRot = _chestShadow.rotation;

            // --- 2. 左右侧划水竞争判定 ---
            // 左肩向量(从胸到左肩近似向量)
            Vector3 leftRadius = -_chestShadow.right; 
            Vector3 rightRadius = _chestShadow.right;

            // 计算线速度 v = w x r
            Vector3 vLeft = Vector3.Cross(_chestAngVel, leftRadius);
            Vector3 vRight = Vector3.Cross(_chestAngVel, rightRadius);

            // 评分：向前(Forward)分量 + 向下(-Up)分量
            // 注意：这里用 _chestShadow.forward/up，因为它是干净的参考系
            float lScore = Vector3.Dot(vLeft, _chestShadow.forward) * yawWeight 
                         + Vector3.Dot(vLeft, -_chestShadow.up) * rollWeight;
            
            float rScore = Vector3.Dot(vRight, _chestShadow.forward) * yawWeight 
                         + Vector3.Dot(vRight, -_chestShadow.up) * rollWeight;

            float targetDiff = lScore - rScore;
            
            // 使用 SmoothDamp 过滤意图，避免高频抖动
            _scoreDiff = Mathf.SmoothDamp(_scoreDiff, targetDiff, ref _scoreVel, sensingSmoothTime);

            // --- 3. 更新状态 ---
            // 互斥逻辑：差值超过阈值，强者划水，弱者回正
            bool leftActive = _scoreDiff > sensitivityThreshold;
            bool rightActive = _scoreDiff < -sensitivityThreshold;

            UpdateLimb(leftLimb, leftActive, dt, -_chestShadow.right);
            UpdateLimb(rightLimb, rightActive, dt, _chestShadow.right);
        }

        private void UpdateLimb(LimbEntry limb, bool isActive, float dt, Vector3 sideDirWorld)
        {
            if (limb.ikTarget == null || limb.shoulderBone == null) return;

            // 计算理论上的"休息点" (跟随身体移动)
            // 这里我们希望 Rest 点是相对于肩膀的，但也稍微受到 Shadow 旋转的影响
            // 为了简单稳定，我们直接取 ShoulderBone 的相对点
            Vector3 restPos = limb.shoulderBone.position + (limb.shoulderBone.rotation * limb.defaultOffsetLocal);
            // 或者强制使用 Shadow 的方向（如果骨骼本身动画太乱）：
            // Vector3 restPos = limb.shoulderBone.position + sideDirWorld * armLength;

            if (isActive)
            {
                limb.isPaddling = true;

                // --- 划水逻辑 (Anchoring) ---
                // 目标：试图留在上一帧的世界位置 (limb.currentWorldPos)
                // 实际上我们是在 currentWorldPos 和 restPos 之间插值，但偏向 currentWorldPos
                
                // 1. 计算如果完全不跟随身体，点应该在哪
                Vector3 anchoredPos = limb.currentWorldPos; // 上一帧位置

                // 2. 施加一点点"抓水力度"的混合，防止完全脱节
                Vector3 targetPos = Vector3.Lerp(restPos, anchoredPos, waterGrip);

                // 3. 角度限制 (Max Drag Angle)
                // 既然是划水，手不能被拉到无限远。计算 手-肩 向量 与 身体横向向量 的夹角
                Vector3 shoulderToHand = targetPos - limb.shoulderBone.position;
                float angle = Vector3.Angle(sideDirWorld, shoulderToHand);

                // 如果角度太大（手被甩到太后面了），强制把它拉回来
                if (angle > maxDragAngle || shoulderToHand.sqrMagnitude > (armLength * armLength * 2.5f))
                {
                    // 超过极限，重置为 Rest，就像"划水划完了，把手提起来"
                    // 这里做一个快速的拉回，而不是瞬间跳变，会更自然
                    limb.currentWorldPos = Vector3.MoveTowards(limb.currentWorldPos, restPos, dt * recoverSpeed * 2f);
                    limb.isPaddling = false; // 强制打断，直到下一次判定周期
                }
                else
                {
                    limb.currentWorldPos = targetPos;
                }
            }
            else
            {
                limb.isPaddling = false;

                // --- 回正逻辑 (Recovering) ---
                // 快速平滑地回到 Rest 点
                limb.currentWorldPos = Vector3.Lerp(limb.currentWorldPos, restPos, dt * recoverSpeed);
            }

            // 应用位置
            limb.ikTarget.position = limb.currentWorldPos;
            
            // 可选：让 IK Target 的旋转也跟随肩膀
            limb.ikTarget.rotation = limb.shoulderBone.rotation;
        }

        private void OnDrawGizmos()
        {
            if (!Application.isPlaying || _chestShadow == null) return;

            DrawLimbGizmo(leftLimb, Color.cyan);
            DrawLimbGizmo(rightLimb, Color.magenta);

            // 画出胸部的角速度意图向量
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(_chestShadow.position, _chestAngVel * 0.5f);
        }

        private void DrawLimbGizmo(LimbEntry limb, Color c)
        {
            if (limb.ikTarget == null) return;
            Gizmos.color = limb.isPaddling ? Color.red : c;
            Gizmos.DrawWireSphere(limb.ikTarget.position, 0.1f);
            Gizmos.DrawLine(limb.shoulderBone.position, limb.ikTarget.position);
        }
    }
}
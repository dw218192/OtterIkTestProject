using UnityEngine;

public class IndirectIKFullReferenceSimulator : MonoBehaviour
{
    [Header("核心节点")]
    public Transform chestEffector;
    public Transform leftShoulder;
    public Transform rightShoulder;

    [Header("间接IK参数")]
    public float maxAngleL = 60f;
    public float armOffset = 0.5f;

    [Header("动力学权重 (判定修正)")]
    [Tooltip("偏航权重。现在：右转时左侧向前冲，左侧变红。")]
    public float yawWeight = 1.0f;
    [Tooltip("横滚权重。现在：向下压水的一侧变红。")]
    public float rollWeight = 1.5f;
    public float deadZone = 0.005f;

    // 逻辑锚点世界坐标
    private Vector3 worldLeftPoint;
    private Vector3 worldRightPoint;
    
    // 物理状态追踪
    private Quaternion lastRot;
    private Vector3 lastPosition; // 修复了报错：添加此定义
    
    private bool isLeftOuter, isRightOuter;

    void Start()
    {
        if (chestEffector) {
            lastRot = chestEffector.rotation;
            lastPosition = chestEffector.position; // 初始化位置
            ResetPoint(true); 
            ResetPoint(false);
        }
    }

    void Update()
    {
        if (!chestEffector) return;

        // 1. 获取每一帧的旋转和位移增量
        Quaternion deltaRot = chestEffector.rotation * Quaternion.Inverse(lastRot);
        Vector3 worldPosDelta = chestEffector.position - lastPosition;
        
        // 计算角速度（世界空间向量）
        deltaRot.ToAngleAxis(out float angle, out Vector3 axis);
        Vector3 angularVelWorld = axis * (angle * Mathf.Deg2Rad / Time.deltaTime);

        // 2. 竞争判定：谁的“蓄力感”更强？
        float leftScore = CalculateSwimmingScore(leftShoulder, angularVelWorld);
        float rightScore = CalculateSwimmingScore(rightShoulder, angularVelWorld);

        // 强制互斥判定：分高者为红色外侧
        float diff = leftScore - rightScore;
        if (Mathf.Abs(diff) > deadZone) {
            isLeftOuter = diff > 0;
            isRightOuter = diff < 0;
        } else {
            isLeftOuter = isRightOuter = false;
        }

        // 3. 执行锚点物理模拟
        UpdateAnchor(leftShoulder, ref worldLeftPoint, worldPosDelta, isLeftOuter, true);
        UpdateAnchor(rightShoulder, ref worldRightPoint, worldPosDelta, isRightOuter, false);

        // 4. 存档当前帧状态
        lastRot = chestEffector.rotation;
        lastPosition = chestEffector.position;
    }

    float CalculateSwimmingScore(Transform shoulder, Vector3 angVelWorld)
    {
        // 计算瞬时线速度 v = ω × r
        Vector3 radius = shoulder.position - chestEffector.position;
        Vector3 v = Vector3.Cross(angVelWorld, radius);

        // --- 核心修正：游泳划水判定逻辑 ---
        
        // Yaw：向正前方向（+Forward）冲的一侧是外侧划水肢体
        // 比如：右转时，身体顺时针转，左肩会向前甩出。此时 v 与 Forward 同向。
        float yawPart = Vector3.Dot(v, chestEffector.forward) * yawWeight;
        
        // Roll：向正下方（-Up）切入的一侧是外侧支撑肢体
        // 比如：向右横滚，右肩下沉。此时 v 与 -Up 同向。
        float rollPart = Vector3.Dot(v, -chestEffector.up) * rollWeight;

        return yawPart + rollPart;
    }

    void UpdateAnchor(Transform shoulder, ref Vector3 worldPoint, Vector3 posDelta, bool isOuter, bool isLeft)
    {
        // 位移补偿：确保直线前进时不误触发旋转逻辑
        worldPoint += posDelta;

        Vector3 sideDir = isLeft ? -chestEffector.right : chestEffector.right;
        Vector3 restPos = shoulder.position + sideDir * armOffset;

        if (isOuter) {
            // 外侧锁定：逻辑点钉在水中，肩膀向前/下运动拉开距离
            float L = Vector3.Angle(sideDir, (worldPoint - shoulder.position).normalized);
            if (L > maxAngleL) ResetPoint(isLeft);
        } else {
            // 内侧回收：快速平滑回位
            worldPoint = Vector3.Lerp(worldPoint, restPos, Time.deltaTime * 20f);
        }
    }

    public void ResetPoint(bool isLeft)
    {
        if (isLeft) worldLeftPoint = leftShoulder.position - chestEffector.right * armOffset;
        else worldRightPoint = rightShoulder.position + chestEffector.right * armOffset;
    }

    private void OnDrawGizmos()
    {
        if (!Application.isPlaying || !chestEffector) return;

        // 蓝线=前，绿线=上
        Gizmos.color = Color.blue; Gizmos.DrawRay(chestEffector.position, chestEffector.forward * 1f);
        Gizmos.color = Color.green; Gizmos.DrawRay(chestEffector.position, chestEffector.up * 1f);

        DrawDebug(leftShoulder, worldLeftPoint, isLeftOuter, true);
        DrawDebug(rightShoulder, worldRightPoint, isRightOuter, false);
    }

    void DrawDebug(Transform shoulder, Vector3 point, bool isOuter, bool isLeft)
    {
        Gizmos.color = isOuter ? Color.red : new Color(0.3f, 0.3f, 0.3f, 0.2f);
        Gizmos.DrawSphere(point, 0.06f);
        if (isOuter) {
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(shoulder.position, point);
        }
    }
}
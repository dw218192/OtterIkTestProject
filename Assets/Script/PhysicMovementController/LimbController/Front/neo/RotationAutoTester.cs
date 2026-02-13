using UnityEngine;

public class RotationAutoTester : MonoBehaviour
{
    [Header("旋转速度 (度/秒)")]
    public Vector3 rotationSpeed = new Vector3(50, 50, 50);
    
    [Header("激活轴向 (可多选以实现斜着转)")]
    public bool rotateX = false; // Pitch: 俯仰
    public bool rotateY = false; // Yaw: 水平
    public bool rotateZ = false; // Roll: 横滚

    [Header("旋转空间")]
    public Space rotationSpace = Space.Self; // 建议使用 Self 以跟随物体轴向

    void Update()
    {
        // 构造复合旋转矢量
        Vector3 currentRotation = Vector3.zero;

        if (rotateX) currentRotation.x = rotationSpeed.x;
        if (rotateY) currentRotation.y = rotationSpeed.y;
        if (rotateZ) currentRotation.z = rotationSpeed.z;

        // 如果没有任何轴被激活，则不执行旋转
        if (currentRotation != Vector3.zero)
        {
            transform.Rotate(currentRotation * Time.deltaTime, rotationSpace);
        }
    }
}
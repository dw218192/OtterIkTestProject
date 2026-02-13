using UnityEngine;
using OtterIK.Neo.Experiment;

public class SpineFlipTestController : MonoBehaviour
{
    public ExpSpineRollFlipProvider rollProvider;

    [Header("Test Config")]
    public float tiltTestAngle = 30f;

    void Update()
    {
        if (rollProvider == null) return;

        // --- Pattern 1: Flip (持久翻转) ---
        if (Input.GetKeyDown(KeyCode.F))
        {
            rollProvider.isBackstroke = !rollProvider.isBackstroke;
            Debug.Log($"<color=cyan>Flip Triggered: IsBackstroke = {rollProvider.isBackstroke}</color>");
        }

        // --- Pattern 2: Side Tilt (单次旋转测试) ---
        // 按下 E 向右倾斜，按下 Q 向左倾斜
        if (Input.GetKey(KeyCode.E))
        {
            rollProvider.additiveRoll = tiltTestAngle;
        }
        else if (Input.GetKey(KeyCode.Q))
        {
            rollProvider.additiveRoll = -tiltTestAngle;
        }
        else
        {
            // 自动 Recover (Pattern 2 的特性：放手回正)
            rollProvider.additiveRoll = 0f;
        }
    }
}
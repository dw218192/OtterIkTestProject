using UnityEngine;

[DisallowMultipleComponent]
public class WavingTestRig : MonoBehaviour
{
    [Header("Refs")]
    public SpineDelayChain delayChain;
    public SprintPulseSystem pulseSystem;

    [Header("Gizmos")]
    public bool drawGizmos = true;
    public float axisLen = 0.22f;
    public float sphereRadius = 0.02f;

    [Header("Labels")]
    public bool drawLabels = true;

    [Header("Test Input")]
    public bool triggerPulseWithKey = true;
    public KeyCode triggerKey = KeyCode.Space;

    [Header("Manual Pulse Params")]
    public float manualCycleDurationK = 0.33f;
    public float manualIntervalI = 0.17f;
    public float manualDemand01 = 1f;
    public float manualOscillationsPerCycle = 1f;
    public float manualResidualTime = -1f;  // -1 => use system default

    private void Update()
    {
        if (!triggerPulseWithKey || pulseSystem == null) return;

        if (Input.GetKeyDown(triggerKey))
        {
            float? resOverride = null;
            if (manualResidualTime >= 0f) resOverride = manualResidualTime;

            pulseSystem.TriggerManualPulse(
                cycleDuration: manualCycleDurationK,
                intervalDuration: manualIntervalI,
                demand01: manualDemand01,
                oscPerCycleOverride: manualOscillationsPerCycle,
                travelCyclesOverride: null,
                residualTimeOverride: resOverride
            );
        }
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmos || delayChain == null || delayChain.nodes == null) return;

        float now = Application.isPlaying ? Time.time : 0f;

        for (int i = 0; i < delayChain.nodes.Length; i++)
        {
            delayChain.GetDelayedPose(i, now, out var p, out var r, out var f, out var u, out var rt);

            // Prefer bone position for gizmo anchors so visuals stay glued to the rig
            var n = delayChain.nodes[i];
            Vector3 anchorPos = (n != null && n.bone != null) ? n.bone.position : p;

            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(anchorPos, sphereRadius);

            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(anchorPos, anchorPos + f * axisLen);

            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(anchorPos, anchorPos + u * axisLen * 0.85f);

            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(anchorPos, anchorPos + rt * axisLen * 0.7f);

#if UNITY_EDITOR
            if (drawLabels)
            {
                float delay = delayChain.GetNodeDelaySeconds(i);
                string name = (n != null && n.bone != null) ? n.bone.name : $"node{i}";
                UnityEditor.Handles.Label(anchorPos + Vector3.up * 0.03f,
                    $"{name}\nidx={delayChain.GetDistanceFactor01(i):0.00}  delay={delay:0.000}s");
            }
#endif
        }
    }
}

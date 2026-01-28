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
    public float manualAmplitudeDeg = 10f;
    public float manualFrequencyHz = 3.0f;
    public float manualDecayPerSec = 2.5f;
    public float manualTravelCycles = 1.2f;

    private void Update()
    {
        if (!triggerPulseWithKey || pulseSystem == null) return;

        if (Input.GetKeyDown(triggerKey))
        {
            pulseSystem.TriggerManual(
                amplitudeDeg: manualAmplitudeDeg,
                frequencyHz: manualFrequencyHz,
                decayPerSec: manualDecayPerSec,
                travelCycles: manualTravelCycles
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

            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(p, sphereRadius);

            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(p, p + f * axisLen);

            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(p, p + u * axisLen * 0.85f);

            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(p, p + rt * axisLen * 0.7f);

#if UNITY_EDITOR
            if (drawLabels)
            {
                var n = delayChain.nodes[i];
                float delay = delayChain.GetNodeDelaySeconds(i);
                string name = (n != null && n.bone != null) ? n.bone.name : $"node{i}";
                UnityEditor.Handles.Label(p + Vector3.up * 0.03f,
                    $"{name}\nidx={delayChain.GetDistanceFactor01(i):0.00}  delay={delay:0.000}s");
            }
#endif
        }
    }
}

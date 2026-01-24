using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Dedicated visualizer (runtime + editor):
/// - Draws trajectory contours for each leg (using HindPaddleTrajectoryRB.EvaluateTrajectoryPointWorld)
/// - Draws rest points, current IK target points
/// - Draws current phase markers for move/idle channels
/// - Shows scheduler/player status text (cooldowns, pending delays, strengths)
///
/// This script is an observer only: it does not alter runtime behavior.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class HindPaddleDebugVisualizerRB : MonoBehaviour
{
    [Header("References")]
    public HindPaddleDriverRB driver;
    public HindPaddleTrajectoryRB trajectory;

    [Header("When to draw")]
    public bool drawInEditMode = true;
    public bool drawInPlayMode = true;

    [Header("What to draw")]
    public bool drawRestPoints = true;
    public bool drawIkTargets = true;
    public bool drawContours = true;
    public bool drawPhaseMarkers = true;
    public bool drawText = true;

    [Header("Contour sampling")]
    [Range(8, 256)] public int segments = 64;
    [Range(0f, 1f)] public float contourDemand01 = 1f;

    [Header("Marker sizes")]
    public float restRadius = 0.015f;
    public float ikRadius = 0.02f;
    public float phaseRadius = 0.022f;

    [Header("Manual preview (Edit Mode)")]
    public bool showManualPreviewPhaseInEditMode = true;
    [Range(0f, 1f)] public float gizmoPreviewPhase01 = 0.0f;
    [Range(0f, 1f)] public float gizmoPreviewStrength01 = 1.0f; // 用于预览 demand/strength

    private void Reset()
    {
        if (driver == null) driver = GetComponent<HindPaddleDriverRB>();
        if (trajectory == null) trajectory = GetComponent<HindPaddleTrajectoryRB>();
    }

    private bool ShouldDrawNow()
    {
        return Application.isPlaying ? drawInPlayMode : drawInEditMode;
    }

    private void OnDrawGizmos()
    {
        if (!ShouldDrawNow()) return;
        if (driver == null || trajectory == null) return;
        if (driver.rootSpace == null || trajectory.rootSpace == null) return;

        // Keep edit-mode rest up to date (driver already does this; but visualizer may be on another object)
        if (!Application.isPlaying)
        {
            // nothing required; we trust driver has valid restLocalPos
        }

        DrawLeg(driver.leftLeg, isLeft: true);
        DrawLeg(driver.rightLeg, isLeft: false);

#if UNITY_EDITOR
        if (drawText)
            DrawTextOverlay();
#endif
    }

    private void DrawLeg(HindPaddleDriverRB.Leg leg, bool isLeft)
    {
        if (leg == null) return;

        Transform root = driver.rootSpace;
        if (root == null) return;

        Vector3 restW = root.TransformPoint(leg.restLocalPos);

        if (drawRestPoints)
        {
            Gizmos.color = new Color(1f, 1f, 1f, 0.7f);
            Gizmos.DrawSphere(restW, restRadius);
        }

        if (drawIkTargets && leg.ikTarget != null)
        {
            Gizmos.color = isLeft
                ? new Color(0.2f, 1.0f, 0.35f, 0.95f)
                : new Color(0.2f, 0.6f, 1.00f, 0.95f);

            Gizmos.DrawSphere(leg.ikTarget.position, ikRadius);
            Gizmos.DrawLine(restW, leg.ikTarget.position);
        }

        if (drawContours)
        {
            int n = Mathf.Clamp(segments, 8, 256);
            float d = Mathf.Clamp01(contourDemand01);

            Gizmos.color = isLeft
                ? new Color(0.2f, 1.0f, 0.35f, 0.35f)
                : new Color(0.2f, 0.6f, 1.00f, 0.35f);

            Vector3 prev = trajectory.EvaluateTrajectoryPointWorld(leg.binding, leg.restLocalPos, 0f, d);
            for (int i = 1; i <= n; i++)
            {
                float u = i / (float)n;
                Vector3 p = trajectory.EvaluateTrajectoryPointWorld(leg.binding, leg.restLocalPos, u, d);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
            Gizmos.DrawLine(prev, trajectory.EvaluateTrajectoryPointWorld(leg.binding, leg.restLocalPos, 0f, d));
        }

        if (drawPhaseMarkers)
        {
            float phase01 = 0f;
            float strength01 = 0f;
            bool shouldDraw = false;

            if (Application.isPlaying)
            {
                var s = driver.GetDebugSnapshot();

                if (s.moveActive)
                {
                    phase01 = s.movePhase01;
                    strength01 = Mathf.Clamp01(s.moveDemand01);
                    shouldDraw = strength01 > 0f;
                }
                else
                {
                    bool idleActive = isLeft ? s.idleLActive : s.idleRActive;
                    phase01 = isLeft ? s.idleLPhase01 : s.idleRPhase01;
                    strength01 = idleActive ? (isLeft ? s.idleLStrength : s.idleRStrength) : 0f;
                    shouldDraw = idleActive && strength01 > 0f;
                }
            }
            else
            {
                // Edit Mode manual preview
                if (showManualPreviewPhaseInEditMode)
                {
                    phase01 = Mathf.Clamp01(gizmoPreviewPhase01);
                    strength01 = Mathf.Clamp01(gizmoPreviewStrength01);
                    shouldDraw = strength01 > 0f;
                }
            }

            if (shouldDraw)
            {
                Vector3 marker = trajectory.EvaluateTrajectoryPointWorld(
                    leg.binding,
                    leg.restLocalPos,
                    phase01,
                    strength01
                );

                Gizmos.color = Color.yellow;
                Gizmos.DrawSphere(marker, phaseRadius);
                Gizmos.DrawLine(restW, marker);
            }
        }
    }

#if UNITY_EDITOR
    private void DrawTextOverlay()
    {
        if (driver == null) return;

        var s = driver.GetDebugSnapshot();

        Vector3 pos = (driver.rootSpace != null) ? driver.rootSpace.position : driver.transform.position;
        pos += Vector3.up * 0.25f;

        string mode = s.moveActive ? "MOVE" : ((s.idleLActive || s.idleRActive) ? "IDLE" : "REST");

        trajectory.GetTurnContext(out _, out _, out _, out float turnSign, out float turnAmount01);

        string line1 = $"HindPaddle [{mode}]  turnSign={turnSign:0}  turn01={turnAmount01:0.00}";
        string line2 = $"Move: {(s.moveActive ? "ON" : "OFF")}  phase={s.movePhase01:0.00}  demand={s.moveDemand01:0.00}  dur={s.moveDuration:0.00}";
        string line3 = $"Idle L: {(s.idleLActive ? "ON" : "OFF")} p={s.idleLPhase01:0.00} str={s.idleLStrength:0.00}  cd={s.cdL:0.00}  sched={(s.schedL ? $"T-{s.schedLT:0.00}" : "no")}";
        string line4 = $"Idle R: {(s.idleRActive ? "ON" : "OFF")} p={s.idleRPhase01:0.00} str={s.idleRStrength:0.00}  cd={s.cdR:0.00}  sched={(s.schedR ? $"T-{s.schedRT:0.00}" : "no")}";

        Handles.color = Color.white;
        Handles.Label(pos, line1);
        Handles.Label(pos + Vector3.up * 0.03f, line2);
        Handles.Label(pos + Vector3.up * 0.06f, line3);
        Handles.Label(pos + Vector3.up * 0.09f, line4);
    }
#endif
}

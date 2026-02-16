using UnityEngine;
using OtterIK.Neo.Experiment;

public class SpineHarmonicTester : MonoBehaviour
{
    public ExpSpineHarmonicProvider provider;
    public KeyCode testKey = KeyCode.H;

    void Update()
    {
        if (Input.GetKeyDown(testKey))
        {
            if (provider == null) provider = FindFirstObjectByType<ExpSpineHarmonicProvider>();
            if (provider != null) provider.TriggerHarmonic(1.0f);
        }
    }
}
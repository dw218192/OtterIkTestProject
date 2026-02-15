using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    public class SpineHarmonicTester : MonoBehaviour
    {
        public ExpSpineHarmonicProvider provider;
        public KeyCode testKey = KeyCode.H;
        [Range(0, 1)] public float testIntensity = 1.0f;

        void Update()
        {
            if (Input.GetKeyDown(testKey))
            {
                Debug.Log($"<color=cyan>SpineHarmonicTester triggered</color> (key={testKey}, intensity={testIntensity:0.###}, provider={(provider != null ? provider.name : "null")})");

                if (provider != null)
                    provider.TriggerHarmonic(testIntensity);
                else
                    Debug.LogWarning($"SpineHarmonicTester: no provider set on '{name}', harmonic not triggered.");
            }
        }
    }
}
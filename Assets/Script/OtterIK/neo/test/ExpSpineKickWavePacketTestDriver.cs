using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    /// <summary>
    /// Test driver for ExpSpineKickWavePacketProvider.
    /// - Press Space to trigger a single kick wave packet.
    /// - Optional auto-loop kicks.
    /// - Hotkeys to tune amplitude/duration/width/wavelength/half-life and direction.
    /// </summary>
    public class ExpSpineKickWavePacketTestDriver : MonoBehaviour
    {
        [Header("Target Provider")]
        public ExpSpineKickWavePacketProvider provider;

        [Header("Kick Trigger")]
        [Range(0f, 1f)] public float demand01 = 1f;
        public float kickDuration = 0.32f;

        [Tooltip("Auto trigger kicks repeatedly (useful to see continuous swimming).")]
        public bool autoLoop = false;

        [Tooltip("Seconds between auto kicks.")]
        public float autoInterval = 0.55f;

        float _nextAutoTime;

        [Header("Hotkeys")]
        public KeyCode kickKey = KeyCode.Space;
        public KeyCode toggleAutoKey = KeyCode.T;
        public KeyCode reverseDirectionKey = KeyCode.R;

        // Parameter tuning keys
        public KeyCode ampUpKey = KeyCode.Equals;   // '='
        public KeyCode ampDownKey = KeyCode.Minus;  // '-'
        public KeyCode durUpKey = KeyCode.RightBracket;  // ']'
        public KeyCode durDownKey = KeyCode.LeftBracket; // '['
        public KeyCode widthUpKey = KeyCode.Period; // '.'
        public KeyCode widthDownKey = KeyCode.Comma; // ','
        public KeyCode waveLenUpKey = KeyCode.Quote; // '''
        public KeyCode waveLenDownKey = KeyCode.Semicolon; // ';'
        public KeyCode halfLifeUpKey = KeyCode.Alpha2; // '2'
        public KeyCode halfLifeDownKey = KeyCode.Alpha1; // '1'

        [Header("Steps")]
        public float ampStepDeg = 2f;
        public float durStep = 0.03f;
        public float widthStepJoints = 0.2f;
        public float waveLenStepJoints = 0.5f;
        public float halfLifeStep = 0.01f;

        void Reset()
        {
            if (provider == null) provider = GetComponent<ExpSpineKickWavePacketProvider>();
        }

        void Start()
        {
            _nextAutoTime = Time.time + autoInterval;
        }

        void Update()
        {
            if (provider == null) return;

            // Manual kick
            if (Input.GetKeyDown(kickKey))
                provider.TriggerKick(demand01, kickDuration);

            // Toggle auto
            if (Input.GetKeyDown(toggleAutoKey))
                autoLoop = !autoLoop;

            // Reverse travel direction
            if (Input.GetKeyDown(reverseDirectionKey))
                provider.travelDirection = -provider.travelDirection;

            // Tune amplitude
            if (Input.GetKeyDown(ampUpKey))
                provider.maxAmplitudeDeg += ampStepDeg;
            if (Input.GetKeyDown(ampDownKey))
                provider.maxAmplitudeDeg = Mathf.Max(0f, provider.maxAmplitudeDeg - ampStepDeg);

            // Tune duration (also sync to local kickDuration)
            if (Input.GetKeyDown(durUpKey))
                kickDuration = Mathf.Min(2f, kickDuration + durStep);
            if (Input.GetKeyDown(durDownKey))
                kickDuration = Mathf.Max(0.05f, kickDuration - durStep);

            // Tune packet width
            if (Input.GetKeyDown(widthUpKey))
                provider.packetWidthJoints = Mathf.Min(20f, provider.packetWidthJoints + widthStepJoints);
            if (Input.GetKeyDown(widthDownKey))
                provider.packetWidthJoints = Mathf.Max(0.2f, provider.packetWidthJoints - widthStepJoints);

            // Tune carrier wavelength
            if (Input.GetKeyDown(waveLenUpKey))
                provider.wavelengthJoints = Mathf.Min(50f, provider.wavelengthJoints + waveLenStepJoints);
            if (Input.GetKeyDown(waveLenDownKey))
                provider.wavelengthJoints = Mathf.Max(0.5f, provider.wavelengthJoints - waveLenStepJoints);

            // Tune half-life
            if (Input.GetKeyDown(halfLifeUpKey))
                provider.halfLife = Mathf.Min(1.0f, provider.halfLife + halfLifeStep);
            if (Input.GetKeyDown(halfLifeDownKey))
                provider.halfLife = Mathf.Max(0.005f, provider.halfLife - halfLifeStep);

            // Auto loop kick
            if (autoLoop && Time.time >= _nextAutoTime)
            {
                provider.TriggerKick(demand01, kickDuration);
                _nextAutoTime = Time.time + Mathf.Max(0.05f, autoInterval);
            }
        }

        void OnGUI()
        {
            if (provider == null) return;

            GUILayout.BeginArea(new Rect(12, 12, 620, 270), GUI.skin.box);
            GUILayout.Label("<b>Kick Wave Packet Test Driver</b>", new GUIStyle(GUI.skin.label) { richText = true });

            GUILayout.Label($"Kick: {kickKey}   AutoLoop: {autoLoop} (toggle {toggleAutoKey})   Reverse: {reverseDirectionKey}");
            GUILayout.Label($"demand01: {demand01:F2}   kickDuration: {kickDuration:F2}s   autoInterval: {autoInterval:F2}s");

            GUILayout.Space(6);
            GUILayout.Label($"Provider.totalJoints: {provider.totalJoints}");
            GUILayout.Label($"maxAmplitudeDeg: {provider.maxAmplitudeDeg:F1}  (-/=)");
            GUILayout.Label($"packetWidthJoints: {provider.packetWidthJoints:F2}  (,/.)");
            GUILayout.Label($"wavelengthJoints: {provider.wavelengthJoints:F2}  (; / ')");
            GUILayout.Label($"halfLife: {provider.halfLife:F3}  (1/2)");
            GUILayout.Label($"travelDirection: {provider.travelDirection}  (+1 hip->chest, -1 chest->hip)");

            GUILayout.Space(8);
            GUILayout.Label("Suggested start: amp 25~35, width 2~3, wavelength 5~8, duration 0.28~0.45, halfLife 0.08~0.14");
            GUILayout.EndArea();
        }
    }
}

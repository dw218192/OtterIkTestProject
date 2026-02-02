using UnityEngine;

[DisallowMultipleComponent]
public class FlipTriggerTest : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("Assign the SpineFlipRollProvider you want to trigger.")]
    public SpineFlipRollProvider flipProvider;

    [Header("Input")]
    public KeyCode triggerKey = KeyCode.F;

    [Tooltip("If true, triggers once on key down.")]
    public bool triggerOnKeyDown = true;

    [Tooltip("If true, also triggers repeatedly while key is held (not recommended for flips).")]
    public bool triggerWhileHeld = false;

    [Tooltip("Min seconds between triggers (prevents spamming).")]
    [Range(0f, 2f)]
    public float cooldownSeconds = 0.15f;

    private float _nextAllowedTime;

    private void Reset()
    {
        // Try to auto-find on the same GameObject
        flipProvider = GetComponent<SpineFlipRollProvider>();
        triggerKey = KeyCode.F;
        triggerOnKeyDown = true;
        triggerWhileHeld = false;
        cooldownSeconds = 0.15f;
    }

    private void Awake()
    {
        if (flipProvider == null)
            flipProvider = GetComponent<SpineFlipRollProvider>();
    }

    private void Update()
    {
        if (flipProvider == null) return;

        bool wantTrigger = false;

        if (triggerOnKeyDown && Input.GetKeyDown(triggerKey))
            wantTrigger = true;

        if (triggerWhileHeld && Input.GetKey(triggerKey))
            wantTrigger = true;

        if (!wantTrigger) return;

        if (Time.time < _nextAllowedTime) return;
        _nextAllowedTime = Time.time + Mathf.Max(0f, cooldownSeconds);

        flipProvider.TriggerFlip();
    }

    /// <summary>
    /// For UI Button OnClick or other scripts.
    /// </summary>
    public void Trigger()
    {
        if (flipProvider == null) return;

        if (Time.time < _nextAllowedTime) return;
        _nextAllowedTime = Time.time + Mathf.Max(0f, cooldownSeconds);

        flipProvider.TriggerFlip();
    }
}

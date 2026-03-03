using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// On-screen debug console for WebGL builds. Captures all Debug.Log output
/// and renders it as an IMGUI overlay. Toggle with backtick (`) key.
/// Only active in Development Builds.
/// </summary>
public class ScreenConsole : MonoBehaviour
{
    struct LogEntry
    {
        public string message;
        public LogType type;
    }

    const int MaxEntries = 200;

    readonly List<LogEntry> _logs = new();
    Vector2 _scrollPosition;
    bool _visible;

    void OnEnable()
    {
        if (!Debug.isDebugBuild) { enabled = false; return; }
        Application.logMessageReceived += HandleLog;
    }

    void OnDisable()
    {
        Application.logMessageReceived -= HandleLog;
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.BackQuote))
            _visible = !_visible;
    }

    void HandleLog(string message, string stackTrace, LogType type)
    {
        _logs.Add(new LogEntry { message = message, type = type });
        if (_logs.Count > MaxEntries)
            _logs.RemoveAt(0);
        _scrollPosition = new Vector2(0, float.MaxValue);
    }

    void OnGUI()
    {
        if (!_visible) return;

        float w = Screen.width * 0.5f;
        float h = Screen.height * 0.6f;
        GUILayout.BeginArea(new Rect(10, 10, w, h), GUI.skin.box);
        _scrollPosition = GUILayout.BeginScrollView(_scrollPosition);

        foreach (var log in _logs)
        {
            GUI.contentColor = log.type switch
            {
                LogType.Error or LogType.Exception => Color.red,
                LogType.Warning => Color.yellow,
                _ => Color.white,
            };
            GUILayout.Label(log.message);
        }

        GUILayout.EndScrollView();
        GUI.contentColor = Color.white;

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Clear")) _logs.Clear();
        if (GUILayout.Button("Close")) _visible = false;
        GUILayout.EndHorizontal();

        GUILayout.EndArea();
    }
}

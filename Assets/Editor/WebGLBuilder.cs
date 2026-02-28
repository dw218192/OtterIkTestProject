using UnityEditor;
using UnityEditor.Build.Reporting;
using System.Linq;

public static class WebGLBuilder
{
    public static void Build()
    {
        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        var report = BuildPipeline.BuildPlayer(
            scenes, "Builds/WebGL", BuildTarget.WebGL, BuildOptions.None);

        if (report.summary.result != BuildResult.Succeeded)
        {
            EditorApplication.Exit(1);
        }
    }
}

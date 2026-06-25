#if UNITY_EDITOR && UNITY_IOS
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace SensorFlex.Recorder.Editor
{
    static class SensorFlexRecorderPostBuild
    {
        [PostProcessBuild(100)]
        static void OnPostProcessBuild(BuildTarget target, string buildPath)
        {
            if (target != BuildTarget.iOS) return;

            string pbxPath = PBXProject.GetPBXProjectPath(buildPath);
            var project = new PBXProject();
            project.ReadFromFile(pbxPath);

            // libcompression provides compression_encode_buffer (COMPRESSION_LZ4_RAW).
            // It ships as libcompression.tbd (not a .framework bundle), so it must be
            // linked via OTHER_LDFLAGS rather than AddFrameworkToProject.
            // Plugin .mm files compile into UnityFramework in Unity 6.
            string frameworkTarget = project.GetUnityFrameworkTargetGuid();
            project.AddBuildProperty(frameworkTarget, "OTHER_LDFLAGS", "-lcompression");

            project.WriteToFile(pbxPath);
        }
    }
}
#endif

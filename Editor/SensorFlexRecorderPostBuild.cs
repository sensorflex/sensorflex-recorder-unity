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

            // Compression.framework provides compression_encode_buffer (COMPRESSION_LZ4_RAW)
            // used by SFDepthLz4_AppendFrame in SFVideoEncoder.mm.
            string mainTarget = project.GetUnityMainTargetGuid();
            project.AddFrameworkToProject(mainTarget, "Compression.framework", false);

            project.WriteToFile(pbxPath);
        }
    }
}
#endif

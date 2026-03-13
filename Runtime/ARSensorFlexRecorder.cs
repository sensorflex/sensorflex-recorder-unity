using Unity.XR.CoreUtils;
using UnityEngine;

namespace SensorFlex.Recorder
{
    /// <summary>
    /// Primary public integration component for SensorFlex recording sessions.
    /// Attach this to an <see cref="XROrigin"/> to host future capture workflows.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(XROrigin))]
    [AddComponentMenu("XR/SensorFlex/AR SensorFlex Recorder")]
    public sealed class ARSensorFlexRecorder : MonoBehaviour
    {
        [Header("Capture")]
        [Min(1)]
        [SerializeField] int m_TargetFPS = 30;
        [Tooltip("Optional session identifier override. Leave empty to auto-generate one.")]
        [SerializeField] string m_SessionId = "";
        [Tooltip("Capture RGB frames.")]
        [SerializeField] bool m_CaptureColor = true;
        [Tooltip("Capture environment depth when supported.")]
        [SerializeField] bool m_CaptureDepth = true;
        [Tooltip("Capture tracked camera pose.")]
        [SerializeField] bool m_CapturePose = true;
        [Tooltip("Capture per-frame camera intrinsics.")]
        [SerializeField] bool m_CaptureIntrinsics = true;
        [Tooltip("Capture a scanned scene mesh when available.")]
        [SerializeField] bool m_CaptureSceneMesh = true;

        [Header("Output")]
        [Tooltip("Relative or absolute output directory for recorded data.")]
        [SerializeField] string m_OutputDirectory = "SensorFlexRecordings";

        [Header("Controls")]
        [SerializeField] bool m_RecordOnStart;

        public int TargetFPS => Mathf.Max(1, m_TargetFPS);
        public string SessionId => m_SessionId;
        public bool CaptureColor => m_CaptureColor;
        public bool CaptureDepth => m_CaptureDepth;
        public bool CapturePose => m_CapturePose;
        public bool CaptureIntrinsics => m_CaptureIntrinsics;
        public bool CaptureSceneMesh => m_CaptureSceneMesh;
        public string OutputDirectory => m_OutputDirectory;
        public bool IsRecording { get; private set; }

        void Start()
        {
            if (m_RecordOnStart)
                StartRecording();
        }

        public void StartRecording()
        {
            if (IsRecording)
                return;

            IsRecording = true;
            Debug.Log($"[SF-Recorder] Recording started. Output='{m_OutputDirectory}' FPS={TargetFPS}");
        }

        public void StopRecording()
        {
            if (!IsRecording)
                return;

            IsRecording = false;
            Debug.Log("[SF-Recorder] Recording stopped.");
        }
    }
}

using Unity.XR.CoreUtils;
using UnityEngine;
using System.IO;
using UnityEngine.XR.ARFoundation;

namespace SensorFlex.Recorder
{
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

        private CaptureCoordinator _coordinator;
        private bool _recordOnStartPending;
        private float _recordOnStartDeadline;

        void Start()
        {
            if (m_RecordOnStart)
            {
                _recordOnStartPending = true;
                _recordOnStartDeadline = Time.realtimeSinceStartup + 5f;
            }
        }

        void Update()
        {
            if (_recordOnStartPending && !IsRecording)
            {
                var origin = GetComponent<XROrigin>();
                var camera = origin != null ? origin.Camera : Camera.main;
                var cameraManager = camera != null ? camera.GetComponent<ARCameraManager>() : null;

                if (cameraManager != null && cameraManager.subsystem != null && cameraManager.subsystem.running)
                {
                    _recordOnStartPending = false;
                    StartRecording();
                }
                else if (Time.realtimeSinceStartup >= _recordOnStartDeadline)
                {
                    _recordOnStartPending = false;
                    Debug.LogWarning("[SF-Recorder] RecordOnStart timed out waiting for an active camera subsystem.");
                }
            }
        }

        void OnDestroy()
        {
            if (IsRecording)
            {
                StopRecording();
            }
        }

        public void StartRecording()
        {
            if (IsRecording)
                return;

            string rootPath = Path.IsPathRooted(m_OutputDirectory) 
                ? m_OutputDirectory 
                : Path.Combine(Application.persistentDataPath, m_OutputDirectory);

            if (!Directory.Exists(rootPath))
                Directory.CreateDirectory(rootPath);

            _coordinator = new CaptureCoordinator(this, rootPath);
            if (!_coordinator.StartCapture())
            {
                _coordinator.Dispose();
                _coordinator = null;
                return;
            }

            IsRecording = true;
            Debug.Log($"[SF-Recorder] Recording started. Output='{_coordinator.SessionFolder}' FPS={TargetFPS}");
        }

        public void StopRecording()
        {
            if (!IsRecording)
                return;

            IsRecording = false;
            
            if (_coordinator != null)
            {
                string sessionFolder = _coordinator.SessionFolder;
                _coordinator.StopCapture();
                _coordinator.Dispose();
                _coordinator = null;

                Debug.Log($"[SF-Recorder] Recording stopped. Finalizing archive...");
                string archivePath = sessionFolder + ".zip";
                ArchiveFinalizer.FinalizeArchive(sessionFolder, archivePath);
            }
        }
    }
}

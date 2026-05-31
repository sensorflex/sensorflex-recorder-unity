using System;
using System.IO;
using System.Threading.Tasks;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace SensorFlex.Recorder
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(XROrigin))]
    [AddComponentMenu("XR/SensorFlex/AR SensorFlex Recorder")]
    public sealed class ARSensorFlexRecorder : MonoBehaviour
    {
        // ── Events ─────────────────────────────────────────────────────────

        // Invoked on the main thread when capture successfully starts.
        // Argument: the temp folder path where binary frame files are being written.
        public event Action<string> RecordingStartedEvent;

        // Invoked on the main thread when finalization completes.
        // Argument: array of output .sfz paths (length 1 for single-file, N for multi-part).
        public event Action<string[]> RecordingFinalizedEvent;

        // Invoked on the main thread on any failure (start or finalization).
        // Argument: human-readable error description.
        public event Action<string> RecordingFailedEvent;

        // ── Inspector ──────────────────────────────────────────────────────

        [Header("Capture")]
        [Min(1)]
        [SerializeField] int m_TargetFPS = 30;

        [Tooltip("Optional session identifier override. Leave empty to auto-generate.")]
        [SerializeField] string m_SessionId = "";

        [Tooltip("Capture RGB frames.")]
        [SerializeField] bool m_CaptureColor = true;

        [Tooltip("Capture environment depth when supported.")]
        [SerializeField] bool m_CaptureDepth = true;

        [Tooltip("Capture tracked camera pose.")]
        [SerializeField] bool m_CapturePose = true;

        [Tooltip("Capture per-frame camera intrinsics.")]
        [SerializeField] bool m_CaptureIntrinsics = true;

        [Header("Output")]
        [Tooltip("Relative (to persistentDataPath) or absolute output directory.")]
        [SerializeField] string m_OutputDirectory = "SensorFlexRecordings";

        [Tooltip("Maximum size in MB per .sfz part file. 0 = single file with no size limit.")]
        [Min(0)]
        [SerializeField] int m_MaxPartSizeMb = 500;

        [Header("Controls")]
        [SerializeField] bool m_RecordOnStart;

        // ── Public read-only state ─────────────────────────────────────────

        public bool     IsRecording        { get; private set; }
        public bool     IsFinalizing       { get; private set; }
        public string   ActiveSessionId    { get; private set; }
        public string[] LastArchivePaths   { get; private set; }
        public string   LastError          { get; private set; }

        // ── Exposed config (read by CaptureCoordinator) ────────────────────

        internal int    TargetFPS         => Mathf.Max(1, m_TargetFPS);
        internal string SessionId         => string.IsNullOrEmpty(m_SessionId)
                                                ? (ActiveSessionId ?? Guid.NewGuid().ToString("N"))
                                                : m_SessionId;
        internal bool   CaptureColor      => m_CaptureColor;
        internal bool   CaptureDepth      => m_CaptureDepth;
        internal bool   CapturePose       => m_CapturePose;
        internal bool   CaptureIntrinsics => m_CaptureIntrinsics;

        // ── Private ────────────────────────────────────────────────────────

        CaptureCoordinator      _coordinator;
        Task<string[]>          _finalizationTask;
        bool                    _recordOnStartPending;
        float                   _recordOnStartDeadline;

        // ── Unity lifecycle ────────────────────────────────────────────────

        void Start()
        {
            if (m_RecordOnStart)
            {
                _recordOnStartPending  = true;
                _recordOnStartDeadline = Time.realtimeSinceStartup + 5f;
            }
        }

        void Update()
        {
            // RecordOnStart: wait until the camera subsystem is running.
            if (_recordOnStartPending && !IsRecording)
            {
                var origin        = GetComponent<XROrigin>();
                var camera        = origin != null ? origin.Camera : Camera.main;
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

            // Poll async finalization result on the main thread.
            if (_finalizationTask != null && _finalizationTask.IsCompleted)
            {
                var task          = _finalizationTask;
                _finalizationTask = null;
                IsFinalizing      = false;

                if (task.IsFaulted)
                {
                    LastError = task.Exception?.GetBaseException().Message ?? "Unknown finalization error";
                    Debug.LogError($"[SF-Recorder] Finalization failed: {LastError}");
                    RecordingFailedEvent?.Invoke(LastError);
                }
                else
                {
                    LastArchivePaths = task.Result;
                    Debug.Log($"[SF-Recorder] Finalization complete. Parts={LastArchivePaths.Length} First='{LastArchivePaths[0]}'");
                    RecordingFinalizedEvent?.Invoke(LastArchivePaths);
                }
            }
        }

        void OnDestroy()
        {
            if (IsRecording)
                StopRecording();
        }

        // ── Public API ─────────────────────────────────────────────────────

        public void StartRecording()
        {
            if (IsRecording || IsFinalizing)
                return;

            LastError        = null;
            LastArchivePaths = null;

            // Resolve the session id once so it is stable for the whole session.
            ActiveSessionId = string.IsNullOrEmpty(m_SessionId)
                ? Guid.NewGuid().ToString("N")
                : m_SessionId;

            string tempDir = Path.Combine(
                Application.temporaryCachePath, "SF-Recorder", ActiveSessionId);

            _coordinator = new CaptureCoordinator(this, tempDir);

            if (!_coordinator.StartCapture())
            {
                LastError = "Recorder failed to start. See Console for details.";
                _coordinator.Dispose();
                _coordinator = null;
                RecordingFailedEvent?.Invoke(LastError);
                return;
            }

            IsRecording = true;
            Debug.Log($"[SF-Recorder] Recording started. Session='{ActiveSessionId}' TempDir='{tempDir}'");
            RecordingStartedEvent?.Invoke(tempDir);
        }

        public void StopRecording()
        {
            if (!IsRecording)
                return;

            IsRecording = false;

            if (_coordinator == null)
                return;

            _coordinator.StopCapture();

            string tempDir  = _coordinator.TempDir;
            var    meta     = _coordinator.SessionMetadata;
            var    records  = _coordinator.FrameRecords;

            _coordinator.Dispose();
            _coordinator = null;

            if (records.Count == 0)
            {
                Debug.LogWarning("[SF-Recorder] Recording stopped with zero frames. No archive will be created.");
                return;
            }

            string outputDir = Path.IsPathRooted(m_OutputDirectory)
                ? m_OutputDirectory
                : Path.Combine(Application.persistentDataPath, m_OutputDirectory);

            long maxBytes = m_MaxPartSizeMb > 0
                ? (long)m_MaxPartSizeMb * 1024L * 1024L
                : 0L;

            IsFinalizing      = true;
            _finalizationTask = ArchiveFinalizer.FinalizeAsync(tempDir, outputDir, meta, records, maxBytes);
            Debug.Log($"[SF-Recorder] Recording stopped ({records.Count} frames). Finalizing archive...");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SensorFlex.Recorder
{
    // Subscribes to ARFoundation callbacks, builds per-frame SfzFrameRecords in memory,
    // and routes binary data to the native encoders.
    //
    // SFZ v2.0 pipeline:
    //   iOS  — RGB:   SFRgbEncoder (HEVC YCbCr420) → rgb.mp4
    //           Depth: SFDepthLz4 (LZ4_RAW float32, GCD background) → depth.bin + depth_sizes.bin
    //   Other — RGB/Depth stubs (unsupported, warnings emitted)
    //
    // FrameRecords are kept in memory; ArchiveFinalizer consumes them to build frames.jsonl.
    internal sealed class CaptureCoordinator : IDisposable
    {
        // ── Public state ───────────────────────────────────────────────────

        public string                TempDir         { get; }
        public SfzSessionMetadata    SessionMetadata { get; private set; }
        public List<SfzFrameRecord>  FrameRecords    { get; } = new List<SfzFrameRecord>(4096);

        // Set to true when the max recording duration is reached.
        public bool LimitReached { get; private set; }

        // ── Derived paths ──────────────────────────────────────────────────

        public string RgbMp4Path     => Path.Combine(TempDir, "rgb.mp4");
        public string DepthBinPath   => Path.Combine(TempDir, "depth.bin");
        public string DepthSizesPath => Path.Combine(TempDir, "depth_sizes.bin");

        // ── Private fields ─────────────────────────────────────────────────

        readonly ARSensorFlexRecorder _config;
        ARCameraManager   _cameraManager;
        AROcclusionManager _occlusionManager;
        Camera            _mainCamera;

        int  _frameIndex;
        bool _isCapturing;
        bool _captureDepthEnabled;

        bool   _hasFirstColorDims;
        int    _firstColorW, _firstColorH;
        bool   _hasFirstDepthDims;
        int    _firstDepthW, _firstDepthH;
        string _depthSensor;

        bool  _hasValidIntrinsics;
        float _cachedFx, _cachedFy, _cachedCx, _cachedCy;

        long _firstTimestampNs;
        long _lastTimestampNs;

        long _maxDurationNs;

        bool _warnedDropped;
        bool _warnedMissingIntrinsics;
        bool _warnedMissingDepth;

#if UNITY_IOS && !UNITY_EDITOR
        bool _nativeRgbStarted;
        bool _nativeDepthLz4Started;
        bool _warnedRgbNotReady;
        bool _warnedDepthNotReady;
#endif

        const EnvironmentDepthMode TargetDepthMode = EnvironmentDepthMode.Medium;

        // ── Constructor ────────────────────────────────────────────────────

        public CaptureCoordinator(ARSensorFlexRecorder config, string tempDir)
        {
            _config = config;
            TempDir = tempDir;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────

        public bool StartCapture()
        {
            if (_isCapturing) return true;

            var origin = _config.GetComponent<XROrigin>();
            _mainCamera = origin != null ? origin.Camera : Camera.main;

            if (_mainCamera == null)
            {
                Debug.LogError("[SF-Recorder] Cannot find AR camera. Attach ARSensorFlexRecorder to the XROrigin that owns the AR camera.");
                return false;
            }

            _cameraManager    = _mainCamera.GetComponent<ARCameraManager>();
            _occlusionManager = _mainCamera.GetComponent<AROcclusionManager>();

            if (_cameraManager == null)
            {
                Debug.LogError("[SF-Recorder] ARCameraManager not found on the AR camera.");
                return false;
            }

            if (_cameraManager.subsystem == null)
            {
                Debug.LogError("[SF-Recorder] XRCameraSubsystem is not active.");
                return false;
            }

            _captureDepthEnabled = _config.CaptureDepth;
            if (_captureDepthEnabled)
            {
                if (_occlusionManager == null || _occlusionManager.subsystem == null)
                {
                    Debug.LogWarning("[SF-Recorder] Depth capture requested but XROcclusionSubsystem is unavailable. Depth will be skipped.");
                    _captureDepthEnabled = false;
                }
                else
                {
                    ConfigureDepthMode();
                }
            }

            _frameIndex              = 0;
            _hasFirstColorDims       = false;
            _hasFirstDepthDims       = false;
            _hasValidIntrinsics      = false;
            _warnedDropped           = false;
            _warnedMissingIntrinsics = false;
            _warnedMissingDepth      = false;
            _firstTimestampNs        = 0;
            _lastTimestampNs         = 0;
            LimitReached             = false;
            _maxDurationNs           = _config.MaxRecordingSeconds > 0
                                         ? (long)(_config.MaxRecordingSeconds * 1_000_000_000.0)
                                         : 0L;
            FrameRecords.Clear();

#if UNITY_IOS && !UNITY_EDITOR
            _nativeRgbStarted     = false;
            _nativeDepthLz4Started = false;
            _warnedRgbNotReady    = false;
            _warnedDepthNotReady  = false;
#endif

            Directory.CreateDirectory(TempDir);
            _cameraManager.frameReceived += OnCameraFrameReceived;
            _isCapturing = true;

            Debug.Log($"[SF-Recorder] Capture started. TempDir='{TempDir}' Depth={_captureDepthEnabled} MaxSeconds={_config.MaxRecordingSeconds}");
            return true;
        }

        public void StopCapture()
        {
            if (!_isCapturing) return;
            _isCapturing = false;

            if (_cameraManager != null)
                _cameraManager.frameReceived -= OnCameraFrameReceived;

#if UNITY_IOS && !UNITY_EDITOR
            if (_nativeRgbStarted) NativeVideoEncoder.FinishRgbSession();
            // Always call FinishDepthLz4Session — native side handles the no-op case
            // (gDepthQueue == NULL → immediate callback). Without this, _depthDone is
            // never set when depth was never acquired, causing WaitForBothFinished to hang.
            NativeVideoEncoder.FinishDepthLz4Session();
#endif

            SessionMetadata = new SfzSessionMetadata
            {
                SessionId    = _config.SessionId,
                StartTimeUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                DeviceModel  = SystemInfo.deviceModel,
                DeviceOs     = SystemInfo.operatingSystem,
                ArFramework  = ResolveCaptureFramework(),
                Fps          = ComputeActualFps(),
                FrameCount   = FrameRecords.Count,
                HasRgb       = _hasFirstColorDims,
                RgbWidth     = _firstColorW,
                RgbHeight    = _firstColorH,
                HasDepth     = _hasFirstDepthDims,
                DepthWidth   = _firstDepthW,
                DepthHeight  = _firstDepthH,
                DepthSensor  = _depthSensor,
            };

            Debug.Log($"[SF-Recorder] Capture stopped. Frames={FrameRecords.Count} ActualFPS={SessionMetadata.Fps}");
        }

        public void Dispose() => StopCapture();

        // ── Frame callback ─────────────────────────────────────────────────

        void OnCameraFrameReceived(ARCameraFrameEventArgs args)
        {
            if (!_isCapturing) return;

            long timestampNs = args.timestampNs.HasValue
                ? args.timestampNs.Value
                : (long)(Time.realtimeSinceStartup * 1_000_000_000L);

            if (_firstTimestampNs == 0) _firstTimestampNs = timestampNs;
            _lastTimestampNs = timestampNs;

            if (_maxDurationNs > 0 && (timestampNs - _firstTimestampNs) >= _maxDurationNs)
            {
                StopCapture();
                LimitReached = true;
                return;
            }

            bool hasColor = false;
            bool hasDepth = false;

#if UNITY_IOS && !UNITY_EDITOR
            if (_config.CaptureColor)
                hasColor = AcquireAndEncodeColorNative(timestampNs - _firstTimestampNs);

            if (_captureDepthEnabled)
                hasDepth = AcquireAndEncodeDepthLz4();
#else
            if (_config.CaptureColor && !_hasFirstColorDims)
                Debug.LogWarning("[SF-Recorder] RGB recording is only supported on iOS. No frames will be captured on this platform.");

            if (_captureDepthEnabled && !_hasFirstDepthDims)
                Debug.LogWarning("[SF-Recorder] Depth recording is only supported on iOS. No depth will be captured on this platform.");
#endif

            // ── Pose ──────────────────────────────────────────────────────
            Vector3    position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            if (_config.CapturePose && _mainCamera != null)
            {
                position = _mainCamera.transform.position;
                rotation = _mainCamera.transform.rotation;
            }

            // ── Intrinsics ────────────────────────────────────────────────
            bool  hasIntrinsics = false;
            float fx = 0, fy = 0, cx = 0, cy = 0;
            if (_config.CaptureIntrinsics)
            {
                if (_hasValidIntrinsics)
                {
                    fx = _cachedFx; fy = _cachedFy; cx = _cachedCx; cy = _cachedCy;
                    hasIntrinsics = true;
                }
                else
                {
                    int colorW = _firstColorW, colorH = _firstColorH;
                    if (TryGetIntrinsics(colorW, colorH, out float oFx, out float oFy, out float oCx, out float oCy))
                    {
                        fx = oFx; fy = oFy; cx = oCx; cy = oCy;
                        _cachedFx = fx; _cachedFy = fy; _cachedCx = cx; _cachedCy = cy;
                        _hasValidIntrinsics = true;
                        hasIntrinsics       = true;
                    }
                    else if (!_warnedMissingIntrinsics)
                    {
                        Debug.LogWarning("[SF-Recorder] No camera intrinsics available yet.");
                        _warnedMissingIntrinsics = true;
                    }
                }
            }

            FrameRecords.Add(new SfzFrameRecord
            {
                FrameIndex    = _frameIndex,
                TimestampNs   = timestampNs,
                Position      = position,
                Rotation      = rotation,
                HasIntrinsics = hasIntrinsics,
                Fx = fx, Fy = fy, Cx = cx, Cy = cy,
                HasColor = hasColor,
                HasDepth = hasDepth,
            });

            _frameIndex++;
        }

        // ── iOS native capture ─────────────────────────────────────────────

#if UNITY_IOS && !UNITY_EDITOR
        unsafe bool AcquireAndEncodeColorNative(long sessionRelativeNs)
        {
            if (!_cameraManager.TryAcquireLatestCpuImage(out var cpuImage))
                return false;

            try
            {
                if (!_nativeRgbStarted)
                {
                    NativeVideoEncoder.StartRgbSession(RgbMp4Path, cpuImage.width, cpuImage.height);
                    _firstColorW       = cpuImage.width;
                    _firstColorH       = cpuImage.height;
                    _hasFirstColorDims = true;
                    _nativeRgbStarted  = true;
                }

                var planeY    = cpuImage.GetPlane(0);
                var planeCbCr = cpuImage.GetPlane(1);
                void* pY    = NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(planeY.data);
                void* pCbCr = NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(planeCbCr.data);

                bool appended = NativeVideoEncoder.AppendRgbFrame(
                    (IntPtr)pY,    planeY.rowStride,
                    (IntPtr)pCbCr, planeCbCr.rowStride,
                    cpuImage.width, cpuImage.height,
                    sessionRelativeNs);

                if (!appended && !_warnedRgbNotReady)
                {
                    Debug.LogWarning("[SF-Recorder] RGB encoder not ready — frame dropped.");
                    _warnedRgbNotReady = true;
                }

                return appended;
            }
            finally
            {
                cpuImage.Dispose();
            }
        }

        unsafe bool AcquireAndEncodeDepthLz4()
        {
            if (!_occlusionManager.TryAcquireEnvironmentDepthCpuImage(out var depthImage))
            {
                if (!_warnedMissingDepth)
                {
                    Debug.LogWarning(
                        $"[SF-Recorder] Depth unavailable. " +
                        $"requested={_occlusionManager.requestedEnvironmentDepthMode} " +
                        $"current={_occlusionManager.currentEnvironmentDepthMode}");
                    _warnedMissingDepth = true;
                }
                return false;
            }

            _warnedMissingDepth = false;

            try
            {
                if (!_nativeDepthLz4Started)
                {
                    NativeVideoEncoder.StartDepthLz4Writer(
                        DepthBinPath, DepthSizesPath,
                        depthImage.width, depthImage.height);
                    _firstDepthW          = depthImage.width;
                    _firstDepthH          = depthImage.height;
                    _hasFirstDepthDims    = true;
                    _nativeDepthLz4Started = true;

                    _depthSensor = "arkit_lidar";
                }

                var plane  = depthImage.GetPlane(0);
                void* pF32 = NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(plane.data);

                bool appended = NativeVideoEncoder.AppendDepthLz4Frame(
                    (IntPtr)pF32, plane.rowStride,
                    depthImage.width, depthImage.height);

                if (!appended && !_warnedDepthNotReady)
                {
                    Debug.LogWarning("[SF-Recorder] Depth LZ4 encoder not ready — frame dropped.");
                    _warnedDepthNotReady = true;
                }

                return appended;
            }
            finally
            {
                depthImage.Dispose();
            }
        }
#endif

        // ── Intrinsics ─────────────────────────────────────────────────────

        bool TryGetIntrinsics(int colorW, int colorH,
            out float fx, out float fy, out float cx, out float cy)
        {
            fx = fy = cx = cy = 0f;

            if (_cameraManager != null && _cameraManager.subsystem != null &&
                _cameraManager.subsystem.TryGetIntrinsics(out var native))
            {
                if (IsValidIntrinsics(native.focalLength.x, native.focalLength.y,
                                      native.principalPoint.x, native.principalPoint.y,
                                      native.resolution.x, native.resolution.y))
                {
                    fx = native.focalLength.x;
                    fy = native.focalLength.y;
                    cx = native.principalPoint.x;
                    cy = native.principalPoint.y;
                    return true;
                }
            }

            int w = colorW > 0 ? colorW : (_mainCamera != null ? _mainCamera.pixelWidth  : Screen.width);
            int h = colorH > 0 ? colorH : (_mainCamera != null ? _mainCamera.pixelHeight : Screen.height);

            if (_mainCamera != null && w > 0 && h > 0 && _mainCamera.fieldOfView > 0f)
            {
                float vFovRad     = _mainCamera.fieldOfView * Mathf.Deg2Rad;
                float estimatedFy = 0.5f * h / Mathf.Tan(vFovRad * 0.5f);
                fx = estimatedFy * ((float)w / h); fy = estimatedFy;
                cx = w * 0.5f; cy = h * 0.5f;
                return IsValidIntrinsics(fx, fy, cx, cy, w, h);
            }

            return false;
        }

        static bool IsValidIntrinsics(float fx, float fy, float cx, float cy, int w, int h)
            => fx > 0 && fy > 0 && cx >= 0 && cx <= w && cy >= 0 && cy <= h;

        // ── Depth mode ─────────────────────────────────────────────────────

        void ConfigureDepthMode()
        {
            if (_occlusionManager == null) return;

            if (_occlusionManager.requestedEnvironmentDepthMode == EnvironmentDepthMode.Disabled ||
                _occlusionManager.requestedEnvironmentDepthMode == EnvironmentDepthMode.Fastest)
            {
                Debug.LogWarning($"[SF-Recorder] Promoting depth mode to {TargetDepthMode}.");
                _occlusionManager.requestedEnvironmentDepthMode = TargetDepthMode;
            }

            _occlusionManager.environmentDepthTemporalSmoothingRequested = true;

            _depthSensor = Application.platform switch
            {
                RuntimePlatform.Android      => "arcore_environment_depth",
                RuntimePlatform.IPhonePlayer => "arkit_lidar",
                _                            => "arfoundation_environment_depth"
            };

            Debug.Log($"[SF-Recorder] Depth configured: mode={_occlusionManager.requestedEnvironmentDepthMode} sensor={_depthSensor}");
        }

        // ── Helpers ────────────────────────────────────────────────────────

        int ComputeActualFps()
        {
            int count = FrameRecords.Count;
            if (count < 2) return 0;
            double durationSec = (_lastTimestampNs - _firstTimestampNs) / 1_000_000_000.0;
            return durationSec > 0 ? Mathf.RoundToInt((float)((count - 1) / durationSec)) : 0;
        }

        static string ResolveCaptureFramework() => Application.platform switch
        {
            RuntimePlatform.IPhonePlayer  => "ARKit",
            RuntimePlatform.Android       => "ARCore",
            RuntimePlatform.OSXEditor
            or RuntimePlatform.WindowsEditor
            or RuntimePlatform.LinuxEditor => "ARFoundation Simulation",
            _                              => "ARFoundation"
        };
    }
}

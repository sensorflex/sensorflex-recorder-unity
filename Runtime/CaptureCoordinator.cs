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
        public string                SessionId       { get; }
        public SfzSessionMetadata    SessionMetadata { get; private set; }
        public List<SfzFrameRecord>  FrameRecords    { get; } = new List<SfzFrameRecord>(4096);

        // Set to true when the max recording duration is reached.
        public bool LimitReached { get; private set; }

        // true after BeginStandby() has detected image dimensions from ARKit.
        public bool HasDims { get; private set; }

        // true after StartEngines() has been called.
        public bool EnginesStarted { get; private set; }

        // true when the async native encoder setup is complete and Begin() can be called with zero drops.
        public bool IsEncoderReady
        {
            get
            {
                if (!EnginesStarted) return false;
#if UNITY_IOS && !UNITY_EDITOR
                bool isVideoDepth = _captureDepthEnabled &&
                    _depthCodecInt != (int)SfzDepthCodec.LZ4 &&
                    _depthCodecInt != (int)SfzDepthCodec.Zstd;
                return NativeVideoEncoder.IsRgbReady &&
                       (!isVideoDepth || NativeVideoEncoder.IsDepthVideoReady);
#else
                return true;
#endif
            }
        }

        // ── Derived paths ──────────────────────────────────────────────────

        public string RgbMp4Path     => Path.Combine(TempDir, "rgb.mp4");
        public string DepthBinPath   => Path.Combine(TempDir, "depth.bin");
        public string DepthSizesPath => Path.Combine(TempDir, "depth_sizes.bin");
        public string DepthMp4Path   => Path.Combine(TempDir, "depth.mp4");

        // ── Private fields ─────────────────────────────────────────────────

        readonly ARSensorFlexRecorder _config;
        ARCameraManager   _cameraManager;
        AROcclusionManager _occlusionManager;
        Camera            _mainCamera;

        int   _frameIndex;
        bool  _isCapturing;
        bool  _captureDepthEnabled;
        int   _depthCodecInt;  // cast of SfzDepthCodec, avoids cross-file type reference at field site

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

        bool _standbySubscribed;   // prevents double-subscription in BeginStandby()
        bool _warnedDropped;
        bool _warnedMissingIntrinsics;
        bool _warnedMissingDepth;

#if UNITY_IOS && !UNITY_EDITOR
        bool _nativeRgbStarted;
        bool _nativeDepthLz4Started;
        bool _nativeDepthVideoStarted;
        bool _warnedRgbNotReady;
        bool _warnedDepthNotReady;
#endif

        const EnvironmentDepthMode TargetDepthMode = EnvironmentDepthMode.Medium;

        // ── Constructor ────────────────────────────────────────────────────

        public CaptureCoordinator(ARSensorFlexRecorder config, string tempDir, string sessionId)
        {
            _config   = config;
            TempDir   = tempDir;
            SessionId = sessionId;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────

        // Phase 1 of pre-warm: set up managers + subscribe to one ARKit frame to
        // detect image dimensions. Returns false if the camera subsystem is not
        // yet running (caller should retry next Update frame).
        public bool BeginStandby()
        {
            if (HasDims || EnginesStarted || _isCapturing) return true;

            var origin = _config.GetComponent<XROrigin>();
            _mainCamera = origin != null ? origin.Camera : Camera.main;
            if (_mainCamera == null) return false;

            _cameraManager    = _mainCamera.GetComponent<ARCameraManager>();
            _occlusionManager = _mainCamera.GetComponent<AROcclusionManager>();

            if (_cameraManager == null ||
                _cameraManager.subsystem == null ||
                !_cameraManager.subsystem.running)
                return false;

            _depthCodecInt       = (int)_config.DepthCodec;
            _captureDepthEnabled = _config.CaptureDepth;
            if (_captureDepthEnabled &&
                (_occlusionManager == null || _occlusionManager.subsystem == null))
                _captureDepthEnabled = false;

            Directory.CreateDirectory(TempDir);
            if (!_standbySubscribed)
            {
                _standbySubscribed = true;
                _cameraManager.frameReceived += OnStandbyFrameReceived;
            }
            return true;
        }

        // Phase 2: start native encoder sessions with the pre-detected dimensions.
        // Must only be called after the previous session's WaitForBothFinished has
        // completed (static NativeVideoEncoder state must be free).
        public void StartEngines()
        {
            if (EnginesStarted || !HasDims) return;
            EnginesStarted = true;

#if UNITY_IOS && !UNITY_EDITOR
            if (_config.CaptureColor && _firstColorW > 0)
            {
                NativeVideoEncoder.StartRgbSession(
                    RgbMp4Path, _firstColorW, _firstColorH, _config.RgbCodec);
                _nativeRgbStarted = true;
            }

            bool isVideoDepth = _captureDepthEnabled &&
                _depthCodecInt != (int)SfzDepthCodec.LZ4 &&
                _depthCodecInt != (int)SfzDepthCodec.Zstd;
            if (isVideoDepth && _firstDepthW > 0)
            {
                NativeVideoEncoder.StartDepthVideoSession(
                    DepthMp4Path, _firstDepthW, _firstDepthH,
                    _config.DepthCodec, _config.DepthMaxMeters);
                _nativeDepthVideoStarted = true;
            }
#else
            // Non-iOS: nothing to pre-start; IsEncoderReady returns true immediately.
#endif
        }

        // Phase 3: called when the user presses Start. Resets per-session counters
        // and enables frame recording. Encoders must be started (via StartCapture or
        // StartEngines) before this.
        public void Begin()
        {
            if (_isCapturing) return;

            _frameIndex              = 0;
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
            _warnedRgbNotReady   = false;
            _warnedDepthNotReady = false;
#endif

            if (_captureDepthEnabled && _occlusionManager != null)
                ConfigureDepthMode();

            _cameraManager.frameReceived += OnCameraFrameReceived;
            _isCapturing = true;

            Debug.Log($"[SF-Recorder] Recording begun (pre-warmed={EnginesStarted}). TempDir='{TempDir}'");
        }

        // Standby dim-detection callback. Retries next frame if depth is not yet available.
        void OnStandbyFrameReceived(ARCameraFrameEventArgs _)
        {
            _cameraManager.frameReceived -= OnStandbyFrameReceived;

            bool colorDone = !_config.CaptureColor;
            bool depthDone = !_captureDepthEnabled;

#if UNITY_IOS && !UNITY_EDITOR
            if (_config.CaptureColor &&
                _cameraManager.TryAcquireLatestCpuImage(out var colorImg))
            {
                _firstColorW       = colorImg.width;
                _firstColorH       = colorImg.height;
                _hasFirstColorDims = true;
                colorImg.Dispose();
                colorDone = true;
            }

            bool isVideoDepth = _captureDepthEnabled &&
                _depthCodecInt != (int)SfzDepthCodec.LZ4 &&
                _depthCodecInt != (int)SfzDepthCodec.Zstd;

            if (_captureDepthEnabled)
            {
                if (!isVideoDepth)
                {
                    depthDone = true; // LZ4/Zstd: dims detected on first recording frame; no pre-warm needed
                }
                else if (_occlusionManager != null &&
                         _occlusionManager.TryAcquireEnvironmentDepthCpuImage(out var depthImg))
                {
                    _firstDepthW       = depthImg.width;
                    _firstDepthH       = depthImg.height;
                    _hasFirstDepthDims = true;
                    _depthSensor       = "arkit_lidar";
                    depthImg.Dispose();
                    depthDone = true;
                }
            }
#else
            colorDone = true;
            depthDone = true;
#endif

            if (colorDone && depthDone)
            {
                HasDims = true;
            }
            else
            {
                // ARKit depth not ready yet on this frame — retry next frame.
                _cameraManager.frameReceived += OnStandbyFrameReceived;
            }
        }

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

            _depthCodecInt       = (int)_config.DepthCodec;
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
            _nativeRgbStarted       = false;
            _nativeDepthLz4Started  = false;
            _nativeDepthVideoStarted = false;
            _warnedRgbNotReady      = false;
            _warnedDepthNotReady    = false;
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
            // FinishDepthSession dispatches to LZ4, video, or no-op based on what was started.
            NativeVideoEncoder.FinishDepthSession();
#endif

            SessionMetadata = new SfzSessionMetadata
            {
                SessionId      = SessionId,
                StartTimeUtc   = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                DeviceModel    = SystemInfo.deviceModel,
                DeviceOs       = SystemInfo.operatingSystem,
                ArFramework    = ResolveCaptureFramework(),
                Fps            = ComputeActualFps(),
                FrameCount     = FrameRecords.Count,
                HasRgb         = _hasFirstColorDims,
                RgbWidth       = _firstColorW,
                RgbHeight      = _firstColorH,
                HasDepth       = _hasFirstDepthDims,
                DepthWidth     = _firstDepthW,
                DepthHeight    = _firstDepthH,
                DepthSensor    = _depthSensor,
                RgbCodec       = _config.RgbCodec,
                DepthCodec     = _config.DepthCodec,
                DepthMaxMeters = _config.DepthMaxMeters,
            };

            Debug.Log($"[SF-Recorder] Capture stopped. Frames={FrameRecords.Count} ActualFPS={SessionMetadata.Fps}");
        }

        public void Dispose()
        {
            // Unsubscribe standby callback if we're disposed mid-scan.
            if (_standbySubscribed && _cameraManager != null)
            {
                _cameraManager.frameReceived -= OnStandbyFrameReceived;
                _standbySubscribed = false;
            }
            StopCapture();
        }

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
                hasDepth = (_depthCodecInt == 0)  // SfzDepthCodec.LZ4 == 0
                    ? AcquireAndEncodeDepthLz4()
                    : AcquireAndEncodeDepthVideo(timestampNs - _firstTimestampNs);
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
                    NativeVideoEncoder.StartRgbSession(RgbMp4Path, cpuImage.width, cpuImage.height, _config.RgbCodec);
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

        unsafe bool AcquireAndEncodeDepthVideo(long sessionRelativeNs)
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
                if (!_nativeDepthVideoStarted)
                {
                    NativeVideoEncoder.StartDepthVideoSession(
                        DepthMp4Path, depthImage.width, depthImage.height,
                        _config.DepthCodec, _config.DepthMaxMeters);
                    _firstDepthW             = depthImage.width;
                    _firstDepthH             = depthImage.height;
                    _hasFirstDepthDims       = true;
                    _nativeDepthVideoStarted = true;
                    _depthSensor             = "arkit_lidar";
                }

                var plane  = depthImage.GetPlane(0);
                void* pF32 = NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(plane.data);

                bool appended = NativeVideoEncoder.AppendDepthVideoFrame(
                    (IntPtr)pF32, plane.rowStride,
                    depthImage.width, depthImage.height,
                    sessionRelativeNs);

                if (!appended && !_warnedDepthNotReady)
                {
                    Debug.LogWarning("[SF-Recorder] Depth video encoder not ready — frame dropped.");
                    _warnedDepthNotReady = true;
                }

                return appended;
            }
            finally
            {
                depthImage.Dispose();
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
                        depthImage.width, depthImage.height,
                        (SfzDepthCodec)_depthCodecInt);
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

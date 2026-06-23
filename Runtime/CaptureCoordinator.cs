using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SensorFlex.Recorder
{
    // Subscribes to ARFoundation callbacks, builds per-frame SfzFrameRecords in memory,
    // and routes binary data to CaptureFolderWriter.
    //
    // Threading: all public methods are called from the main thread.
    // CaptureFolderWriter's encoder/writer threads only see value-type snapshots
    // that are fully copied before being enqueued.
    //
    // On iOS the color and depth paths are replaced by hardware HEVC encoding via
    // NativeVideoEncoder (P/Invoke into SFVideoEncoder.mm).  CaptureFolderWriter
    // runs as a no-op on those platforms; only pose/intrinsics metadata is routed
    // through it as before.
    internal sealed class CaptureCoordinator : IDisposable
    {
        // ── Public state ───────────────────────────────────────────────────

        public string                TempDir         { get; }
        public CaptureFolderWriter   Writer          { get; }
        public SfzSessionMetadata    SessionMetadata { get; private set; }
        public List<SfzFrameRecord>  FrameRecords    { get; } = new List<SfzFrameRecord>(4096);

        // Set to true when the max recording duration is reached.
        // ARSensorFlexRecorder.Update() watches this and triggers finalization.
        public bool LimitReached { get; private set; }

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

        // Intrinsics — cached on first valid read; stable for a session
        bool  _hasValidIntrinsics;
        float _cachedFx, _cachedFy, _cachedCx, _cachedCy;

        // Actual FPS tracking
        long _firstTimestampNs;
        long _lastTimestampNs;

        // Max duration (nanoseconds); 0 = unlimited
        long _maxDurationNs;

        // One-shot warning flags
        bool _warnedDropped;
        bool _warnedMissingIntrinsics;
        bool _warnedMissingDepth;

#if UNITY_IOS && !UNITY_EDITOR
        // Native HEVC encoder state
        bool _nativeRgbStarted;
        bool _nativeDepthStarted;
        bool _warnedRgbNotReady;
        bool _warnedDepthNotReady;
#endif

        // Whether this session uses the native HEVC path
        readonly bool _useNativeEncoder;

        const EnvironmentDepthMode TargetDepthMode = EnvironmentDepthMode.Medium;

        // ── Constructor ────────────────────────────────────────────────────

        public CaptureCoordinator(ARSensorFlexRecorder config, string tempDir)
        {
            _config = config;
            TempDir = tempDir;

#if UNITY_IOS && !UNITY_EDITOR
            _useNativeEncoder = true;
#else
            _useNativeEncoder = false;
#endif

            Writer = new CaptureFolderWriter(tempDir, _useNativeEncoder);
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
                Debug.LogError("[SF-Recorder] XRCameraSubsystem is not active. Enable the ARCore/ARKit XR plug-in for the target platform.");
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
            _nativeRgbStarted   = false;
            _nativeDepthStarted = false;
            _warnedRgbNotReady  = false;
            _warnedDepthNotReady = false;
#endif

            Writer.Start();
            _cameraManager.frameReceived += OnCameraFrameReceived;
            _isCapturing = true;

            Debug.Log($"[SF-Recorder] Capture started. TempDir='{TempDir}' Depth={_captureDepthEnabled} NativeEncoder={_useNativeEncoder} MaxSeconds={_config.MaxRecordingSeconds}");
            return true;
        }

        // Stops subscribing to camera events and signals the writer to drain.
        // Does NOT wait for the writer — call Writer.WaitForFlush() off the main thread.
        public void StopCapture()
        {
            if (!_isCapturing) return;
            _isCapturing = false;

            if (_cameraManager != null)
                _cameraManager.frameReceived -= OnCameraFrameReceived;

#if UNITY_IOS && !UNITY_EDITOR
            // Signal native encoders to finalize their MP4 files.
            // WaitForBothFinished() is called inside CaptureFolderWriter.WaitForFlush().
            if (_nativeRgbStarted)   NativeVideoEncoder.FinishRgbSession();
            if (_nativeDepthStarted) NativeVideoEncoder.FinishDepthSession();
#endif

            Writer.CompleteAdding();

            SessionMetadata = new SfzSessionMetadata
            {
                SessionId    = _config.SessionId,
                StartTimeUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                DeviceModel  = SystemInfo.deviceModel,
                DeviceOs     = SystemInfo.operatingSystem,
                ArFramework  = ResolveCaptureFramework(),
                Fps          = ComputeActualFps(),
                HasRgb       = _hasFirstColorDims,
                RgbWidth     = _firstColorW,
                RgbHeight    = _firstColorH,
                HasDepth     = _hasFirstDepthDims,
                DepthWidth   = _firstDepthW,
                DepthHeight  = _firstDepthH,
                DepthSensor  = _depthSensor,
                RgbEncoding  = _useNativeEncoder ? "hevc" : "jpeg",
                DepthEncoding = _useNativeEncoder ? "hevc_float16" : "raw_float32_le",
            };

            Debug.Log($"[SF-Recorder] Capture stopped. Frames={FrameRecords.Count} ActualFPS={SessionMetadata.Fps}");
        }

        public void Dispose() => StopCapture();

        // ── Frame callback (main thread) ───────────────────────────────────

        void OnCameraFrameReceived(ARCameraFrameEventArgs args)
        {
            if (!_isCapturing) return;

            long timestampNs = args.timestampNs.HasValue
                ? args.timestampNs.Value
                : (long)(Time.realtimeSinceStartup * 1_000_000_000L);

            if (_firstTimestampNs == 0) _firstTimestampNs = timestampNs;
            _lastTimestampNs = timestampNs;

            // Check max recording duration.
            if (_maxDurationNs > 0 && (timestampNs - _firstTimestampNs) >= _maxDurationNs)
            {
                StopCapture();
                LimitReached = true;
                return;
            }

            bool hasColor = false;
            bool hasDepth = false;

#if UNITY_IOS && !UNITY_EDITOR
            // ── iOS: hardware HEVC encoding, no managed pixel data ─────────

            if (_config.CaptureColor)
                hasColor = AcquireAndEncodeColorNative(timestampNs);

            if (_captureDepthEnabled)
                hasDepth = AcquireAndEncodeDepthNative(timestampNs);

#else
            // ── Non-iOS: existing RGBA → JPEG → writer-thread path ─────────

            byte[] rawRgba = null;
            byte[] jpg     = null;
            uint   rgbaW   = 0, rgbaH = 0;
            int    colorW  = 0, colorH = 0;

            if (_config.CaptureColor)
            {
                (rawRgba, jpg, colorW, colorH) = TryAcquireColorFrame(args);
                hasColor = rawRgba != null || jpg != null;

                if (hasColor)
                {
                    if (rawRgba != null) { rgbaW = (uint)colorW; rgbaH = (uint)colorH; }

                    if (!_hasFirstColorDims)
                    {
                        _firstColorW       = colorW;
                        _firstColorH       = colorH;
                        _hasFirstColorDims = true;
                    }
                }
            }

            byte[] depthF32 = null;
            int    depthW   = 0, depthH = 0;

            if (_captureDepthEnabled)
            {
                (depthF32, depthW, depthH) = TryAcquireDepthFloat32();
                hasDepth = depthF32 != null;

                if (hasDepth && !_hasFirstDepthDims)
                {
                    _firstDepthW       = depthW;
                    _firstDepthH       = depthH;
                    _hasFirstDepthDims = true;
                }
            }
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
#if UNITY_IOS && !UNITY_EDITOR
                    int colorW = _firstColorW, colorH = _firstColorH;
#endif
                    if (TryGetIntrinsics(colorW, colorH, out float oFx, out float oFy, out float oCx, out float oCy))
                    {
                        fx = oFx; fy = oFy; cx = oCx; cy = oCy;
                        _cachedFx = fx; _cachedFy = fy; _cachedCx = cx; _cachedCy = cy;
                        _hasValidIntrinsics = true;
                        hasIntrinsics       = true;
                    }
                    else if (!_warnedMissingIntrinsics)
                    {
                        Debug.LogWarning("[SF-Recorder] No camera intrinsics available yet; frame will have no intrinsics.");
                        _warnedMissingIntrinsics = true;
                    }
                }
            }

            // ── Record ────────────────────────────────────────────────────
            FrameRecords.Add(new SfzFrameRecord
            {
                FrameIndex    = _frameIndex,
                TimestampNs   = timestampNs,
                Position      = position,
                Rotation      = rotation,
                HasIntrinsics = hasIntrinsics,
                Fx = fx, Fy = fy, Cx = cx, Cy = cy,
                HasColor = hasColor,
                HasDepth = hasDepth
            });

#if !(UNITY_IOS && !UNITY_EDITOR)
            // Non-iOS: enqueue binary payload for the writer thread.
            var job = new CaptureFolderWriter.RawFrameJob
            {
                FrameIndex = _frameIndex,
                RgbaData   = rawRgba,
                RgbaWidth  = rgbaW,
                RgbaHeight = rgbaH,
                JpgData    = jpg,
                DepthData  = depthF32
            };

            if (!Writer.TryEnqueue(job) && !_warnedDropped)
            {
                Debug.LogWarning($"[SF-Recorder] Frame {_frameIndex} dropped — raw queue is full.");
                _warnedDropped = true;
            }
#endif

            _frameIndex++;
        }

        // ── iOS native capture helpers ─────────────────────────────────────

#if UNITY_IOS && !UNITY_EDITOR
        unsafe bool AcquireAndEncodeColorNative(long timestampNs)
        {
            if (!_cameraManager.TryAcquireLatestCpuImage(out var cpuImage))
                return false;

            try
            {
                if (!_nativeRgbStarted)
                {
                    NativeVideoEncoder.StartRgbSession(Writer.RgbMp4Path, cpuImage.width, cpuImage.height);
                    _firstColorW = cpuImage.width; _firstColorH = cpuImage.height;
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
                    timestampNs - _firstTimestampNs);

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

        unsafe bool AcquireAndEncodeDepthNative(long timestampNs)
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
                if (!_nativeDepthStarted)
                {
                    NativeVideoEncoder.StartDepthSession(Writer.DepthMp4Path, depthImage.width, depthImage.height);
                    _firstDepthW = depthImage.width; _firstDepthH = depthImage.height;
                    _hasFirstDepthDims  = true;
                    _nativeDepthStarted = true;
                }

                var plane  = depthImage.GetPlane(0);
                void* pF32 = NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(plane.data);

                bool appended = NativeVideoEncoder.AppendDepthFrame(
                    (IntPtr)pF32, plane.rowStride,
                    depthImage.width, depthImage.height,
                    timestampNs - _firstTimestampNs);

                if (!appended && !_warnedDepthNotReady)
                {
                    Debug.LogWarning("[SF-Recorder] Depth encoder not ready — frame dropped.");
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

        // ── Non-iOS color acquisition ──────────────────────────────────────

        (byte[] rawRgba, byte[] jpg, int w, int h) TryAcquireColorFrame(ARCameraFrameEventArgs args)
        {
            if (_cameraManager.TryAcquireLatestCpuImage(out var cpuImage))
            {
                var (rgba, w, h) = ConvertCpuImageToRgba(cpuImage);
                return (rgba, null, w, h);
            }

            if (args.textures != null && args.textures.Count > 0 && args.textures[0] != null)
            {
                var tex = args.textures[0];
                var (encoded, w, h) = EncodeTextureToJpg(tex, null, tex.width, tex.height);
                return (null, encoded, w, h);
            }

            Debug.LogWarning("[SF-Recorder] No camera image available this frame.");
            return (null, null, 0, 0);
        }

        static (byte[] rgba, int w, int h) ConvertCpuImageToRgba(XRCpuImage image)
        {
            var convParams = new XRCpuImage.ConversionParams
            {
                inputRect        = new RectInt(0, 0, image.width, image.height),
                outputDimensions = new Vector2Int(image.width, image.height),
                outputFormat     = TextureFormat.RGBA32,
                transformation   = XRCpuImage.Transformation.MirrorX
            };

            int size   = image.GetConvertedDataSize(convParams.outputDimensions, convParams.outputFormat);
            var buffer = new NativeArray<byte>(size, Allocator.Temp);

            try
            {
                image.Convert(convParams, buffer);
                return (buffer.ToArray(), image.width, image.height);
            }
            finally
            {
                buffer.Dispose();
                image.Dispose();
            }
        }

        static (byte[] jpg, int w, int h) EncodeTextureToJpg(Texture src, Material mat, int w, int h)
        {
            w = Mathf.Max(1, w);
            h = Mathf.Max(1, h);

            var rt   = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            var tex  = new Texture2D(w, h, TextureFormat.RGBA32, false);

            try
            {
                if (mat != null) Graphics.Blit(src, rt, mat); else Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                return (tex.EncodeToJPG(80), w, h);
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(tex);
            }
        }

        // ── Non-iOS depth acquisition ──────────────────────────────────────

        (byte[] depthF32, int w, int h) TryAcquireDepthFloat32()
        {
            if (_occlusionManager == null) return (null, 0, 0);

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
                return (null, 0, 0);
            }

            _warnedMissingDepth = false;

            try
            {
                var plane       = depthImage.GetPlane(0);
                int pixelCount  = depthImage.width * depthImage.height;
                int pixelStride = plane.pixelStride;

                byte[] result;

                if (pixelStride == 4)
                {
                    result = new byte[pixelCount * 4];
                    NativeArray<byte>.Copy(plane.data, result, result.Length);
                }
                else
                {
                    // uint16 mm (ARCore) → float32 metres
                    var floats = new float[pixelCount];
                    var src    = plane.data;
                    for (int i = 0; i < pixelCount; i++)
                    {
                        ushort mm = (ushort)(src[i * 2] | (src[i * 2 + 1] << 8));
                        floats[i] = mm / 1000.0f;
                    }
                    result = new byte[pixelCount * 4];
                    Buffer.BlockCopy(floats, 0, result, 0, result.Length);
                }

                // Flip rows vertically — ARKit depth arrives top-to-bottom reversed.
                int rowBytes = depthImage.width * 4;
                var tempRow  = new byte[rowBytes];
                for (int row = 0; row < depthImage.height / 2; row++)
                {
                    int top    = row * rowBytes;
                    int bottom = (depthImage.height - 1 - row) * rowBytes;
                    Buffer.BlockCopy(result, top,    tempRow, 0,      rowBytes);
                    Buffer.BlockCopy(result, bottom, result,  top,    rowBytes);
                    Buffer.BlockCopy(tempRow, 0,     result,  bottom, rowBytes);
                }

                return (result, depthImage.width, depthImage.height);
            }
            finally
            {
                depthImage.Dispose();
            }
        }

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
                float vFovRad      = _mainCamera.fieldOfView * Mathf.Deg2Rad;
                float estimatedFy  = 0.5f * h / Mathf.Tan(vFovRad * 0.5f);
                fx = estimatedFy * ((float)w / h); fy = estimatedFy;
                cx = w * 0.5f; cy = h * 0.5f;
                return IsValidIntrinsics(fx, fy, cx, cy, w, h);
            }

            return false;
        }

        static bool IsValidIntrinsics(float fx, float fy, float cx, float cy, int w, int h)
            => fx > 0 && fy > 0 && cx >= 0 && cx <= w && cy >= 0 && cy <= h;

        // ── Depth configuration ────────────────────────────────────────────

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

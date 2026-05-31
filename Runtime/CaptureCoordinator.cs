using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace SensorFlex.Recorder
{
    // Subscribes to ARFoundation callbacks, builds per-frame SfzFrameRecords in memory,
    // and routes binary data (jpg, depth) to CaptureFolderWriter.
    //
    // Threading: all public methods are called from the main thread.
    // The CaptureFolderWriter background thread only sees the FrameWriteJob value types
    // that are already copied by the time they are enqueued.
    internal sealed class CaptureCoordinator : IDisposable
    {
        // ── Public state (read after StopCapture) ──────────────────────────

        public string             TempDir       { get; }
        public SfzSessionMetadata SessionMetadata { get; private set; }
        public List<SfzFrameRecord> FrameRecords  { get; } = new List<SfzFrameRecord>(4096);

        // ── Private fields ─────────────────────────────────────────────────

        readonly ARSensorFlexRecorder _config;
        CaptureFolderWriter           _writer;
        ARCameraManager               _cameraManager;
        AROcclusionManager            _occlusionManager;
        Camera                        _mainCamera;

        int  _frameIndex;
        bool _isCapturing;
        bool _captureDepthEnabled;

        // First-frame dimension discovery (stable after first successful frame)
        bool _hasFirstColorDims;
        int  _firstColorW, _firstColorH;
        bool _hasFirstDepthDims;
        int  _firstDepthW, _firstDepthH;
        string _depthSensor;

        // Intrinsics fallback cache
        bool             _hasValidIntrinsics;
        float            _cachedFx, _cachedFy, _cachedCx, _cachedCy;

        // One-shot warning flags
        bool _warnedDropped;
        bool _warnedMissingIntrinsics;
        bool _warnedMissingDepth;

        const EnvironmentDepthMode TargetDepthMode = EnvironmentDepthMode.Medium;

        // ── Constructor ────────────────────────────────────────────────────

        public CaptureCoordinator(ARSensorFlexRecorder config, string tempDir)
        {
            _config  = config;
            TempDir  = tempDir;
            _writer  = new CaptureFolderWriter(tempDir);
        }

        // ── Lifecycle ──────────────────────────────────────────────────────

        public bool StartCapture()
        {
            if (_isCapturing)
                return true;

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
            FrameRecords.Clear();

            _writer.Start();
            _cameraManager.frameReceived += OnCameraFrameReceived;
            _isCapturing = true;

            Debug.Log($"[SF-Recorder] Capture started. TempDir='{TempDir}' FPS={_config.TargetFPS} Depth={_captureDepthEnabled}");
            return true;
        }

        public void StopCapture()
        {
            if (!_isCapturing)
                return;

            _isCapturing = false;

            if (_cameraManager != null)
                _cameraManager.frameReceived -= OnCameraFrameReceived;

            _writer.Stop();

            // Assemble session metadata from what was observed.
            SessionMetadata = new SfzSessionMetadata
            {
                SessionId    = _config.SessionId,
                StartTimeUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                DeviceModel  = SystemInfo.deviceModel,
                DeviceOs     = SystemInfo.operatingSystem,
                ArFramework  = ResolveCaptureFramework(),
                Fps          = _config.TargetFPS,
                HasRgb       = _hasFirstColorDims,
                RgbWidth     = _firstColorW,
                RgbHeight    = _firstColorH,
                HasDepth     = _hasFirstDepthDims,
                DepthWidth   = _firstDepthW,
                DepthHeight  = _firstDepthH,
                DepthSensor  = _depthSensor
            };

            Debug.Log($"[SF-Recorder] Capture stopped. Frames={FrameRecords.Count}");
        }

        public void Dispose() => StopCapture();

        // ── Frame callback (main thread) ───────────────────────────────────

        void OnCameraFrameReceived(ARCameraFrameEventArgs args)
        {
            if (!_isCapturing)
                return;

            long timestampNs = args.timestampNs.HasValue
                ? args.timestampNs.Value
                : (long)(Time.realtimeSinceStartup * 1_000_000_000L);

            // ── Colour ────────────────────────────────────────────────────
            byte[] jpg      = null;
            int    colorW   = 0, colorH = 0;
            bool   hasColor = false;

            if (_config.CaptureColor)
            {
                (jpg, colorW, colorH) = TryEncodeColorFrame(args);
                hasColor = jpg != null;

                if (hasColor && !_hasFirstColorDims)
                {
                    _firstColorW    = colorW;
                    _firstColorH    = colorH;
                    _hasFirstColorDims = true;
                }
            }

            // ── Depth ─────────────────────────────────────────────────────
            byte[] depthF32 = null;
            int    depthW   = 0, depthH = 0;
            bool   hasDepth = false;

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

            if (_config.CaptureIntrinsics
                && TryGetIntrinsics(colorW, colorH, out float oFx, out float oFy, out float oCx, out float oCy))
            {
                fx = oFx; fy = oFy; cx = oCx; cy = oCy;
                _cachedFx = fx; _cachedFy = fy; _cachedCx = cx; _cachedCy = cy;
                _hasValidIntrinsics = true;
                hasIntrinsics       = true;
            }
            else if (_config.CaptureIntrinsics && _hasValidIntrinsics)
            {
                fx = _cachedFx; fy = _cachedFy; cx = _cachedCx; cy = _cachedCy;
                hasIntrinsics = true;
            }
            else if (_config.CaptureIntrinsics && !_warnedMissingIntrinsics)
            {
                Debug.LogWarning("[SF-Recorder] No camera intrinsics available yet; frame will have no intrinsics.");
                _warnedMissingIntrinsics = true;
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

            // ── Enqueue disk write ─────────────────────────────────────────
            var job = new CaptureFolderWriter.FrameWriteJob
            {
                FrameIndex   = _frameIndex,
                JpgData      = jpg,
                DepthF32Data = depthF32
            };

            if (!_writer.TryEnqueue(job) && !_warnedDropped)
            {
                Debug.LogWarning($"[SF-Recorder] Frame {_frameIndex} dropped — disk writer queue is full.");
                _warnedDropped = true;
            }

            _frameIndex++;
        }

        // ── Color encoding ─────────────────────────────────────────────────

        (byte[] jpg, int w, int h) TryEncodeColorFrame(ARCameraFrameEventArgs args)
        {
            // Prefer CPU image (most accurate, no GPU readback stall)
            if (_cameraManager.TryAcquireLatestCpuImage(out var cpuImage))
                return EncodeCpuImageToJpg(cpuImage);

            // Fallback: encode directly from an ARFoundation texture in the event args
            if (args.textures != null && args.textures.Count > 0 && args.textures[0] != null)
            {
                var tex = args.textures[0];
                return EncodeTextureToJpg(tex, null, tex.width, tex.height);
            }

            Debug.LogWarning("[SF-Recorder] No camera image available this frame.");
            return (null, 0, 0);
        }

        static (byte[] jpg, int w, int h) EncodeCpuImageToJpg(XRCpuImage image)
        {
            var convParams = new XRCpuImage.ConversionParams
            {
                inputRect        = new RectInt(0, 0, image.width, image.height),
                outputDimensions = new Vector2Int(image.width, image.height),
                outputFormat     = TextureFormat.RGBA32,
                transformation   = XRCpuImage.Transformation.None
            };

            int size   = image.GetConvertedDataSize(convParams.outputDimensions, convParams.outputFormat);
            var buffer = new NativeArray<byte>(size, Allocator.Temp);

            try
            {
                image.Convert(convParams, buffer);
                var tex = new Texture2D(image.width, image.height, TextureFormat.RGBA32, false);
                tex.LoadRawTextureData(buffer);
                tex.Apply();
                byte[] jpg = tex.EncodeToJPG(80);
                UnityEngine.Object.Destroy(tex);
                return (jpg, image.width, image.height);
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

            var rt  = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);

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

        // ── Depth acquisition + conversion ─────────────────────────────────

        (byte[] depthF32, int w, int h) TryAcquireDepthFloat32()
        {
            if (_occlusionManager == null)
                return (null, 0, 0);

            if (!_occlusionManager.TryAcquireEnvironmentDepthCpuImage(out var depthImage))
            {
                if (!_warnedMissingDepth)
                {
                    Debug.LogWarning(
                        $"[SF-Recorder] Depth image unavailable. " +
                        $"requestedMode={_occlusionManager.requestedEnvironmentDepthMode} " +
                        $"currentMode={_occlusionManager.currentEnvironmentDepthMode}");
                    _warnedMissingDepth = true;
                }
                return (null, 0, 0);
            }

            _warnedMissingDepth = false;

            try
            {
                var plane      = depthImage.GetPlane(0);
                int pixelCount = depthImage.width * depthImage.height;
                int pixelStride = plane.pixelStride;

                byte[] result;

                if (pixelStride == 4)
                {
                    // Already float32 metres (e.g. ARKit LiDAR) — copy directly.
                    result = new byte[pixelCount * 4];
                    NativeArray<byte>.Copy(plane.data, result, result.Length);
                }
                else
                {
                    // uint16 millimetres (ARCore environment depth) → float32 metres.
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

            // 1. Native subsystem
            if (_cameraManager?.subsystem != null &&
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

            // 2. Estimate from encoded frame dimensions + camera FOV
            int w = colorW > 0 ? colorW : (_mainCamera != null ? _mainCamera.pixelWidth  : Screen.width);
            int h = colorH > 0 ? colorH : (_mainCamera != null ? _mainCamera.pixelHeight : Screen.height);

            if (_mainCamera != null && w > 0 && h > 0 && _mainCamera.fieldOfView > 0f)
            {
                float vFovRad = _mainCamera.fieldOfView * Mathf.Deg2Rad;
                float estimatedFy = 0.5f * h / Mathf.Tan(vFovRad * 0.5f);
                float estimatedFx = estimatedFy * ((float)w / h);
                fx = estimatedFx; fy = estimatedFy;
                cx = w * 0.5f;   cy = h * 0.5f;
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
                Debug.LogWarning($"[SF-Recorder] Promoting environment depth mode to {TargetDepthMode} for smoothed capture.");
                _occlusionManager.requestedEnvironmentDepthMode = TargetDepthMode;
            }

            _occlusionManager.environmentDepthTemporalSmoothingRequested = true;

            _depthSensor = Application.platform switch
            {
                RuntimePlatform.Android    => "arcore_environment_depth",
                RuntimePlatform.IPhonePlayer => "arkit_lidar",
                _                          => "arfoundation_environment_depth"
            };

            Debug.Log($"[SF-Recorder] Depth configured: requestedMode={_occlusionManager.requestedEnvironmentDepthMode} sensor={_depthSensor}");
        }

        // ── Helpers ────────────────────────────────────────────────────────

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

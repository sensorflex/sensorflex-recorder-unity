using System;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.Collections;
using Unity.XR.CoreUtils;

namespace SensorFlex.Recorder
{
    public class CaptureCoordinator : IDisposable
    {
        private enum IntrinsicsSource
        {
            Native,
            EncodedColorFrame,
            CameraConfiguration,
            CameraViewport,
            Cached
        }

        private struct ColorFrameCapture
        {
            public byte[] jpgData;
            public int width;
            public int height;
        }

        private struct DepthFrameCapture
        {
            public byte[] depthData;
            public int width;
            public int height;
        }

        private ARSensorFlexRecorder _config;
        private CaptureFolderWriter _writer;
        private RecorderSessionManifest _manifest;
        private ARCameraManager _cameraManager;
        private ARCameraBackground _cameraBackground;
        private AROcclusionManager _occlusionManager;
        private Camera _mainCamera;

        private int _frameIndex = 0;
        private bool _isCapturing;
        private bool _hasValidIntrinsics;
        private CameraIntrinsics _lastValidIntrinsics;
        private bool _loggedMissingIntrinsics;
        private string _lastLoggedIntrinsicsSource;
        private byte[] _cachedMaskPng;
        private int _cachedMaskWidth;
        private int _cachedMaskHeight;
        private bool _captureDepthEnabled;
        private bool _loggedMissingPose;
        private bool _loggedMissingDepthFrame;
        private bool _loggedDepthReadiness;

        private const EnvironmentDepthMode TargetDepthMode = EnvironmentDepthMode.Medium;

        public string SessionFolder { get; private set; }

        public CaptureCoordinator(ARSensorFlexRecorder config, string outputRoot)
        {
            _config = config;

            string sessionId = string.IsNullOrEmpty(config.SessionId) ? 
                Guid.NewGuid().ToString("N") : config.SessionId;

            SessionFolder = System.IO.Path.Combine(outputRoot, sessionId);

            _writer = new CaptureFolderWriter(SessionFolder);
            
            _manifest = new RecorderSessionManifest()
            {
                fps = config.TargetFPS,
                scene_id = sessionId
            };
            _manifest.source.device = SystemInfo.deviceModel;
            _manifest.source.capture_framework = ResolveCaptureFramework();
            _captureDepthEnabled = config.CaptureDepth;
            _manifest.pose.note = config.CaptureSceneMesh
                ? "aligned to packaged scanned mesh coordinate space when scanned_mesh is present"
                : "camera_to_world pose in the active Unity world frame";
            _manifest.pose_raw.scale = "arbitrary";
            _manifest.pose_raw.note = "raw camera_to_world pose captured directly from Unity/ARFoundation before any offline alignment";

            if (_config.CaptureSceneMesh)
            {
                _manifest.scanned_mesh = new ScannedMeshMetadata
                {
                    path = "scanned_mesh/mesh_aligned_0.05.ply",
                    format = "ply",
                    units = "meters",
                    coordinate_frame = "pose",
                    note = "Offline scanned mesh vertices are in the same world coordinate frame as frame meta.json pose"
                };
            }

            var origin = config.GetComponent<XROrigin>();
            if (origin != null)
                _mainCamera = origin.Camera;
            else
                _mainCamera = Camera.main;

            if (_mainCamera != null)
            {
                _cameraManager = _mainCamera.GetComponent<ARCameraManager>();
                _cameraBackground = _mainCamera.GetComponent<ARCameraBackground>();
                _occlusionManager = _mainCamera.GetComponent<AROcclusionManager>();
            }
            else
                Debug.LogError("[CaptureCoordinator] Could not find Main Camera or XROrigin Camera.");
        }

        public bool StartCapture()
        {
            if (_isCapturing) return true;

            if (_cameraManager == null)
            {
                Debug.LogError("[CaptureCoordinator] ARCameraManager not found on Camera. Cannot capture frames.");
                return false;
            }

            if (_config.CapturePose && _mainCamera == null)
            {
                Debug.LogError("[CaptureCoordinator] Camera pose capture was requested, but no AR camera reference is available.");
                return false;
            }

            if (_cameraManager.subsystem == null)
            {
                string platformHint = Application.platform == RuntimePlatform.Android
                    ? " On Android, enable the ARCore XR Plug-in for the Android build target and confirm the device supports ARCore."
                    : string.Empty;
                Debug.LogError($"[CaptureCoordinator] XRCameraSubsystem is not active. Recording requires an active XR loader.{platformHint}");
                return false;
            }

            if (Application.platform == RuntimePlatform.Android)
            {
                Debug.Log($"[CaptureCoordinator] Android capture path initialized with framework '{ResolveCaptureFramework()}'.");
            }

            if (_config.CaptureColor && _cameraBackground == null)
            {
                Debug.LogWarning("[CaptureCoordinator] ARCameraBackground is missing on the AR camera. CPU capture may still work, but GPU fallback for rgb.jpg will be unavailable.");
            }

            if (_config.CaptureDepth && (_occlusionManager == null || _occlusionManager.subsystem == null))
            {
                Debug.LogWarning("[CaptureCoordinator] Depth capture requested but XROcclusionSubsystem is unavailable. Depth will be skipped.");
                _captureDepthEnabled = false;
            }
            else if (_captureDepthEnabled)
            {
                ConfigureDepthCapture();
            }

            _writer.Start();
            _cameraManager.frameReceived += OnCameraFrameReceived;
            _isCapturing = true;
            _frameIndex = 0;
            _hasValidIntrinsics = false;
            _lastValidIntrinsics = default;
            _loggedMissingIntrinsics = false;
            _lastLoggedIntrinsicsSource = null;
            _cachedMaskPng = null;
            _cachedMaskWidth = 0;
            _cachedMaskHeight = 0;
            _loggedMissingPose = false;
            _loggedMissingDepthFrame = false;
            _loggedDepthReadiness = false;
            return true;
        }

        public void StopCapture()
        {
            if (!_isCapturing) return;

            _isCapturing = false;
            if (_cameraManager != null)
                _cameraManager.frameReceived -= OnCameraFrameReceived;

            _manifest.n_frames = _frameIndex;
            
            // finalize manifest before stop
            _writer.WriteSessionManifest(_manifest);
            _writer.Stop();
        }

        public void Dispose()
        {
            StopCapture();
        }

        private void OnCameraFrameReceived(ARCameraFrameEventArgs args)
        {
            if (!_isCapturing) return;

            // Optional: Throttle based on target FPS, currently capturing every available AR frame 
            // Phase 1 implementation grabs frame data immediately

            double timestamp = args.timestampNs.HasValue ? (args.timestampNs.Value / 1e9) : Time.realtimeSinceStartup;

            var frameMetadata = new FrameMetadata()
            {
                timestamp = timestamp
            };

            var colorFrame = default(ColorFrameCapture);
            if (_config.CaptureColor)
                colorFrame = TryEncodeColorFrame(args);

            var depthFrame = default(DepthFrameCapture);
            if (_captureDepthEnabled)
                depthFrame = TryEncodeDepthFrame();
            UpdateSessionStreamMetadata(colorFrame, depthFrame);

            // 1. Pose
            if (_config.CapturePose && _mainCamera != null)
            {
                var poseMatrix = BuildPoseMatrix(_mainCamera.transform.localToWorldMatrix);
                frameMetadata.pose = poseMatrix;
                frameMetadata.pose_raw = poseMatrix;
            }
            else if (_config.CapturePose && !_loggedMissingPose)
            {
                Debug.LogError("[CaptureCoordinator] Camera pose capture was requested, but the AR camera reference is no longer available during recording.");
                _loggedMissingPose = true;
            }

            // 2. Intrinsics
            if (_config.CaptureIntrinsics
                && TryGetValidIntrinsics(colorFrame.width, colorFrame.height, out var cameraIntrinsics, out var intrinsicsSource))
            {
                frameMetadata.intrinsic = BuildIntrinsicMatrix(cameraIntrinsics);
                _lastValidIntrinsics = cameraIntrinsics;
                _hasValidIntrinsics = true;
                _loggedMissingIntrinsics = false;
                LogIntrinsicsSource(intrinsicsSource, cameraIntrinsics);
            }
            else if (_config.CaptureIntrinsics)
            {
                if (!_loggedMissingIntrinsics)
                {
                    Debug.LogWarning("[CaptureCoordinator] No valid camera intrinsics were available. Frame metadata will keep a zero 3x3 intrinsic matrix until a valid source is found.");
                    _loggedMissingIntrinsics = true;
                }
                frameMetadata.intrinsic = BuildZeroIntrinsicMatrix();
            }

            frameMetadata.imu = ImuFrameMetadata.CreateZero();
            string metaJson = RecorderJsonSerializer.SerializeFrameMetadata(frameMetadata);

            // Enqueue
            var job = new CaptureFolderWriter.FrameWriteJob()
            {
                frameIndex = _frameIndex,
                metaJson = metaJson,
                jpgData = colorFrame.jpgData,
                maskPngData = TryBuildMaskPng(colorFrame.width, colorFrame.height),
                depthData = depthFrame.depthData
            };

            bool queued = _writer.TryEnqueueFrame(job);
            if (!queued)
            {
                Debug.LogWarning($"[CaptureCoordinator] Frame {_frameIndex} dropped due to queue pressure.");
            }

            _frameIndex++;
        }

        private ColorFrameCapture TryEncodeColorFrame(ARCameraFrameEventArgs args)
        {
            if (_cameraManager.TryAcquireLatestCpuImage(out var image))
                return EncodeCpuImageToJpg(image);

            if (TryEncodeBackgroundTextureToJpg(out var backgroundCapture))
                return backgroundCapture;

            if (args.textures != null && args.textures.Count > 0 && args.textures[0] != null)
                return EncodeTextureToJpg(args.textures[0]);

            Debug.LogWarning("[CaptureCoordinator] No camera image was available for this frame. AR Foundation color capture requires camera permission, an active ARCameraManager, and a working camera image source.");
            return default;
        }

        private bool TryEncodeBackgroundTextureToJpg(out ColorFrameCapture capture)
        {
            capture = default;

            if (_cameraBackground == null || !_cameraBackground.enabled)
                return false;

            var backgroundMaterial = _cameraBackground.material;
            if (backgroundMaterial == null || !backgroundMaterial.HasProperty("_MainTex"))
                return false;

            var backgroundTexture = backgroundMaterial.GetTexture("_MainTex");
            if (backgroundTexture == null)
                return false;

            capture = EncodeTextureToJpg(
                backgroundTexture,
                backgroundMaterial,
                Mathf.Max(backgroundTexture.width, _mainCamera != null ? _mainCamera.pixelWidth : Screen.width, 1),
                Mathf.Max(backgroundTexture.height, _mainCamera != null ? _mainCamera.pixelHeight : Screen.height, 1));
            return capture.jpgData != null && capture.jpgData.Length > 0;
        }

        private DepthFrameCapture TryEncodeDepthFrame()
        {
            if (_occlusionManager == null || _occlusionManager.subsystem == null)
                return default;

            if (!_occlusionManager.TryAcquireEnvironmentDepthCpuImage(out var depthImage))
            {
                if (!_loggedMissingDepthFrame)
                {
                    Debug.LogWarning(
                        $"[CaptureCoordinator] Depth capture is enabled, but no smoothed environment depth CPU image is available yet. " +
                        $"requestedMode={_occlusionManager.requestedEnvironmentDepthMode}, currentMode={_occlusionManager.currentEnvironmentDepthMode}, " +
                        $"temporalSmoothing={_occlusionManager.environmentDepthTemporalSmoothingRequested}.");
                    _loggedMissingDepthFrame = true;
                }

                return default;
            }

            try
            {
                _loggedMissingDepthFrame = false;
                var plane = depthImage.GetPlane(0);
                var bytes = new byte[plane.data.Length];
                plane.data.CopyTo(bytes);
                return new DepthFrameCapture
                {
                    depthData = bytes,
                    width = depthImage.width,
                    height = depthImage.height
                };
            }
            finally
            {
                depthImage.Dispose();
            }
        }

        private static ColorFrameCapture EncodeCpuImageToJpg(XRCpuImage image)
        {
            var conversionParams = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, image.width, image.height),
                outputDimensions = new Vector2Int(image.width, image.height),
                outputFormat = TextureFormat.RGBA32,
                transformation = XRCpuImage.Transformation.None
            };

            var tex = new Texture2D(conversionParams.outputDimensions.x, conversionParams.outputDimensions.y, TextureFormat.RGBA32, false);
            var size = image.GetConvertedDataSize(conversionParams.outputDimensions, conversionParams.outputFormat);
            var buffer = new NativeArray<byte>(size, Allocator.Temp);

            try
            {
                image.Convert(conversionParams, buffer);
                tex.LoadRawTextureData(buffer);
                tex.Apply();
                return new ColorFrameCapture
                {
                    jpgData = tex.EncodeToJPG(80),
                    width = conversionParams.outputDimensions.x,
                    height = conversionParams.outputDimensions.y
                };
            }
            finally
            {
                buffer.Dispose();
                image.Dispose();
                UnityEngine.Object.Destroy(tex);
            }
        }

        private static ColorFrameCapture EncodeTextureToJpg(Texture sourceTexture)
        {
            return EncodeTextureToJpg(
                sourceTexture,
                null,
                Mathf.Max(sourceTexture.width, 1),
                Mathf.Max(sourceTexture.height, 1));
        }

        private static ColorFrameCapture EncodeTextureToJpg(Texture sourceTexture, Material blitMaterial, int targetWidth, int targetHeight)
        {
            var width = Mathf.Max(targetWidth, 1);
            var height = Mathf.Max(targetHeight, 1);
            var renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            var previousRenderTexture = RenderTexture.active;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);

            try
            {
                if (blitMaterial != null)
                    Graphics.Blit(sourceTexture, renderTexture, blitMaterial);
                else
                    Graphics.Blit(sourceTexture, renderTexture);

                RenderTexture.active = renderTexture;
                tex.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
                tex.Apply();
                return new ColorFrameCapture
                {
                    jpgData = tex.EncodeToJPG(80),
                    width = renderTexture.width,
                    height = renderTexture.height
                };
            }
            finally
            {
                RenderTexture.active = previousRenderTexture;
                RenderTexture.ReleaseTemporary(renderTexture);
                UnityEngine.Object.Destroy(tex);
            }
        }

        private bool TryGetValidIntrinsics(int encodedColorWidth, int encodedColorHeight, out CameraIntrinsics cameraIntrinsics, out IntrinsicsSource intrinsicsSource)
        {
            if (TryGetNativeIntrinsics(out cameraIntrinsics))
            {
                intrinsicsSource = IntrinsicsSource.Native;
                return true;
            }

            if (TryBuildIntrinsicsFromDimensions(encodedColorWidth, encodedColorHeight, out cameraIntrinsics))
            {
                intrinsicsSource = IntrinsicsSource.EncodedColorFrame;
                return true;
            }

            if (TryBuildIntrinsicsFromCurrentConfiguration(out cameraIntrinsics))
            {
                intrinsicsSource = IntrinsicsSource.CameraConfiguration;
                return true;
            }

            if (TryBuildIntrinsicsFromCameraViewport(out cameraIntrinsics))
            {
                intrinsicsSource = IntrinsicsSource.CameraViewport;
                return true;
            }

            if (_hasValidIntrinsics && IsValidCameraIntrinsics(_lastValidIntrinsics))
            {
                cameraIntrinsics = _lastValidIntrinsics;
                intrinsicsSource = IntrinsicsSource.Cached;
                return true;
            }

            cameraIntrinsics = default;
            intrinsicsSource = default;
            return false;
        }

        private bool TryGetNativeIntrinsics(out CameraIntrinsics cameraIntrinsics)
        {
            if (_cameraManager != null
                && _cameraManager.subsystem != null
                && _cameraManager.subsystem.TryGetIntrinsics(out var intrinsics))
            {
                var candidate = new CameraIntrinsics
                {
                    focalLengthX = intrinsics.focalLength.x,
                    focalLengthY = intrinsics.focalLength.y,
                    principalPointX = intrinsics.principalPoint.x,
                    principalPointY = intrinsics.principalPoint.y,
                    resolutionWidth = intrinsics.resolution.x,
                    resolutionHeight = intrinsics.resolution.y
                };

                if (IsValidCameraIntrinsics(candidate))
                {
                    cameraIntrinsics = candidate;
                    return true;
                }
            }

            cameraIntrinsics = default;
            return false;
        }

        private bool TryBuildIntrinsicsFromCurrentConfiguration(out CameraIntrinsics cameraIntrinsics)
        {
            var currentConfiguration = _cameraManager != null ? _cameraManager.currentConfiguration : null;
            if (currentConfiguration.HasValue)
            {
                return TryBuildIntrinsicsFromDimensions(
                    currentConfiguration.Value.resolution.x,
                    currentConfiguration.Value.resolution.y,
                    out cameraIntrinsics);
            }

            cameraIntrinsics = default;
            return false;
        }

        private bool TryBuildIntrinsicsFromCameraViewport(out CameraIntrinsics cameraIntrinsics)
        {
            if (_mainCamera == null)
            {
                cameraIntrinsics = default;
                return false;
            }

            var width = _mainCamera.pixelWidth > 0 ? _mainCamera.pixelWidth : Screen.width;
            var height = _mainCamera.pixelHeight > 0 ? _mainCamera.pixelHeight : Screen.height;
            return TryBuildIntrinsicsFromDimensions(width, height, out cameraIntrinsics);
        }

        private bool TryBuildIntrinsicsFromDimensions(int width, int height, out CameraIntrinsics cameraIntrinsics)
        {
            if (_mainCamera == null || width <= 0 || height <= 0)
            {
                cameraIntrinsics = default;
                return false;
            }

            var verticalFovRadians = _mainCamera.fieldOfView * Mathf.Deg2Rad;
            if (verticalFovRadians <= 0f)
            {
                cameraIntrinsics = default;
                return false;
            }

            var focalLengthY = 0.5f * height / Mathf.Tan(verticalFovRadians * 0.5f);
            var focalLengthX = focalLengthY * ((float)width / height);

            var candidate = new CameraIntrinsics
            {
                focalLengthX = focalLengthX,
                focalLengthY = focalLengthY,
                principalPointX = width * 0.5f,
                principalPointY = height * 0.5f,
                resolutionWidth = width,
                resolutionHeight = height
            };

            if (!IsValidCameraIntrinsics(candidate))
            {
                cameraIntrinsics = default;
                return false;
            }

            cameraIntrinsics = candidate;
            return true;
        }

        private static bool IsValidCameraIntrinsics(CameraIntrinsics cameraIntrinsics)
        {
            return cameraIntrinsics.resolutionWidth > 0
                && cameraIntrinsics.resolutionHeight > 0
                && cameraIntrinsics.focalLengthX > 0f
                && cameraIntrinsics.focalLengthY > 0f
                && cameraIntrinsics.principalPointX >= 0f
                && cameraIntrinsics.principalPointX <= cameraIntrinsics.resolutionWidth
                && cameraIntrinsics.principalPointY >= 0f
                && cameraIntrinsics.principalPointY <= cameraIntrinsics.resolutionHeight;
        }

        private void LogIntrinsicsSource(IntrinsicsSource intrinsicsSource, CameraIntrinsics cameraIntrinsics)
        {
            var sourceLabel = intrinsicsSource.ToString();
            if (sourceLabel == _lastLoggedIntrinsicsSource)
                return;

            _lastLoggedIntrinsicsSource = sourceLabel;
            Debug.Log(
                $"[CaptureCoordinator] Camera intrinsics source={sourceLabel} " +
                $"size={cameraIntrinsics.resolutionWidth}x{cameraIntrinsics.resolutionHeight} " +
                $"fx={cameraIntrinsics.focalLengthX:F2} fy={cameraIntrinsics.focalLengthY:F2}");
        }

        private static double[][] BuildPoseMatrix(Matrix4x4 matrix)
        {
            return new[]
            {
                new[] { (double)matrix.m00, (double)matrix.m01, (double)matrix.m02, (double)matrix.m03 },
                new[] { (double)matrix.m10, (double)matrix.m11, (double)matrix.m12, (double)matrix.m13 },
                new[] { (double)matrix.m20, (double)matrix.m21, (double)matrix.m22, (double)matrix.m23 },
                new[] { (double)matrix.m30, (double)matrix.m31, (double)matrix.m32, (double)matrix.m33 }
            };
        }

        private static double[][] BuildIntrinsicMatrix(CameraIntrinsics cameraIntrinsics)
        {
            return new[]
            {
                new[] { (double)cameraIntrinsics.focalLengthX, 0d, (double)cameraIntrinsics.principalPointX },
                new[] { 0d, (double)cameraIntrinsics.focalLengthY, (double)cameraIntrinsics.principalPointY },
                new[] { 0d, 0d, 1d }
            };
        }

        private static double[][] BuildZeroIntrinsicMatrix()
        {
            return new[]
            {
                new[] { 0d, 0d, 0d },
                new[] { 0d, 0d, 0d },
                new[] { 0d, 0d, 1d }
            };
        }

        private byte[] TryBuildMaskPng(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return null;

            if (_cachedMaskPng != null && _cachedMaskWidth == width && _cachedMaskHeight == height)
                return _cachedMaskPng;

            var maskTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color32[width * height];
                for (var i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32(0, 0, 0, 255);
                maskTexture.SetPixels32(pixels);
                maskTexture.Apply();
                _cachedMaskPng = maskTexture.EncodeToPNG();
                _cachedMaskWidth = width;
                _cachedMaskHeight = height;
                return _cachedMaskPng;
            }
            finally
            {
                UnityEngine.Object.Destroy(maskTexture);
            }
        }

        private void UpdateSessionStreamMetadata(ColorFrameCapture colorFrame, DepthFrameCapture depthFrame)
        {
            if (colorFrame.width > 0 && colorFrame.height > 0)
            {
                _manifest.rgb = new ImageStreamMetadata
                {
                    width = colorFrame.width,
                    height = colorFrame.height,
                    format = "jpeg",
                    anonymization = "magenta_pixels"
                };

                _manifest.mask = new MaskStreamMetadata
                {
                    width = colorFrame.width,
                    height = colorFrame.height,
                    format = "png",
                    description = "non-zero pixels indicate anonymized regions"
                };
            }

            if (depthFrame.width > 0 && depthFrame.height > 0)
            {
                _manifest.depth = new DepthStreamMetadata
                {
                    width = depthFrame.width,
                    height = depthFrame.height,
                    format = "raw_uint16_le",
                    layout = "row_major",
                    units = "millimeters",
                    sensor = "arcore_environment_depth",
                    range_min = 0.0,
                    range_max = 65535.0,
                    invalid_value = 0.0,
                    note = "Smoothed environment depth acquired through AROcclusionManager.TryAcquireEnvironmentDepthCpuImage(). Each pixel is an unsigned 16-bit little-endian distance in millimeters along the camera principal axis."
                };
            }
        }

        private void ConfigureDepthCapture()
        {
            if (_occlusionManager == null)
                return;

            if (_occlusionManager.requestedEnvironmentDepthMode == EnvironmentDepthMode.Disabled)
            {
                Debug.LogWarning("[CaptureCoordinator] Depth capture requested while AROcclusionManager environment depth mode is disabled in the scene. Promoting it to Medium for this recording.");
                _occlusionManager.requestedEnvironmentDepthMode = TargetDepthMode;
            }
            else if (_occlusionManager.requestedEnvironmentDepthMode == EnvironmentDepthMode.Fastest)
            {
                Debug.LogWarning("[CaptureCoordinator] Depth capture requested with Fastest mode, which bypasses smoothing. Promoting it to Medium for smoothed environment depth recording.");
                _occlusionManager.requestedEnvironmentDepthMode = TargetDepthMode;
            }

            _occlusionManager.environmentDepthTemporalSmoothingRequested = true;

            if (!_loggedDepthReadiness)
            {
                Debug.Log(
                    $"[CaptureCoordinator] Depth capture configured with requestedMode={_occlusionManager.requestedEnvironmentDepthMode}, " +
                    $"currentMode={_occlusionManager.currentEnvironmentDepthMode}, temporalSmoothing={_occlusionManager.environmentDepthTemporalSmoothingRequested}.");
                _loggedDepthReadiness = true;
            }
        }

        private static string ResolveCaptureFramework()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.IPhonePlayer:
                    return "ARKit";
                case RuntimePlatform.Android:
                    return "ARCore";
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.LinuxEditor:
                    return "ARFoundation Simulation";
                default:
                    return "ARFoundation";
            }
        }
    }
}

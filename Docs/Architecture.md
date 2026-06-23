# SensorFlex Recorder Architecture

`com.sensorflex.recorder.unity` is the recording-side counterpart to `com.sensorflex.player.unity`.

The recorder captures ARFoundation session data in real time, persists binary frame assets to a
temporary folder on device during recording, and packages the result into one or more SFZ archives
after recording stops.

## Goals

- Record ARFoundation data in real time without stalling frame delivery.
- On iOS, use hardware HEVC encoding (VideoToolbox) for zero managed allocation on the hot path.
- Produce SFZ archives readable by the player and the Python data pipeline.
- Keep the live capture path as simple as possible.
- Treat archive creation as a post-recording finalization step.

## Non-Goals

- Compressing or zipping entries in the hot capture path.
- Per-frame JSON files on disk.
- Generalized media pipelines for unrelated formats.

## System Overview — iOS HEVC path (v1.1)

```
 ┌─────────────────────────────────────────────────────────┐
 │  Scene                                                   │
 │  XROrigin ──── ARSensorFlexRecorder                      │
 │    └─ Camera                                             │
 │         ├─ ARCameraManager                               │
 │         └─ AROcclusionManager                            │
 └───────────────┬─────────────────────┬───────────────────┘
                 │ frameReceived        │ depth CPU image
                 ▼                      ▼
 ┌──────────────────────────────────────────────────────────┐
 │  MAIN THREAD                                             │
 │  CaptureCoordinator.OnCameraFrameReceived()              │
 │                                                          │
 │  color ──► NativeVideoEncoder.AppendRgbFrame()           │
 │            (IntPtr to Y+CbCr planes; ~4 MB memcpy)       │
 │  depth ──► NativeVideoEncoder.AppendDepthFrame()         │
 │            (IntPtr to float32 plane; → float16 BGRA pack) │
 │  pose  ──► position + quaternion (Camera.transform)      │
 │  intrin ──► native › FOV estimate › cached fallback      │
 │                                                          │
 │  SfzFrameRecord ──────────────────────────────► List<>   │
 └──────────────────────────────────────────────────────────┘
                    │ P/Invoke (fast, < 0.1 ms)
                    ▼
 ┌──────────────────────────────────────────────────────────┐
 │  NATIVE (VideoToolbox hardware, dedicated HW block)      │
 │  SFVideoEncoder.mm                                       │
 │                                                          │
 │  RGB encoder:                                            │
 │    CVPixelBufferCreateWithPlanarBytes (owns copy)        │
 │    → AVAssetWriterInputPixelBufferAdaptor                │
 │    → HEVC YCbCr420 → rgb.mp4                            │
 │                                                          │
 │  Depth encoder:                                          │
 │    vImageConvert_PlanarFtoPlanar16F (float32 → float16)  │
 │    → CVPixelBufferPool (OneComponent16Half)              │
 │    → AVAssetWriterInputPixelBufferAdaptor                │
 │    → HEVC Monochrome → depth.mp4                        │
 └────────────────────┬─────────────────────────────────────┘
                      │ (on finish)
             temp/{id}/rgb.mp4
             temp/{id}/depth.mp4
                      │
                      │  List<SfzFrameRecord>
                      │  SfzSessionMetadata
                      ▼
 ┌────────────────────────────────────────────────────────────┐
 │  TASK THREAD — ArchiveFinalizer                            │
 │                                                            │
 │  NativeVideoEncoder.WaitForBothFinished()                  │
 │  SfzSerializer → session.json  (version "1.1")             │
 │                                                            │
 │  ┌─ {id}.sfz ──────────────────────────────────────────┐  │
 │  │  session/session.json  [DEFLATE]                     │  │
 │  │  session/rgb.mp4       [STORED]                      │  │
 │  │  session/depth.mp4     [STORED]                      │  │
 │  └──────────────────────────────────────────────────────┘  │
 └────────────────────────────────────────────────────────────┘
```

## System Overview — non-iOS / legacy path (v1.0)

```
 MAIN THREAD
   color ──► XRCpuImage.Convert(RGBA32) ──► NativeArray.ToArray()
   depth ──► float32 bytes
   both  ──► CaptureFolderWriter.TryEnqueue(RawFrameJob)
                     │  BlockingCollection[16]
                     ▼
   ENCODER THREADS (×2): RGBA → JPEG (software, ImageConversion)
   WRITER THREAD:  rgb.stream + depth.stream  (length-prefixed binary)
                     │
   TASK THREAD: scan streams → per-frame .jpg + .bin → SFZ v1.0
```

## High-Level Flow (iOS)

```
StartRecording
  CaptureCoordinator.StartCapture()
    └─ Subscribes to ARCameraManager.frameReceived

Each AR frame (main thread):
  ├─ First frame: NativeVideoEncoder.StartRgbSession(mp4Path, w, h)
  │                NativeVideoEncoder.StartDepthSession(mp4Path, w, h)
  ├─ NativeVideoEncoder.AppendRgbFrame(pY, strideY, pCbCr, strideCbCr, ...)
  ├─ NativeVideoEncoder.AppendDepthFrame(pF32, stride, ...)
  └─ Append SfzFrameRecord to in-memory list

StopRecording
  ├─ NativeVideoEncoder.FinishRgbSession()   → async seal
  ├─ NativeVideoEncoder.FinishDepthSession() → async seal
  └─ Launch ArchiveFinalizer.FinalizeAsync (background Task)
        ├─ NativeVideoEncoder.WaitForBothFinished(30s)
        ├─ Build session.json (v1.1)
        ├─ Write .sfz: session.json + rgb.mp4 + depth.mp4
        └─ Delete temp folder
```

## Output Format: SFZ

### v1.1 (iOS HEVC) — archive layout

```
session/
  session.json          ← version "1.1", channel format "hevc_mp4" / "hevc_bgra_float16"
  rgb.mp4               ← HEVC YCbCr420, all frames
  depth.mp4             ← max-quality HEVC BGRA32 (float16 metres packed in B+G channels)
```

### v1.0 (legacy) — archive layout

```
session/
  session.json          ← version "1.0", channel format "jpeg" / "raw_float32_le"
  rgb/
    000000.jpg … NNNNNN.jpg
  depth/
    000000.bin … NNNNNN.bin  (float32 LE metres)
```

### session.json — v1.1 example

```json
{
  "version": "1.1",
  "session_id": "abc123",
  "start_time_utc": "2026-05-30T10:30:00.000Z",
  "device": { "model": "iPhone 16 Pro", "os": "iOS 18.0", "ar_framework": "ARKit" },
  "tracks": {
    "frames": {
      "metadata": {
        "fps": 60,
        "channels": {
          "rgb":   { "width": 1920, "height": 1440, "format": "hevc_mp4",         "file": "rgb.mp4" },
          "depth": { "width": 256,  "height": 192,  "format": "hevc_bgra_float16", "file": "depth.mp4",
                     "units": "meters", "sensor": "arkit_lidar", "invalid_value": 0.0 }
        }
      },
      "data": [
        {
          "timestamp_ns": 178454292513458,
          "camera": {
            "pose": { "position": [1.69, 4.42, -1.62], "rotation": [0.12, 0.34, 0.56, 0.75] },
            "intrinsics": { "fx": 1425.3, "fy": 1425.3, "cx": 954.9, "cy": 725.4 }
          },
          "rgb":   { "file": "rgb.mp4",   "frame_index": 0 },
          "depth": { "file": "depth.mp4", "frame_index": 0 }
        }
      ]
    }
  }
}
```

### Depth encoding

**v1.1 (hevc_bgra_float16):** ARKit delivers depth as `kCVPixelFormatType_DepthFloat32` (metres).
The native plugin converts float32 → float16 (ARM `__fp16` cast) and packs the 16-bit value into a
BGRA32 pixel: B = low byte, G = high byte, R = 0, A = 0xFF. The CVPixelBuffer pool uses
`kCVPixelFormatType_32BGRA` with IOSurface backing, encoded at `"Quality": 1.0` (maximum HEVC
quality; `"Lossless"` crashes hvc1 on iOS). For smooth 256×192 LiDAR depth maps, quantisation
error is far below sensor noise (~1 cm). On decode: `bits = (G << 8) | B → view as float16 → float32`.

**v1.0 (raw_float32_le):** ARKit depth is copied directly (float32 metres, row-major). ARCore
uint16 mm depth is converted to float32 metres at capture time.

### Zip compression per entry type

| Entry              | Method     | Reason                                       |
|--------------------|------------|----------------------------------------------|
| `session.json`     | DEFLATE    | Text compresses well                         |
| `rgb.mp4`          | STORED     | HEVC is already compressed                   |
| `depth.mp4`        | STORED     | HEVC is already compressed                   |
| `rgb/NNNNNN.jpg`   | STORED     | JPEG is already compressed (legacy)          |
| `depth/NNNNNN.bin` | DEFLATE    | Float32 compresses significantly (legacy)    |

## Runtime Modules

### `ARSensorFlexRecorder`

Scene-facing MonoBehaviour attached to `XROrigin`. Unchanged from v1.0; all
iOS/non-iOS branching happens below this level.

### `CaptureCoordinator`

Owns the per-frame callback. On iOS (`#if UNITY_IOS && !UNITY_EDITOR`):
- Accesses `XRCpuImage.GetPlane()` NativeArrays via `NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr`
- Passes raw plane pointers to `NativeVideoEncoder`; disposes `XRCpuImage` immediately after
- Never allocates RGBA or depth byte arrays on the hot path

On non-iOS: unchanged RGBA→JPEG + float32 path.

### `CaptureFolderWriter`

Accepts `bool useNativeEncoder` in its constructor.

When true (iOS):
- No encoder or writer threads are started
- `TryEnqueue()` / `CompleteAdding()` are no-ops
- `WaitForFlush()` delegates to `NativeVideoEncoder.WaitForBothFinished()`
- Exposes `RgbMp4Path` / `DepthMp4Path` for the finalizer

When false (non-iOS): unchanged thread model.

### `NativeVideoEncoder`

Static C# class. On iOS: P/Invoke bridge into `SFVideoEncoder.mm`.
On all other platforms: empty stubs (no-ops).

Key methods:
- `StartRgbSession(path, w, h)` / `StartDepthSession(path, w, h)` — lazy, called on first frame
- `AppendRgbFrame(pY, strideY, pCbCr, strideCbCr, w, h, tsNs)` — per-frame, main thread
- `AppendDepthFrame(pF32, stride, w, h, tsNs)` — per-frame, main thread
- `FinishRgbSession()` / `FinishDepthSession()` — async, called from `StopCapture()`
- `WaitForBothFinished(timeoutMs)` — blocks on two `ManualResetEventSlim`s; called in finalization

### `SFVideoEncoder.mm` (iOS native plugin)

ObjC. Two independent `AVAssetWriter` sessions.

**RGB session:**
- `CVPixelBufferPoolCreatePixelBuffer` from the adaptor's own IOSurface-backed pool
- Per frame: row-by-row `memcpy` of Y plane and CbCr plane into the locked pixel buffer
- `AVAssetWriterInputPixelBufferAdaptor.appendPixelBuffer:withPresentationTime:` feeds VideoToolbox

**Depth session:**
- Same adaptor pool pattern as RGB; pool uses `kCVPixelFormatType_32BGRA` with IOSurface backing
- Per frame: per-pixel `__fp16` cast (float32 → float16) + bit-pack into B (low) / G (high) channels
- Max-quality HEVC (`"Quality": 1.0` via `AVVideoCompressionPropertiesKey`); "Lossless" is unsupported for hvc1 on iOS and crashes

### `ArchiveFinalizer`

Detects encoding mode by checking for `rgb.mp4` / `depth.mp4` in the temp folder:
- **Native path:** copies MP4 files wholesale into part 0 of the SFZ; no per-frame scanning
- **Legacy path:** unchanged `ScanStream` → per-frame zip entries

### `SfzSerializer` (`RecorderJsonSerializer.cs`)

`BuildSessionJson` now accepts `bool isNativePath`.  When true:
- Version `"1.1"`
- Channel blocks include `"format": "hevc_mp4"` / `"hevc_bgra_float16"` and `"file"` entries
- Per-frame `rgb`/`depth` objects are `{"file":"rgb.mp4","frame_index":N}` instead of per-file refs

## Threading Model

### iOS

| Thread          | Work                                                                        |
|-----------------|-----------------------------------------------------------------------------|
| Main thread     | ARFoundation callbacks, unsafe plane ptr access, NativeVideoEncoder calls   |
| VideoToolbox HW | Encoding (fully managed by AVFoundation; zero CPU cores consumed)           |
| Task thread     | `ArchiveFinalizer.Finalize` — wait for encoders, session.json build, zip    |

### Non-iOS

| Thread          | Work                                                                        |
|-----------------|-----------------------------------------------------------------------------|
| Main thread     | ARFoundation callbacks, image encoding, depth conversion, record accumulation|
| Encoder ×2      | `CaptureFolderWriter.EncoderLoop` — RGBA → JPEG                             |
| Writer          | `CaptureFolderWriter.WriterLoop` — disk IO for stream files                 |
| Task thread     | `ArchiveFinalizer.Finalize` — session.json build + zip                      |

## Data Model

### `SfzSessionMetadata` additions (v1.1)

| Field         | Values                                    |
|---------------|-------------------------------------------|
| RgbEncoding   | `"hevc"` (iOS) / `"jpeg"` (non-iOS)      |
| DepthEncoding | `"hevc_bgra_float16"` (iOS) / `"raw_float32_le"` (non-iOS) |

## Coordinate System

Unity world-space convention (left-handed, +Y up, +Z forward, metres). Pose `position` and
`rotation` are read directly from `Camera.transform` and written as-is.

## Temp Folder Location

`Application.temporaryCachePath/SF-Recorder/{sessionId}/`

Contains either `rgb.mp4` + `depth.mp4` (iOS) or `rgb.stream` + `depth.stream` (non-iOS).
Deleted automatically after successful finalization.

## Scene Setup

```
XROrigin  ← ARSensorFlexRecorder
  └─ Camera
       ├─ ARCameraManager
       └─ AROcclusionManager  (optional, for depth)
```

## Future Work

- Scanned mesh export via `ARMeshManager`
- FPS throttling (frame-skip when device runs faster than target)
- Partial session recovery on next app launch

# SensorFlex Recorder Architecture

`com.sensorflex.recorder.unity` is the recording-side counterpart to `com.sensorflex.player.unity`.

The recorder captures ARFoundation session data in real time, persists binary frame assets to a
temporary folder on device during recording, and packages the result into one or more SFZ 1.0
archives after recording stops.

The output format is exactly the SFZ 1.0 specification consumed by the player. No conversion step
is required to replay a recorded archive.

## Goals

- Record ARFoundation data in real time without stalling frame delivery.
- Produce SFZ 1.0 archives that the player can replay directly.
- Keep the live capture path as simple as possible: write binary files, accumulate lightweight metadata.
- Treat archive creation as a post-recording finalization step.
- Keep scene-facing APIs small.

## Non-Goals

- Compressing or zipping entries in the hot capture path.
- Per-frame JSON files on disk.
- Generalized media pipelines for unrelated formats.
- Capturing every ARFoundation subsystem on day one.

## System Overview

```
 ┌─────────────────────────────────────────────────────────┐
 │  Scene                                                   │
 │                                                          │
 │  XROrigin ──── ARSensorFlexRecorder                      │
 │    └─ Camera                                             │
 │         ├─ ARCameraManager                               │
 │         └─ AROcclusionManager                            │
 └───────────────┬─────────────────────┬───────────────────┘
                 │ frameReceived        │ depth CPU image
                 │                      │
 ┌───────────────▼──────────────────────▼───────────────────┐
 │  MAIN THREAD                                              │
 │                                                           │
 │  CaptureCoordinator.OnCameraFrameReceived()               │
 │                                                           │
 │   color  ──► encode JPEG  (XRCpuImage › Texture2D › JPG) │
 │   depth  ──► float32 m    (uint16 mm ÷ 1000, or copy)    │
 │   pose   ──► position + quaternion  (Camera.transform)    │
 │   intrin ──► native › FOV estimate › cached fallback      │
 │                                          │                │
 │   SfzFrameRecord ────────────────────────┼──► List<>      │
 │   (per-frame metadata, ~64 bytes)        │    in memory   │
 │                                          │                │
 │   FrameWriteJob (jpg bytes, depth bytes) │                │
 │     │                                    │                │
 └─────┼────────────────────────────────────┼────────────────┘
       │  BlockingCollection[120]            │
       ▼                                     │
 ┌─────────────────────────────────┐         │
 │  BACKGROUND THREAD              │         │
 │  CaptureFolderWriter            │         │
 │                                 │         │
 │  temp/{id}/rgb/000000.jpg       │         │
 │  temp/{id}/rgb/000001.jpg  …    │         │
 │  temp/{id}/depth/000000.bin     │         │
 │  temp/{id}/depth/000001.bin  …  │         │
 └─────────────────────────────────┘         │
        temp folder on disk                  │
              │    ┌───────────────────────── ┘
              │    │  List<SfzFrameRecord>
              │    │  SfzSessionMetadata
              ▼    ▼
 ┌────────────────────────────────────────────────────────────┐
 │  TASK THREAD                                               │
 │  ArchiveFinalizer                                          │
 │                                                            │
 │  SfzSerializer                                             │
 │    └─ session.json  (all frame records inline in data[])   │
 │                                                            │
 │  greedy bin-pack ─► SfzPartPlan[]                          │
 │                                                            │
 │  ┌─ single file ──────────────────────────────────────┐   │
 │  │  {id}.sfz                                          │   │
 │  │  └─ session/ ─ session.json  [DEFLATE]             │   │
 │  │              ─ rgb/NNNNNN.jpg   [STORED]           │   │
 │  │              ─ depth/NNNNNN.bin [DEFLATE]          │   │
 │  └────────────────────────────────────────────────────┘   │
 │  ─ or ─                                                    │
 │  ┌─ multi-part ───────────────────────────────────────┐   │
 │  │  {id}-00000-of-N.sfz  session.json + [0,   k)      │   │
 │  │  {id}-00001-of-N.sfz               + [k,  2k)      │   │
 │  │  …                                                  │   │
 │  └────────────────────────────────────────────────────┘   │
 │                                                            │
 │  delete temp folder                                        │
 └───────────────────────────────┬────────────────────────────┘
                                 │
                  ┌──────────────▼──────────────────┐
                  │  MAIN THREAD  (Update() poll)    │
                  │  RecordingFinalizedEvent(paths)  │
                  └─────────────────────────────────┘
```

## High-Level Flow

```
Start Recording
    │
    ├─ CaptureCoordinator subscribes to ARCameraManager.frameReceived
    │
    ▼ (each AR frame, main thread)
    ├─ Encode color image → jpg bytes
    ├─ Acquire depth, convert to float32 metres → raw bytes
    ├─ Extract camera pose (position + quaternion)
    ├─ Extract camera intrinsics (fx, fy, cx, cy)
    ├─ Append SfzFrameRecord to in-memory list
    └─ Enqueue FrameWriteJob ──► CaptureFolderWriter (background thread)
                                      │
                                      └─ writes  temp/{sessionId}/rgb/NNNNNN.jpg
                                                 temp/{sessionId}/depth/NNNNNN.bin

Stop Recording
    │
    ├─ Join writer thread (flush pending writes)
    ├─ Assemble SfzSessionMetadata from observed dimensions
    └─ Launch ArchiveFinalizer.FinalizeAsync (background Task)
                │
                ├─ Build session.json from in-memory frame records
                ├─ Plan partition (single file or multi-part)
                ├─ Write .sfz archive(s)
                └─ Delete temp folder

Main thread Update() polls Task completion → fire RecordingFinalizedEvent
```

## Output Format: SFZ 1.0

The recorder targets SFZ 1.0 as defined by `com.sensorflex.player.unity/Docs/SensorFlexFormat.md`.

### Archive layout (single-file)

```
session/
  session.json          ← all metadata + full frame record array
  rgb/
    000000.jpg
    000001.jpg
    ...
  depth/                ← present only when depth was captured
    000000.bin
    000001.bin
    ...
```

### Archive layout (multi-part)

When `MaxPartSizeMb > 0` and the total session exceeds the limit, output is split:

```
{sessionId}-00000-of-NNNNN.sfz   session.json + first frame chunk
{sessionId}-00001-of-NNNNN.sfz   next frame chunk
...
```

`session.json` always lives in part 0. It contains a `parts` manifest that maps each frame
range to its part file. All parts must be present before the player can load the session.

### session.json schema

```json
{
  "version": "1.0",
  "session_id": "abc123",
  "start_time_utc": "2026-05-30T10:30:00.000Z",
  "device": {
    "model": "Pixel 7 Pro",
    "os": "Android OS 13",
    "ar_framework": "ARCore"
  },
  "parts": [ ... ],
  "tracks": {
    "frames": {
      "metadata": {
        "fps": 30,
        "channels": {
          "rgb":   { "width": 1920, "height": 1440, "format": "jpeg" },
          "depth": { "width": 256,  "height": 192,  "format": "raw_float32_le",
                     "units": "meters", "sensor": "arcore_environment_depth", "invalid_value": 0.0 }
        }
      },
      "data": [
        {
          "timestamp_ns": 178454292513458,
          "camera": {
            "pose": { "position": [1.69, 4.42, -1.62], "rotation": [0.12, 0.34, 0.56, 0.75] },
            "intrinsics": { "fx": 1425.3, "fy": 1425.3, "cx": 954.9, "cy": 725.4 }
          },
          "rgb":   { "file": "rgb/000000.jpg" },
          "depth": { "file": "depth/000000.bin" }
        },
        ...
      ]
    }
  }
}
```

All per-frame metadata (pose, intrinsics, timestamps, file references) is embedded inline in the
`tracks.frames.data` array. There are no per-frame JSON files on disk.

### Depth encoding

Depth is stored as raw IEEE 754 float32, little-endian, row-major, in metres.

- ARCore (`TryAcquireEnvironmentDepthCpuImage` → uint16 mm): converted to float32 metres at
  capture time using integer division by 1000.
- ARKit (`XRCpuImage` with 4-byte pixel stride, already float32 metres): copied directly.

Invalid/no-return pixels are represented as `0.0`.

### Coordinate system

Follows the Unity world-space convention (left-handed, +Y up, +Z forward, metres). Pose
`position` and `rotation` are read directly from `Camera.transform.position` and
`Camera.transform.rotation` and written as-is.

### Zip compression per entry type

| Entry              | Method     | Reason                                      |
|--------------------|------------|---------------------------------------------|
| `session.json`     | DEFLATE    | Text compresses well                        |
| `rgb/NNNNNN.jpg`   | STORED     | JPEG is already compressed                  |
| `depth/NNNNNN.bin` | DEFLATE    | Float32 depth compresses significantly      |

## Runtime Modules

### `ARSensorFlexRecorder`

Scene-facing MonoBehaviour attached to `XROrigin`. Owns the recording lifecycle.

Responsibilities:
- Expose all configuration through Inspector fields.
- Validate subsystem availability before starting.
- Coordinate `CaptureCoordinator` and `ArchiveFinalizer`.
- Poll the finalization `Task` in `Update()` and fire events on the main thread.

Inspector fields:

| Field             | Default               | Description                                    |
|-------------------|-----------------------|------------------------------------------------|
| TargetFPS         | 30                    | Nominal capture rate written into session.json |
| SessionId         | _(auto UUID)_         | Override to use a fixed session identifier     |
| CaptureColor      | true                  | Write RGB frames                               |
| CaptureDepth      | true                  | Write depth frames when available              |
| CapturePose       | true                  | Record camera pose per frame                   |
| CaptureIntrinsics | true                  | Record intrinsics per frame                    |
| OutputDirectory   | SensorFlexRecordings  | Relative to persistentDataPath, or absolute    |
| MaxPartSizeMb     | 500                   | 0 = single file; > 0 = split at this limit     |
| RecordOnStart     | false                 | Auto-start once the camera subsystem is ready  |

Events:

| Event                    | Argument         | When fired                                  |
|--------------------------|------------------|---------------------------------------------|
| `RecordingStartedEvent`  | `string tempDir` | Immediately after capture begins            |
| `RecordingFinalizedEvent`| `string[] paths` | On main thread when .sfz files are written  |
| `RecordingFailedEvent`   | `string error`   | On start failure or finalization exception  |

### `CaptureCoordinator`

Internal class; one instance per recording session.

Responsibilities:
- Locate `ARCameraManager` and `AROcclusionManager` from the `XROrigin` camera.
- Subscribe to / unsubscribe from `ARCameraManager.frameReceived`.
- Encode each color frame to JPEG on the main thread (CPU image path preferred; texture blit fallback).
- Convert depth to float32 metres on the main thread.
- Accumulate `List<SfzFrameRecord>` in memory.
- Route binary data to `CaptureFolderWriter`.
- Expose `SessionMetadata` and `FrameRecords` after `StopCapture()`.

Frame record accumulation is the reason per-frame JSON files are not needed: the coordinator
maintains the full metadata list in memory and hands it off to the finalizer in one shot.

### `CaptureFolderWriter`

Background-thread disk writer.

Responsibilities:
- Create `rgb/` and `depth/` subdirectories inside the temp folder.
- Accept `FrameWriteJob` items from a bounded `BlockingCollection<>` (capacity 120).
- Write `rgb/NNNNNN.jpg` and `depth/NNNNNN.bin` on a dedicated worker thread.
- When the queue is full, the producer drops the frame and logs a warning rather than blocking
  the main thread.
- On `Stop()`, mark the queue complete and `Join` the thread with a 10-second timeout to flush
  all pending writes.

### `ArchiveFinalizer`

Post-recording packager.

Responsibilities:
- Accept the temp folder, session metadata, and frame record list from the coordinator.
- Build `session.json` bytes using `SfzSerializer`.
- Collect on-disk file sizes to plan the partition (if `maxPartSizeBytes > 0`).
- Write one or more `.sfz` files using `System.IO.Compression.ZipArchive` with per-entry
  compression levels.
- Delete the temp folder after successful packaging.
- Run entirely on a background `Task`; the result is polled from `ARSensorFlexRecorder.Update()`.

#### Multi-part algorithm

1. Compute `session.json` bytes (without parts manifest) → get byte length.
2. Collect `(rgbSize, depthSize)` for each frame from the temp folder.
3. Greedy bin-pack: iterate frames in order, accumulate size, start a new part when the
   current part would exceed `maxPartSizeBytes`. A single frame is never split across parts.
4. Assign `{sessionId}-{p:D5}-of-{total:D5}.sfz` filenames.
5. Rebuild `session.json` with the `parts` manifest.
6. Write each part zip, placing `session.json` only in part 0.

### `SfzSerializer` (`RecorderJsonSerializer.cs`)

Internal static class.

Responsibilities:
- Build the complete `session.json` byte array from `SfzSessionMetadata`,
  `List<SfzFrameRecord>`, and an optional `SfzPartPlan[]`.
- Serialize frame records compactly (one JSON object per line in the `data` array).
- No external JSON library; manual `StringBuilder` serialization with `G9`-formatted floats.

## Data Model

### `SfzFrameRecord` (struct)

In-memory representation of one captured frame. Kept in a `List<SfzFrameRecord>` during recording.

| Field          | Type       | Description                              |
|----------------|------------|------------------------------------------|
| FrameIndex     | int        | Zero-based sequential index              |
| TimestampNs    | long       | `ARCameraFrameEventArgs.timestampNs`     |
| Position       | Vector3    | Camera world position (metres)           |
| Rotation       | Quaternion | Camera world rotation                    |
| HasIntrinsics  | bool       |                                          |
| Fx, Fy, Cx, Cy | float      | Camera intrinsics in pixels              |
| HasColor       | bool       | Whether `rgb/NNNNNN.jpg` was written     |
| HasDepth       | bool       | Whether `depth/NNNNNN.bin` was written   |

Memory cost: ~64 bytes × frame count. At 30 fps for 10 minutes: ~18,000 frames ≈ 1.1 MB.

### `SfzSessionMetadata` (struct)

Session-level info assembled at `StopCapture()` time.

Includes: session id, UTC start time, device model, OS string, AR framework name, FPS target,
and the RGB + depth dimensions observed from the first successful frames.

### `SfzPartPlan` (struct)

One element of the multi-part partition plan.

| Field      | Description                                 |
|------------|---------------------------------------------|
| FileName   | Basename of the output .sfz file            |
| FrameStart | First frame index in this part (inclusive)  |
| FrameEnd   | Last frame index in this part (exclusive)   |

## Threading Model

| Thread       | Work                                                                 |
|--------------|----------------------------------------------------------------------|
| Main thread  | ARFoundation callbacks, image encoding, depth conversion, record accumulation |
| Writer thread| `CaptureFolderWriter.WriteLoop` — disk IO for jpg and bin files     |
| Task thread  | `ArchiveFinalizer.Finalize` — session.json build + zip writing       |

Unity objects (`Texture2D`, `XRCpuImage`, `Camera.transform`) are always accessed on the
main thread. The writer and finalizer threads only see plain byte arrays and value-type structs.

## Intrinsics Fallback Chain

Per-frame intrinsics are resolved in this priority order:

1. `XRCameraSubsystem.TryGetIntrinsics()` — native subsystem values (most accurate)
2. Camera FOV + frame dimensions — approximate estimate
3. Last valid intrinsics — reused when the above two fail mid-session

## Temp Folder Location

`Application.temporaryCachePath/SF-Recorder/{sessionId}/`

The OS may reclaim this path if the process is killed. Incomplete temp folders are not
automatically cleaned up in that case, but the finalizer will fail with a clear error.
The folder is deleted automatically after successful finalization.

## Scene Setup

Attach `ARSensorFlexRecorder` to the `XROrigin` GameObject (not to `ARSession`):

```
XROrigin  ← ARSensorFlexRecorder
  └─ Camera
       ├─ ARCameraManager
       └─ AROcclusionManager  (optional, for depth)
```

The component locates `ARCameraManager` and `AROcclusionManager` by walking down from the
`XROrigin` camera. No explicit scene references are required.

## Relationship to the Player

The recorder targets SFZ 1.0 exactly. The player's `SfzFileBackend` and `FileIoBackend`
(in `SfzSessionStore.cs`) define the contract:

- `session/session.json` must be present and parse as `SfzSessionJson`.
- `tracks.frames.data[i].rgb.file` resolves relative to `session/`.
- `tracks.frames.data[i].depth.file` resolves relative to `session/` (absent when not captured).
- Pose `position` and `rotation` map to `Matrix4x4.TRS(position, quaternion, Vector3.one)`.
- Intrinsics `{fx, fy, cx, cy}` map to `new Vector4(fx, fy, cx, cy)`.

The temp folder layout mirrors the SFZ `session/` layout, so an unfinalized session can be
loaded directly via the player's `FileIo` source mode for development and debugging.

## Phase 2 (Future)

- Scanned mesh export via `ARMeshManager` — write `scene_mesh.ply`, add `attachments.scene_mesh`
  to `session.json`, support multi-part attachment chunking.
- FPS throttling — currently captures every available AR frame; add frame-skip logic to enforce
  `TargetFPS` when the device runs faster.
- Partial session recovery — detect incomplete temp folders on next app launch and offer
  to re-run finalization.

# SensorFlex Recorder Architecture

`com.sensorflex.recorder.unity` is the recording-side counterpart to `com.sensorflex.player.unity`.

The recorder should be designed as a classical real-time capture pipeline:

- acquire ARFoundation data on the main thread
- stage only the minimum required data in memory
- persist raw capture artifacts to a folder on device during recording
- package that folder into the player-compatible `.zip` archive after recording stops

This keeps the live path simple and high-performance while still producing the same archive structure consumed by the player.

Configuration should follow the same approach used by the player package:

- recorder options live on scene components and are edited through the Inspector
- the recorder should not rely on a package-level `ScriptableObject` settings asset
- scene-local configuration is the source of truth for recording behavior

## Goals

- Record ARFoundation data in real time without stalling frame delivery.
- Preserve enough metadata to reconstruct the same playback format used by the player.
- Prefer folder-first capture for reliability and crash recovery.
- Treat zip creation as a finalization/export step, not a live recording step.
- Keep scene-facing APIs small and understandable.

## Non-Goals

- Writing compressed zip entries directly in the hot capture path.
- Building a generalized media pipeline for unrelated formats.
- Capturing every ARFoundation subsystem on day one.

## High-Level Flow

The recorder is split into three stages.

### 1. Acquisition

The acquisition stage reads live ARFoundation data:

- camera image or GPU texture
- per-frame pose
- camera intrinsics
- optional depth
- optional scanned mesh or scene metadata

This stage should do minimal work and avoid blocking disk IO, compression, or archive layout decisions.

### 2. Persistence

The persistence stage converts live data into a capture-folder layout on device:

- image files
- depth files
- per-frame metadata files
- session-level `meta.json`
- optional scene mesh artifacts

This stage is allowed to use background workers and bounded queues.

### 3. Finalization

After recording stops:

- flush pending writes
- verify capture completeness
- write any final manifest metadata
- package the capture folder into the final `.zip`

This stage can run after capture has ended, or on demand as an export step.

## Recommended Runtime Modules

The runtime can stay small if the design is centered on four modules.

### `ARSensorFlexRecorder`

Scene-facing component that owns recorder lifecycle.

Responsibilities:

- start and stop recording
- resolve scene references such as `ARSession`, `XROrigin`, camera, and optional mesh source
- expose recorder settings directly in the Inspector
- coordinate the other modules

This should be the only component most users need to attach.

### `CaptureCoordinator`

Internal orchestrator for a single recording session.

Responsibilities:

- subscribe to ARFoundation producers
- build per-frame capture records
- route capture data to staging queues
- own session identifiers, timestamps, and frame indices

This is the boundary between Unity/ARFoundation callbacks and the recorder pipeline.

### `CaptureFolderWriter`

Background-oriented writer that persists capture data to disk.

Responsibilities:

- create the session folder
- write RGB, depth, metadata, and mesh files
- maintain bounded queues and backpressure policy
- guarantee on-disk consistency at stop/finalize time

This is the main durability layer during live recording.

### `ArchiveFinalizer`

Post-recording packager that creates the final player archive.

Responsibilities:

- read the capture folder
- verify required files are present
- write or update top-level `meta.json` if needed
- generate the final `.zip` using `System.IO.Compression.ZipArchive`

This module should not be part of the hot path.

## Data Model

The recorder should distinguish between two kinds of data.

### Session Data

Written once or updated infrequently:

- scene id
- capture device metadata
- coordinate system metadata
- capture FPS target
- mesh metadata
- stream availability flags

This becomes top-level archive metadata.

### Frame Data

Written for each frame:

- `rgb.jpg`
- `meta.json`
- optional `depth.bin`

Frame metadata should include:

- frame index
- timestamp
- camera pose
- camera intrinsics
- image dimensions
- optional depth metadata

## On-Disk Capture Format

During recording, write to a plain folder using the same logical layout as the player archive.

Example:

```text
capture_root/
  scene_001/
    meta.json
    frames/
      000000/
        rgb.jpg
        meta.json
        depth.bin
      000001/
        rgb.jpg
        meta.json
    mesh/
      scanned_mesh.ply
```

Using the archive layout as the folder layout has two advantages:

- packaging becomes a straightforward zip step
- inspection and debugging are much easier

## Why Folder-First Instead Of Zip-First

Live zip writing is possible, but it is not the preferred architecture.

Problems with zip-first recording:

- compression adds CPU cost in the hot path
- frequent small-entry writes are inefficient
- partially written archives are harder to recover
- interruption handling is worse
- debugging captured artifacts is much harder

Advantages of folder-first recording:

- simpler live writer
- better failure recovery
- easy to inspect and validate output
- final zip packaging can run after capture ends

## Performance Strategy

The recorder should assume that camera capture and disk writes run at different speeds.

Recommended approach:

- main thread acquires frame references and lightweight metadata only
- expensive image encoding happens off the critical path when possible
- disk writes use a bounded queue
- if the queue fills, apply an explicit policy:
  - drop frames
  - reduce capture rate
  - or stop recording with a clear error

The system should not silently accumulate unbounded memory.

## Threading Model

Suggested threading model:

- main thread:
  - ARFoundation callbacks
  - frame index assignment
  - minimal metadata extraction
- worker thread or task queue:
  - image encoding
  - depth serialization
  - disk writes
- finalization task:
  - validation
  - zip packaging

Unity object access should remain on the main thread unless data has been copied into plain managed/native buffers first.

## Crash Safety

The recorder should be able to recover useful data from an interrupted capture.

Recommended safeguards:

- create a session folder immediately on start
- write files incrementally as frames are captured
- maintain a small session manifest or status file
- mark the capture as complete only after final flush succeeds

This allows:

- partial recovery
- easier debugging of failed captures
- optional later repackaging of an incomplete session

## Initial Scope Recommendation

For the first implementation, keep the supported streams narrow.

Phase 1:

- RGB frames
- pose
- intrinsics
- top-level session metadata
- zip finalization

Phase 2:

- depth
- scanned mesh export
- validation tooling
- recovery and resume improvements

## Scene Setup Recommendation

Recorder scene setup should be simple.

- `ARSession` with `ARSensorFlexRecorder`
- `XROrigin`
- optional mesh-export helper component if scanned mesh capture is enabled

The recorder should use explicit scene references where ambiguity is possible, especially for `XROrigin` and camera source selection.

## Relationship To The Player

The recorder should target the same logical package format that the player expects.

That means:

- same top-level `meta.json` concepts
- same frame folder structure
- same per-frame metadata schema
- same mesh metadata references

The player archive format should be the source of truth for exported capture layout. The recorder’s live folder format should mirror that layout as closely as possible.

## Summary

This recorder should not be designed as “zip writer plus ARFoundation hooks.”

It should be designed as:

- a real-time acquisition layer
- a durable folder writer
- a post-recording archive finalizer

That is the standard recording-system shape, and it is the right tradeoff for performance, reliability, and compatibility with the SensorFlex player format.

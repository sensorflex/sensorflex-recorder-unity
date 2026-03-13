# SensorFlex Recorder Task

## Purpose

Build the first usable version of the SensorFlex Unity recorder package:

- package: `Packages/com.sensorflex.recorder.unity`
- primary component: `SensorFlex.Recorder.ARSensorFlexRecorder`

The goal is to record ARFoundation session data in real time, write it safely to disk on device, and then package the capture into the same archive structure used by the SensorFlex player package.

This document is intended as a handoff to another developer. It assumes they do not have prior context from earlier design discussions.

## Product Goal

The recorder should let a Unity developer:

1. attach `ARSensorFlexRecorder` to the scene's `XROrigin`
2. configure recording behavior directly in the Inspector
3. start and stop a recording session
4. capture session data to a folder on device during recording
5. finalize that capture into a `.zip` archive compatible with the SensorFlex player

The final archive must match the player-side expectations closely enough that the player can replay recorded sessions without requiring a separate conversion step.

## Key Design Decisions Already Made

These decisions should be treated as intentional unless there is a strong technical reason to change them.

### 1. No Package-Level Recorder Settings Asset

The recorder should not use a `ScriptableObject` settings asset.

Reason:

- This mirrors the direction already taken in the player package.
- Recording configuration is scene/session-specific, not package-global.
- Users should configure recorder behavior through the Inspector on `ARSensorFlexRecorder`.

Current status:

- `SensorFlexRecorderSettings` has already been removed.
- `ARSensorFlexRecorder` now owns inspector-exposed fields directly.

### 2. Folder-First Recording, Zip-After-Stop

The recorder should write to a normal folder on device while recording.

Only after recording stops should it package that folder into a `.zip`.

Reason:

- better real-time performance
- lower CPU pressure during capture
- simpler crash recovery
- easier debugging of recorded artifacts
- avoids making archive packaging part of the hot path

### 3. Match The Player Archive Format

The recorder does not define a new export format.

It should target the same logical archive layout already consumed by:

- `Packages/com.sensorflex.player.unity/Runtime/Library/FrameLoading.cs`
- `Packages/com.sensorflex.player.unity/Runtime/Library/ScannedSceneMeshLoading.cs`
- `Packages/com.sensorflex.player.unity/Runtime/Library/ArchiveIOUtils.cs`

The player-side archive format is the effective source of truth for exported data layout.

### 4. Recorder Scope Is Core Session Recording First

The first implementation should focus on recording core session data:

- color frames
- camera pose
- camera intrinsics
- optional depth
- session metadata

Scanned mesh support is useful, but it should not complicate the first milestone if it slows down delivery of a stable core recorder.

## Current Repository State

The recorder package is still an early skeleton.

Important files:

- [`ARSensorFlexRecorder.cs`](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.recorder.unity/Runtime/ARSensorFlexRecorder.cs)
- [`Architecture.md`](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.recorder.unity/Docs/Architecture.md)
- [`README.md`](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.recorder.unity/README.md)

Important context from the player package:

- [`FrameLoading.cs`](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Library/FrameLoading.cs)
- [`ScannedSceneMeshLoading.cs`](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Library/ScannedSceneMeshLoading.cs)
- [`ArchiveIOUtils.cs`](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Library/ArchiveIOUtils.cs)

Note:

- the generated Unity `.csproj` files may temporarily reference deleted files until Unity regenerates them
- do not treat generated `.csproj` contents as design source of truth

## Recorder Scene Model

Expected scene usage:

- `XROrigin`
  - has `ARSensorFlexRecorder`
- optional helper objects for mesh export or debugging

For now, `ARSensorFlexRecorder` is attached to `XROrigin`, unlike the player session which was moved onto `ARSession`.

This is acceptable unless the implementation reveals a strong reason to change it.

## Required Functional Behavior

The recorder implementation should support the following lifecycle.

### Start Recording

When recording starts:

- validate required scene references and subsystem availability
- create a unique session id if none was provided
- create the output folder for the capture
- create initial session metadata structures
- begin capturing frame-aligned data

### During Recording

While recording:

- capture data from ARFoundation without blocking the main thread unnecessarily
- write frame artifacts into the capture folder structure
- keep memory bounded
- maintain enough metadata to produce a valid final archive

### Stop Recording

When recording stops:

- stop accepting new frames
- flush pending queued writes
- finalize session-level metadata
- package the capture folder into a `.zip`
- report success or failure clearly in logs

## Minimum Data To Capture

Phase 1 should capture:

- RGB image per frame
- pose per frame
- camera intrinsics per frame
- timestamps / frame index
- top-level scene/session metadata

Optional for the first milestone:

- depth
- scanned mesh
- device metadata beyond the essentials

The implementation should be structured so these optional streams can be added cleanly later.

## Expected Archive Shape

The recorder should write a folder layout that mirrors the player archive layout.

High-level example:

```text
recording_output/
  <scene_id>/
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

The final `.zip` should preserve this logical structure.

The exact names and metadata fields must be validated against the player-side loaders.

## Performance Requirements

The recorder is explicitly intended to support real-time, high-performance capture.

That means:

- do not perform zip creation during live capture
- avoid heavy image encoding work directly inside ARFoundation callbacks when possible
- do not allow unbounded in-memory buffering
- prefer bounded queues and explicit backpressure policy

Acceptable strategies include:

- main thread copies lightweight frame data references or buffers
- background worker encodes image data and writes files
- finalizer packages the session after stop

## Reliability Requirements

The recorder should degrade predictably instead of silently failing.

Requirements:

- if recording cannot start, log a clear reason and do not enter a half-recording state
- if disk writing falls behind, use an explicit policy and log it
- partially written folder captures should remain inspectable for debugging
- final archive creation should be treated as a separate success/failure step from live recording

## Suggested Internal Modules

The implementation should likely be organized around these internal modules.

### `CaptureCoordinator`

Responsibilities:

- own per-session state
- assign frame indices
- collect frame-level metadata
- route data to the writer

### `CaptureFolderWriter`

Responsibilities:

- create directories
- write RGB, metadata, and optional depth files
- flush safely at stop

### `ArchiveFinalizer`

Responsibilities:

- validate output folder completeness
- generate final archive using `System.IO.Compression.ZipArchive`

### `RecorderSessionManifest`

Responsibilities:

- track capture state
- store session-level metadata before final archive assembly

These types do not have to use exactly these names, but the separation of responsibilities should remain clear.

## Inspector Expectations

`ARSensorFlexRecorder` should remain the main user-facing surface.

Its inspector should expose:

- target FPS
- optional session id override
- color capture toggle
- depth capture toggle
- pose capture toggle
- intrinsics capture toggle
- scanned mesh capture toggle
- output directory
- record on start

If custom inspector UI is added, it should follow the same style direction used in the player:

- scene/session-local config only
- no package-global settings asset
- UI should be straightforward, not over-abstracted

## Relationship To Player Work

The developer taking this task should understand the recorder and player are coupled through data format, not through shared runtime behavior.

Important implications:

- the player's archive readers define what the recorder must emit
- if the archive schema changes, both packages must stay in sync
- the recorder should not invent a new metadata layout without updating the player accordingly

Before implementing final archive writing, inspect:

- player metadata parsing
- frame layout assumptions
- mesh metadata expectations
- ZIP layout validation logic

## Things To Be Careful About

### ARFoundation Access Patterns

Not all ARFoundation data is equally cheap to acquire.

Be careful with:

- camera image extraction cost
- texture readback cost
- depth availability differences by platform
- when Unity objects can and cannot be touched off the main thread

### Image Encoding

If RGB frames are stored as `.jpg`, the implementation needs to decide:

- where JPEG encoding occurs
- how much data is copied
- how queue pressure is managed

This will likely be one of the main performance bottlenecks.

### Archive Compatibility

Do not assume that “roughly similar” output is enough.

The player archive loader has concrete assumptions about:

- path layout
- metadata presence
- naming
- file format

### Scope Control

It is easy for this task to expand into a full sensor framework.

Do not overbuild the first milestone.

Phase 1 should aim for:

- a stable recording lifecycle
- valid folder output
- valid final `.zip`
- successful replay in the player for the supported streams

## Concrete Deliverables

The implementation should produce:

1. A usable `ARSensorFlexRecorder` runtime component.
2. Internal runtime modules for capture coordination, folder writing, and archive finalization.
3. A capture folder layout compatible with the player archive structure.
4. Final `.zip` generation after recording stops.
5. Updated documentation describing usage and limitations.

## Acceptance Criteria

The task is complete when all of the following are true.

### Functional

- A developer can add `ARSensorFlexRecorder` to a scene and configure it only through the Inspector.
- Starting a recording creates a capture folder and writes session data.
- Stopping a recording finalizes the folder and creates a `.zip`.
- The resulting archive can be consumed by the SensorFlex player for the supported streams.

### Technical

- Live recording does not rely on writing zip entries directly.
- Recorder state is bounded and does not rely on unbounded in-memory growth.
- The design is modular enough to add depth and mesh support incrementally.

### Documentation

- The package docs clearly explain how to use the recorder.
- The implementation constraints and remaining limitations are documented.

## Out Of Scope For The First Pass

Unless implementation is trivial, do not let these block the first milestone:

- advanced resume/recovery features
- incremental zip updates during recording
- generalized import/export tooling
- multiple concurrent recorder sessions
- broad platform-specific optimization work

## Recommended First Implementation Order

1. Implement recorder lifecycle and session folder creation.
2. Implement frame indexing and per-frame metadata writing.
3. Implement RGB frame persistence.
4. Implement final archive packaging.
5. Verify player compatibility with a recorded archive.
6. Add optional depth support.
7. Add optional mesh export support.

## Summary

Build a practical, high-performance session recorder for ARFoundation that:

- is configured through `ARSensorFlexRecorder` in the Inspector
- records to a folder during capture
- packages to the player-compatible `.zip` after stop
- stays narrowly focused on reliable session recording, not generalized media tooling

The most important constraint is compatibility with the existing SensorFlex player archive format while keeping the live recording path simple and robust.

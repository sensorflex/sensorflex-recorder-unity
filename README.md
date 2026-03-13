# SensorFlex Unity Recorder

`com.sensorflex.recorder.unity` is the recording-side Unity package for the SensorFlex ecosystem.

This package is intentionally starting with a minimal structure:

- `Runtime/`
  Public runtime APIs and components for capture sessions.
- `Editor/`
  Optional editor tooling and inspectors.
- `Docs/`
  Package-level documentation and architecture notes.

The current skeleton includes:

- `ARSensorFlexRecorder`
- runtime and editor assembly definitions

The actual recording pipeline, data export flow, and capture backends still need to be implemented.

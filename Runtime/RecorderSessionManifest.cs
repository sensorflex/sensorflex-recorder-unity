using UnityEngine;

namespace SensorFlex.Recorder
{
    // Per-frame record kept in memory during recording and serialized into
    // session.json tracks.frames.data at finalization time.
    internal struct SfzFrameRecord
    {
        public int      FrameIndex;
        public long     TimestampNs;
        public Vector3  Position;
        public Quaternion Rotation;
        public bool     HasIntrinsics;
        public float    Fx, Fy, Cx, Cy;
        public bool     HasColor;
        public bool     HasDepth;
    }

    // Session-level info collected at StartCapture time.
    internal struct SfzSessionMetadata
    {
        public string SessionId;
        public string StartTimeUtc;   // ISO-8601, e.g. "2026-05-30T10:00:00.000Z"
        public string DeviceModel;
        public string DeviceOs;
        public string ArFramework;
        public int    Fps;
        // Populated from the first successful frame.
        public bool   HasRgb;
        public int    RgbWidth, RgbHeight;
        public bool   HasDepth;
        public int    DepthWidth, DepthHeight;
        public string DepthSensor;    // e.g. "arcore_environment_depth"
    }

    // One entry in the multi-part plan; FrameStart inclusive, FrameEnd exclusive.
    internal struct SfzPartPlan
    {
        public string FileName;    // basename only, e.g. "abc-00001-of-00003.sfz"
        public int    FrameStart;
        public int    FrameEnd;
    }
}

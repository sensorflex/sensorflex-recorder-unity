using System;

namespace SensorFlex.Recorder
{
    [Serializable]
    public class RecorderSessionManifest
    {
        public string format_version = "1.1";
        public SourceMetadata source = new SourceMetadata();
        public CoordinateSystemMetadata coordinate_system = new CoordinateSystemMetadata();
        public PoseSchemaMetadata pose = new PoseSchemaMetadata();
        public PoseSchemaMetadata pose_raw = new PoseSchemaMetadata();
        public CameraSchemaMetadata camera = new CameraSchemaMetadata();
        public ImageStreamMetadata rgb;
        public DepthStreamMetadata depth;
        public MaskStreamMetadata mask;
        public ImuSchemaMetadata imu = new ImuSchemaMetadata();
        public int fps;
        public string scene_id;
        public int n_frames;
        public ScannedMeshMetadata scanned_mesh;
    }

    [Serializable]
    public class FrameMetadata
    {
        public double timestamp;
        public double[][] pose;
        public double[][] pose_raw;
        public double[][] intrinsic;
        public ImuFrameMetadata imu = ImuFrameMetadata.CreateZero();
    }

    [Serializable]
    public class SourceMetadata
    {
        public string dataset = "SensorFlex Recorder";
        public string device = "Unknown";
        public string capture_framework = "ARFoundation";
    }

    [Serializable]
    public class CoordinateSystemMetadata
    {
        public string handedness = "right";
        public string up = "+Y";
        public string forward = "-Z";
        public string units = "meters";
    }

    [Serializable]
    public class PoseSchemaMetadata
    {
        public string convention = "camera_to_world";
        public string scale = "metric";
        public string layout = "row_major_4x4";
        public string note;
    }

    [Serializable]
    public class CameraSchemaMetadata
    {
        public string model = "perspective";
        public string distortion_model = "none";
        public double[] distortion_coefficients = Array.Empty<double>();
        public string intrinsic_variation = "per_frame";
        public string intrinsic_layout = "row_major_3x3";
    }

    [Serializable]
    public class ImageStreamMetadata
    {
        public int width;
        public int height;
        public string format;
        public string anonymization;
    }

    [Serializable]
    public class DepthStreamMetadata
    {
        public int width;
        public int height;
        public string format;
        public string layout;
        public string units;
        public string sensor;
        public double range_min;
        public double range_max;
        public double invalid_value;
    }

    [Serializable]
    public class MaskStreamMetadata
    {
        public int width;
        public int height;
        public string format;
        public string description;
    }

    [Serializable]
    public class ScannedMeshMetadata
    {
        public string path;
        public string format;
        public string units;
        public string coordinate_frame;
        public string note;
    }

    [Serializable]
    public class ImuSchemaMetadata
    {
        public string frame = "device_body";
        public ImuFieldsMetadata fields = new ImuFieldsMetadata();
    }

    [Serializable]
    public class ImuFieldsMetadata
    {
        public VectorFieldMetadata rotate_rate = new VectorFieldMetadata
        {
            units = "rad/s",
            axes = new[] { "x", "y", "z" }
        };

        public VectorFieldMetadata acceleration = new VectorFieldMetadata
        {
            units = "m/s2",
            axes = new[] { "x", "y", "z" },
            note = "gravity removed"
        };

        public VectorFieldMetadata magnet = new VectorFieldMetadata
        {
            units = "uT",
            axes = new[] { "x", "y", "z" }
        };

        public VectorFieldMetadata attitude = new VectorFieldMetadata
        {
            units = "rad",
            axes = new[] { "roll", "pitch", "yaw" }
        };

        public VectorFieldMetadata gravity = new VectorFieldMetadata
        {
            units = "unit_vector",
            axes = new[] { "x", "y", "z" }
        };
    }

    [Serializable]
    public class VectorFieldMetadata
    {
        public string units;
        public int[] shape = new[] { 3 };
        public string[] axes;
        public string note;
    }

    [Serializable]
    public class ImuFrameMetadata
    {
        public double[] rotate_rate;
        public double[] acceleration;
        public double[] magnet;
        public double[] attitude;
        public double[] gravity;

        public static ImuFrameMetadata CreateZero()
        {
            return new ImuFrameMetadata
            {
                rotate_rate = CreateZeroVector(),
                acceleration = CreateZeroVector(),
                magnet = CreateZeroVector(),
                attitude = CreateZeroVector(),
                gravity = CreateZeroVector()
            };
        }

        private static double[] CreateZeroVector()
        {
            return new double[] { 0d, 0d, 0d };
        }
    }

    public struct CameraIntrinsics
    {
        public float focalLengthX;
        public float focalLengthY;
        public float principalPointX;
        public float principalPointY;
        public int resolutionWidth;
        public int resolutionHeight;
    }
}

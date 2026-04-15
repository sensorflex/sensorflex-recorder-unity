using System.Globalization;
using System.Text;

namespace SensorFlex.Recorder
{
    internal static class RecorderJsonSerializer
    {
        public static string SerializeSessionManifest(RecorderSessionManifest manifest)
        {
            var sb = new StringBuilder(4096);
            sb.AppendLine("{");
            AppendProperty(sb, "format_version", manifest.format_version, 1, true);

            sb.AppendLine(Indent(1) + "\"source\": {");
            AppendProperty(sb, "dataset", manifest.source?.dataset, 2, true);
            AppendProperty(sb, "device", manifest.source?.device, 2, true);
            AppendProperty(sb, "capture_framework", manifest.source?.capture_framework, 2, false);
            sb.AppendLine(Indent(1) + "},");

            sb.AppendLine(Indent(1) + "\"coordinate_system\": {");
            AppendProperty(sb, "handedness", manifest.coordinate_system?.handedness, 2, true);
            AppendProperty(sb, "up", manifest.coordinate_system?.up, 2, true);
            AppendProperty(sb, "forward", manifest.coordinate_system?.forward, 2, true);
            AppendProperty(sb, "units", manifest.coordinate_system?.units, 2, false);
            sb.AppendLine(Indent(1) + "},");

            AppendPoseSchema(sb, "pose", manifest.pose, true);
            AppendPoseSchema(sb, "pose_raw", manifest.pose_raw, true);

            sb.AppendLine(Indent(1) + "\"camera\": {");
            AppendProperty(sb, "model", manifest.camera?.model, 2, true);
            AppendProperty(sb, "distortion_model", manifest.camera?.distortion_model, 2, true);
            AppendDoubleArrayProperty(sb, "distortion_coefficients", manifest.camera?.distortion_coefficients, 2, true);
            AppendProperty(sb, "intrinsic_variation", manifest.camera?.intrinsic_variation, 2, true);
            AppendProperty(sb, "intrinsic_layout", manifest.camera?.intrinsic_layout, 2, false);
            sb.AppendLine(Indent(1) + "},");

            if (manifest.rgb != null)
            {
                sb.AppendLine(Indent(1) + "\"rgb\": {");
                AppendProperty(sb, "width", manifest.rgb.width, 2, true);
                AppendProperty(sb, "height", manifest.rgb.height, 2, true);
                AppendProperty(sb, "format", manifest.rgb.format, 2, true);
                AppendProperty(sb, "anonymization", manifest.rgb.anonymization, 2, false);
                sb.AppendLine(Indent(1) + "},");
            }

            if (manifest.depth != null)
            {
                sb.AppendLine(Indent(1) + "\"depth\": {");
                AppendProperty(sb, "width", manifest.depth.width, 2, true);
                AppendProperty(sb, "height", manifest.depth.height, 2, true);
                AppendProperty(sb, "format", manifest.depth.format, 2, true);
                AppendProperty(sb, "layout", manifest.depth.layout, 2, true);
                AppendProperty(sb, "units", manifest.depth.units, 2, true);
                AppendProperty(sb, "sensor", manifest.depth.sensor, 2, true);
                AppendProperty(sb, "range_min", manifest.depth.range_min, 2, true);
                AppendProperty(sb, "range_max", manifest.depth.range_max, 2, true);
                AppendProperty(sb, "invalid_value", manifest.depth.invalid_value, 2, manifest.depth.note != null);
                if (manifest.depth.note != null)
                    AppendProperty(sb, "note", manifest.depth.note, 2, false);
                sb.AppendLine(Indent(1) + "},");
            }

            if (manifest.mask != null)
            {
                sb.AppendLine(Indent(1) + "\"mask\": {");
                AppendProperty(sb, "width", manifest.mask.width, 2, true);
                AppendProperty(sb, "height", manifest.mask.height, 2, true);
                AppendProperty(sb, "format", manifest.mask.format, 2, true);
                AppendProperty(sb, "description", manifest.mask.description, 2, false);
                sb.AppendLine(Indent(1) + "},");
            }

            if (manifest.imu != null)
            {
                sb.AppendLine(Indent(1) + "\"imu\": {");
                AppendProperty(sb, "frame", manifest.imu.frame, 2, true);
                sb.AppendLine(Indent(2) + "\"fields\": {");
                AppendVectorField(sb, "rotate_rate", manifest.imu.fields?.rotate_rate, true);
                AppendVectorField(sb, "acceleration", manifest.imu.fields?.acceleration, true);
                AppendVectorField(sb, "magnet", manifest.imu.fields?.magnet, true);
                AppendVectorField(sb, "attitude", manifest.imu.fields?.attitude, true);
                AppendVectorField(sb, "gravity", manifest.imu.fields?.gravity, false);
                sb.AppendLine(Indent(2) + "}");
                sb.AppendLine(Indent(1) + "},");
            }

            AppendProperty(sb, "fps", manifest.fps, 1, true);
            AppendProperty(sb, "scene_id", manifest.scene_id, 1, true);
            AppendProperty(sb, "n_frames", manifest.n_frames, 1, manifest.scanned_mesh != null);

            if (manifest.scanned_mesh != null)
            {
                sb.AppendLine(Indent(1) + "\"scanned_mesh\": {");
                AppendProperty(sb, "path", manifest.scanned_mesh.path, 2, true);
                AppendProperty(sb, "format", manifest.scanned_mesh.format, 2, true);
                AppendProperty(sb, "units", manifest.scanned_mesh.units, 2, true);
                AppendProperty(sb, "coordinate_frame", manifest.scanned_mesh.coordinate_frame, 2, true);
                AppendProperty(sb, "note", manifest.scanned_mesh.note, 2, false);
                sb.AppendLine(Indent(1) + "}");
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        public static string SerializeFrameMetadata(FrameMetadata metadata)
        {
            var sb = new StringBuilder(2048);
            sb.AppendLine("{");
            AppendProperty(sb, "timestamp", metadata.timestamp, 1, true);
            AppendMatrixProperty(sb, "pose", metadata.pose, 1, true);
            AppendMatrixProperty(sb, "pose_raw", metadata.pose_raw, 1, true);
            AppendMatrixProperty(sb, "intrinsic", metadata.intrinsic, 1, true);
            sb.AppendLine(Indent(1) + "\"imu\": {");
            AppendDoubleArrayProperty(sb, "rotate_rate", metadata.imu?.rotate_rate, 2, true);
            AppendDoubleArrayProperty(sb, "acceleration", metadata.imu?.acceleration, 2, true);
            AppendDoubleArrayProperty(sb, "magnet", metadata.imu?.magnet, 2, true);
            AppendDoubleArrayProperty(sb, "attitude", metadata.imu?.attitude, 2, true);
            AppendDoubleArrayProperty(sb, "gravity", metadata.imu?.gravity, 2, false);
            sb.AppendLine(Indent(1) + "}");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void AppendPoseSchema(StringBuilder sb, string propertyName, PoseSchemaMetadata metadata, bool trailingComma)
        {
            sb.AppendLine(Indent(1) + $"\"{propertyName}\": {{");
            AppendProperty(sb, "convention", metadata?.convention, 2, true);
            AppendProperty(sb, "scale", metadata?.scale, 2, true);
            AppendProperty(sb, "layout", metadata?.layout, 2, metadata != null && !string.IsNullOrEmpty(metadata.note));
            if (metadata != null && !string.IsNullOrEmpty(metadata.note))
                AppendProperty(sb, "note", metadata.note, 2, false);
            sb.AppendLine(Indent(1) + "}" + (trailingComma ? "," : ""));
        }

        private static void AppendVectorField(StringBuilder sb, string propertyName, VectorFieldMetadata metadata, bool trailingComma)
        {
            sb.AppendLine(Indent(3) + $"\"{propertyName}\": {{");
            AppendProperty(sb, "units", metadata?.units, 4, true);
            AppendIntArrayProperty(sb, "shape", metadata?.shape, 4, true);
            AppendStringArrayProperty(sb, "axes", metadata?.axes, 4, metadata != null && !string.IsNullOrEmpty(metadata.note));
            if (metadata != null && !string.IsNullOrEmpty(metadata.note))
                AppendProperty(sb, "note", metadata.note, 4, false);
            sb.AppendLine(Indent(3) + "}" + (trailingComma ? "," : ""));
        }

        private static void AppendProperty(StringBuilder sb, string name, string value, int indentLevel, bool trailingComma)
        {
            sb.Append(Indent(indentLevel)).Append('"').Append(name).Append("\": ");
            AppendQuoted(sb, value ?? string.Empty);
            if (trailingComma)
                sb.Append(',');
            sb.AppendLine();
        }

        private static void AppendProperty(StringBuilder sb, string name, int value, int indentLevel, bool trailingComma)
        {
            sb.Append(Indent(indentLevel)).Append('"').Append(name).Append("\": ").Append(value);
            if (trailingComma)
                sb.Append(',');
            sb.AppendLine();
        }

        private static void AppendProperty(StringBuilder sb, string name, double value, int indentLevel, bool trailingComma)
        {
            sb.Append(Indent(indentLevel)).Append('"').Append(name).Append("\": ").Append(FormatNumber(value));
            if (trailingComma)
                sb.Append(',');
            sb.AppendLine();
        }

        private static void AppendDoubleArrayProperty(StringBuilder sb, string name, double[] values, int indentLevel, bool trailingComma)
        {
            sb.Append(Indent(indentLevel)).Append('"').Append(name).Append("\": ");
            AppendDoubleArray(sb, values);
            if (trailingComma)
                sb.Append(',');
            sb.AppendLine();
        }

        private static void AppendIntArrayProperty(StringBuilder sb, string name, int[] values, int indentLevel, bool trailingComma)
        {
            sb.Append(Indent(indentLevel)).Append('"').Append(name).Append("\": ");
            sb.Append('[');
            if (values != null)
            {
                for (var i = 0; i < values.Length; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(values[i]);
                }
            }
            sb.Append(']');
            if (trailingComma)
                sb.Append(',');
            sb.AppendLine();
        }

        private static void AppendStringArrayProperty(StringBuilder sb, string name, string[] values, int indentLevel, bool trailingComma)
        {
            sb.Append(Indent(indentLevel)).Append('"').Append(name).Append("\": ");
            sb.Append('[');
            if (values != null)
            {
                for (var i = 0; i < values.Length; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    AppendQuoted(sb, values[i] ?? string.Empty);
                }
            }
            sb.Append(']');
            if (trailingComma)
                sb.Append(',');
            sb.AppendLine();
        }

        private static void AppendMatrixProperty(StringBuilder sb, string name, double[][] matrix, int indentLevel, bool trailingComma)
        {
            sb.Append(Indent(indentLevel)).Append('"').Append(name).Append("\": ");
            AppendMatrix(sb, matrix);
            if (trailingComma)
                sb.Append(',');
            sb.AppendLine();
        }

        private static void AppendMatrix(StringBuilder sb, double[][] matrix)
        {
            sb.Append("[\n");
            if (matrix != null)
            {
                for (var row = 0; row < matrix.Length; row++)
                {
                    sb.Append(Indent(2)).Append('[');
                    var values = matrix[row];
                    if (values != null)
                    {
                        for (var col = 0; col < values.Length; col++)
                        {
                            if (col > 0)
                                sb.Append(", ");
                            sb.Append(FormatNumber(values[col]));
                        }
                    }
                    sb.Append(']');
                    if (row < matrix.Length - 1)
                        sb.Append(',');
                    sb.Append('\n');
                }
            }
            sb.Append(Indent(1)).Append(']');
        }

        private static void AppendDoubleArray(StringBuilder sb, double[] values)
        {
            sb.Append('[');
            if (values != null)
            {
                for (var i = 0; i < values.Length; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(FormatNumber(values[i]));
                }
            }
            sb.Append(']');
        }

        private static void AppendQuoted(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }

        private static string Indent(int level)
        {
            return new string(' ', level * 2);
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("0.################", CultureInfo.InvariantCulture);
        }
    }
}

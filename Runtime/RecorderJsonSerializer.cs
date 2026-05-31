using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SensorFlex.Recorder
{
    // Builds session.json for the SFZ 1.0 format.
    // Single-file: pass parts = null → no "parts" key is emitted.
    // Multi-part:  pass the partition plan → "parts" array is written into part 0's JSON.
    internal static class SfzSerializer
    {
        public static byte[] BuildSessionJson(
            SfzSessionMetadata meta,
            List<SfzFrameRecord> frames,
            SfzPartPlan[] parts)
        {
            var sb = new StringBuilder(512 + frames.Count * 160);
            sb.Append("{\n");

            // ── Top-level scalars ────────────────────────────────────────────
            AppendStr(sb, "version",        "1.0",                1, true);
            AppendStr(sb, "session_id",     meta.SessionId,       1, true);
            AppendStr(sb, "start_time_utc", meta.StartTimeUtc,    1, true);

            // ── device ───────────────────────────────────────────────────────
            sb.Append(I(1)).Append("\"device\": {\n");
            AppendStr(sb, "model",        meta.DeviceModel,   2, true);
            AppendStr(sb, "os",           meta.DeviceOs,      2, true);
            AppendStr(sb, "ar_framework", meta.ArFramework,   2, false);
            sb.Append(I(1)).Append("},\n");

            // ── parts (multi-part only) ──────────────────────────────────────
            if (parts != null && parts.Length > 0)
            {
                sb.Append(I(1)).Append("\"parts\": [\n");
                for (int p = 0; p < parts.Length; p++)
                {
                    var part = parts[p];
                    sb.Append(I(2)).Append("{\n");
                    AppendStr(sb, "file", part.FileName, 3, true);
                    sb.Append(I(3)).Append("\"contents\": [\n");

                    bool isFirstPart = p == 0;
                    bool isLastContent = part.FrameEnd <= part.FrameStart;

                    if (isFirstPart)
                    {
                        // Part 0 holds session.json
                        sb.Append(I(4)).Append("{ \"type\": \"session\" }");
                        if (part.FrameEnd > part.FrameStart) sb.Append(',');
                        sb.Append('\n');
                    }

                    if (part.FrameEnd > part.FrameStart)
                    {
                        sb.Append(I(4))
                          .Append("{ \"type\": \"frames\", \"frame_range\": [")
                          .Append(part.FrameStart)
                          .Append(", ")
                          .Append(part.FrameEnd)
                          .Append("] }\n");
                    }

                    sb.Append(I(3)).Append("]\n");
                    sb.Append(I(2)).Append('}');
                    if (p < parts.Length - 1) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(I(1)).Append("],\n");
            }

            // ── tracks ───────────────────────────────────────────────────────
            sb.Append(I(1)).Append("\"tracks\": {\n");
            sb.Append(I(2)).Append("\"frames\": {\n");

            // metadata
            sb.Append(I(3)).Append("\"metadata\": {\n");
            AppendInt(sb, "fps", meta.Fps, 4, true);
            sb.Append(I(4)).Append("\"channels\": {\n");

            bool rgbTrailing = meta.HasDepth;
            if (meta.HasRgb)
            {
                sb.Append(I(5)).Append("\"rgb\": {\n");
                AppendInt(sb, "width",  meta.RgbWidth,  6, true);
                AppendInt(sb, "height", meta.RgbHeight, 6, true);
                AppendStr(sb, "format", "jpeg",         6, false);
                sb.Append(I(5)).Append('}');
                if (rgbTrailing) sb.Append(',');
                sb.Append('\n');
            }

            if (meta.HasDepth)
            {
                sb.Append(I(5)).Append("\"depth\": {\n");
                AppendInt(sb, "width",  meta.DepthWidth,  6, true);
                AppendInt(sb, "height", meta.DepthHeight, 6, true);
                AppendStr(sb, "format", "raw_float32_le", 6, true);
                AppendStr(sb, "units",  "meters",         6, true);
                AppendStr(sb, "sensor", meta.DepthSensor ?? "arcore_environment_depth", 6, true);
                AppendDbl(sb, "invalid_value", 0.0,       6, false);
                sb.Append(I(5)).Append("}\n");
            }

            sb.Append(I(4)).Append("}\n");   // channels
            sb.Append(I(3)).Append("},\n");  // metadata

            // data array
            sb.Append(I(3)).Append("\"data\": [\n");
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                AppendFrameRecord(sb, f, 4);
                if (i < frames.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append(I(3)).Append("]\n");   // data

            sb.Append(I(2)).Append("}\n");   // frames
            sb.Append(I(1)).Append("}\n");   // tracks
            sb.Append("}\n");                // root

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        // ── Frame record ─────────────────────────────────────────────────────

        static void AppendFrameRecord(StringBuilder sb, SfzFrameRecord f, int indent)
        {
            sb.Append(I(indent)).Append('{');
            sb.Append("\"timestamp_ns\":").Append(f.TimestampNs).Append(',');

            // camera
            sb.Append("\"camera\":{");
            sb.Append("\"pose\":{");
            sb.Append("\"position\":[");
            sb.Append(F(f.Position.x)).Append(',')
              .Append(F(f.Position.y)).Append(',')
              .Append(F(f.Position.z));
            sb.Append("],\"rotation\":[");
            sb.Append(F(f.Rotation.x)).Append(',')
              .Append(F(f.Rotation.y)).Append(',')
              .Append(F(f.Rotation.z)).Append(',')
              .Append(F(f.Rotation.w));
            sb.Append("]}");

            if (f.HasIntrinsics)
            {
                sb.Append(",\"intrinsics\":{");
                sb.Append("\"fx\":").Append(F(f.Fx)).Append(',');
                sb.Append("\"fy\":").Append(F(f.Fy)).Append(',');
                sb.Append("\"cx\":").Append(F(f.Cx)).Append(',');
                sb.Append("\"cy\":").Append(F(f.Cy));
                sb.Append('}');
            }

            sb.Append('}');  // camera

            if (f.HasColor)
            {
                string file = "rgb/" + f.FrameIndex.ToString("D6") + ".jpg";
                sb.Append(",\"rgb\":{\"file\":").Append('"').Append(file).Append("\"}");
            }

            if (f.HasDepth)
            {
                string file = "depth/" + f.FrameIndex.ToString("D6") + ".bin";
                sb.Append(",\"depth\":{\"file\":").Append('"').Append(file).Append("\"}");
            }

            sb.Append('}');
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        static void AppendStr(StringBuilder sb, string key, string value, int indent, bool comma)
        {
            sb.Append(I(indent)).Append('"').Append(key).Append("\": \"");
            AppendEscaped(sb, value ?? string.Empty);
            sb.Append('"');
            if (comma) sb.Append(',');
            sb.Append('\n');
        }

        static void AppendInt(StringBuilder sb, string key, int value, int indent, bool comma)
        {
            sb.Append(I(indent)).Append('"').Append(key).Append("\": ").Append(value);
            if (comma) sb.Append(',');
            sb.Append('\n');
        }

        static void AppendDbl(StringBuilder sb, string key, double value, int indent, bool comma)
        {
            sb.Append(I(indent)).Append('"').Append(key).Append("\": ")
              .Append(value.ToString("0.################", CultureInfo.InvariantCulture));
            if (comma) sb.Append(',');
            sb.Append('\n');
        }

        static void AppendEscaped(StringBuilder sb, string s)
        {
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:   sb.Append(c);      break;
                }
            }
        }

        // 9 significant digits preserves float32 values exactly on round-trip.
        static string F(float v) => v.ToString("G9", CultureInfo.InvariantCulture);

        static string I(int level) => level switch
        {
            1 => "  ",
            2 => "    ",
            3 => "      ",
            4 => "        ",
            5 => "          ",
            6 => "            ",
            _ => new string(' ', level * 2)
        };
    }
}

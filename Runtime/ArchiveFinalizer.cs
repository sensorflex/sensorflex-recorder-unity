using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using SysCompressionLevel = System.IO.Compression.CompressionLevel;
using ZipArchive          = System.IO.Compression.ZipArchive;
using ZipArchiveMode      = System.IO.Compression.ZipArchiveMode;

namespace SensorFlex.Recorder
{
    // Packages a temp capture folder into one or more SFZ 1.0 archives.
    //
    // Single-file mode  (maxPartSizeBytes <= 0):
    //   {outputDir}/{sessionId}.sfz
    //
    // Multi-part mode   (maxPartSizeBytes >  0):
    //   {outputDir}/{sessionId}-00000-of-NNNNN.sfz  ...
    //   session.json in part 0 carries a "parts" manifest.
    //
    // Temp folder layout produced by CaptureFolderWriter:
    //   rgb.stream   — [int32 size][size bytes JPEG] per frame, in order
    //   depth.stream — [int32 size][size bytes float32] per frame, in order
    internal static class ArchiveFinalizer
    {
        // (frameByteOffset, dataSize) into a binary stream for one frame.
        struct FrameInfo { public long Offset; public int Size; }

        public static Task<string[]> FinalizeAsync(
            string tempDir,
            string outputDir,
            SfzSessionMetadata meta,
            List<SfzFrameRecord> frameRecords,
            CaptureFolderWriter writer,
            long maxPartSizeBytes)
        {
            return Task.Run(() => Finalize(tempDir, outputDir, meta, frameRecords, writer, maxPartSizeBytes));
        }

        // ── Core (background thread) ───────────────────────────────────────

        static string[] Finalize(
            string tempDir,
            string outputDir,
            SfzSessionMetadata meta,
            List<SfzFrameRecord> frameRecords,
            CaptureFolderWriter writer,
            long maxPartSizeBytes)
        {
            // Wait for writer to finish flushing before reading the streams.
            writer?.WaitForFlush();

            if (!Directory.Exists(tempDir))
                throw new DirectoryNotFoundException($"[SF-Recorder] Temp dir not found: {tempDir}");

            Directory.CreateDirectory(outputDir);

            string rgbStreamPath   = Path.Combine(tempDir, "rgb.stream");
            string depthStreamPath = Path.Combine(tempDir, "depth.stream");

            var rgbInfo   = ScanStream(rgbStreamPath,   frameRecords.Count);
            var depthInfo = ScanStream(depthStreamPath, frameRecords.Count);

            var frameSizes = ComputeFrameSizes(frameRecords, rgbInfo, depthInfo);

            string[] outputPaths;

            if (maxPartSizeBytes <= 0)
            {
                byte[] sessionJsonBytes = SfzSerializer.BuildSessionJson(meta, frameRecords, null);
                string outPath = Path.Combine(outputDir, meta.SessionId + ".sfz");
                WritePart(outPath, sessionJsonBytes, frameRecords, 0, frameRecords.Count,
                          rgbStreamPath, depthStreamPath, rgbInfo, depthInfo);
                outputPaths = new[] { outPath };
                Debug.Log($"[SF-Recorder] SFZ written: {outPath} ({new FileInfo(outPath).Length / 1024} KB)");
            }
            else
            {
                byte[] jsonNoParts  = SfzSerializer.BuildSessionJson(meta, frameRecords, null);
                var    plan         = PlanPartition(frameRecords, frameSizes, jsonNoParts.Length, maxPartSizeBytes, meta.SessionId);
                byte[] sessionJsonBytes = SfzSerializer.BuildSessionJson(meta, frameRecords, plan);

                outputPaths = new string[plan.Length];
                for (int p = 0; p < plan.Length; p++)
                {
                    string outPath = Path.Combine(outputDir, plan[p].FileName);
                    WritePart(outPath,
                              p == 0 ? sessionJsonBytes : null,
                              frameRecords,
                              plan[p].FrameStart, plan[p].FrameEnd,
                              rgbStreamPath, depthStreamPath, rgbInfo, depthInfo);
                    outputPaths[p] = outPath;
                    Debug.Log($"[SF-Recorder] SFZ part {p + 1}/{plan.Length}: {outPath} ({new FileInfo(outPath).Length / 1024} KB)");
                }
            }

            TryDeleteDir(tempDir);
            return outputPaths;
        }

        // ── Stream scanning ────────────────────────────────────────────────

        // Scans a length-prefixed binary stream and returns (dataOffset, dataSize)
        // for each frame. dataOffset points past the 4-byte length prefix.
        static FrameInfo[] ScanStream(string path, int frameCount)
        {
            var info = new FrameInfo[frameCount];
            if (!File.Exists(path)) return info;

            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            for (int i = 0; i < frameCount && fs.Position < fs.Length; i++)
            {
                int  size   = br.ReadInt32();
                long offset = fs.Position; // data starts here (after the 4-byte length)
                info[i] = new FrameInfo { Offset = offset, Size = size };
                fs.Seek(size, SeekOrigin.Current);
            }
            return info;
        }

        static long[] ComputeFrameSizes(List<SfzFrameRecord> records, FrameInfo[] rgb, FrameInfo[] depth)
        {
            var sizes = new long[records.Count];
            for (int i = 0; i < records.Count; i++)
                sizes[i] = rgb[i].Size + depth[i].Size;
            return sizes;
        }

        // ── Partition planning ─────────────────────────────────────────────

        static SfzPartPlan[] PlanPartition(
            List<SfzFrameRecord> frameRecords,
            long[] frameSizes,
            long jsonSize,
            long maxPartSizeBytes,
            string sessionId)
        {
            var  plans       = new List<SfzPartPlan>();
            long currentSize = jsonSize;
            int  partStart   = 0;

            for (int i = 0; i < frameSizes.Length; i++)
            {
                long fSize = frameSizes[i];
                if (i > partStart && currentSize + fSize > maxPartSizeBytes)
                {
                    plans.Add(new SfzPartPlan { FrameStart = partStart, FrameEnd = i });
                    currentSize = fSize;
                    partStart   = i;
                }
                else
                {
                    currentSize += fSize;
                }
            }
            plans.Add(new SfzPartPlan { FrameStart = partStart, FrameEnd = frameRecords.Count });

            int total = plans.Count;
            for (int p = 0; p < total; p++)
            {
                var plan = plans[p];
                plan.FileName = $"{sessionId}-{p:D5}-of-{total:D5}.sfz";
                plans[p] = plan;
            }
            return plans.ToArray();
        }

        // ── Zip writing ────────────────────────────────────────────────────

        static void WritePart(
            string outPath,
            byte[] sessionJsonBytes,
            List<SfzFrameRecord> frameRecords,
            int frameStart, int frameEnd,
            string rgbStreamPath, string depthStreamPath,
            FrameInfo[] rgbInfo, FrameInfo[] depthInfo)
        {
            if (File.Exists(outPath)) File.Delete(outPath);

            using var zipStream = new FileStream(outPath, FileMode.Create, FileAccess.Write);
            using var archive   = new ZipArchive(zipStream, ZipArchiveMode.Create);

            if (sessionJsonBytes != null)
            {
                var entry = archive.CreateEntry("session/session.json", SysCompressionLevel.Optimal);
                using var es = entry.Open();
                es.Write(sessionJsonBytes, 0, sessionJsonBytes.Length);
            }

            using var rgbStream   = File.Exists(rgbStreamPath)   ? File.OpenRead(rgbStreamPath)   : null;
            using var depthStream = File.Exists(depthStreamPath) ? File.OpenRead(depthStreamPath) : null;

            var copyBuf = new byte[65536];

            for (int i = frameStart; i < frameEnd; i++)
            {
                var    rec  = frameRecords[i];
                string stem = rec.FrameIndex.ToString("D6");

                if (rec.HasColor && rgbStream != null && rgbInfo[i].Size > 0)
                {
                    var entry = archive.CreateEntry("session/rgb/" + stem + ".jpg",
                                                    SysCompressionLevel.NoCompression);
                    using var es = entry.Open();
                    rgbStream.Seek(rgbInfo[i].Offset, SeekOrigin.Begin);
                    CopyBytes(rgbStream, es, rgbInfo[i].Size, copyBuf);
                }

                if (rec.HasDepth && depthStream != null && depthInfo[i].Size > 0)
                {
                    var entry = archive.CreateEntry("session/depth/" + stem + ".bin",
                                                    SysCompressionLevel.Optimal);
                    using var es = entry.Open();
                    depthStream.Seek(depthInfo[i].Offset, SeekOrigin.Begin);
                    CopyBytes(depthStream, es, depthInfo[i].Size, copyBuf);
                }
            }
        }

        static void CopyBytes(Stream src, Stream dst, int count, byte[] buf)
        {
            int remaining = count;
            while (remaining > 0)
            {
                int read = src.Read(buf, 0, Math.Min(remaining, buf.Length));
                if (read == 0) break;
                dst.Write(buf, 0, read);
                remaining -= read;
            }
        }

        // ── Cleanup ────────────────────────────────────────────────────────

        static void TryDeleteDir(string path)
        {
            try   { Directory.Delete(path, true); }
            catch (Exception e) { Debug.LogWarning($"[SF-Recorder] Could not delete temp dir '{path}': {e.Message}"); }
        }
    }
}

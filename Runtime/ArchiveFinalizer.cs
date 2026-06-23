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
    // Packages a temp capture folder into one or more SFZ archives.
    //
    // On the non-iOS (legacy) path the temp folder contains:
    //   rgb.stream   — [int32 size][size bytes JPEG] per frame, in order
    //   depth.stream — [int32 size][size bytes float32] per frame, in order
    //   → packed as session/rgb/NNNNNN.jpg and session/depth/NNNNNN.bin
    //
    // On the iOS native path the temp folder contains:
    //   rgb.mp4   — HEVC YCbCr420 video
    //   depth.mp4 — HEVC OneComponent16Half (float16 metres) video
    //   → packed as session/rgb.mp4 and session/depth.mp4 (both STORED, already compressed)
    //   → per-frame records reference {"file":"rgb.mp4","frame_index":N} etc.
    //
    // Single-file mode  (maxPartSizeBytes <= 0):
    //   {outputDir}/{sessionId}.sfz
    //
    // Multi-part mode   (maxPartSizeBytes >  0):
    //   {outputDir}/{sessionId}-00000-of-NNNNN.sfz  ...
    //   On the native path the MP4 files land in part 0 (may exceed the size limit).
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
            // Wait for all data to be fully written (native encoder MP4s or legacy streams).
            writer?.WaitForFlush();

            if (!Directory.Exists(tempDir))
                throw new DirectoryNotFoundException($"[SF-Recorder] Temp dir not found: {tempDir}");

            Directory.CreateDirectory(outputDir);

            // Detect encoding path by checking for MP4 files.
            string rgbMp4Path   = writer?.RgbMp4Path   ?? Path.Combine(tempDir, "rgb.mp4");
            string depthMp4Path = writer?.DepthMp4Path ?? Path.Combine(tempDir, "depth.mp4");
            bool   isNativePath = File.Exists(rgbMp4Path) || File.Exists(depthMp4Path);

            string[] outputPaths;

            if (maxPartSizeBytes <= 0)
            {
                // ── Single-file ────────────────────────────────────────────
                byte[] sessionJsonBytes = SfzSerializer.BuildSessionJson(meta, frameRecords, null, isNativePath);
                string outPath = Path.Combine(outputDir, meta.SessionId + ".sfz");

                WritePart(outPath, sessionJsonBytes, frameRecords, 0, frameRecords.Count,
                          isNativePath, rgbMp4Path, depthMp4Path,
                          null, null, null, null);

                outputPaths = new[] { outPath };
                Debug.Log($"[SF-Recorder] SFZ written: {outPath} ({new FileInfo(outPath).Length / 1024} KB)");
            }
            else
            {
                // ── Multi-part ─────────────────────────────────────────────
                long[] frameSizes;

                if (isNativePath)
                {
                    // Estimate per-frame size from total video file sizes.
                    long totalVideoBytes =
                        (File.Exists(rgbMp4Path)   ? new FileInfo(rgbMp4Path).Length   : 0) +
                        (File.Exists(depthMp4Path) ? new FileInfo(depthMp4Path).Length : 0);
                    long perFrame = frameRecords.Count > 0 ? totalVideoBytes / frameRecords.Count : 0;
                    frameSizes = new long[frameRecords.Count];
                    for (int i = 0; i < frameSizes.Length; i++) frameSizes[i] = perFrame;
                }
                else
                {
                    string rgbStreamPath   = writer?.RgbStreamPath   ?? Path.Combine(tempDir, "rgb.stream");
                    string depthStreamPath = writer?.DepthStreamPath ?? Path.Combine(tempDir, "depth.stream");
                    var rgbInfo   = ScanStream(rgbStreamPath,   frameRecords.Count);
                    var depthInfo = ScanStream(depthStreamPath, frameRecords.Count);
                    frameSizes = ComputeFrameSizes(frameRecords, rgbInfo, depthInfo);

                    byte[] sessionJsonBytes = SfzSerializer.BuildSessionJson(meta, frameRecords, null, isNativePath);
                    var    plan             = PlanPartition(frameRecords, frameSizes, sessionJsonBytes.Length, maxPartSizeBytes, meta.SessionId);
                    sessionJsonBytes        = SfzSerializer.BuildSessionJson(meta, frameRecords, plan, isNativePath);

                    outputPaths = new string[plan.Length];
                    for (int p = 0; p < plan.Length; p++)
                    {
                        string outPath = Path.Combine(outputDir, plan[p].FileName);
                        WritePart(outPath,
                                  p == 0 ? sessionJsonBytes : null,
                                  frameRecords,
                                  plan[p].FrameStart, plan[p].FrameEnd,
                                  isNativePath, rgbMp4Path, depthMp4Path,
                                  rgbStreamPath, depthStreamPath, rgbInfo, depthInfo);
                        outputPaths[p] = outPath;
                        Debug.Log($"[SF-Recorder] SFZ part {p + 1}/{plan.Length}: {outPath} ({new FileInfo(outPath).Length / 1024} KB)");
                    }

                    TryDeleteDir(tempDir);
                    return outputPaths;
                }

                // Native path multi-part: plan partition, videos land in part 0.
                byte[] jsonBytes = SfzSerializer.BuildSessionJson(meta, frameRecords, null, isNativePath);
                var    nativePlan = PlanPartition(frameRecords, frameSizes, jsonBytes.Length, maxPartSizeBytes, meta.SessionId);
                jsonBytes = SfzSerializer.BuildSessionJson(meta, frameRecords, nativePlan, isNativePath);

                outputPaths = new string[nativePlan.Length];
                for (int p = 0; p < nativePlan.Length; p++)
                {
                    string outPath = Path.Combine(outputDir, nativePlan[p].FileName);
                    WritePart(outPath,
                              p == 0 ? jsonBytes : null,
                              frameRecords,
                              nativePlan[p].FrameStart, nativePlan[p].FrameEnd,
                              isNativePath, rgbMp4Path, depthMp4Path,
                              null, null, null, null,
                              isFirstPart: p == 0);
                    outputPaths[p] = outPath;
                    Debug.Log($"[SF-Recorder] SFZ part {p + 1}/{nativePlan.Length}: {outPath} ({new FileInfo(outPath).Length / 1024} KB)");
                }
            }

            TryDeleteDir(tempDir);
            return outputPaths;
        }

        // ── Stream scanning (legacy path) ──────────────────────────────────

        static FrameInfo[] ScanStream(string path, int frameCount)
        {
            var info = new FrameInfo[frameCount];
            if (!File.Exists(path)) return info;

            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            for (int i = 0; i < frameCount && fs.Position < fs.Length; i++)
            {
                int  size   = br.ReadInt32();
                long offset = fs.Position;
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
            bool isNativePath,
            string rgbMp4Path, string depthMp4Path,
            string rgbStreamPath, string depthStreamPath,
            FrameInfo[] rgbInfo, FrameInfo[] depthInfo,
            bool isFirstPart = true)
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

            if (isNativePath)
            {
                // MP4 files go into the first part only (they cover all frames).
                if (isFirstPart)
                {
                    CopyFileIntoZip(archive, rgbMp4Path,   "session/rgb.mp4",   SysCompressionLevel.NoCompression);
                    CopyFileIntoZip(archive, depthMp4Path, "session/depth.mp4", SysCompressionLevel.NoCompression);
                }
                // No per-frame entries — frame references are baked into session.json records.
            }
            else
            {
                // Legacy: per-frame JPEG + float32 depth bin from the stream files.
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
        }

        static void CopyFileIntoZip(ZipArchive archive, string srcPath, string entryName,
                                     SysCompressionLevel compression)
        {
            if (!File.Exists(srcPath)) return;
            var entry = archive.CreateEntry(entryName, compression);
            using var es  = entry.Open();
            using var src = File.OpenRead(srcPath);
            src.CopyTo(es);
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

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
    //   session.json has no "parts" key.
    //
    // Multi-part mode   (maxPartSizeBytes >  0):
    //   {outputDir}/{sessionId}-00000-of-NNNNN.sfz
    //   {outputDir}/{sessionId}-00001-of-NNNNN.sfz
    //   ...
    //   session.json in part 0 carries a "parts" manifest.
    //   Part naming follows the SFZ spec: -DDDDD-of-DDDDD suffix.
    //
    // All frame files inside the archive live under the "session/" prefix as
    // required by the player's SfzFileBackend.
    internal static class ArchiveFinalizer
    {
        public static Task<string[]> FinalizeAsync(
            string tempDir,
            string outputDir,
            SfzSessionMetadata meta,
            List<SfzFrameRecord> frameRecords,
            long maxPartSizeBytes)
        {
            // Capture everything the background task needs; avoid captures of large
            // managed objects that could keep the main thread live longer than needed.
            return Task.Run(() => Finalize(tempDir, outputDir, meta, frameRecords, maxPartSizeBytes));
        }

        // ── Core (background thread) ───────────────────────────────────────

        static string[] Finalize(
            string tempDir,
            string outputDir,
            SfzSessionMetadata meta,
            List<SfzFrameRecord> frameRecords,
            long maxPartSizeBytes)
        {
            if (!Directory.Exists(tempDir))
                throw new DirectoryNotFoundException($"[SF-Recorder] Temp dir not found: {tempDir}");

            Directory.CreateDirectory(outputDir);

            // Collect on-disk file sizes for the partition planner.
            // We only need sizes, not the bytes themselves; the zip writer streams
            // each file directly from disk so we don't hold large buffers in RAM.
            var frameSizes = CollectFrameSizes(tempDir, frameRecords);

            string[] outputPaths;

            if (maxPartSizeBytes <= 0)
            {
                // ── Single file ────────────────────────────────────────────
                byte[] sessionJsonBytes = SfzSerializer.BuildSessionJson(meta, frameRecords, null);
                string outPath = Path.Combine(outputDir, meta.SessionId + ".sfz");
                WritePart(outPath, sessionJsonBytes, frameRecords, 0, frameRecords.Count, tempDir);
                outputPaths = new[] { outPath };
                Debug.Log($"[SF-Recorder] SFZ written: {outPath} ({new FileInfo(outPath).Length / 1024} KB)");
            }
            else
            {
                // ── Multi-part ─────────────────────────────────────────────
                //
                // Step 1: compute session.json without parts → get its byte size.
                byte[] jsonNoParts = SfzSerializer.BuildSessionJson(meta, frameRecords, null);
                long   jsonSize    = jsonNoParts.Length;

                // Step 2: plan partition.
                var plan = PlanPartition(frameRecords, frameSizes, jsonSize, maxPartSizeBytes, meta.SessionId);

                // Step 3: rebuild session.json with the parts manifest (part 0 only).
                byte[] sessionJsonBytes = SfzSerializer.BuildSessionJson(meta, frameRecords, plan);

                // Step 4: write each part.
                outputPaths = new string[plan.Length];
                for (int p = 0; p < plan.Length; p++)
                {
                    string outPath = Path.Combine(outputDir, plan[p].FileName);
                    bool   isPart0 = p == 0;
                    WritePart(
                        outPath,
                        isPart0 ? sessionJsonBytes : null,
                        frameRecords,
                        plan[p].FrameStart,
                        plan[p].FrameEnd,
                        tempDir);
                    outputPaths[p] = outPath;
                    Debug.Log($"[SF-Recorder] SFZ part {p + 1}/{plan.Length}: {outPath} ({new FileInfo(outPath).Length / 1024} KB)");
                }
            }

            // Clean up temp folder.
            TryDeleteDir(tempDir);

            return outputPaths;
        }

        // ── Partition planning ─────────────────────────────────────────────

        // Returns a minimal array of SfzPartPlan; always at least one element.
        static SfzPartPlan[] PlanPartition(
            List<SfzFrameRecord> frameRecords,
            long[] frameSizes,
            long jsonSize,
            long maxPartSizeBytes,
            string sessionId)
        {
            var plans = new List<SfzPartPlan>();

            // Part 0 carries session.json; account for its size upfront.
            long currentSize  = jsonSize;
            int  partStart    = 0;

            for (int i = 0; i < frameSizes.Length; i++)
            {
                long fSize = frameSizes[i];

                // If adding this frame would overflow the part (and the part already
                // has at least one frame), close the current part and open a new one.
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

            // Close the final (or only) part.
            plans.Add(new SfzPartPlan
            {
                FrameStart = partStart,
                FrameEnd   = frameRecords.Count
            });

            // Assign filenames now that we know total part count.
            int total = plans.Count;
            for (int p = 0; p < total; p++)
            {
                var plan = plans[p];
                plan.FileName = $"{sessionId}-{p:D5}-of-{total:D5}.sfz";
                plans[p] = plan;
            }

            return plans.ToArray();
        }

        // Estimates the raw (uncompressed) byte cost for each frame record.
        // RGB files are stored with ZIP_STORED so zip size ≈ file size.
        // Depth files compress well, but we use raw size as an upper bound.
        static long[] CollectFrameSizes(string tempDir, List<SfzFrameRecord> frameRecords)
        {
            string rgbDir   = Path.Combine(tempDir, "rgb");
            string depthDir = Path.Combine(tempDir, "depth");

            var sizes = new long[frameRecords.Count];

            for (int i = 0; i < frameRecords.Count; i++)
            {
                var rec  = frameRecords[i];
                string stem = rec.FrameIndex.ToString("D6");

                if (rec.HasColor)
                {
                    string path = Path.Combine(rgbDir, stem + ".jpg");
                    if (File.Exists(path)) sizes[i] += new FileInfo(path).Length;
                }
                if (rec.HasDepth)
                {
                    string path = Path.Combine(depthDir, stem + ".bin");
                    if (File.Exists(path)) sizes[i] += new FileInfo(path).Length;
                }
            }

            return sizes;
        }

        // ── Zip writing ────────────────────────────────────────────────────

        // Writes one .sfz part file.
        //   sessionJsonBytes — non-null only for part 0 (single-file or first part).
        //   frameStart/frameEnd — slice of frameRecords whose binary files go here.
        static void WritePart(
            string outPath,
            byte[] sessionJsonBytes,
            List<SfzFrameRecord> frameRecords,
            int frameStart,
            int frameEnd,
            string tempDir)
        {
            string rgbDir   = Path.Combine(tempDir, "rgb");
            string depthDir = Path.Combine(tempDir, "depth");

            if (File.Exists(outPath))
                File.Delete(outPath);

            using var zipStream = new FileStream(outPath, FileMode.Create, FileAccess.Write);
            using var archive   = new ZipArchive(zipStream, ZipArchiveMode.Create);

            // session.json (Deflate — compresses well)
            if (sessionJsonBytes != null)
            {
                var entry = archive.CreateEntry("session/session.json", SysCompressionLevel.Optimal);
                using var es = entry.Open();
                es.Write(sessionJsonBytes, 0, sessionJsonBytes.Length);
            }

            // Frame binary files
            for (int i = frameStart; i < frameEnd; i++)
            {
                var rec  = frameRecords[i];
                string stem = rec.FrameIndex.ToString("D6");

                if (rec.HasColor)
                {
                    string src = Path.Combine(rgbDir, stem + ".jpg");
                    if (File.Exists(src))
                    {
                        // JPEGs are already compressed — use NoCompression (ZIP_STORED).
                        var entry = archive.CreateEntry("session/rgb/" + stem + ".jpg",
                                                        SysCompressionLevel.NoCompression);
                        using var es = entry.Open();
                        using var fs = File.OpenRead(src);
                        fs.CopyTo(es);
                    }
                }

                if (rec.HasDepth)
                {
                    string src = Path.Combine(depthDir, stem + ".bin");
                    if (File.Exists(src))
                    {
                        // Raw float32 depth compresses well under Deflate.
                        var entry = archive.CreateEntry("session/depth/" + stem + ".bin",
                                                        SysCompressionLevel.Optimal);
                        using var es = entry.Open();
                        using var fs = File.OpenRead(src);
                        fs.CopyTo(es);
                    }
                }
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

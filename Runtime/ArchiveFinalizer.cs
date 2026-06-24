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
    // Packages a completed capture temp folder into a single SFZ archive (SFZ v2.0).
    //
    // Expected files in tempDir:
    //   rgb.mp4          — HEVC YCbCr420 video (from SFRgbEncoder)
    //   depth.bin        — LZ4_RAW float32 depth blocks (from SFDepthLz4)
    //   depth_sizes.bin  — int32 per-frame compressed byte counts (from SFDepthLz4)
    //
    // Output zip entries (all under "session/"):
    //   session/session.json   — metadata (Deflate)
    //   session/frames.jsonl   — per-frame camera+depth records (Deflate)
    //   session/rgb.mp4        — video (Stored, already compressed)
    //   session/depth.bin      — depth blocks (Stored, already compressed)
    //
    // The temp folder is deleted after successful packaging.
    internal static class ArchiveFinalizer
    {
        public static Task<string[]> FinalizeAsync(
            string tempDir,
            string outputDir,
            SfzSessionMetadata meta,
            List<SfzFrameRecord> frameRecords)
        {
            return Task.Run(() => Finalize(tempDir, outputDir, meta, frameRecords));
        }

        // ── Core (background thread) ───────────────────────────────────────

        static string[] Finalize(
            string tempDir,
            string outputDir,
            SfzSessionMetadata meta,
            List<SfzFrameRecord> frameRecords)
        {
            // Wait for native encoders to flush (RGB HEVC + depth LZ4 queue drain).
            NativeVideoEncoder.WaitForBothFinished(timeoutMs: 60_000);

            if (!Directory.Exists(tempDir))
                throw new DirectoryNotFoundException($"[SF-Recorder] Temp dir not found: {tempDir}");

            Directory.CreateDirectory(outputDir);

            // ── Read depth compressed sizes ────────────────────────────────
            string depthSizesPath = Path.Combine(tempDir, "depth_sizes.bin");
            int[]  depthSizes     = ReadDepthSizes(depthSizesPath, frameRecords.Count);

            // ── Build text files ───────────────────────────────────────────
            byte[] sessionJsonBytes  = SfzSerializer.BuildSessionJson(meta);
            byte[] framesJsonlBytes  = SfzSerializer.BuildFramesJsonl(frameRecords, depthSizes);

            // ── Write SFZ archive ──────────────────────────────────────────
            string outPath  = Path.Combine(outputDir, meta.SessionId + ".sfz");
            if (File.Exists(outPath)) File.Delete(outPath);

            using (var zipStream = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            using (var archive   = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                WriteBytes(archive, "session/session.json",  sessionJsonBytes, SysCompressionLevel.Optimal);
                WriteBytes(archive, "session/frames.jsonl",  framesJsonlBytes, SysCompressionLevel.Optimal);
                CopyFile(archive, "session/rgb.mp4",   Path.Combine(tempDir, "rgb.mp4"),   SysCompressionLevel.NoCompression);
                CopyFile(archive, "session/depth.bin", Path.Combine(tempDir, "depth.bin"), SysCompressionLevel.NoCompression);
            }

            long kb = new FileInfo(outPath).Length / 1024;
            Debug.Log($"[SF-Recorder] SFZ written: {outPath} ({kb} KB) frames={frameRecords.Count}");

            TryDeleteDir(tempDir);
            return new[] { outPath };
        }

        // ── Helpers ────────────────────────────────────────────────────────

        // depth_sizes.bin is a flat array of int32 (little-endian), one per frame.
        // If the file doesn't exist (depth not recorded), returns an array of zeros.
        static int[] ReadDepthSizes(string path, int frameCount)
        {
            var sizes = new int[frameCount];
            if (!File.Exists(path)) return sizes;

            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            for (int i = 0; i < frameCount && fs.Position + 4 <= fs.Length; i++)
                sizes[i] = br.ReadInt32();

            return sizes;
        }

        static void WriteBytes(ZipArchive archive, string entryName, byte[] bytes,
                               SysCompressionLevel compression)
        {
            var entry = archive.CreateEntry(entryName, compression);
            using var es = entry.Open();
            es.Write(bytes, 0, bytes.Length);
        }

        static void CopyFile(ZipArchive archive, string entryName, string srcPath,
                              SysCompressionLevel compression)
        {
            if (!File.Exists(srcPath))
            {
                Debug.LogWarning($"[SF-Recorder] File not found, skipping zip entry: {srcPath}");
                return;
            }
            var entry = archive.CreateEntry(entryName, compression);
            using var es  = entry.Open();
            using var src = File.OpenRead(srcPath);
            src.CopyTo(es);
        }

        static void TryDeleteDir(string path)
        {
            try   { Directory.Delete(path, recursive: true); }
            catch (Exception e)
            {
                Debug.LogWarning($"[SF-Recorder] Could not delete temp dir '{path}': {e.Message}");
            }
        }
    }
}

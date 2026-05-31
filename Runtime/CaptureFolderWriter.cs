using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using UnityEngine;

namespace SensorFlex.Recorder
{
    // Writes rgb/NNNNNN.jpg and depth/NNNNNN.bin to disk on a background thread.
    // All frame metadata lives in-memory in CaptureCoordinator; this class only
    // handles binary file persistence.
    internal sealed class CaptureFolderWriter
    {
        public struct FrameWriteJob
        {
            public int    FrameIndex;
            public byte[] JpgData;       // null when color not captured
            public byte[] DepthF32Data;  // raw_float32_le meters; null when depth not captured
        }

        readonly string _rgbDir;
        readonly string _depthDir;
        readonly BlockingCollection<FrameWriteJob> _queue;

        Thread _workerThread;
        bool   _isRunning;

        // Frames further ahead than this from the playhead are held back to avoid
        // unbounded memory growth on the producer side.
        const int MaxQueueSize = 120;

        public CaptureFolderWriter(string sessionTempDir)
        {
            _rgbDir   = Path.Combine(sessionTempDir, "rgb");
            _depthDir = Path.Combine(sessionTempDir, "depth");

            Directory.CreateDirectory(sessionTempDir);
            Directory.CreateDirectory(_rgbDir);
            Directory.CreateDirectory(_depthDir);

            _queue = new BlockingCollection<FrameWriteJob>(MaxQueueSize);
        }

        public void Start()
        {
            _isRunning    = true;
            _workerThread = new Thread(WriteLoop)
            {
                IsBackground = true,
                Name         = "SF-Recorder-DiskWriter"
            };
            _workerThread.Start();
        }

        // Returns false when the queue is full — the caller should drop the frame
        // rather than blocking the main thread.
        public bool TryEnqueue(FrameWriteJob job)
        {
            if (!_isRunning || _queue.IsAddingCompleted)
                return false;
            return _queue.TryAdd(job);
        }

        // Stop accepting new jobs and wait for the background thread to flush all
        // pending writes before returning.
        public void Stop()
        {
            _isRunning = false;
            _queue.CompleteAdding();
            _workerThread?.Join(10_000);
            _queue.Dispose();
        }

        void WriteLoop()
        {
            while (true)
            {
                FrameWriteJob job;
                try
                {
                    if (!_queue.TryTake(out job, Timeout.Infinite))
                        break;
                }
                catch (InvalidOperationException)
                {
                    // Queue was completed and is now empty.
                    break;
                }

                string stem = job.FrameIndex.ToString("D6");

                try
                {
                    if (job.JpgData != null && job.JpgData.Length > 0)
                        File.WriteAllBytes(Path.Combine(_rgbDir, stem + ".jpg"), job.JpgData);

                    if (job.DepthF32Data != null && job.DepthF32Data.Length > 0)
                        File.WriteAllBytes(Path.Combine(_depthDir, stem + ".bin"), job.DepthF32Data);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[SF-Recorder] Disk write failed for frame {job.FrameIndex}: {e.Message}");
                }
            }
        }
    }
}

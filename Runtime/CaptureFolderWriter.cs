using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace SensorFlex.Recorder
{
    // Two encoder threads convert raw RGBA frames to JPEG in parallel.
    // A single writer thread drains encoded frames in frame-index order and appends
    // them to binary stream files, eliminating per-frame file-create overhead.
    //
    // Stream format (rgb.stream and depth.stream):
    //   [int32 dataSize][dataSize bytes payload]  repeated per frame in order.
    //   dataSize == 0 means no data for that frame.
    internal sealed class CaptureFolderWriter
    {
        public struct RawFrameJob
        {
            public int    FrameIndex;
            public byte[] RgbaData;     // raw RGBA32 pixels; null if JpgData is set
            public uint   RgbaWidth;
            public uint   RgbaHeight;
            public byte[] JpgData;      // pre-encoded JPEG (GPU texture fallback only)
            public byte[] DepthData;    // raw float32 LE metres; null when depth not captured
        }

        struct EncodedSlot
        {
            public int    FrameIndex;
            public byte[] JpgData;
            public byte[] DepthData;
        }

        // ── Output paths ───────────────────────────────────────────────────
        public string RgbStreamPath   { get; }
        public string DepthStreamPath { get; }

        // ── Threading ──────────────────────────────────────────────────────
        const int RawQueueCapacity = 16;

        BlockingCollection<RawFrameJob>        _rawQueue;
        ConcurrentDictionary<int, EncodedSlot> _encodedSlots;
        Thread   _encoderThread1, _encoderThread2, _writerThread;
        volatile bool _stopWriter;

        // Writer thread owns these exclusively after Start().
        BinaryWriter _rgbWriter;
        BinaryWriter _depthWriter;

        public CaptureFolderWriter(string tempDir)
        {
            Directory.CreateDirectory(tempDir);
            RgbStreamPath   = Path.Combine(tempDir, "rgb.stream");
            DepthStreamPath = Path.Combine(tempDir, "depth.stream");
        }

        public void Start()
        {
            _rawQueue     = new BlockingCollection<RawFrameJob>(RawQueueCapacity);
            _encodedSlots = new ConcurrentDictionary<int, EncodedSlot>();
            _stopWriter   = false;

            const int bufSize = 1 << 17; // 128 KB write buffer
            _rgbWriter   = new BinaryWriter(new FileStream(RgbStreamPath,   FileMode.Create, FileAccess.Write, FileShare.None, bufSize));
            _depthWriter = new BinaryWriter(new FileStream(DepthStreamPath, FileMode.Create, FileAccess.Write, FileShare.None, bufSize));

            _writerThread   = new Thread(WriterLoop)  { IsBackground = true, Name = "SF-Writer" };
            _encoderThread1 = new Thread(EncoderLoop) { IsBackground = true, Name = "SF-Encoder-1" };
            _encoderThread2 = new Thread(EncoderLoop) { IsBackground = true, Name = "SF-Encoder-2" };

            _writerThread.Start();
            _encoderThread1.Start();
            _encoderThread2.Start();
        }

        // Called on main thread. Returns false only if raw queue is full (capacity 16).
        // With two encoder threads at camera FPS this should never happen in practice.
        public bool TryEnqueue(RawFrameJob job) => _rawQueue?.TryAdd(job) ?? false;

        // Signal encoders that no more frames are coming. Returns immediately;
        // call WaitForFlush() on a background thread to wait for all writes to complete.
        public void CompleteAdding() => _rawQueue?.CompleteAdding();

        // Blocks until all frames are encoded and flushed to disk. Call off main thread.
        public void WaitForFlush(int timeoutMs = 30_000)
        {
            _encoderThread1?.Join(timeoutMs);
            _encoderThread2?.Join(timeoutMs);
            _stopWriter = true;
            _writerThread?.Join(timeoutMs);

            _rgbWriter?.Flush();
            _rgbWriter?.Dispose();
            _depthWriter?.Flush();
            _depthWriter?.Dispose();
            _rgbWriter   = null;
            _depthWriter = null;

            _rawQueue?.Dispose();
            _rawQueue = null;
        }

        // ── Encoder threads ────────────────────────────────────────────────

        void EncoderLoop()
        {
            try
            {
                foreach (var job in _rawQueue.GetConsumingEnumerable())
                {
                    byte[] jpg = job.JpgData;
                    if (jpg == null && job.RgbaData != null)
                        jpg = ImageConversion.EncodeArrayToJPG(
                            job.RgbaData, GraphicsFormat.R8G8B8A8_UNorm,
                            job.RgbaWidth, job.RgbaHeight, 0, 75);

                    _encodedSlots[job.FrameIndex] = new EncodedSlot
                    {
                        FrameIndex = job.FrameIndex,
                        JpgData    = jpg,
                        DepthData  = job.DepthData
                    };
                }
            }
            catch (Exception e) when (e is not ThreadAbortException)
            {
                Debug.LogError($"[SF-Recorder] Encoder thread error: {e.Message}");
            }
        }

        // ── Writer thread ──────────────────────────────────────────────────

        void WriterLoop()
        {
            int nextWrite = 0;

            while (true)
            {
                // Drain all consecutive ready frames from the reorder map.
                bool wrote = false;
                while (_encodedSlots.TryRemove(nextWrite, out var slot))
                {
                    WriteSlot(slot);
                    nextWrite++;
                    wrote = true;
                }

                if (_stopWriter && _encodedSlots.IsEmpty)
                    break;

                if (!wrote)
                    Thread.Sleep(1);
            }

            // Final drain — encoders are confirmed done when _stopWriter is set.
            while (_encodedSlots.TryRemove(nextWrite, out var slot))
            {
                WriteSlot(slot);
                nextWrite++;
            }
        }

        void WriteSlot(EncodedSlot slot)
        {
            try
            {
                if (slot.JpgData != null && slot.JpgData.Length > 0)
                {
                    _rgbWriter.Write(slot.JpgData.Length);
                    _rgbWriter.Write(slot.JpgData);
                }
                else
                {
                    _rgbWriter.Write(0);
                }

                if (slot.DepthData != null && slot.DepthData.Length > 0)
                {
                    _depthWriter.Write(slot.DepthData.Length);
                    _depthWriter.Write(slot.DepthData);
                }
                else
                {
                    _depthWriter.Write(0);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[SF-Recorder] Write failed for frame {slot.FrameIndex}: {e.Message}");
            }
        }
    }
}

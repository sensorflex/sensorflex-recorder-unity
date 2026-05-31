using System.IO;
using System.Threading;
using System.Collections.Concurrent;
using UnityEngine;

namespace SensorFlex.Recorder
{
    public class CaptureFolderWriter
    {
        public struct FrameWriteJob
        {
            public int frameIndex;
            public byte[] jpgData;
            public byte[] maskPngData;
            public byte[] depthData;
            public string metaJson;
        }

        private string _sessionFolder;
        private string _framesFolder;

        private Thread _workerThread;
        private BlockingCollection<FrameWriteJob> _writeQueue;
        private bool _isRunning;

        // Bounded queue to avoid unbounded memory growth
        private const int MAX_QUEUE_SIZE = 120;

        public CaptureFolderWriter(string sessionFolder)
        {
            _sessionFolder = sessionFolder;
            _framesFolder = Path.Combine(_sessionFolder, "frames");

            if (!Directory.Exists(_sessionFolder))
                Directory.CreateDirectory(_sessionFolder);

            if (!Directory.Exists(_framesFolder))
                Directory.CreateDirectory(_framesFolder);

            _writeQueue = new BlockingCollection<FrameWriteJob>(MAX_QUEUE_SIZE);
        }

        public void Start()
        {
            _isRunning = true;
            _workerThread = new Thread(WriteLoop);
            _workerThread.IsBackground = true;
            _workerThread.Name = "CaptureFolderWriterThread";
            _workerThread.Start();
        }

        public void Stop()
        {
            _isRunning = false;
            // Signal queue completion
            _writeQueue.CompleteAdding();
            
            if (_workerThread != null && _workerThread.IsAlive)
            {
                // Wait for the background thread to flush pending writes
                _workerThread.Join(5000); 
            }
            
            _writeQueue.Dispose();
        }

        public void WriteSessionManifest(RecorderSessionManifest manifest)
        {
            try 
            {
                string json = RecorderJsonSerializer.SerializeSessionManifest(manifest);
                string path = Path.Combine(_sessionFolder, "meta.json");
                File.WriteAllText(path, json);
            } 
            catch (System.Exception e)
            {
                Debug.LogError($"[CaptureFolderWriter] Failed to write session manifest: {e.Message}");
            }
        }

        public bool TryEnqueueFrame(FrameWriteJob job)
        {
            if (!_isRunning || _writeQueue.IsAddingCompleted)
                return false;

            // Bounded queue explicit backpressure policy - if full, drop/ignore current frame or wait.
            // Using TryAdd to immediately drop if queue is full (do not block main thread).
            return _writeQueue.TryAdd(job);
        }

        private void WriteLoop()
        {
            while (true)
            {
                FrameWriteJob job;
                
                try
                {
                    if (!_writeQueue.TryTake(out job, Timeout.Infinite))
                    {
                        // TryTake failed and CompleteAdding was called
                        break;
                    }
                }
                catch (System.InvalidOperationException) // Queue closed
                {
                    break;
                }

                // Write actual data
                string frameDir = Path.Combine(_framesFolder, job.frameIndex.ToString("D6"));
                if (!Directory.Exists(frameDir))
                {
                    Directory.CreateDirectory(frameDir);
                }

                if (job.jpgData != null && job.jpgData.Length > 0)
                {
                    string colorPath = Path.Combine(frameDir, "rgb.jpg");
                    File.WriteAllBytes(colorPath, job.jpgData);
                }

                if (job.maskPngData != null && job.maskPngData.Length > 0)
                {
                    string maskPath = Path.Combine(frameDir, "mask.png");
                    File.WriteAllBytes(maskPath, job.maskPngData);
                }

                if (job.depthData != null && job.depthData.Length > 0)
                {
                    string depthPath = Path.Combine(frameDir, "depth.bin");
                    File.WriteAllBytes(depthPath, job.depthData);
                }

                if (!string.IsNullOrEmpty(job.metaJson))
                {
                    string metaPath = Path.Combine(frameDir, "meta.json");
                    File.WriteAllText(metaPath, job.metaJson);
                }
            }
        }
    }
}

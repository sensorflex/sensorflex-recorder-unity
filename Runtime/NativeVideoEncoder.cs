using System;
using System.Runtime.InteropServices;
using System.Threading;
using AOT;

#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections.Concurrent;
using System.IO;
using UnityEngine;
#endif

namespace SensorFlex.Recorder
{
    // P/Invoke / JNI bridge to platform-native video encoders.
    // All public methods are safe to call on any platform; non-platform builds get no-op stubs.
    //
    // Call pattern:
    //   LogCapabilities(rgbCodec, depthCodec)         (once at Awake)
    //   StartRgbSession / StartDepthLz4Writer          (at first frame, mutually exclusive with StartDepthVideoSession)
    //     or StartDepthVideoSession
    //   AppendRgbFrame / AppendDepthLz4Frame           (each frame, main thread)
    //     or AppendDepthVideoFrame
    //   FinishRgbSession / FinishDepthSession           (at StopCapture)
    //   WaitForBothFinished                             (in ArchiveFinalizer, background thread)
    internal static class NativeVideoEncoder
    {
#if UNITY_IOS && !UNITY_EDITOR

        // ── Depth mode ─────────────────────────────────────────────────────────

        enum DepthMode { None, Lz4, Video }
        static DepthMode _depthMode;

        // ── Native function imports ────────────────────────────────────────────

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void EncoderDoneCallback(int success);

        // Capability check / prewarm
        [DllImport("__Internal")] static extern void    SFLogCapabilities(int rgbCodec, int depthCodec);
        [DllImport("__Internal")] static extern void SFPrewarmEncoders();
        [DllImport("__Internal")] static extern int  SFRgbEncoderIsReady();
        [DllImport("__Internal")] static extern int  SFDepthVideoEncoderIsReady();

        // RGB (codec: 0=H264, 1=HEVC)
        [DllImport("__Internal")] static extern void    SFRgbEncoder_Start(string path, int w, int h, int codec);
        [DllImport("__Internal")] static extern int     SFRgbEncoder_AppendFrame(IntPtr pY,    int strideY,
                                                                                  IntPtr pCbCr, int strideCbCr,
                                                                                  int w, int h, long tsNs);
        [DllImport("__Internal")] static extern void    SFRgbEncoder_Finish(EncoderDoneCallback cb);

        // Depth LZ4
        [DllImport("__Internal")] static extern void    SFDepthLz4_Start(string binPath, string sizesPath, int w, int h, int algo);
        [DllImport("__Internal")] static extern int     SFDepthLz4_AppendFrame(IntPtr pF32, int stride, int w, int h);
        [DllImport("__Internal")] static extern void    SFDepthLz4_Finish(EncoderDoneCallback cb);

        // Depth Video (codec: 0=H264, 1=HEVC)
        [DllImport("__Internal")] static extern void    SFDepthVideo_Start(string mp4Path, int w, int h, int codec, float maxDepthMeters);
        [DllImport("__Internal")] static extern int     SFDepthVideo_AppendFrame(IntPtr pF32, int stride, int w, int h, long tsNs);
        [DllImport("__Internal")] static extern void    SFDepthVideo_Finish(EncoderDoneCallback cb);

        // ── Completion events ──────────────────────────────────────────────────

        static readonly ManualResetEventSlim _rgbDone   = new ManualResetEventSlim(false);
        static readonly ManualResetEventSlim _depthDone = new ManualResetEventSlim(false);

        static readonly EncoderDoneCallback _onRgbDone   = OnRgbDone;
        static readonly EncoderDoneCallback _onDepthDone = OnDepthDone;

        [MonoPInvokeCallback(typeof(EncoderDoneCallback))]
        static void OnRgbDone(int success)   => _rgbDone.Set();

        [MonoPInvokeCallback(typeof(EncoderDoneCallback))]
        static void OnDepthDone(int success) => _depthDone.Set();

        // ── Public API ─────────────────────────────────────────────────────────

        public static void LogCapabilities(SfzRgbCodec rgbCodec, SfzDepthCodec depthCodec)
            => SFLogCapabilities((int)rgbCodec, (int)depthCodec);

        public static void PrewarmEncoders() => SFPrewarmEncoders();

        public static bool IsRgbReady        => SFRgbEncoderIsReady()       != 0;
        public static bool IsDepthVideoReady => SFDepthVideoEncoderIsReady() != 0;

        public static void StartRgbSession(string mp4Path, int width, int height, SfzRgbCodec codec)
        {
            _rgbDone.Reset();
            _depthDone.Reset();  // Reset at session start so WaitForBothFinished works even if depth never starts
            _depthMode = DepthMode.None;
            SFRgbEncoder_Start(mp4Path, width, height, (int)codec);
        }

        public static bool AppendRgbFrame(IntPtr pY, int strideY, IntPtr pCbCr, int strideCbCr,
                                           int width, int height, long timestampNs)
            => SFRgbEncoder_AppendFrame(pY, strideY, pCbCr, strideCbCr, width, height, timestampNs) != 0;

        public static void FinishRgbSession() => SFRgbEncoder_Finish(_onRgbDone);

        public static void StartDepthLz4Writer(string binPath, string sizesPath, int width, int height,
                                                SfzDepthCodec codec = SfzDepthCodec.LZ4)
        {
            _depthMode = DepthMode.Lz4;
            _depthDone.Reset();
            int algo = codec == SfzDepthCodec.Zstd ? 1 : 0;
            SFDepthLz4_Start(binPath, sizesPath, width, height, algo);
        }

        public static bool AppendDepthLz4Frame(IntPtr pF32, int stride, int width, int height)
            => SFDepthLz4_AppendFrame(pF32, stride, width, height) != 0;

        public static void StartDepthVideoSession(string mp4Path, int width, int height,
                                                   SfzDepthCodec codec, float depthMaxMeters)
        {
            _depthMode = DepthMode.Video;
            _depthDone.Reset();
            SFDepthVideo_Start(mp4Path, width, height, (int)codec, depthMaxMeters);
        }

        public static bool AppendDepthVideoFrame(IntPtr pF32, int stride, int width, int height, long tsNs)
            => SFDepthVideo_AppendFrame(pF32, stride, width, height, tsNs) != 0;

        // Unified finish — dispatches to whichever depth encoder is active.
        public static void FinishDepthSession()
        {
            switch (_depthMode)
            {
                case DepthMode.Lz4:
                    SFDepthLz4_Finish(_onDepthDone);
                    break;
                case DepthMode.Video:
                    SFDepthVideo_Finish(_onDepthDone);
                    break;
                default:
                    // No depth encoder started — signal done immediately.
                    _depthDone.Set();
                    break;
            }
        }

        public static void WaitForBothFinished(int timeoutMs)
        {
            _rgbDone.Wait(timeoutMs);
            _depthDone.Wait(timeoutMs);
        }

#elif UNITY_ANDROID && !UNITY_EDITOR

        // ── Depth mode ─────────────────────────────────────────────────────────

        enum DepthMode { None, Lz4, Video }
        static DepthMode _depthMode;

        // ── Android Java objects ───────────────────────────────────────────────

        static AndroidJavaObject _rgbEncoder;
        static AndroidJavaObject _depthVideoEncoder;
        static float             _depthVideoScale = 255.0f / 10.0f;

        // Pre-allocated marshal buffers for RGB (allocated at StartRgbSession).
        static byte[] _yBuf;
        static byte[] _uvBuf;
        static int    _rgbWidth, _rgbHeight;

        // Depth LZ4 producer-consumer.
        static BlockingCollection<byte[]> _depthLz4Queue;
        static Thread                     _depthLz4Thread;
        static FileStream                 _depthBinStream;
        static FileStream                 _depthSizStream;
        static int                        _depthLz4Width, _depthLz4Height;
        static byte[]                     _depthLz4EncodeBuf;

        // Completion events.
        static readonly ManualResetEventSlim _rgbDone   = new ManualResetEventSlim(false);
        static readonly ManualResetEventSlim _depthDone = new ManualResetEventSlim(false);

        // ── AndroidJavaProxy for done callbacks ────────────────────────────────

        sealed class AndroidDoneProxy : AndroidJavaProxy
        {
            readonly ManualResetEventSlim _done;
            public AndroidDoneProxy(ManualResetEventSlim done)
                : base("com.sensorflex.recorder.IEncoderDoneListener")
            { _done = done; }

            void onEncoderDone(bool success) => _done.Set();
        }

        // ── Public API ─────────────────────────────────────────────────────────

        public static void PrewarmEncoders() { }   // Android VT warmup not needed; MediaCodec starts fast
        public static bool IsRgbReady        => true;
        public static bool IsDepthVideoReady => true;

        public static void LogCapabilities(SfzRgbCodec rgbCodec, SfzDepthCodec depthCodec)
        {
            try
            {
                using var cls = new AndroidJavaClass("com.sensorflex.recorder.SFVideoEncoderAndroid");
                bool hevcAvail = cls.CallStatic<bool>("isCodecAvailable", "video/hevc");
                bool h264Avail = cls.CallStatic<bool>("isCodecAvailable", "video/avc");
                Debug.Log($"[SF] LogCapabilities: requested RGB={rgbCodec} Depth={depthCodec} " +
                          $"HEVC={hevcAvail} H264={h264Avail}");
                if (rgbCodec   == SfzRgbCodec.HEVC   && !hevcAvail) Debug.LogWarning("[SF] HEVC encoder requested for RGB but not available on this device.");
                if (depthCodec == SfzDepthCodec.HEVC  && !hevcAvail) Debug.LogWarning("[SF] HEVC encoder requested for Depth but not available on this device.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SF] LogCapabilities failed: {e.Message}");
            }
        }

        public static void StartRgbSession(string mp4Path, int width, int height, SfzRgbCodec codec)
        {
            _rgbDone.Reset();
            _depthDone.Reset();
            _depthMode  = DepthMode.None;
            _rgbWidth   = width;
            _rgbHeight  = height;
            _yBuf       = new byte[width * height];
            _uvBuf      = new byte[width * (height / 2)];

            _rgbEncoder = new AndroidJavaObject("com.sensorflex.recorder.SFVideoEncoderAndroid");
            _rgbEncoder.Call("start", mp4Path, width, height, (int)codec, false);
        }

        public static bool AppendRgbFrame(IntPtr pY, int strideY, IntPtr pCbCr, int strideCbCr,
                                           int width, int height, long timestampNs)
        {
            if (_rgbEncoder == null || _yBuf == null) return false;
            // Marshal Y plane row-by-row into pre-allocated buffer.
            unsafe
            {
                byte* srcY = (byte*)pY;
                for (int row = 0; row < height; row++)
                    Marshal.Copy((IntPtr)(srcY + row * strideY), _yBuf, row * width, width);
                byte* srcUV = (byte*)pCbCr;
                int uvRows = height / 2;
                for (int row = 0; row < uvRows; row++)
                    Marshal.Copy((IntPtr)(srcUV + row * strideCbCr), _uvBuf, row * width, width);
            }
            return _rgbEncoder.Call<bool>("appendFrame", _yBuf, _uvBuf, timestampNs / 1000L);
        }

        public static void FinishRgbSession()
        {
            if (_rgbEncoder == null) { _rgbDone.Set(); return; }
            var proxy = new AndroidDoneProxy(_rgbDone);
            _rgbEncoder.Call("finish", proxy);
        }

        public static void StartDepthLz4Writer(string binPath, string sizesPath, int width, int height,
                                                SfzDepthCodec codec = SfzDepthCodec.LZ4)
        {
            if (codec == SfzDepthCodec.Zstd)
                Debug.LogWarning("[SF] Zstd depth not yet implemented on Android — recording as LZ4.");
            _depthMode     = DepthMode.Lz4;
            _depthLz4Width  = width;
            _depthLz4Height = height;
            _depthLz4Queue  = new BlockingCollection<byte[]>(64);

            int maxEncoded = width * height * 4 + (width * height * 4 >> 3) + 256;
            _depthLz4EncodeBuf = new byte[maxEncoded];

            _depthBinStream = new FileStream(binPath,   FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            _depthSizStream = new FileStream(sizesPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096);

            _depthLz4Thread = new Thread(DepthLz4Worker) { IsBackground = true, Name = "SF-DepthLZ4" };
            _depthLz4Thread.Start();
        }

        public static bool AppendDepthLz4Frame(IntPtr pF32, int stride, int width, int height)
        {
            if (_depthLz4Queue == null || _depthLz4Queue.IsAddingCompleted) return false;
            int srcSize = width * height * 4;
            var buf = new byte[srcSize];
            unsafe
            {
                byte* src = (byte*)pF32;
                for (int row = 0; row < height; row++)
                    Marshal.Copy((IntPtr)(src + row * stride), buf, row * width * 4, width * 4);
            }
            return _depthLz4Queue.TryAdd(buf);
        }

        public static void StartDepthVideoSession(string mp4Path, int width, int height,
                                                   SfzDepthCodec codec, float depthMaxMeters)
        {
            _depthMode       = DepthMode.Video;
            _depthVideoScale = 255.0f / (depthMaxMeters > 0f ? depthMaxMeters : 10f);
            _depthDone.Reset();

            // For video depth on Android, normalise float32 to uint8 before passing to Java encoder.
            _depthVideoEncoder = new AndroidJavaObject("com.sensorflex.recorder.SFVideoEncoderAndroid");
            _depthVideoEncoder.Call("start", mp4Path, width, height, (int)codec == 1 ? 1 : 0, true);
        }

        public static bool AppendDepthVideoFrame(IntPtr pF32, int stride, int width, int height, long tsNs)
        {
            if (_depthVideoEncoder == null) return false;
            // Normalise float metres to uint8 Y plane; neutral CbCr.
            var yBuf  = new byte[width * height];
            var uvBuf = new byte[width * (height / 2)];
            unsafe
            {
                float* src   = (float*)pF32;
                float  scale = _depthVideoScale;
                for (int row = 0; row < height; row++)
                {
                    float* srcRow = (float*)((byte*)src + row * stride);
                    for (int col = 0; col < width; col++)
                    {
                        float d = srcRow[col] * scale;
                        yBuf[row * width + col] = (byte)(d < 0f ? 0 : d > 255f ? 255 : d);
                    }
                }
            }
            // Fill CbCr with 128 (neutral chroma).
            for (int i = 0; i < uvBuf.Length; i++) uvBuf[i] = 128;
            return _depthVideoEncoder.Call<bool>("appendFrame", yBuf, uvBuf, tsNs / 1000L);
        }

        // Unified finish for whichever depth encoder is active.
        public static void FinishDepthSession()
        {
            switch (_depthMode)
            {
                case DepthMode.Lz4:
                    _depthLz4Queue?.CompleteAdding();
                    _depthLz4Thread?.Join();
                    _depthBinStream?.Close();
                    _depthSizStream?.Close();
                    _depthDone.Set();
                    break;
                case DepthMode.Video:
                    if (_depthVideoEncoder != null)
                    {
                        var proxy = new AndroidDoneProxy(_depthDone);
                        _depthVideoEncoder.Call("finish", proxy);
                    }
                    else
                    {
                        _depthDone.Set();
                    }
                    break;
                default:
                    _depthDone.Set();
                    break;
            }
        }

        public static void WaitForBothFinished(int timeoutMs)
        {
            _rgbDone.Wait(timeoutMs);
            _depthDone.Wait(timeoutMs);
        }

        // ── Background LZ4 worker thread ───────────────────────────────────────

        static void DepthLz4Worker()
        {
            var sizeBytes = new byte[4];
            foreach (var rawFrame in _depthLz4Queue.GetConsumingEnumerable())
            {
                int encoded = Lz4BlockEncoder.Encode(
                    rawFrame, 0, rawFrame.Length,
                    _depthLz4EncodeBuf, 0, _depthLz4EncodeBuf.Length);

                if (encoded > 0)
                {
                    _depthBinStream.Write(_depthLz4EncodeBuf, 0, encoded);
                    sizeBytes[0] = (byte)(encoded         & 0xFF);
                    sizeBytes[1] = (byte)((encoded >>  8) & 0xFF);
                    sizeBytes[2] = (byte)((encoded >> 16) & 0xFF);
                    sizeBytes[3] = (byte)((encoded >> 24) & 0xFF);
                    _depthSizStream.Write(sizeBytes, 0, 4);
                }
            }
        }

#else

        // ── Stubs for Editor / unsupported platforms ───────────────────────────

        public static void PrewarmEncoders() { }
        public static bool IsRgbReady        => true;
        public static bool IsDepthVideoReady => true;

        public static void LogCapabilities(SfzRgbCodec _, SfzDepthCodec __) { }

        public static void StartRgbSession(string _, int __, int ___, SfzRgbCodec ____) { }
        public static bool AppendRgbFrame(IntPtr _, int __, IntPtr ___, int ____,
                                           int _____, int ______, long _______) => false;
        public static void FinishRgbSession() { }

        public static void StartDepthLz4Writer(string _, string __, int ___, int ____,
                                                SfzDepthCodec _____ = SfzDepthCodec.LZ4) { }
        public static bool AppendDepthLz4Frame(IntPtr _, int __, int ___, int ____) => false;

        public static void StartDepthVideoSession(string _, int __, int ___,
                                                   SfzDepthCodec ____, float _____) { }
        public static bool AppendDepthVideoFrame(IntPtr _, int __, int ___, int ____, long _____) => false;

        public static void FinishDepthSession() { }

        public static void WaitForBothFinished(int timeoutMs) { }

#endif
    }
}

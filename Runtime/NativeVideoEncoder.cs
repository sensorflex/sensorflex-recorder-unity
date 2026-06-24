using System;
using System.Runtime.InteropServices;
using System.Threading;
using AOT;

namespace SensorFlex.Recorder
{
    // P/Invoke bridge to SFVideoEncoder.mm.
    // All public methods are safe to call on any platform; non-iOS builds get no-op stubs.
    //
    // RGB  session: HEVC YCbCr420 → rgb.mp4
    // Depth writer: COMPRESSION_LZ4_RAW float32 → depth.bin + depth_sizes.bin
    //
    // Call pattern:
    //   StartRgbSession / StartDepthLz4Writer  (at first frame)
    //   AppendRgbFrame / AppendDepthLz4Frame   (each frame, main thread)
    //   FinishRgbSession / FinishDepthLz4Session (at StopCapture)
    //   WaitForBothFinished                     (in ArchiveFinalizer, background thread)
    internal static class NativeVideoEncoder
    {
#if UNITY_IOS && !UNITY_EDITOR

        // ── Native function imports ────────────────────────────────────────────

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void EncoderDoneCallback(int success);

        // RGB
        [DllImport("__Internal")] static extern void    SFRgbEncoder_Start(string path, int w, int h);
        [DllImport("__Internal")] static extern int     SFRgbEncoder_AppendFrame(IntPtr pY,    int strideY,
                                                                                  IntPtr pCbCr, int strideCbCr,
                                                                                  int w, int h, long tsNs);
        [DllImport("__Internal")] static extern void    SFRgbEncoder_Finish(EncoderDoneCallback cb);

        // Depth LZ4
        [DllImport("__Internal")] static extern void    SFDepthLz4_Start(string binPath, string sizesPath, int w, int h);
        [DllImport("__Internal")] static extern int     SFDepthLz4_AppendFrame(IntPtr pF32, int stride, int w, int h);
        [DllImport("__Internal")] static extern void    SFDepthLz4_Finish(EncoderDoneCallback cb);

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

        public static void StartRgbSession(string mp4Path, int width, int height)
        {
            _rgbDone.Reset();
            SFRgbEncoder_Start(mp4Path, width, height);
        }

        public static bool AppendRgbFrame(IntPtr pY, int strideY, IntPtr pCbCr, int strideCbCr,
                                           int width, int height, long timestampNs)
            => SFRgbEncoder_AppendFrame(pY, strideY, pCbCr, strideCbCr, width, height, timestampNs) != 0;

        public static void FinishRgbSession() => SFRgbEncoder_Finish(_onRgbDone);

        public static void StartDepthLz4Writer(string binPath, string sizesPath, int width, int height)
        {
            _depthDone.Reset();
            SFDepthLz4_Start(binPath, sizesPath, width, height);
        }

        public static bool AppendDepthLz4Frame(IntPtr pF32, int stride, int width, int height)
            => SFDepthLz4_AppendFrame(pF32, stride, width, height) != 0;

        public static void FinishDepthLz4Session() => SFDepthLz4_Finish(_onDepthDone);

        public static void WaitForBothFinished(int timeoutMs)
        {
            _rgbDone.Wait(timeoutMs);
            _depthDone.Wait(timeoutMs);
        }

#else
        // ── Stubs for non-iOS platforms ────────────────────────────────────────

        public static void StartRgbSession(string mp4Path, int width, int height) { }
        public static bool AppendRgbFrame(IntPtr pY, int strideY, IntPtr pCbCr, int strideCbCr,
                                           int width, int height, long timestampNs) => false;
        public static void FinishRgbSession() { }

        public static void StartDepthLz4Writer(string binPath, string sizesPath, int width, int height) { }
        public static bool AppendDepthLz4Frame(IntPtr pF32, int stride, int width, int height) => false;
        public static void FinishDepthLz4Session() { }

        public static void WaitForBothFinished(int timeoutMs) { }
#endif
    }
}

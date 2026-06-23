using System;
using System.Runtime.InteropServices;
using System.Threading;
using AOT;

namespace SensorFlex.Recorder
{
    // P/Invoke bridge to SFVideoEncoder.mm.
    // All public methods are safe to call on any platform; they no-op on non-iOS.
    // On iOS the native plugin encodes RGB via HEVC YCbCr420 and depth via HEVC
    // OneComponent16Half.  Both sessions run concurrently and are waited on via
    // ManualResetEventSlim in WaitForBothFinished().
    internal static class NativeVideoEncoder
    {
#if UNITY_IOS && !UNITY_EDITOR

        // ── Native function imports ────────────────────────────────────────────

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void EncoderDoneCallback(int success);

        [DllImport("__Internal")] static extern void    SFRgbEncoder_Start(string path, int w, int h);
        [DllImport("__Internal")] static extern int     SFRgbEncoder_AppendFrame(IntPtr pY,    int strideY,
                                                                                  IntPtr pCbCr, int strideCbCr,
                                                                                  int w, int h, long tsNs);
        [DllImport("__Internal")] static extern void    SFRgbEncoder_Finish(EncoderDoneCallback cb);

        [DllImport("__Internal")] static extern void    SFDepthEncoder_Start(string path, int w, int h);
        [DllImport("__Internal")] static extern int     SFDepthEncoder_AppendFrame(IntPtr pF32, int stride,
                                                                                    int w, int h, long tsNs);
        [DllImport("__Internal")] static extern void    SFDepthEncoder_Finish(EncoderDoneCallback cb);

        // ── Completion events ──────────────────────────────────────────────────

        static readonly ManualResetEventSlim _rgbDone   = new ManualResetEventSlim(false);
        static readonly ManualResetEventSlim _depthDone = new ManualResetEventSlim(false);

        // Hold static delegate references to prevent GC collection.
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

        public static void StartDepthSession(string mp4Path, int width, int height)
        {
            _depthDone.Reset();
            SFDepthEncoder_Start(mp4Path, width, height);
        }

        public static bool AppendDepthFrame(IntPtr pF32, int stride,
                                             int width, int height, long timestampNs)
            => SFDepthEncoder_AppendFrame(pF32, stride, width, height, timestampNs) != 0;

        public static void FinishDepthSession() => SFDepthEncoder_Finish(_onDepthDone);

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

        public static void StartDepthSession(string mp4Path, int width, int height) { }
        public static bool AppendDepthFrame(IntPtr pF32, int stride,
                                             int width, int height, long timestampNs) => false;
        public static void FinishDepthSession() { }

        public static void WaitForBothFinished(int timeoutMs) { }
#endif
    }
}

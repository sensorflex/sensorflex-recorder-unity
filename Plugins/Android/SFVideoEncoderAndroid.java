package com.sensorflex.recorder;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.MediaMuxer;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Log;

import java.io.IOException;
import java.nio.ByteBuffer;

public class SFVideoEncoderAndroid {
    private static final String TAG = "SFVideoEncoder";
    private static final int BIT_RATE_RGB   = 10_000_000;
    private static final int BIT_RATE_DEPTH =  2_000_000;

    private MediaCodec      mEncoder;
    private MediaMuxer      mMuxer;
    private int             mTrackIndex   = -1;
    private boolean         mMuxerStarted = false;
    private HandlerThread   mThread;
    private Handler         mHandler;
    private volatile boolean mFinishing   = false;
    private IEncoderDoneListener mDoneListener;
    private int mWidth, mHeight;

    // codec: 0=H264, 1=HEVC  |  isDepth: depth uses low bit-rate
    public void start(String outputPath, int width, int height, int codec, boolean isDepth) {
        mWidth  = width;
        mHeight = height;
        String mime = codec == 1 ? MediaFormat.MIMETYPE_VIDEO_HEVC
                                 : MediaFormat.MIMETYPE_VIDEO_AVC;
        try {
            MediaFormat fmt = MediaFormat.createVideoFormat(mime, width, height);
            fmt.setInteger(MediaFormat.KEY_COLOR_FORMAT,
                           MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible);
            fmt.setInteger(MediaFormat.KEY_BIT_RATE,   isDepth ? BIT_RATE_DEPTH : BIT_RATE_RGB);
            fmt.setInteger(MediaFormat.KEY_FRAME_RATE, 60);
            fmt.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);

            mEncoder = MediaCodec.createEncoderByType(mime);
            mEncoder.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            mMuxer   = new MediaMuxer(outputPath, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4);

            mThread  = new HandlerThread("SFEncoder");
            mThread.start();
            mHandler = new Handler(mThread.getLooper());
            mEncoder.start();
            Log.d(TAG, "[SF] " + mime + " encoder started " + width + "x" + height);
        } catch (IOException e) {
            Log.e(TAG, "[SF] Encoder start failed: " + e.getMessage());
        }
    }

    // yData: Y plane row-packed (width bytes/row), uvData: interleaved CbCr (width bytes/row for height/2 rows)
    public boolean appendFrame(final byte[] yData, final byte[] uvData, final long timestampUs) {
        if (mEncoder == null || mFinishing) return false;
        mHandler.post(() -> { encodeFrame(yData, uvData, timestampUs); drainEncoder(false); });
        return true;
    }

    public void finish(final IEncoderDoneListener listener) {
        mDoneListener = listener;
        mFinishing    = true;
        mHandler.post(() -> {
            if (mEncoder != null) {
                mEncoder.signalEndOfInputStream();
                drainEncoder(true);
            }
            cleanUp(true);
        });
    }

    // Returns whether HEVC or H264 encoding is available on this device.
    public static boolean isCodecAvailable(String mimeType) {
        try { MediaCodec c = MediaCodec.createEncoderByType(mimeType); c.release(); return true; }
        catch (Exception e) { return false; }
    }

    private void encodeFrame(byte[] yData, byte[] uvData, long timestampUs) {
        int idx = mEncoder.dequeueInputBuffer(100_000);
        if (idx < 0) { Log.w(TAG, "[SF] Input buffer unavailable, frame dropped"); return; }
        ByteBuffer buf = mEncoder.getInputBuffer(idx);
        buf.clear();
        buf.put(yData);
        buf.put(uvData);
        mEncoder.queueInputBuffer(idx, 0, yData.length + uvData.length, timestampUs, 0);
    }

    private void drainEncoder(boolean eos) {
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        while (true) {
            int idx = mEncoder.dequeueOutputBuffer(info, eos ? 100_000 : 0);
            if (idx == MediaCodec.INFO_TRY_AGAIN_LATER) { if (!eos) break; }
            else if (idx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                mTrackIndex   = mMuxer.addTrack(mEncoder.getOutputFormat());
                mMuxer.start();
                mMuxerStarted = true;
            } else if (idx >= 0) {
                ByteBuffer data = mEncoder.getOutputBuffer(idx);
                if ((info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) == 0
                        && info.size > 0 && mMuxerStarted) {
                    data.position(info.offset);
                    data.limit(info.offset + info.size);
                    mMuxer.writeSampleData(mTrackIndex, data, info);
                }
                mEncoder.releaseOutputBuffer(idx, false);
                if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) break;
            }
        }
    }

    private void cleanUp(boolean success) {
        try {
            if (mEncoder != null) { mEncoder.stop(); mEncoder.release(); mEncoder = null; }
            if (mMuxer   != null) { mMuxer.stop();  mMuxer.release();   mMuxer   = null; }
        } catch (Exception e) { Log.e(TAG, "[SF] Cleanup error: " + e.getMessage()); success = false; }
        mThread.quitSafely();
        if (mDoneListener != null) mDoneListener.onEncoderDone(success);
    }
}

// SFVideoEncoder.mm
// Hardware HEVC encoding for SensorFlex Recorder — RGB path.
// LZ4 depth writer using Apple Compression framework.
//
// RGB: YCbCr420 (BiPlanar, FullRange) via AVFoundation + VideoToolbox HEVC → rgb.mp4
// Depth: float32 metres → COMPRESSION_LZ4_RAW → depth.bin (raw concatenated blocks)
//         Compressed byte count per frame → depth_sizes.bin (int32 per frame, little-endian)
//
// depth.bin is compatible with K4os.Compression.LZ4 LZ4Codec.Decode on the player side.
//
// Timestamps passed from C# are session-relative nanoseconds (first frame = 0 ns).

#import <AVFoundation/AVFoundation.h>
#include <compression.h>

// ─── Shared types ─────────────────────────────────────────────────────────────

typedef void (*SFEncoderDoneCallback)(int success);

typedef struct {
    AVAssetWriter                          *writer;
    AVAssetWriterInput                     *input;
    AVAssetWriterInputPixelBufferAdaptor   *adaptor;
} SFEncoderSession;

// ─── RGB encoder ──────────────────────────────────────────────────────────────

static SFEncoderSession gRgb = {};

extern "C" {

void SFRgbEncoder_Start(const char *mp4Path, int32_t width, int32_t height) {
    NSURL *url = [NSURL fileURLWithPath:@(mp4Path)];
    [[NSFileManager defaultManager] removeItemAtURL:url error:nil];
    NSError *err = nil;
    gRgb.writer = [AVAssetWriter assetWriterWithURL:url fileType:AVFileTypeMPEG4 error:&err];
    if (!gRgb.writer) { NSLog(@"[SF] SFRgbEncoder_Start: writer creation failed: %@", err); return; }

    NSDictionary *settings = @{
        AVVideoCodecKey:  AVVideoCodecTypeHEVC,
        AVVideoWidthKey:  @(width),
        AVVideoHeightKey: @(height),
        AVVideoCompressionPropertiesKey: @{
            AVVideoExpectedSourceFrameRateKey: @60,
            AVVideoAllowFrameReorderingKey:    @NO,
        },
    };
    gRgb.input = [AVAssetWriterInput assetWriterInputWithMediaType:AVMediaTypeVideo
                                                    outputSettings:settings];
    gRgb.input.expectsMediaDataInRealTime = YES;

    // IOSurface-backed pool so VideoToolbox gets zero-copy access to the pixel data.
    NSDictionary *pbAttrs = @{
        (NSString *)kCVPixelBufferPixelFormatTypeKey:       @(kCVPixelFormatType_420YpCbCr8BiPlanarFullRange),
        (NSString *)kCVPixelBufferWidthKey:                 @(width),
        (NSString *)kCVPixelBufferHeightKey:                @(height),
        (NSString *)kCVPixelBufferIOSurfacePropertiesKey:   @{},
    };
    gRgb.adaptor = [AVAssetWriterInputPixelBufferAdaptor
        assetWriterInputPixelBufferAdaptorWithAssetWriterInput:gRgb.input
                                  sourcePixelBufferAttributes:pbAttrs];

    [gRgb.writer addInput:gRgb.input];
    if (![gRgb.writer startWriting]) {
        NSLog(@"[SF] SFRgbEncoder_Start: startWriting failed: %@", gRgb.writer.error);
        gRgb = {};
        return;
    }
    [gRgb.writer startSessionAtSourceTime:kCMTimeZero];
}

// Returns 1 on success, 0 if the input is not ready (frame dropped).
int32_t SFRgbEncoder_AppendFrame(void *pY,    int32_t strideY,
                                  void *pCbCr, int32_t strideCbCr,
                                  int32_t width, int32_t height,
                                  int64_t timestampNs) {
    if (!gRgb.input || !gRgb.input.isReadyForMoreMediaData) return 0;
    if (!gRgb.adaptor.pixelBufferPool) return 0;

    CVPixelBufferRef pixBuf = NULL;
    if (CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault,
                                           gRgb.adaptor.pixelBufferPool,
                                           &pixBuf) != kCVReturnSuccess || !pixBuf) return 0;

    CVPixelBufferLockBaseAddress(pixBuf, 0);

    // Copy Y plane row-by-row to handle stride differences.
    uint8_t *yDst    = (uint8_t *)CVPixelBufferGetBaseAddressOfPlane(pixBuf, 0);
    size_t   yDstRow = CVPixelBufferGetBytesPerRowOfPlane(pixBuf, 0);
    uint8_t *ySrc    = (uint8_t *)pY;
    for (int32_t row = 0; row < height; row++)
        memcpy(yDst + row * yDstRow, ySrc + row * (size_t)strideY, (size_t)width);

    // Copy CbCr plane (width bytes per row).
    uint8_t *uvDst    = (uint8_t *)CVPixelBufferGetBaseAddressOfPlane(pixBuf, 1);
    size_t   uvDstRow = CVPixelBufferGetBytesPerRowOfPlane(pixBuf, 1);
    uint8_t *uvSrc    = (uint8_t *)pCbCr;
    for (int32_t row = 0; row < height / 2; row++)
        memcpy(uvDst + row * uvDstRow, uvSrc + row * (size_t)strideCbCr, (size_t)width);

    CVPixelBufferUnlockBaseAddress(pixBuf, 0);

    CMTime time = CMTimeMake(timestampNs, 1000000000LL);
    BOOL ok = [gRgb.adaptor appendPixelBuffer:pixBuf withPresentationTime:time];
    CVPixelBufferRelease(pixBuf);
    return ok ? 1 : 0;
}

void SFRgbEncoder_Finish(SFEncoderDoneCallback callback) {
    if (!gRgb.input) { if (callback) callback(0); return; }
    [gRgb.input markAsFinished];
    [gRgb.writer finishWritingWithCompletionHandler:^{
        int ok = (gRgb.writer.status == AVAssetWriterStatusCompleted) ? 1 : 0;
        if (!ok) NSLog(@"[SF] SFRgbEncoder_Finish failed: %@", gRgb.writer.error);
        gRgb = {};
        if (callback) callback(ok);
    }];
}

// ─── Depth LZ4 writer ─────────────────────────────────────────────────────────
//
// Each depth frame: copy float32 plane → COMPRESSION_LZ4_RAW → append to depth.bin.
// Compressed byte count (int32 LE) appended to depth_sizes.bin.
// Both writes happen on a serial GCD queue (background thread).

static dispatch_queue_t gDepthQueue    = NULL;
static FILE            *gDepthBinFile  = NULL;
static FILE            *gDepthSizFile  = NULL;

void SFDepthLz4_Start(const char *binPath, const char *sizesPath,
                       int32_t width, int32_t height) {
    remove(binPath);
    remove(sizesPath);
    gDepthBinFile = fopen(binPath,   "wb");
    gDepthSizFile = fopen(sizesPath, "wb");
    if (!gDepthBinFile || !gDepthSizFile) {
        NSLog(@"[SF] SFDepthLz4_Start: failed to open output files");
        return;
    }
    gDepthQueue = dispatch_queue_create("com.sensorflex.depth_lz4",
                                         DISPATCH_QUEUE_SERIAL);
}

// Copy depth plane, enqueue LZ4 compression + write to background queue.
// Returns 1 if successfully enqueued, 0 on failure (frame dropped).
int32_t SFDepthLz4_AppendFrame(void *pF32, int32_t stride,
                                 int32_t width, int32_t height) {
    if (!gDepthQueue) return 0;

    int32_t srcSize = width * height * 4;
    void *buf = malloc(srcSize);
    if (!buf) return 0;

    // Row-by-row copy to de-stride the plane (ARKit stride may exceed width*4).
    for (int32_t row = 0; row < height; row++)
        memcpy((char*)buf + row * width * 4,
               (char*)pF32 + row * stride,
               width * 4);

    dispatch_async(gDepthQueue, ^{
        // LZ4 worst-case: srcSize + srcSize/255 + 16; use +1/8 + 256 for safety.
        size_t maxDst = (size_t)srcSize + (srcSize >> 3) + 256;
        void  *dst    = malloc(maxDst);
        size_t compLen = 0;
        if (dst)
            compLen = compression_encode_buffer(dst, maxDst,
                                                (const uint8_t *)buf, srcSize,
                                                NULL, COMPRESSION_LZ4_RAW);
        free(buf);

        if (compLen > 0 && gDepthBinFile && gDepthSizFile) {
            fwrite(dst, 1, compLen, gDepthBinFile);
            int32_t len32 = (int32_t)compLen;
            fwrite(&len32, sizeof(int32_t), 1, gDepthSizFile);
        }
        if (dst) free(dst);
    });
    return 1;
}

// Drain the serial queue, close both files, then invoke callback.
void SFDepthLz4_Finish(SFEncoderDoneCallback callback) {
    if (!gDepthQueue) { if (callback) callback(1); return; }
    dispatch_queue_t q = gDepthQueue;
    gDepthQueue = NULL;   // prevent new appends from enqueueing
    dispatch_async(q, ^{
        if (gDepthBinFile) { fclose(gDepthBinFile); gDepthBinFile = NULL; }
        if (gDepthSizFile) { fclose(gDepthSizFile); gDepthSizFile = NULL; }
        if (callback) callback(1);
    });
}

} // extern "C"

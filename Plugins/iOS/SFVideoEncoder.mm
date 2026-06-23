// SFVideoEncoder.mm
// Hardware HEVC encoding for SensorFlex Recorder.
//
// Two independent AVAssetWriter sessions:
//   RGB   — YCbCr420 (BiPlanar, FullRange) via the adaptor's IOSurface-backed pool
//   Depth — BGRA32 (float16 packed: B=low byte, G=high byte, R=0, A=0xFF),
//            lossless HEVC for bit-exact float16 preservation
//
// Depth packing:
//   float32 metres → float16 (ARM hardware conversion, __fp16 cast)
//   uint16_t bits  → B = bits & 0xFF,  G = bits >> 8
//   Decoder:  bits = (G << 8) | B → view as float16 → cast to float32
//
// Timestamps passed from C# are already session-relative (first frame = 0 ns).

#import <AVFoundation/AVFoundation.h>
#import <VideoToolbox/VideoToolbox.h>

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

    // Get an IOSurface-backed pixel buffer from the adaptor's own pool.
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

    // Copy CbCr plane (width/2 pairs × 2 bytes = width bytes per row).
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

// ─── Depth encoder ────────────────────────────────────────────────────────────

static SFEncoderSession gDepth = {};

void SFDepthEncoder_Start(const char *mp4Path, int32_t width, int32_t height) {
    NSURL *url = [NSURL fileURLWithPath:@(mp4Path)];
    [[NSFileManager defaultManager] removeItemAtURL:url error:nil];
    NSError *err = nil;
    gDepth.writer = [AVAssetWriter assetWriterWithURL:url fileType:AVFileTypeMPEG4 error:&err];
    if (!gDepth.writer) { NSLog(@"[SF] SFDepthEncoder_Start: writer creation failed: %@", err); return; }

    // Lossless HEVC with BGRA32 input: bit-exact preservation of float16 depth.
    NSDictionary *settings = @{
        AVVideoCodecKey:  AVVideoCodecTypeHEVC,
        AVVideoWidthKey:  @(width),
        AVVideoHeightKey: @(height),
        AVVideoCompressionPropertiesKey: @{
            AVVideoExpectedSourceFrameRateKey:           @60,
            AVVideoAllowFrameReorderingKey:              @NO,
            (NSString *)kVTCompressionPropertyKey_Lossless: @YES,
        },
    };
    gDepth.input = [AVAssetWriterInput assetWriterInputWithMediaType:AVMediaTypeVideo
                                                      outputSettings:settings];
    gDepth.input.expectsMediaDataInRealTime = YES;

    NSDictionary *pbAttrs = @{
        (NSString *)kCVPixelBufferPixelFormatTypeKey:       @(kCVPixelFormatType_32BGRA),
        (NSString *)kCVPixelBufferWidthKey:                 @(width),
        (NSString *)kCVPixelBufferHeightKey:                @(height),
        (NSString *)kCVPixelBufferIOSurfacePropertiesKey:   @{},
    };
    gDepth.adaptor = [AVAssetWriterInputPixelBufferAdaptor
        assetWriterInputPixelBufferAdaptorWithAssetWriterInput:gDepth.input
                                  sourcePixelBufferAttributes:pbAttrs];

    [gDepth.writer addInput:gDepth.input];
    if (![gDepth.writer startWriting]) {
        NSLog(@"[SF] SFDepthEncoder_Start: startWriting failed: %@", gDepth.writer.error);
        gDepth = {};
        return;
    }
    [gDepth.writer startSessionAtSourceTime:kCMTimeZero];
}

// pF32: float32 depth plane (metres, row-major).  stride is in bytes.
// Returns 1 on success, 0 if input not ready (frame dropped).
int32_t SFDepthEncoder_AppendFrame(void *pF32, int32_t stride,
                                    int32_t width, int32_t height,
                                    int64_t timestampNs) {
    if (!gDepth.input || !gDepth.input.isReadyForMoreMediaData) return 0;
    if (!gDepth.adaptor.pixelBufferPool) return 0;

    CVPixelBufferRef dstBuf = NULL;
    if (CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault,
                                           gDepth.adaptor.pixelBufferPool,
                                           &dstBuf) != kCVReturnSuccess || !dstBuf) return 0;

    CVPixelBufferLockBaseAddress(dstBuf, 0);
    uint8_t *dstBase        = (uint8_t *)CVPixelBufferGetBaseAddress(dstBuf);
    size_t   dstStride      = CVPixelBufferGetBytesPerRow(dstBuf);
    float   *srcBase        = (float *)pF32;
    int32_t  srcFloatStride = stride / 4;  // byte stride → float elements per row

    for (int32_t row = 0; row < height; row++) {
        uint8_t *dstRow = dstBase + row * dstStride;
        float   *srcRow = srcBase + row * srcFloatStride;
        for (int32_t col = 0; col < width; col++) {
            __fp16 h = (__fp16)srcRow[col];  // float32 → float16 (ARM hardware)
            uint16_t bits;
            memcpy(&bits, &h, sizeof(bits));
            dstRow[col * 4 + 0] = (uint8_t)(bits & 0xFF);          // B = low byte
            dstRow[col * 4 + 1] = (uint8_t)((bits >> 8) & 0xFF);   // G = high byte
            dstRow[col * 4 + 2] = 0;                                // R = unused
            dstRow[col * 4 + 3] = 0xFF;                             // A = opaque
        }
    }
    CVPixelBufferUnlockBaseAddress(dstBuf, 0);

    CMTime time = CMTimeMake(timestampNs, 1000000000LL);
    BOOL ok = [gDepth.adaptor appendPixelBuffer:dstBuf withPresentationTime:time];
    CVPixelBufferRelease(dstBuf);
    return ok ? 1 : 0;
}

void SFDepthEncoder_Finish(SFEncoderDoneCallback callback) {
    if (!gDepth.input) { if (callback) callback(0); return; }
    [gDepth.input markAsFinished];
    [gDepth.writer finishWritingWithCompletionHandler:^{
        int ok = (gDepth.writer.status == AVAssetWriterStatusCompleted) ? 1 : 0;
        if (!ok) NSLog(@"[SF] SFDepthEncoder_Finish failed: %@", gDepth.writer.error);
        gDepth = {};
        if (callback) callback(ok);
    }];
}

} // extern "C"

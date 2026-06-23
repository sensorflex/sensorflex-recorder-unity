// SFVideoEncoder.mm
// Hardware HEVC encoding for SensorFlex Recorder.
//
// Two independent AVAssetWriter sessions:
//   RGB   — YCbCr420 (BiPlanar, FullRange) planes passed from Unity managed code
//   Depth — float32 planes converted to float16 via vDSP, encoded as HEVC Monochrome
//
// Unity passes raw plane pointers from XRCpuImage.GetPlane() NativeArrays.
// RGB planes are memcpy'd to a malloc'd YUV buffer owned by the CVPixelBuffer;
// the release callback frees it once AVFoundation is done.
// Depth conversion is synchronous (vDSP) into a pooled OneComponent16Half CVPixelBuffer.

#import <AVFoundation/AVFoundation.h>
#import <Accelerate/Accelerate.h>

// ─── Shared types ─────────────────────────────────────────────────────────────

typedef void (*SFEncoderDoneCallback)(int success);

typedef struct {
    AVAssetWriter *writer;
    AVAssetWriterInput *input;
    AVAssetWriterInputPixelBufferAdaptor *adaptor;
} SFEncoderSession;

// ─── RGB encoder state ─────────────────────────────────────────────────────────

static SFEncoderSession gRgb = {};

// Called by CoreVideo when the CVPixelBuffer wrapping our malloc'd buffer is released.
// releaseRefCon is the malloc'd buffer pointer.
static void SFRgbRelease(void *releaseRefCon, const void *baseAddress,
                          size_t dataSize, size_t planeCount,
                          const void *planeAddresses[]) {
    free(releaseRefCon);
}

extern "C" {

void SFRgbEncoder_Start(const char *mp4Path, int32_t width, int32_t height) {
    NSURL *url = [NSURL fileURLWithPath:@(mp4Path)];
    [[NSFileManager defaultManager] removeItemAtURL:url error:nil];
    NSError *err = nil;
    gRgb.writer = [AVAssetWriter assetWriterWithURL:url fileType:AVFileTypeMPEG4 error:&err];
    if (err) { NSLog(@"[SF] SFRgbEncoder_Start writer error: %@", err); return; }

    NSDictionary *settings = @{
        AVVideoCodecKey:  AVVideoCodecTypeHEVC,
        AVVideoWidthKey:  @(width),
        AVVideoHeightKey: @(height),
        AVVideoCompressionPropertiesKey: @{
            AVVideoExpectedSourceFrameRateKey: @30,
            AVVideoAllowFrameReorderingKey: @NO,
        },
    };
    gRgb.input = [AVAssetWriterInput assetWriterInputWithMediaType:AVMediaTypeVideo
                                                    outputSettings:settings];
    gRgb.input.expectsMediaDataInRealTime = YES;

    NSDictionary *pbAttrs = @{
        (NSString *)kCVPixelBufferPixelFormatTypeKey:
            @(kCVPixelFormatType_420YpCbCr8BiPlanarFullRange),
        (NSString *)kCVPixelBufferWidthKey:  @(width),
        (NSString *)kCVPixelBufferHeightKey: @(height),
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

    // Allocate a contiguous YUV buffer so the CVPixelBuffer owns its own memory.
    size_t ySize  = (size_t)strideY    * (size_t)height;
    size_t uvSize = (size_t)strideCbCr * (size_t)(height / 2);
    uint8_t *buf  = (uint8_t *)malloc(ySize + uvSize);
    if (!buf) return 0;

    memcpy(buf,         pY,    ySize);
    memcpy(buf + ySize, pCbCr, uvSize);

    void   *planes[2]  = { buf, buf + ySize };
    size_t  widths[2]  = { (size_t)width, (size_t)width / 2 };
    size_t  heights[2] = { (size_t)height, (size_t)height / 2 };
    size_t  strides[2] = { (size_t)strideY, (size_t)strideCbCr };

    CVPixelBufferRef pixBuf = NULL;
    CVReturn ret = CVPixelBufferCreateWithPlanarBytes(
        kCFAllocatorDefault,
        (size_t)width, (size_t)height,
        kCVPixelFormatType_420YpCbCr8BiPlanarFullRange,
        buf, ySize + uvSize,
        2, planes, widths, heights, strides,
        SFRgbRelease, buf,   // release callback frees buf
        NULL, &pixBuf);

    if (ret != kCVReturnSuccess || !pixBuf) { free(buf); return 0; }

    CMTime time = CMTimeMake(timestampNs, 1000000000LL);
    BOOL ok = [gRgb.adaptor appendPixelBuffer:pixBuf withPresentationTime:time];
    CVPixelBufferRelease(pixBuf);
    // buf freed by SFRgbRelease when pixBuf refcount drops to zero.
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

// ─── Depth encoder state ───────────────────────────────────────────────────────

static SFEncoderSession     gDepth     = {};
static CVPixelBufferPoolRef gDepthPool = NULL;
static int32_t              gDepthW    = 0, gDepthH = 0;

void SFDepthEncoder_Start(const char *mp4Path, int32_t width, int32_t height) {
    gDepthW = width; gDepthH = height;

    NSURL *url = [NSURL fileURLWithPath:@(mp4Path)];
    [[NSFileManager defaultManager] removeItemAtURL:url error:nil];
    NSError *err = nil;
    gDepth.writer = [AVAssetWriter assetWriterWithURL:url fileType:AVFileTypeMPEG4 error:&err];
    if (err) { NSLog(@"[SF] SFDepthEncoder_Start writer error: %@", err); return; }

    NSDictionary *settings = @{
        AVVideoCodecKey:  AVVideoCodecTypeHEVC,
        AVVideoWidthKey:  @(width),
        AVVideoHeightKey: @(height),
        AVVideoCompressionPropertiesKey: @{
            AVVideoExpectedSourceFrameRateKey: @30,
            AVVideoAllowFrameReorderingKey: @NO,
        },
    };
    gDepth.input = [AVAssetWriterInput assetWriterInputWithMediaType:AVMediaTypeVideo
                                                      outputSettings:settings];
    gDepth.input.expectsMediaDataInRealTime = YES;

    // Pool of OneComponent16Half pixel buffers for the float16 depth frames.
    NSDictionary *poolAttrs = @{
        (NSString *)kCVPixelBufferPixelFormatTypeKey:
            @(kCVPixelFormatType_OneComponent16Half),
        (NSString *)kCVPixelBufferWidthKey:  @(width),
        (NSString *)kCVPixelBufferHeightKey: @(height),
        (NSString *)kCVPixelBufferIOSurfacePropertiesKey: @{},
    };
    NSDictionary *pbAttrs = @{
        (NSString *)kCVPixelBufferPixelFormatTypeKey:
            @(kCVPixelFormatType_OneComponent16Half),
        (NSString *)kCVPixelBufferWidthKey:  @(width),
        (NSString *)kCVPixelBufferHeightKey: @(height),
    };
    gDepth.adaptor = [AVAssetWriterInputPixelBufferAdaptor
        assetWriterInputPixelBufferAdaptorWithAssetWriterInput:gDepth.input
                                  sourcePixelBufferAttributes:pbAttrs];

    CVPixelBufferPoolCreate(kCFAllocatorDefault, NULL,
                            (__bridge CFDictionaryRef)poolAttrs, &gDepthPool);

    [gDepth.writer addInput:gDepth.input];
    if (![gDepth.writer startWriting]) {
        NSLog(@"[SF] SFDepthEncoder_Start: startWriting failed: %@", gDepth.writer.error);
        if (gDepthPool) { CVPixelBufferPoolRelease(gDepthPool); gDepthPool = NULL; }
        gDepth = {};
        return;
    }
    [gDepth.writer startSessionAtSourceTime:kCMTimeZero];
}

// pF32: pointer to float32 depth plane (metres, row-major).
// Returns 1 on success, 0 if input not ready (frame dropped).
int32_t SFDepthEncoder_AppendFrame(void *pF32, int32_t stride,
                                    int32_t width, int32_t height,
                                    int64_t timestampNs) {
    if (!gDepth.input || !gDepth.input.isReadyForMoreMediaData || !gDepthPool) return 0;

    CVPixelBufferRef dstBuf = NULL;
    CVReturn ret = CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault, gDepthPool, &dstBuf);
    if (ret != kCVReturnSuccess || !dstBuf) return 0;

    CVPixelBufferLockBaseAddress(dstBuf, 0);
    vImage_Buffer srcVI = { pF32, (vImagePixelCount)height, (vImagePixelCount)width, (size_t)stride };
    vImage_Buffer dstVI = { CVPixelBufferGetBaseAddress(dstBuf), (vImagePixelCount)height, (vImagePixelCount)width, CVPixelBufferGetBytesPerRow(dstBuf) };
    vImageConvert_PlanarFtoPlanar16F(&srcVI, &dstVI, kvImageNoFlags);
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
        if (gDepthPool) { CVPixelBufferPoolRelease(gDepthPool); gDepthPool = NULL; }
        gDepth = {};
        if (callback) callback(ok);
    }];
}

} // extern "C"

namespace SensorFlex.Recorder
{
    public enum SfzRgbCodec   { H264 = 0, HEVC = 1 }

    // LZ4  = lossless float32 blocks → depth.bin (cross-platform, largest files)
    // HEVC = float16 video → depth.mp4 (iOS: OneComponent16Half; Android: uint8_norm)
    // H264 = uint8 normalised video → depth.mp4 (most compatible, lowest precision)
    public enum SfzDepthCodec { LZ4 = 0, HEVC = 1, H264 = 2, Zstd = 3 }
}

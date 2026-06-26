using System;
using Discap.Host.Capture;

namespace Discap.Host.Compression;

public interface IVideoEncoder : IDisposable
{
    bool IsAvailable { get; }
    bool Initialize(int width, int height, int frameRate = 60, int bitrate = 8_000_000);
    void SubmitFrame(FrameBuffer frame);
    bool TryGetNextPacket(out byte[] naluData, out int naluSize, int timeoutMs);
    void Reconfigure(int bitrate, int fps);
    void ForceKeyFrame();
}

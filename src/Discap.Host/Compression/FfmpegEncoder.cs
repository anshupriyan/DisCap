using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Discap.Host.Capture;

namespace Discap.Host.Compression;

public sealed class FfmpegEncoder : IVideoEncoder
{
    private Process? _ffmpeg;
    private bool _available;
    private Thread? _readerThread;
    private CancellationTokenSource _cts = new CancellationTokenSource();
    private BlockingCollection<byte[]> _naluQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
    public bool IsAvailable => _available;
    public int CurrentWidth { get; private set; }
    public int CurrentHeight { get; private set; }
    public int CurrentFrameRate { get; private set; }

    public bool Initialize(int width, int height, int frameRate = 60, int bitrate = 8_000_000)
    {
        CurrentWidth = width;
        CurrentHeight = height;
        CurrentFrameRate = frameRate;
        try
        {
            _ffmpeg = new Process();
            _ffmpeg.StartInfo.FileName = "ffmpeg.exe";
            
            string bitrateStr = (bitrate / 1_000_000) + "M";
            _ffmpeg.StartInfo.Arguments = $"-loglevel error -fflags nobuffer -flags low_delay -f rawvideo -pixel_format nv12 -video_size {width}x{height} -framerate {frameRate} -i pipe:0 -c:v h264_nvenc -preset llhq -rc cbr -cbr 1 -b:v {bitrateStr} -profile:v baseline -bf 0 -zerolatency 1 -delay 0 -bsf:v dump_extra -flush_packets 1 -max_muxing_queue_size 0 -f h264 pipe:1";
            
            _ffmpeg.StartInfo.UseShellExecute = false;
            _ffmpeg.StartInfo.RedirectStandardInput = true;
            _ffmpeg.StartInfo.RedirectStandardOutput = true;
            _ffmpeg.StartInfo.RedirectStandardError = true;
            _ffmpeg.ErrorDataReceived += (s, e) => {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Console.Error.WriteLine($"[FFMPEG] {e.Data}");
                }
            };

            _ffmpeg.Start();
            _ffmpeg.BeginErrorReadLine();

            Console.WriteLine($"[ENC] ffmpeg NVENC encoder started ({width}x{height} @ {frameRate}fps, {bitrateStr})");
            _available = true;

            _readerThread = new Thread(ReadLoop);
            _readerThread.Start();

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ENC] Failed to start ffmpeg: {ex.Message}");
            _available = false;
            return false;
        }
    }

    public void SubmitFrame(FrameBuffer frame)
    {
        if (!_available || _ffmpeg == null || _ffmpeg.HasExited) return;

        try
        {
            var bgraBytes = frame.GetTightPixels();
            _ffmpeg.StandardInput.BaseStream.Write(bgraBytes);
            _ffmpeg.StandardInput.BaseStream.Flush();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ENC] Encode exception: {ex.Message}");
        }
    }

    public bool TryGetNextPacket(out byte[] naluData, out int naluSize, int timeoutMs)
    {
        if (_naluQueue.TryTake(out byte[]? nalu, timeoutMs, _cts.Token))
        {
            naluData = nalu;
            naluSize = nalu.Length;
            return true;
        }
        
        naluData = Array.Empty<byte>();
        naluSize = 0;
        return false;
    }

    private void ReadLoop()
    {
        byte[] buffer = new byte[81920];
        List<byte> streamBuffer = new List<byte>(2 * 1024 * 1024);

        try
        {
            while (!_cts.IsCancellationRequested && _ffmpeg != null && !_ffmpeg.HasExited)
            {
                int read = _ffmpeg.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;

                streamBuffer.AddRange(new ReadOnlySpan<byte>(buffer, 0, read));
                ExtractNalUnits(streamBuffer);
            }
        }
        catch { }
    }

    private void ExtractNalUnits(List<byte> stream)
    {
        while (stream.Count >= 4)
        {
            // Find the FIRST start code (00 00 00 01)
            int firstStart = IndexOfStartCode(stream, 0);
            if (firstStart == -1) 
            {
                // No start code. Retain the last 3 bytes just in case they are part of a start code.
                if (stream.Count > 3)
                    stream.RemoveRange(0, stream.Count - 3);
                return;
            }

            // Remove any garbage before the first start code
            if (firstStart > 0)
            {
                stream.RemoveRange(0, firstStart);
            }

            if (stream.Count < 4) return;

            // Find the NEXT start code
            int nextStart = IndexOfStartCode(stream, 4);
            if (nextStart == -1)
            {
                // We have one start code but haven't found the next one yet.
                // Incomplete NAL unit, wait for more data.
                return;
            }

            // We found the next start code! Everything before it is one complete NAL unit.
            byte[] nalu = new byte[nextStart];
            stream.CopyTo(0, nalu, 0, nextStart);
            stream.RemoveRange(0, nextStart);

            _naluQueue.Add(nalu);
        }
    }

    private int IndexOfStartCode(List<byte> stream, int startIndex)
    {
        for (int i = startIndex; i <= stream.Count - 4; i++)
        {
            if (stream[i] == 0 && stream[i+1] == 0 && stream[i+2] == 0 && stream[i+3] == 1)
            {
                return i;
            }
        }
        return -1;
    }

    // Keep dummy methods to avoid breaking Program.cs signature completely before we update it
    public int Encode(FrameBuffer frame, out byte[] encodedData)
    {
        encodedData = Array.Empty<byte>();
        return 0;
    }

    public void ForceKeyFrame() { }
    public void Reconfigure(int bitrate, int fps) { }

    public void Dispose()
    {
        _cts.Cancel();
        if (_ffmpeg != null)
        {
            try
            {
                if (!_ffmpeg.HasExited)
                {
                    _ffmpeg.Kill();
                }
                _ffmpeg.Dispose();
            }
            catch { }
            _ffmpeg = null;
        }
        _available = false;
    }
}

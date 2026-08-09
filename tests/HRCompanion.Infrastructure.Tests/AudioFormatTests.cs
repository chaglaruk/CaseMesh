using HRCompanion.Audio.Windows;

namespace HRCompanion.Infrastructure.Tests;

public sealed class AudioFormatTests
{
    [Fact]
    public void ProcessLoopbackPacketBytes_UseCaptureBlockAlignment()
    {
        var format = ProcessLoopbackCaptureFormat.Create();

        Assert.Equal(44100, format.SampleRate);
        Assert.Equal(16, format.BitsPerSample);
        Assert.Equal(2, format.Channels);
        Assert.Equal(4, format.BlockAlign);
        Assert.Equal(4096, ProcessLoopbackCaptureFormat.GetByteCount(1024, format.BlockAlign));
    }

    [Fact]
    public void Converter_ProducesTwentyFourKhzMonoPcmFromSupportedLoopbackFormat()
    {
        var format = ProcessLoopbackCaptureFormat.Create();
        var converter = new AudioFrameConverter(format);
        var inputFrames = format.SampleRate / 10;
        var input = new byte[inputFrames * format.BlockAlign];
        for (var frame = 0; frame < inputFrames; frame++)
        {
            var sample = (short)(Math.Sin(frame * 2 * Math.PI * 440 / format.SampleRate) * 12000);
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var offset = frame * format.BlockAlign + channel * 2;
                input[offset] = (byte)(sample & 0xff);
                input[offset + 1] = (byte)((sample >> 8) & 0xff);
            }
        }

        var output = converter.Push(input, input.Length, DateTimeOffset.UtcNow).ToArray();
        var outputBytes = output.Sum(frame => frame.Pcm16Bit24KhzMono.Length);

        Assert.InRange(outputBytes, 4700, 4900);
        Assert.All(output, frame => Assert.Equal(0, frame.Pcm16Bit24KhzMono.Length % 2));
    }
}

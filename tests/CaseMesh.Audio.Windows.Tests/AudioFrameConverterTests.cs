using System.Buffers.Binary;
using CaseMesh.Core.Abstractions;
using NAudio.Wave;

namespace CaseMesh.Audio.Windows.Tests;

public sealed class AudioFrameConverterTests
{
    [Fact]
    public void RepeatedSmallFloatPackets_ProduceContinuous24KhzPcm16MonoFrames()
    {
        var converter = new AudioFrameConverter(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var frames = new List<AudioFrame>();

        for (var packetIndex = 0; packetIndex < 200; packetIndex++)
        {
            var packet = CreateStereoFloatPacket(240, packetIndex * 240, 48000);
            frames.AddRange(converter.Push(packet, DateTimeOffset.UtcNow));
        }

        Assert.Equal(20, frames.Count);
        Assert.All(frames, frame => Assert.Equal(2400, frame.Pcm16Bit24KhzMono.Length));
        Assert.Contains(frames, frame => frame.Pcm16Bit24KhzMono.Span.IndexOfAnyExcept((byte)0) >= 0);
    }

    [Fact]
    public void DefaultProcessLoopbackFormat_ProducesTwentyFramesPerSecond()
    {
        var converter = new AudioFrameConverter(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
        var frames = new List<AudioFrame>();

        for (var packetIndex = 0; packetIndex < 100; packetIndex++)
        {
            var packet = CreateStereoFloatPacket(441, packetIndex * 441, 44100);
            frames.AddRange(converter.Push(packet, DateTimeOffset.UtcNow));
        }

        Assert.Equal(20, frames.Count);
        Assert.All(frames, frame => Assert.Equal(2400, frame.Pcm16Bit24KhzMono.Length));
    }

    [Fact]
    public void Existing24KhzPcm16Mono_IsAggregatedWithoutFormatDrift()
    {
        var converter = new AudioFrameConverter(new WaveFormat(24000, 16, 1));
        var input = new byte[2401 * sizeof(short)];
        for (var sample = 0; sample < 2401; sample++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(sample * sizeof(short)), 8192);
        }

        var frames = converter.Push(input, DateTimeOffset.UtcNow);

        Assert.Equal(2, frames.Count);
        Assert.All(frames, frame => Assert.Equal(2400, frame.Pcm16Bit24KhzMono.Length));
        Assert.Equal(8192, BinaryPrimitives.ReadInt16LittleEndian(frames[0].Pcm16Bit24KhzMono.Span));
    }

    private static byte[] CreateStereoFloatPacket(int sampleFrames, int startSample, int sampleRate)
    {
        var bytes = new byte[sampleFrames * 2 * sizeof(float)];
        for (var frame = 0; frame < sampleFrames; frame++)
        {
            var value = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * (startSample + frame) / sampleRate));
            var bits = BitConverter.SingleToInt32Bits(value);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan((frame * 2) * sizeof(float)), bits);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan((frame * 2 + 1) * sizeof(float)), bits);
        }
        return bytes;
    }
}

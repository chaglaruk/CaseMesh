using System.Buffers.Binary;
using System.Diagnostics;
using CaseMesh.Core.Abstractions;
using NAudio.Wave;

namespace CaseMesh.Audio.Windows;

internal sealed class AudioFrameConverter
{
    private const int OutputSampleRate = 24000;
    private const int OutputSamplesPerFrame = 1200; // 50 ms
    private readonly WaveFormat _inputFormat;
    private readonly bool _isFloat;
    private readonly double _sourceSamplesPerOutputSample;
    private readonly List<float> _monoSamples = [];
    private readonly List<short> _outputSamples = [];
    private double _nextOutputIndex;

    public AudioFrameConverter(WaveFormat inputFormat)
    {
        if (inputFormat.Channels <= 0 || inputFormat.SampleRate <= 0 || inputFormat.BlockAlign <= 0)
            throw new ArgumentException("Audio input format is invalid.", nameof(inputFormat));

        _inputFormat = inputFormat;
        _isFloat = inputFormat.Encoding == WaveFormatEncoding.IeeeFloat ||
                   inputFormat is WaveFormatExtensible extensible &&
                   extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
        var isPcm = inputFormat.Encoding == WaveFormatEncoding.Pcm ||
                    inputFormat is WaveFormatExtensible pcmExtensible &&
                    pcmExtensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM;
        if (!_isFloat && !isPcm)
            throw new NotSupportedException($"Audio format {inputFormat.Encoding} is not supported for live transcription.");
        if (_isFloat && inputFormat.BitsPerSample is not (32 or 64))
            throw new NotSupportedException($"{inputFormat.BitsPerSample}-bit IEEE float audio is not supported.");
        if (!_isFloat && inputFormat.BitsPerSample is not (8 or 16 or 24 or 32))
            throw new NotSupportedException($"{inputFormat.BitsPerSample}-bit PCM audio is not supported.");

        _sourceSamplesPerOutputSample = inputFormat.SampleRate / (double)OutputSampleRate;
    }

    public IReadOnlyList<AudioFrame> Push(ReadOnlySpan<byte> buffer, DateTimeOffset capturedAt)
    {
        var completeBytes = buffer.Length - buffer.Length % _inputFormat.BlockAlign;
        var bytesPerSample = _inputFormat.BitsPerSample / 8;
        for (var frameOffset = 0; frameOffset < completeBytes; frameOffset += _inputFormat.BlockAlign)
        {
            double sum = 0;
            for (var channel = 0; channel < _inputFormat.Channels; channel++)
            {
                var sampleOffset = frameOffset + channel * bytesPerSample;
                sum += ReadSample(buffer.Slice(sampleOffset, bytesPerSample));
            }
            _monoSamples.Add((float)(sum / _inputFormat.Channels));
        }

        while (_nextOutputIndex + 1 < _monoSamples.Count)
        {
            var lowerIndex = (int)_nextOutputIndex;
            var fraction = _nextOutputIndex - lowerIndex;
            var sample = _monoSamples[lowerIndex] +
                         (_monoSamples[lowerIndex + 1] - _monoSamples[lowerIndex]) * fraction;
            _outputSamples.Add(FloatToPcm16(sample));
            _nextOutputIndex += _sourceSamplesPerOutputSample;
        }

        var removableSamples = Math.Min((int)_nextOutputIndex, Math.Max(0, _monoSamples.Count - 1));
        if (removableSamples > 0)
        {
            _monoSamples.RemoveRange(0, removableSamples);
            _nextOutputIndex -= removableSamples;
        }

        var frames = new List<AudioFrame>(_outputSamples.Count / OutputSamplesPerFrame);
        while (_outputSamples.Count >= OutputSamplesPerFrame)
        {
            var pcm = new byte[OutputSamplesPerFrame * sizeof(short)];
            for (var index = 0; index < OutputSamplesPerFrame; index++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)),
                    _outputSamples[index]);
            }
            _outputSamples.RemoveRange(0, OutputSamplesPerFrame);
            frames.Add(new AudioFrame(pcm, capturedAt));
        }
        return frames;
    }

    private double ReadSample(ReadOnlySpan<byte> sample)
    {
        if (_isFloat)
        {
            return _inputFormat.BitsPerSample == 32
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample))
                : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(sample));
        }

        return _inputFormat.BitsPerSample switch
        {
            8 => (sample[0] - 128) / 128.0,
            16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768.0,
            24 => ReadPcm24(sample) / 8388608.0,
            32 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648.0,
            _ => throw new UnreachableException()
        };
    }

    private static int ReadPcm24(ReadOnlySpan<byte> sample)
    {
        var value = sample[0] | sample[1] << 8 | sample[2] << 16;
        return (value & 0x800000) == 0 ? value : value | unchecked((int)0xff000000);
    }

    private static short FloatToPcm16(double sample)
    {
        if (!double.IsFinite(sample)) return 0;
        var clamped = Math.Clamp(sample, -1.0, 1.0);
        return clamped <= -1.0 ? short.MinValue : (short)Math.Round(clamped * short.MaxValue);
    }
}

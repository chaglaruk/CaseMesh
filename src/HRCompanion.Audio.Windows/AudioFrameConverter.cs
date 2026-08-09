using HRCompanion.Core.Abstractions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HRCompanion.Audio.Windows;

internal sealed class AudioFrameConverter
{
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _resampler;
    private readonly float[] _sampleBuffer = new float[2400]; // ~100 ms at 24 kHz

    public AudioFrameConverter(WaveFormat inputFormat)
    {
        _buffer = new BufferedWaveProvider(inputFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };

        ISampleProvider samples = _buffer.ToSampleProvider();
        if (samples.WaveFormat.Channels > 1)
        {
            samples = new DownmixToMonoSampleProvider(samples);
        }
        _resampler = samples.WaveFormat.SampleRate == 24000
            ? samples
            : new WdlResamplingSampleProvider(samples, 24000);
    }

    public IEnumerable<AudioFrame> Push(byte[] buffer, int bytesRecorded, DateTimeOffset capturedAt)
    {
        _buffer.AddSamples(buffer, 0, bytesRecorded);
        while (_buffer.BufferedBytes > 0)
        {
            var read = _resampler.Read(_sampleBuffer, 0, _sampleBuffer.Length);
            if (read <= 0) yield break;

            var pcm = new byte[read * 2];
            for (var i = 0; i < read; i++)
            {
                var sample = Math.Clamp(_sampleBuffer[i], -1f, 1f);
                var value = (short)Math.Round(sample * short.MaxValue);
                pcm[i * 2] = (byte)(value & 0xff);
                pcm[i * 2 + 1] = (byte)((value >> 8) & 0xff);
            }
            yield return new AudioFrame(pcm, capturedAt);

            // Avoid manufacturing silence when the buffered source has been drained.
            if (_buffer.BufferedBytes == 0) yield break;
        }
    }

    private sealed class DownmixToMonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private float[] _sourceBuffer = [];

        public DownmixToMonoSampleProvider(ISampleProvider source)
        {
            _source = source;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var channels = _source.WaveFormat.Channels;
            var sourceNeeded = count * channels;
            if (_sourceBuffer.Length < sourceNeeded) _sourceBuffer = new float[sourceNeeded];
            var sourceRead = _source.Read(_sourceBuffer, 0, sourceNeeded);
            var frames = sourceRead / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                float sum = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += _sourceBuffer[frame * channels + channel];
                }
                buffer[offset + frame] = sum / channels;
            }
            return frames;
        }
    }
}

using System.Buffers.Binary;
using Highlights.Core.Pipeline;

namespace Highlights.Core.Stages.Signals;

internal static class WavReader
{
    /// <summary>Reads a 16-bit PCM mono WAV (as produced by Extract) into samples in [-1, 1].</summary>
    public static async Task<(float[] Samples, int SampleRate)> ReadMonoPcm16Async(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var span = bytes.AsSpan();
        if (span.Length < 12 || !span[..4].SequenceEqual("RIFF"u8) || !span[8..12].SequenceEqual("WAVE"u8))
            throw new PipelineException($"{path} is not a WAV file.");

        int sampleRate = 0, channels = 0, bits = 0;
        var pos = 12;
        while (pos + 8 <= span.Length)
        {
            var id = span.Slice(pos, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos + 4, 4)), (uint)(span.Length - pos - 8));
            var body = span.Slice(pos + 8, size);

            if (id.SequenceEqual("fmt "u8))
            {
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels != 1 || bits != 16)
                    throw new PipelineException($"{path}: expected 16-bit mono PCM, got {channels} ch / {bits} bit. Re-run extract.");

                var samples = new float[size / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(body.Slice(i * 2, 2)) / 32768f;
                return (samples, sampleRate);
            }

            pos += 8 + size + (size & 1);
        }

        throw new PipelineException($"{path} has no audio data.");
    }
}

using System.Numerics;

namespace Highlights.Core.Stages.Signals;

/// <summary>
/// YAMNet front end (tensorflow/models research/audioset/yamnet): 16 kHz, 25 ms periodic Hann window,
/// 10 ms hop, 512-point FFT magnitude, 64 mel bands 125–7500 Hz (HTK mel, TF weight matrix), log(mel + 0.001).
/// </summary>
internal static class LogMelSpectrogram
{
    public const int SampleRate = 16_000;
    public const int WindowLength = 400;
    public const int HopLength = 160;
    public const int FftLength = 512;
    public const int MelBands = 64;
    public const double FrameSeconds = HopLength / (double)SampleRate;

    private const int SpectrumBins = FftLength / 2 + 1;
    private const double LowerHz = 125, UpperHz = 7500, LogOffset = 0.001;

    private static readonly float[] Window = CreateWindow();
    private static readonly float[,] MelWeights = CreateMelWeights();

    /// <summary>Returns log-mel frames, row-major [frames, 64].</summary>
    public static float[] Compute(float[] samples, out int frames)
    {
        frames = samples.Length < WindowLength ? 0 : 1 + (samples.Length - WindowLength) / HopLength;
        var result = new float[frames * MelBands];
        var frameCount = frames;

        Parallel.For(0, frameCount, () => new Complex[FftLength], (f, _, buffer) =>
        {
            var offset = f * HopLength;
            for (var i = 0; i < FftLength; i++)
                buffer[i] = i < WindowLength ? new Complex(samples[offset + i] * Window[i], 0) : Complex.Zero;
            Fft.Forward(buffer);

            var row = result.AsSpan(f * MelBands, MelBands);
            for (var b = 0; b < SpectrumBins; b++)
            {
                var magnitude = (float)buffer[b].Magnitude;
                if (magnitude == 0)
                    continue;
                for (var m = 0; m < MelBands; m++)
                    row[m] += magnitude * MelWeights[b, m];
            }
            for (var m = 0; m < MelBands; m++)
                row[m] = (float)Math.Log(row[m] + LogOffset);
            return buffer;
        }, _ => { });

        return result;
    }

    private static float[] CreateWindow()
    {
        // Periodic Hann, as tf.signal.hann_window(periodic=True).
        var w = new float[WindowLength];
        for (var i = 0; i < WindowLength; i++)
            w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / WindowLength));
        return w;
    }

    /// <summary>Port of tf.signal.linear_to_mel_weight_matrix (DC bin zeroed).</summary>
    private static float[,] CreateMelWeights()
    {
        static double HzToMel(double hz) => 1127.0 * Math.Log(1 + hz / 700.0);

        var weights = new float[SpectrumBins, MelBands];
        var nyquist = SampleRate / 2.0;
        var lowerMel = HzToMel(LowerHz);
        var upperMel = HzToMel(UpperHz);
        var edges = new double[MelBands + 2];
        for (var i = 0; i < edges.Length; i++)
            edges[i] = lowerMel + (upperMel - lowerMel) * i / (MelBands + 1);

        for (var b = 1; b < SpectrumBins; b++)
        {
            var mel = HzToMel(nyquist * b / (SpectrumBins - 1));
            for (var m = 0; m < MelBands; m++)
            {
                var lower = (mel - edges[m]) / (edges[m + 1] - edges[m]);
                var upper = (edges[m + 2] - mel) / (edges[m + 2] - edges[m + 1]);
                weights[b, m] = (float)Math.Max(0, Math.Min(lower, upper));
            }
        }
        return weights;
    }

    /// <summary>In-place iterative radix-2 FFT.</summary>
    private static class Fft
    {
        private static readonly Complex[] Twiddles = Enumerable.Range(0, FftLength / 2)
            .Select(k => Complex.FromPolarCoordinates(1, -2 * Math.PI * k / FftLength)).ToArray();

        public static void Forward(Complex[] data)
        {
            var n = data.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                var bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                    j ^= bit;
                j ^= bit;
                if (i < j)
                    (data[i], data[j]) = (data[j], data[i]);
            }

            for (var len = 2; len <= n; len <<= 1)
            {
                var half = len / 2;
                var step = n / len;
                for (var i = 0; i < n; i += len)
                for (var k = 0; k < half; k++)
                {
                    var t = Twiddles[k * step] * data[i + k + half];
                    data[i + k + half] = data[i + k] - t;
                    data[i + k] += t;
                }
            }
        }
    }
}

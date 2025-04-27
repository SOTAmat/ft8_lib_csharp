using System;
using System.Numerics;
using Ft8Lib.Fft;

namespace Ft8Lib.Common
{
    /// <summary>
    /// Provides signal processing utilities for FT8/FT4 decoding
    /// </summary>
    public static class SignalProcessing
    {
        /// <summary>
        /// Compute power of complex values: |z|^2
        /// </summary>
        /// <param name="buffer">Complex buffer</param>
        /// <param name="output">Output power values (|z|^2)</param>
        /// <param name="length">Number of samples to process</param>
        public static void ComputePower(Complex[] buffer, float[] output, int length)
        {
            for (int i = 0; i < length; i++)
            {
                output[i] = (float)(buffer[i].Real * buffer[i].Real + buffer[i].Imaginary * buffer[i].Imaginary);
            }
        }

        /// <summary>
        /// Apply Hann window to the signal
        /// </summary>
        /// <param name="buffer">Signal buffer</param>
        /// <param name="length">Number of samples to process</param>
        public static void ApplyHannWindow(float[] buffer, int length)
        {
            for (int i = 0; i < length; i++)
            {
                // Hann window: 0.5 * (1 - cos(2π*n/(N-1)))
                float window = 0.5f * (1.0f - (float)Math.Cos(2.0 * Math.PI * i / (length - 1)));
                buffer[i] *= window;
            }
        }

        /// <summary>
        /// Calculate the logarithm (base 10) of the spectrum
        /// </summary>
        /// <param name="buffer">Spectrum buffer</param>
        /// <param name="length">Number of samples to process</param>
        /// <param name="offset">Offset to add before taking log (to avoid log(0))</param>
        public static void LogSpectrum(float[] buffer, int length, float offset = 1e-6f)
        {
            for (int i = 0; i < length; i++)
            {
                buffer[i] = (float)Math.Log10(buffer[i] + offset);
            }
        }

        /// <summary>
        /// Normalize a buffer to have zero mean and unit variance
        /// </summary>
        /// <param name="buffer">Buffer to normalize</param>
        /// <param name="length">Number of samples to process</param>
        public static void Normalize(float[] buffer, int length)
        {
            // Calculate mean
            float mean = 0;
            for (int i = 0; i < length; i++)
            {
                mean += buffer[i];
            }
            mean /= length;

            // Calculate standard deviation
            float stdDev = 0;
            for (int i = 0; i < length; i++)
            {
                float diff = buffer[i] - mean;
                stdDev += diff * diff;
            }
            stdDev = (float)Math.Sqrt(stdDev / length);

            // Normalize
            if (stdDev > 1e-10f)
            {
                for (int i = 0; i < length; i++)
                {
                    buffer[i] = (buffer[i] - mean) / stdDev;
                }
            }
            else
            {
                // If stdDev is too small, just center the data
                for (int i = 0; i < length; i++)
                {
                    buffer[i] -= mean;
                }
            }
        }

        /// <summary>
        /// Find the sub-bin maximum using quadratic interpolation
        /// </summary>
        /// <param name="y1">Value at x-1</param>
        /// <param name="y2">Value at x</param>
        /// <param name="y3">Value at x+1</param>
        /// <returns>The fractional offset of the peak from index x</returns>
        public static float QuadraticInterpolation(float y1, float y2, float y3)
        {
            // Finds the fractional offset d of the peak from the central value
            // using the formula: d = 0.5 * (y1 - y3) / (y1 - 2*y2 + y3)

            float d = 0;
            float denominator = 2.0f * y2 - y1 - y3;

            if (Math.Abs(denominator) > 1e-10f)
            {
                d = 0.5f * (y1 - y3) / denominator;

                // Limit to reasonable range to avoid numerical issues
                if (d < -0.5f) d = -0.5f;
                if (d > 0.5f) d = 0.5f;
            }

            return d;
        }

        /// <summary>
        /// Find the maximum value in a buffer
        /// </summary>
        /// <param name="buffer">Buffer to search</param>
        /// <param name="length">Number of samples to process</param>
        /// <param name="maxIndex">Output: index of the maximum value</param>
        /// <returns>The maximum value found</returns>
        public static float FindMaximum(float[] buffer, int length, out int maxIndex)
        {
            maxIndex = 0;
            float maxValue = buffer[0];

            for (int i = 1; i < length; i++)
            {
                if (buffer[i] > maxValue)
                {
                    maxValue = buffer[i];
                    maxIndex = i;
                }
            }

            return maxValue;
        }

        /// <summary>
        /// Create a spectrogram from time-domain samples
        /// </summary>
        /// <param name="samples">Time-domain samples</param>
        /// <param name="sampleCount">Number of samples</param>
        /// <param name="fftSize">FFT size</param>
        /// <param name="stepSize">Step size between consecutive FFT windows</param>
        /// <param name="spectrogram">Output spectrogram (time x frequency)</param>
        /// <param name="timeBins">Number of time bins (output)</param>
        /// <param name="freqBins">Number of frequency bins (output)</param>
        public static void CreateSpectrogram(
            float[] samples, int sampleCount, int fftSize, int stepSize,
            float[,] spectrogram, out int timeBins, out int freqBins)
        {
            // Create FFT configuration
            var fftConfig = new KissFFTConfig(fftSize, false);

            // Calculate the number of time and frequency bins
            timeBins = (sampleCount - fftSize) / stepSize + 1;
            freqBins = fftSize / 2 + 1;

            // Temporary buffers
            float[] windowBuffer = new float[fftSize];
            Complex[] fftOutput = new Complex[freqBins];

            // Process each time bin
            for (int t = 0; t < timeBins; t++)
            {
                int startSample = t * stepSize;

                // Copy samples to window buffer
                Array.Copy(samples, startSample, windowBuffer, 0, fftSize);

                // Apply window function
                ApplyHannWindow(windowBuffer, fftSize);

                // Perform real-to-complex FFT
                KissFFT.FFT_Real(fftConfig, windowBuffer, fftOutput);

                // Compute power spectrum
                for (int f = 0; f < freqBins; f++)
                {
                    // |z|^2
                    spectrogram[t, f] = (float)(fftOutput[f].Real * fftOutput[f].Real +
                                               fftOutput[f].Imaginary * fftOutput[f].Imaginary);
                }
            }
        }

        /// <summary>
        /// Convert a spectrogram to a logarithmic scale and normalize it
        /// </summary>
        /// <param name="spectrogram">Input/output spectrogram</param>
        /// <param name="timeBins">Number of time bins</param>
        /// <param name="freqBins">Number of frequency bins</param>
        public static void NormalizeSpectrogram(float[,] spectrogram, int timeBins, int freqBins)
        {
            // Convert to logarithmic scale
            for (int t = 0; t < timeBins; t++)
            {
                for (int f = 0; f < freqBins; f++)
                {
                    spectrogram[t, f] = (float)Math.Log10(spectrogram[t, f] + 1e-6f);
                }
            }

            // Normalize each frequency bin (column)
            for (int f = 0; f < freqBins; f++)
            {
                float mean = 0;
                float stdDev = 0;

                // Calculate mean
                for (int t = 0; t < timeBins; t++)
                {
                    mean += spectrogram[t, f];
                }
                mean /= timeBins;

                // Calculate standard deviation
                for (int t = 0; t < timeBins; t++)
                {
                    float diff = spectrogram[t, f] - mean;
                    stdDev += diff * diff;
                }
                stdDev = (float)Math.Sqrt(stdDev / timeBins);

                // Normalize
                if (stdDev > 1e-10f)
                {
                    for (int t = 0; t < timeBins; t++)
                    {
                        spectrogram[t, f] = (spectrogram[t, f] - mean) / stdDev;
                    }
                }
                else
                {
                    // If stdDev is too small, just center the data
                    for (int t = 0; t < timeBins; t++)
                    {
                        spectrogram[t, f] -= mean;
                    }
                }
            }
        }
    }
}
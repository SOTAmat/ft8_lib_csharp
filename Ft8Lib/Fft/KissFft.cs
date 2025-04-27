using System;
using System.Numerics;

namespace Ft8Lib.Fft
{
    /// <summary>
    /// Kiss FFT configuration
    /// </summary>
    public class KissFFTConfig
    {
        /// <summary>
        /// FFT size
        /// </summary>
        public int Size { get; }

        /// <summary>
        /// Whether this is an inverse FFT
        /// </summary>
        public bool Inverse { get; }

        /// <summary>
        /// Twiddle factors for the FFT
        /// </summary>
        public Complex[] Twiddles { get; }

        /// <summary>
        /// Factor array for the FFT
        /// </summary>
        public int[] Factors { get; }

        /// <summary>
        /// Creates a new FFT configuration
        /// </summary>
        /// <param name="size">FFT size (should be a power of 2 for efficiency)</param>
        /// <param name="inverse">Whether this is an inverse FFT</param>
        public KissFFTConfig(int size, bool inverse)
        {
            Size = size;
            Inverse = inverse;
            Twiddles = new Complex[size];
            Factors = new int[64]; // More than enough for any reasonable factorization

            // Initialize twiddle factors
            InitTwiddles();

            // Factorize the size
            Factorize();
        }

        /// <summary>
        /// Initialize twiddle factors for the FFT
        /// </summary>
        private void InitTwiddles()
        {
            for (int i = 0; i < Size; i++)
            {
                double phase = -2.0 * Math.PI * i / Size * (Inverse ? -1 : 1);
                Twiddles[i] = new Complex(Math.Cos(phase), Math.Sin(phase));
            }
        }

        /// <summary>
        /// Factorize the FFT size
        /// </summary>
        private void Factorize()
        {
            int factorCount = 0;
            int n = Size;

            // Factor out powers of 4, 2, 3, 5
            while (n % 4 == 0)
            {
                Factors[factorCount++] = 4;
                n /= 4;
            }

            while (n % 2 == 0)
            {
                Factors[factorCount++] = 2;
                n /= 2;
            }

            if (n > 1)
            {
                // Factor out any remaining 3s
                while (n % 3 == 0)
                {
                    Factors[factorCount++] = 3;
                    n /= 3;
                }

                // Factor out any remaining 5s
                while (n % 5 == 0)
                {
                    Factors[factorCount++] = 5;
                    n /= 5;
                }

                // Any remaining primes
                int p = 7;
                while (n > 1)
                {
                    while (n % p != 0)
                    {
                        p += 2; // Only try odd numbers
                    }

                    Factors[factorCount++] = p;
                    n /= p;
                }
            }

            // Terminate with 0
            Factors[factorCount] = 0;
        }
    }

    /// <summary>
    /// A C# port of the KissFFT library for FFT operations
    /// </summary>
    public static class KissFFT
    {
        /// <summary>
        /// Perform a complex-to-complex FFT
        /// </summary>
        /// <param name="config">FFT configuration</param>
        /// <param name="input">Input complex array</param>
        /// <param name="output">Output complex array</param>
        public static void FFT(KissFFTConfig config, Complex[] input, Complex[] output)
        {
            if (input == null || output == null)
                throw new ArgumentNullException("Input or output array is null");

            if (input.Length < config.Size || output.Length < config.Size)
                throw new ArgumentException("Input or output array too small");

            // For in-place FFT, copy input to output
            if (input != output)
            {
                Array.Copy(input, output, config.Size);
            }

            // Perform the FFT
            FFT_Recursive(config, output, 0, 1, config.Factors, 0);

            // Scale if inverse
            if (config.Inverse)
            {
                for (int i = 0; i < config.Size; i++)
                {
                    output[i] /= config.Size;
                }
            }
        }

        /// <summary>
        /// Recursive FFT implementation
        /// </summary>
        private static void FFT_Recursive(KissFFTConfig config, Complex[] buffer, int startIndex, int stride, int[] factors, int factorIndex)
        {
            int p = factors[factorIndex];

            if (p == 0)
            {
                // Base case
                return;
            }

            if (p == 2)
            {
                // Radix-2 butterfly
                int k0 = startIndex;
                int k1 = startIndex + stride;

                Complex tmp = buffer[k1];
                buffer[k1] = buffer[k0] - tmp;
                buffer[k0] = buffer[k0] + tmp;

                return;
            }

            int m = config.Size / p;

            // Process each stage
            for (int u = 0; u < m; u++)
            {
                int k = u;
                int k1 = u;
                int k2 = u;

                for (int q1 = 1; q1 < p; q1++)
                {
                    k1 += m;
                    k2 += m;

                    // Butterfly operation
                    Complex t = buffer[k1] * config.Twiddles[k2];
                    buffer[k1] = buffer[k] - t;
                    buffer[k] = buffer[k] + t;
                }
            }

            // Recursively process each sub-FFT
            for (int q = 0; q < p; q++)
            {
                FFT_Recursive(config, buffer, startIndex + q * m * stride, stride * p, factors, factorIndex + 1);
            }
        }

        /// <summary>
        /// Perform a real-to-complex forward FFT
        /// </summary>
        /// <param name="config">FFT configuration (should be for forward FFT)</param>
        /// <param name="input">Real input array (length = config.Size)</param>
        /// <param name="output">Complex output array (length = config.Size/2 + 1)</param>
        public static void FFT_Real(KissFFTConfig config, float[] input, Complex[] output)
        {
            if (input == null || output == null)
                throw new ArgumentNullException("Input or output array is null");

            if (input.Length < config.Size || output.Length < config.Size / 2 + 1)
                throw new ArgumentException("Input or output array too small");

            // Pack the real input into complex buffer
            Complex[] buffer = new Complex[config.Size];
            for (int i = 0; i < config.Size; i++)
            {
                buffer[i] = new Complex(input[i], 0);
            }

            // Perform complex FFT
            FFT(config, buffer, buffer);

            // Extract the positive frequencies
            output[0] = new Complex(buffer[0].Real, 0);

            // Handle the Nyquist frequency for even-length FFTs
            if (config.Size % 2 == 0)
            {
                output[config.Size / 2] = new Complex(buffer[config.Size / 2].Real, 0);
            }

            // Copy the remaining positive frequencies
            for (int i = 1; i < config.Size / 2; i++)
            {
                output[i] = buffer[i];
            }
        }

        /// <summary>
        /// Perform a complex-to-real inverse FFT
        /// </summary>
        /// <param name="config">FFT configuration (should be for inverse FFT)</param>
        /// <param name="input">Complex input array (length = config.Size/2 + 1)</param>
        /// <param name="output">Real output array (length = config.Size)</param>
        public static void IFFT_Real(KissFFTConfig config, Complex[] input, float[] output)
        {
            if (input == null || output == null)
                throw new ArgumentNullException("Input or output array is null");

            if (input.Length < config.Size / 2 + 1 || output.Length < config.Size)
                throw new ArgumentException("Input or output array too small");

            // Prepare the complex buffer
            Complex[] buffer = new Complex[config.Size];

            // Copy the positive frequencies
            buffer[0] = input[0];

            // Handle the Nyquist frequency for even-length FFTs
            if (config.Size % 2 == 0)
            {
                buffer[config.Size / 2] = input[config.Size / 2];
            }

            // Copy the remaining positive frequencies and generate the negative frequencies
            for (int i = 1; i < config.Size / 2; i++)
            {
                buffer[i] = input[i];
                buffer[config.Size - i] = Complex.Conjugate(input[i]);
            }

            // Perform complex inverse FFT
            FFT(config, buffer, buffer);

            // Extract the real part for output
            for (int i = 0; i < config.Size; i++)
            {
                output[i] = (float)buffer[i].Real;
            }
        }
    }
}
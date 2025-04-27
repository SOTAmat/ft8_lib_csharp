using System;
using System.Collections.Generic;
using System.IO;
using Ft8Lib.Ft8;
using Ft8Lib.Common;

namespace Ft8Lib.Demo
{
    /// <summary>
    /// Demo application for decoding FT8 signals from a WAV file
    /// </summary>
    public class DecodeFt8
    {
        /// <summary>
        /// Main entry point for the FT8 decoder demo
        /// </summary>
        /// <param name="args">Command line arguments</param>
        public static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: DecodeFt8 <wav_file> [--verbose]");
                Console.WriteLine("  wav_file:  Path to WAV file containing FT8 signals");
                Console.WriteLine("  --verbose: Print detailed decoding information");
                return 1;
            }

            string wavFilePath = args[0];
            bool verbose = args.Length > 1 && args[1] == "--verbose";

            // Check if file exists
            if (!File.Exists(wavFilePath))
            {
                Console.WriteLine($"Error: File '{wavFilePath}' not found.");
                return 1;
            }

            try
            {
                // Read WAV file
                Console.WriteLine($"Reading WAV file: {wavFilePath}");
                var wavFile = Wave.WavFile.ReadFromFile(wavFilePath);

                // Check sampling rate
                if (wavFile.SampleRate != 12000)
                {
                    Console.WriteLine($"Warning: Expected 12000 Hz sample rate, got {wavFile.SampleRate} Hz.");
                    Console.WriteLine("Decoding may not work correctly.");
                }

                // Create waterfall data from the audio
                var waterfall = CreateWaterfall(wavFile.Samples, wavFile.SampleRate, verbose);

                // Decode FT8 messages
                List<Decode.Candidate> candidates = Decode.DecodeFt8(waterfall, verbose);

                // Print results
                Console.WriteLine("\nDecoded messages:");
                Console.WriteLine("=================");

                if (candidates.Count == 0)
                {
                    Console.WriteLine("No messages found.");
                    return 0;
                }

                // Sort by score (descending)
                candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

                // Print decoded messages
                foreach (var candidate in candidates)
                {
                    float timeOffset = candidate.TimeOffset * Constants.FT8_SLOT_TIME;
                    float freqHz = candidate.FrequencyOffset;

                    Console.WriteLine($"{timeOffset:F1}s {freqHz:F1}Hz SNR:{candidate.Score:F1}dB: {candidate.Message}");

                    // Print additional message details if verbose
                    if (verbose && !candidate.Message.StartsWith("CRC check failed") && !candidate.Message.StartsWith("Invalid"))
                    {
                        try
                        {
                            var message = new Message(candidate.Message);
                            Console.WriteLine($"  Type: {message.Type}");

                            if (!string.IsNullOrEmpty(message.Callsign1))
                                Console.WriteLine($"  Callsign1: {message.Callsign1}");

                            if (!string.IsNullOrEmpty(message.Callsign2))
                                Console.WriteLine($"  Callsign2: {message.Callsign2}");

                            if (!string.IsNullOrEmpty(message.Grid))
                                Console.WriteLine($"  Grid: {message.Grid}");

                            if (!string.IsNullOrEmpty(message.Report))
                                Console.WriteLine($"  Report: {message.Report}");

                            Console.WriteLine();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  Error parsing message details: {ex.Message}");
                            Console.WriteLine();
                        }
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                if (verbose)
                {
                    Console.WriteLine(ex.StackTrace);
                }
                return 1;
            }
        }

        /// <summary>
        /// Create a waterfall from audio samples
        /// </summary>
        /// <param name="samples">Audio samples</param>
        /// <param name="sampleRate">Sample rate in Hz</param>
        /// <param name="verbose">Whether to print verbose information</param>
        /// <returns>Waterfall data for decoding</returns>
        private static Decode.Waterfall CreateWaterfall(float[] samples, int sampleRate, bool verbose)
        {
            if (verbose)
            {
                Console.WriteLine($"Creating waterfall from {samples.Length} samples at {sampleRate}Hz");
            }

            // Calculate FFT parameters
            int subBlockSize = 2048; // FFT size, must be a power of 2
            int step = 512;          // Step size between consecutive FFTs

            // Calculate the number of time and frequency bins
            int timeBins = (samples.Length - subBlockSize) / step + 1;
            int freqBins = subBlockSize / 2 + 1;

            if (verbose)
            {
                Console.WriteLine($"Waterfall dimensions: {timeBins} time bins x {freqBins} frequency bins");
            }

            // Create and initialize the spectrogram
            float[,] spectrogram = new float[timeBins, freqBins];
            int actualTimeBins, actualFreqBins;

            // Generate the spectrogram
            SignalProcessing.CreateSpectrogram(
                samples, samples.Length, subBlockSize, step,
                spectrogram, out actualTimeBins, out actualFreqBins
            );

            // Normalize the spectrogram
            SignalProcessing.NormalizeSpectrogram(spectrogram, actualTimeBins, actualFreqBins);

            // Create the waterfall object
            var waterfall = new Decode.Waterfall
            {
                Magnitudes = spectrogram,
                NumTimeBins = actualTimeBins,
                NumFreqBins = actualFreqBins,
                OversamplingRate = (float)sampleRate / (float)(subBlockSize / step),
                BinSize = (float)sampleRate / (float)subBlockSize
            };

            if (verbose)
            {
                Console.WriteLine($"Waterfall created: {waterfall.NumTimeBins} time bins x {waterfall.NumFreqBins} frequency bins");
                Console.WriteLine($"Bin size: {waterfall.BinSize:F2} Hz, Oversampling rate: {waterfall.OversamplingRate:F2}");
            }

            return waterfall;
        }
    }
}
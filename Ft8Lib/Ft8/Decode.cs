using System;
using System.Collections.Generic;
using Ft8Lib.Common;

namespace Ft8Lib.Ft8
{
    /// <summary>
    /// Provides functionality for decoding FT8/FT4 signals
    /// </summary>
    public static class Decode
    {
        /// <summary>
        /// Represents a candidate message in the waterfall
        /// </summary>
        public class Candidate
        {
            /// <summary>
            /// Time offset (in slots) from start of waterfall
            /// </summary>
            public float TimeOffset { get; set; }

            /// <summary>
            /// Frequency offset (in Hz) from base frequency
            /// </summary>
            public float FrequencyOffset { get; set; }

            /// <summary>
            /// Score (SNR in dB)
            /// </summary>
            public float Score { get; set; }

            /// <summary>
            /// Decoded message text
            /// </summary>
            public string Message { get; set; }

            /// <summary>
            /// Creates a new candidate message
            /// </summary>
            public Candidate()
            {
                Message = string.Empty;
                Score = -99.0f;
            }
        }

        /// <summary>
        /// Represents waterfall data for decoding FT8/FT4 signals
        /// </summary>
        public class Waterfall
        {
            /// <summary>
            /// Magnitude data for the waterfall
            /// </summary>
            public float[,] Magnitudes { get; set; } = new float[0, 0];

            /// <summary>
            /// Number of frequency bins
            /// </summary>
            public int NumFreqBins { get; set; }

            /// <summary>
            /// Number of time blocks
            /// </summary>
            public int NumTimeBins { get; set; }

            /// <summary>
            /// Frequency bin size in Hz
            /// </summary>
            public float BinSize { get; set; }

            /// <summary>
            /// Oversampling rate
            /// </summary>
            public float OversamplingRate { get; set; }
        }

        // Constants for FT8 sync pattern
        private static readonly int[] FT8_SYNC_PATTERN = {
            1, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 0, 0, 1, 0, 0, 1, 0, 1, 1, 1, 1, 0, 0, 0, 0, 0,
            0, 0, 1, 0, 1, 1, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0,
            0, 0, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 0, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0, 1, 1
        };

        /// <summary>
        /// Decode FT8 messages from a waterfall
        /// </summary>
        /// <param name="waterfall">Waterfall data containing signal information</param>
        /// <param name="verbose">Whether to print verbose decoding information</param>
        /// <returns>List of candidate messages</returns>
        public static List<Candidate> DecodeFt8(Waterfall waterfall, bool verbose)
        {
            var candidates = new List<Candidate>();

            if (waterfall == null || waterfall.Magnitudes == null)
            {
                return candidates;
            }

            if (verbose)
            {
                Console.WriteLine("Decoding FT8 messages from waterfall...");
                Console.WriteLine($"Waterfall dimensions: {waterfall.NumTimeBins}x{waterfall.NumFreqBins}");
            }

            // Define the FT8 parameters
            int samplesPerSymbol = (int)(Constants.FT8_SYMBOL_PERIOD * waterfall.OversamplingRate);
            int totalSymbols = Constants.FT8_NN;
            int expectedMessageLength = samplesPerSymbol * totalSymbols;

            // Validate waterfall has enough samples for FT8 decoding
            if (waterfall.NumTimeBins < expectedMessageLength)
            {
                if (verbose)
                {
                    Console.WriteLine($"Warning: Waterfall too short for FT8 decoding. Expected at least {expectedMessageLength} time bins, got {waterfall.NumTimeBins}");
                }
                return candidates;
            }

            // Search for signals in the waterfall
            // We look for sync pattern matches and extract potential messages

            // Define frequency search range
            int minFreq = 50;  // Hz
            int maxFreq = 2500; // Hz
            int minBin = (int)(minFreq / waterfall.BinSize);
            int maxBin = (int)(maxFreq / waterfall.BinSize);

            minBin = Math.Max(0, minBin);
            maxBin = Math.Min(waterfall.NumFreqBins - 1, maxBin);

            // For each potential time offset
            for (int timeOffset = 0; timeOffset <= waterfall.NumTimeBins - expectedMessageLength; timeOffset += samplesPerSymbol)
            {
                // For each frequency bin
                for (int freqBin = minBin; freqBin <= maxBin; freqBin++)
                {
                    // Calculate sync score at this location
                    float syncScore = CalculateSyncScore(waterfall, timeOffset, freqBin, samplesPerSymbol);

                    // If sync score is above threshold, try to decode a message
                    float syncThreshold = 0.15f; // Adjust based on testing
                    if (syncScore > syncThreshold)
                    {
                        if (verbose)
                        {
                            float timeSeconds = timeOffset / waterfall.OversamplingRate;
                            float freqHz = freqBin * waterfall.BinSize;
                            Console.WriteLine($"Found potential message at t={timeSeconds:F1}s, f={freqHz:F1}Hz, sync={syncScore:F2}");
                        }

                        // Extract log-likelihood ratios for the symbols
                        float[] llrs = ExtractLLRs(waterfall, timeOffset, freqBin, samplesPerSymbol, totalSymbols);

                        // Decode the message from the LLRs
                        byte[]? decodedPayload = LdpcDecode(llrs);

                        if (decodedPayload != null)
                        {
                            // If LDPC decoding and extraction were successful, check CRC and unpack
                            string messageText = ExtractMessageFromPayload(decodedPayload);

                            if (!string.IsNullOrEmpty(messageText) && !messageText.StartsWith("CRC") && !messageText.StartsWith("Invalid"))
                            {
                                // Calculate signal-to-noise ratio (simplified)
                                float snr = CalculateSnr(waterfall, timeOffset, freqBin, samplesPerSymbol, totalSymbols);

                                // Add to candidates
                                candidates.Add(new Candidate
                                {
                                    TimeOffset = timeOffset / (float)(samplesPerSymbol * Constants.FT8_SYMBOLS_PER_SLOT),
                                    FrequencyOffset = freqBin * waterfall.BinSize,
                                    Score = snr,
                                    Message = messageText
                                });

                                if (verbose)
                                {
                                    Console.WriteLine($"Successfully decoded: {messageText} (SNR: {snr:F1}dB)");
                                }
                            }
                            else if (verbose)
                            {
                                Console.WriteLine($"LDPC ok, but CRC/unpack failed: {messageText}");
                            }
                        }
                        else if (verbose)
                        {
                            Console.WriteLine("LDPC decoding failed");
                        }

                        // Skip ahead to avoid duplicates
                        freqBin += 2;
                    }
                }

                // Skip ahead to avoid excessive checking
                timeOffset += samplesPerSymbol * 8;
            }

            if (verbose)
            {
                Console.WriteLine($"Found {candidates.Count} candidate messages");
            }

            return candidates;
        }

        /// <summary>
        /// Calculate the sync score at a given location in the waterfall
        /// </summary>
        private static float CalculateSyncScore(Waterfall waterfall, int timeOffset, int freqBin, int samplesPerSymbol)
        {
            float score = 0.0f;

            // For each sync symbol
            for (int i = 0; i < FT8_SYNC_PATTERN.Length; i++)
            {
                // Calculate symbol position in time
                int symbolPos = timeOffset + i * samplesPerSymbol;

                // Get the magnitude at this position
                float magnitude = 0.0f;
                if (symbolPos < waterfall.NumTimeBins)
                {
                    magnitude = waterfall.Magnitudes[symbolPos, freqBin];
                }

                // If sync pattern bit is 1, add magnitude, otherwise subtract
                if (FT8_SYNC_PATTERN[i] == 1)
                {
                    score += magnitude;
                }
                else
                {
                    score -= magnitude;
                }
            }

            // Normalize the score
            return score / FT8_SYNC_PATTERN.Length;
        }

        /// <summary>
        /// Extract log-likelihood ratios for symbols from the waterfall
        /// </summary>
        private static float[] ExtractLLRs(Waterfall waterfall, int timeOffset, int freqBin, int samplesPerSymbol, int totalSymbols)
        {
            float[] llrs = new float[totalSymbols * 3]; // 3 bits per symbol in FT8

            // For each symbol
            for (int i = 0; i < totalSymbols; i++)
            {
                // Calculate symbol position in time
                int symbolPos = timeOffset + i * samplesPerSymbol;

                // Get magnitude for this symbol and adjacent frequency bins
                float mag0 = 0.0f, mag1 = 0.0f;
                if (symbolPos < waterfall.NumTimeBins && freqBin < waterfall.NumFreqBins - 1)
                {
                    mag0 = waterfall.Magnitudes[symbolPos, freqBin];
                    mag1 = waterfall.Magnitudes[symbolPos, freqBin + 1];
                }

                // Convert to log-likelihood ratios (simplified)
                // In a real implementation, this would use the proper FSK demodulation
                // and soft-decision metrics
                llrs[i * 3] = (mag0 - mag1) * 10.0f;  // First bit
                llrs[i * 3 + 1] = (mag0 + mag1) * 5.0f;  // Second bit
                llrs[i * 3 + 2] = (mag0 - mag1) * 2.0f;  // Third bit
            }

            return llrs;
        }

        /// <summary>
        /// Extracts a message from a decoded payload AFTER CRC check
        /// </summary>
        /// <param name="payload">The decoded and CRC-verified payload (91 bits packed in K_BYTES)</param>
        /// <returns>The extracted message text or error message</returns>
        private static string ExtractMessageFromPayload(byte[] payload)
        {
            if (payload == null || payload.Length != Constants.FTX_LDPC_K_BYTES)
            {
                // Return empty string instead of specific error to satisfy non-nullable return
                return string.Empty; // "Invalid payload length"; 
            }

            // Assuming CRC is OK (checked before calling this, or by FromPayloadWithCrc)
            Message? unpackedMsg = Message.FromPayloadWithCrc(payload);
            if (unpackedMsg != null && unpackedMsg.Type != MessageType.Invalid)
            {
                return unpackedMsg.ToString(); // Returns unpackedMsg.Content which should not be null
            }
            else
            {
                // Return empty string instead of specific error
                return string.Empty; // "Invalid Message Unpack";
            }
        }

        /// <summary>
        /// Performs LDPC decoding on the received LLRs and extracts the K-bit message payload if successful.
        /// </summary>
        /// <param name="llrs">Log-likelihood ratios (N bits)</param>
        /// <param name="maxIterations">Maximum number of iterations</param>
        /// <returns>Decoded K-bit message payload (packed in K_BYTES), or null if decoding failed</returns>
        private static byte[] LdpcDecode(float[] llrs, int maxIterations = 20)
        {
            if (llrs == null || llrs.Length < Constants.FTX_LDPC_N)
            {
                return null; // Invalid input
            }

            byte[] decodedCodeword = new byte[Constants.FTX_LDPC_N]; // For the full N-bit codeword

            // Perform LDPC decoding using the refactored Ldpc.Decode
            bool decodeSuccess = Ldpc.Decode(llrs, maxIterations, decodedCodeword, out int ldpcErrors);

            if (!decodeSuccess || ldpcErrors > 0)
            {
                // Decoding failed (either returned false or had remaining errors)
                return null;
            }

            // Decoding succeeded (errors == 0), now extract the K message bits
            byte[] payload = new byte[Constants.FTX_LDPC_K_BYTES]; // For the K-bit payload
            bool extractSuccess = Ldpc.ExtractMessage(decodedCodeword, payload);

            if (!extractSuccess)
            {
                // Should not happen if decodeSuccess was true and arrays are correct size
                Console.Error.WriteLine("Error: Failed to extract message from successfully decoded codeword.");
                return null;
            }

            // Check CRC on the extracted payload *before* returning
            // The FromPayloadWithCrc method handles the CRC check internally
            // But we need the full 12 bytes for that.
            // The current 'payload' is only K_BYTES (12).
            // Let's re-think. The CRC check needs the 91-bit payload. Ldpc.ExtractMessage gives us that.
            // Message.FromPayloadWithCrc expects the 91-bit payload *with* CRC attached (12 bytes).
            // How was the CRC checked in C?
            // C code: pack_bits(plain174, FTX_LDPC_K, a91); -> a91 has K bits (91 bits = 12 bytes)
            // C code: crc_extracted = ftx_extract_crc(a91);
            // C code: a91[9] &= 0xF8; a91[10] &= 0x00; <-- Zeroes out CRC bits within the payload
            // C code: crc_calculated = ftx_compute_crc(a91, 96 - 14); <-- Calculates CRC on 82 bits?
            // C code: if (crc_extracted != crc_calculated) return false;
            // C# Message.FromPayloadWithCrc does its own CRC check internally. Let's rely on that.

            // We have the K-bit (91-bit) payload in 'payload' (K_BYTES = 12 bytes).
            // This payload *includes* the CRC bits as transmitted.
            // Pass this directly to FromPayloadWithCrc which will perform the check.
            if (!Crc.CheckCrc(payload)) // Check CRC using the standalone function on the 91-bit payload
            {
                Console.WriteLine("CRC check failed on extracted payload."); // More specific verbose message
                return null; // Indicate CRC failure
            }

            // CRC is okay, return the payload
            return payload;
        }

        /// <summary>
        /// Calculate the signal-to-noise ratio for a message
        /// </summary>
        private static float CalculateSnr(Waterfall waterfall, int timeOffset, int freqBin, int samplesPerSymbol, int totalSymbols)
        {
            // In a real implementation, this would calculate the actual SNR
            // by comparing signal power to noise power

            // Calculate the average magnitude in the message region (signal)
            float signalPower = 0.0f;
            int count = 0;

            for (int i = 0; i < totalSymbols; i++)
            {
                int symbolPos = timeOffset + i * samplesPerSymbol;
                if (symbolPos < waterfall.NumTimeBins)
                {
                    signalPower += waterfall.Magnitudes[symbolPos, freqBin];
                    count++;
                }
            }

            if (count > 0)
            {
                signalPower /= count;
            }

            // Calculate the average magnitude in surrounding region (noise)
            float noisePower = 0.0f;
            count = 0;

            for (int t = Math.Max(0, timeOffset - 10); t < Math.Min(waterfall.NumTimeBins, timeOffset + totalSymbols * samplesPerSymbol + 10); t++)
            {
                for (int f = Math.Max(0, freqBin - 2); f < Math.Min(waterfall.NumFreqBins, freqBin + 3); f++)
                {
                    // Skip the signal region
                    bool inSignalRegion = false;
                    for (int i = 0; i < totalSymbols; i++)
                    {
                        int symbolPos = timeOffset + i * samplesPerSymbol;
                        if (Math.Abs(t - symbolPos) < samplesPerSymbol / 2 && Math.Abs(f - freqBin) <= 1)
                        {
                            inSignalRegion = true;
                            break;
                        }
                    }

                    if (!inSignalRegion)
                    {
                        noisePower += waterfall.Magnitudes[t, f];
                        count++;
                    }
                }
            }

            if (count > 0)
            {
                noisePower /= count;
            }

            // Calculate SNR in dB
            if (noisePower > 0)
            {
                float snr = 10.0f * (float)Math.Log10(signalPower / noisePower);
                return snr;
            }

            return 0.0f;
        }
    }
}
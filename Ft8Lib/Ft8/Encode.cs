using System;

namespace Ft8Lib.Ft8
{
    /// <summary>
    /// Implements encoding functions for FT8 and FT4 protocols
    /// </summary>
    public static class Encode
    {
        /// <summary>
        /// Encodes an FT8 message payload into tone symbols
        /// </summary>
        /// <param name="payload">Input array with 77 message bits (10 bytes)</param>
        /// <param name="tones">Output array of FT8_NUM_SYMBOLS (79) tone values (0-7)</param>
        public static void EncodeFt8(byte[] payload, byte[] tones)
        {
            if (payload == null || tones == null ||
                payload.Length < 10 ||
                tones.Length < Constants.FT8_NUM_SYMBOLS)
            {
                throw new ArgumentException("Invalid buffer sizes for FT8 encoding");
            }

            byte[] a91 = new byte[Constants.FTX_LDPC_K_BYTES]; // Store 77 bits of payload + 14 bits CRC

            // Compute and add CRC at the end of the message
            // a91 contains 77 bits of payload + 14 bits of CRC
            Crc.AppendCrc(payload, Constants.FTX_LDPC_K - 14, a91);

            byte[] codeword = new byte[Constants.FTX_LDPC_N_BYTES];
            Ldpc.Encode(a91, codeword);

            // Message structure: S7 D29 S7 D29 S7
            // Total symbols: 79 (FT8_NUM_SYMBOLS)

            byte mask = 0x80; // Mask to extract 1 bit from codeword
            int byteIdx = 0;  // Index of the current byte of the codeword

            for (int toneIdx = 0; toneIdx < Constants.FT8_NUM_SYMBOLS; ++toneIdx)
            {
                if (toneIdx >= 0 && toneIdx < 7)
                {
                    tones[toneIdx] = Constants.Ft8CostasPattern[toneIdx];
                }
                else if (toneIdx >= 36 && toneIdx < 43)
                {
                    tones[toneIdx] = Constants.Ft8CostasPattern[toneIdx - 36];
                }
                else if (toneIdx >= 72 && toneIdx < 79)
                {
                    tones[toneIdx] = Constants.Ft8CostasPattern[toneIdx - 72];
                }
                else
                {
                    // Extract 3 bits from codeword at i-th position
                    byte bits3 = 0;

                    if ((codeword[byteIdx] & mask) != 0)
                        bits3 |= 4;
                    if (0 == (mask >>= 1))
                    {
                        mask = 0x80;
                        byteIdx++;
                    }

                    if ((codeword[byteIdx] & mask) != 0)
                        bits3 |= 2;
                    if (0 == (mask >>= 1))
                    {
                        mask = 0x80;
                        byteIdx++;
                    }

                    if ((codeword[byteIdx] & mask) != 0)
                        bits3 |= 1;
                    if (0 == (mask >>= 1))
                    {
                        mask = 0x80;
                        byteIdx++;
                    }

                    tones[toneIdx] = Constants.Ft8GrayMap[bits3];
                }
            }
        }

        /// <summary>
        /// Encodes an FT4 message payload into tone symbols
        /// </summary>
        /// <param name="payload">Input array with 77 message bits (10 bytes)</param>
        /// <param name="tones">Output array of FT4_NUM_SYMBOLS (105) tone values (0-3)</param>
        public static void EncodeFt4(byte[] payload, byte[] tones)
        {
            if (payload == null || tones == null ||
                payload.Length < 10 ||
                tones.Length < Constants.FT4_NUM_SYMBOLS)
            {
                throw new ArgumentException("Invalid buffer sizes for FT4 encoding");
            }

            byte[] a91 = new byte[Constants.FTX_LDPC_K_BYTES]; // Store 77 bits of payload + 14 bits CRC
            byte[] payloadXor = new byte[10];                  // Encoded payload data

            // For FT4 only, to avoid transmitting a long string of zeros when sending CQ messages,
            // the assembled 77-bit message is bitwise exclusive-OR'ed with a pseudorandom sequence
            // before computing the CRC and FEC parity bits
            for (int i = 0; i < 10; ++i)
            {
                payloadXor[i] = (byte)(payload[i] ^ Constants.Ft4XorSequence[i]);
            }

            // Compute and add CRC at the end of the message
            // a91 contains 77 bits of payload + 14 bits of CRC
            Crc.AppendCrc(payloadXor, Constants.FTX_LDPC_K - 14, a91);

            byte[] codeword = new byte[Constants.FTX_LDPC_N_BYTES];
            Ldpc.Encode(a91, codeword);

            // Message structure: R S4_1 D29 S4_2 D29 S4_3 D29 S4_4 R
            // Total symbols: 105 (FT4_NUM_SYMBOLS)

            byte mask = 0x80; // Mask to extract 1 bit from codeword
            int byteIdx = 0;  // Index of the current byte of the codeword

            for (int toneIdx = 0; toneIdx < Constants.FT4_NUM_SYMBOLS; ++toneIdx)
            {
                if (toneIdx == 0 || toneIdx == 104)
                {
                    tones[toneIdx] = 0; // R (ramp) symbol
                }
                else if (toneIdx >= 1 && toneIdx < 5)
                {
                    tones[toneIdx] = Constants.Ft4CostasPattern[0][toneIdx - 1];
                }
                else if (toneIdx >= 34 && toneIdx < 38)
                {
                    tones[toneIdx] = Constants.Ft4CostasPattern[1][toneIdx - 34];
                }
                else if (toneIdx >= 67 && toneIdx < 71)
                {
                    tones[toneIdx] = Constants.Ft4CostasPattern[2][toneIdx - 67];
                }
                else if (toneIdx >= 100 && toneIdx < 104)
                {
                    tones[toneIdx] = Constants.Ft4CostasPattern[3][toneIdx - 100];
                }
                else
                {
                    // Extract 2 bits from codeword at i-th position
                    byte bits2 = 0;

                    if ((codeword[byteIdx] & mask) != 0)
                        bits2 |= 2;
                    if (0 == (mask >>= 1))
                    {
                        mask = 0x80;
                        byteIdx++;
                    }

                    if ((codeword[byteIdx] & mask) != 0)
                        bits2 |= 1;
                    if (0 == (mask >>= 1))
                    {
                        mask = 0x80;
                        byteIdx++;
                    }

                    tones[toneIdx] = Constants.Ft4GrayMap[bits2];
                }
            }
        }

        /// <summary>
        /// Synthesizes GFSK pulse shapes for signal modulation
        /// </summary>
        /// <param name="nSpsym">Number of samples per symbol</param>
        /// <param name="symbolBt">Symbol smoothing filter bandwidth factor (BT)</param>
        /// <param name="pulse">Output array of pulse samples (must have space for 3*n_spsym elements)</param>
        public static void GfskPulse(int nSpsym, float symbolBt, float[] pulse)
        {
            for (int i = 0; i < 3 * nSpsym; ++i)
            {
                float t = i / (float)nSpsym - 1.5f;
                float arg1 = Constants.GFSK_CONST_K * symbolBt * (t + 0.5f);
                float arg2 = Constants.GFSK_CONST_K * symbolBt * (t - 0.5f);
                pulse[i] = (Erf(arg1) - Erf(arg2)) / 2;
            }
        }

        /// <summary>
        /// Synthesizes waveform data using GFSK phase shaping
        /// </summary>
        /// <param name="symbols">Array of symbols (tones) (0-7 for FT8, 0-3 for FT4)</param>
        /// <param name="nSym">Number of symbols in the symbol array</param>
        /// <param name="f0">Audio frequency in Hertz for the symbol 0 (base frequency)</param>
        /// <param name="symbolBt">Symbol smoothing filter bandwidth (2 for FT8, 1 for FT4)</param>
        /// <param name="symbolPeriod">Symbol period (duration), seconds</param>
        /// <param name="sampleRate">Sample rate of synthesized signal, Hertz</param>
        /// <param name="signal">Output array of signal waveform samples (should have space for n_sym*n_spsym samples)</param>
        public static void SynthesizeGfsk(byte[] symbols, int nSym, float f0, float symbolBt, float symbolPeriod, int sampleRate, float[] signal)
        {
            int nSpsym = (int)(0.5f + sampleRate * symbolPeriod); // Samples per symbol
            int nWave = nSym * nSpsym;                           // Number of output samples
            float hmod = 1.0f;

            // Compute the smoothed frequency waveform.
            // Length = (nsym+2)*n_spsym samples, first and last symbols extended
            float dphiPeak = 2 * (float)Math.PI * hmod / nSpsym;
            float[] dphi = new float[nWave + 2 * nSpsym];

            // Shift frequency up by f0
            for (int i = 0; i < nWave + 2 * nSpsym; ++i)
            {
                dphi[i] = 2 * (float)Math.PI * f0 / sampleRate;
            }

            float[] pulse = new float[3 * nSpsym];
            GfskPulse(nSpsym, symbolBt, pulse);

            for (int i = 0; i < nSym; ++i)
            {
                int ib = i * nSpsym;
                for (int j = 0; j < 3 * nSpsym; ++j)
                {
                    dphi[j + ib] += dphiPeak * symbols[i] * pulse[j];
                }
            }

            // Add dummy symbols at beginning and end with tone values equal to 1st and last symbol, respectively
            for (int j = 0; j < 2 * nSpsym; ++j)
            {
                dphi[j] += dphiPeak * pulse[j + nSpsym] * symbols[0];
                dphi[j + nSym * nSpsym] += dphiPeak * pulse[j] * symbols[nSym - 1];
            }

            // Calculate and insert the audio waveform
            float phi = 0;
            for (int k = 0; k < nWave; ++k)
            { // Don't include dummy symbols
                signal[k] = (float)Math.Sin(phi);
                phi = (float)Math.IEEERemainder(phi + dphi[k + nSpsym], 2 * Math.PI);
            }

            // Apply envelope shaping to the first and last symbols
            int nRamp = nSpsym / 8;
            for (int i = 0; i < nRamp; ++i)
            {
                float env = (1 - (float)Math.Cos(2 * Math.PI * i / (2 * nRamp))) / 2;
                signal[i] *= env;
                signal[nWave - 1 - i] *= env;
            }
        }

        /// <summary>
        /// Error function approximation (equivalent to C erff function)
        /// </summary>
        /// <param name="x">Input value</param>
        /// <returns>Approximated erf value</returns>
        private static float Erf(float x)
        {
            // Constants for approximation
            float a1 = 0.254829592f;
            float a2 = -0.284496736f;
            float a3 = 1.421413741f;
            float a4 = -1.453152027f;
            float a5 = 1.061405429f;
            float p = 0.3275911f;

            // Save the sign of x
            int sign = x < 0 ? -1 : 1;
            x = Math.Abs(x);

            // A&S formula 7.1.26
            float t = 1.0f / (1.0f + p * x);
            float y = 1.0f - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * (float)Math.Exp(-x * x);

            return sign * y;
        }
    }
}
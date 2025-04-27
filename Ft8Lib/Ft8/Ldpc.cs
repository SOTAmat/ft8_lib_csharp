using System;
using System.Linq;

namespace Ft8Lib.Ft8
{
    /// <summary>
    /// Provides LDPC (Low-Density Parity-Check) encoding and decoding functions for FT8/FT4 protocols
    /// </summary>
    public static class Ldpc
    {
        // LDPC matrix dimensions (using constants from Constants class)
        private const int N = Constants.FTX_LDPC_N;             // Number of columns in H (Codeword bits)
        private const int K = Constants.FTX_LDPC_K;             // Number of payload bits (including CRC)
        private const int M = Constants.FTX_LDPC_M;             // Number of parity bits
        private const int K_BYTES = Constants.FTX_LDPC_K_BYTES; // Bytes for K bits
        private const int N_BYTES = Constants.FTX_LDPC_N_BYTES; // Bytes for N bits

        // LDPC tables (Parity check related - used for decoding)
        private static readonly int[] Nr;          // Number of 1's per row
        private static readonly int[] Nc;          // Number of 1's per column
        private static readonly int[,] Nm;        // Column indexes for row number i
        private static readonly int[,] Mn;        // Row indexes for column number j

        /// <summary>
        /// Static constructor to initialize LDPC tables
        /// </summary>
        static Ldpc()
        {
            // Initialize the LDPC matrix data from the LdpcMatrix class
            Nr = LdpcMatrix.GetNr();
            Nc = LdpcMatrix.GetNc();
            Nm = LdpcMatrix.GetNm();
            Mn = LdpcMatrix.GetMn();
        }

        /// <summary>
        /// Returns 1 if an odd number of bits are set in x, zero otherwise.
        /// Equivalent to C parity8 function.
        /// </summary>
        /// <param name="x">Byte value</param>
        /// <returns>1 if parity is odd, 0 otherwise</returns>
        private static byte Parity8(byte x)
        {
            x ^= (byte)(x >> 4); // a b c d ae bf cg dh
            x ^= (byte)(x >> 2); // a b ac bd cae dbf aecg bfdh
            x ^= (byte)(x >> 1); // a ab bac acbd bdcae caedbf aecgbfdh
            return (byte)(x % 2); // modulo 2
        }

        /// <summary>
        /// Encode via LDPC a 91-bit message and return a 174-bit codeword.
        /// The generator matrix has dimensions (83, 91 bits = 12 bytes).
        /// The code is a (174,91) regular LDPC code with column weight 3.
        /// Equivalent to C encode174 function.
        /// </summary>
        /// <param name="message">Array of 91 bits stored as 12 bytes (MSB first)</param>
        /// <param name="codeword">Array of 174 bits stored as 22 bytes (MSB first)</param>
        public static void Encode(byte[] message, byte[] codeword)
        {
            if (message == null || codeword == null || message.Length < K_BYTES || codeword.Length < N_BYTES)
            {
                throw new ArgumentException($"Invalid buffer sizes for LDPC encoding. Need {K_BYTES} bytes for message and {N_BYTES} bytes for codeword.");
            }

            // This implementation accesses the generator bits straight from the packed binary representation
            // in Constants.FtXLDPCGenerator

            // Fill the codeword with message and zeros, as we will only update binary ones later
            for (int j = 0; j < N_BYTES; ++j)
            {
                codeword[j] = (j < K_BYTES) ? message[j] : (byte)0;
            }

            // Compute the byte index and bit mask for the first checksum bit
            // K is 91 bits
            byte colMask = (byte)(0x80u >> (K % 8)); // bitmask of current byte (start at bit K)
            int colIdx = K_BYTES - 1;               // index into byte array (start at byte containing bit K)

            // Compute the LDPC checksum bits (M=83) and store them in codeword
            for (int i = 0; i < M; ++i)
            {
                // Fast implementation of bitwise multiplication and parity checking
                // Normally nsum would contain the result of dot product between message and FtXLDPCGenerator[i],
                // but we only compute the sum modulo 2.
                byte nsum = 0;
                for (int j = 0; j < K_BYTES; ++j)
                {
                    byte bits = (byte)(message[j] & Constants.FtXLDPCGenerator[i][j]); // bitwise AND (bitwise multiplication)
                    nsum ^= Parity8(bits);                                              // bitwise XOR (addition modulo 2)
                }

                // Set the current checksum bit in codeword if nsum is odd
                if ((nsum % 2) != 0)
                {
                    codeword[colIdx] |= colMask;
                }

                // Update the byte index and bit mask for the next checksum bit
                colMask >>= 1;
                if (colMask == 0)
                {
                    colMask = 0x80;
                    ++colIdx;
                }
            }
        }

        /// <summary>
        /// Decode a received codeword using LDPC Sum-Product algorithm
        /// Equivalent to C ldpc_decode function.
        /// </summary>
        /// <param name="llrInput">Log-likelihood ratios for received bits (N)</param>
        /// <param name="maxIterations">Maximum number of decoding iterations</param>
        /// <param name="decodedCodeword">Output for decoded codeword bits (N) - contains full 174 bits</param>
        /// <param name="syndromeErrors">Output: number of errors remaining after decoding (0 is success)</param>
        /// <returns>True if decoding resulted in 0 errors, false otherwise</returns>
        public static bool Decode(float[] llrInput, int maxIterations, byte[] decodedCodeword, out int syndromeErrors)
        {
            syndromeErrors = M; // Initialize with max possible errors

            if (llrInput == null || decodedCodeword == null || llrInput.Length < N || decodedCodeword.Length < N) // decodedCodeword now needs N length
            {
                return false;
            }

            // Implementation of ldpc_decode (Sum-Product) from ldpc.c

            // Messages from check nodes to variable nodes (e[m][n] in C)
            // Corresponds to E_j(i) in Johnson's book notation
            float[,] e = new float[M, N];

            // Messages from variable nodes to check nodes (m[m][n] in C)
            // Corresponds to L_i(j) in Johnson's book notation
            // Initialized with channel LLRs
            float[,] m = new float[M, N];

            int minErrors = M;
            byte[] currentBestCodeword = new byte[N]; // Store the best full codeword found

            // Initialize messages m[j][i] = codeword[i] for all j
            // Initialize messages e[j][i] = 0.0f for all j, i
            for (int j = 0; j < M; j++)
            {
                for (int i = 0; i < N; i++)
                {
                    m[j, i] = llrInput[i];
                    e[j, i] = 0.0f;
                }
            }

            // Iterative Sum-Product algorithm
            for (int iter = 0; iter < maxIterations; ++iter)
            {
                // Update messages from check nodes to variable nodes (e)
                for (int j = 0; j < M; j++) // Iterate over check nodes
                {
                    // For each variable node i1 connected to check node j
                    for (int ii1 = 0; ii1 < Nr[j]; ii1++)
                    {
                        int i1 = Nm[j, ii1]; // Note: C matrix is 1-based, ours is 0-based
                        if (i1 < 0 || i1 >= N) continue; // Skip invalid indices (padding -1)

                        // Calculate the update message e[j][i1]
                        float a = 1.0f;
                        // Product part: iterate over variable nodes i2 connected to check node j (excluding i1)
                        for (int ii2 = 0; ii2 < Nr[j]; ii2++)
                        {
                            int i2 = Nm[j, ii2];
                            if (i2 < 0 || i2 >= N) continue; // Skip invalid indices

                            if (i2 != i1)
                            {
                                // Use the message m[j][i2] from variable i2 TO this check node j
                                // C code: a *= fast_tanh(-m[j][i2] / 2.0f);
                                // Note the sign difference: LLR = log(P0/P1), so we use tanh(m[j][i2] / 2.0f)
                                float m_ji2 = m[j, i2];
                                // Clamp before tanh like in bp_decode
                                if (m_ji2 > 20.0f) m_ji2 = 20.0f;
                                else if (m_ji2 < -20.0f) m_ji2 = -20.0f;
                                // Apply the negation as in the C code
                                a *= (float)Math.Tanh(-m_ji2 / 2.0f);
                            }
                        }
                        // Clamp product before Atanh
                        if (a > 0.999999f) a = 0.999999f;
                        else if (a < -0.999999f) a = -0.999999f;

                        // C code: e[j][i1] = -2.0f * fast_atanh(a);
                        // Corresponds to E_j(i) = 2 * atanh(product of tanh(L_i'(j)/2) for i' != i)
                        // Apply the -2.0 multiplier as in the C code
                        e[j, i1] = -2.0f * Atanh(a);
                    }
                }

                // Make a hard decision based on current LLR sums
                byte[] plain = new byte[N]; // Temporary array for hard decision
                for (int i = 0; i < N; i++) // Iterate over variable nodes
                {
                    float l = llrInput[i]; // Start with channel LLR
                    // Add messages e[j][i] from all check nodes j connected to variable node i
                    // C code uses fixed loop < 3, matching dimension of Mn. Nc[i] can be 4.
                    // Use fixed loop limit 3 to match C code and Mn dimension.
                    for (int ji1 = 0; ji1 < 3; ji1++) // Nc[i] should be 3 or 4 <-- Corrected loop limit
                    {
                        int j1 = Mn[i, ji1]; // Get connected check node j1
                        if (j1 < 0 || j1 >= M) continue; // Skip invalid index

                        l += e[j1, i];
                    }
                    // Hard decision: LLR > 0 means 0 is more likely, LLR < 0 means 1 is more likely
                    // C Code: plain[i] = (l > 0) ? 1 : 0; <-- Matches C code (but seems wrong based on LLR definition)
                    plain[i] = (l > 0) ? (byte)1 : (byte)0; // Corrected hard decision based on LLR definition -> Reverted to match C
                }

                // Check syndrome
                int errors = CalculateSyndrome(plain);

                if (errors < minErrors)
                {
                    minErrors = errors;
                    Array.Copy(plain, currentBestCodeword, N); // Save this potentially better codeword

                    if (errors == 0)
                    {
                        break; // Found a perfect answer
                    }
                }

                // If not done, update messages from variable nodes to check nodes (m)
                for (int i = 0; i < N; i++) // Iterate over variable nodes
                {
                    // For each check node j1 connected to variable node i
                    // C code uses fixed loop < 3, matching dimension of Mn. Nc[i] can be 4.
                    // Use fixed loop limit 3 to match C code and Mn dimension.
                    for (int ji1 = 0; ji1 < 3; ji1++) // <-- Corrected loop limit
                    {
                        int j1 = Mn[i, ji1]; // Get connected check node j1
                        if (j1 < 0 || j1 >= M) continue; // Skip invalid index

                        // Calculate message m[j1][i] from variable i TO check node j1
                        float l = llrInput[i]; // Start with channel LLR
                        // Sum messages e[j2][i] from all OTHER check nodes j2 connected to variable i
                        // C code uses fixed loop < 3, matching dimension of Mn. Nc[i] can be 4.
                        // Use fixed loop limit 3 to match C code and Mn dimension.
                        for (int ji2 = 0; ji2 < 3; ji2++) // <-- Corrected loop limit
                        {
                            if (ji1 != ji2) // Exclude the message from the target check node j1
                            {
                                int j2 = Mn[i, ji2]; // Get other connected check node j2
                                if (j2 < 0 || j2 >= M) continue; // Skip invalid index
                                l += e[j2, i];
                            }
                        }
                        m[j1, i] = l; // Update the message
                    }
                }
            } // End of iterations

            // Copy the best codeword found (or the last one if no perfect one was found)
            Array.Copy(currentBestCodeword, decodedCodeword, N);
            syndromeErrors = minErrors;

            return minErrors == 0;
        }

        /// <summary>
        /// Calculate the syndrome of a codeword (array of 0s and 1s)
        /// </summary>
        /// <param name="codeword">The 174-bit codeword (as byte array of 0s/1s)</param>
        /// <returns>Number of parity check errors (0 means success)</returns>
        public static int CalculateSyndrome(byte[] codeword)
        {
            if (codeword == null || codeword.Length < N)
            {
                return -1;
            }

            int errors = 0;

            for (int i = 0; i < M; i++)
            {
                byte sum = 0;
                for (int j = 0; j < Nr[i]; j++)
                {
                    int n = Nm[i, j];
                    if (n >= 0 && n < N) // Check bounds using N
                    {
                        sum ^= codeword[n];
                    }
                    else
                    {
                        // Handle potential invalid index from Nm if necessary (should not happen with valid matrix)
                        Console.Error.WriteLine($"Warning: Invalid index {n} in Nm[{i},{j}]");
                    }
                }

                if (sum != 0)
                {
                    errors++;
                }
            }

            return errors;
        }

        /// <summary>
        /// Calculates the inverse hyperbolic tangent (atanh) of a value
        /// </summary>
        /// <param name="x">Input value (-1 < x < 1)</param>
        /// <returns>The inverse hyperbolic tangent of x</returns>
        private static float Atanh(float x)
        {
            // Ensure x is in valid range, clamp near +/- 1 to avoid infinity/NaN
            if (x <= -1.0f) x = -0.999999f;
            if (x >= 1.0f) x = 0.999999f;

            // atanh(x) = 0.5 * ln((1 + x) / (1 - x))
            return 0.5f * (float)Math.Log((1.0f + x) / (1.0f - x));
        }

        /// <summary>
        /// Extract the K message bits from the N codeword bits
        /// </summary>
        /// <param name="codeword">The full decoded codeword (N bits as 0s/1s)</param>
        /// <param name="message">Output array for the message (K bits packed into bytes)</param>
        /// <returns>True if parameters are valid, false otherwise</returns>
        public static bool ExtractMessage(byte[] codeword, byte[] message)
        {
            if (codeword == null || message == null || codeword.Length < N || message.Length < K_BYTES)
            {
                return false;
            }

            // Extract the first K bits (message bits) from the N-bit codeword
            // and pack them into the message byte array (MSB first).
            for (int i = 0; i < K_BYTES; ++i)
            {
                message[i] = 0;
                for (int j = 0; j < 8; ++j)
                {
                    int bitIndex = i * 8 + j;
                    if (bitIndex < K) // Ensure we don't read past K bits
                    {
                        if (codeword[bitIndex] == 1)
                        {
                            message[i] |= (byte)(0x80 >> j);
                        }
                    }
                }
            }
            return true;
        }
    }
}
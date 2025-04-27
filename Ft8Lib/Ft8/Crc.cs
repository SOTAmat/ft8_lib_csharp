using System;

namespace Ft8Lib.Ft8
{
    /// <summary>
    /// Provides CRC (Cyclic Redundancy Check) functionality for FT8/FT4 protocols
    /// </summary>
    public static class Crc
    {
        // CRC-14 polynomial as used in the C code's constants.h
        // FT8_CRC_POLYNOMIAL = 0x2757 (from this project's ft8/constants.h)
        private const ushort FT8_CRC_POLYNOMIAL = 0x2757; // Corrected to match C code
        private const int FT8_CRC_WIDTH = 14;
        private const ushort TOPBIT = (1 << (FT8_CRC_WIDTH - 1)); // 0x2000

        /// <summary>
        /// Calculate the CRC-14 for a message, ported from ftx_compute_crc C code.
        /// </summary>
        /// <param name="message">Byte sequence (MSB first)</param>
        /// <param name="numBits">Number of bits in the sequence</param>
        /// <returns>14-bit CRC value</returns>
        public static ushort CalculateCrc(byte[] message, int numBits)
        {
            if (message == null)
                return 0;

            ushort remainder = 0;
            int idxByte = 0;
            int messageByteLength = (numBits + 7) / 8; // Calculate needed bytes

            // Perform modulo-2 division, a bit at a time.
            for (int idxBit = 0; idxBit < numBits; ++idxBit)
            {
                if (idxBit % 8 == 0)
                {
                    if (idxByte < message.Length) // Avoid reading past the end
                    {
                        // Bring the next byte into the remainder.
                        // The C code shifts left by (FT8_CRC_WIDTH - 8) = 6.
                        // This aligns the 8 bits of the message byte with the upper bits of the 14-bit remainder.
                        remainder ^= (ushort)(message[idxByte] << (FT8_CRC_WIDTH - 8));
                        ++idxByte;
                    }
                    // If numBits is not a multiple of 8, and we've processed all full bytes,
                    // we don't bring in any more data for the remaining bits.
                }

                // Try to divide the current data bit.
                if ((remainder & TOPBIT) != 0) // Check if the MSB (bit 13) is set
                {
                    remainder = (ushort)((remainder << 1) ^ FT8_CRC_POLYNOMIAL);
                }
                else
                {
                    remainder = (ushort)(remainder << 1);
                }
            }

            // Mask to 14 bits - the C code uses ((TOPBIT << 1) - 1u) which is 0x3FFF
            return (ushort)(remainder & 0x3FFF);
        }

        /// <summary>
        /// Append a CRC-14 to a message
        /// </summary>
        /// <param name="message">Message to append CRC to</param>
        /// <param name="messageLength">Length of the message in bits</param>
        /// <param name="output">Output buffer to store message with CRC</param>
        /// <returns>True if successful</returns>
        public static bool AppendCrc(byte[] message, int messageLength, byte[] output)
        {
            if (message == null || output == null || messageLength != (Constants.FTX_LDPC_K - CRC_BITS)) // Expecting 77 bits for FT8/FT4
                return false;

            // The C code calculates CRC on 82 bits (77 payload + 5 zeros)
            int crcCalculationLengthBits = messageLength + 5; // 77 + 5 = 82 bits
            int crcCalculationLengthBytes = (crcCalculationLengthBits + 7) / 8; // 11 bytes needed

            // Prepare a temporary buffer for CRC calculation (82 bits)
            byte[] tempBuffer = new byte[crcCalculationLengthBytes];

            // Copy 77 bits of payload data (first 9 bytes fully, 5 bits from byte 10)
            int messageByteLength = (messageLength + 7) / 8; // 10 bytes
            Array.Copy(message, 0, tempBuffer, 0, messageByteLength);

            // Clear the bits after the 77th bit to ensure 5 zero bits are appended
            // The 77th bit is at index 76. It's bit 4 of byte 9 (76 % 8 = 4).
            // We need bits 5, 6, 7 of byte 9 to be 0.
            tempBuffer[9] &= 0xF8; // 11111000 - Keep first 5 bits, zero last 3
            // Ensure subsequent bytes used in calculation (byte 10) are zero
            if (crcCalculationLengthBytes > messageByteLength)
            {
                for (int i = messageByteLength; i < crcCalculationLengthBytes; ++i)
                {
                    tempBuffer[i] = 0;
                }
            }


            // Calculate CRC of 82 bits (77 payload + 5 zeros) using the ported function
            ushort checksum = CalculateCrc(tempBuffer, crcCalculationLengthBits); // Calculate on 82 bits

            // Copy the original 77-bit message to the output
            int outputByteLength = (messageLength + CRC_BITS + 7) / 8; // (77 + 14 + 7) / 8 = 12 bytes needed for output
            if (output.Length < outputByteLength) return false; // Ensure output buffer is large enough

            Array.Copy(message, 0, output, 0, messageByteLength);
            // Ensure remaining bits in the last byte of payload copy are handled if messageLength%8 != 0
            if (messageLength % 8 != 0)
            {
                byte mask = (byte)(0xFF << (8 - (messageLength % 8)));
                output[messageByteLength - 1] &= mask;
            }
            else if (output.Length > messageByteLength)
            {
                // Clear rest of output buffer before adding CRC
                for (int i = messageByteLength; i < outputByteLength; ++i) output[i] = 0;
            }


            // Store the 14-bit CRC at the end of the 77-bit message (bits 77 to 90)
            // Porting the logic from C ftx_add_crc:
            // a91[9] |= (uint8_t)(checksum >> 11);       // CRC bits 13, 12, 11 go into bits 2, 1, 0 of byte 9
            // a91[10] = (uint8_t)(checksum >> 3);        // CRC bits 10..3 go into byte 10
            // a91[11] = (uint8_t)(checksum << 5);        // CRC bits 2..0 go into bits 7, 6, 5 of byte 11

            output[9] |= (byte)(checksum >> 11);        // checksum has 14 bits (0-13). >> 11 leaves bits 13, 12, 11. OR them into lower 3 bits of byte 9.
            output[10] = (byte)(checksum >> 3);         // >> 3 leaves bits 13..3. Take lower 8 bits (which are bits 10..3). Store in byte 10.
            output[11] = (byte)(checksum << 5);         // << 5 shifts bits 2, 1, 0 into bits 7, 6, 5. Store in byte 11.

            return true;
        }

        /// <summary>
        /// Check if a message with CRC is valid
        /// </summary>
        /// <param name="messageWithCrc">Message with CRC (Expected 91 bits for FT8/FT4)</param>
        /// <param name="totalBits">Total number of bits in messageWithCrc (e.g., 91)</param>
        /// <returns>True if CRC is valid</returns>
        public static bool CheckCrc(byte[] messageWithCrc, int totalBits = Constants.FTX_LDPC_N) // Default to 91 bits
        {
            if (messageWithCrc == null || totalBits != Constants.FTX_LDPC_N) // Currently supports only 91 bits (77 payload + 14 CRC)
                return false;

            int messageLength = Constants.FTX_LDPC_K - CRC_BITS; // 77 bits
            int crcCalculationLengthBits = messageLength + 5; // 82 bits
            int crcCalculationLengthBytes = (crcCalculationLengthBits + 7) / 8; // 11 bytes

            // Prepare a temporary buffer for CRC calculation (82 bits)
            byte[] tempBuffer = new byte[crcCalculationLengthBytes];

            // Copy 77 bits of payload data from messageWithCrc
            int messageByteLength = (messageLength + 7) / 8; // 10 bytes
            Array.Copy(messageWithCrc, 0, tempBuffer, 0, messageByteLength);

            // Clear the bits after the 77th bit to ensure 5 zero bits are appended
            tempBuffer[9] &= 0xF8; // 11111000 - Keep first 5 bits, zero last 3
                                   // Ensure subsequent bytes used in calculation (byte 10) are zero
            if (crcCalculationLengthBytes > messageByteLength)
            {
                for (int i = messageByteLength; i < crcCalculationLengthBytes; ++i)
                {
                    tempBuffer[i] = 0;
                }
            }


            // Calculate the CRC of the 82-bit message (77 payload + 5 zeros)
            ushort calculatedCrc = CalculateCrc(tempBuffer, crcCalculationLengthBits);

            // Extract the 14-bit CRC from the original messageWithCrc (bits 77 to 90)
            // Porting the logic from C ftx_extract_crc:
            // uint16_t chksum = ((a91[9] & 0x07) << 11) | (a91[10] << 3) | (a91[11] >> 5);
            int totalBytes = (totalBits + 7) / 8; // 12 bytes for 91 bits
            if (messageWithCrc.Length < totalBytes) return false; // Not enough bytes

            ushort messageCrc = (ushort)(((messageWithCrc[9] & 0x07) << 11) | // Get bits 2,1,0 from byte 9 -> CRC bits 13,12,11
                                         (messageWithCrc[10] << 3) |          // Get bits 7..0 from byte 10 -> CRC bits 10..3
                                         (messageWithCrc[11] >> 5));           // Get bits 7,6,5 from byte 11 -> CRC bits 2,1,0

            // Compare the calculated CRC with the message CRC
            return calculatedCrc == messageCrc;
        }

        // Keep the original CRC14 polynomial and CRC_BITS constants if they are used elsewhere,
        // or remove them if FT8_CRC_POLYNOMIAL and FT8_CRC_WIDTH replace them entirely.
        // For now, keeping both to avoid breaking other potential references.
        // CRC-14 polynomial: x^14 + x^13 + x^5 + x^3 + x^2 + 1
        // The polynomial 0x2757 corresponds to 0b10011101010111 which is x^14 + x^10 + x^9 + x^8 + x^7 + x^5 + x^3 + x^2 + x + 1 (This seems different from the comment? Double check polynomial representation)
        // Let's stick to the C code's value 0x2757 for now.
        private const ushort CRC14_POLYNOMIAL_OLD = 0x6757; // This seems incorrect for FT8/FT4, based on C code.
        public const int CRC_BITS = 14; // Make public for access from Message class
    }
}
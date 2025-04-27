using System;
using Ft8Lib.Ft8;

namespace Ft8Lib.Demo
{
    /// <summary>
    /// Test program for verifying the CRC implementation
    /// </summary>
    public class TestCrc
    {
        /// <summary>
        /// Main entry point for the CRC test program
        /// </summary>
        /// <param name="args">Command line arguments</param>
        public static int Main(string[] args)
        {
            Console.WriteLine("FT8/FT4 CRC Test Program");
            Console.WriteLine("=======================");

            // Test case 1: Simple message
            TestCrcForMessage("CQ K1ABC FN42");

            // Test case 2: Standard message with report
            TestCrcForMessage("K1ABC W9XYZ -10");

            // Test case 3: Free text message
            TestCrcForMessage("TNX FOR QSO 73");

            // Test case 4: Test with binary data
            TestWithBinaryData();

            return 0;
        }

        /// <summary>
        /// Test CRC for a specific message
        /// </summary>
        /// <param name="messageText">Message text to test</param>
        private static void TestCrcForMessage(string messageText)
        {
            Console.WriteLine($"\nTesting message: \"{messageText}\"");

            // Create a message
            var message = new Message(messageText);

            // Get the payload
            byte[]? payload = message.ToPayload();

            if (payload == null)
            {
                Console.WriteLine("  Failed to generate payload (message invalid?)");
                return;
            }

            // Create payload with CRC
            byte[] payloadWithCrc = new byte[12];
            bool success = Crc.AppendCrc(payload, Constants.FTX_LDPC_K - 14, payloadWithCrc);

            if (!success)
            {
                Console.WriteLine("  Failed to append CRC");
                return;
            }

            // Print the payload with CRC
            Console.WriteLine("  Payload with CRC:");
            PrintByteArray(payloadWithCrc);

            // Verify CRC
            bool crcValid = Crc.CheckCrc(payloadWithCrc);
            Console.WriteLine($"  CRC check: {(crcValid ? "VALID" : "INVALID")}");

            // Try to decode the message
            if (crcValid)
            {
                Message? decodedMessage = Message.FromPayloadWithCrc(payloadWithCrc);
                if (decodedMessage != null)
                {
                    Console.WriteLine($"  Decoded message: \"{decodedMessage.Content}\"");
                    Console.WriteLine($"  Message type: {decodedMessage.Type}");
                }
                else
                {
                    Console.WriteLine("  Failed to decode message from payload with CRC.");
                }
            }

            // Test corrupting the data
            Console.WriteLine("\n  Testing with corrupted data:");
            payloadWithCrc[5] ^= 0x01; // Flip one bit

            crcValid = Crc.CheckCrc(payloadWithCrc);
            Console.WriteLine($"  CRC check after corruption: {(crcValid ? "VALID" : "INVALID")}");
        }

        /// <summary>
        /// Test with known binary data
        /// </summary>
        private static void TestWithBinaryData()
        {
            Console.WriteLine("\nTesting with binary data:");

            // Create a test payload (77 bits)
            byte[] payload = new byte[10];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i + 1);
            }

            // Print the original payload
            Console.WriteLine("  Original payload:");
            PrintByteArray(payload);

            // Calculate CRC
            ushort crc = Crc.CalculateCrc(payload, Constants.FTX_LDPC_K - 14);
            Console.WriteLine($"  Calculated CRC: 0x{crc:X4}");

            // Create payload with CRC
            byte[] payloadWithCrc = new byte[12];
            Crc.AppendCrc(payload, Constants.FTX_LDPC_K - 14, payloadWithCrc);

            // Print the payload with CRC
            Console.WriteLine("  Payload with CRC:");
            PrintByteArray(payloadWithCrc);

            // Verify CRC
            bool crcValid = Crc.CheckCrc(payloadWithCrc);
            Console.WriteLine($"  CRC check: {(crcValid ? "VALID" : "INVALID")}");

            // Test corrupting the data
            Console.WriteLine("\n  Testing with corrupted data:");
            payloadWithCrc[3] ^= 0x10; // Flip one bit

            crcValid = Crc.CheckCrc(payloadWithCrc);
            Console.WriteLine($"  CRC check after corruption: {(crcValid ? "VALID" : "INVALID")}");
        }

        /// <summary>
        /// Print a byte array in hexadecimal format
        /// </summary>
        /// <param name="data">Byte array to print</param>
        private static void PrintByteArray(byte[] data)
        {
            Console.Write("  ");
            foreach (byte b in data)
            {
                Console.Write($"{b:X2} ");
            }
            Console.WriteLine();

            // Also print in binary format
            Console.Write("  ");
            foreach (byte b in data)
            {
                Console.Write(Convert.ToString(b, 2).PadLeft(8, '0') + " ");
            }
            Console.WriteLine();
        }
    }
}
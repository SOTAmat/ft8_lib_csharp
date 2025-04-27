using System;
using Ft8Lib.Ft8;
using Ft8Lib.Common;

namespace Ft8Lib.Demo
{
    /// <summary>
    /// Demo application for encoding FT8 messages and saving them as WAV files
    /// </summary>
    public class GenFt8
    {
        /// <summary>
        /// Main entry point for the FT8 encoder demo
        /// </summary>
        /// <param name="args">Command line arguments</param>
        public static int Main(string[] args)
        {
            // Expect at least two command-line arguments
            if (args.Length < 2)
            {
                ShowUsage();
                return -1;
            }

            string message = args[0];
            string wavPath = args[1];
            float frequency = 1000.0f;  // Default frequency
            bool isFt4 = false;

            if (args.Length > 2)
            {
                if (!float.TryParse(args[2], out frequency))
                {
                    Console.WriteLine("Invalid frequency specified. Using default 1000 Hz.");
                    frequency = 1000.0f;
                }
            }

            if (args.Length > 3 && args[3] == "-ft4")
            {
                isFt4 = true;
            }

            // Create a message object
            Message msg = new Message(message);

            // Check if message is valid
            if (msg.Type == MessageType.Invalid)
            {
                Console.WriteLine("Cannot parse message!");
                return -2;
            }

            Console.Write("Packed data: ");
            byte[]? payload = msg.ToPayload();

            if (payload == null)
            {
                Console.WriteLine("\nError: Failed to generate payload.");
                return -3;
            }

            foreach (byte b in payload)
            {
                Console.Write($"{b:X2} ");
            }
            Console.WriteLine();

            int numTones = isFt4 ? Constants.FT4_NUM_SYMBOLS : Constants.FT8_NUM_SYMBOLS;
            float symbolPeriod = isFt4 ? Constants.FT4_SYMBOL_PERIOD : Constants.FT8_SYMBOL_PERIOD;
            float symbolBt = isFt4 ? Constants.FT4_SYMBOL_BT : Constants.FT8_SYMBOL_BT;
            float slotTime = isFt4 ? Constants.FT4_SLOT_TIME : Constants.FT8_SLOT_TIME;

            // Encode the binary message as a sequence of FSK tones
            byte[] tones = new byte[numTones];
            if (isFt4)
            {
                Encode.EncodeFt4(payload, tones);
            }
            else
            {
                Encode.EncodeFt8(payload, tones);
            }

            Console.Write("FSK tones: ");
            foreach (byte tone in tones)
            {
                Console.Write(tone);
            }
            Console.WriteLine();

            // Convert the FSK tones into an audio signal
            int sampleRate = 12000;
            int numSamples = (int)(0.5f + numTones * symbolPeriod * sampleRate); // Number of samples in the data signal
            int numSilence = (int)((slotTime * sampleRate - numSamples) / 2);    // Silence padding at both ends
            int numTotalSamples = numSilence + numSamples + numSilence;          // Number of samples in the padded signal

            float[] signal = new float[numTotalSamples];
            float[] signalData = new float[numSamples]; // Create a separate array for the signal data

            // Initialize silence at both ends
            for (int i = 0; i < numSilence; i++)
            {
                signal[i] = 0;
                signal[i + numSamples + numSilence] = 0;
            }

            // Synthesize waveform data into the signal data array
            Encode.SynthesizeGfsk(tones, numTones, frequency, symbolBt, symbolPeriod, sampleRate, signalData);

            // Copy the signal data to the main signal array
            Array.Copy(signalData, 0, signal, numSilence, numSamples);

            // Create a WAV file and save it
            var wavFile = new Wave.WavFile(1, sampleRate, 16, signal);
            wavFile.WriteToFile(wavPath);

            Console.WriteLine($"Successfully encoded message to {wavPath}");
            return 0;
        }

        /// <summary>
        /// Displays usage information
        /// </summary>
        private static void ShowUsage()
        {
            Console.WriteLine("Generate a 15-second WAV file encoding a given message.");
            Console.WriteLine("Usage:");
            Console.WriteLine();
            Console.WriteLine("GenFt8 MESSAGE WAV_FILE [FREQUENCY] [-ft4]");
            Console.WriteLine();
            Console.WriteLine("(Note that you might have to enclose your message in quote marks if it contains spaces)");
        }
    }
}
using System;
using System.IO;
using System.Text;

namespace Ft8Lib.Common
{
    /// <summary>
    /// Provides utilities for working with WAV audio files
    /// </summary>
    public static class Wave
    {
        /// <summary>
        /// Represents a WAV audio file
        /// </summary>
        public class WavFile
        {
            /// <summary>
            /// Number of audio channels (1 for mono, 2 for stereo)
            /// </summary>
            public int Channels { get; set; }

            /// <summary>
            /// Sample rate in Hz
            /// </summary>
            public int SampleRate { get; set; }

            /// <summary>
            /// Bits per sample (8, 16, or 32)
            /// </summary>
            public int BitsPerSample { get; set; }

            /// <summary>
            /// Audio samples (normalized to range -1.0 to 1.0)
            /// </summary>
            public float[] Samples { get; set; }

            /// <summary>
            /// Creates a new WAV file object with specified parameters
            /// </summary>
            /// <param name="channels">Number of audio channels</param>
            /// <param name="sampleRate">Sample rate in Hz</param>
            /// <param name="bitsPerSample">Bits per sample</param>
            /// <param name="samples">Audio samples (normalized to range -1.0 to 1.0)</param>
            public WavFile(int channels, int sampleRate, int bitsPerSample, float[] samples)
            {
                Channels = channels;
                SampleRate = sampleRate;
                BitsPerSample = bitsPerSample;
                Samples = samples;
            }

            /// <summary>
            /// Reads a WAV file from disk
            /// </summary>
            /// <param name="filePath">Path to the WAV file</param>
            /// <returns>A WavFile object containing the audio data</returns>
            public static WavFile ReadFromFile(string filePath)
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(stream))
                {
                    // Read WAV header
                    string chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    if (chunkId != "RIFF")
                    {
                        throw new InvalidDataException("Not a valid WAV file - missing RIFF header");
                    }

                    int fileSize = reader.ReadInt32();
                    string waveId = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    if (waveId != "WAVE")
                    {
                        throw new InvalidDataException("Not a valid WAV file - missing WAVE identifier");
                    }

                    // Read format chunk
                    string formatChunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    if (formatChunkId != "fmt ")
                    {
                        throw new InvalidDataException("Not a valid WAV file - missing format chunk");
                    }

                    int formatChunkSize = reader.ReadInt32();
                    int audioFormat = reader.ReadInt16();
                    int numChannels = reader.ReadInt16();
                    int sampleRate = reader.ReadInt32();
                    int byteRate = reader.ReadInt32();
                    int blockAlign = reader.ReadInt16();
                    int bitsPerSample = reader.ReadInt16();

                    // Skip any extra format bytes
                    if (formatChunkSize > 16)
                    {
                        reader.ReadBytes(formatChunkSize - 16);
                    }

                    // Find data chunk (skip other chunks until we find the data chunk)
                    string dataChunkId = "";
                    int dataChunkSize = 0;

                    while (dataChunkId != "data")
                    {
                        try
                        {
                            dataChunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
                            dataChunkSize = reader.ReadInt32();

                            if (dataChunkId != "data")
                            {
                                // Skip this chunk
                                reader.ReadBytes(dataChunkSize);
                            }
                        }
                        catch (EndOfStreamException)
                        {
                            throw new InvalidDataException("Not a valid WAV file - data chunk not found");
                        }
                    }

                    // Calculate number of samples
                    int bytesPerSample = bitsPerSample / 8;
                    int sampleCount = dataChunkSize / bytesPerSample;

                    // Create sample array (normalize to -1.0 to 1.0)
                    float[] samples = new float[sampleCount / numChannels];

                    // Only support mono audio for now - if stereo, we'll mix to mono
                    for (int i = 0; i < sampleCount / numChannels; i++)
                    {
                        float sampleValue = 0;

                        for (int c = 0; c < numChannels; c++)
                        {
                            // Read sample based on bit depth
                            if (bitsPerSample == 8)
                            {
                                // 8-bit: unsigned
                                byte val = reader.ReadByte();
                                sampleValue += (val - 128) / 128.0f;
                            }
                            else if (bitsPerSample == 16)
                            {
                                // 16-bit: signed
                                short val = reader.ReadInt16();
                                sampleValue += val / 32768.0f;
                            }
                            else if (bitsPerSample == 24)
                            {
                                // 24-bit: signed (read as 3 bytes)
                                byte[] bytes = reader.ReadBytes(3);
                                int val = (bytes[0]) | (bytes[1] << 8) | (bytes[2] << 16);
                                // If the highest bit is set, extend the sign to 32 bits
                                if ((val & 0x800000) != 0)
                                {
                                    val |= -0x1000000; // Sign extend to 32-bit
                                }
                                sampleValue += val / 8388608.0f; // 2^23
                            }
                            else if (bitsPerSample == 32)
                            {
                                // 32-bit: usually floating point, but could be integer
                                if (audioFormat == 3) // IEEE float
                                {
                                    sampleValue += reader.ReadSingle();
                                }
                                else // 32-bit integer
                                {
                                    int val = reader.ReadInt32();
                                    sampleValue += val / 2147483648.0f; // 2^31
                                }
                            }
                            else
                            {
                                throw new NotSupportedException($"Bit depth of {bitsPerSample} is not supported");
                            }
                        }

                        // Average if we have multiple channels
                        if (numChannels > 1)
                        {
                            sampleValue /= numChannels;
                        }

                        samples[i] = sampleValue;
                    }

                    // Return the WAV file object
                    return new WavFile(numChannels, sampleRate, bitsPerSample, samples);
                }
            }

            /// <summary>
            /// Writes the WAV file to disk
            /// </summary>
            /// <param name="filePath">Path to save the WAV file</param>
            public void WriteToFile(string filePath)
            {
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(stream))
                {
                    int bytesPerSample = BitsPerSample / 8;
                    int dataSize = Samples.Length * Channels * bytesPerSample;
                    int fileSize = 36 + dataSize; // Header (44 bytes) - 8 bytes

                    // Write RIFF header
                    writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                    writer.Write(fileSize);
                    writer.Write(Encoding.ASCII.GetBytes("WAVE"));

                    // Write format chunk
                    writer.Write(Encoding.ASCII.GetBytes("fmt "));
                    writer.Write(16); // Format chunk size
                    writer.Write((short)1); // Audio format (1 for PCM)
                    writer.Write((short)Channels);
                    writer.Write(SampleRate);
                    writer.Write(SampleRate * Channels * bytesPerSample); // Byte rate
                    writer.Write((short)(Channels * bytesPerSample)); // Block align
                    writer.Write((short)BitsPerSample);

                    // Write data chunk
                    writer.Write(Encoding.ASCII.GetBytes("data"));
                    writer.Write(dataSize);

                    // Write sample data
                    for (int i = 0; i < Samples.Length; i++)
                    {
                        // For each channel (usually just one for mono)
                        for (int c = 0; c < Channels; c++)
                        {
                            float sample = Samples[i];

                            // Clip sample to valid range
                            if (sample > 1.0f) sample = 1.0f;
                            if (sample < -1.0f) sample = -1.0f;

                            // Write based on bit depth
                            if (BitsPerSample == 8)
                            {
                                // 8-bit: unsigned
                                byte val = (byte)((sample + 1.0f) * 127.5f);
                                writer.Write(val);
                            }
                            else if (BitsPerSample == 16)
                            {
                                // 16-bit: signed
                                short val = (short)(sample * 32767.0f);
                                writer.Write(val);
                            }
                            else if (BitsPerSample == 24)
                            {
                                // 24-bit: signed (write as 3 bytes)
                                int val = (int)(sample * 8388607.0f);
                                writer.Write((byte)(val & 0xFF));
                                writer.Write((byte)((val >> 8) & 0xFF));
                                writer.Write((byte)((val >> 16) & 0xFF));
                            }
                            else if (BitsPerSample == 32)
                            {
                                // 32-bit: floating point
                                writer.Write(sample);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Generates a sine wave
        /// </summary>
        /// <param name="frequency">Frequency in Hz</param>
        /// <param name="duration">Duration in seconds</param>
        /// <param name="amplitude">Amplitude (0.0 to 1.0)</param>
        /// <param name="sampleRate">Sample rate in Hz</param>
        /// <returns>Sample data for the sine wave</returns>
        public static float[] GenerateSineWave(float frequency, float duration, float amplitude, int sampleRate)
        {
            int sampleCount = (int)(duration * sampleRate);
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float time = (float)i / sampleRate;
                samples[i] = amplitude * (float)Math.Sin(2.0 * Math.PI * frequency * time);
            }

            return samples;
        }

        /// <summary>
        /// Applies amplitude modulation to a signal
        /// </summary>
        /// <param name="carrier">Carrier signal samples</param>
        /// <param name="modulationSignal">Modulation signal samples (range 0.0 to 1.0)</param>
        /// <returns>Amplitude modulated signal</returns>
        public static float[] ApplyAmplitudeModulation(float[] carrier, float[] modulationSignal)
        {
            if (carrier.Length != modulationSignal.Length)
            {
                throw new ArgumentException("Carrier and modulation signals must have the same length");
            }

            float[] result = new float[carrier.Length];

            for (int i = 0; i < carrier.Length; i++)
            {
                result[i] = carrier[i] * modulationSignal[i];
            }

            return result;
        }

        /// <summary>
        /// Applies frequency modulation to a signal
        /// </summary>
        /// <param name="centerFrequency">Center frequency in Hz</param>
        /// <param name="modulationSignal">Modulation signal samples (range -1.0 to 1.0)</param>
        /// <param name="frequencyDeviation">Maximum frequency deviation in Hz</param>
        /// <param name="sampleRate">Sample rate in Hz</param>
        /// <returns>Frequency modulated signal</returns>
        public static float[] ApplyFrequencyModulation(float centerFrequency, float[] modulationSignal, float frequencyDeviation, int sampleRate)
        {
            float[] result = new float[modulationSignal.Length];
            double phase = 0.0;

            for (int i = 0; i < modulationSignal.Length; i++)
            {
                // Calculate instantaneous frequency
                float instantFrequency = centerFrequency + frequencyDeviation * modulationSignal[i];

                // Integrate frequency to get phase
                phase += 2.0 * Math.PI * instantFrequency / sampleRate;

                // Generate FM signal
                result[i] = (float)Math.Sin(phase);
            }

            return result;
        }
    }
}
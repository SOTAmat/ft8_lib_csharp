# FT8/FT4 Library for C#

This is a C# port of the FT8/FT4 communication protocol library for amateur radio. The original C implementation was created by Karlis Goba (YL3JG).

## Overview

FT8 and FT4 are digital communication protocols designed for weak-signal operation in amateur radio. They use frequency-shift keying (FSK) modulation with error correction to enable reliable communication over challenging propagation conditions.

This library provides:

- Encoding of text messages into FT8/FT4 audio signals
- Decoding of FT8/FT4 audio signals back into text messages
- Support for various message formats used in amateur radio communication
- WAV file reading and writing capabilities

## Project Structure

The library is organized into the following namespaces:

- `Ft8Lib.Ft8`: Core FT8/FT4 protocol implementation
  - Message encoding/decoding
  - LDPC error correction
  - CRC calculation
  - FSK modulation
- `Ft8Lib.Common`: Common utilities
  - WAV file handling
- `Ft8Lib.Demo`: Example applications
  - GenFt8: Generates FT8/FT4 WAV files from text messages

## Usage

### Encoding a Message

```csharp
// Create a message
Message msg = new Message();
msg.Encode("CQ K1ABC FN42");

// Encode as FT8 tones
byte[] tones = new byte[Constants.FT8_NUM_SYMBOLS];
Encode.EncodeFt8(msg.Payload, tones);

// Generate audio signal
float[] signal = new float[15 * 12000]; // 15 seconds at 12000 Hz
Encode.SynthesizeGfsk(tones, Constants.FT8_NUM_SYMBOLS, 1000.0f,
                      Constants.FT8_SYMBOL_BT, Constants.FT8_SYMBOL_PERIOD,
                      12000, signal);

// Save to WAV file
Wave.SaveWav(signal, signal.Length, 12000, "output.wav");
```

### Using the Demo Application

```
GenFt8 "CQ K1ABC FN42" output.wav 1000
```

This will generate a 15-second WAV file containing the encoded message "CQ K1ABC FN42" at 1000 Hz.

## Requirements

- .NET Standard 2.0 compatible runtime
- C# 10.0 or later

## License

This project is licensed under the same terms as the original C implementation.

## Acknowledgments

- Karlis Goba (YL3JG) for the original C implementation
- WSJT-X authors for developing the FT8/FT4 protocols

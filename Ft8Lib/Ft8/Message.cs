using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
// Add System.Numerics
using System.Numerics;

namespace Ft8Lib.Ft8
{
    /// <summary>
    /// Represents the type of FT8/FT4 message
    /// </summary>
    public enum MessageType
    {
        Standard,       // Standard message (callsigns + grid/report/etc)
        FreeText,       // Free text message
        Telemetry,      // Telemetry data
        Compound,       // Compound callsign
        NonStandard,    // Other non-standard message
        Invalid         // Invalid message
    }

    /// <summary>
    /// Result codes for message encoding/decoding operations
    /// </summary>
    public enum MessageResult
    {
        OK = 0,
        ErrorInvalidMessage = 1,
        ErrorInvalidCallsign = 2,
        ErrorInvalidLocator = 3,
        ErrorDecode = 4,
        ErrorInvalidLength = 5
    }

    // Add CallsignHashType enum (ported from ftx_callsign_hash_type_t)
    public enum CallsignHashType
    {
        Hash22Bits,
        Hash12Bits,
        Hash10Bits
    }

    // Add ICallsignHasher interface (ported from ftx_callsign_hash_interface_t)
    public interface ICallsignHasher
    {
        /// <summary>
        /// Looks up a callsign by its hash value.
        /// </summary>
        /// <param name="hashType">Type of hash (22, 12, or 10 bits).</param>
        /// <param name="hash">The hash value.</param>
        /// <param name="callsign">Output: The found callsign (without brackets).</param>
        /// <returns>True if found, false otherwise.</returns>
        bool LookupHash(CallsignHashType hashType, uint hash, out string callsign);

        /// <summary>
        /// Saves a callsign and its associated 22-bit hash.
        /// Implementations may derive and store 12/10 bit hashes as well.
        /// </summary>
        /// <param name="callsign">The callsign to save (without brackets).</param>
        /// <param name="n22">The 22-bit hash value.</param>
        void SaveHash(string callsign, uint n22);
    }

    // Add a default in-memory hasher implementation
    public class InMemoryHasher : ICallsignHasher
    {
        // Store hashes and their corresponding callsigns
        // We only strictly need n22 -> callsign for lookup, but storing
        // all helps if we needed reverse lookups or more complex logic later.
        private readonly Dictionary<uint, string> _hash22Store = new Dictionary<uint, string>();
        // We can derive n12 and n10 from n22 when needed for lookup

        public bool LookupHash(CallsignHashType hashType, uint hash, out string callsign)
        {
            callsign = string.Empty;
            // Since we store by n22, we need to iterate if looking up by n12 or n10
            // This is inefficient but simple for an in-memory default.
            // A production hasher might use separate dictionaries or a database.
            foreach (var kvp in _hash22Store)
            {
                uint n22 = kvp.Key;
                uint currentHash = 0;

                switch (hashType)
                {
                    case CallsignHashType.Hash22Bits:
                        currentHash = n22;
                        break;
                    case CallsignHashType.Hash12Bits:
                        currentHash = n22 >> 10; // Get top 12 bits
                        break;
                    case CallsignHashType.Hash10Bits:
                        currentHash = n22 >> 12; // Get top 10 bits
                        break;
                }

                if (currentHash == hash)
                {
                    callsign = kvp.Value;
                    return true; // Found a match
                }
            }
            return false; // Not found
        }

        public void SaveHash(string callsign, uint n22)
        {
            // Store or update the callsign for this n22 hash
            _hash22Store[n22] = callsign;
        }
    }

    /// <summary>
    /// Handles encoding and decoding of FT8/FT4 protocol messages
    /// </summary>
    public class Message
    {
        // Constants ported from C code (message.c, text.c, constants.h)
        private const uint NTOKENS = 2063592;
        private const uint MAX22 = 4194304;
        private const ushort MAXGRID4 = 32400;

        // Character table enum equivalent
        private enum Ft8CharTable
        {
            Full,                 // " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ+-./?"
            AlphanumSpaceSlash, // " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/"
            AlphanumSpace,       // " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"
            LettersSpace,        // " ABCDEFGHIJKLMNOPQRSTUVWXYZ"
            Alphanum,             // "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"
            Numeric               // "0123456789"
        }

        private const int MaxMessageLength = 13;
        private const string StandardCallsignPattern = @"^[A-Z0-9]{1,6}$";
        private const string CompoundCallsignPattern = @"^[A-Z0-9]{1,6}/[A-Z0-9]{1,6}$";
        private const string GridLocatorPattern = @"^[A-R]{2}[0-9]{2}([A-X]{2})?$";
        private const string ReportPattern = @"^[+-](?:[0-9]{2}|R[0-9]{2})$";

        // Add the default hasher instance
        private static readonly ICallsignHasher defaultHasher = new InMemoryHasher();

        /// <summary>
        /// The encoded message content
        /// </summary>
        public string Content { get; private set; }

        /// <summary>
        /// The type of the message
        /// </summary>
        public MessageType Type { get; private set; }

        /// <summary>
        /// First callsign (if applicable)
        /// </summary>
        public string Callsign1 { get; private set; } = string.Empty;

        /// <summary>
        /// Second callsign (if applicable)
        /// </summary>
        public string Callsign2 { get; private set; } = string.Empty;

        /// <summary>
        /// Grid locator (if applicable)
        /// </summary>
        public string Grid { get; private set; } = string.Empty;

        /// <summary>
        /// Signal report (if applicable)
        /// </summary>
        public string Report { get; private set; } = string.Empty;

        /// <summary>
        /// Creates a new message from text content
        /// </summary>
        /// <param name="content">The message content</param>
        public Message(string content)
        {
            if (string.IsNullOrEmpty(content) || content.Length > MaxMessageLength)
            {
                Type = MessageType.Invalid;
                Content = string.Empty;
                return;
            }

            Content = content.ToUpperInvariant().Trim();
            ParseMessage();
        }

        /// <summary>
        /// Creates a standard message with callsigns and grid locator
        /// </summary>
        /// <param name="callsign1">First callsign</param>
        /// <param name="callsign2">Second callsign</param>
        /// <param name="grid">Grid locator</param>
        /// <returns>A new message instance or null if invalid</returns>
        public static Message? CreateStandardWithGrid(string callsign1, string callsign2, string grid)
        {
            if (string.IsNullOrEmpty(callsign1) || string.IsNullOrEmpty(callsign2) || string.IsNullOrEmpty(grid))
            {
                return null; // Return null for invalid
            }

            return new Message($"{callsign1.ToUpperInvariant().Trim()} {callsign2.ToUpperInvariant().Trim()} {grid.ToUpperInvariant().Trim()}");
        }

        /// <summary>
        /// Creates a standard message with callsigns and report
        /// </summary>
        /// <param name="callsign1">First callsign</param>
        /// <param name="callsign2">Second callsign</param>
        /// <param name="report">Signal report</param>
        /// <returns>A new message instance or null if invalid</returns>
        public static Message? CreateStandardWithReport(string callsign1, string callsign2, string report)
        {
            if (string.IsNullOrEmpty(callsign1) || string.IsNullOrEmpty(callsign2) || string.IsNullOrEmpty(report))
            {
                return null; // Return null for invalid
            }

            return new Message($"{callsign1.ToUpperInvariant().Trim()} {callsign2.ToUpperInvariant().Trim()} {report.Trim()}");
        }

        /// <summary>
        /// Creates a free text message
        /// </summary>
        /// <param name="text">Free text (up to 13 characters)</param>
        /// <returns>A new message instance or null if invalid</returns>
        public static Message? CreateFreeText(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > MaxMessageLength)
            {
                return null; // Return null for invalid
            }

            return new Message(text);
        }

        /// <summary>
        /// Encodes the message to a 77-bit payload
        /// </summary>
        /// <returns>10 bytes containing the 77-bit payload, or null if encoding fails</returns>
        public byte[]? ToPayload()
        {
            byte[] payload = new byte[10];
            bool success = false; // Declare success here

            if (Type == MessageType.Invalid)
            {
                return null; // Return null for invalid messages
            }

            // Attempt to encode based on message type
            // Currently only implementing Standard Message Type encoding accurately
            switch (Type)
            {
                case MessageType.Standard:
                    // Ensure we have the necessary components for standard message
                    if (!string.IsNullOrEmpty(Callsign1) && !string.IsNullOrEmpty(Callsign2) &&
                        (!string.IsNullOrEmpty(Grid) || !string.IsNullOrEmpty(Report) || ExtraIsEmpty()))
                    {
                        string extra = !string.IsNullOrEmpty(Grid) ? Grid : (!string.IsNullOrEmpty(Report) ? Report : "");
                        if (extra == "" && ExtraIsEmpty())
                        {
                            // Handle cases like "K1ABC W9XYZ 73", "K1ABC W9XYZ RRR", "K1ABC W9XYZ" (empty extra)
                            extra = GetSpecialExtraToken();
                        }

                        //bool success = EncodeStandardPayload(payload, Callsign1, Callsign2, extra); // Remove local declaration
                        success = EncodeStandardPayload(payload, Callsign1, Callsign2, extra);
                        return success ? payload : null;
                    }
                    else
                    {
                        // Maybe it's a CQ message? Try encoding that.
                        if (Callsign1 == "CQ" && !string.IsNullOrEmpty(Callsign2) && !string.IsNullOrEmpty(Grid))
                        {
                            //bool success = EncodeStandardPayload(payload, Callsign1, Callsign2, Grid); // Remove local declaration
                            success = EncodeStandardPayload(payload, Callsign1, Callsign2, Grid);
                            return success ? payload : null;
                        }
                        // Not enough info for standard encoding
                        return null;
                    }
                // No break needed due to returns

                case MessageType.FreeText:
                    //success = EncodeFreeTextPayload(Content, payload);
                    success = EncodeFreeTextPayload(Content, payload);
                    break; // Keep break, will return below

                case MessageType.NonStandard: // Requires NonStd encoding (Type 4)
                case MessageType.Compound:    // Requires NonStd encoding (Type 4)
                case MessageType.Telemetry:   // Requires Telemetry encoding (Type 0.5)
                                              // TODO: Implement other encoding types based on C code
                    return null; // Not implemented yet

                default:
                    return null; // Invalid or unknown type
            }

            // Return based on success for cases that break (like FreeText)
            return success ? payload : null;
        }

        /// <summary>
        /// Encodes the message to a 91-bit payload with CRC
        /// </summary>
        /// <returns>12 bytes containing the 91-bit payload with CRC, or null if encoding fails</returns>
        public byte[]? ToPayloadWithCrc()
        {
            byte[]? payload = ToPayload();
            if (payload == null)
            {
                return null; // Encoding failed
            }

            byte[] payloadWithCrc = new byte[12]; // 91 bits (77 + 14) requires 12 bytes

            // Append CRC to the payload using the previously corrected Crc class
            // Assuming Crc.AppendCrc expects 77 bits (Constants.FTX_LDPC_K - 14)
            bool crcSuccess = Crc.AppendCrc(payload, Constants.FTX_LDPC_K - Crc.CRC_BITS, payloadWithCrc);

            return crcSuccess ? payloadWithCrc : null;
        }

        /// <summary>
        /// Parse the message content to determine type and extract components
        /// </summary>
        private void ParseMessage()
        {
            // Default to invalid until proven otherwise
            Type = MessageType.Invalid;
            Callsign1 = string.Empty;
            Callsign2 = string.Empty;
            Grid = string.Empty;
            Report = string.Empty;

            if (string.IsNullOrEmpty(Content))
            {
                return;
            }

            // Remove consecutive spaces and convert to upper
            Content = Regex.Replace(Content.ToUpperInvariant().Trim(), @"\s+", " ");

            string[] parts = Content.Split(' ');

            // Simple check for Free Text (if it doesn't match other patterns)
            if (parts.Length == 1 && Content.Length <= MaxMessageLength)
            {
                Type = MessageType.FreeText;
                return; // Treat as free text for now
            }

            // Check for CQ
            if (parts.Length >= 3 && parts[0] == "CQ")
            {
                Callsign1 = "CQ";
                Callsign2 = parts[1];
                Grid = parts[2]; // Assume 3rd part is grid for now
                // TODO: Add validation based on C logic (e.g., CQ DX, CQ POTA etc. might be handled differently or as free text)
                if (IsValidCallsign(Callsign2) && IsGridLocator(Grid)) // Basic validation
                {
                    Type = MessageType.Standard;
                }
                else
                {
                    Type = MessageType.FreeText; // Fallback if not valid standard CQ
                }
                return;
            }

            // Check for Standard Message (Call Call Grid/Report/Special)
            if (parts.Length >= 2)
            {
                string c1 = parts[0];
                string c2 = parts[1];

                if (IsValidCallsign(c1) && IsValidCallsign(c2))
                {
                    Callsign1 = c1;
                    Callsign2 = c2;

                    if (parts.Length >= 3)
                    {
                        string extra = parts[2];
                        if (IsGridLocator(extra))
                        {
                            Grid = extra;
                            Type = MessageType.Standard;
                        }
                        else if (IsReport(extra))
                        {
                            Report = extra;
                            Type = MessageType.Standard;
                        }
                        else if (extra == "RRR" || extra == "RR73" || extra == "73")
                        {
                            // Report is empty, Grid is empty, Content holds the special token
                            Type = MessageType.Standard;
                        }
                        else
                        {
                            Type = MessageType.FreeText; // Fallback if 3rd part is not recognized standard
                        }
                    }
                    else if (parts.Length == 2)
                    {
                        // Two callsigns only is a valid standard message with empty "extra"
                        Type = MessageType.Standard;
                    }
                }
                else
                {
                    Type = MessageType.FreeText; // Fallback if callsigns invalid
                }
                return;
            }

            // If none of the above, treat as FreeText (or potentially other types later)
            if (Content.Length <= MaxMessageLength)
            {
                Type = MessageType.FreeText;
            }
            else
            {
                Type = MessageType.Invalid;
            }
        }

        /// <summary>
        /// Checks if a string is a valid callsign
        /// </summary>
        private bool IsValidCallsign(string callsign)
        {
            if (string.IsNullOrEmpty(callsign))
            {
                return false;
            }

            // Standard callsign
            if (Regex.IsMatch(callsign, StandardCallsignPattern))
            {
                return true;
            }

            // Compound callsign
            if (Regex.IsMatch(callsign, CompoundCallsignPattern))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if a string is a valid grid locator
        /// </summary>
        private static bool IsGridLocator(string grid)
        {
            if (string.IsNullOrEmpty(grid))
            {
                return false;
            }

            return Regex.IsMatch(grid, GridLocatorPattern);
        }

        /// <summary>
        /// Checks if a string is a valid signal report
        /// </summary>
        private static bool IsReport(string report)
        {
            if (string.IsNullOrEmpty(report))
            {
                return false;
            }

            return Regex.IsMatch(report, ReportPattern);
        }

        /// <summary>
        /// Helper to check if grid/report are empty but Content implies a special extra like "73"
        /// </summary>
        private bool ExtraIsEmpty()
        {
            return string.IsNullOrEmpty(Grid) && string.IsNullOrEmpty(Report);
        }

        private string GetSpecialExtraToken()
        {
            string[] parts = Content.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3)
            {
                if (parts[2] == "RRR" || parts[2] == "RR73" || parts[2] == "73")
                {
                    return parts[2];
                }
            }
            else if (parts.Length == 2)
            {
                // Case like "K1ABC W9XYZ" - empty extra is valid
                return "";
            }
            return ""; // Default to empty
        }

        /// <summary>
        /// Port of ftx_message_encode_std from message.c
        /// Packs Type 1 (Standard 77-bit message) or Type 2 (ditto, with a "/P" call) message
        /// </summary>
        /// <param name="payload">Output 10-byte payload buffer</param>
        /// <param name="callTo">Destination callsign</param>
        /// <param name="callDe">Source callsign</param>
        /// <param name="extra">Grid locator, report, RRR, RR73, 73, or empty string</param>
        /// <returns>True if encoding successful</returns>
        private static bool EncodeStandardPayload(byte[] payload, string callTo, string callDe, string extra)
        {
            // Use default internal hasher for now
            return EncodeStandardPayload(payload, callTo, callDe, extra, defaultHasher);
        }

        /// <summary>
        /// Port of ftx_message_encode_std from message.c (extended version with hasher).
        /// Packs Type 1 (Standard 77-bit message) or Type 2 (ditto, with a "/P" call) message.
        /// </summary>
        /// <param name="payload">Output 10-byte payload buffer</param>
        /// <param name="callTo">Destination callsign</param>
        /// <param name="callDe">Source callsign</param>
        /// <param name="extra">Grid locator, report, RRR, RR73, 73, or empty string</param>
        /// <param name="hasher">Hasher implementation to use.</param>
        /// <returns>True if encoding successful</returns>
        private static bool EncodeStandardPayload(byte[] payload, string callTo, string callDe, string extra, ICallsignHasher hasher)
        {
            // Pack callsigns using ported Pack28 logic
            long n28a_raw = Pack28(callTo, hasher, out byte ipa);
            long n28b_raw = Pack28(callDe, hasher, out byte ipb);

            if (n28a_raw < 0 || n28b_raw < 0) return false; // Invalid callsign packing

            uint n28a = (uint)n28a_raw;
            uint n28b = (uint)n28b_raw;

            // Determine message type i3 based on /P suffix
            byte i3 = 1; // Default: No suffix or /R
            if (callTo.EndsWith("/P") || callDe.EndsWith("/P"))
            {
                if (callTo.EndsWith("/R") || callDe.EndsWith("/R"))
                {
                    return false; // Cannot have both /P and /R in standard message
                }
                i3 = 2; // Suffix /P indicates Type 2 (EU VHF contest style)
            }

            // Pack grid/report/extra using ported PackGrid logic
            ushort igrid4_raw = PackGrid(extra, out byte ir); // ir is output flag for reports starting with R
            if (igrid4_raw > MAXGRID4 + 4 && igrid4_raw != 0xFFFF) return false; // Check for invalid PackGrid result (allow 0xFFFF as error)
            if (igrid4_raw == 0xFFFF) return false; // packgrid returns > MAXGRID4 normally, check specific error code if needed


            // Combine packed values into the 77-bit payload
            // Based on message.c:ftx_message_encode_std packing logic

            // C code shifts in ipa/ipb here, but Pack28 already handled that conceptually
            // by returning the packed value and the separate ip flag.
            // The bit packing needs n28 (value without ip) and ip separately.
            uint n29a = (n28a << 1) | ipa;
            uint n29b = (n28b << 1) | ipb;
            uint igrid4 = (uint)igrid4_raw; // Use the value returned by packgrid

            // Pack into (28 + 1) + (28 + 1) + (1 + 15) + 3 bits = 77 bits
            // Payload bit indices (MSB=76, LSB=0):
            // n29a:   76..48 (29 bits)
            // n29b:   47..19 (29 bits)
            // ir:     18     (1 bit)  -- Note: C code packs igrid4 | (ir << 15) effectively
            // igrid4: 17..3  (15 bits) -- Note: C code packs 16 bits total for grid/report/flag
            // i3:     2..0   (3 bits)

            // Let's re-verify the bit packing from C:
            // msg->payload[0] = (uint8_t)(n29a >> 21);                     // Bits 76..69 (8) = n29a bits 28..21
            // msg->payload[1] = (uint8_t)(n29a >> 13);                     // Bits 68..61 (8) = n29a bits 20..13
            // msg->payload[2] = (uint8_t)(n29a >> 5);                      // Bits 60..53 (8) = n29a bits 12..5
            // msg->payload[3] = (uint8_t)(n29a << 3) | (uint8_t)(n29b >> 26); // Bits 52..45 (8) = n29a bits 4..0 (5) | n29b bits 28..26 (3)
            // msg->payload[4] = (uint8_t)(n29b >> 18);                     // Bits 44..37 (8) = n29b bits 25..18
            // msg->payload[5] = (uint8_t)(n29b >> 10);                     // Bits 36..29 (8) = n29b bits 17..10
            // msg->payload[6] = (uint8_t)(n29b >> 2);                      // Bits 28..21 (8) = n29b bits 9..2
            // msg->payload[7] = (uint8_t)(n29b << 6) | (uint8_t)(igrid4 >> 10); // Bits 20..13 (8) = n29b bits 1..0 (2) | igrid4 bits 15..10 (6) -- C uses 16bit combined igrid4
            // msg->payload[8] = (uint8_t)(igrid4 >> 2);                      // Bits 12..5 (8)  = igrid4 bits 9..2
            // msg->payload[9] = (uint8_t)(igrid4 << 6) | (uint8_t)(i3 << 3);  // Bits 4..0 (5)   = igrid4 bits 1..0 (2) | i3 bits 2..0 (3) -- 5 bits only, payload[9] bits 7..5 unused?

            // Re-implementing C# packing based on C code structure (using 16-bit combined grid/report value)
            uint combined_grid_report = igrid4 | (uint)(ir << 15); // Combine grid/report value with flag

            payload[0] = (byte)(n29a >> 21);
            payload[1] = (byte)(n29a >> 13);
            payload[2] = (byte)(n29a >> 5);
            payload[3] = (byte)((n29a << 3) | (n29b >> 26));
            payload[4] = (byte)(n29b >> 18);
            payload[5] = (byte)(n29b >> 10);
            payload[6] = (byte)(n29b >> 2);
            // payload[7] = (byte)((n29b << 6) | (igrid4_raw >> 10)); // Original based on igrid4
            payload[7] = (byte)((n29b << 6) | (combined_grid_report >> 10)); // Use combined 16-bit value
            // payload[8] = (byte)((igrid4_raw >> 2));
            payload[8] = (byte)(combined_grid_report >> 2);
            // payload[9] = (byte)((igrid4_raw << 6) | (i3 << 3));
            payload[9] = (byte)((combined_grid_report << 6) | (i3 << 3)); // Use combined value, shift left 6, OR with i3 shifted left 3


            // Zero out the last 3 bits (bits 0, 1, 2) of payload[9] as they are not used by this packing structure
            // payload[9] &= 0xF8; // 11111000 -> This seems wrong based on C code, C code just ORs i3<<3

            // C code calculates a 14-bit CRC on 77 bits (payload + 5 zero bits).
            // Our Crc.AppendCrc should handle this based on previous fix.
            // The 77 bits are correctly placed by the logic above.

            return true;
        }

        /// <summary>
        /// Port of pack28 from message.c (extended version with hasher).
        /// Pack a special token, a valid base call, or hash a non-standard call.
        /// </summary>
        /// <param name="callsign">Callsign string.</param>
        /// <param name="hasher">Hasher implementation to use.</param>
        /// <param name="ip">Output: Suffix flag (1 if /P or /R, 0 otherwise).</param>
        /// <returns>28-bit packed value representing token, standard call, or hash. Returns -1 on error.</returns>
        private static long Pack28(string callsign, ICallsignHasher hasher, out byte ip)
        {
            ip = 0;
            if (string.IsNullOrEmpty(callsign)) return -1;

            callsign = callsign.ToUpperInvariant(); // Ensure uppercase

            // Check for special tokens first
            if (callsign == "DE") return 0;
            if (callsign == "QRZ") return 1;
            if (callsign == "CQ") return 2;

            // TODO: Handle CQ_nnn, CQ_abcd if needed (from C pack28 lines 770-780)
            if (callsign.StartsWith("CQ ") && callsign.Length > 3)
            {
                // For now, treat these as invalid for standard packing
                return -1;
            }

            int length = callsign.Length;
            int lengthBase = length;
            string baseCall = callsign;

            // Detect /R and /P suffix
            if (callsign.EndsWith("/P"))
            {
                ip = 1;
                lengthBase = length - 2;
                baseCall = callsign.Substring(0, lengthBase);
            }
            else if (callsign.EndsWith("/R"))
            {
                ip = 1;
                lengthBase = length - 2;
                baseCall = callsign.Substring(0, lengthBase);
            }

            // Attempt to pack as standard basecall
            long n28_base = PackBasecall(baseCall, lengthBase);

            if (n28_base >= 0)
            {
                // Successfully packed as standard basecall
                if (n28_base >= 0 && n28_base < 100000000L) // Check plausible range
                {
                    // Save the full callsign (potentially with suffix) to the hash table
                    if (!SaveCallsign(callsign, hasher, out _))
                        return -1; // Error saving (invalid chars?)
                    // Return standard call encoding with offsets
                    return NTOKENS + MAX22 + (uint)n28_base;
                }
                else
                {
                    return -1; // Basecall packing returned implausible value
                }
            }

            // If not a standard basecall, try hashing if length is valid (3-11 chars)
            // Note: C code hashes the call *without* suffix for storage.
            // Let's hash the baseCall identified earlier.
            // C# hashes the baseCall (suffix removed)
            // --- Correction: C code hashes the *original* callsign here --- 
            // if ((lengthBase >= 3) && (lengthBase <= 11)) // Original C# used lengthBase
            if ((length >= 3) && (length <= 11)) // Use original length for check like C
            {
                // Calculate N22 hash of the base callsign
                // if (!SaveCallsign(baseCall, hasher, out uint n22)) // Original C# hashed baseCall
                if (!SaveCallsign(callsign, hasher, out uint n22)) // Hash original full callsign like C
                {
                    return -1; // Error calculating/saving hash (invalid chars?)
                }
                // For hashed calls, ip flag should be 0 according to C logic example (it's not a standard call with suffix)
                ip = 0;
                // Return hashed call encoding with offset
                return NTOKENS + n22;
            }

            return -1; // Error: Not a special token, not a standard basecall, not a hashable callsign.
        }

        /// <summary>
        /// Port of pack_basecall from message.c
        /// Packs a standard base callsign into a numeric value.
        /// </summary>
        /// <param name="callsign">The base callsign (no suffix)</param>
        /// <param name="length">Length of the base callsign</param>
        /// <returns>Packed value, or -1 if not a valid standard basecall</returns>
        private static long PackBasecall(string callsign, int length)
        {
            if (length < 3) return -1; // Need at least 3 chars for basecall

            // C code handles Swaziland (3DA0) and Guinea (3X) prefixes specially.
            // These map to different characters before packing.
            string packableCall = callsign;
            if (callsign.StartsWith("3DA0") && length > 4 && length <= 7)
            {
                // 3DA0XYZ -> 3D0XYZ
                packableCall = "3D0" + callsign.Substring(4);
                length = packableCall.Length;
            }
            else if (callsign.StartsWith("3X") && length > 2 && length <= 7 && char.IsLetter(callsign[2]))
            {
                // 3XA... -> QA...
                packableCall = "Q" + callsign.Substring(2);
                length = packableCall.Length;
            }

            // Create a 6-char buffer, right-aligned based on digit position
            char[] c6 = { ' ', ' ', ' ', ' ', ' ', ' ' };
            bool plausible = false;
            if (length <= 6)
            {
                if (length > 2 && char.IsDigit(packableCall[2]))
                {
                    // AB0XYZ - Left align in c6
                    Array.Copy(packableCall.ToCharArray(), 0, c6, 0, length);
                    plausible = true;
                }
                else if (length > 1 && length <= 5 && char.IsDigit(packableCall[1]))
                {
                    // A0XYZ - Align starting at c6[1]
                    Array.Copy(packableCall.ToCharArray(), 0, c6, 1, length);
                    plausible = true;
                }
                else if (length <= 6 && char.IsLetter(packableCall[0]) && char.IsLetter(packableCall[1]) && char.IsLetter(packableCall[2]))
                {
                    // Handle cases like G4XYZ (no digit) - Left align
                    Array.Copy(packableCall.ToCharArray(), 0, c6, 0, length);
                    plausible = true; // Allow non-digit calls if structure seems valid
                }
                else if (length <= 5 && char.IsLetter(packableCall[0]) && char.IsLetter(packableCall[1]))
                {
                    // Handle cases like G4XY (no digit) - Align starting at c6[1]
                    Array.Copy(packableCall.ToCharArray(), 0, c6, 1, length);
                    plausible = true;
                }
                // May need more robust check for valid basecall structure
            }

            if (!plausible && length > 0) return -1; // Reject if doesn't fit known patterns or too long

            // Check character validity and pack using specific tables
            int i0 = NChar(c6[0], Ft8CharTable.AlphanumSpace);
            int i1 = NChar(c6[1], Ft8CharTable.Alphanum);
            int i2 = NChar(c6[2], Ft8CharTable.Numeric);
            int i3 = NChar(c6[3], Ft8CharTable.LettersSpace);
            int i4 = NChar(c6[4], Ft8CharTable.LettersSpace);
            int i5 = NChar(c6[5], Ft8CharTable.LettersSpace);

            // Original C check: if ((i0 >= 0) && (i1 >= 0) && (i2 >= 0) && (i3 >= 0) && (i4 >= 0) && (i5 >= 0))
            // Relaxing the check slightly for calls without a digit like G4XYZ:
            // Need i0, i1, i3, i4, i5 valid for their tables. i2 must be numeric *if* c6[2] is not space.
            // Reverting to strict C check:
            if (i0 < 0 || i1 < 0 || i2 < 0 || i3 < 0 || i4 < 0 || i5 < 0)
            {
                // If c6[2] is space, i2 would be 0 (valid), otherwise it must be a valid digit.
                // This condition implicitly handles the C logic.
                return -1;
            }
            // if (i0 < 0 || i1 < 0 || i3 < 0 || i4 < 0 || i5 < 0) return -1;
            // if (c6[2] != ' ' && i2 < 0) return -1; // i2 must be valid if the char exists
            // if (c6[2] == ' ') i2 = 0; // Treat space as 0 if no digit present? C logic implies digit needed. Let's stick to C: i2 must be valid digit.
            // if (i2 < 0) return -1; // Strict check: digit must be present and valid


            // Pack according to C formula
            long n = i0;
            n = n * 36 + i1;
            n = n * 10 + i2;
            n = n * 27 + i3;
            n = n * 27 + i4;
            n = n * 27 + i5;

            return n; // Standard callsign packed value
        }

        /// <summary>
        /// Port of packgrid from message.c
        /// Packs a grid locator, report, or special token into a 16-bit value.
        /// The LSB 15 bits are the value, the MSB (bit 15) is the 'ir' flag (1 for reports starting with R).
        /// </summary>
        /// <param name="extra">Grid (4 chars), Report (+dd, -dd, R+dd, R-dd), RRR, RR73, 73, or empty</param>
        /// <param name="ir">Output: ir flag (1 if report starts with R, 0 otherwise)</param>
        /// <returns>16-bit packed value (value in lower 15 bits, ir flag in bit 15). Returns 0xFFFF on error.</returns>
        private static ushort PackGrid(string extra, out byte ir)
        {
            ir = 0;
            if (string.IsNullOrEmpty(extra))
            {
                // Two callsigns only, no report/grid -> C returns MAXGRID4 + 1
                return MAXGRID4 + 1;
            }

            extra = extra.ToUpperInvariant();

            // Special cases
            if (extra == "RRR") return MAXGRID4 + 2;
            if (extra == "RR73") return MAXGRID4 + 3;
            if (extra == "73") return MAXGRID4 + 4;

            // Check for standard 4-character grid (A-R)(A-R)(0-9)(0-9)
            if (extra.Length == 4 &&
                extra[0] >= 'A' && extra[0] <= 'R' &&
                extra[1] >= 'A' && extra[1] <= 'R' &&
                char.IsDigit(extra[2]) && char.IsDigit(extra[3]))
            {
                ushort igrid4 = (ushort)(extra[0] - 'A');
                igrid4 = (ushort)(igrid4 * 18 + (extra[1] - 'A'));
                igrid4 = (ushort)(igrid4 * 10 + (extra[2] - '0'));
                igrid4 = (ushort)(igrid4 * 10 + (extra[3] - '0'));
                // Ensure it's within the valid range 0..MAXGRID4-1
                if (igrid4 < MAXGRID4)
                {
                    return igrid4; // ir = 0 implicitly
                }
                else
                {
                    return 0xFFFF; // Should not happen if logic is correct
                }
            }

            // Check for report (+dd, -dd, R+dd, R-dd)
            // C uses dd_to_int, which parses [-+]?[0-9]+
            // Replace Regex with direct parsing similar to C
            bool parsedReport = false;
            int reportVal = 0;
            int offset = 0;
            if (extra.Length >= 1 && extra[0] == 'R')
            {
                if (extra.Length >= 4) // Need R + sign + 2 digits
                {
                    ir = 1;
                    offset = 1;
                }
                else
                {
                    return 0xFFFF; // Invalid format e.g. "R+" or "R+1"
                }
            }

            if (extra.Length - offset == 3 && (extra[offset] == '+' || extra[offset] == '-') &&
                char.IsDigit(extra[offset + 1]) && char.IsDigit(extra[offset + 2]))
            {
                // Simple inline dd_to_int logic
                int dd = (extra[offset + 1] - '0') * 10 + (extra[offset + 2] - '0');
                reportVal = (extra[offset] == '+') ? dd : -dd;
                parsedReport = true;
            }

            if (parsedReport)
            {
                // C logic: uint16_t irpt = 35 + dd;
                // Need to ensure reportVal is within a valid range implied by C's irpt calculation.
                // The C unpack code checks if irpt is between 5 and 65 (inclusive).
                // This corresponds to reportVal between -30 and +30.
                if (reportVal >= -30 && reportVal <= 30) // Check valid FT8 report range
                {
                    ushort irpt = (ushort)(35 + reportVal); // Maps [-30, +30] to [5, 65]
                    // Combine irpt and ir flag (bit 15) as C does
                    return (ushort)((MAXGRID4 + irpt) | (ir << 15));
                }
                else
                {
                    return 0xFFFF; // Report value out of range
                }
            }
            /* Old Regex implementation:
            Match reportMatch = Regex.Match(extra, @"^(R?)([+-])(\d{2})$");
            if (reportMatch.Success)
            {
                bool hasR = reportMatch.Groups[1].Value == "R";
                string sign = reportMatch.Groups[2].Value;
                string digits = reportMatch.Groups[3].Value;

                if (int.TryParse(digits, out int dd))
                {
                    if (dd >= 0 && dd <= 99) // Valid report range? C uses 0-49 for +/- reports? Check WSJT-X spec. Assuming 0-99 for now.
                    {
                        // C report encoding: irpt = 35 + (report_value). Range seems to be -30 to +30?
                        // Let's assume report value is dd. Need to map to C's irpt range.
                        // Example C code: irpt = 35 + dd; return (MAXGRID4 + irpt);
                        // If R is present, sets ir = 1.
                        // Let's map +dd to 35+dd, -dd to 35-dd ?? C logic seems simpler:
                        // int dd = dd_to_int(grid4 + offset, length); // Parses signed int
                        // uint16_t irpt = 35 + dd; // dd seems to range -30 to +30 perhaps?
                        // return (MAXGRID4 + irpt) | (ir << 15); // where ir=1 if starts with R

                        // Simplified parsing based on +/- sign directly
                        int reportVal = (sign == "+") ? dd : -dd;

                        // Map report value to irpt (needs range check based on FT8 spec)
                        // Common FT8 reports range from -30 to +30 dB.
                        // Let's clamp to that range for irpt calculation.
                        reportVal = Math.Max(-30, Math.Min(30, reportVal));
                        ushort irpt = (ushort)(35 + reportVal); // Maps [-30, +30] to [5, 65]


                        if (irpt >= 1 && irpt <= 70) // Check plausible range for irpt (1..4 are special cases)
                        {
                            ir = hasR ? (byte)1 : (byte)0;
                            return (ushort)((MAXGRID4 + irpt) | (ir << 15));
                        }
                    }
                }
            }
            */

            // If none of the above match, return error/invalid
            return 0xFFFF; // Indicate error
        }

        /// <summary>
        /// Port of charn from text.c
        /// Convert integer index to ASCII character according to one of character tables.
        /// </summary>
        private static char CharN(int c, Ft8CharTable table)
        {
            string charSet = "";
            switch (table)
            {
                case Ft8CharTable.Full: // " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ+-./?" (42 chars)
                    charSet = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ+-./?";
                    break;
                case Ft8CharTable.AlphanumSpaceSlash: // " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/" (38 chars)
                    charSet = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/";
                    break;
                case Ft8CharTable.AlphanumSpace: // " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ" (37 chars)
                    charSet = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
                    break;
                case Ft8CharTable.LettersSpace: // " ABCDEFGHIJKLMNOPQRSTUVWXYZ" (27 chars)
                    charSet = " ABCDEFGHIJKLMNOPQRSTUVWXYZ";
                    break;
                case Ft8CharTable.Alphanum: // "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ" (36 chars)
                    charSet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
                    break;
                case Ft8CharTable.Numeric: // "0123456789" (10 chars)
                    charSet = "0123456789";
                    break;
                default: return '_'; // Error
            }
            if (c >= 0 && c < charSet.Length)
            {
                return charSet[c];
            }
            System.Diagnostics.Debug.WriteLine($"CharN Error: Index {c} out of bounds for table {table}");
            return '_'; // Error or index out of bounds
        }

        /// <summary>
        /// Port of nchar from text.c
        /// Look up the index of an ASCII character in one of character tables.
        /// </summary>
        private static int NChar(char c, Ft8CharTable table)
        {
            // Ensure uppercase for lookup consistency
            // c = char.ToUpperInvariant(c); -> C code doesn't do this here, assumes caller handles case? Let's match C.

            int n = 0;
            if ((table != Ft8CharTable.Alphanum) && (table != Ft8CharTable.Numeric))
            {
                if (c == ' ') return n + 0;
                n += 1;
            }
            if (table != Ft8CharTable.LettersSpace)
            {
                if (c >= '0' && c <= '9') return n + (c - '0');
                n += 10;
            }
            if (table != Ft8CharTable.Numeric)
            {
                if (c >= 'A' && c <= 'Z') return n + (c - 'A');
                n += 26;
            }

            if (table == Ft8CharTable.Full)
            {
                if (c == '+') return n + 0;
                if (c == '-') return n + 1;
                if (c == '.') return n + 2;
                if (c == '/') return n + 3;
                if (c == '?') return n + 4;
                // n should be 1 + 10 + 26 = 37 here
                // C returns -1 if not found later.
                // Let's check if n matches expected size for Full table (42)
                // if (n + 5 != 42) -> something is wrong or char not in list
            }
            else if (table == Ft8CharTable.AlphanumSpaceSlash)
            {
                // n should be 1 + 10 + 26 = 37 here
                if (c == '/') return n + 0;
                // Check if n matches expected size (38)
            }
            else if (table == Ft8CharTable.AlphanumSpace)
            {
                // n should be 1 + 10 + 26 = 37. Size is 37. No extra chars.
            }
            else if (table == Ft8CharTable.LettersSpace)
            {
                // n should be 1 + 26 = 27. Size is 27. No extra chars.
            }
            else if (table == Ft8CharTable.Alphanum)
            {
                // n should be 10 + 26 = 36. Size is 36. No extra chars (space excluded).
            }
            else if (table == Ft8CharTable.Numeric)
            {
                // n should be 10. Size is 10. No extra chars.
            }


            // Character not found in the specified additions for Full/AlphanumSpaceSlash
            // or if it wasn't space/alphanumeric/numeric when expected.
            // We need to return -1 if the character didn't match any valid char in the table.
            // The C logic implicitly does this by falling through. Let's add explicit checks.

            switch (table)
            {
                case Ft8CharTable.Full: // Size 42: Space(1)+Num(10)+Alpha(26)+Special(5) = 42
                    if (c != ' ' && !(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'Z') && "+-./?".IndexOf(c) < 0) return -1;
                    break;
                case Ft8CharTable.AlphanumSpaceSlash: // Size 38: Space(1)+Num(10)+Alpha(26)+Slash(1)=38
                    if (c != ' ' && !(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'Z') && c != '/') return -1;
                    break;
                case Ft8CharTable.AlphanumSpace: // Size 37: Space(1)+Num(10)+Alpha(26)=37
                    if (c != ' ' && !(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'Z')) return -1;
                    break;
                case Ft8CharTable.LettersSpace: // Size 27: Space(1)+Alpha(26)=27
                    if (c != ' ' && !(c >= 'A' && c <= 'Z')) return -1;
                    break;
                case Ft8CharTable.Alphanum: // Size 36: Num(10)+Alpha(26)=36 (No space)
                    if (!(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'Z')) return -1;
                    break;
                case Ft8CharTable.Numeric: // Size 10: Num(10)
                    if (!(c >= '0' && c <= '9')) return -1;
                    break;
            }

            // If we got here, the character should have been found in the main blocks.
            // The return statements inside the blocks already returned the index.
            // If it falls through, it means it matched a main block but wasn't a special char when needed.
            // This should only happen if the character was valid for a subset of the table.
            // Let's reconsider the flow.

            // Revised flow: Reset n, check membership, calculate index directly.
            int index = -1;
            switch (table)
            {
                case Ft8CharTable.Full:
                    if (c == ' ') index = 0;
                    else if (c >= '0' && c <= '9') index = 1 + (c - '0');
                    else if (c >= 'A' && c <= 'Z') index = 1 + 10 + (c - 'A');
                    else if (c == '+') index = 1 + 10 + 26 + 0;
                    else if (c == '-') index = 1 + 10 + 26 + 1;
                    else if (c == '.') index = 1 + 10 + 26 + 2;
                    else if (c == '/') index = 1 + 10 + 26 + 3;
                    else if (c == '?') index = 1 + 10 + 26 + 4;
                    break;
                case Ft8CharTable.AlphanumSpaceSlash:
                    if (c == ' ') index = 0;
                    else if (c >= '0' && c <= '9') index = 1 + (c - '0');
                    else if (c >= 'A' && c <= 'Z') index = 1 + 10 + (c - 'A');
                    else if (c == '/') index = 1 + 10 + 26 + 0;
                    break;
                case Ft8CharTable.AlphanumSpace:
                    if (c == ' ') index = 0;
                    else if (c >= '0' && c <= '9') index = 1 + (c - '0');
                    else if (c >= 'A' && c <= 'Z') index = 1 + 10 + (c - 'A');
                    break;
                case Ft8CharTable.LettersSpace:
                    if (c == ' ') index = 0;
                    else if (c >= 'A' && c <= 'Z') index = 1 + (c - 'A');
                    break;
                case Ft8CharTable.Alphanum: // No space
                    if (c >= '0' && c <= '9') index = 0 + (c - '0');
                    else if (c >= 'A' && c <= 'Z') index = 0 + 10 + (c - 'A');
                    break;
                case Ft8CharTable.Numeric: // No space
                    if (c >= '0' && c <= '9') index = 0 + (c - '0');
                    break;
            }
            return index; // Returns -1 if not found
        }

        /// <summary>
        /// Decode protocol-specific number fields into a message
        /// </summary>
        private static string DecodeMessage(uint n28a, uint n28b, uint n15, uint n1)
        {
            // NOTE: This is the simplified implementation from original C# - Needs porting from C ftx_message_decode_std etc.
            if (n1 == 0)
            {
                // Standard message (placeholder unpack)
                string callsign1 = UnpackCallsignPlaceholder(n28a);
                string callsign2 = UnpackCallsignPlaceholder(n28b);
                string extra = UnpackGrid15Placeholder(n15);
                return $"{callsign1} {callsign2} {extra}";
            }
            else
            {
                // Free text or other format (placeholder unpack)
                return UnpackFreeTextPlaceholder(n28a, n28b, n15);
            }
        }

        // Placeholder unpacking methods from original C# - These need replacing with ports from C unpack logic (unpack28, unpackgrid etc.)
        private static string UnpackCallsignPlaceholder(uint packed) { /* Simplified logic */ return "CALL"; }
        private static string UnpackGrid15Placeholder(uint packed) { /* Simplified logic */ return "GRID"; }
        private static string UnpackReportPlaceholder(uint packed) { /* Simplified logic */ return "+00"; }
        private static string UnpackFreeTextPlaceholder(uint n28a, uint n28b, uint n15) { /* Simplified logic */ return "FREETEXT"; }

        /// <summary>
        /// Port of C unpackgrid function.
        /// Unpacks a 15-bit grid/report value combined with a 1-bit 'ir' flag.
        /// </summary>
        /// <param name="combinedGridReport">16-bit value (igrid4 in lower 15, ir in bit 15)</param>
        /// <param name="extra">Output string for the grid/report/special token</param>
        /// <returns>0 on success, -1 on error (though C returns void essentially)</returns>
        private static int UnpackGrid(ushort combinedGridReport, out string extra)
        {
            extra = string.Empty;
            StringBuilder sb = new StringBuilder();

            byte ir = (byte)(combinedGridReport >> 15);      // Extract ir flag from MSB
            ushort igrid4 = (ushort)(combinedGridReport & 0x7FFF); // Extract value from lower 15 bits

            if (igrid4 <= MAXGRID4)
            {
                // Extract 4 symbol grid locator
                if (ir > 0)
                {
                    // In case of ir=1 add an "R " before grid (unlikely for grid, but matches C logic)
                    sb.Append("R ");
                }

                ushort n = igrid4;
                char[] gridChars = new char[4];
                gridChars[3] = (char)('0' + (n % 10)); // 0..9
                n /= 10;
                gridChars[2] = (char)('0' + (n % 10)); // 0..9
                n /= 10;
                gridChars[1] = (char)('A' + (n % 18)); // A..R
                n /= 18;
                gridChars[0] = (char)('A' + (n % 18)); // A..R
                sb.Append(gridChars);
            }
            else
            {
                // Extract report or special token
                int irpt = igrid4 - MAXGRID4;

                // Check special cases first (irpt > 0 always)
                if (irpt == 1)
                    sb.Append(""); // Empty extra (e.g., "K1ABC W9XYZ")
                else if (irpt == 2)
                    sb.Append("RRR");
                else if (irpt == 3)
                    sb.Append("RR73");
                else if (irpt == 4)
                    sb.Append("73");
                else if (irpt >= 5 && irpt <= 65) // Range for report values -30 to +30 mapped to 5..65
                {
                    // Extract signal report as a two digit number with a + or - sign
                    if (ir > 0)
                    {
                        sb.Append('R'); // Add "R" before report
                    }
                    int reportVal = irpt - 35; // Convert back from irpt to report value [-30, +30]
                    sb.Append(reportVal.ToString("+00;-00;+00")); // Format as +dd or -dd
                }
                else
                {
                    // Invalid irpt value
                    extra = "? GRID ?"; // Indicate error
                    return -1;
                }
            }

            extra = sb.ToString();
            return 0; // Success
        }

        /// <summary>
        /// Port of unpack28 from message.c (extended version with hasher).
        /// Unpacks a 28-bit value (n28) combined with suffix flag (ip) and message type (i3).
        /// </summary>
        /// <param name="n28">28-bit packed value (callsign, token, or hash reference)</param>
        /// <param name="ip">Suffix flag (1 if /P or /R indicated)</param>
        /// <param name="i3">Message type (1 or 2 for standard)</param>
        /// <param name="hasher">Hasher implementation to use.</param>
        /// <param name="result">Output string for the callsign</param>
        /// <returns>0 on success, < 0 on error</returns>
        private static int Unpack28(uint n28, byte ip, byte i3, ICallsignHasher hasher, out string result)
        {
            result = string.Empty;

            // Check for special tokens DE, QRZ, CQ, CQ_nnn, CQ_aaaa
            if (n28 < NTOKENS)
            {
                if (n28 <= 2u)
                {
                    if (n28 == 0) result = "DE";
                    else if (n28 == 1) result = "QRZ";
                    else /* if (n28 == 2) */ result = "CQ";
                    return 0; // Success
                }
                // TODO: Port CQ nnn / CQ ABCD logic if needed (from C unpack28 lines 813-840)
                // These seem less common and complex to port, skip for now.
                // --- Implementing CQ nnn and CQ ABCD --- 
                if (n28 <= 1002u)
                {
                    // CQ nnn with 3 digits (n28 = 3..1002)
                    result = "CQ ";
                    uint num = n28 - 3;
                    // Format number as 3 digits (e.g., 007, 042, 999)
                    result += (num / 100 % 10).ToString();
                    result += (num / 10 % 10).ToString();
                    result += (num % 10).ToString();
                    return 0; // Success
                }
                if (n28 <= 532443u)
                {
                    // CQ ABCD with 4 alphanumeric symbols (n28 = 1003..532443)
                    // uint n = n28 - 1003u; // Original conflicting declaration
                    uint n_abcd = n28 - 1003u; // Renamed inner variable
                    char[] aaaa = new char[4];

                    // Unpack 4 chars using LettersSpace table (27 chars)
                    for (int i = 3; ; --i)
                    {
                        // aaaa[i] = CharN((int)(n % 27u), Ft8CharTable.LettersSpace); // Use renamed variable
                        aaaa[i] = CharN((int)(n_abcd % 27u), Ft8CharTable.LettersSpace);
                        if (i == 0) break;
                        // n /= 27u; // Use renamed variable
                        n_abcd /= 27u;
                    }

                    result = "CQ " + new string(aaaa).TrimStart(); // Trim leading spaces
                    return 0; // Success
                }
                // --- End CQ nnn / ABCD --- 

                result = "? TOKEN ?"; // Indicate unhandled token
                return -1;
            }

            uint n28_offset = n28 - NTOKENS;
            if (n28_offset < MAX22)
            {
                // This is a 22-bit hash of a callsign
                if (!LookupCallsign(CallsignHashType.Hash22Bits, n28_offset, hasher, out result))
                {
                    // Hash not found, result is already set to placeholder by LookupCallsign
                }
                return 0; // Success (found or placeholder)
            }

            // Standard callsign
            uint n = n28_offset - MAX22;

            char[] callsignChars = new char[6];
            // Unpack using specific character tables
            callsignChars[5] = CharN((int)(n % 27), Ft8CharTable.LettersSpace);
            n /= 27;
            callsignChars[4] = CharN((int)(n % 27), Ft8CharTable.LettersSpace);
            n /= 27;
            callsignChars[3] = CharN((int)(n % 27), Ft8CharTable.LettersSpace);
            n /= 27;
            callsignChars[2] = CharN((int)(n % 10), Ft8CharTable.Numeric);
            n /= 10;
            callsignChars[1] = CharN((int)(n % 36), Ft8CharTable.Alphanum);
            n /= 36;
            callsignChars[0] = CharN((int)(n % 37), Ft8CharTable.AlphanumSpace);

            string baseCall = new string(callsignChars).Trim(); // Trim leading/trailing spaces from the 6-char buffer

            // Handle special prefix remapping (reverse of PackBasecall)
            if (baseCall.StartsWith("3D0"))
            {
                // 3D0XYZ -> 3DA0XYZ
                if (baseCall.Length > 3) result = "3DA0" + baseCall.Substring(3);
                else result = baseCall; // Should not happen if packing was correct
            }
            else if (baseCall.StartsWith("Q") && baseCall.Length > 1 && char.IsLetter(baseCall[1]))
            {
                // QA... -> 3XA...
                result = "3X" + baseCall.Substring(1);
            }
            else
            {
                result = baseCall;
            }

            if (result.Length < 3)
            {
                result = "? SHORT ?";
                return -1; // Callsign too short
            }

            // Check if we should append /R or /P suffix based on ip and i3
            if (ip != 0)
            {
                if (i3 == 1) result += "/R";
                else if (i3 == 2) result += "/P";
                else
                {
                    // Invalid combination according to C comments
                    result = "? SUFFIX ?";
                    return -2;
                }
            }

            // Save the fully formed callsign (with suffix) to hash table
            SaveCallsign(result, hasher, out _);

            return 0; // Success
        }

        /// <summary>
        /// Port of C ftx_message_decode_std function (extended version with hasher).
        /// Decodes a standard message (Type 1 or 2) payload.
        /// </summary>
        /// <param name="payloadWithCrc">12-byte buffer containing 91-bit payload + CRC</param>
        /// <param name="hasher">Hasher implementation to use.</param>
        /// <param name="callTo">Output: Decoded destination callsign</param>
        /// <param name="callDe">Output: Decoded source callsign</param>
        /// <param name="extra">Output: Decoded grid/report/token</param>
        /// <returns>MessageResult.OK on success, error code otherwise</returns>
        private static MessageResult DecodeStandardPayload(byte[] payloadWithCrc, ICallsignHasher hasher, out string callTo, out string callDe, out string extra)
        {
            callTo = string.Empty;
            callDe = string.Empty;
            extra = string.Empty;

            if (payloadWithCrc == null || payloadWithCrc.Length < 12)
            {
                return MessageResult.ErrorInvalidLength;
            }

            // Extract packed fields directly from the 12-byte (91-bit) buffer
            // Based on C ftx_message_decode_std bit extraction
            uint n29a = ((uint)payloadWithCrc[0] << 21);
            n29a |= ((uint)payloadWithCrc[1] << 13);
            n29a |= ((uint)payloadWithCrc[2] << 5);
            n29a |= ((uint)payloadWithCrc[3] >> 3);

            uint n29b = (((uint)payloadWithCrc[3] & 0x07u) << 26);
            n29b |= ((uint)payloadWithCrc[4] << 18);
            n29b |= ((uint)payloadWithCrc[5] << 10);
            n29b |= ((uint)payloadWithCrc[6] << 2);
            n29b |= ((uint)payloadWithCrc[7] >> 6);

            // Combined grid/report field (16 bits)
            ushort combinedGridReport = (ushort)(((uint)payloadWithCrc[7] & 0x3Fu) << 10); // Get lower 6 bits of byte 7, shift left 10
            combinedGridReport |= (ushort)((uint)payloadWithCrc[8] << 2);         // Get byte 8, shift left 2
            combinedGridReport |= (ushort)((uint)payloadWithCrc[9] >> 6);         // Get upper 2 bits of byte 9

            // Extract i3 (message type indicator, bits 74..76 of original 77bit packing)
            // These are bits 5, 4, 3 of byte 9 in the 91-bit structure
            byte i3 = (byte)((payloadWithCrc[9] >> 3) & 0x07u);

            // Unpack callsigns using the ported Unpack28
            if (Unpack28(n29a >> 1, (byte)(n29a & 1u), i3, hasher, out callTo) < 0)
            {
                return MessageResult.ErrorInvalidCallsign; // Error in first callsign
            }
            if (Unpack28(n29b >> 1, (byte)(n29b & 1u), i3, hasher, out callDe) < 0)
            {
                return MessageResult.ErrorInvalidCallsign; // Error in second callsign
            }

            // Unpack grid/report using the ported UnpackGrid
            if (UnpackGrid(combinedGridReport, out extra) < 0)
            {
                return MessageResult.ErrorInvalidLocator; // Error in grid/report
            }

            return MessageResult.OK;
        }

        /// <summary>
        /// Extract the message from the 77-bit payload
        /// </summary>
        /// <param name="payload">77-bit payload (as 10 bytes)</param>
        /// <returns>A new message instance or null if invalid</returns>
        public static Message? FromPayload(byte[] payload)
        {
            // NOTE: Uses simplified/placeholder DecodeMessage - Needs porting from C
            if (payload == null || payload.Length != 10)
            {
                return null; // Invalid
            }

            // This function should ideally not be used directly if FromPayloadWithCrc is the entry point.
            // It expects a 77-bit payload, but the unpacking logic needs the full 91 bits usually.
            // For now, mark as invalid if called directly.

            Message decodedMsg = new Message("");
            decodedMsg.Type = MessageType.Invalid; // Mark as invalid until properly decoded from 91 bits
            return decodedMsg;
        }

        /// <summary>
        /// Extract the message from the 91-bit payload with CRC
        /// </summary>
        /// <param name="payloadWithCrc">91-bit payload with CRC (as 12 bytes)</param>
        /// <returns>A new message instance or null if CRC check fails or decoding fails</returns>
        public static Message? FromPayloadWithCrc(byte[] payloadWithCrc)
        {
            // Use default internal hasher for now
            return FromPayloadWithCrc(payloadWithCrc, defaultHasher);
        }

        /// <summary>
        /// Extract the message from the 91-bit payload with CRC (extended version with hasher)
        /// </summary>
        /// <param name="payloadWithCrc">91-bit payload with CRC (as 12 bytes)</param>
        /// <param name="hasher">Hasher implementation to use.</param>
        /// <returns>A new message instance or null if CRC check fails or decoding fails</returns>
        public static Message? FromPayloadWithCrc(byte[] payloadWithCrc, ICallsignHasher hasher)
        {
            if (payloadWithCrc == null || payloadWithCrc.Length != 12)
            {
                return null;
            }

            // Check CRC using updated Crc class
            if (!Crc.CheckCrc(payloadWithCrc, Constants.FTX_LDPC_K)) // Check 91 bits
            {
                System.Diagnostics.Debug.WriteLine("CRC Check Failed");
                return null; // CRC check failed
            }

            // Determine message type based on i3 / n3
            byte i3 = (byte)((payloadWithCrc[9] >> 3) & 0x07u);
            byte n3 = (byte)(((payloadWithCrc[8] << 2) & 0x04u) | ((payloadWithCrc[9] >> 6) & 0x03u));

            MessageType msgType = MessageType.Invalid;
            // Logic based on C ftx_message_get_type and WSJT-X pack.f90 comments
            if (i3 == 0)
            {
                // 71-bit messages
                if (n3 == 0) msgType = MessageType.FreeText;       // Type 0.0
                else if (n3 == 1) msgType = MessageType.NonStandard; // Type 0.1 DXpedition - treat as NonStd for now
                else if (n3 == 2) msgType = MessageType.NonStandard; // Type 0.2 EU_VHF - treat as NonStd for now
                else if (n3 == 3 || n3 == 4) msgType = MessageType.NonStandard; // Type 0.3/0.4 ARRL_FD - treat as NonStd for now
                else if (n3 == 5) msgType = MessageType.Telemetry;      // Type 0.5
                // n3 = 6, 7 are undefined/reserved?
            }
            else if (i3 == 1 || i3 == 2) // 77-bit standard messages
            {
                msgType = MessageType.Standard; // Type 1.x / 2.x
            }
            else if (i3 == 3) msgType = MessageType.NonStandard; // Type 3 ARRL_RTTY - treat as NonStd for now
            else if (i3 == 4) msgType = MessageType.NonStandard; // Type 4 Nonstandard Call - Needs specific decoder
            else if (i3 == 5) msgType = MessageType.NonStandard; // Type 5 WWROF - treat as NonStd for now
            // i3 = 6, 7 are undefined/reserved?


            // Attempt decoding based on determined type
            string callTo, callDe, extra, content;
            MessageResult decodeResult = MessageResult.ErrorDecode;
            Message decodedMsg = new Message(string.Empty) { Type = MessageType.Invalid }; // Start with invalid

            switch (msgType)
            {
                case MessageType.Standard:
                    decodeResult = DecodeStandardPayload(payloadWithCrc, hasher, out callTo, out callDe, out extra);
                    if (decodeResult == MessageResult.OK)
                    {
                        content = $"{callTo} {callDe} {extra}".Trim().Replace("  ", " "); // Reconstruct content
                        decodedMsg = new Message(content); // Re-parse to set internal state correctly
                        if (decodedMsg.Type != MessageType.Invalid) // Check if basic parsing succeeded
                        {
                            // Ensure the decoded components match the parser's understanding
                            decodedMsg.Callsign1 = callTo;
                            decodedMsg.Callsign2 = callDe;
                            if (IsGridLocator(extra)) decodedMsg.Grid = extra;
                            else decodedMsg.Report = extra; // Assume report or special token
                            decodedMsg.Type = MessageType.Standard; // Force type based on decode path
                        }
                        else
                        {
                            // Parsing failed, but decode was OK? Log or handle discrepancy
                            System.Diagnostics.Debug.WriteLine($"Standard decode OK, but re-parsing failed for: {content}");
                            decodedMsg.Type = MessageType.Invalid; // Mark as invalid if re-parse fails
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Standard decode failed with result: {decodeResult}");
                    }
                    break;

                case MessageType.FreeText:
                    //decodeResult = DecodeFreeTextPayload(payloadWithCrc, out content);
                    decodeResult = DecodeFreeTextPayload(payloadWithCrc, out content);
                    if (decodeResult == MessageResult.OK)
                    {
                        decodedMsg = new Message(content) { Type = MessageType.FreeText };
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Free Text decode failed with result: {decodeResult}");
                    }
                    break;

                case MessageType.Telemetry:
                    //decodeResult = DecodeTelemetryHex(payloadWithCrc, out content);
                    byte[] telemetryBytes = new byte[9];
                    if (DecodeTelemetryPayload(payloadWithCrc, telemetryBytes))
                    {
                        // Convert the 9 bytes (71 bits) to a hex string
                        content = BitConverter.ToString(telemetryBytes).Replace("-", "");
                        // The C code might expect a specific format or length (e.g., 18 hex chars)
                        // Let's assume this hex representation is sufficient for now.
                        decodeResult = MessageResult.OK;
                    }
                    else
                    {
                        content = string.Empty;
                        decodeResult = MessageResult.ErrorDecode;
                    }

                    if (decodeResult == MessageResult.OK)
                    {
                        // Store hex telemetry data directly in Content for now
                        decodedMsg = new Message(content) { Type = MessageType.Telemetry };
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Telemetry decode failed with result: {decodeResult}");
                    }
                    break;

                // TODO: Add cases for other NonStandard message types (DXpedition, EU_VHF, ARRL_FD, ARRL_RTTY, Type 4, WWROF)
                // These require porting specific packing/unpacking logic from C.
                case MessageType.NonStandard:
                    // Placeholder: Indicate it's a known non-standard type but can't decode details yet
                    decodedMsg = new Message($"<NonStd i3={i3} n3={n3}>") { Type = MessageType.NonStandard };
                    System.Diagnostics.Debug.WriteLine($"Non-standard message type i3={i3}, n3={n3} not fully decoded.");
                    break;

                default:
                    // Type determined as Invalid or unhandled
                    System.Diagnostics.Debug.WriteLine($"Invalid or unknown message type i3={i3}, n3={n3}");
                    break;
            }

            // Return the decoded message (might still be marked Invalid if decoding failed)
            return decodedMsg;
        }

        /// <summary>
        /// Returns a string representation of the message
        /// </summary>
        public override string ToString()
        {
            return Content;
        }

        // +++ Add SaveCallsign method +++
        /// <summary>
        /// Port of C static function save_callsign.
        /// Compute hash value for a callsign and save it in a hash table via the provided callsign hash interface.
        /// </summary>
        /// <param name="callsign">Callsign (up to 11 characters, trimmed, no brackets).</param>
        /// <param name="hasher">Hasher implementation to use.</param>
        /// <param name="n22_out">Output: 22-bit hash value.</param>
        /// <returns>True on success, false if callsign contains invalid characters.</returns>
        private static bool SaveCallsign(string callsign, ICallsignHasher hasher, out uint n22_out)
        {
            n22_out = 0;
            if (string.IsNullOrEmpty(callsign) || callsign.Length > 11) return false; // Basic validation

            // Remove brackets if present (though caller should ideally handle this)
            if (callsign.StartsWith("<") && callsign.EndsWith(">") && callsign.Length > 2)
            {
                callsign = callsign.Substring(1, callsign.Length - 2);
            }

            ulong n58 = 0;
            int i = 0;
            while (i < callsign.Length) // Iterate only over actual characters
            {
                int j = NChar(callsign[i], Ft8CharTable.AlphanumSpaceSlash);
                if (j < 0)
                    return false; // Hash error (wrong character set)
                n58 = (38 * n58) + (ulong)j;
                i++;
            }
            // Pad with virtual spaces up to 11 characters
            while (i < 11)
            {
                n58 = (38 * n58); // Equivalent to adding j=0 (space index)
                i++;
            }

            // Perform the hash calculation using the magic number from C
            // uint32_t n22 = ((47055833459ull * n58) >> (64 - 22)) & (0x3FFFFFul);
            const ulong magicMultiplier = 47055833459UL;
            n22_out = (uint)((magicMultiplier * n58) >> (64 - 22)) & 0x3FFFFFu;

            // Optional: derive n12 and n10 if needed (not strictly required by C interface)
            // uint n12 = n22_out >> 10;
            // uint n10 = n22_out >> 12;
            // System.Diagnostics.Debug.WriteLine($"SaveCallsign('{callsign}') = [n58={n58:X}, n22={n22_out}, n12={n12}, n10={n10}]");

            // Call the hasher interface to save the result
            hasher?.SaveHash(callsign, n22_out);

            return true;
        }

        // +++ Add LookupCallsign method +++
        /// <summary>
        /// Port of C static function lookup_callsign.
        /// Looks up a callsign by its hash in the provided hash table.
        /// </summary>
        /// <param name="hashType">Type of hash to lookup (22, 12, or 10 bits).</param>
        /// <param name="hash">The hash value.</param>
        /// <param name="hasher">Hasher implementation to use.</param>
        /// <param name="callsign">Output: Callsign string, including brackets e.g., "<CALLSIGN>". Sets to "<...>" if not found.</param>
        /// <returns>True if found, false otherwise.</returns>
        private static bool LookupCallsign(CallsignHashType hashType, uint hash, ICallsignHasher hasher, out string callsign)
        {
            bool found = false;
            string lookedUpCall = string.Empty;

            if (hasher != null)
            {
                found = hasher.LookupHash(hashType, hash, out lookedUpCall);
            }

            if (!found)
            {
                callsign = "<...>"; // Placeholder for not found
            }
            else
            {
                // Add brackets as per C logic
                callsign = $"<{lookedUpCall}>";
            }

            // string typeStr = hashType switch { CallsignHashType.Hash22Bits => "22", CallsignHashType.Hash12Bits => "12", CallsignHashType.Hash10Bits => "10", _ => "?" };
            // System.Diagnostics.Debug.WriteLine($"LookupCallsign(n{typeStr}={hash}) = '{callsign}'");
            return found;
        }

        /// <summary>
        /// Decodes the 71-bit data portion used by Telemetry (0.5) and Free Text (0.0) messages.
        /// Port of C ftx_message_decode_telemetry function.
        /// It performs a 1-bit right shift on the first 72 bits (9 bytes) of the payload.
        /// </summary>
        /// <param name="payloadWithCrc">The 12-byte buffer containing the 91-bit payload+CRC.</param>
        /// <param name="telemetryOutput9Bytes">Output: 9-byte array to store the extracted 71 bits (right-aligned).</param>
        /// <returns>True if successful, false if input is invalid.</returns>
        private static bool DecodeTelemetryPayload(byte[] payloadWithCrc, byte[] telemetryOutput9Bytes)
        {
            if (payloadWithCrc == null || payloadWithCrc.Length < 12 || telemetryOutput9Bytes == null || telemetryOutput9Bytes.Length < 9)
            {
                return false;
            }

            // C logic: Shift bits in payload right by 1 bit to right-align the data
            // Processes the first 9 bytes (payloadWithCrc[0]..[8])
            byte carry = 0;
            for (int i = 0; i < 9; ++i)
            {
                // Save the LSB of the current byte to become the carry for the next byte
                byte nextCarry = (byte)(payloadWithCrc[i] & 0x01u);
                // Right shift the current byte, and OR the MSB with the previous carry shifted left
                telemetryOutput9Bytes[i] = (byte)((carry << 7) | (payloadWithCrc[i] >> 1));
                carry = nextCarry;
            }
            return true;
        }

        /// <summary>
        /// Decodes a Free Text message (Type 0.0) payload.
        /// Port of C ftx_message_decode_free function.
        /// </summary>
        /// <param name="payloadWithCrc">12-byte buffer containing 91-bit payload + CRC</param>
        /// <param name="text">Output: Decoded free text message (max 13 chars)</param>
        /// <returns>MessageResult.OK on success, error code otherwise</returns>
        private static MessageResult DecodeFreeTextPayload(byte[] payloadWithCrc, out string text)
        {
            text = string.Empty;
            byte[] b71_shifted = new byte[9];

            if (!DecodeTelemetryPayload(payloadWithCrc, b71_shifted))
            {
                return MessageResult.ErrorDecode;
            }

            // Manual BigInteger conversion from big-endian bytes
            BigInteger b71_bigint = 0;
            for (int i = 0; i < 9; i++)
            {
                b71_bigint = (b71_bigint << 8) | b71_shifted[i];
            }


            // C loops 13 times (0..12) to extract 13 chars max
            char[] c14_reversed = new char[13];
            int charCount = 0;
            for (int idx = 0; idx < 13; ++idx)
            {
                // Divide the long integer by 42
                BigInteger remainder = b71_bigint % 42;
                BigInteger quotient = b71_bigint / 42;

                int rem_int = (int)remainder; // Remainder is guaranteed to be 0-41

                // Convert remainder to character using the full table
                c14_reversed[idx] = CharN(rem_int, Ft8CharTable.Full);
                charCount++;

                // Update the number for the next iteration
                b71_bigint = quotient;

                // Early exit if number becomes 0 (no more significant characters)
                if (b71_bigint == 0)
                {
                    break;
                }
            }
            // Resize the array to the actual number of characters found
            Array.Resize(ref c14_reversed, charCount);


            // The characters are generated in reverse order (LSB first). Reverse them.
            Array.Reverse(c14_reversed);

            // C uses a trim function (likely removes leading/trailing spaces)
            text = new string(c14_reversed).Trim();

            return MessageResult.OK;
        }

        /// <summary>
        /// Encodes a Free Text message (Type 0.0) into the 77-bit payload structure.
        /// </summary>
        /// <param name="text">Input free text (max 13 chars, using Full charset).</param>
        /// <param name="outputPayload10Bytes">Output: 10-byte array for the 77-bit payload.</param>
        /// <returns>True on success, false on error (invalid chars, too long).</returns>
        private static bool EncodeFreeTextPayload(string text, byte[] outputPayload10Bytes)
        {
            if (text == null || text.Length > 13 || outputPayload10Bytes == null || outputPayload10Bytes.Length < 10)
            {
                return false;
            }

            // Ensure the text only contains valid characters from the Full table
            BigInteger b71_bigint = 0;
            foreach (char c_orig in text)
            {
                char c = char.ToUpperInvariant(c_orig); // Convert to upper like NChar expects potentially
                int j = NChar(c, Ft8CharTable.Full);
                if (j < 0)
                {
                    System.Diagnostics.Debug.WriteLine($"Invalid character '{c}' in free text for encoding.");
                    return false; // Invalid character found
                }
                b71_bigint = (b71_bigint * 42) + j;
            }

            // Convert the BigInteger (max 71 bits) back to a 9-byte array (big-endian)
            // byte[] b71_bytes_raw = b71_bigint.ToByteArray(isBigEndian: true);
            byte[] b71_bytes_raw = b71_bigint.ToByteArray(); // Returns little-endian
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(b71_bytes_raw); // Convert to big-endian if necessary
            }

            // Ensure we have 9 bytes, padding with leading zeros if necessary (for big-endian representation).
            byte[] b71_bytes = new byte[9];
            if (b71_bytes_raw.Length > 9)
            {
                if (b71_bytes_raw.Length == 10 && b71_bytes_raw[0] == 0x00)
                {
                    Array.Copy(b71_bytes_raw, 1, b71_bytes, 0, 9);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("Error: BigInteger for free text exceeds 71 bits.");
                    return false; // Exceeds 71 bits
                }
            }
            else
            {
                int offset = 9 - b71_bytes_raw.Length;
                Array.Copy(b71_bytes_raw, 0, b71_bytes, offset, b71_bytes_raw.Length);
            }

            // Place the 71 bits (b71_bytes) left-shifted by 1 into payload bits 76..6.
            // Use BigInteger for the shift:
            // BigInteger data_int = new BigInteger(b71_bytes, isBigEndian: true);
            byte[] b71_bytes_for_bigint = (byte[])b71_bytes.Clone(); // Clone to avoid modifying original
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(b71_bytes_for_bigint); // BigInteger constructor expects little-endian
            }
            // Ensure positive number for BigInteger constructor (needs leading zero byte if MSB is set)
            if ((b71_bytes_for_bigint[b71_bytes_for_bigint.Length - 1] & 0x80) != 0)
            {
                Array.Resize(ref b71_bytes_for_bigint, b71_bytes_for_bigint.Length + 1);
                b71_bytes_for_bigint[b71_bytes_for_bigint.Length - 1] = 0;
            }
            BigInteger data_int = new BigInteger(b71_bytes_for_bigint);

            data_int <<= 1;

            // Get the shifted data back into bytes (max 72 bits now)
            // byte[] shifted_data_bytes_raw = data_int.ToByteArray(isBigEndian: true);
            byte[] shifted_data_bytes_raw = data_int.ToByteArray(); // Returns little-endian
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(shifted_data_bytes_raw); // Convert to big-endian if necessary
            }

            // Pad/trim shifted_data_bytes to exactly 9 bytes (representing bits 71..0 of shifted value, big-endian)
            byte[] final_9_bytes = new byte[9];
            if (shifted_data_bytes_raw.Length > 9)
            {
                if (shifted_data_bytes_raw.Length == 10 && shifted_data_bytes_raw[0] == 0x00)
                {
                    Array.Copy(shifted_data_bytes_raw, 1, final_9_bytes, 0, 9);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("Error: Shifted BigInteger for free text exceeds 72 bits unexpectedly.");
                    return false;
                }
            }
            else
            {
                int offset = 9 - shifted_data_bytes_raw.Length;
                Array.Copy(shifted_data_bytes_raw, 0, final_9_bytes, offset, shifted_data_bytes_raw.Length);
            }

            // Copy these 9 bytes into the payload buffer
            Array.Clear(outputPayload10Bytes, 0, 10);
            Array.Copy(final_9_bytes, 0, outputPayload10Bytes, 0, 9);

            // Ensure bits 5..0 (i3=0, n3=0) are zero.
            // Bits 5..0 are payload[8] bit 0 and payload[9] bits 4..0.
            outputPayload10Bytes[8] &= 0xFE; // Clear bit 0
            outputPayload10Bytes[9] &= 0xE0; // Clear bits 4..0

            return true;
        }
    }
}
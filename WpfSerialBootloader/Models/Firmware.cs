using System.IO;
using WpfSerialBootloader.Utils;

namespace WpfSerialBootloader.Models
{
    /// <summary>
    /// Handles reading, parsing, and preparing the firmware for upload.
    /// </summary>
    public class Firmware
    {
        public byte[] MagicBytes { get; }
        public byte[] SizeBytes { get; }
        public byte[] Payload { get; }
        public byte[] CrcBytes { get; }
        public int TotalSize => Payload.Length;

        /// <summary>
        /// Reads a hex file and prepares the firmware package.
        /// </summary>
        /// <param name="filePath">The path to the firmware .hex file.</param>
        public Firmware(string filePath)
        {
            // 1. Read and parse payload from hex file
            var payloadList = new List<byte>();
            var lines = File.ReadLines(filePath);
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine)) continue;

                uint word = Convert.ToUInt32(trimmedLine, 16);
                payloadList.AddRange(BitConverter.GetBytes(word)); // Little-endian by default
            }
            Payload = payloadList.ToArray();

            if (Payload.Length == 0)
            {
                throw new InvalidDataException("HEX file is empty or contains no valid data.");
            }

            // 2. Prepare header: Magic Word (Big-Endian) and Size (Little-Endian)
            uint magic = 0xDEADBEEF;
            MagicBytes = BitConverter.GetBytes(magic);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(MagicBytes);
            }

            SizeBytes = BitConverter.GetBytes((uint)Payload.Length);

            // 3. Calculate CRC32
            var dataForCrc = new List<byte[]> { SizeBytes, Payload };
            uint crc = Crc32Util.Calculate(dataForCrc);
            CrcBytes = BitConverter.GetBytes(crc);
        }
    }
}

namespace WpfSerialBootloader.Utils
{
    /// <summary>
    /// Computes a CRC32 checksum compatible with the specific configuration used in the Python script.
    /// Configuration:
    ///  - Polynomial: 0x04C11DB7
    ///  - Initial Value: 0xFFFFFFFF
    ///  - Final XOR Value: 0xFFFFFFFF
    ///  - Reverse Input: True
    ///  - Reverse Output: True
    /// </summary>
    public static class Crc32Util
    {
        private const uint Polynomial = 0x04C11DB7;
        private static readonly uint[] table;

        static Crc32Util()
        {
            table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint entry = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((entry & 1) == 1)
                        entry = (entry >> 1) ^ Polynomial;
                    else
                        entry >>= 1;
                }
                table[i] = entry;
            }
        }

        /// <summary>
        /// Calculates the CRC32 checksum for a collection of byte arrays.
        /// </summary>
        /// <param name="dataParts">A list of byte arrays to be included in the checksum calculation.</param>
        /// <returns>The 32-bit CRC checksum.</returns>
        public static uint Calculate(IEnumerable<byte[]> dataParts)
        {
            // Initial and final XOR values are both 0xFFFFFFFF.
            // This is equivalent to starting with ~0U and returning ~crc.
            uint crc = 0xFFFFFFFF;

            foreach (var data in dataParts)
            {
                foreach (byte b in data)
                {
                    // Input is reversed, so we process byte by byte.
                    crc = (crc >> 8) ^ table[(crc ^ b) & 0xFF];
                }
            }

            // Final XOR and output reversal are implicitly handled by the standard algorithm used.
            // The final result needs to be inverted.
            return ~crc;
        }
    }
}

using System.Buffers.Binary;
using MdlFile = Penumbra.GameData.Files.Models.MdlFile;

namespace Intoner.Objects.Assets
{
    internal static class ModelFileReader
    {
        public static MdlFile? Read(byte[]? data)
        {
            if (data is null || !HasSupportedHeader(data))
            {
                return null;
            }

            try
            {
                MdlFile file = new(data);
                return file.Valid ? file : null;
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                return null;
            }
        }

        /// <summary> checks supported MDL versions and the declared metadata extent </summary>
        private static bool HasSupportedHeader(ReadOnlySpan<byte> data)
        {
            if (data.Length < MdlFile.FileHeaderSize)
            {
                return false;
            }

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(data);

            if (version is not (MdlFile.V5 or MdlFile.V6))
            {
                return false;
            }

            ulong metadataEnd = MdlFile.FileHeaderSize
                + (ulong)BinaryPrimitives.ReadUInt32LittleEndian(data[4..])
                + BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
            return metadataEnd <= (ulong)data.Length;
        }
    }
}

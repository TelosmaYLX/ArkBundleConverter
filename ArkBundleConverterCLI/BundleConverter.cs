using K4os.Compression.LZ4;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
// 移除了不必要的 using 语句
// using static System.Windows.Forms.VisualStyles.VisualStyleElement;
// using System.Xml.Linq;
// using System.Reflection.PortableExecutable;
using System.Numerics;

// 复用 AssetStudio
public enum ArchiveFlags
{
    CompressionTypeMask = 0x3f,
    BlocksAndDirectoryInfoCombined = 0x40,
    BlocksInfoAtTheEnd = 0x80,
    OldWebPluginCompatibility = 0x100,
    BlockInfoNeedPaddingAtStart = 0x200
}

[Flags]
public enum StorageBlockFlags
{
    CompressionTypeMask = 0x3f,
    Streamed = 0x40
}

public enum CompressionType
{
    None,
    Lzma,
    Lz4,
    Lz4HC,
    ArkLz4
}
public class Header
{
    public string signature;
    public uint version;
    public string unityVersion;
    public string unityRevision;
    public long size;
    public uint compressedBlocksInfoSize;
    public uint uncompressedBlocksInfoSize;
    public ArchiveFlags flags;
}

public class StorageBlock
{
    public uint compressedSize;
    public uint uncompressedSize;
    public StorageBlockFlags flags;
}

public class Node
{
    public long offset;
    public long size;
    public uint flags;
    public string path;
}

public class StreamFile
{
    public string path;
    public string fileName;
    public Stream stream;
}

public enum EndianType
{
    LittleEndian,
    BigEndian
}


public class BundleConverter
{
    #region Decompression Logic (Existing)

    private static (int length, int newPos) ReadLongLength(byte[] data, int pos)
    {
        int length = 0;
        byte b;
        int bytesRead = 0;
        do
        {
            if (pos >= data.Length)
            {
                throw new EndOfStreamException("Failed to read long length: unexpected end of data.");
            }
            b = data[pos];
            pos++;
            bytesRead++;
            length += b;
        } while (b == 255);
        return (length, pos);
    }

    private static void DecompressCustomLzham(byte[] compressedBytes, int compressedSize, byte[] uncompressedBytes, int uncompressedSize)
    {
        var modifiedCompressedBytes = ArrayPool<byte>.Shared.Rent(compressedSize);
        try
        {
            Buffer.BlockCopy(compressedBytes, 0, modifiedCompressedBytes, 0, compressedSize);

            // --- Start Pre-processing (ArkLz4 -> Standard Lz4) ---
            int ip = 0;
            long op = 0;
            const int AK_LITERAL_LENGTH_MASK = 0x0F;
            const int AK_MATCH_LENGTH_MASK = 0xF0;

            while (ip < compressedSize)
            {
                if (ip >= compressedSize) break;
                byte controlByte = modifiedCompressedBytes[ip];
                int literalLength = controlByte & AK_LITERAL_LENGTH_MASK;
                int matchLength = (controlByte & AK_MATCH_LENGTH_MASK) >> 4;
                modifiedCompressedBytes[ip] = (byte)((literalLength << 4) | matchLength); // Swap nibbles
                ip++;
                if (literalLength == 15) { var (longLen, newIp) = ReadLongLength(modifiedCompressedBytes, ip); literalLength += longLen; ip = newIp; }
                op += literalLength;
                ip += literalLength;
                if (uncompressedSize - op < 12 || ip + 1 >= compressedSize) break;
                byte offsetByte1 = modifiedCompressedBytes[ip];
                byte offsetByte2 = modifiedCompressedBytes[ip + 1];
                ushort offset = (ushort)(offsetByte2 | (offsetByte1 << 8)); // Read Big Endian
                modifiedCompressedBytes[ip] = (byte)(offset & 0xFF);         // Write Little Endian
                modifiedCompressedBytes[ip + 1] = (byte)(offset >> 8);
                ip += 2;
                if (matchLength == 15) { if (ip >= compressedSize) break; var (longLen, newIp) = ReadLongLength(modifiedCompressedBytes, ip); matchLength += longLen; ip = newIp; }
                matchLength += 4;
                op += matchLength;
            }
            // --- End Pre-processing ---

            var numWrite = LZ4Codec.Decode(modifiedCompressedBytes, 0, compressedSize, uncompressedBytes, 0, uncompressedSize);
            if (numWrite != uncompressedSize)
            {
                throw new IOException($"Custom Lz4 decompression error, wrote {numWrite} bytes but expected {uncompressedSize} bytes after pre-processing");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(modifiedCompressedBytes);
        }
    }

    public void ConvertToUncompressed(string inputPath, string outputPath)
    {
        using (var reader = new EndianBinaryReader(File.OpenRead(inputPath), EndianType.BigEndian))
        {
            // --- 1. 读取原始 Bundle ---
            string signature = reader.ReadStringToNull();
            uint version = reader.ReadUInt32();
            string unityVersion = reader.ReadStringToNull();
            string unityRevision = reader.ReadStringToNull();

            if (signature != "UnityFS")
            {
                throw new NotSupportedException($"Unsupported bundle signature: {signature}");
            }

            Header originalHeader = new Header
            {
                signature = signature,
                version = version,
                unityVersion = unityVersion,
                unityRevision = unityRevision
            };
            ReadHeader(reader, originalHeader);

            long blocksInfoPosition = reader.BaseStream.Position;
            if (originalHeader.version >= 7)
            {
                reader.AlignStream(16);
                blocksInfoPosition = reader.BaseStream.Position;
            }

            byte[] compressedBlocksInfoBytes;
            if ((originalHeader.flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0)
            {
                long currentPos = reader.BaseStream.Position;
                reader.BaseStream.Position = reader.BaseStream.Length - originalHeader.compressedBlocksInfoSize;
                compressedBlocksInfoBytes = reader.ReadBytes((int)originalHeader.compressedBlocksInfoSize);
                reader.BaseStream.Position = currentPos;
            }
            else
            {
                compressedBlocksInfoBytes = reader.ReadBytes((int)originalHeader.compressedBlocksInfoSize);
            }

            long dataBlocksStartPosition;
            if ((originalHeader.flags & ArchiveFlags.BlocksInfoAtTheEnd) == 0)
            {
                dataBlocksStartPosition = reader.BaseStream.Position;
                if ((originalHeader.flags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
                {
                    dataBlocksStartPosition = (dataBlocksStartPosition + 15) & ~15L;
                }
            }
            else
            {
                dataBlocksStartPosition = blocksInfoPosition;
            }

            MemoryStream blocksInfoUncompressedStream;
            var blocksInfoCompressionType = (CompressionType)(originalHeader.flags & ArchiveFlags.CompressionTypeMask);
            switch (blocksInfoCompressionType)
            {
                case CompressionType.None:
                    blocksInfoUncompressedStream = new MemoryStream(compressedBlocksInfoBytes);
                    break;
                case CompressionType.Lz4:
                case CompressionType.Lz4HC:
                    var uncompressedBytes = new byte[originalHeader.uncompressedBlocksInfoSize];
                    var numWrite = LZ4Codec.Decode(compressedBlocksInfoBytes, uncompressedBytes);
                    if (numWrite != originalHeader.uncompressedBlocksInfoSize) throw new IOException("BlocksInfo decompression failed");
                    blocksInfoUncompressedStream = new MemoryStream(uncompressedBytes);
                    break;
                default:
                    throw new IOException($"Unsupported BlocksInfo compression type {blocksInfoCompressionType}");
            }

            StorageBlock[] originalBlocksInfo;
            Node[] originalDirectoryInfo;
            byte[] originalBlocksInfoHash;
            using (var blocksInfoReader = new EndianBinaryReader(blocksInfoUncompressedStream, EndianType.BigEndian))
            {
                originalBlocksInfoHash = blocksInfoReader.ReadBytes(16);
                var blocksInfoCount = blocksInfoReader.ReadInt32();
                originalBlocksInfo = new StorageBlock[blocksInfoCount];
                for (int i = 0; i < blocksInfoCount; i++)
                    originalBlocksInfo[i] = new StorageBlock
                    {
                        uncompressedSize = blocksInfoReader.ReadUInt32(),
                        compressedSize = blocksInfoReader.ReadUInt32(),
                        flags = (StorageBlockFlags)blocksInfoReader.ReadUInt16()
                    };

                var nodesCount = blocksInfoReader.ReadInt32();
                originalDirectoryInfo = new Node[nodesCount];
                for (int i = 0; i < nodesCount; i++)
                {
                    originalDirectoryInfo[i] = new Node
                    {
                        offset = blocksInfoReader.ReadInt64(),
                        size = blocksInfoReader.ReadInt64(),
                        flags = blocksInfoReader.ReadUInt32(),
                        path = blocksInfoReader.ReadStringToNull(),
                    };
                }
            }

            // --- 2. 解压所有数据块 ---
            using (var decompressedDataStream = new MemoryStream())
            {
                reader.BaseStream.Position = dataBlocksStartPosition;
                long totalUncompressedDataSize = 0;

                foreach (var blockInfo in originalBlocksInfo)
                {
                    byte[] compressedBytes = reader.ReadBytes((int)blockInfo.compressedSize);
                    byte[] uncompressedBytes = ArrayPool<byte>.Shared.Rent((int)blockInfo.uncompressedSize);
                    try
                    {
                        var blockCompressionType = (CompressionType)(blockInfo.flags & StorageBlockFlags.CompressionTypeMask);
                        switch (blockCompressionType)
                        {
                            case CompressionType.None:
                                Buffer.BlockCopy(compressedBytes, 0, uncompressedBytes, 0, (int)blockInfo.uncompressedSize);
                                break;
                            case CompressionType.Lz4:
                            case CompressionType.ArkLz4:
                                DecompressCustomLzham(compressedBytes, compressedBytes.Length, uncompressedBytes, (int)blockInfo.uncompressedSize);
                                break;
                            case CompressionType.Lz4HC:
                                var decoded = LZ4Codec.Decode(compressedBytes, 0, compressedBytes.Length, uncompressedBytes, 0, (int)blockInfo.uncompressedSize);
                                if (decoded != blockInfo.uncompressedSize) throw new IOException($"Lz4HC decompression error for block.");
                                break;
                            default:
                                throw new IOException($"Unsupported block compression type {blockCompressionType}");
                        }
                        decompressedDataStream.Write(uncompressedBytes, 0, (int)blockInfo.uncompressedSize);
                        totalUncompressedDataSize += blockInfo.uncompressedSize;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(uncompressedBytes);
                    }
                }

                // --- 3. 准备写入新 Bundle ---
                StorageBlock[] newBlocksInfo = new StorageBlock[originalBlocksInfo.Length];
                for (int i = 0; i < originalBlocksInfo.Length; i++)
                {
                    var oldBlock = originalBlocksInfo[i];
                    newBlocksInfo[i] = new StorageBlock
                    {
                        uncompressedSize = oldBlock.uncompressedSize,
                        compressedSize = oldBlock.uncompressedSize,
                        flags = (StorageBlockFlags)(((ushort)oldBlock.flags & ~(ushort)StorageBlockFlags.CompressionTypeMask) | (ushort)CompressionType.None)
                    };
                }

                byte[] newBlocksInfoBytes;
                uint newUncompressedBlocksInfoSize;
                using (var ms = new MemoryStream())
                using (var bw = new EndianBinaryWriter(ms, EndianType.BigEndian))
                {
                    bw.Write(originalBlocksInfoHash);
                    bw.Write(newBlocksInfo.Length);
                    foreach (var block in newBlocksInfo)
                    {
                        bw.Write(block.uncompressedSize);
                        bw.Write(block.compressedSize);
                        bw.Write((ushort)block.flags);
                    }
                    bw.Write(originalDirectoryInfo.Length);
                    foreach (var node in originalDirectoryInfo)
                    {
                        bw.Write(node.offset);
                        bw.Write(node.size);
                        bw.Write(node.flags);
                        bw.WriteNullTerminatedString(node.path);
                    }
                    newBlocksInfoBytes = ms.ToArray();
                    newUncompressedBlocksInfoSize = (uint)newBlocksInfoBytes.Length;
                }

                // --- 4. 写入新的 Bundle 文件 ---
                using (var writer = new EndianBinaryWriter(File.Create(outputPath), EndianType.BigEndian))
                {
                    long headerSize = GetHeaderSize(originalHeader);
                    long finalSize = headerSize + newUncompressedBlocksInfoSize + totalUncompressedDataSize;
                    if ((originalHeader.flags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
                    {
                        finalSize = (finalSize + 15) & ~15L;
                    }

                    writer.WriteNullTerminatedString(originalHeader.signature);
                    writer.Write(originalHeader.version);
                    writer.WriteNullTerminatedString(originalHeader.unityVersion);
                    writer.WriteNullTerminatedString(originalHeader.unityRevision);

                    // Write a placeholder size, we'll fix it at the end
                    long sizePos = writer.BaseStream.Position;
                    writer.Write((long)0);

                    writer.Write(newUncompressedBlocksInfoSize); // compressed size
                    writer.Write(newUncompressedBlocksInfoSize); // uncompressed size
                    writer.Write((uint)((originalHeader.flags & ~ArchiveFlags.CompressionTypeMask) | ArchiveFlags.CompressionTypeMask & 0)); // Set compression to None

                    bool blocksInfoAtEnd = (originalHeader.flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0;
                    if (originalHeader.version >= 7) writer.AlignStream(16);

                    if (!blocksInfoAtEnd)
                    {
                        writer.Write(newBlocksInfoBytes);
                        if ((originalHeader.flags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0) writer.AlignStream(16);
                        decompressedDataStream.Position = 0;
                        decompressedDataStream.CopyTo(writer.BaseStream);
                    }
                    else
                    {
                        decompressedDataStream.Position = 0;
                        decompressedDataStream.CopyTo(writer.BaseStream);
                        writer.Write(newBlocksInfoBytes);
                    }

                    // Fix the total size in the header
                    long finalPosition = writer.BaseStream.Position;
                    writer.BaseStream.Position = sizePos;
                    writer.Write(finalPosition);
                }
            }
        }
    }

    #endregion

    #region Compression Logic (New)

    private static (int length, int pos) WriteLongLength(BinaryWriter writer, int length)
    {
        int bytesWritten = 0;
        while (length >= 255)
        {
            writer.Write((byte)255);
            length -= 255;
            bytesWritten++;
        }
        writer.Write((byte)length);
        bytesWritten++;
        return (length, bytesWritten);
    }

    private static byte[] CompressToCustomArkLz4(byte[] uncompressedBytes, int uncompressedSize)
    {
        // 1. Compress with standard LZ4
        byte[] standardLz4Bytes = new byte[LZ4Codec.MaximumOutputSize(uncompressedSize)];
        int standardLz4Size = LZ4Codec.Encode(uncompressedBytes, 0, uncompressedSize, standardLz4Bytes, 0, standardLz4Bytes.Length);

        // 2. Post-process the standard LZ4 data to create ArkLz4 format
        using (var arkLz4Stream = new MemoryStream())
        using (var writer = new BinaryWriter(arkLz4Stream))
        {
            int ip = 0; // input position for standardLz4Bytes
            while (ip < standardLz4Size)
            {
                // Read control byte
                byte controlByte = standardLz4Bytes[ip++];
                int literalLength = (controlByte & 0xF0) >> 4;
                int matchLength = controlByte & 0x0F;

                // Write swapped control byte
                writer.Write((byte)((matchLength << 4) | literalLength));

                // Handle long literal length
                if (literalLength == 15)
                {
                    int longLen = 0;
                    byte b;
                    do
                    {
                        b = standardLz4Bytes[ip++];
                        longLen += b;
                    } while (b == 255);
                    WriteLongLength(writer, longLen);
                    literalLength += longLen;
                }

                // Write literal data
                writer.Write(standardLz4Bytes, ip, literalLength);
                ip += literalLength;

                // Check for end of data (last block can be just literals)
                if (ip >= standardLz4Size) break;

                // Read little-endian offset
                ushort offset = (ushort)(standardLz4Bytes[ip] | (standardLz4Bytes[ip + 1] << 8));
                ip += 2;

                // Write big-endian offset
                writer.Write((byte)(offset >> 8));
                writer.Write((byte)(offset & 0xFF));

                // Handle long match length
                if (matchLength == 15)
                {
                    int longLen = 0;
                    byte b;
                    do
                    {
                        b = standardLz4Bytes[ip++];
                        longLen += b;
                    } while (b == 255);
                    WriteLongLength(writer, longLen);
                }
            }
            return arkLz4Stream.ToArray();
        }
    }

    public void ConvertToArkLz4(string inputPath, string outputPath)
    {
        using (var reader = new EndianBinaryReader(File.OpenRead(inputPath), EndianType.BigEndian))
        {
            // --- 1. 读取未压缩的 Bundle ---
            string signature = reader.ReadStringToNull();
            uint version = reader.ReadUInt32();
            string unityVersion = reader.ReadStringToNull();
            string unityRevision = reader.ReadStringToNull();

            if (signature != "UnityFS")
            {
                throw new NotSupportedException($"Unsupported bundle signature: {signature}");
            }

            Header originalHeader = new Header
            {
                signature = signature,
                version = version,
                unityVersion = unityVersion,
                unityRevision = unityRevision
            };
            ReadHeader(reader, originalHeader);

            var blocksInfoCompression = (CompressionType)(originalHeader.flags & ArchiveFlags.CompressionTypeMask);
            if (blocksInfoCompression != CompressionType.None)
            {
                throw new NotSupportedException($"Input file is not an uncompressed bundle. BlocksInfo compression is {blocksInfoCompression}.");
            }

            if (originalHeader.version >= 7)
            {
                reader.AlignStream(16);
            }

            // BlocksInfo is uncompressed, read it directly
            var uncompressedBlocksInfoBytes = reader.ReadBytes((int)originalHeader.uncompressedBlocksInfoSize);
            long dataBlocksStartPosition = reader.BaseStream.Position;
            if ((originalHeader.flags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
            {
                dataBlocksStartPosition = (dataBlocksStartPosition + 15) & ~15L;
                reader.BaseStream.Position = dataBlocksStartPosition;
            }

            StorageBlock[] originalBlocksInfo;
            Node[] originalDirectoryInfo;
            byte[] originalBlocksInfoHash;
            using (var blocksInfoReader = new EndianBinaryReader(new MemoryStream(uncompressedBlocksInfoBytes), EndianType.BigEndian))
            {
                originalBlocksInfoHash = blocksInfoReader.ReadBytes(16);
                int blocksCount = blocksInfoReader.ReadInt32();
                originalBlocksInfo = new StorageBlock[blocksCount];
                for (int i = 0; i < blocksCount; i++)
                {
                    originalBlocksInfo[i] = new StorageBlock
                    {
                        uncompressedSize = blocksInfoReader.ReadUInt32(),
                        compressedSize = blocksInfoReader.ReadUInt32(),
                        flags = (StorageBlockFlags)blocksInfoReader.ReadUInt16()
                    };
                    if ((CompressionType)(originalBlocksInfo[i].flags & StorageBlockFlags.CompressionTypeMask) != CompressionType.None)
                    {
                        throw new NotSupportedException($"Input file is not fully uncompressed. Data block {i} has compression.");
                    }
                }
                int nodesCount = blocksInfoReader.ReadInt32();
                originalDirectoryInfo = new Node[nodesCount];
                for (int i = 0; i < nodesCount; i++)
                {
                    originalDirectoryInfo[i] = new Node
                    {
                        offset = blocksInfoReader.ReadInt64(),
                        size = blocksInfoReader.ReadInt64(),
                        flags = blocksInfoReader.ReadUInt32(),
                        path = blocksInfoReader.ReadStringToNull()
                    };
                }
            }

            // --- 2. 压缩所有数据块为 ArkLz4 ---
            var compressedDataBlocks = new List<byte[]>();
            var newBlocksInfo = new StorageBlock[originalBlocksInfo.Length];
            reader.BaseStream.Position = dataBlocksStartPosition;

            for (int i = 0; i < originalBlocksInfo.Length; i++)
            {
                var blockInfo = originalBlocksInfo[i];
                Console.WriteLine($"Compressing Block {i} (Uncompressed: {blockInfo.uncompressedSize}) to ArkLz4...");
                byte[] uncompressedBytes = reader.ReadBytes((int)blockInfo.uncompressedSize);

                byte[] compressedBytes = CompressToCustomArkLz4(uncompressedBytes, (int)blockInfo.uncompressedSize);
                compressedDataBlocks.Add(compressedBytes);

                newBlocksInfo[i] = new StorageBlock
                {
                    uncompressedSize = blockInfo.uncompressedSize,
                    compressedSize = (uint)compressedBytes.Length,
                    flags = (StorageBlockFlags)(((ushort)blockInfo.flags & ~(ushort)StorageBlockFlags.CompressionTypeMask) | (ushort)CompressionType.ArkLz4)
                };
                Console.WriteLine($"  -> Compressed size: {compressedBytes.Length}");
            }

            // --- 3. 准备新的 Header 和 BlocksInfo ---
            // Serialize and compress the new BlocksInfo metadata (using standard Lz4)
            byte[] newUncompressedBlocksInfoBytes;
            using (var ms = new MemoryStream())
            using (var bw = new EndianBinaryWriter(ms, EndianType.BigEndian))
            {
                bw.Write(originalBlocksInfoHash);
                bw.Write(newBlocksInfo.Length);
                foreach (var block in newBlocksInfo)
                {
                    bw.Write(block.uncompressedSize);
                    bw.Write(block.compressedSize);
                    bw.Write((ushort)block.flags);
                }
                bw.Write(originalDirectoryInfo.Length);
                foreach (var node in originalDirectoryInfo)
                {
                    bw.Write(node.offset);
                    bw.Write(node.size);
                    bw.Write(node.flags);
                    bw.WriteNullTerminatedString(node.path);
                }
                newUncompressedBlocksInfoBytes = ms.ToArray();
            }

            byte[] newCompressedBlocksInfoBytes = new byte[LZ4Codec.MaximumOutputSize(newUncompressedBlocksInfoBytes.Length)];
            int newCompressedBlocksInfoSize = LZ4Codec.Encode(newUncompressedBlocksInfoBytes, newCompressedBlocksInfoBytes);

            // --- 4. 写入新的 Bundle 文件 ---
            using (var writer = new EndianBinaryWriter(File.Create(outputPath), EndianType.BigEndian))
            {
                writer.WriteNullTerminatedString(signature);
                writer.Write(version);
                writer.WriteNullTerminatedString(unityVersion);
                writer.WriteNullTerminatedString(unityRevision);

                long sizePos = writer.BaseStream.Position;
                writer.Write((long)0); // Placeholder for total size

                writer.Write((uint)newCompressedBlocksInfoSize);
                writer.Write((uint)newUncompressedBlocksInfoBytes.Length);
                // Set BlocksInfo compression flag to Lz4, preserve other flags
                var newFlags = (originalHeader.flags & ~ArchiveFlags.CompressionTypeMask) | (ArchiveFlags)CompressionType.Lz4;
                writer.Write((uint)newFlags);

                if (version >= 7) writer.AlignStream(16);

                // Write compressed blocks info
                writer.Write(newCompressedBlocksInfoBytes, 0, newCompressedBlocksInfoSize);

                if ((newFlags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0) writer.AlignStream(16);

                // Write compressed data blocks
                foreach (var blockData in compressedDataBlocks)
                {
                    writer.Write(blockData);
                }

                // Go back and write the final file size
                long finalSize = writer.BaseStream.Position;
                writer.BaseStream.Position = sizePos;
                writer.Write(finalSize);
                writer.BaseStream.Position = finalSize; // Go back to the end
            }
            Console.WriteLine($"Successfully created compressed bundle at: {outputPath}");
        }
    }


    #endregion

    private void ReadHeader(EndianBinaryReader reader, Header header)
    {
        header.size = reader.ReadInt64();
        header.compressedBlocksInfoSize = reader.ReadUInt32();
        header.uncompressedBlocksInfoSize = reader.ReadUInt32();
        header.flags = (ArchiveFlags)reader.ReadUInt32();
    }

    private long GetHeaderSize(Header header)
    {
        long size = 0;
        size += Encoding.UTF8.GetByteCount(header.signature) + 1;
        size += 4; //version
        size += Encoding.UTF8.GetByteCount(header.unityVersion) + 1;
        size += Encoding.UTF8.GetByteCount(header.unityRevision) + 1;
        size += 8 + 4 + 4 + 4; //size, cBlockSize, uBlockSize, flags

        if (header.version >= 7)
        {
            size = (size + 15) & ~15L;
        }

        return size;
    }

    public class BundleMetadata
    {
        /// <summary>包内第一个 CAB 资源名，用于兼容现有逻辑显示。</summary>
        public string ResourceName { get; set; } = string.Empty;
        /// <summary>包内全部 CAB 资源名（去重，保持出现顺序）。</summary>
        public List<string> Cabs { get; set; } = new();
        public List<string> Dependencies { get; set; } = new();
    }

    /// <summary>
    /// 快速判断文件是否为 UnityFS Bundle（仅读取文件头签名，不做任何转换）。
    /// </summary>
    public static bool IsUnityBundle(string inputPath)
    {
        const string signature = "UnityFS"; // 7 个字符
        try
        {
            using var fs = File.OpenRead(inputPath);
            Span<byte> sig = stackalloc byte[signature.Length];
            int read = fs.Read(sig);
            return read == signature.Length && Encoding.ASCII.GetString(sig) == signature;
        }
        catch
        {
            return false;
        }
    }

    public BundleMetadata ExtractBundleMetadata(string inputPath)
    {
        var metadata = new BundleMetadata();
        
        try
        {
            using (var reader = new EndianBinaryReader(File.OpenRead(inputPath), EndianType.BigEndian))
            {
                string signature = reader.ReadStringToNull();
                uint version = reader.ReadUInt32();
                string unityVersion = reader.ReadStringToNull();
                string unityRevision = reader.ReadStringToNull();

                if (signature != "UnityFS")
                {
                    return metadata;
                }

                Header header = new Header
                {
                    signature = signature,
                    version = version,
                    unityVersion = unityVersion,
                    unityRevision = unityRevision
                };
                ReadHeader(reader, header);

                long blocksInfoPosition = reader.BaseStream.Position;
                if (header.version >= 7)
                {
                    reader.AlignStream(16);
                    blocksInfoPosition = reader.BaseStream.Position;
                }

                byte[] compressedBlocksInfoBytes;
                if ((header.flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0)
                {
                    long currentPos = reader.BaseStream.Position;
                    reader.BaseStream.Position = reader.BaseStream.Length - header.compressedBlocksInfoSize;
                    compressedBlocksInfoBytes = reader.ReadBytes((int)header.compressedBlocksInfoSize);
                    reader.BaseStream.Position = currentPos;
                }
                else
                {
                    compressedBlocksInfoBytes = reader.ReadBytes((int)header.compressedBlocksInfoSize);
                }

                MemoryStream blocksInfoUncompressedStream;
                var blocksInfoCompressionType = (CompressionType)(header.flags & ArchiveFlags.CompressionTypeMask);
                switch (blocksInfoCompressionType)
                {
                    case CompressionType.None:
                        blocksInfoUncompressedStream = new MemoryStream(compressedBlocksInfoBytes);
                        break;
                    case CompressionType.Lz4:
                    case CompressionType.Lz4HC:
                        var uncompressedBytes = new byte[header.uncompressedBlocksInfoSize];
                        var numWrite = LZ4Codec.Decode(compressedBlocksInfoBytes, uncompressedBytes);
                        if (numWrite != header.uncompressedBlocksInfoSize) return metadata;
                        blocksInfoUncompressedStream = new MemoryStream(uncompressedBytes);
                        break;
                    default:
                        return metadata;
                }

                using (var blocksInfoReader = new EndianBinaryReader(blocksInfoUncompressedStream, EndianType.BigEndian))
                {
                    blocksInfoReader.ReadBytes(16); // hash
                    int blocksCount = blocksInfoReader.ReadInt32();
                    for (int i = 0; i < blocksCount; i++)
                    {
                        blocksInfoReader.ReadUInt32(); // uncompressedSize
                        blocksInfoReader.ReadUInt32(); // compressedSize
                        blocksInfoReader.ReadUInt16(); // flags
                    }

                    int nodesCount = blocksInfoReader.ReadInt32();
                    for (int i = 0; i < nodesCount; i++)
                    {
                        blocksInfoReader.ReadInt64(); // offset
                        blocksInfoReader.ReadInt64(); // size
                        blocksInfoReader.ReadUInt32(); // flags
                        string path = blocksInfoReader.ReadStringToNull();
                        
                        // Extract CAB name(s) from path (e.g., "CAB-12345678abc")
                        if (path.Contains("CAB-"))
                        {
                            var cabMatch = System.Text.RegularExpressions.Regex.Match(path, @"CAB-[A-Fa-f0-9]+");
                            if (cabMatch.Success)
                            {
                                string cab = cabMatch.Value;
                                if (!metadata.Cabs.Contains(cab))
                                {
                                    metadata.Cabs.Add(cab);
                                }
                                if (string.IsNullOrEmpty(metadata.ResourceName))
                                {
                                    metadata.ResourceName = cab;
                                }
                            }
                        }
                        
                        // Extract dependencies if they exist
                        if (path.EndsWith(".ab") && path != metadata.ResourceName + ".ab")
                        {
                            var fileName = Path.GetFileName(path);
                            if (!metadata.Dependencies.Contains(fileName))
                            {
                                metadata.Dependencies.Add(fileName);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Silently fail and return empty metadata
        }

        return metadata;
    }
}


/// <summary>
/// Writes primitive types in binary to a stream and supports writing specific byte orders (endianness).
/// </summary>
public class EndianBinaryWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _buffer; // Reusable buffer for primitive types
    private bool _leaveOpen;
    private bool _disposed;

    /// <summary>
    /// Gets the underlying stream.
    /// </summary>
    public Stream BaseStream => _stream;

    /// <summary>
    /// Gets or sets the endianness for writing multi-byte values.
    /// </summary>
    public EndianType Endian { get; set; }

    public EndianBinaryWriter(Stream output, EndianType endian = EndianType.BigEndian, bool leaveOpen = false)
    {
        if (output == null)
            throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite)
            throw new ArgumentException("Stream does not support writing or is already closed.", nameof(output));

        _stream = output;
        Endian = endian;
        _buffer = new byte[16]; // Sufficient for largest primitive (decimal) + padding
        _leaveOpen = leaveOpen;
        _disposed = false;
    }

    public virtual void Write(byte value) => _stream.WriteByte(value);
    [CLSCompliant(false)]
    public virtual void Write(sbyte value) => _stream.WriteByte((byte)value);

    public virtual void Write(short value)
    {
        if (Endian == EndianType.BigEndian) BinaryPrimitives.WriteInt16BigEndian(_buffer, value);
        else BinaryPrimitives.WriteInt16LittleEndian(_buffer, value);
        _stream.Write(_buffer, 0, 2);
    }

    [CLSCompliant(false)]
    public virtual void Write(ushort value)
    {
        if (Endian == EndianType.BigEndian) BinaryPrimitives.WriteUInt16BigEndian(_buffer, value);
        else BinaryPrimitives.WriteUInt16LittleEndian(_buffer, value);
        _stream.Write(_buffer, 0, 2);
    }

    public virtual void Write(int value)
    {
        if (Endian == EndianType.BigEndian) BinaryPrimitives.WriteInt32BigEndian(_buffer, value);
        else BinaryPrimitives.WriteInt32LittleEndian(_buffer, value);
        _stream.Write(_buffer, 0, 4);
    }

    [CLSCompliant(false)]
    public virtual void Write(uint value)
    {
        if (Endian == EndianType.BigEndian) BinaryPrimitives.WriteUInt32BigEndian(_buffer, value);
        else BinaryPrimitives.WriteUInt32LittleEndian(_buffer, value);
        _stream.Write(_buffer, 0, 4);
    }

    public virtual void Write(long value)
    {
        if (Endian == EndianType.BigEndian) BinaryPrimitives.WriteInt64BigEndian(_buffer, value);
        else BinaryPrimitives.WriteInt64LittleEndian(_buffer, value);
        _stream.Write(_buffer, 0, 8);
    }

    [CLSCompliant(false)]
    public virtual void Write(ulong value)
    {
        if (Endian == EndianType.BigEndian) BinaryPrimitives.WriteUInt64BigEndian(_buffer, value);
        else BinaryPrimitives.WriteUInt64LittleEndian(_buffer, value);
        _stream.Write(_buffer, 0, 8);
    }

    // ... Other write methods from your original code ...
    // (The implementation of float/double/byte[]/string is fine)
    public virtual void Write(float value)
    {
        if (Endian == EndianType.BigEndian)
        {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER
             BinaryPrimitives.WriteSingleBigEndian(_buffer, value);
#else
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            Buffer.BlockCopy(bytes, 0, _buffer, 0, 4);
#endif
        }
        else // Little Endian
        {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER
             BinaryPrimitives.WriteSingleLittleEndian(_buffer, value);
#else
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            Buffer.BlockCopy(bytes, 0, _buffer, 0, 4);
#endif
        }
        _stream.Write(_buffer, 0, 4);
    }

    public virtual void Write(double value)
    {
        if (Endian == EndianType.BigEndian)
        {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER
             BinaryPrimitives.WriteDoubleBigEndian(_buffer, value);
#else
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            Buffer.BlockCopy(bytes, 0, _buffer, 0, 8);
#endif
        }
        else // Little Endian
        {
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER
             BinaryPrimitives.WriteDoubleLittleEndian(_buffer, value);
#else
            var bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            Buffer.BlockCopy(bytes, 0, _buffer, 0, 8);
#endif
        }
        _stream.Write(_buffer, 0, 8);
    }

    public virtual void Write(byte[] buffer)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        _stream.Write(buffer, 0, buffer.Length);
    }

    public virtual void Write(byte[] buffer, int index, int count)
    {
        _stream.Write(buffer, index, count);
    }

    public virtual void WriteNullTerminatedString(string value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        _stream.Write(bytes, 0, bytes.Length);
        _stream.WriteByte(0);
    }

    public virtual void AlignStream(int alignment)
    {
        if (alignment <= 0) throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be positive.");
        long currentPosition = _stream.Position;
        long padding = (alignment - (currentPosition % alignment)) % alignment;
        if (padding > 0)
        {
            for (int i = 0; i < padding; i++) _stream.WriteByte(0);
        }
    }

    public virtual void Flush() => _stream.Flush();

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                if (!_leaveOpen)
                {
                    _stream?.Dispose();
                }
            }
            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

public class EndianBinaryReader : BinaryReader
{
    // ... Your existing EndianBinaryReader implementation is fine ...
    // (No changes needed here)
    private readonly byte[] buffer;

    public EndianType Endian;

    public EndianBinaryReader(Stream stream, EndianType endian = EndianType.BigEndian) : base(stream, Encoding.UTF8, true)
    {
        Endian = endian;
        buffer = new byte[8];
    }

    public long Position
    {
        get => BaseStream.Position;
        set => BaseStream.Position = value;
    }

    public override short ReadInt16()
    {
        if (Endian == EndianType.BigEndian)
        {
            this.Read(buffer, 0, 2);
            return BinaryPrimitives.ReadInt16BigEndian(buffer);
        }
        return base.ReadInt16();
    }

    public override int ReadInt32()
    {
        if (Endian == EndianType.BigEndian)
        {
            this.Read(buffer, 0, 4);
            return BinaryPrimitives.ReadInt32BigEndian(buffer);
        }
        return base.ReadInt32();
    }

    public override long ReadInt64()
    {
        if (Endian == EndianType.BigEndian)
        {
            this.Read(buffer, 0, 8);
            return BinaryPrimitives.ReadInt64BigEndian(buffer);
        }
        return base.ReadInt64();
    }

    public override ushort ReadUInt16()
    {
        if (Endian == EndianType.BigEndian)
        {
            this.Read(buffer, 0, 2);
            return BinaryPrimitives.ReadUInt16BigEndian(buffer);
        }
        return base.ReadUInt16();
    }

    public override uint ReadUInt32()
    {
        if (Endian == EndianType.BigEndian)
        {
            this.Read(buffer, 0, 4);
            return BinaryPrimitives.ReadUInt32BigEndian(buffer);
        }
        return base.ReadUInt32();
    }

    public override ulong ReadUInt64()
    {
        if (Endian == EndianType.BigEndian)
        {
            this.Read(buffer, 0, 8);
            return BinaryPrimitives.ReadUInt64BigEndian(buffer);
        }
        return base.ReadUInt64();
    }

    public override float ReadSingle()
    {
        if (Endian == EndianType.BigEndian)
        {
            this.Read(buffer, 0, 4);
            Array.Reverse(buffer, 0, 4);
            return BitConverter.ToSingle(buffer, 0);
        }
        return base.ReadSingle();
    }

    public override double ReadDouble()
    {
        if (Endian == EndianType.BigEndian)
        {
            this.Read(buffer, 0, 8);
            Array.Reverse(buffer);
            return BitConverter.ToDouble(buffer, 0);
        }
        return base.ReadDouble();
    }
}

public static class BinaryReaderExtensions
{
    // ... Your existing BinaryReaderExtensions implementation is fine ...
    // (No changes needed here)
    public static void AlignStream(this BinaryReader reader, int alignment)
    {
        var pos = reader.BaseStream.Position;
        var mod = pos % alignment;
        if (mod != 0)
        {
            reader.BaseStream.Position += alignment - mod;
        }
    }

    public static string ReadStringToNull(this BinaryReader reader, int maxLength = 32767)
    {
        var bytes = new List<byte>();
        int count = 0;
        while (reader.BaseStream.Position != reader.BaseStream.Length && count < maxLength)
        {
            var b = reader.ReadByte();
            if (b == 0)
            {
                break;
            }
            bytes.Add(b);
            count++;
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PakToolGUI;

public class PakIndexReader
{
    private const uint PakMagic = 0x5A6F12E1;
    private const uint PubgmCnMagic = 0xFF67FF70;
    private const byte PubgmCnXorKey = 0x79;
    private static readonly byte[] PubgmCnZucKey = { 0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37 };
    private static readonly byte[] PubgmCnZucIv  = { 0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45 };

    public class FileEntry
    {
        public string FileName { get; set; } = "";
        public int CompressionMethod { get; set; }
    }

    public class CompressionManifest
    {
        public string SourcePak { get; set; } = "";
        public int PakVersion { get; set; }
        public string MountPoint { get; set; } = "";
        public string[] CompressionNames { get; set; } = Array.Empty<string>();
        public Dictionary<string, int> FileCompression { get; set; } = new();
    }

    public static CompressionManifest ReadPakIndex(string pakPath)
    {
        using var fs = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs);

        int detectedVersion = 0;
        long indexOffset = 0;
        long indexSize = 0;
        bool encryptedIndex = false;
        bool isPubgmCn = false;

        // Try both standard UE4 and PUBGM_CN (EncIdx-first) formats
        for (int ver = 11; ver >= 1; ver--)
        {
            // Standard UE4 footer
            int footerSize = GetFooterSize(ver);
            if (fs.Length >= footerSize)
            {
                fs.Seek(-footerSize, SeekOrigin.End);
                var magic = reader.ReadUInt32();
                if (magic == PakMagic)
                {
                    var fver = reader.ReadUInt32();
                    if (fver == ver)
                    {
                        detectedVersion = ver;
                        indexOffset = reader.ReadInt64();
                        indexSize = reader.ReadInt64();
                        reader.ReadBytes(20);
                        if (ver >= 4) encryptedIndex = reader.ReadByte() != 0;
                        break;
                    }
                }
            }

            // PUBGM_CN: EncIdx(1) + Magic(4) + Ver(4) + Hash(20) + Size(8) + Off(8) = 45
            int pubgmSize = 45;
            if (fs.Length >= pubgmSize)
            {
                fs.Seek(-pubgmSize, SeekOrigin.End);
                byte encIdx = reader.ReadByte();
                uint pubgmMagic = reader.ReadUInt32();
                if (pubgmMagic == 0xFF67FF70)
                {
                    uint pubgmVer = reader.ReadUInt32();
                    detectedVersion = (int)pubgmVer;
                    reader.ReadBytes(20); // hash
                    indexSize = reader.ReadInt64();
                    indexOffset = reader.ReadInt64();
                    var zucKeys = ZucCipher.GenerateKeyArray(PubgmCnZucKey, PubgmCnZucIv, 16);
                    ulong sizeKey = ((ulong)zucKeys[10] << 32) | zucKeys[11];
                    ulong offsetKey = ((ulong)zucKeys[0] << 32) | zucKeys[1];
                    indexSize = (long)((ulong)indexSize ^ sizeKey);
                    indexOffset = (long)((ulong)indexOffset ^ offsetKey);
                    encryptedIndex = (encIdx ^ (byte)(zucKeys[3] & 0xFF)) != 0;
                    isPubgmCn = true;
                    break;
                }
            }
        }

        if (detectedVersion == 0)
            throw new InvalidDataException("Not a valid PAK file (bad magic)");

        var compNames = new List<string>();
        if (detectedVersion >= 8)
        {
            // Only read compression names for standard UE4 (not PUBGM_CN with 45-byte footer)
            // Try to read compression names from end
            fs.Seek(-(32 * 5), SeekOrigin.End);
            var testMagic = reader.ReadUInt32();
            if (testMagic == PakMagic || testMagic == 0xFF67FF70)
            {
                // No compression names (names would be before magic)
            }
            else
            {
                // Compression names might exist
                fs.Seek(-(32 * 5), SeekOrigin.End);
                for (int i = 0; i < 5; i++)
                {
                    var nameBytes = reader.ReadBytes(32);
                    var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                    if (!string.IsNullOrEmpty(name)) compNames.Add(name);
                }
            }
        }

        var manifest = new CompressionManifest
        {
            SourcePak = Path.GetFileName(pakPath),
            PakVersion = detectedVersion,
            CompressionNames = compNames.ToArray()
        };

        // Read the PAK index
        fs.Seek(indexOffset, SeekOrigin.Begin);
        var indexData = reader.ReadBytes(checked((int)indexSize));
        if (isPubgmCn && encryptedIndex)
        {
            for (int i = 0; i < indexData.Length; i++)
                indexData[i] ^= PubgmCnXorKey;
        }

        using var indexMs = new MemoryStream(indexData);
        using var indexReader = new BinaryReader(indexMs);
        var mountPoint = ReadPakString(indexReader);
        manifest.MountPoint = mountPoint;
        var entryCount = indexReader.ReadInt32();

        if (isPubgmCn && detectedVersion >= 10)
        {
            var methodsByIndex = new int[entryCount];
            for (int i = 0; i < entryCount; i++)
                methodsByIndex[i] = ReadPakEntryCompression(indexReader, detectedVersion);

            byte[] textSection;
            if (indexMs.Position < indexData.Length)
            {
                textSection = indexData.AsSpan((int)indexMs.Position).ToArray();
            }
            else
            {
                fs.Seek(indexOffset + indexMs.Position, SeekOrigin.Begin);
                var textSize = reader.ReadInt64();
                textSection = reader.ReadBytes(checked((int)textSize));
            }

            using var textMs = new MemoryStream(textSection);
            using var textReader = new BinaryReader(textMs);
            var dirCount = textReader.ReadInt64();
            for (long dirIndex = 0; dirIndex < dirCount; dirIndex++)
            {
                var dirName = ReadPakString(textReader);
                var fileCount = textReader.ReadInt64();
                for (long fileIndex = 0; fileIndex < fileCount; fileIndex++)
                {
                    var fileName = ReadPakString(textReader);
                    var dataIndex = textReader.ReadInt32();
                    if (dataIndex < 0) dataIndex = ~dataIndex;
                    if ((uint)dataIndex >= (uint)methodsByIndex.Length) continue;
                    var fullName = (mountPoint.Length > "../../../".Length ? mountPoint : "") + dirName + fileName;
                    manifest.FileCompression[fullName] = methodsByIndex[dataIndex];
                }
            }

            return manifest;
        }

        for (int i = 0; i < entryCount; i++)
        {
            var fileName = ReadPakString(indexReader);
            var compressionMethod = ReadPakEntryCompression(indexReader, detectedVersion);

            manifest.FileCompression[fileName] = compressionMethod;
        }

        return manifest;
    }

    private static int GetFooterSize(int ver)
    {
        int size = 4 + 4 + 8 + 8 + 20;
        if (ver >= 4) size += 1;
        if (ver >= 7) size += 16;
        if (ver >= 8) size += 32 * 5;
        return size;
    }

    public static void SaveManifest(CompressionManifest manifest, string outputPath)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(manifest, options);
        File.WriteAllText(outputPath, json);
    }

    public static CompressionManifest? LoadManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath)) return null;
        var json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize<CompressionManifest>(json);
    }

    public static PakPacker.CompressionType MethodToType(int method)
    {
        return method switch
        {
            1 => PakPacker.CompressionType.Zlib,
            6 => PakPacker.CompressionType.Zstd,
            7 => PakPacker.CompressionType.Oodle,
            _ => PakPacker.CompressionType.None
        };
    }

    private static string ReadPakString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length == 0) return "";
        if (length < 0)
        {
            length = -length;
            var bytes = reader.ReadBytes(length * 2);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }

        return Encoding.UTF8.GetString(reader.ReadBytes(length)).TrimEnd('\0');
    }

    private static int ReadPakEntryCompression(BinaryReader reader, int version)
    {
        reader.ReadBytes(20);
        reader.ReadInt64();
        reader.ReadInt64();
        var compressionMethod = reader.ReadInt32() & 0x0F;
        reader.ReadInt64();
        reader.ReadByte();
        reader.ReadBytes(20);

        if (compressionMethod != 0)
        {
            var blockCount = reader.ReadInt32();
            if (blockCount > 0) reader.ReadBytes(blockCount * 16);
        }

        if (version >= 3)
        {
            reader.ReadInt32();
            reader.ReadByte();
        }

        if (version >= 12) reader.ReadInt32();
        if (version >= 14) reader.ReadInt32();

        return compressionMethod;
    }
}

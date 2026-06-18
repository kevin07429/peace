using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PakToolGUI;

public class PakPacker
{
    private const uint FallbackMagic = 0x5A6F12E1;
    private const int DefaultBlockSize = 0x10000;
    private const int DefaultZlibLevel = 3;
    private const int DefaultZstdLevel = 6;
    private const string DefaultMountPoint = "../../../";
    private const int PubgmCnNonCompressedHeaderSize = 0x4A;

    // PUBGM_CN ZUC keys for footer XOR encryption
    private static readonly byte[] PubgmCnZucKey = { 0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37,0x37 };
    private static readonly byte[] PubgmCnZucIv  = { 0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45,0x45 };
    private const byte PubgmCnXorKey = 0x79;

    public enum CompressionType { None, Zlib, Zstd, Oodle }

    private readonly string _outputPath;
    private readonly string _mountPoint;
    private readonly byte[] _aesKey;
    private readonly CompressionType _defaultCompression;
    private readonly Dictionary<string, CompressionType>? _perFileCompression;
    private readonly bool _encryptIndex;
    private readonly bool _isPubgmCn;
    private readonly uint _pakMagic;
    private readonly int _pakVersion;
    private readonly int _footerSize;
    private readonly List<PendingEntry> _pending = new();
    private readonly List<string> _errors = new();

    public int FileCount => _pending.Count;
    public IReadOnlyList<string> Errors => _errors;

    private class PendingEntry
    {
        public required string FileName { get; init; }
        public required byte[] ProcessedData { get; init; }
        public long UncompressedSize { get; init; }
        public long StoredSize { get; init; }
        public int CompressionMethod { get; init; }
        public byte[]? Hash { get; set; }
        public byte[]? ContentHash { get; set; }
        public bool IsEncrypted { get; init; }
        public List<(long Start, long End)>? Blocks { get; init; }
        public uint BlockSize { get; init; }
    }

    public PakPacker(string outputPath, byte[] aesKey, CompressionType compression,
                      string mountPoint = DefaultMountPoint, bool encryptIndex = false,
                      Dictionary<string, CompressionType>? perFileCompression = null,
                      int pakVersion = 8, uint pakMagic = 0x5A6F12E1, int footerSize = 45)
    {
        _outputPath = outputPath;
        _aesKey = aesKey;
        _defaultCompression = compression;
        _perFileCompression = perFileCompression;
        _mountPoint = mountPoint;
        _encryptIndex = encryptIndex;
        _pakVersion = Math.Clamp(pakVersion, 1, 11);
        _pakMagic = (pakMagic == 0) ? FallbackMagic : pakMagic;
        _footerSize = footerSize;
        _isPubgmCn = _pakMagic == 0xFF67FF70;
    }

    public bool AddFile(string relativePath, string fullPath)
    {
        try
        {
            var data = File.ReadAllBytes(fullPath);
            var uncompressedSize = data.Length;
            byte[] processedData;
            long storedSize;
            int compressionMethod;
            List<(long Start, long End)>? blocks = null;
            uint blockSize = (uint)Math.Min(DefaultBlockSize, Math.Max(1, data.Length));

            var fileCompression = ResolveCompression(relativePath.Replace('\\', '/'));

            if (fileCompression == CompressionType.None || data.Length == 0)
            {
                var contentData = data;
                var isEncrypted = !_isPubgmCn && _aesKey.Length > 0;
                if (isEncrypted && contentData.Length > 0)
                    contentData = Aes256EcbEncrypt(contentData, _aesKey);

                processedData = contentData;
                storedSize = contentData.Length;
                compressionMethod = 0;

                var storedHash = SHA1.HashData(contentData);
                var rawContentHash = _isPubgmCn ? new byte[20] : SHA1.HashData(data);

                _pending.Add(new PendingEntry
                {
                    FileName = relativePath.Replace('\\', '/'),
                    ProcessedData = processedData,
                    UncompressedSize = uncompressedSize,
                    StoredSize = storedSize,
                    CompressionMethod = compressionMethod,
                    Hash = storedHash,
                    ContentHash = rawContentHash,
                    IsEncrypted = isEncrypted,
                    Blocks = blocks,
                    BlockSize = blockSize
                });
                return true;
            }

            var isCompressedEncrypted = !_isPubgmCn && _aesKey.Length > 0;
            var compressedBlocks = new List<byte[]>();
            blocks = new List<(long Start, long End)>();
            long storedPos = 0;
            long actualCompressedSize = 0;
            for (var offset = 0; offset < data.Length; offset += DefaultBlockSize)
            {
                var size = Math.Min(DefaultBlockSize, data.Length - offset);
                var block = data.AsSpan(offset, size).ToArray();
                var compressed = fileCompression switch
                {
                    CompressionType.Zstd => CompressZstd(block),
                    CompressionType.Oodle => CompressOodle(block),
                    _ => CompressZlib(block)
                };

                if (isCompressedEncrypted && compressed.Length > 0)
                    compressed = Aes256EcbEncrypt(compressed, _aesKey);

                var actualEnd = storedPos + compressed.Length;
                blocks.Add((storedPos, actualEnd));
                actualCompressedSize += compressed.Length;

                if (_isPubgmCn || isCompressedEncrypted)
                {
                    var paddedLength = Align(compressed.Length, 16);
                    if (paddedLength != compressed.Length)
                        Array.Resize(ref compressed, paddedLength);
                }

                compressedBlocks.Add(compressed);
                storedPos += compressed.Length;
            }

            compressionMethod = fileCompression switch
            {
                CompressionType.Zlib => 1,
                CompressionType.Zstd => 6,
                CompressionType.Oodle => 7,
                _ => 0
            };

            processedData = compressedBlocks.SelectMany(b => b).ToArray();
            storedSize = processedData.Length;

            var hashData = _isPubgmCn || isCompressedEncrypted
                ? blocks.SelectMany(b => processedData.AsSpan((int)b.Start, (int)(b.End - b.Start)).ToArray()).ToArray()
                : processedData;
            var hash = SHA1.HashData(hashData);
            var contentHash = _isPubgmCn ? new byte[20] : SHA1.HashData(data);

            _pending.Add(new PendingEntry
            {
                FileName = relativePath.Replace('\\', '/'),
                ProcessedData = processedData,
                UncompressedSize = uncompressedSize,
                StoredSize = storedSize,
                CompressionMethod = compressionMethod,
                Hash = hash,
                ContentHash = contentHash,
                IsEncrypted = isCompressedEncrypted,
                Blocks = blocks,
                BlockSize = blockSize
            });
            return true;
        }
        catch (Exception ex)
        {
            _errors.Add($"{relativePath}: {ex.Message}");
            return false;
        }
    }

    public void Write(IProgress<int>? progress = null)
    {
        using var fs = new FileStream(_outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096 * 1024);
        progress?.Report(0);

        var writtenEntries = new List<(PendingEntry Pending, long Offset, long CompressedSize)>();
        foreach (var pe in _pending)
        {
            progress?.Report((int)(writtenEntries.Count / (double)_pending.Count * 50));
            var offset = fs.Position;
            var localHeaderSize = GetPakEntrySize(pe);
            using var localEntryMs = new MemoryStream(localHeaderSize);
            WritePakEntry(localEntryMs, pe, offset, offset + localHeaderSize);
            fs.Write(localEntryMs.ToArray());
            fs.Write(pe.ProcessedData);
            writtenEntries.Add((pe, offset, pe.StoredSize));
        }

        progress?.Report(60);
        var indexOffset = fs.Position;
        using var indexMs = new MemoryStream();
        byte[]? externalTextSection = null;
        WriteString(indexMs, _mountPoint);
        WriteU32(indexMs, (uint)writtenEntries.Count);

        if (_isPubgmCn && _pakVersion >= 10)
            externalTextSection = WritePubgmCnTextSectionIndex(indexMs, writtenEntries);
        else
            WriteLegacyIndex(indexMs, writtenEntries);

        var indexRaw = indexMs.ToArray();
        byte[] indexFinal;
        if (_encryptIndex && !_isPubgmCn && _aesKey.Length > 0)
            indexFinal = Aes256EcbEncrypt(indexRaw, _aesKey);
        else
            indexFinal = (byte[])indexRaw.Clone();

        // PUBGM_CN: XOR the index only when the footer advertises an encrypted index.
        if (_isPubgmCn && _encryptIndex)
        {
            for (int i = 0; i < indexFinal.Length; i++)
                indexFinal[i] ^= PubgmCnXorKey;
        }

        var indexHash = SHA1.HashData(indexRaw);
        progress?.Report(80);
        fs.Write(indexFinal);
        var indexSize = fs.Position - indexOffset;
        if (externalTextSection != null)
        {
            WriteI64(fs, externalTextSection.Length);
            fs.Write(externalTextSection);
        }

        // PUBGM_CN: pre-XOR footer fields with ZUC-generated keys
        long xorIndexOffset = indexOffset;
        long xorIndexSize = indexSize;
        byte[] xorHash = (byte[])indexHash.Clone();
        byte xorEncIdx = (byte)(_encryptIndex ? 1 : 0);

        if (_isPubgmCn)
        {
            var zucKeys = ZucCipher.GenerateKeyArray(PubgmCnZucKey, PubgmCnZucIv, 16);
            ulong offKey = ((ulong)zucKeys[0] << 32) | zucKeys[1];
            xorIndexOffset = (long)((ulong)indexOffset ^ offKey);
            ulong sizeKey = ((ulong)zucKeys[10] << 32) | zucKeys[11];
            xorIndexSize = (long)((ulong)indexSize ^ sizeKey);
            xorEncIdx ^= (byte)(zucKeys[3] & 0xFF);
            for (int hi = 0; hi < 5; hi++)
            {
                uint hk = zucKeys[4 + hi];
                int bo = hi * 4;
                xorHash[bo] ^= (byte)(hk & 0xFF);
                xorHash[bo+1] ^= (byte)((hk >> 8) & 0xFF);
                xorHash[bo+2] ^= (byte)((hk >> 16) & 0xFF);
                xorHash[bo+3] ^= (byte)((hk >> 24) & 0xFF);
            }
        }

        // Footer: EncIdx(1) + Magic(4) + Version(4) + Hash(20) + Size(8) + Offset(8)
        if (_footerSize >= 45)
            fs.WriteByte(xorEncIdx);

        WriteU32(fs, _pakMagic);
        WriteU32(fs, (uint)_pakVersion);
        fs.Write(xorHash);
        WriteI64(fs, xorIndexSize);
        WriteI64(fs, xorIndexOffset);

        if (_footerSize >= 61)
        {
            Span<byte> encGuid = stackalloc byte[16];
            encGuid.Clear();
            fs.Write(encGuid);
        }

        if (_footerSize >= 221)
        {
            var compNames = new[] { "Zlib", "Gzip", "Oodle", "Zstd", "LZ4" };
            Span<byte> nameBuf = stackalloc byte[32];
            foreach (var name in compNames)
            {
                nameBuf.Clear();
                var nameBytes = Encoding.ASCII.GetBytes(name);
                nameBytes.AsSpan().CopyTo(nameBuf);
                fs.Write(nameBuf);
            }
        }
    }

    private CompressionType ResolveCompression(string relativePath)
    {
        if (_perFileCompression == null || _perFileCompression.Count == 0)
            return _defaultCompression;

        if (_perFileCompression.TryGetValue(relativePath, out var exact))
            return exact;

        foreach (var kvp in _perFileCompression)
        {
            if (kvp.Key.EndsWith("/" + relativePath, StringComparison.Ordinal))
                return kvp.Value;
        }

        return _defaultCompression;
    }

    private void WriteLegacyIndex(MemoryStream indexMs, List<(PendingEntry Pending, long Offset, long CompressedSize)> writtenEntries)
    {
        foreach (var (pe, offset, _) in writtenEntries)
        {
            WriteString(indexMs, pe.FileName);
            WritePakEntry(indexMs, pe, offset, offset + GetPakEntrySize(pe));
        }
    }

    private byte[] WritePubgmCnTextSectionIndex(MemoryStream indexMs, List<(PendingEntry Pending, long Offset, long CompressedSize)> writtenEntries)
    {
        foreach (var (pe, offset, _) in writtenEntries)
            WritePakEntry(indexMs, pe, offset, offset + GetPakEntrySize(pe));

        using var textMs = new MemoryStream();

        var dirs = writtenEntries
            .Select((entry, index) =>
            {
                var normalized = StripMountPrefix(entry.Pending.FileName.Replace('\\', '/'));
                var slash = normalized.LastIndexOf('/');
                var dir = slash >= 0 ? normalized[..(slash + 1)] : "";
                var name = slash >= 0 ? normalized[(slash + 1)..] : normalized;
                return new { Dir = dir, Name = name, Index = index };
            })
            .GroupBy(x => x.Dir, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        WriteI64(textMs, dirs.Count);
        foreach (var dir in dirs)
        {
            WriteString(textMs, dir.Key);
            WriteI64(textMs, dir.Count());
            foreach (var file in dir.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                WriteString(textMs, file.Name);
                WriteU32(textMs, (uint)file.Index);
            }
        }

        return textMs.ToArray();
    }

    private string StripMountPrefix(string relativePath)
    {
        const string rootMount = "../../../";
        if (!_mountPoint.StartsWith(rootMount, StringComparison.Ordinal) || _mountPoint.Length <= rootMount.Length)
            return relativePath;

        var mountRelative = _mountPoint[rootMount.Length..].TrimStart('/');
        if (relativePath.StartsWith(mountRelative, StringComparison.Ordinal))
            return relativePath[mountRelative.Length..].TrimStart('/');

        return relativePath;
    }

    private static void WritePakEntry(Stream indexMs, PendingEntry pe, long offset, long blockBaseOffset)
    {
        indexMs.Write(pe.Hash!);
        WriteI64(indexMs, offset);
        WriteI64(indexMs, pe.UncompressedSize);
        WriteU32(indexMs, (uint)pe.CompressionMethod);
        WriteI64(indexMs, pe.StoredSize);
        indexMs.WriteByte(0);
        indexMs.Write(pe.ContentHash!);

        if (pe.Blocks != null && pe.Blocks.Count > 0)
        {
                WriteU32(indexMs, (uint)pe.Blocks.Count);
            foreach (var (start, end) in pe.Blocks)
            {
                WriteI64(indexMs, blockBaseOffset + start);
                WriteI64(indexMs, blockBaseOffset + end);
            }
        }
        else if (pe.CompressionMethod != 0)
        {
            WriteU32(indexMs, 0);
        }

        WriteU32(indexMs, pe.BlockSize);
        indexMs.WriteByte((byte)(pe.IsEncrypted ? 1 : 0));
    }

    private static int GetPakEntrySize(PendingEntry pe)
    {
        const int fixedEntrySize = 20 + 8 + 8 + 4 + 8 + 1 + 20;
        var size = fixedEntrySize;
        if (pe.CompressionMethod != 0)
            size += 4 + ((pe.Blocks?.Count ?? 0) * 16);
        size += 4 + 1;
        return size;
    }

    private static unsafe byte[] CompressZlib(byte[] data, int level = DefaultZlibLevel)
    {
        fixed (byte* srcPtr = data)
        {
            var srcLen = (nuint)data.Length;
            var destLen = NativeMethods.compressBound(srcLen);
            var dest = new byte[destLen];
            fixed (byte* dstPtr = dest)
            {
                var result = NativeMethods.compress2((nint)dstPtr, ref destLen, (nint)srcPtr, srcLen, level);
                if (result != 0)
                    throw new InvalidOperationException($"zlib compress failed: {result}");
                Array.Resize(ref dest, (int)destLen);
                return dest;
            }
        }
    }

    private static unsafe byte[] CompressZstd(byte[] data, int level = DefaultZstdLevel)
    {
        fixed (byte* srcPtr = data)
        {
            var srcLen = (nuint)data.Length;
            var dstCap = NativeMethods.ZSTD_compressBound(srcLen);
            var dest = new byte[dstCap];
            fixed (byte* dstPtr = dest)
            {
                var result = NativeMethods.ZSTD_compress((nint)dstPtr, dstCap, (nint)srcPtr, srcLen, level);
                if (NativeMethods.ZSTD_isError(result) != 0)
                    throw new InvalidOperationException("zstd compress failed");
                Array.Resize(ref dest, (int)result);
                return dest;
            }
        }
    }

    private static unsafe byte[] CompressOodle(byte[] data)
    {
        if (!NativeMethods.IsOodleLoaded)
            throw new InvalidOperationException("Oodle DLL not loaded");

        var compressor = NativeMethods.OodleLZ_Compressor_Kraken;
        var level = NativeMethods.OodleLZ_CompressionLevel_Normal;

        fixed (byte* srcPtr = data)
        {
            var srcLen = (nuint)data.Length;
            var bound = NativeMethods.OodleCompressBound(srcLen, compressor);
            var dest = new byte[bound];
            fixed (byte* dstPtr = dest)
            {
                var result = NativeMethods.OodleCompress((nint)srcPtr, srcLen, (nint)dstPtr, bound, compressor, level);
                if (result == 0)
                    throw new InvalidOperationException("Oodle compress failed");
                Array.Resize(ref dest, (int)result);
                return dest;
            }
        }
    }

    private static byte[] XorEncrypt(byte[] data)
    {
        var result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
            result[i] = (byte)(data[i] ^ PubgmCnXorKey);
        return result;
    }

    private static byte[] Aes256EcbEncrypt(byte[] data, byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("AES-256 key must be 32 bytes", nameof(key));

        var padLen = 16 - (data.Length % 16);
        if (padLen == 0) padLen = 16;
        var paddedData = new byte[data.Length + padLen];
        Array.Copy(data, paddedData, data.Length);
        for (var i = data.Length; i < paddedData.Length; i++)
            paddedData[i] = (byte)padLen;

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(paddedData, 0, paddedData.Length);
    }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, v);
        s.Write(buf);
    }

    private static void WriteI64(Stream s, long v)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buf, v);
        s.Write(buf);
    }

    private static int Align(int value, int alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : value + alignment - remainder;
    }

    private static void WriteString(Stream s, string str)
    {
        var bytes = Encoding.UTF8.GetBytes(str);
        WriteU32(s, (uint)bytes.Length + 1);
        s.Write(bytes);
        s.WriteByte(0);
    }
}

using System.IO;
using System.Runtime.InteropServices;

namespace PakToolGUI;

internal static unsafe class NativeMethods
{
    // ==================== zlib (z.dll) ====================
    private const string ZlibDll = "z.dll";

    [DllImport(ZlibDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int compress(nint dest, ref nuint destLen, nint source, nuint sourceLen);

    [DllImport(ZlibDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int compress2(nint dest, ref nuint destLen, nint source, nuint sourceLen, int level);

    [DllImport(ZlibDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint compressBound(nuint sourceLen);

    [DllImport(ZlibDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int uncompress(nint dest, ref nuint destLen, nint source, nuint sourceLen);

    // ==================== zstd (zstd.dll) ====================
    private const string ZstdDll = "zstd.dll";

    [DllImport(ZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint ZSTD_compress(nint dst, nuint dstCapacity, nint src, nuint srcSize, int compressionLevel);

    [DllImport(ZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint ZSTD_compressBound(nuint srcSize);

    [DllImport(ZstdDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint ZSTD_isError(nuint code);

    // ==================== Oodle (oo2core_*_win64.dll) ====================
    public const int OodleLZ_Compressor_Kraken = 8;
    public const int OodleLZ_CompressionLevel_Normal = 4;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint OodleCompressDelegate(
        int compressor, nint srcBuf, nuint srcLen, nint dstBuf, int level,
        nint opts, nint dict, nint scratch, nuint scratchSize, int threadPhase);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint OodleCompressBoundDelegate(nuint srcLen, int compressor);

    private static nint _oodleModule;
    private static OodleCompressDelegate? _oodleCompress;
    private static OodleCompressBoundDelegate? _oodleCompressBound;

    public static bool IsOodleLoaded => _oodleModule != 0;

    public static bool LoadOodle(string? dllPath = null)
    {
        if (_oodleModule != 0) return true;

        if (dllPath != null && File.Exists(dllPath))
        {
            _oodleModule = NativeLibrary.Load(dllPath);
        }
        else
        {
            foreach (var name in new[] { "oo2core_9_win64.dll", "oo2core_8_win64.dll", "oo2core_6_win64.dll" })
            {
                if (NativeLibrary.TryLoad(name, typeof(NativeMethods).Assembly, null, out _oodleModule))
                    break;
            }
        }

        if (_oodleModule == 0) return false;

        if (!NativeLibrary.TryGetExport(_oodleModule, "OodleLZ_Compress", out var compressPtr)) return false;
        if (!NativeLibrary.TryGetExport(_oodleModule, "OodleLZ_GetCompressedBufferSizeNeeded", out var boundPtr)) return false;

        _oodleCompress = Marshal.GetDelegateForFunctionPointer<OodleCompressDelegate>(compressPtr);
        _oodleCompressBound = Marshal.GetDelegateForFunctionPointer<OodleCompressBoundDelegate>(boundPtr);
        return true;
    }

    public static nuint OodleCompress(nint src, nuint srcLen, nint dst, nuint dstCap, int compressor, int level)
    {
        if (_oodleCompress == null) throw new InvalidOperationException("Oodle not loaded");
        return _oodleCompress(compressor, src, srcLen, dst, level, 0, 0, 0, 0, 0);
    }

    public static nuint OodleCompressBound(nuint srcLen, int compressor)
    {
        if (_oodleCompressBound == null) throw new InvalidOperationException("Oodle not loaded");
        return _oodleCompressBound(srcLen, compressor);
    }

    // ==================== OpenSSL (libcrypto-3-x64.dll) ====================
    private const string CryptoDll = "libcrypto-3-x64.dll";

    [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint EVP_CIPHER_fetch(nint ctx, string algorithm, string properties);

    [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint EVP_CIPHER_CTX_new();

    [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void EVP_CIPHER_CTX_free(nint ctx);

    [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int EVP_EncryptInit_ex(nint ctx, nint cipher, nint engine, nint key, nint iv);

    [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int EVP_EncryptUpdate(nint ctx, nint outBuf, ref int outLen, nint inBuf, int inLen);

    [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int EVP_EncryptFinal_ex(nint ctx, nint outBuf, ref int outLen);

    [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int EVP_DecryptInit_ex(nint ctx, nint cipher, nint engine, nint key, nint iv);

    [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int EVP_DecryptUpdate(nint ctx, nint outBuf, ref int outLen, nint inBuf, int inLen);

    [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int EVP_DecryptFinal_ex(nint ctx, nint outBuf, ref int outLen);

    [DllImport(CryptoDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void EVP_CIPHER_free(nint cipher);
}

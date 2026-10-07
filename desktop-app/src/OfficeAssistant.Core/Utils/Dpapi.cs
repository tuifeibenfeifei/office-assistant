using System.Runtime.InteropServices;
using System.Text;

namespace OfficeAssistant.Core.Utils;

/// <summary>
/// Windows DPAPI（crypt32.dll 的 CryptProtectData / CryptUnprotectData）封装。
///
/// 说明：.NET 8 中 <c>System.Security.Cryptography.ProtectedData</c> 属于独立 NuGet 包，
/// 本项目零第三方依赖（离线构建约束），因此直接调用系统 DPAPI。
/// 密文只能由同一 Windows 用户账户解密，密钥由系统托管，应用自身不保存任何密钥。
/// </summary>
public static class Dpapi
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>用当前用户凭据加密字节数组。</summary>
    public static byte[] Protect(byte[] plain, string description = "OfficeAssistant")
    {
        var inBlob = ToBlob(plain);
        try
        {
            if (!CryptProtectData(ref inBlob, description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
                throw new InvalidOperationException($"DPAPI 加密失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
            try { return FromBlob(outBlob); }
            finally { if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData); }
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
        }
    }

    /// <summary>解密当前用户加密的字节数组。</summary>
    public static byte[] Unprotect(byte[] cipher)
    {
        var inBlob = ToBlob(cipher);
        try
        {
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
                throw new InvalidOperationException($"DPAPI 解密失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
            try { return FromBlob(outBlob); }
            finally { if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData); }
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
        }
    }

    public static string ProtectString(string plain)
        => Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(plain)));

    public static string UnprotectString(string cipherBase64)
        => Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(cipherBase64)));

    private static DataBlob ToBlob(byte[] data)
    {
        var blob = new DataBlob { cbData = data.Length, pbData = IntPtr.Zero };
        if (data.Length == 0) return blob;
        blob.pbData = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static byte[] FromBlob(DataBlob blob)
    {
        if (blob.cbData <= 0 || blob.pbData == IntPtr.Zero) return [];
        var result = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, result, 0, blob.cbData);
        return result;
    }
}

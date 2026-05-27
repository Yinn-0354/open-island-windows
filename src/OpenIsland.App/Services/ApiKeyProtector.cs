using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenIsland.App.Services;

/// <summary>
/// 用 Windows DPAPI（当前用户密钥）加 / 解密第三方模型的 API key，使其不以明文落到
/// %APPDATA%\OpenIsland\settings.json。纯 P/Invoke crypt32，无需额外 NuGet 包。
///
/// 落盘格式：明文 → <c>dpapi:v1:&lt;base64(DPAPI 密文)&gt;</c>。
/// 向后兼容：旧的明文 key（无前缀）原样返回；解密失败（换机器 / 换 Windows 用户，密钥不匹配）
/// 返回空串，让用户在控制中心重新填写，而不是抛异常或写出乱码。
///
/// 注意：写进 ~/.claude/settings.json 的 env 仍是解密后的明文（Claude CLI 需要明文），
/// 这里加密的只是 OpenIsland 自己的配置文件。
/// </summary>
internal static class ApiKeyProtector
{
    private const string Prefix = "dpapi:v1:";
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>明文 → 带前缀的 DPAPI 密文。已是密文 / 空则原样返回；加密失败退回明文（不丢 key）。</summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain ?? "";
        if (plain.StartsWith(Prefix, StringComparison.Ordinal)) return plain;

        var data = Encoding.UTF8.GetBytes(plain);
        var inBlob = default(DATA_BLOB);
        var outBlob = default(DATA_BLOB);
        var pin = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, pin, data.Length);
            inBlob.cbData = data.Length;
            inBlob.pbData = pin;
            if (!CryptProtectData(ref inBlob, "OpenIsland API key", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                return plain; // 加密失败：退回明文，总比丢 key 好
            var enc = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, enc, 0, outBlob.cbData);
            return Prefix + Convert.ToBase64String(enc);
        }
        catch { return plain; }
        finally
        {
            Marshal.FreeHGlobal(pin);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    /// <summary>带前缀的 DPAPI 密文 → 明文。旧明文（无前缀）原样返回；解密失败返回空串。</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored ?? "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored; // 旧明文，向后兼容

        byte[] enc;
        try { enc = Convert.FromBase64String(stored.Substring(Prefix.Length)); }
        catch { return ""; }

        var inBlob = default(DATA_BLOB);
        var outBlob = default(DATA_BLOB);
        var pin = Marshal.AllocHGlobal(enc.Length);
        try
        {
            Marshal.Copy(enc, 0, pin, enc.Length);
            inBlob.cbData = enc.Length;
            inBlob.pbData = pin;
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                return ""; // 解密失败（换机 / 换用户）→ 清空，让用户重填
            var dec = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, dec, 0, outBlob.cbData);
            return Encoding.UTF8.GetString(dec);
        }
        catch { return ""; }
        finally
        {
            Marshal.FreeHGlobal(pin);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}

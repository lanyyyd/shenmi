using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SteamEyaWinUI.Localization;

namespace SteamEyaWinUI.Services;

internal sealed class SteamCryptoService
{
    private const int CryptprotectUiForbidden = 0x1;

    public string EncryptToHex(string token, string accountName)
    {
        var data = Encoding.UTF8.GetBytes(token);
        var entropy = Encoding.UTF8.GetBytes(accountName);
        var dataBlob = CreateBlob(data);
        var entropyBlob = CreateBlob(entropy);

        try
        {
            // 对齐 SteamEYA_GUI.exe：仅 CRYPTPROTECT_UI_FORBIDDEN（不加 AUDIT）。
            var flags = CryptprotectUiForbidden;
            if (!CryptProtectData(
                    ref dataBlob,
                    "BObfuscateBuffer",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    flags,
                    out var protectedBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), Loc.T("Steam_Error_TokenEncryptFailed"));
            }

            try
            {
                var output = new byte[protectedBlob.cbData];
                Marshal.Copy(protectedBlob.pbData, output, 0, output.Length);
                return Convert.ToHexString(output).ToLowerInvariant();
            }
            finally
            {
                LocalFree(protectedBlob.pbData);
            }
        }
        finally
        {
            FreeBlob(dataBlob);
            FreeBlob(entropyBlob);
        }
    }

    /// <summary>解密 <see cref="EncryptToHex"/> 产出的密文（同一 Windows 用户、同一 accountName 才能解开）。</summary>
    public string DecryptFromHex(string hex, string accountName)
    {
        var data = Convert.FromHexString(hex);
        var entropy = Encoding.UTF8.GetBytes(accountName);
        var dataBlob = CreateBlob(data);
        var entropyBlob = CreateBlob(entropy);

        try
        {
            // 与加密保持一致：仅 CRYPTPROTECT_UI_FORBIDDEN。
            var flags = CryptprotectUiForbidden;
            if (!CryptUnprotectData(
                    ref dataBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    flags,
                    out var plainBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), Loc.T("Steam_Error_TokenEncryptFailed"));
            }

            try
            {
                var output = new byte[plainBlob.cbData];
                Marshal.Copy(plainBlob.pbData, output, 0, output.Length);
                return Encoding.UTF8.GetString(output);
            }
            finally
            {
                LocalFree(plainBlob.pbData);
            }
        }
        finally
        {
            FreeBlob(dataBlob);
            FreeBlob(entropyBlob);
        }
    }

    private static DataBlob CreateBlob(byte[] data)
    {
        var blob = new DataBlob
        {
            cbData = data.Length,
            pbData = Marshal.AllocHGlobal(data.Length)
        };

        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.pbData);
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string szDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }
}

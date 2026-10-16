using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PcCard.Core.Models;

namespace PcCard.Core.Storage;

/// <summary>
/// 云凭据存储：DPAPI(LocalMachine) 加密后落 <c>%ProgramData%\PcCard\credentials.bin</c>。
///
/// ★ LocalMachine 作用域：登录用户与 SYSTEM（计划任务）都能解，这是
///   「无人登录时定时任务也能执行」的前提。仅限本机，拷走文件解不开。
/// </summary>
public static partial class CredentialVault
{
    private const uint CRYPTPROTECT_LOCAL_MACHINE = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        ref DATA_BLOB pDataIn, IntPtr szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr hMem);

    public static bool Exists => File.Exists(AppPaths.CredentialsPath);

    public static void Save(Credentials cred)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(cred, JsonFile.Options);
        var blob = Protect(plain);
        File.WriteAllBytes(AppPaths.CredentialsPath, blob);
    }

    public static Credentials? Load()
    {
        if (!File.Exists(AppPaths.CredentialsPath)) return null;
        try
        {
            var plain = Unprotect(File.ReadAllBytes(AppPaths.CredentialsPath));
            return JsonSerializer.Deserialize<Credentials>(plain, JsonFile.Options);
        }
        catch
        {
            return null;
        }
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(AppPaths.CredentialsPath)) File.Delete(AppPaths.CredentialsPath);
        }
        catch
        {
            // 删不掉就算了
        }
    }

    private static byte[] Protect(byte[] data)
    {
        var inBlob = MakeBlob(data);
        try
        {
            if (!CryptProtectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_LOCAL_MACHINE, out var outBlob))
                throw new InvalidOperationException($"CryptProtectData 失败，Win32 错误 {Marshal.GetLastWin32Error()}");
            try
            {
                return TakeBlob(outBlob);
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            LocalFree(inBlob.pbData);
        }
    }

    private static byte[] Unprotect(byte[] blob)
    {
        var inBlob = MakeBlob(blob);
        try
        {
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    0, out var outBlob))
                throw new InvalidOperationException($"CryptUnprotectData 失败，Win32 错误 {Marshal.GetLastWin32Error()}");
            try
            {
                return TakeBlob(outBlob);
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            LocalFree(inBlob.pbData);
        }
    }

    private static DATA_BLOB MakeBlob(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DATA_BLOB { cbData = data.Length, pbData = ptr };
    }

    private static byte[] TakeBlob(DATA_BLOB blob)
    {
        var result = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, result, 0, blob.cbData);
        return result;
    }
}

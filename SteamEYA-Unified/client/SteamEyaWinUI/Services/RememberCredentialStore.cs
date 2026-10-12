using System.IO;

namespace SteamEyaWinUI.Services;

/// <summary>
/// 「记住密码」凭据存储：用户名明文 + 密码用 DPAPI（当前 Windows 用户）加密，
/// 落盘于 %AppData%\SteamEYA\remember.dat。换机或换 Windows 用户后无法解密，属预期行为。
/// </summary>
internal sealed class RememberCredentialStore
{
    private readonly string _path;
    private readonly SteamCryptoService _crypto = new();

    public RememberCredentialStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _path = Path.Combine(appData, "SteamEYA", "remember.dat");
    }

    /// <summary>读取已保存的凭据；文件不存在/损坏/无法解密时返回 null。</summary>
    public (string Username, string Password)? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var lines = File.ReadAllLines(_path);
            if (lines.Length < 2)
            {
                return null;
            }

            // 熵取用户名：密码与用户名绑定，与 SteamCryptoService 的加密方式保持一致。
            var username = lines[0];
            return (username, _crypto.DecryptFromHex(lines[1], username));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>加密保存用户名与密码。</summary>
    public void Save(string username, string password)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllLines(_path, [username, _crypto.EncryptToHex(password, username)]);
        }
        catch
        {
            // 保存失败不影响登录流程。
        }
    }

    /// <summary>清除已保存的凭据。</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
            // 忽略。
        }
    }
}

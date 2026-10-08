using System.Security.Cryptography;
using System.Text;
using WinIsland.Core.Diagnostics;

namespace WinIsland.Platform.Windows.Security;

/// <summary>
/// Small secrets (refresh tokens, access tokens) encrypted with DPAPI for the current Windows
/// user, stored next to the settings in %LOCALAPPDATA%\WinIsland\secrets.
/// </summary>
public static class SecretStore
{
    private static readonly byte[] Entropy = "WinIsland.SecretStore.v1"u8.ToArray();

    public static string Directory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinIsland", "secrets");

    public static string? Read(string name)
    {
        string path = PathOf(name);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] data = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            AppLog.Warn(nameof(SecretStore), $"Could not read secret '{name}'", ex);
            return null;
        }
    }

    public static void Write(string name, string? value)
    {
        string path = PathOf(name);
        if (string.IsNullOrEmpty(value))
        {
            File.Delete(path);
            return;
        }

        System.IO.Directory.CreateDirectory(Directory);
        byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }

    private static string PathOf(string name) => Path.Combine(Directory, name + ".bin");
}

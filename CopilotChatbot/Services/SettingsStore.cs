using System.IO;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotChatbot.Models;

namespace CopilotChatbot.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private const string AesPrefix = "aes256:v1:";
    private const string DpapiPrefix = "dpapi:";
    private const int KeySizeBytes = 32;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const int KeyDerivationIterations = 120_000;
    private static readonly byte[] PasswordSalt = Encoding.UTF8.GetBytes("CopilotChatbot.Settings.Aes256.v1");
    private readonly string _settingsPath;
    private byte[]? _settingsKey;
    private string? _settingsPassword;
    private bool _useBlankSettingsForSession;

    public bool HasActiveSettingsPassword => _settingsKey is not null;
    public string SettingsPasswordForSession => _settingsPassword ?? "";

    public SettingsStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CopilotChatbot");
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    public AppSettings Load()
    {
        if (_useBlankSettingsForSession)
        {
            return new AppSettings();
        }

        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        var json = File.ReadAllText(_settingsPath);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        return DecryptSettings(settings);
    }

    public void Save(AppSettings settings)
    {
        if (_useBlankSettingsForSession)
        {
            return;
        }

        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(EncryptSettings(settings), Options));
    }

    public bool RequiresSettingsPassword()
    {
        if (!File.Exists(_settingsPath))
        {
            return false;
        }

        var json = File.ReadAllText(_settingsPath);
        return json.Contains(AesPrefix, StringComparison.Ordinal);
    }

    public void SetSettingsPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            ClearSettingsPassword();
            return;
        }

        _settingsKey = DeriveSettingsKey(password);
        _settingsPassword = password;
        _useBlankSettingsForSession = false;
    }

    public void ClearSettingsPassword()
    {
        _settingsKey = null;
        _settingsPassword = null;
        _useBlankSettingsForSession = false;
    }

    public void UseBlankSettingsForSession()
    {
        _settingsKey = null;
        _settingsPassword = null;
        _useBlankSettingsForSession = true;
    }

    public string ProtectSecret(string value)
    {
        if (_settingsKey is not null)
        {
            return ProtectWithAes(value);
        }

        return ProtectWithDpapi(value);
    }

    public string UnprotectSecret(string encryptedValue)
    {
        if (string.IsNullOrWhiteSpace(encryptedValue))
        {
            return "";
        }

        if (encryptedValue.StartsWith(AesPrefix, StringComparison.Ordinal))
        {
            return UnprotectWithAes(encryptedValue);
        }

        if (encryptedValue.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            encryptedValue = encryptedValue[DpapiPrefix.Length..];
        }

        try
        {
            var bytes = Convert.FromBase64String(encryptedValue);
            var clearBytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clearBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return encryptedValue;
        }
    }

    private AppSettings EncryptSettings(AppSettings settings)
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, Options), Options) ?? new AppSettings();
        if (!string.IsNullOrWhiteSpace(copy.GitHubToken))
        {
            copy.GitHubToken = ProtectSecret(copy.GitHubToken);
        }

        copy.UserSecrets = new ObservableCollection<UserSecretSetting>(
            copy.UserSecrets.Select(secret => new UserSecretSetting
            {
                Name = secret.Name,
                EnvironmentVariable = secret.EnvironmentVariable,
                EncryptedValue = ProtectSecret(UnprotectSecret(secret.EncryptedValue))
            }));

        return copy;
    }

    private AppSettings DecryptSettings(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.GitHubToken) && IsProtectedValue(settings.GitHubToken))
        {
            settings.GitHubToken = UnprotectSecret(settings.GitHubToken);
        }

        foreach (var secret in settings.UserSecrets.Where(secret => IsProtectedValue(secret.EncryptedValue)))
        {
            _ = UnprotectSecret(secret.EncryptedValue);
        }

        return settings;
    }

    private static bool IsProtectedValue(string value)
    {
        return value.StartsWith(AesPrefix, StringComparison.Ordinal) ||
               value.StartsWith(DpapiPrefix, StringComparison.Ordinal);
    }

    private static string ProtectWithDpapi(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return DpapiPrefix + Convert.ToBase64String(protectedBytes);
    }

    private string ProtectWithAes(string value)
    {
        var key = _settingsKey ?? throw new SettingsDecryptionException("Settings password is not available.");
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var payload = new byte[NonceSizeBytes + TagSizeBytes + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceSizeBytes);
        Buffer.BlockCopy(tag, 0, payload, NonceSizeBytes, TagSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, payload, NonceSizeBytes + TagSizeBytes, ciphertext.Length);
        return AesPrefix + Convert.ToBase64String(payload);
    }

    private string UnprotectWithAes(string encryptedValue)
    {
        var key = _settingsKey ?? throw new SettingsDecryptionException("Settings password is required to decrypt saved settings.");
        try
        {
            var payload = Convert.FromBase64String(encryptedValue[AesPrefix.Length..]);
            if (payload.Length < NonceSizeBytes + TagSizeBytes)
            {
                throw new SettingsDecryptionException("Saved settings are encrypted but the payload is invalid.");
            }

            var nonce = payload[..NonceSizeBytes];
            var tag = payload[NonceSizeBytes..(NonceSizeBytes + TagSizeBytes)];
            var ciphertext = payload[(NonceSizeBytes + TagSizeBytes)..];
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw new SettingsDecryptionException("The settings password is incorrect or the saved settings are corrupted.", ex);
        }
    }

    private static byte[] DeriveSettingsKey(string password)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            password,
            PasswordSalt,
            KeyDerivationIterations,
            HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeySizeBytes);
    }
}

public sealed class SettingsDecryptionException : Exception
{
    public SettingsDecryptionException(string message) : base(message) { }
    public SettingsDecryptionException(string message, Exception innerException) : base(message, innerException) { }
}

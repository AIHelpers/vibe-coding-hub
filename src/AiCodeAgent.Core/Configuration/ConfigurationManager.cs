using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Core.Configuration;

public class ConfigurationService
{
    private readonly string _configPath;
    private readonly ILogger<ConfigurationService>? _logger;
    private AppConfiguration _config = AppConfiguration.GetDefaults();

    /// <summary>
    /// Raised after configuration is saved via <see cref="SaveAsync"/>. Subscribers
    /// (e.g. view-models) use this to react to changes such as a new working directory.
    /// </summary>
    public event EventHandler? Saved;

    public ConfigurationService(string? configDir = null, ILogger<ConfigurationService>? logger = null)
    {
        _logger = logger;
        configDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aiagent");
        Directory.CreateDirectory(configDir);
        _configPath = Path.Combine(configDir, "config.json");
    }

    public AppConfiguration Config => _config;

    public async Task LoadAsync()
    {
        if (!File.Exists(_configPath))
        {
            await SaveAsync();
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_configPath);
            _config = JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions.Default)
                      ?? AppConfiguration.GetDefaults();

            // Normalize the default provider name to lowercase so it always
            // matches the provider dictionary key casing (e.g. "Ollama" -> "ollama").
            if (!string.IsNullOrEmpty(_config.DefaultProvider))
            {
                _config.DefaultProvider = _config.DefaultProvider.ToLowerInvariant();
            }

            // Decrypt any encrypted API keys
            var decryptedProviders = new Dictionary<string, ProviderConfiguration>();
            foreach (var (key, provider) in _config.Providers)
            {
                if (!string.IsNullOrEmpty(provider.ApiKey) && IsEncrypted(provider.ApiKey))
                {
                    var decrypted = DecryptString(provider.ApiKey);
                    if (decrypted != null)
                    {
                        decryptedProviders[key] = provider with { ApiKey = decrypted };
                        continue;
                    }
                }
                decryptedProviders[key] = provider;
            }
            _config.Providers = decryptedProviders;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load configuration, using defaults");
            _config = AppConfiguration.GetDefaults();
        }
    }

    public async Task SaveAsync()
    {
        // Encrypt API keys before saving
        var encryptedProviders = new Dictionary<string, ProviderConfiguration>();
        foreach (var (key, provider) in _config.Providers)
        {
            if (!string.IsNullOrEmpty(provider.ApiKey))
            {
                encryptedProviders[key] = provider with { ApiKey = EncryptString(provider.ApiKey) };
            }
            else
            {
                encryptedProviders[key] = provider;
            }
        }
        _config.Providers = encryptedProviders;
        var json = JsonSerializer.Serialize(_config, JsonOptions.Pretty);
        await File.WriteAllTextAsync(_configPath, json);

        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Persists the user's selected model (alias or concrete id) into
    /// <see cref="UiConfiguration.SelectedModel"/> and saves configuration.
    /// </summary>
    public async Task SetSelectedModelAsync(string model)
    {
        _config.Ui.SelectedModel = model;
        await SaveAsync();
    }

    public void SetApiKey(string provider, string apiKey)
    {
        // Case-insensitive lookup so "Ollama" matches the lowercase key "ollama"
        var key = _config.Providers.Keys
            .FirstOrDefault(k => string.Equals(k, provider, StringComparison.OrdinalIgnoreCase));
        if (key != null)
        {
            _config.Providers[key] = _config.Providers[key] with { ApiKey = apiKey };
        }
    }

    public ProviderConfiguration? GetProvider(string name)
    {
        if (_config.Providers.TryGetValue(name, out var cfg))
            return cfg;

        // Fall back to a case-insensitive lookup so config values like
        // "Ollama" match the lowercase provider key "ollama".
        var match = _config.Providers.Keys
            .FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        return match != null ? _config.Providers[match] : null;
    }

    /// <summary>
    /// Encrypts a string using AES-256-GCM with a machine-derived key.
    /// Works cross-platform on Windows, Linux, and macOS.
    /// </summary>
    private static string EncryptString(string plainText)
    {
        try
        {
            var key = GetMachineKey();
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];     // 16 bytes
            var ciphertext = new byte[plainBytes.Length];

            RandomNumberGenerator.Fill(nonce);

            using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
            aes.Encrypt(nonce, plainBytes, ciphertext, tag);

            // Format: AESGCM:{base64(nonce)}:{base64(ciphertext)}:{base64(tag)}
            return $"AESGCM:{Convert.ToBase64String(nonce)}:{Convert.ToBase64String(ciphertext)}:{Convert.ToBase64String(tag)}";
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to encrypt API key. Ensure the platform supports AES-GCM.", ex);
        }
    }

    /// <summary>
    /// Decrypts a string encrypted with EncryptString.
    /// </summary>
    private static string? DecryptString(string encryptedText)
    {
        try
        {
            if (!encryptedText.StartsWith("AESGCM:"))
                return null;

            var parts = encryptedText.Split(':');
            if (parts.Length != 4)
                return null;

            var nonce = Convert.FromBase64String(parts[1]);
            var ciphertext = Convert.FromBase64String(parts[2]);
            var tag = Convert.FromBase64String(parts[3]);
            var key = GetMachineKey();
            var plainBytes = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
            aes.Decrypt(nonce, ciphertext, tag, plainBytes);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex)
        {
            // Log but don't expose details - return null to indicate decryption failure
            System.Diagnostics.Debug.WriteLine($"Decryption failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Checks if a value looks like an encrypted string (AESGCM format).
    /// </summary>
    private static bool IsEncrypted(string value) =>
        value.StartsWith("AESGCM:");

    /// <summary>
    /// Derives a machine-specific encryption key using PBKDF2.
    /// This ensures the key is bound to the machine but works cross-platform.
    /// </summary>
    private static byte[] GetMachineKey()
    {
        // Use machine-specific entropy sources that work cross-platform
        var machineName = Environment.MachineName;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var osVersion = Environment.OSVersion.VersionString;

        // Combine entropy sources and derive a 256-bit key via PBKDF2
        var entropy = Encoding.UTF8.GetBytes($"{machineName}::{userProfile}::{osVersion}::AiCodeAgent-v1");
        var salt = new byte[16];
        
        // Use a fixed salt derived from the application name for deterministic key derivation
        var appSalt = SHA256.HashData(Encoding.UTF8.GetBytes("AiCodeAgent-KeyDerivation-v1"));
        Array.Copy(appSalt, salt, Math.Min(salt.Length, appSalt.Length));

        return Rfc2898DeriveBytes.Pbkdf2(entropy, salt, 600_000, HashAlgorithmName.SHA256, 32);
    }
}
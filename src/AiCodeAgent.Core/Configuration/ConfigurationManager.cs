using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProtectedDataApi = System.Security.Cryptography.ProtectedData;

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

            // Decrypt any encrypted API keys. A key stored in the legacy
            // AESGCM format (see DecryptString) is decrypted via a
            // migration fallback and flagged so we immediately re-save it
            // using the current scheme (DPAPI on Windows) — otherwise it
            // would keep round-tripping through the fragile legacy key
            // derivation on every load until it finally broke.
            var decryptedProviders = new Dictionary<string, ProviderConfiguration>();
            var needsMigration = false;
            foreach (var (key, provider) in _config.Providers)
            {
                if (!string.IsNullOrEmpty(provider.ApiKey) && IsEncrypted(provider.ApiKey))
                {
                    var (decrypted, wasLegacy) = DecryptString(provider.ApiKey);
                    if (decrypted != null)
                    {
                        decryptedProviders[key] = provider with { ApiKey = decrypted };
                        if (wasLegacy)
                            needsMigration = true;
                        continue;
                    }

                    _logger?.LogWarning(
                        "Could not decrypt the stored API key for provider '{Provider}' — it will need to be re-entered.",
                        key);
                }
                decryptedProviders[key] = provider;
            }
            _config.Providers = decryptedProviders;

            if (needsMigration)
            {
                _logger?.LogInformation("Migrating one or more API keys to the current encryption scheme.");
                await SaveAsync();
            }
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
    /// DPAPI entropy — an extra, app-specific secret mixed into the
    /// Windows Data Protection API call so another app running as the same
    /// user can't call ProtectedData.Unprotect on our blob.
    /// </summary>
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("AiCodeAgent-DPAPI-v1");

    /// <summary>
    /// Encrypts a string. On Windows this uses DPAPI (<see cref="ProtectedDataApi"/>,
    /// scoped to the current user account) — the OS already binds and
    /// protects the key material, with no derivation of our own to get
    /// wrong or invalidate. Elsewhere (no built-in OS-keychain API in the
    /// BCL) it falls back to AES-256-GCM with a machine-derived key.
    /// </summary>
    private static string EncryptString(string plainText)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var plainBytes = Encoding.UTF8.GetBytes(plainText);
                var protectedBytes = ProtectedDataApi.Protect(plainBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
                return $"DPAPI:{Convert.ToBase64String(protectedBytes)}";
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to encrypt API key via DPAPI.", ex);
            }
        }

        try
        {
            var key = GetMachineKeyStable();
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
    /// Decrypts a string produced by <see cref="EncryptString"/>. Returns the
    /// plaintext plus whether it had to fall back to the legacy
    /// (OS-version-dependent) key derivation to get there, which callers use
    /// to trigger a one-time migration back to the current scheme.
    /// </summary>
    private static (string? PlainText, bool WasLegacy) DecryptString(string encryptedText)
    {
        try
        {
            if (encryptedText.StartsWith("DPAPI:", StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows())
                    return (null, false); // DPAPI blobs don't travel across OSes

                var protectedBytes = Convert.FromBase64String(encryptedText["DPAPI:".Length..]);
                var plainBytes = ProtectedDataApi.Unprotect(protectedBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
                return (Encoding.UTF8.GetString(plainBytes), false);
            }

            if (!encryptedText.StartsWith("AESGCM:", StringComparison.Ordinal))
                return (null, false);

            var parts = encryptedText.Split(':');
            if (parts.Length != 4)
                return (null, false);

            var nonce = Convert.FromBase64String(parts[1]);
            var ciphertext = Convert.FromBase64String(parts[2]);
            var tag = Convert.FromBase64String(parts[3]);
            var plainBytes2 = new byte[ciphertext.Length];

            // Try the current (stable) key first, then fall back to the
            // legacy derivation — which included Environment.MachineName,
            // the user-profile PATH and Environment.OSVersion.VersionString,
            // so a Windows update or a renamed profile directory silently
            // made every previously-saved key undecryptable (issue 10).
            foreach (var (key, isLegacy) in new[] { (GetMachineKeyStable(), false), (GetMachineKeyLegacy(), true) })
            {
                try
                {
                    using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
                    aes.Decrypt(nonce, ciphertext, tag, plainBytes2);
                    return (Encoding.UTF8.GetString(plainBytes2), isLegacy);
                }
                catch (CryptographicException)
                {
                    // Wrong key for this blob (e.g. AuthenticationTagMismatchException
                    // on a modern runtime) — try the next one.
                }
            }

            return (null, false);
        }
        catch (Exception ex)
        {
            // Log but don't expose details - return null to indicate decryption failure
            System.Diagnostics.Debug.WriteLine($"Decryption failed: {ex.Message}");
            return (null, false);
        }
    }

    /// <summary>
    /// Checks if a value looks like an encrypted string (DPAPI or legacy
    /// AESGCM format).
    /// </summary>
    private static bool IsEncrypted(string value) =>
        value.StartsWith("DPAPI:", StringComparison.Ordinal) || value.StartsWith("AESGCM:", StringComparison.Ordinal);

    /// <summary>
    /// Derives a machine-specific encryption key using PBKDF2, for the
    /// non-Windows fallback path. Deliberately excludes anything that
    /// changes on its own — no OS version string, no user-profile path —
    /// so the key stays stable across routine OS updates and account
    /// changes; only <see cref="Environment.MachineName"/> plus a fixed
    /// app salt.
    /// </summary>
    private static byte[] GetMachineKeyStable()
    {
        var entropy = Encoding.UTF8.GetBytes($"{Environment.MachineName}::AiCodeAgent-v2");
        var salt = new byte[16];
        var appSalt = SHA256.HashData(Encoding.UTF8.GetBytes("AiCodeAgent-KeyDerivation-v2"));
        Array.Copy(appSalt, salt, Math.Min(salt.Length, appSalt.Length));
        return Rfc2898DeriveBytes.Pbkdf2(entropy, salt, 600_000, HashAlgorithmName.SHA256, 32);
    }

    /// <summary>
    /// The original (buggy) key derivation, kept ONLY so
    /// <see cref="DecryptString"/> can migrate values that were encrypted
    /// with it before this fix. Never used for new encryption.
    /// </summary>
    private static byte[] GetMachineKeyLegacy()
    {
        var machineName = Environment.MachineName;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var osVersion = Environment.OSVersion.VersionString;

        var entropy = Encoding.UTF8.GetBytes($"{machineName}::{userProfile}::{osVersion}::AiCodeAgent-v1");
        var salt = new byte[16];
        var appSalt = SHA256.HashData(Encoding.UTF8.GetBytes("AiCodeAgent-KeyDerivation-v1"));
        Array.Copy(appSalt, salt, Math.Min(salt.Length, appSalt.Length));

        return Rfc2898DeriveBytes.Pbkdf2(entropy, salt, 600_000, HashAlgorithmName.SHA256, 32);
    }
}
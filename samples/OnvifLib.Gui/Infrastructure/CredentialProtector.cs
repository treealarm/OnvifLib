using System.Security.Cryptography;
using System.Text;

namespace OnvifLib.Gui.Infrastructure;

/// <summary>
/// Encrypts remembered camera passwords with an operating-system-backed secret, so
/// <c>settings.json</c> never holds one in clear text.
/// </summary>
/// <remarks>
/// Windows: DPAPI, current-user scope — only the same Windows account on the same machine can
/// decrypt. Linux: AES-256-GCM with a random key kept in the desktop keyring (Secret Service over
/// D-Bus, see <see cref="SecretServiceKeyring"/>). Where neither is available,
/// <see cref="IsAvailable"/> is false and passwords are simply not persisted.
/// </remarks>
public static class CredentialProtector
{
  private const string DpapiPrefix = "dpapi:";
  private const string KeyringPrefix = "aesgcm:";

  // Ties the DPAPI blob to this app, so another program running as the same user cannot decrypt
  // it with a plain Unprotect call.
  private static readonly byte[] DpapiEntropy = "OnvifLib.Gui/remembered-password/v1"u8.ToArray();

  private const string KeyringLabel = "OnvifLib.Gui settings encryption key";
  private static readonly Dictionary<string, string> KeyringAttributes = new()
  {
    ["service"] = "OnvifLib.Gui",
    ["key"] = "settings-encryption",
  };

  // One bus round trip, made the first time the option is shown; its answer does not change
  // while the app runs.
  private static readonly Lazy<bool> KeyringAvailable = new(SecretServiceKeyring.IsAvailable);
  private static readonly object KeyLock = new();
  private static byte[]? _keyringKey;

  public static bool IsAvailable => OperatingSystem.IsWindows() || KeyringAvailable.Value;

  public static string UnavailableReason => OperatingSystem.IsLinux()
    ? Environment.GetEnvironmentVariable("SNAP_NAME") is { Length: > 0 } snap
      // Strict confinement: the keyring is on the bus, but the app may not talk to it yet.
      ? $"Remembering passwords needs the keyring interface: sudo snap connect {snap}:password-manager-service"
      : "Remembering passwords needs a desktop keyring (GNOME Keyring, KWallet) on the session bus."
    : "Remembering passwords is not supported on this platform.";

  /// <summary>Null when the password is empty or cannot be protected; never returns clear text.</summary>
  public static string? Protect(string password)
  {
    if (password.Length == 0 || !IsAvailable) return null;
    var plain = Encoding.UTF8.GetBytes(password);
    try
    {
      if (OperatingSystem.IsWindows())
        return DpapiPrefix + Convert.ToBase64String(ProtectedData.Protect(plain, DpapiEntropy, DataProtectionScope.CurrentUser));

      if (GetKeyringKey(create: true) is not { } key) return null;
      var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
      var tag = new byte[AesGcm.TagByteSizes.MaxSize];
      var cipher = new byte[plain.Length];
      using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, plain, cipher, tag);
      return KeyringPrefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }
    catch (Exception)
    {
      return null;
    }
    finally
    {
      CryptographicOperations.ZeroMemory(plain);
    }
  }

  /// <summary>Empty when the blob is missing, was made for another user or machine, or is corrupt.</summary>
  public static string Unprotect(string? blob)
  {
    if (string.IsNullOrEmpty(blob)) return "";
    try
    {
      if (blob.StartsWith(DpapiPrefix, StringComparison.Ordinal) && OperatingSystem.IsWindows())
      {
        var data = Convert.FromBase64String(blob[DpapiPrefix.Length..]);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, DpapiEntropy, DataProtectionScope.CurrentUser));
      }

      if (blob.StartsWith(KeyringPrefix, StringComparison.Ordinal) && OperatingSystem.IsLinux())
      {
        // Lookup only: a missing key means the blob is unreadable, not that a new one is due.
        if (GetKeyringKey(create: false) is not { } key) return "";
        var data = Convert.FromBase64String(blob[KeyringPrefix.Length..]);
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;
        if (data.Length < nonceSize + tagSize) return "";
        var plain = new byte[data.Length - nonceSize - tagSize];
        using (var aes = new AesGcm(key, tagSize))
          aes.Decrypt(data.AsSpan(0, nonceSize), data.AsSpan(nonceSize + tagSize), data.AsSpan(nonceSize, tagSize), plain);
        return Encoding.UTF8.GetString(plain);
      }
    }
    catch (Exception)
    {
      // Wrong account, wiped keyring, hand-edited file: the user re-enters the password.
    }

    return "";
  }

  private static byte[]? GetKeyringKey(bool create)
  {
    lock (KeyLock)
    {
      if (_keyringKey is not null) return _keyringKey;
      if (!KeyringAvailable.Value) return null;

      if (SecretServiceKeyring.Lookup(KeyringAttributes) is { } stored && TryDecodeKey(stored) is { } existing)
        return _keyringKey = existing;
      if (!create) return null;

      var fresh = RandomNumberGenerator.GetBytes(32);
      return SecretServiceKeyring.Store(KeyringLabel, KeyringAttributes, Convert.ToBase64String(fresh)) ? _keyringKey = fresh : null;
    }
  }

  private static byte[]? TryDecodeKey(string text)
  {
    try
    {
      var key = Convert.FromBase64String(text.Trim());
      return key.Length == 32 ? key : null;
    }
    catch (FormatException)
    {
      return null;
    }
  }
}

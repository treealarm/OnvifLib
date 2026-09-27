using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace OnvifLib.Gui.Infrastructure;

/// <summary>
/// Encrypts remembered camera passwords with an operating-system-backed secret, so
/// <c>settings.json</c> never holds one in clear text.
/// </summary>
/// <remarks>
/// Windows: DPAPI, current-user scope — only the same Windows account on the same machine can
/// decrypt. Linux: AES-256-GCM with a random key kept in the desktop keyring (Secret Service,
/// via <c>secret-tool</c> from libsecret). Where neither is available, <see cref="IsAvailable"/>
/// is false and passwords are simply not persisted.
/// </remarks>
public static class CredentialProtector
{
  private const string DpapiPrefix = "dpapi:";
  private const string KeyringPrefix = "aesgcm:";

  // Ties the DPAPI blob to this app, so another program running as the same user cannot decrypt
  // it with a plain Unprotect call.
  private static readonly byte[] DpapiEntropy = "OnvifLib.Gui/remembered-password/v1"u8.ToArray();

  private static readonly string[] KeyringAttributes = ["service", "OnvifLib.Gui", "key", "settings-encryption"];

  private static readonly Lazy<string?> SecretTool = new(() => ExecutableSearch.Find("secret-tool"));
  private static readonly object KeyLock = new();
  private static byte[]? _keyringKey;

  public static bool IsAvailable => OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && SecretTool.Value is not null);

  public static string UnavailableReason => OperatingSystem.IsLinux()
    ? "Remembering passwords needs secret-tool (libsecret-tools) and a running keyring."
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
      if (SecretTool.Value is not { } tool) return null;

      var (ok, output) = RunSecretTool(tool, ["lookup", .. KeyringAttributes], stdin: null);
      if (ok && TryDecodeKey(output) is { } existing) return _keyringKey = existing;
      if (!create) return null;

      var fresh = RandomNumberGenerator.GetBytes(32);
      var (stored, _) = RunSecretTool(tool, ["store", "--label=OnvifLib.Gui settings encryption key", .. KeyringAttributes],
        stdin: Convert.ToBase64String(fresh));
      return stored ? _keyringKey = fresh : null;
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

  private static (bool Ok, string Output) RunSecretTool(string tool, IEnumerable<string> arguments, string? stdin)
  {
    var start = new ProcessStartInfo(tool)
    {
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);

    try
    {
      using var process = Process.Start(start);
      if (process is null) return (false, "");

      // The secret goes over stdin, never the command line, where other processes could read it.
      if (stdin is not null) process.StandardInput.Write(stdin);
      process.StandardInput.Close();

      var output = process.StandardOutput.ReadToEndAsync();
      _ = process.StandardError.ReadToEndAsync();

      // Generous: a locked keyring shows an unlock prompt and waits for the user to type.
      if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
      {
        try { process.Kill(); } catch (Exception) { /* already gone */ }
        return (false, "");
      }

      return (process.ExitCode == 0, output.Result);
    }
    catch (Exception)
    {
      return (false, "");
    }
  }
}

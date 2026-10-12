using System.Text;
using Tmds.DBus.Protocol;

namespace OnvifLib.Gui.Infrastructure;

/// <summary>
/// The desktop keyring — GNOME Keyring, KWallet and friends — through the Secret Service API on
/// the session D-Bus. Speaking to the bus directly means no <c>secret-tool</c> binary is needed:
/// the keyring itself runs on every desktop, but the tool is a separate package few have.
/// </summary>
/// <remarks>
/// Only the one shape this app needs is handled: a single text secret found by its attributes.
/// The transport session is "plain". The bus socket is per user, and any process that can reach
/// it can ask the keyring for the secret outright, so encrypting it on the way adds nothing.
/// The attributes are the ones <c>secret-tool</c> stored under in earlier versions, so a key made
/// then is still found now.
/// </remarks>
internal static class SecretServiceKeyring
{
  private const string Bus = "org.freedesktop.secrets";
  private const string ServicePath = "/org/freedesktop/secrets";
  private const string ServiceInterface = "org.freedesktop.Secret.Service";
  private const string CollectionInterface = "org.freedesktop.Secret.Collection";
  private const string ItemInterface = "org.freedesktop.Secret.Item";
  private const string PromptInterface = "org.freedesktop.Secret.Prompt";

  /// <summary>What the Secret Service returns for "no object": no prompt needed, no such collection.</summary>
  private const string NoObject = "/";

  // Generous: a locked keyring shows an unlock dialog and waits for the user to type.
  private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(2);
  private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

  /// <summary>True when a Secret Service answers on the session bus and has a default collection.</summary>
  /// <remarks>
  /// Asks the service itself instead of listing bus names. Under snap confinement ListNames is
  /// denied whatever interfaces are connected, whereas a call to the service goes through exactly
  /// when password-manager-service is connected; so this probe, not the name list, tells whether
  /// <see cref="Store"/> can work. The bus starts the keyring on first call if it is not running.
  /// </remarks>
  public static bool IsAvailable()
  {
    if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Address.Session)) return false;
    try
    {
      return Run(async connection => await ReadAliasAsync(connection, "default") != NoObject, ProbeTimeout);
    }
    catch (Exception)
    {
      return false;
    }
  }

  /// <summary>The text secret stored under these attributes; null when there is none or the keyring stays locked.</summary>
  public static string? Lookup(IReadOnlyDictionary<string, string> attributes)
  {
    try
    {
      return Run(async connection =>
      {
        var session = await OpenSessionAsync(connection);
        if (await FindItemAsync(connection, attributes) is not { } item) return null;
        return await GetSecretAsync(connection, session, item);
      }, PromptTimeout);
    }
    catch (Exception)
    {
      return null;
    }
  }

  /// <summary>Stores a text secret in the default collection, replacing one with the same attributes.</summary>
  public static bool Store(string label, IReadOnlyDictionary<string, string> attributes, string secret)
  {
    try
    {
      return Run(async connection =>
      {
        var session = await OpenSessionAsync(connection);
        var collection = await ReadAliasAsync(connection, "default");
        // No default keyring at all: creating one is a dialog flow of its own, not worth it here.
        if (collection == NoObject || !await UnlockAsync(connection, collection)) return false;
        var prompt = await CreateItemAsync(connection, collection, session, label, attributes, secret);
        return prompt == NoObject || await PromptAsync(connection, prompt);
      }, PromptTimeout);
    }
    catch (Exception)
    {
      return false;
    }
  }

  /// <summary>
  /// Callers are the synchronous settings load and save, so the bus work runs on a pool thread:
  /// a UI-thread caller then cannot deadlock on its own synchronization context.
  /// </summary>
  private static T Run<T>(Func<Connection, Task<T>> action, TimeSpan timeout) =>
    Task.Run(async () =>
    {
      using var connection = new Connection(Address.Session!);
      await connection.ConnectAsync();
      return await action(connection).WaitAsync(timeout);
    }).GetAwaiter().GetResult();

  private static async Task<string?> FindItemAsync(Connection connection, IReadOnlyDictionary<string, string> attributes)
  {
    var (unlocked, locked) = await SearchItemsAsync(connection, attributes);
    if (unlocked.Length > 0) return unlocked[0];
    if (locked.Length == 0) return null;

    // The item sits in a locked collection; unlocking it may put a dialog in front of the user.
    if (!await UnlockAsync(connection, locked[0])) return null;
    (unlocked, _) = await SearchItemsAsync(connection, attributes);
    return unlocked.Length > 0 ? unlocked[0] : null;
  }

  private static Task<string> OpenSessionAsync(Connection connection)
  {
    var message = Build(connection, ServicePath, ServiceInterface, "OpenSession", "sv", static (ref writer) =>
    {
      writer.WriteString("plain");
      writer.WriteVariantString("");
    });
    return connection.CallMethodAsync(message, static (message, _) =>
    {
      var reader = message.GetBodyReader();
      reader.ReadVariantValue();
      return reader.ReadObjectPathAsString();
    });
  }

  private static Task<(string[] Unlocked, string[] Locked)> SearchItemsAsync(Connection connection, IReadOnlyDictionary<string, string> attributes)
  {
    var message = Build(connection, ServicePath, ServiceInterface, "SearchItems", "a{ss}",
      (ref writer) => WriteAttributes(ref writer, attributes));
    return connection.CallMethodAsync(message, static (message, _) =>
    {
      var reader = message.GetBodyReader();
      return (Paths(reader.ReadArrayOfObjectPath()), Paths(reader.ReadArrayOfObjectPath()));
    });
  }

  /// <summary>Unlocks one object (collection or item); true once it is usable.</summary>
  private static async Task<bool> UnlockAsync(Connection connection, string path)
  {
    var message = Build(connection, ServicePath, ServiceInterface, "Unlock", "ao",
      (ref writer) => writer.WriteArray(new[] { new ObjectPath(path) }));
    var (unlocked, prompt) = await connection.CallMethodAsync(message, static (message, _) =>
    {
      var reader = message.GetBodyReader();
      return (Paths(reader.ReadArrayOfObjectPath()), reader.ReadObjectPathAsString());
    });
    if (unlocked.Contains(path)) return true;
    return prompt != NoObject && await PromptAsync(connection, prompt);
  }

  private static Task<string> ReadAliasAsync(Connection connection, string alias)
  {
    var message = Build(connection, ServicePath, ServiceInterface, "ReadAlias", "s",
      (ref writer) => writer.WriteString(alias));
    return connection.CallMethodAsync(message, static (message, _) => message.GetBodyReader().ReadObjectPathAsString());
  }

  private static Task<string> GetSecretAsync(Connection connection, string session, string item)
  {
    var message = Build(connection, item, ItemInterface, "GetSecret", "o",
      (ref writer) => writer.WriteObjectPath(session));
    return connection.CallMethodAsync(message, static (message, _) =>
    {
      // Secret struct: (session, parameters, value, content type). Plain session: value as is.
      var reader = message.GetBodyReader();
      reader.AlignStruct();
      reader.ReadObjectPathAsString();
      reader.ReadArrayOfByte();
      return Encoding.UTF8.GetString(reader.ReadArrayOfByte());
    });
  }

  /// <summary>Returns the prompt path the keyring wants shown first, or <see cref="NoObject"/>.</summary>
  private static Task<string> CreateItemAsync(Connection connection, string collection, string session,
    string label, IReadOnlyDictionary<string, string> attributes, string secret)
  {
    var message = Build(connection, collection, CollectionInterface, "CreateItem", "a{sv}(oayays)b", (ref writer) =>
    {
      writer.WriteDictionary(new Dictionary<string, VariantValue>
      {
        ["org.freedesktop.Secret.Item.Label"] = VariantValue.String(label),
        ["org.freedesktop.Secret.Item.Attributes"] = new Dict<string, string>(attributes.ToDictionary()).AsVariantValue(),
      });
      writer.WriteStructureStart();
      writer.WriteObjectPath(session);
      writer.WriteArray(Array.Empty<byte>());
      writer.WriteArray(Encoding.UTF8.GetBytes(secret));
      writer.WriteString("text/plain");
      writer.WriteBool(true); // replace an item with the same attributes
    });
    return connection.CallMethodAsync(message, static (message, _) =>
    {
      var reader = message.GetBodyReader();
      reader.ReadObjectPathAsString();
      return reader.ReadObjectPathAsString();
    });
  }

  /// <summary>Shows the keyring's own dialog and waits; false when the user dismissed it.</summary>
  private static async Task<bool> PromptAsync(Connection connection, string prompt)
  {
    var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var rule = new MatchRule { Type = MessageType.Signal, Path = prompt, Interface = PromptInterface, Member = "Completed" };

    // Subscribed before Prompt is sent: a keyring that needs no input answers at once.
    using var subscription = await connection.AddMatchAsync(rule,
      static (message, _) => message.GetBodyReader().ReadBool(),
      (error, dismissed, _, _) =>
      {
        if (error is not null) completed.TrySetException(error);
        else completed.TrySetResult(!dismissed);
      },
      ObserverFlags.None, readerState: null, handlerState: null, emitOnCapturedContext: false);

    // No parent window: the dialog stands on its own.
    await connection.CallMethodAsync(Build(connection, prompt, PromptInterface, "Prompt", "s", static (ref writer) => writer.WriteString("")));
    return await completed.Task;
  }

  private delegate void BodyWriter(ref MessageWriter writer);

  /// <summary>
  /// Builds a call to the Secret Service in one go. The writer is a ref struct, so it cannot live
  /// across an await; the finished buffer can.
  /// </summary>
  private static MessageBuffer Build(Connection connection, string path, string @interface, string member, string signature, BodyWriter body)
  {
    var writer = connection.GetMessageWriter();
    try
    {
      writer.WriteMethodCallHeader(Bus, path, @interface, member, signature);
      body(ref writer);
      return writer.CreateMessage();
    }
    finally
    {
      writer.Dispose();
    }
  }

  private static void WriteAttributes(ref MessageWriter writer, IReadOnlyDictionary<string, string> attributes)
  {
    var start = writer.WriteDictionaryStart();
    foreach (var (key, value) in attributes)
    {
      writer.WriteDictionaryEntryStart();
      writer.WriteString(key);
      writer.WriteString(value);
    }
    writer.WriteDictionaryEnd(start);
  }

  private static string[] Paths(ObjectPath[] paths) => [.. paths.Select(p => p.ToString())];
}

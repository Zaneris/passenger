using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Passenger.Core;

/// <summary>The system's own store of secrets, each kept under a service and an account: the login Keychain on macOS, the
/// Credential Manager on Windows, the Secret Service (GNOME Keyring, KWallet) on Linux. A test run, or a system with no
/// such store, keeps them in this process's memory alone (<see cref="Variable"/>), never in a file. Called on any thread.</summary>
public static partial class SystemCredentials
{
    /// <summary>The variable that, set to <c>memory</c>, keeps every secret in this process alone, as a test run does.</summary>
    [IdentifierHome]
    public const string Variable = "PASSENGER_CREDENTIALS";

    private static readonly ConcurrentDictionary<(string Service, string Account), string> Held = new();

    /// <summary>Whether secrets go to the system's store; false where they stay in memory.</summary>
    public static bool Kept => Environment.GetEnvironmentVariable(Variable) switch
    {
        null or "" => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() || OperatingSystem.IsLinux() && LinuxSecrets.Present,
        "memory" => false,
        string other => throw new InvalidOperationException($"{Variable} is \"{other}\": it takes only \"memory\""),
    };

    /// <summary>Keeps a secret, in place of any kept under the same service and account. False where the store refused it,
    /// which keeps it in memory instead, for this session.</summary>
    public static bool Store(string service, string account, string secret)
    {
        Held[(service, account)] = secret;
        if (!Kept)
        {
            return true;
        }
        try
        {
            return OperatingSystem.IsMacOS() ? MacKeychain.Store(service, account, secret)
                : OperatingSystem.IsWindows() ? WindowsCredentials.Store(service, account, secret)
                : LinuxSecrets.Store(service, account, secret);
        }
        catch (Exception failed) when (failed is ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            PassengerLog.Write($"Credentials: the system's store refused {service}: {failed.Message}");
            return false;
        }
    }

    /// <summary>The secret kept under the service and account, or null where none is.</summary>
    public static string? Read(string service, string account)
    {
        if (Held.TryGetValue((service, account), out string? held) || !Kept)
        {
            return held;
        }
        try
        {
            string? read = OperatingSystem.IsMacOS() ? MacKeychain.Read(service, account)
                : OperatingSystem.IsWindows() ? WindowsCredentials.Read(service, account)
                : LinuxSecrets.Read(service, account);
            if (read is not null)
            {
                Held[(service, account)] = read;
            }
            return read;
        }
        catch (Exception failed) when (failed is ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            PassengerLog.Write($"Credentials: the system's store gave nothing for {service}: {failed.Message}");
            return null;
        }
    }

    /// <summary>Forgets the secret kept under the service and account, where one is.</summary>
    public static void Forget(string service, string account)
    {
        Held.TryRemove((service, account), out _);
        if (!Kept)
        {
            return;
        }
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                MacKeychain.Forget(service, account);
            }
            else if (OperatingSystem.IsWindows())
            {
                WindowsCredentials.Forget(service, account);
            }
            else
            {
                LinuxSecrets.Forget(service, account);
            }
        }
        catch (Exception failed) when (failed is ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            PassengerLog.Write($"Credentials: the system's store kept {service}: {failed.Message}");
        }
    }

    /// <summary>The login Keychain's generic passwords, through Security's item calls.</summary>
    private static partial class MacKeychain
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const int NotFound = -25300;

        private static readonly nint SecurityLibrary = NativeLibrary.Load(Security);
        private static readonly nint FoundationLibrary = NativeLibrary.Load(Foundation);

        public static bool Store(string service, string account, string secret)
        {
            Forget(service, account);
            byte[] bytes = Encoding.UTF8.GetBytes(secret);
            using Owned data = new(CFDataCreate(0, bytes, bytes.Length));
            using Owned query = Query(service, account, ("kSecValueData", data.Handle));
            return Check(SecItemAdd(query.Handle, 0));
        }

        public static string? Read(string service, string account)
        {
            using Owned query = Query(service, account, ("kSecReturnData", Constant(FoundationLibrary, "kCFBooleanTrue")),
                ("kSecMatchLimit", Constant(SecurityLibrary, "kSecMatchLimitOne")));
            int status = SecItemCopyMatching(query.Handle, out nint found);
            if (status == NotFound)
            {
                return null;
            }
            Check(status);
            using Owned data = new(found);
            byte[] bytes = new byte[CFDataGetLength(found)];
            Marshal.Copy(CFDataGetBytePtr(found), bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }

        public static void Forget(string service, string account)
        {
            using Owned query = Query(service, account);
            int status = SecItemDelete(query.Handle);
            if (status != NotFound)
            {
                Check(status);
            }
        }

        /// <summary>A generic password's query by its service and account, with what else the call needs.</summary>
        private static Owned Query(string service, string account, params (string Key, nint Value)[] more)
        {
            using Owned serviceText = Text(service);
            using Owned accountText = Text(account);
            List<(string Key, nint Value)> pairs =
            [
                ("kSecClass", Constant(SecurityLibrary, "kSecClassGenericPassword")),
                ("kSecAttrService", serviceText.Handle),
                ("kSecAttrAccount", accountText.Handle),
                .. more,
            ];
            nint[] keys = [.. pairs.Select(p => Constant(SecurityLibrary, p.Key))];
            nint[] values = [.. pairs.Select(p => p.Value)];
            return new Owned(CFDictionaryCreate(0, keys, values, keys.Length,
                NativeLibrary.GetExport(FoundationLibrary, "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(FoundationLibrary, "kCFTypeDictionaryValueCallBacks")));
        }

        private static Owned Text(string text) => new(CFStringCreateWithCharacters(0, text, text.Length));

        private static nint Constant(nint library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));

        private static bool Check(int status) => status == 0 ? true : throw new ExternalException($"Keychain error {status}", status);

        /// <summary>A Core Foundation object this side made or was given, released once.</summary>
        private readonly struct Owned(nint handle) : IDisposable
        {
            public nint Handle { get; } = handle;

            public void Dispose()
            {
                if (Handle != 0)
                {
                    CFRelease(Handle);
                }
            }
        }

        [LibraryImport(Foundation, StringMarshalling = StringMarshalling.Utf16)]
        private static partial nint CFStringCreateWithCharacters(nint allocator, string characters, nint length);

        [LibraryImport(Foundation)]
        private static partial nint CFDataCreate(nint allocator, byte[] bytes, nint length);

        [LibraryImport(Foundation)]
        private static partial nint CFDataGetLength(nint data);

        [LibraryImport(Foundation)]
        private static partial nint CFDataGetBytePtr(nint data);

        [LibraryImport(Foundation)]
        private static partial nint CFDictionaryCreate(nint allocator, nint[] keys, nint[] values, nint count, nint keyCallBacks, nint valueCallBacks);

        [LibraryImport(Foundation)]
        private static partial void CFRelease(nint value);

        [LibraryImport(Security)]
        private static partial int SecItemAdd(nint attributes, nint result);

        [LibraryImport(Security)]
        private static partial int SecItemCopyMatching(nint query, out nint result);

        [LibraryImport(Security)]
        private static partial int SecItemDelete(nint query);
    }

    /// <summary>The Credential Manager's generic credentials, named <c>service/account</c>, kept on this machine for this user.</summary>
    private static partial class WindowsCredentials
    {
        private const int Generic = 1;
        private const int LocalMachine = 2;
        private const int NotFound = 1168;

        public static bool Store(string service, string account, string secret)
        {
            byte[] blob = Encoding.Unicode.GetBytes(secret);
            nint data = Marshal.AllocHGlobal(blob.Length);
            nint target = Marshal.StringToHGlobalUni($"{service}/{account}");
            nint user = Marshal.StringToHGlobalUni(account);
            try
            {
                Marshal.Copy(blob, 0, data, blob.Length);
                Credential credential = new()
                {
                    Type = Generic,
                    TargetName = target,
                    UserName = user,
                    CredentialBlobSize = blob.Length,
                    CredentialBlob = data,
                    Persist = LocalMachine,
                };
                if (!CredWrite(in credential, 0))
                {
                    throw new ExternalException("Credential Manager refused the secret", Marshal.GetLastPInvokeError());
                }
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(data);
                Marshal.FreeHGlobal(target);
                Marshal.FreeHGlobal(user);
            }
        }

        public static string? Read(string service, string account)
        {
            if (!CredRead($"{service}/{account}", Generic, 0, out nint found))
            {
                return Marshal.GetLastPInvokeError() == NotFound ? null : throw new ExternalException("Credential Manager gave no secret", Marshal.GetLastPInvokeError());
            }
            try
            {
                Credential credential = Marshal.PtrToStructure<Credential>(found);
                return Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
            }
            finally
            {
                CredFree(found);
            }
        }

        public static void Forget(string service, string account)
        {
            if (!CredDelete($"{service}/{account}", Generic, 0) && Marshal.GetLastPInvokeError() != NotFound)
            {
                throw new ExternalException("Credential Manager kept the secret", Marshal.GetLastPInvokeError());
            }
        }

        /// <summary>CREDENTIALW, its strings as pointers.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct Credential
        {
            public int Flags;
            public int Type;
            public nint TargetName;
            public nint Comment;
            public long LastWritten;
            public int CredentialBlobSize;
            public nint CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public nint Attributes;
            public nint TargetAlias;
            public nint UserName;
        }

        [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CredWrite(in Credential credential, int flags);

        [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CredRead(string target, int type, int flags, out nint credential);

        [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CredDelete(string target, int type, int flags);

        [LibraryImport("advapi32.dll")]
        private static partial void CredFree(nint buffer);
    }

    /// <summary>The Secret Service through libsecret's calls that take their attributes as a table (its others are variadic,
    /// which P/Invoke cannot pass on arm64), under a schema of a service and an account.</summary>
    private static partial class LinuxSecrets
    {
        private const string Secret = "libsecret-1.so.0";
        private const string Glib = "libglib-2.0.so.0";

        private static readonly Lazy<bool> Loads = new(() => NativeLibrary.TryLoad(Secret, out _) && NativeLibrary.TryLoad(Glib, out _));

        /// <summary>Whether libsecret is there to ask; a session with no Secret Service running refuses each call.</summary>
        public static bool Present => Loads.Value;

        public static bool Store(string service, string account, string secret)
        {
            using Schema schema = new();
            nint attributes = Attributes(service, account);
            try
            {
                bool stored = secret_password_storev_sync(schema.Handle, attributes, null, $"Passenger: {service}", secret, 0, out nint error);
                return Checked(error) && stored;
            }
            finally
            {
                g_hash_table_unref(attributes);
            }
        }

        public static string? Read(string service, string account)
        {
            using Schema schema = new();
            nint attributes = Attributes(service, account);
            try
            {
                nint found = secret_password_lookupv_sync(schema.Handle, attributes, 0, out nint error);
                Checked(error);
                if (found == 0)
                {
                    return null;
                }
                string? read = Marshal.PtrToStringUTF8(found);
                secret_password_free(found);
                return read;
            }
            finally
            {
                g_hash_table_unref(attributes);
            }
        }

        public static void Forget(string service, string account)
        {
            using Schema schema = new();
            nint attributes = Attributes(service, account);
            try
            {
                secret_password_clearv_sync(schema.Handle, attributes, 0, out nint error);
                Checked(error);
            }
            finally
            {
                g_hash_table_unref(attributes);
            }
        }

        /// <summary>A table of the two attributes, its keys and values copies glib frees with it.</summary>
        private static nint Attributes(string service, string account)
        {
            nint glib = NativeLibrary.Load(Glib);
            nint table = g_hash_table_new_full(NativeLibrary.GetExport(glib, "g_str_hash"), NativeLibrary.GetExport(glib, "g_str_equal"),
                NativeLibrary.GetExport(glib, "g_free"), NativeLibrary.GetExport(glib, "g_free"));
            g_hash_table_insert(table, g_strdup("service"), g_strdup(service));
            g_hash_table_insert(table, g_strdup("account"), g_strdup(account));
            return table;
        }

        private static bool Checked(nint error)
        {
            if (error == 0)
            {
                return true;
            }
            string message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "the Secret Service failed";
            g_error_free(error);
            throw new ExternalException(message);
        }

        /// <summary>SecretSchema as libsecret lays it out: its name, flags, then 32 attributes of a name and a type.</summary>
        private sealed class Schema : IDisposable
        {
            private readonly nint name = Marshal.StringToCoTaskMemUTF8("net.passenger-ide.Credential");
            private readonly nint service = Marshal.StringToCoTaskMemUTF8("service");
            private readonly nint account = Marshal.StringToCoTaskMemUTF8("account");

            public Schema()
            {
                int size = IntPtr.Size * 2 + 32 * IntPtr.Size * 2 + IntPtr.Size * 8;
                Handle = Marshal.AllocHGlobal(size);
                Marshal.Copy(new byte[size], 0, Handle, size);
                Marshal.WriteIntPtr(Handle, 0, name);
                Marshal.WriteIntPtr(Handle, IntPtr.Size * 2, service);
                Marshal.WriteIntPtr(Handle, IntPtr.Size * 4, account);
            }

            public nint Handle { get; }

            public void Dispose()
            {
                Marshal.FreeHGlobal(Handle);
                Marshal.FreeCoTaskMem(name);
                Marshal.FreeCoTaskMem(service);
                Marshal.FreeCoTaskMem(account);
            }
        }

        [LibraryImport(Secret, StringMarshalling = StringMarshalling.Utf8)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool secret_password_storev_sync(nint schema, nint attributes, string? collection, string label, string password,
            nint cancellable, out nint error);

        [LibraryImport(Secret)]
        private static partial nint secret_password_lookupv_sync(nint schema, nint attributes, nint cancellable, out nint error);

        [LibraryImport(Secret)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool secret_password_clearv_sync(nint schema, nint attributes, nint cancellable, out nint error);

        [LibraryImport(Secret)]
        private static partial void secret_password_free(nint password);

        [LibraryImport(Glib)]
        private static partial nint g_hash_table_new_full(nint hash, nint equal, nint freeKey, nint freeValue);

        [LibraryImport(Glib)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool g_hash_table_insert(nint table, nint key, nint value);

        [LibraryImport(Glib)]
        private static partial void g_hash_table_unref(nint table);

        [LibraryImport(Glib, StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint g_strdup(string text);

        [LibraryImport(Glib)]
        private static partial void g_error_free(nint error);
    }
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using TelltaleKube.Core.Security;

namespace TelltaleKube.Desktop.Platform;

/// <summary>
/// Linux: master key stored via the freedesktop Secret Service (GNOME Keyring, KWallet, KeePassXC…) through
/// libsecret. Loaded dynamically so the app still runs (with a weaker fallback) where libsecret is absent.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class LibSecretKeyProtector : IKeyProtector
{
    private readonly delegate* unmanaged<nint, nint, byte*, byte*, byte*, nint, nint*, int> _store;
    private readonly delegate* unmanaged<nint, nint, nint, nint*, byte*> _lookup;
    private readonly delegate* unmanaged<nint, nint, nint, nint*, int> _clear;
    private readonly delegate* unmanaged<byte*, void> _free;
    private readonly delegate* unmanaged<nint, nint, nint> _hashNew;
    private readonly delegate* unmanaged<nint, nint, nint, int> _hashInsert;
    private readonly delegate* unmanaged<nint, void> _hashUnref;
    private readonly delegate* unmanaged<nint, void> _errorFree;
    private readonly nint _strHash;
    private readonly nint _strEqual;

    private LibSecretKeyProtector(nint secret, nint glib)
    {
        _store = (delegate* unmanaged<nint, nint, byte*, byte*, byte*, nint, nint*, int>)NativeLibrary.GetExport(secret, "secret_password_storev_sync");
        _lookup = (delegate* unmanaged<nint, nint, nint, nint*, byte*>)NativeLibrary.GetExport(secret, "secret_password_lookupv_sync");
        _clear = (delegate* unmanaged<nint, nint, nint, nint*, int>)NativeLibrary.GetExport(secret, "secret_password_clearv_sync");
        _free = (delegate* unmanaged<byte*, void>)NativeLibrary.GetExport(secret, "secret_password_free");
        _hashNew = (delegate* unmanaged<nint, nint, nint>)NativeLibrary.GetExport(glib, "g_hash_table_new");
        _hashInsert = (delegate* unmanaged<nint, nint, nint, int>)NativeLibrary.GetExport(glib, "g_hash_table_insert");
        _hashUnref = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(glib, "g_hash_table_unref");
        _errorFree = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(glib, "g_error_free");
        _strHash = NativeLibrary.GetExport(glib, "g_str_hash");
        _strEqual = NativeLibrary.GetExport(glib, "g_str_equal");
    }

    public string Description => "Secret Service (libsecret)";
    public bool IsStrong => true;

    public static LibSecretKeyProtector? TryCreate()
    {
        if (!NativeLibrary.TryLoad("libsecret-1.so.0", out var secret) || !NativeLibrary.TryLoad("libglib-2.0.so.0", out var glib)) return null;
        try
        {
            var protector = new LibSecretKeyProtector(secret, glib);
            // Probe: a lookup fails fast when no Secret Service is running on the session bus.
            protector.Lookup();
            return protector;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or CryptographicException)
        {
            return null;
        }
    }

    public Task<byte[]> GetOrCreateKeyAsync(CancellationToken ct)
    {
        if (Lookup() is { } existing) return Task.FromResult(existing);
        var key = RandomNumberGenerator.GetBytes(32);
        Store(Convert.ToBase64String(key));
        return Task.FromResult(key);
    }

    public Task DeleteKeyAsync(CancellationToken ct)
    {
        WithAttributes(attrs =>
        {
            nint error = 0;
            _clear(0, attrs, 0, &error);
            Check(error, "clear");
            return 0;
        });
        return Task.CompletedTask;
    }

    private byte[]? Lookup() => WithAttributes(attrs =>
    {
        nint error = 0;
        var result = _lookup(0, attrs, 0, &error);
        Check(error, "lookup");
        if (result is null) return null;
        try
        {
            return Convert.FromBase64String(Marshal.PtrToStringUTF8((nint)result)!);
        }
        finally
        {
            _free(result); // secret_password_free also wipes the memory
        }
    });

    private void Store(string secret)
    {
        WithAttributes(attrs =>
        {
            var label = Marshal.StringToCoTaskMemUTF8("Telltale Kube vault key");
            var collection = Marshal.StringToCoTaskMemUTF8("default");
            var password = Marshal.StringToCoTaskMemUTF8(secret);
            try
            {
                nint error = 0;
                var ok = _store(0, attrs, (byte*)collection, (byte*)label, (byte*)password, 0, &error);
                Check(error, "store");
                if (ok == 0) throw new CryptographicException("Secret Service refused to store the key.");
                return 0;
            }
            finally
            {
                new Span<byte>((void*)password, secret.Length).Clear();
                Marshal.FreeCoTaskMem(password);
                Marshal.FreeCoTaskMem(label);
                Marshal.FreeCoTaskMem(collection);
            }
        });
    }

    private T WithAttributes<T>(Func<nint, T> action)
    {
        var table = _hashNew(_strHash, _strEqual);
        var k1 = Marshal.StringToCoTaskMemUTF8("application");
        var v1 = Marshal.StringToCoTaskMemUTF8("uk.co.neillans.telltalekube");
        var k2 = Marshal.StringToCoTaskMemUTF8("purpose");
        var v2 = Marshal.StringToCoTaskMemUTF8("vault-master-key");
        try
        {
            _hashInsert(table, k1, v1);
            _hashInsert(table, k2, v2);
            return action(table);
        }
        finally
        {
            _hashUnref(table);
            Marshal.FreeCoTaskMem(k1);
            Marshal.FreeCoTaskMem(v1);
            Marshal.FreeCoTaskMem(k2);
            Marshal.FreeCoTaskMem(v2);
        }
    }

    private void Check(nint error, string op)
    {
        if (error == 0) return;
        // GError { GQuark domain; gint code; gchar *message; }
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "unknown error";
        _errorFree(error);
        throw new CryptographicException($"Secret Service {op} failed: {message}");
    }
}

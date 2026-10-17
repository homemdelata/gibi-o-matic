using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using GibiOMatic.Core.IO;

namespace GibiOMatic.Core.Secrets;

/// <summary>
/// Windows implementation: each secret is encrypted with DPAPI for the current user
/// and stored as its own file under the config folder.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(AppPaths paths) : ISecretStore
{
    private static readonly byte[] Entropy = "GibiOMatic.Secrets.v1"u8.ToArray();

    public bool IsSupported => true;

    public async Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        var file = FileFor(name);
        if (!File.Exists(file))
        {
            return null;
        }

        var encrypted = await File.ReadAllBytesAsync(file, ct);
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public Task SetAsync(string name, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return AtomicFile.WriteAllBytesAsync(FileFor(name), encrypted, ct);
    }

    public Task RemoveAsync(string name, CancellationToken ct = default)
    {
        File.Delete(FileFor(name));
        return Task.CompletedTask;
    }

    private string FileFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            throw new ArgumentException($"Invalid secret name: '{name}'", nameof(name));
        }

        return Path.Combine(paths.SecretsDirectory, name + ".bin");
    }
}

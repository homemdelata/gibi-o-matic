namespace GibiOMatic.Core.Secrets;

/// <summary>
/// Placeholder for platforms without a secret store yet.
/// TODO: libsecret implementation for Linux (SPEC §11).
/// </summary>
public sealed class UnsupportedSecretStore : ISecretStore
{
    public bool IsSupported => false;

    public Task<string?> GetAsync(string name, CancellationToken ct = default) => Task.FromResult<string?>(null);

    public Task SetAsync(string name, string value, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("Secure secret storage is not implemented on this platform yet.");

    public Task RemoveAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
}

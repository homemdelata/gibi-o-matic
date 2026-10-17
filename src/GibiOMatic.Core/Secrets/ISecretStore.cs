namespace GibiOMatic.Core.Secrets;

/// <summary>
/// Protected storage for API keys and other secrets. Secrets never go to <c>settings.json</c>
/// or to the library folder (SPEC §11).
/// </summary>
public interface ISecretStore
{
    bool IsSupported { get; }

    Task<string?> GetAsync(string name, CancellationToken ct = default);

    Task SetAsync(string name, string value, CancellationToken ct = default);

    Task RemoveAsync(string name, CancellationToken ct = default);
}

public static class SecretNames
{
    public const string ComicVineApiKey = "comicvine-api-key";
}

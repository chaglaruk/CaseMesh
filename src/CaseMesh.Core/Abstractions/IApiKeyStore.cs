namespace CaseMesh.Core.Abstractions;

public interface IApiKeyStore
{
    Task SaveAsync(string apiKey, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

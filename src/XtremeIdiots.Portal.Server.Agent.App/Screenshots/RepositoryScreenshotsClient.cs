using System.Net;

using Microsoft.Extensions.DependencyInjection;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Screenshots;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Agent.App.Screenshots;

public interface IRepositoryScreenshotsClient
{
    Task<RepositoryScreenshotUploadResult> UploadScreenshotAsync(UploadScreenshotDto request, string filePath, CancellationToken ct = default);
}

public enum RepositoryScreenshotUploadResult
{
    Success,
    PermanentFailure,
    TransientFailure
}

public sealed class RepositoryScreenshotsClient : IRepositoryScreenshotsClient
{
    private readonly IServiceScopeFactory _scopeFactory;

    public RepositoryScreenshotsClient(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<RepositoryScreenshotUploadResult> UploadScreenshotAsync(UploadScreenshotDto request, string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var scope = _scopeFactory.CreateScope();
        var repositoryApiClient = scope.ServiceProvider.GetRequiredService<IRepositoryApiClient>();
        var response = await repositoryApiClient.Screenshots.V1
            .UploadScreenshot(request, request.SourceFileName, filePath, ct)
            .ConfigureAwait(false);

        return response.StatusCode switch
        {
            HttpStatusCode.Created or HttpStatusCode.OK => RepositoryScreenshotUploadResult.Success,
            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => RepositoryScreenshotUploadResult.TransientFailure,
            >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError => RepositoryScreenshotUploadResult.PermanentFailure,
            _ => RepositoryScreenshotUploadResult.TransientFailure
        };
    }
}

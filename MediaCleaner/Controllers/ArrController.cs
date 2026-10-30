using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Net;
using MediaCleaner.Adapters;
using MediaCleaner.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Controllers;

[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Route("MediaCleaner")]
public class ArrController(ILoggerFactory loggerFactory, System.Net.Http.IHttpClientFactory httpClientFactory) : ControllerBase
{
    public class TestArrRequest
    {
        public string? App { get; set; }
        public string? Url { get; set; }
        public string? ApiKey { get; set; }
    }

    [HttpPost("Arr/Test")]
    public async Task<ActionResult> Test([FromBody] TestArrRequest request, CancellationToken cancellationToken)
    {
        if (request.App is not ("Radarr" or "Sonarr" or "Seerr"))
        {
            return BadRequest("Unknown app");
        }

        var client = new ArrSyncClient(loggerFactory.CreateLogger<ArrSyncClient>(), httpClientFactory.CreateClient(NamedClient.Default));
        var (success, message) = await client.TestConnectionAsync(
            request.App,
            new ArrIntegrationConfiguration { Url = request.Url ?? string.Empty, ApiKey = request.ApiKey ?? string.Empty },
            cancellationToken);
        return Ok(new { Success = success, Message = message });
    }
}

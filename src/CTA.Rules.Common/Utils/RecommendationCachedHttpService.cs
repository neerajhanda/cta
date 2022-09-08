using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace CTA.Rules.Common.Helpers;

public class RecommendationsCachedHttpService : CachedHttpService
{
    public RecommendationsCachedHttpService(IHttpClientFactory httpClientFactory, ILogger<RecommendationsCachedHttpService> logger) : base(httpClientFactory, logger, new CachedHttpServiceOptions("Recommendations"))
    {
    }
}

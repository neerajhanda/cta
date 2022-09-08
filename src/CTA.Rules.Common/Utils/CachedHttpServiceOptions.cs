using System;

namespace CTA.Rules.Common.Helpers;

public class CachedHttpServiceOptions
{
    public string ServiceName { get; private set; }
    public Uri BaseAddress { get; set; }
    public string SolutionId { get; set; }

    public CachedHttpServiceOptions(string serviceName)
    {
        if (String.IsNullOrWhiteSpace(serviceName))
            throw new ArgumentNullException(nameof(serviceName));
        ServiceName = serviceName;
    }
}

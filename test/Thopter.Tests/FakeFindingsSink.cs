using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Thopter.Cloud.Abstractions;

namespace Thopter.Tests;

/// <summary>
/// A configured, in-memory <see cref="IFindingsSink"/> used to prove the app's submit
/// flow works against the public contract alone - with no real connector present.
/// It records what it was handed and hands back an opaque success result.
/// </summary>
public sealed class FakeFindingsSink : IFindingsSink
{
    public string Name => "Fake";
    public bool IsConfigured { get; set; } = true;

    public List<FindingsBatch> Submitted { get; } = new();
    public string? LastLicenseKey { get; private set; }

    public Task<ActivationResult> ActivateAsync(string licenseKey, CancellationToken cancellationToken)
    {
        LastLicenseKey = licenseKey;
        return Task.FromResult(new ActivationResult { Activated = true, PlanName = "Test" });
    }

    public Task<SubmitResult> SubmitAsync(FindingsBatch batch, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return Task.FromResult(SubmitResult.Failure("not configured"));

        Submitted.Add(batch);
        return Task.FromResult(new SubmitResult
        {
            Ok = true,
            ReportUrl = "https://example.test/report/123",
            JobId = "job-123",
        });
    }
}

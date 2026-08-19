using System.Threading;
using System.Threading.Tasks;

namespace Thopter.Cloud.Abstractions
{
    /// <summary>
    /// The default sink when no paid connector is installed. It is never configured and
    /// always refuses to submit — the compile-time embodiment of "no call-home in the free tool".
    /// The open app ships with this and swaps in a real connector only when one is found.
    /// </summary>
    public sealed class NoOpFindingsSink : IFindingsSink
    {
        public string Name => "None";

        public bool IsConfigured => false;

        public Task<ActivationResult> ActivateAsync(string licenseKey, CancellationToken cancellationToken) =>
            Task.FromResult(ActivationResult.Fail("No cloud connector is installed."));

        public Task<SubmitResult> SubmitAsync(FindingsBatch batch, CancellationToken cancellationToken) =>
            Task.FromResult(SubmitResult.Failure("No cloud connector is installed; nothing was sent."));
    }
}

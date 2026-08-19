using System.Threading;
using System.Threading.Tasks;

namespace Thopter.Cloud.Abstractions
{
    /// <summary>
    /// The single seam between the open tool and any paid cloud connector. The open app
    /// depends only on this interface; the concrete implementation ships in the separate,
    /// proprietary connector process. The dependency arrow only ever points private → public.
    ///
    /// Contract guarantees that keep the wall intact:
    ///  * The request type (<see cref="FindingsBatch"/>) is commodity data only.
    ///  * The response type (<see cref="SubmitResult"/>) is deliberately opaque — no
    ///    monitoring schema may leak into this assembly.
    ///  * A sink must refuse to send when it is not configured (no silent call-home).
    /// </summary>
    public interface IFindingsSink
    {
        /// <summary>Display name of the connector (e.g. "MentatNOC").</summary>
        string Name { get; }

        /// <summary>
        /// True only once a real endpoint/license is present. The open app must refuse
        /// to submit while this is false — that is the no-call-home guarantee.
        /// </summary>
        bool IsConfigured { get; }

        /// <summary>Activate the connector with a license key obtained by the user out-of-band.</summary>
        Task<ActivationResult> ActivateAsync(string licenseKey, CancellationToken cancellationToken);

        /// <summary>
        /// Submit a batch of findings. Fires only on an explicit user action, never automatically.
        /// The sink ships the JSON and computes nothing locally.
        /// </summary>
        Task<SubmitResult> SubmitAsync(FindingsBatch batch, CancellationToken cancellationToken);
    }

    /// <summary>Result of an activation attempt. Intentionally minimal.</summary>
    public sealed class ActivationResult
    {
        public bool Activated { get; set; }
        public string? PlanName { get; set; }
        public string? Message { get; set; }

        public static ActivationResult Fail(string message) =>
            new ActivationResult { Activated = false, Message = message };
    }

    /// <summary>
    /// Opaque result of a submit. Carries only what the app needs to hand the user a link —
    /// no report structure, no monitoring data. This opacity is a wall requirement.
    /// </summary>
    public sealed class SubmitResult
    {
        public bool Ok { get; set; }

        /// <summary>URL to open in the browser to view the server-rendered report.</summary>
        public string? ReportUrl { get; set; }

        public string? JobId { get; set; }
        public string? Message { get; set; }

        public static SubmitResult Failure(string message) =>
            new SubmitResult { Ok = false, Message = message };
    }
}

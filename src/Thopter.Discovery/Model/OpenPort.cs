namespace Thopter.Discovery.Model;

/// <summary>
/// An open TCP port and the light, unauthenticated banner grabbed from it.
/// Populated by the port-scan stage (added in the protocol layer, step 2).
/// </summary>
public sealed class OpenPort
{
    public required int Port { get; init; }

    /// <summary>Best-guess service label, e.g. "http", "https", "rtsp". Never a login.</summary>
    public string? Service { get; init; }

    /// <summary>HTTP <c>Server</c> header, RTSP OPTIONS server, or TLS cert CN - no credentials, ever.</summary>
    public string? Banner { get; init; }

    public override string ToString() =>
        Service is null ? Port.ToString() : $"{Port}/{Service}";
}

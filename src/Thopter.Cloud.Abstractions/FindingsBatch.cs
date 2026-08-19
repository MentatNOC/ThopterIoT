using System.Collections.Generic;

namespace Thopter.Cloud.Abstractions
{
    /// <summary>
    /// The commodity request payload sent to a cloud sink. Everything here is information
    /// a standard, unauthenticated LAN scan already produced — no monitoring semantics.
    /// </summary>
    public sealed class FindingsBatch
    {
        /// <summary>Frozen schema id. The whole open-core contract keys off this string.</summary>
        public string SchemaVersion { get; set; } = "thopter.findings/v1";

        /// <summary>Optional human label for the scanned site (user-entered).</summary>
        public string? SiteLabel { get; set; }

        /// <summary>UTC timestamp the scan finished, ISO-8601 (set by the caller).</summary>
        public string? ScannedAtUtc { get; set; }

        public List<DeviceFinding> Devices { get; set; } = new List<DeviceFinding>();
    }

    /// <summary>
    /// One discovered device, reduced to commodity, non-invasive facts. Protocol presence
    /// is expressed as booleans only — never protocol payloads, media, or credentials.
    /// </summary>
    public sealed class DeviceFinding
    {
        public string? IpAddress { get; set; }

        /// <summary>MAC in AA:BB:CC:DD:EE:FF form (or null if unresolved).</summary>
        public string? MacAddress { get; set; }

        /// <summary>IEEE OUI vendor for the MAC (public dataset).</summary>
        public string? Vendor { get; set; }

        /// <summary>One standard model string, if learned from a published, unauthenticated response.</summary>
        public string? Model { get; set; }

        /// <summary>One standard firmware string, if advertised unauthenticated.</summary>
        public string? Firmware { get; set; }

        public string? Hostname { get; set; }

        // Protocol *presence* only — booleans, never the exchanged data.
        public bool HasOnvif { get; set; }
        public bool HasSsdp { get; set; }
        public bool HasMdns { get; set; }
        public bool HasRtsp { get; set; }

        public List<int> OpenPorts { get; set; } = new List<int>();

        /// <summary>User-assigned labels (e.g. "front door", "lobby NVR").</summary>
        public List<string> Labels { get; set; } = new List<string>();
    }
}

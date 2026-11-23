using System;

namespace DuckovCoop.Client
{
    /// <summary>
    /// Wire/contract versions (ADR-007). RUNTIME reads these, it must never hardcode a
    /// version string: <see cref="ClientVersion"/> mirrors <c>coop::wire::kClientVersion</c>.
    /// </summary>
    public static class Protocol
    {
        /// <summary>Frame layout version carried in every header (N2 keeps it at 1).</summary>
        public const byte WireVersion = Header.ProtocolVersion;

        /// <summary>Contract-level client version sent in <c>CLogin.clientVersion</c>.</summary>
        public const string ClientVersion = "2.18.0";

        /// <summary>What the Unity mod reports in <c>CLogin.clientVersion</c>.</summary>
        public static string ModClientVersion => "mod/" + ClientVersion;

        /// <summary>What the protocol bot/tests report in <c>CLogin.clientVersion</c>.</summary>
        public static string BotClientVersion => "bot/" + ClientVersion;

        public enum ClientVersionStatus
        {
            Ok,
            Older,
            Newer,
            Malformed,
        }

        /// <summary>
        /// Parses <c>&lt;product&gt;/&lt;semver&gt;</c>: product must be <c>mod</c> or
        /// <c>bot</c>, semver exactly <c>x.y.z</c>. Mirrors <c>coop::wire::ParseClientVersion</c>.
        /// </summary>
        public static bool TryParseClientVersion(string value, out string product, out string semver)
        {
            product = string.Empty;
            semver = string.Empty;

            if (string.IsNullOrEmpty(value) || value.Length > 32)
                return false;

            var slash = value.IndexOf('/');
            if (slash < 0 || value.IndexOf('/', slash + 1) >= 0)
                return false;

            var candidateProduct = value.Substring(0, slash);
            if (candidateProduct != "mod" && candidateProduct != "bot")
                return false;

            var candidateSemver = value.Substring(slash + 1);
            if (!TryParseSemver(candidateSemver, out _))
                return false;

            product = candidateProduct;
            semver = candidateSemver;
            return true;
        }

        /// <summary>
        /// Strict comparison against <paramref name="expected"/> (default:
        /// <see cref="ClientVersion"/>). Older/newer are both mismatches, direction is only
        /// used for the server log.
        /// </summary>
        public static ClientVersionStatus ClassifyClientVersion(string value, string expected = ClientVersion)
        {
            if (!TryParseClientVersion(value, out _, out var semver))
                return ClientVersionStatus.Malformed;
            if (!TryParseSemver(semver, out var got) || !TryParseSemver(expected, out var want))
                return ClientVersionStatus.Malformed;

            for (var i = 0; i < 3; i++)
            {
                if (got[i] == want[i])
                    continue;
                return got[i] < want[i] ? ClientVersionStatus.Older : ClientVersionStatus.Newer;
            }

            return ClientVersionStatus.Ok;
        }

        /// <summary><c>ok</c> / <c>older</c> / <c>newer</c> / <c>malformed</c>, for logs.</summary>
        public static string ClientVersionStatusText(ClientVersionStatus status)
        {
            switch (status)
            {
                case ClientVersionStatus.Ok: return "ok";
                case ClientVersionStatus.Older: return "older";
                case ClientVersionStatus.Newer: return "newer";
                default: return "malformed";
            }
        }

        private static bool TryParseSemver(string value, out int[] parts)
        {
            parts = null;
            if (string.IsNullOrEmpty(value) || value.Length > 32)
                return false;

            var segments = value.Split('.');
            if (segments.Length != 3)
                return false;

            var parsed = new int[3];
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (segment.Length == 0 || segment.Length > 5)
                    return false;

                var number = 0;
                foreach (var c in segment)
                {
                    if (c < '0' || c > '9')
                        return false;
                    number = number * 10 + (c - '0');
                }
                parsed[i] = number;
            }

            parts = parsed;
            return true;
        }
    }

    /// <summary>
    /// Application-level error codes shared with <c>server/src/protocol/Opcodes.h</c>.
    /// </summary>
    public enum ErrorCode : byte
    {
        Ok = 0,
        InvalidArgument = 1,
        Unauthorized = 2,
        VersionMismatch = 3,
        RoomFull = 4,
        NotInRaid = 5,
        Conflict = 6,
        IdempotentReplay = 7,
        ServerBusy = 8,

        /// <summary>A1: the account logged in elsewhere, this session was fenced out.</summary>
        SessionReplaced = 9,

        /// <summary>Content tables (monsters/spawn/scenes/items) differ from the server.</summary>
        ContentMismatch = 10,
    }
}


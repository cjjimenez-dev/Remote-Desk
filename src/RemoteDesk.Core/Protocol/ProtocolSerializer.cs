using MessagePack;
using MessagePack.Resolvers;

namespace RemoteDesk.Core.Protocol;

public static class ProtocolSerializer
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);

    public static byte[] Serialize<T>(T value)
    {
        return MessagePackSerializer.Serialize(value, Options);
    }

    public static T Deserialize<T>(ReadOnlyMemory<byte> memory)
    {
        return MessagePackSerializer.Deserialize<T>(memory, Options);
    }

    public static HandshakeResponse Negotiate(
        HandshakeRequest request,
        int hostCurrentVersion,
        string sessionId,
        HashSet<string> hostSupportedCapabilities,
        Models.SessionPermissions initialPermissions)
    {
        if (hostCurrentVersion < request.MinProtocolVersion || hostCurrentVersion > request.MaxProtocolVersion)
        {
            return new HandshakeResponse
            {
                Accepted = false,
                RejectionReason = $"Incompatible protocol version. Host version: {hostCurrentVersion}, Client supported: {request.MinProtocolVersion}-{request.MaxProtocolVersion}."
            };
        }

        var agreedCaps = new HashSet<string>(request.Capabilities);
        agreedCaps.IntersectWith(hostSupportedCapabilities);

        return new HandshakeResponse
        {
            Accepted = true,
            AgreedProtocolVersion = hostCurrentVersion,
            SessionId = sessionId,
            AgreedCapabilities = agreedCaps,
            GrantedPermissions = initialPermissions
        };
    }
}

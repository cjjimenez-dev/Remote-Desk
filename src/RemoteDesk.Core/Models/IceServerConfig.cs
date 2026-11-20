using MessagePack;

namespace RemoteDesk.Core.Models;

[MessagePackObject]
public sealed class IceServerConfig
{
    [Key(0)]
    public List<string> Urls { get; set; } = [];

    [Key(1)]
    public string? Username { get; set; }

    [Key(2)]
    public string? Credential { get; set; }

    public IceServerConfig()
    {
    }

    public IceServerConfig(string url, string? username = null, string? credential = null)
    {
        Urls = [url];
        Username = username;
        Credential = credential;
    }

    public IceServerConfig(IEnumerable<string> urls, string? username = null, string? credential = null)
    {
        Urls = new List<string>(urls);
        Username = username;
        Credential = credential;
    }
}

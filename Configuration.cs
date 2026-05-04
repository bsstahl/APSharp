namespace Fedi;

public class AppConfiguration
{
    public string Database { get; set; } = ":memory:";
    public string Hostname { get; set; } = "localhost";
    public int Port { get; set; } = 65380;
    public string Key { get; set; } = "localhost.key";
    public string Cert { get; set; } = "localhost.crt";
    public string LogLevel { get; set; } = "Information";
    public string SessionSecret { get; set; } = "insecure-session-secret";
    public string? InviteCode { get; set; }
    public string? BlockList { get; set; }
    public string? Origin { get; set; }
    public string? Name { get; set; }
    public string? UploadDir { get; set; }
    public int SqliteCache { get; set; } = 16384;
}

public static class Constants
{
    public const string AsContext = "https://www.w3.org/ns/activitystreams";
    public const string SecContext = "https://w3id.org/security/v1";
    public const string BlockedContext = "https://purl.archive.org/socialweb/blocked";
    public const string PendingContext = "https://purl.archive.org/socialweb/pending";
    public const string WebfingerContext = "https://purl.archive.org/socialweb/webfinger";
    public const string MiscellanyContext = "https://purl.archive.org/socialweb/miscellany";
    public const string OAuthContext = "https://purl.archive.org/socialweb/oauth/1.1";

    public static readonly string[] Context =
    [
        AsContext, SecContext, BlockedContext, PendingContext,
        WebfingerContext, MiscellanyContext, OAuthContext
    ];

    public const string LdMediaType = "application/ld+json";
    public const string ActivityMediaType = "application/activity+json";
    public const string JsonMediaType = "application/json";
    public static readonly string AcceptHeader = $"{LdMediaType};q=1.0, {ActivityMediaType};q=0.9, {JsonMediaType};q=0.3";
    public const string Public = "https://www.w3.org/ns/activitystreams#Public";
    public static readonly string[] Publics = [Public, "as:Public", "Public"];
    public static readonly object PublicObj = new { id = Public, nameMap = new Dictionary<string, string> { ["en"] = "Public" }, type = "Collection" };
    public const int MaxPageSize = 20;
    public static readonly string[] GrantTypesSupported = ["authorization_code", "refresh_token"];
    public static readonly string[] ScopesSupported = ["read", "write"];
    public static readonly string[] ResponseTypesSupported = ["code"];
    public static readonly string[] TokenEndpointAuthMethodsSupported = ["none"];
    public const int MaximumTimeSkew = 5 * 60 * 1000; // 5 minutes
    public const int MaintenanceInterval = 6 * 60 * 60 * 1000; // 6 hours
    public const int DefaultExpires = 24 * 60 * 60 * 1000; // one day
    public const int FailureExpires = 60 * 60 * 1000; // one hour
}

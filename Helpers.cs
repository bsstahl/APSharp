using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fedi;

public static class Helpers
{
    public static bool IsString(object? value) =>
        value is string || value is System.String;

    public static bool IsPublic(string? id) =>
        id is not null && Constants.Publics.Contains(id);

    public static string Base64UrlEncode(byte[] input) =>
        Convert.ToBase64String(input)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    public static T DeepCopy<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    public static async Task<string?> ToId(object? value)
    {
        if (value == null) return null;
        if (value is string s) return s;
        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String) return je.GetString();
            if (je.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                return idProp.GetString();
        }
        if (value is JsonDocument jd)
        {
            if (jd.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                return idProp.GetString();
        }
        if (value is ActivityObject ao) return await ao.Id();
        if (value is Dictionary<string, object?> dict && dict.TryGetValue("id", out var dictId))
            return dictId?.ToString();
        throw new Exception($"Cannot convert {value} to id");
    }

    public static List<T> ToArray<T>(T? value)
    {
        if (value == null) return new List<T>();
        if (value is List<T> list) return list;
        if (value is T[] arr) return arr.ToList();
        return new List<T> { value };
    }

    public static List<object?> ToArray(object? value)
    {
        if (value == null) return new List<object?>();
        if (value is List<object?> list) return list;
        if (value is object?[] arr) return arr.ToList();
        return new List<object?> { value };
    }

    public static List<object?> ToObjectArray(JsonElement? element)
    {
        if (element == null) return new List<object?>();
        var el = element.Value;
        if (el.ValueKind == JsonValueKind.Array)
            return el.EnumerateArray().Select(e => (object?)e).ToList();
        return new List<object?> { el };
    }

    public static string MakeUrl(string relative, string origin)
    {
        if (relative.Length > 0 && relative[0] == '/')
            relative = relative[1..];
        return $"{origin}/{relative}";
    }

    public static string DigestBody(string body)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(body));
        return $"sha-256={Convert.ToBase64String(hash)}";
    }

    public static bool EqualDigests(string digest1, string digest2)
    {
        var parts1 = digest1.Split('=', 2);
        var parts2 = digest2.Split('=', 2);
        if (parts1.Length != 2 || parts2.Length != 2) return false;
        if (!string.Equals(parts1[0], parts2[0], StringComparison.OrdinalIgnoreCase)) return false;
        return parts1[1] == parts2[1];
    }

    public static string ToSpki(string pem)
    {
        if (pem.StartsWith("-----BEGIN RSA PUBLIC KEY-----"))
        {
            var key = RSA.Create();
            key.ImportFromPem(pem.ToCharArray());
            return key.ExportRSAPublicKeyPem();
        }
        return pem;
    }

    public static string ToPkcs8(string pem)
    {
        if (pem.StartsWith("-----BEGIN RSA PRIVATE KEY-----"))
        {
            var key = RSA.Create();
            key.ImportFromPem(pem.ToCharArray());
            return key.ExportPkcs8PrivateKeyPem();
        }
        return pem;
    }

    public static Dictionary<string, object?> StandardEndpoints(string origin)
    {
        return new Dictionary<string, object?>
        {
            ["proxyUrl"] = MakeUrl("endpoint/proxyUrl", origin),
            ["oauthAuthorizationEndpoint"] = MakeUrl("endpoint/oauth/authorize", origin),
            ["oauthTokenEndpoint"] = MakeUrl("endpoint/oauth/token", origin),
            ["uploadMedia"] = MakeUrl("endpoint/upload", origin)
        };
    }

    public static bool DomainIsBlocked(string? url, List<string> blockedDomains)
    {
        if (url == null) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return blockedDomains.Contains(uri.Host);
    }

    public static bool NotIncluded(string[] arr1, string[] arr2) =>
        arr1.Any(item => !arr2.Contains(item));
}

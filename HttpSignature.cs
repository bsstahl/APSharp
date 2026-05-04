using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fedi;

public class HttpSignature
{
    public string KeyId { get; }
    public string Headers { get; }
    public string Signature { get; }
    public string Algorithm { get; }

    private readonly string? _privateKey;
    private readonly string? _method;
    private readonly Uri? _url;
    private readonly string? _date;
    private readonly string? _digest;

    public string Header { get; }

    public HttpSignature(string keyId, string privateKey, string method, string url, string date, string? digest = null)
    {
        KeyId = keyId;
        _privateKey = privateKey;
        _method = method;
        _url = new Uri(url);
        _date = date;
        _digest = digest;
        var sig = Sign(SignableData());
        Signature = sig;
        Algorithm = "rsa-sha256";
        Headers = $"(request-target) host date" + (_digest != null ? " digest" : "");
        Header = $"keyId=\"{KeyId}\",headers=\"{Headers}\",signature=\"{sig.Replace("\"", "\\\"")}\",algorithm=\"rsa-sha256\"";
    }

    public HttpSignature(string sigHeader)
    {
        var parts = new Dictionary<string, string>();
        var matches = Regex.Matches(sigHeader, @"\s*(\w+)\s*=\s*""(.*?)""\s*");
        foreach (Match m in matches)
        {
            parts[m.Groups[1].Value] = m.Groups[2].Value.Replace("\\\"", "\"");
        }
        if (!parts.ContainsKey("keyId") || !parts.ContainsKey("headers") || !parts.ContainsKey("signature") || !parts.ContainsKey("algorithm"))
        {
            throw new Exception("Invalid signature header");
        }
        if (parts["algorithm"] != "rsa-sha256")
        {
            throw new Exception($"Unsupported algorithm: {parts["algorithm"]}");
        }
        KeyId = parts["keyId"];
        Headers = parts["headers"];
        Signature = parts["signature"];
        Algorithm = parts["algorithm"];
        Header = sigHeader;
    }

    private string SignableData()
    {
        var target = !string.IsNullOrEmpty(_url!.Query) ? $"{_url.PathAndQuery}" : $"{_url.AbsolutePath}";
        var sb = new StringBuilder();
        sb.AppendLine($"(request-target): {_method!.ToLower()} {target}");
        sb.AppendLine($"host: {_url.Host}");
        sb.Append($"date: {_date}");
        if (_digest != null)
        {
            sb.Append($"\ndigest: {_digest}");
        }
        return sb.ToString();
    }

    private string Sign(string data)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_privateKey!.ToCharArray());
        var bytes = Encoding.UTF8.GetBytes(data);
        var sig = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(sig);
    }

    public async Task<ActivityObject?> ValidateAsync(HttpContext req, Database db, bool cache = true)
    {
        var lines = new List<string>();
        foreach (var name in Headers.Split(' '))
        {
            if (name == "(request-target)")
            {
                lines.Add($"(request-target): {req.Request.Method.ToLower()} {req.Request.Path}{req.Request.QueryString}");
            }
            else
            {
                var value = req.Request.Headers[name].FirstOrDefault();
                lines.Add($"{name.ToLower()}: {value?.Trim()}");
            }
        }
        var data = string.Join("\n", lines);
        var url = new Uri(KeyId);
        var fragment = !string.IsNullOrEmpty(url.Fragment) ? url.Fragment.TrimStart('#') : null;
        url = new Uri(url.GetLeftPart(UriPartial.Path));

        var options = new FetchOptions();
        if (cache)
        {
            // Use request cache if available
            if (req.Items.TryGetValue("aoCache", out var cacheObj) && cacheObj is Dictionary<string, ActivityObject> aoCache)
            {
                options.Cache = aoCache;
            }
            if (req.Items.TryGetValue("counter", out var counterObj) && counterObj is Counter counter)
            {
                options.Counter = counter;
            }
        }
        else
        {
            options.SkipRemoteCache = true;
            if (req.Items.TryGetValue("counter", out var counterObj) && counterObj is Counter counter)
            {
                options.Counter = counter;
            }
        }

        var ao = await ActivityObject.Get(url.ToString(), options);
        if (ao == null)
        {
            AppState.Logger?.LogWarning("Could not retrieve key id {KeyId}", url.ToString());
            return null;
        }

        ActivityObject? publicKey = null;
        var aoJson = await ao.Json();

        if (fragment == null)
        {
            publicKey = ao;
        }
        else if (aoJson.ContainsKey(fragment))
        {
            publicKey = await ActivityObject.Get(await Helpers.ToId(aoJson[fragment]), options);
        }
        else if (fragment == "main-key" && aoJson.ContainsKey("publicKey"))
        {
            publicKey = await ActivityObject.Get(await Helpers.ToId(aoJson["publicKey"]), options);
        }
        else
        {
            return null;
        }

        if (publicKey == null) return null;
        var pkJson = await publicKey.Json();
        if (pkJson == null || !pkJson.ContainsKey("owner") || !pkJson.ContainsKey("publicKeyPem"))
            return null;

        var ownerId = await Helpers.ToId(pkJson["owner"]);
        var publicKeyPem = pkJson["publicKeyPem"]?.GetValue<string>();
        if (string.IsNullOrEmpty(publicKeyPem)) return null;

        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem.ToCharArray());
        var bytes = Encoding.UTF8.GetBytes(data);
        var sigBytes = Convert.FromBase64String(Signature);
        var verified = rsa.VerifyData(bytes, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var endTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (req.Items.TryGetValue("counter", out var cobj) && cobj is Counter ctr)
        {
            ctr.Increment("crypto", "count");
            ctr.Add("crypto", "dur", endTime - startTime);
        }

        if (verified)
        {
            var owner = await ActivityObject.Get(ownerId, options);
            if (owner != null)
            {
                if (options.Cache != null)
                {
                    options.Cache[await owner.Id() ?? ownerId ?? ""] = owner;
                    options.Cache[await publicKey.Id() ?? url.ToString()] = publicKey;
                }
                return owner;
            }
        }
        return null;
    }
}

namespace Fedi;

public class ServerModel
{
    private static ServerModel? _singleton;
    private static readonly object _lock = new();

    public string Origin { get; }
    public string PublicKey { get; }
    private readonly string _privateKey;

    public ServerModel(string origin, string publicKey, string privateKey)
    {
        Origin = origin;
        PublicKey = publicKey;
        _privateKey = privateKey;
    }

    public string KeyId() => $"{Origin}/key";
    public string Id() => Origin;
    public string PrivateKey() => _privateKey;

    public object ToJson(string? name = null)
    {
        return new
        {
            @context = Constants.Context,
            id = Origin,
            type = "Service",
            name = name ?? "One Page Pub",
            preferredUsername = new Uri(Origin).Host,
            publicKey = new Dictionary<string, object>
            {
                ["type"] = "CryptographicKey",
                ["id"] = KeyId(),
                ["owner"] = Origin,
                ["publicKeyPem"] = PublicKey
            }
        };
    }

    public object GetKeyJson()
    {
        return new
        {
            @context = Constants.Context,
            type = "CryptographicKey",
            id = KeyId(),
            owner = Origin,
            publicKeyPem = PublicKey
        };
    }

    public static async Task<ServerModel?> GetAsync(Database db, Counter? counter = null)
    {
        if (_singleton != null) return _singleton;
        lock (_lock)
        {
            if (_singleton != null) return _singleton;
        }
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var row = await db.GetRowAsync("SELECT * FROM server WHERE origin = ?", string.Empty);
        var end = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        counter?.Add("db", "dur", end - start);
        if (row == null) return null;
        var origin = row["origin"] as string ?? row["origin"]?.ToString() ?? "";
        var pubKey = row["publicKey"] as string ?? row["publicKey"]?.ToString() ?? "";
        var privKey = row["privateKey"] as string ?? row["privateKey"]?.ToString() ?? "";
        _singleton = new ServerModel(origin, pubKey, privKey);
        return _singleton;
    }

    public static async Task EnsureKeyAsync(Database db, string origin)
    {
        var row = await db.GetRowAsync("SELECT * FROM server WHERE origin = ?", origin);
        if (row == null)
        {
            var (publicKey, privateKey) = await NewKeyPairAsync();
            await db.RunAsync("INSERT INTO server (origin, privateKey, publicKey) VALUES (?, ?, ?) ON CONFLICT DO NOTHING", origin, privateKey, publicKey);
        }
        else if (row["privateKey"] == null)
        {
            var (publicKey, privateKey) = await NewKeyPairAsync();
            await db.RunAsync("UPDATE server SET privateKey = ?, publicKey = ? WHERE origin = ?", privateKey, publicKey, origin);
        }
    }

    public static async Task<(string publicKey, string privateKey)> NewKeyPairAsync()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var privateKey = rsa.ExportRSAPrivateKeyPem();
        var publicKey = rsa.ExportRSAPublicKeyPem();
        return (publicKey, privateKey);
    }
}

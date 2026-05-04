using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fedi;

public class ActivityObject
{
    private string? _id;
    protected JsonObject? _json;
    private ActivityObject? _owner;
    private List<ActivityObject>? _addressees;
    private bool _complete = false;
    private readonly object? _subject;
    protected readonly Dictionary<string, ActivityObject>? _cache;
    protected readonly Counter? _counter;
    private readonly bool _skipRemoteCache;

    protected static readonly string[] IdProps = [
        "actor", "alsoKnownAs", "attachment", "attributedTo", "anyOf", "audience",
        "blocked", "cc", "context", "current", "describes", "first", "following",
        "followers", "generator", "href", "icon", "image", "inbox", "inReplyTo",
        "instrument", "last", "liked", "likes", "location", "next", "object", "oneOf",
        "origin", "outbox", "partOf", "pendingFollowers", "pendingFollowing", "prev",
        "preview", "publicKey", "relationship", "replies", "result", "shares", "subject",
        "tag", "target", "to"
    ];

    protected static readonly string[] LinkProps = ["url"];
    protected static readonly string[] ArrayProps = ["items", "orderedItems"];

    public ActivityObject(object? data, FetchOptions? options = null)
    {
        options ??= new FetchOptions();
        _subject = options.Subject;
        _cache = options.Cache;
        _counter = options.Counter;
        _skipRemoteCache = options.SkipRemoteCache;

        if (data == null)
            throw new ArgumentException("No data provided");

        if (data is string s)
        {
            _id = s;
        }
        else if (data is JsonObject jo)
        {
            _json = jo;
            _id = jo["id"]?.GetValue<string>() ?? jo["@id"]?.GetValue<string>();
        }
        else if (data is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            _json = JsonObject.Create(je)!;
            _id = _json["id"]?.GetValue<string>() ?? _json["@id"]?.GetValue<string>();
        }
        else if (data is ActivityObject)
        {
            throw new ArgumentException("ActivityObject constructor with ActivityObject argument");
        }
        else
        {
            throw new ArgumentException($"Unrecognized activity object: {data}");
        }
    }

    private FetchOptions Options() => new()
    {
        Subject = _subject,
        Cache = _cache,
        Counter = _counter
    };

    public static async Task<string> MakeId(string type)
    {
        var best = BestType(type);
        var prefix = best != null ? best.ToLowerInvariant() : "object";
        return $"{AppState.Origin}/{prefix}/{Fedi.Nanoid.Generate()}";
    }

    public async Task<JsonNode?> Prop(string name)
    {
        if (_json != null && _json.ContainsKey(name))
            return _json[name];
        if (_id != null && (!_json?.ContainsKey(name) ?? true) && !_complete)
        {
            await EnsureCompleteAsync();
            return _json?[name];
        }
        return null;
    }

    public async Task<(string? name, JsonNode? value)> FirstOf(string[] names)
    {
        if (_json != null)
        {
            foreach (var name in names)
            {
                if (_json.ContainsKey(name))
                    return (name, _json[name]);
            }
        }
        if (_id != null && !_complete)
        {
            await EnsureCompleteAsync();
            foreach (var name in names)
            {
                if (_json?.ContainsKey(name) ?? false)
                    return (name, _json[name]);
            }
        }
        return (null, null);
    }

    public async Task SetProp(string name, JsonNode? value)
    {
        _json ??= new JsonObject();
        _json[name] = value;
        if (name == "id")
            _id = value?.GetValue<string>();
    }

    public async Task<string?> Id()
    {
        if (_id == null && _json != null)
            _id = _json["id"]?.GetValue<string>();
        return _id;
    }

    public async Task<string?> Type() => (await Prop("type"))?.GetValue<string>();

    public async Task<string?> Name(string? lang = null)
    {
        var (nameProp, nameMap, summary, summaryMap) = await Task.WhenAll(
            Prop("name"),
            Prop("nameMap"),
            Prop("summary"),
            Prop("summaryMap")
        ).ContinueWith(t =>
        {
            var r = t.Result;
            return (r[0], r[1], r[2], r[3]);
        });

        if (nameMap is JsonObject nmo && lang != null && nmo.ContainsKey(lang))
            return nmo[lang]?.GetValue<string>();
        if (nameProp != null)
            return nameProp.GetValue<string>();
        if (summaryMap is JsonObject smo && lang != null && smo.ContainsKey(lang))
            return smo[lang]?.GetValue<string>();
        if (summary != null)
            return summary.GetValue<string>();
        return null;
    }

    public async Task<bool> EnsureCompleteAsync()
    {
        if (_complete) return _json != null;
        await GetCompleteJsonAsync();
        return _json != null && _complete;
    }

    private async Task GetCompleteJsonAsync()
    {
        if (_complete) return;
        var id = await Id();
        if (id == null) return;
        if (Helpers.IsPublic(id))
        {
            _json = JsonObject.Create(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(Constants.PublicObj)))!;
            _complete = true;
            return;
        }

        var db = GetDatabase();
        if (db == null) return;

        if (IsRemoteId(id))
        {
            if (!_skipRemoteCache)
            {
                var (json, complete) = await GetFromRemoteCacheAsync(db, id, "https://www.w3.org/ns/activitystreams#Public");
                if (json != null)
                {
                    _json = json;
                    _complete = complete;
                    return;
                }
                if (_subject != null)
                {
                    var subjId = await Helpers.ToId(_subject);
                    if (subjId != null)
                    {
                        (json, complete) = await GetFromRemoteCacheAsync(db, id, subjId);
                        if (json != null)
                        {
                            _json = json;
                            _complete = complete;
                            return;
                        }
                    }
                }
            }
            await GetFromRemoteAsync(db, id);
        }
        else
        {
            await GetFromDatabaseAsync(db, id);
        }
    }

    private async Task GetFromDatabaseAsync(Database db, string id)
    {
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var row = await db.GetRowAsync("SELECT data FROM object WHERE id = ?", id);
        var end = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _counter?.Add("db", "dur", end - start);
        _counter?.Increment("db", "count");

        if (row == null)
        {
            _json = null;
            return;
        }

        var parseStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _json = JsonObject.Create(JsonSerializer.Deserialize<JsonElement>((string)row["data"]!))!;
        var parseEnd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _counter?.Add("json", "dur", parseEnd - parseStart);
        _counter?.Increment("json", "count");
        _complete = true;
    }

    private async Task<(JsonObject? json, bool complete)> GetFromRemoteCacheAsync(Database db, string dataId, string subjectId)
    {
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var row = await db.GetRowAsync(
            "SELECT data, expires, complete FROM remotecache WHERE id = ? and subject = ?",
            dataId, subjectId);
        var end = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _counter?.Add("db", "dur", end - start);
        _counter?.Increment("db", "count");

        if (row == null) return (null, false);

        var expires = Convert.ToInt64(row["expires"]!);
        if (expires > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            var parseStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var json = JsonObject.Create(JsonSerializer.Deserialize<JsonElement>((string)row["data"]!))!;
            var parseEnd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _counter?.Add("json", "dur", parseEnd - parseStart);
            _counter?.Increment("json", "count");
            return (json, Convert.ToInt32(row["complete"]!) != 0);
        }
        else
        {
            await db.RunAsync("DELETE FROM remotecache WHERE id = ? and subject = ?", dataId, subjectId);
            return (null, false);
        }
    }

    private async Task GetFromRemoteAsync(Database db, string id)
    {
        var date = DateTime.UtcNow.ToString("R");
        var headers = new Dictionary<string, string>
        {
            ["Date"] = date,
            ["Accept"] = Constants.AcceptHeader
        };

        string keyId;
        string privKey;
        if (_subject != null && await User.IsUser(_subject, db))
        {
            var user = await User.FromActorId(await Helpers.ToId(_subject) ?? "", db);
            ActivityObject subjectObj;
            if (_subject is ActivityObject ao)
                subjectObj = ao;
            else
                subjectObj = new ActivityObject(await Helpers.ToId(_subject) ?? "", Options());
            keyId = await Helpers.ToId(await subjectObj.Prop("publicKey")) ?? "";
            privKey = user?.PrivateKey ?? "";
        }
        else
        {
            var server = await ServerModel.GetAsync(db, _counter);
            keyId = server?.KeyId() ?? "";
            privKey = server?.PrivateKey() ?? "";
        }

        var u = new Uri(id);
        var baseUrl = u.GetLeftPart(UriPartial.Path) + u.Query;
        if (await FailedBefore(db, baseUrl))
        {
            AppState.Logger?.LogInformation("Skipping fetch of {BaseUrl} for {Subject}", baseUrl, await Helpers.ToId(_subject));
            _complete = false;
            return;
        }

        var signStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var signature = new HttpSignature(keyId, privKey, "GET", baseUrl, date);
        headers["Signature"] = signature.Header;
        var signEnd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _counter?.Add("crypto", "dur", signEnd - signStart);
        _counter?.Increment("crypto", "count");

        AppState.Logger?.LogDebug("fetching {Id} with key ID {KeyId}", id, keyId);

        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        HttpResponseMessage? res = null;
        try
        {
            using var client = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Get, baseUrl);
            foreach (var h in headers)
                request.Headers.TryAddWithoutValidation(h.Key, h.Value);
            res = await client.SendAsync(request);
        }
        catch (Exception ex)
        {
            AppState.Logger?.LogWarning(ex, "Error fetching {Id}", id);
            await RememberFailure(db, baseUrl, 0);
            _complete = false;
            return;
        }
        var endTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _counter?.Add("http", "dur", endTime - startTime);
        _counter?.Increment("http", "count");

        if (res.StatusCode != HttpStatusCode.OK && res.StatusCode != HttpStatusCode.Gone)
        {
            var message = await res.Content.ReadAsStringAsync();
            AppState.Logger?.LogWarning("Error fetching {Id}: {Status} {StatusText} ({Message})", id, (int)res.StatusCode, res.ReasonPhrase, message);
            await RememberFailure(db, baseUrl, (int)res.StatusCode);
            _complete = false;
            return;
        }

        var parseStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var jsonText = await res.Content.ReadAsStringAsync();
        JsonObject? json = null;
        try
        {
            json = JsonObject.Create(JsonSerializer.Deserialize<JsonElement>(jsonText));
        }
        catch { }
        var parseEnd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _counter?.Add("json", "dur", parseEnd - parseStart);
        _counter?.Increment("json", "count");

        if (json == null)
        {
            _complete = false;
            return;
        }

        var hash = !string.IsNullOrEmpty(u.Fragment) ? u.Fragment.TrimStart('#') : null;
        if (string.IsNullOrEmpty(hash))
        {
            _json = json;
        }
        else if (json.ContainsKey(hash))
        {
            var hashNode = json[hash];
            if (hashNode is JsonObject hashObj)
                _json = hashObj;
            else
                _json = JsonObject.Create(JsonSerializer.Deserialize<JsonElement>(hashNode!.ToJsonString()));
        }
        else if (hash == "main-key" && json.ContainsKey("publicKey"))
        {
            var pkNode = json["publicKey"];
            if (pkNode is JsonObject pkObj)
                _json = pkObj;
            else
                _json = JsonObject.Create(JsonSerializer.Deserialize<JsonElement>(pkNode!.ToJsonString()));
        }
        else
        {
            AppState.Logger?.LogWarning("Can't resolve fragment {Hash} in {Id}", hash, id);
            _complete = false;
            return;
        }

        _complete = true;

        if (res.StatusCode == HttpStatusCode.Gone && await Type() != "Tombstone")
        {
            AppState.Logger?.LogWarning("Object {Id} returned 410 but is not a Tombstone", id);
            _complete = false;
            return;
        }

        await Cache();
    }

    private async Task<bool> FailedBefore(Database db, string url)
    {
        var subject = (await Helpers.ToId(_subject)) ?? (await ServerModel.GetAsync(db, _counter))?.Id() ?? "";
        var row = await db.GetRowAsync("SELECT expires FROM remote_failure WHERE url = ? AND subject = ?", url, subject);
        if (row == null) return false;
        var expires = Convert.ToInt64(row["expires"]!);
        if (expires > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return true;
        await db.RunAsync("DELETE FROM remote_failure WHERE url = ? AND subject = ?", url, subject);
        return false;
    }

    private async Task RememberFailure(Database db, string url, int status)
    {
        var subject = (await Helpers.ToId(_subject)) ?? (await ServerModel.GetAsync(db, _counter))?.Id() ?? "";
        AppState.Logger?.LogInformation("Logging failure status {Status} for url {Url} with subject {Subject}", status, url, subject);
        await db.RunAsync("INSERT OR REPLACE INTO remote_failure (url, subject, status, expires) VALUES (?, ?, ?, ?)",
            url, subject, status, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Constants.FailureExpires);
    }

    public static async Task<ActivityObject?> Get(object? reference, FetchOptions? options = null)
    {
        options ??= new FetchOptions();
        var id = await Helpers.ToId(reference);
        if (id == null) return null;

        if (options.Cache != null)
        {
            if (options.Cache.TryGetValue(id, out var cached))
            {
                options.Counter?.Increment("cache", "hit");
                return cached;
            }
            options.Counter?.Increment("cache", "miss");
        }

        var obj = new ActivityObject(reference, options);
        if (await obj.EnsureCompleteAsync())
        {
            if (options.Cache != null)
                options.Cache[id] = obj;
            return obj;
        }
        return null;
    }

    public static bool IsRemoteId(string? id) => id != null && !id.StartsWith(AppState.Origin);

    public static string? GuessOwner(JsonObject json)
    {
        foreach (var prop in new[] { "attributedTo", "actor", "owner" })
        {
            if (json.ContainsKey(prop))
                return json[prop]?.GetValue<string>();
        }
        return null;
    }

    public static List<string> GuessAddressees(JsonObject json)
    {
        var addressees = new List<string>();
        foreach (var prop in new[] { "to", "cc", "bto", "bcc", "audience" })
        {
            if (json.ContainsKey(prop))
            {
                var value = json[prop];
                if (value is JsonArray arr)
                {
                    addressees.AddRange(arr.Where(e => e != null).Select(e => e!.GetValue<string>()));
                }
                else if (value != null)
                {
                    addressees.Add(value.GetValue<string>());
                }
            }
        }
        return addressees;
    }

    public async Task<JsonObject> Json()
    {
        if (_json == null)
            await GetCompleteJsonAsync();
        return _json ?? new JsonObject();
    }

    public async Task Save(string? owner = null, List<string>? addressees = null)
    {
        var db = GetDatabase();
        if (db == null) throw new InvalidOperationException("No database available");

        var data = await Compressed();
        if (owner == null)
            owner = GuessOwner(data);
        if (addressees == null)
            addressees = GuessAddressees(data);

        data["type"] = data["type"] ?? DefaultType();
        data["id"] = data["id"] ?? await MakeId(data["type"]!.GetValue<string>());
        data["updated"] = DateTime.UtcNow.ToString("o");
        data["published"] = data["published"] ?? data["updated"];

        var ownerId = owner ?? data["id"]!.GetValue<string>();
        var addresseeIds = addressees;

        var id = data["id"]!.GetValue<string>();
        await db.RunAsync("INSERT INTO object (id, owner, data) VALUES (?, ?, ?)",
            id, ownerId, JsonSerializer.Serialize(data));

        foreach (var addresseeId in addresseeIds)
        {
            await db.RunAsync("INSERT INTO addressee_2 (objectId, addresseeId) VALUES (?, ?)",
                id, addresseeId);
        }

        _id = id;
        _json = data;
    }

    public virtual string DefaultType() => "Object";

    public async Task<JsonObject> Compressed()
    {
        var json = await Json();
        var clone = JsonObject.Create(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(json)))!;

        foreach (var prop in IdProps)
        {
            if (clone.ContainsKey(prop))
            {
                var value = clone[prop];
                if (value is JsonArray arr)
                {
                    clone[prop] = new JsonArray(arr.Select(async item =>
                    {
                        var id = await Helpers.ToId(item);
                        return id != null ? JsonValue.Create(id) : item;
                    }).Select(t => t.Result).ToArray());
                }
                else if (value != null)
                {
                    var id = await Helpers.ToId(value);
                    if (id != null)
                        clone[prop] = JsonValue.Create(id);
                }
            }
        }
        return clone;
    }

    public static string? BestType(string? type)
    {
        if (type == null) return null;
        var types = type.Split(',').Select(t => t.Trim()).ToArray();
        var knownTypes = new[] {
            "Object", "Link", "Activity", "IntransitiveActivity", "Collection", "OrderedCollection",
            "CollectionPage", "OrderedCollectionPage", "Accept", "Add", "Announce", "Arrive",
            "Block", "Create", "Delete", "Dislike", "Flag", "Follow", "Ignore", "Invite",
            "Join", "Leave", "Like", "Listen", "Move", "Offer", "Question", "Reject", "Read",
            "Remove", "TentativeReject", "TentativeAccept", "Travel", "Undo", "Update", "View",
            "Application", "Group", "Organization", "Person", "Service",
            "Article", "Audio", "Document", "Event", "Image", "Note", "Page", "Place",
            "Profile", "Relationship", "Tombstone", "Video", "Mention"
        };
        foreach (var item in types)
        {
            if (knownTypes.Contains(item))
                return item;
        }
        return types.FirstOrDefault();
    }

    private static readonly string[] ActivityTypes = [
        "Accept", "Add", "Announce", "Arrive", "Block", "Create", "Delete", "Dislike", "Flag",
        "Follow", "Ignore", "Invite", "Join", "Leave", "Like", "Listen", "Move", "Offer",
        "Question", "Reject", "Read", "Remove", "TentativeReject", "TentativeAccept",
        "Travel", "Undo", "Update", "View"
    ];

    private static readonly string[] ObjectTypes = [
        "Article", "Audio", "Document", "Event", "Image", "Note", "Page", "Place",
        "Profile", "Relationship", "Tombstone", "Video"
    ];

    public static bool IsActivityType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        var types = type.Split(',').Select(t => t.Trim()).ToArray();
        return types.Any(t => t is "Activity" or "IntransitiveActivity" || ActivityTypes.Contains(t));
    }

    public static bool IsObjectType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        var types = type.Split(',').Select(t => t.Trim()).ToArray();
        return types.Any(t => t is "Object" or "Link" or "Collection" or "CollectionPage"
            or "OrderedCollection" or "OrderedCollectionPage" || ObjectTypes.Contains(t));
    }

    public async Task<bool> IsCollection() =>
        new[] { "Collection", "OrderedCollection" }.Contains(await Type());

    public async Task<bool> IsCollectionPage() =>
        new[] { "CollectionPage", "OrderedCollectionPage" }.Contains(await Type());

    public async Task<bool> HasProp(string prop)
    {
        var json = await Json();
        return json?.ContainsKey(prop) ?? false;
    }

    public async Task<bool> IsLinkType() =>
        new[] { "Link", "Hashtag", "Mention" }.Contains(await Type());

    public async Task<Dictionary<string, object?>> Brief()
    {
        var brief = await IsLinkType()
            ? new Dictionary<string, object?> { ["href"] = (await Prop("href"))?.GetValue<string>(), ["type"] = await Type() }
            : new Dictionary<string, object?> { ["id"] = await Id(), ["type"] = await Type(), ["icon"] = (await Prop("icon"))?.GetValue<string>() };

        var (propName, propValue) = await FirstOf(["nameMap", "name", "summaryMap", "summary"]);
        if (propName != null && propValue != null)
            brief[propName] = propValue.GetValue<object>();

        var type = await Type();
        switch (type)
        {
            case "Key":
            case "PublicKey":
            case "CryptographicKey":
                brief["owner"] = (await Prop("owner"))?.GetValue<string>();
                brief["publicKeyPem"] = (await Prop("publicKeyPem"))?.GetValue<string>();
                break;
            case "Note":
                brief["content"] = (await Prop("content"))?.GetValue<string>();
                brief["contentMap"] = (await Prop("contentMap"))?.AsObject();
                break;
            case "OrderedCollection":
            case "Collection":
                if (!Helpers.IsPublic(_id))
                    brief["first"] = (await Prop("first"))?.GetValue<string>();
                break;
        }
        return brief;
    }

    public async Task<JsonObject> Expanded()
    {
        await EnsureCompleteAsync();
        if (_json == null)
        {
            return _id != null
                ? new JsonObject { ["id"] = _id }
                : new JsonObject();
        }

        var obj = Helpers.DeepCopy(_json);

        foreach (var prop in IdProps)
        {
            if (!obj.ContainsKey(prop)) continue;
            var original = obj[prop];
            try
            {
                if (original is JsonArray arr)
                {
                    var expanded = new JsonArray();
                    foreach (var item in arr)
                    {
                        var brief = await ToBrief(item);
                        expanded.Add(brief != null ? JsonValue.Create(brief) : item);
                    }
                    obj[prop] = expanded;
                }
                else if (prop == "object" && await NeedsExpandedObject())
                {
                    var subObj = await Get(original, Options());
                    obj[prop] = subObj != null ? await subObj.Expanded() : original;
                }
                else
                {
                    var brief = await ToBrief(original);
                    obj[prop] = brief != null ? JsonValue.Create(brief) : original;
                }
            }
            catch
            {
                obj[prop] = original;
            }
        }

        foreach (var prop in LinkProps)
        {
            if (obj[prop] is JsonValue jv && jv.TryGetValue<string>(out var strVal))
            {
                obj[prop] = new JsonObject
                {
                    ["type"] = "Link",
                    ["href"] = strVal
                };
            }
        }

        if (obj.ContainsKey("publicKeyPem"))
        {
            obj["publicKeyPem"] = Helpers.ToSpki(obj["publicKeyPem"]!.GetValue<string>());
        }

        return obj;
    }

    public async Task Patch(JsonObject patch)
    {
        var db = GetDatabase();
        if (db == null) return;
        var id = await Id();
        if (id == null) return;

        var json = await Json();
        foreach (var p in patch)
        {
            if (p.Value == null)
                json.Remove(p.Key);
            else
                json[p.Key] = p.Value;
        }
        json["updated"] = DateTime.UtcNow.ToString("o");
        await db.RunAsync("UPDATE object SET data = ? WHERE id = ?", JsonSerializer.Serialize(json), id);
        _json = json;
    }

    public async Task<JsonObject> Expand()
    {
        return await Expanded();
    }

    private async Task<object?> ToBrief(JsonNode? value)
    {
        if (value == null) return null;
        if (value is JsonObject vjo && new[] { "Link", "Hashtag", "Mention" }.Contains(vjo["type"]?.GetValue<string>()))
            return vjo;
        var id = await Helpers.ToId(value);
        if (id == null) return value;
        var obj = await Get(id, Options());
        return obj != null ? await obj.Brief() : new { id };
    }

    private async Task<bool> NeedsExpandedObject()
    {
        var needs = new[] { "Create", "Update", "Accept", "Reject", "Announce" };
        var type = await Type();
        return type != null && needs.Contains(type);
    }

    private static readonly long DefaultExpires = 24 * 60 * 60 * 1000; // one day

    public async Task Cache(long? expires = null)
    {
        expires ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + DefaultExpires;
        var dataId = await Id();
        if (dataId == null) return;
        if (!IsRemoteId(dataId))
        {
            AppState.Logger?.LogWarning("Skipping cache for local object {DataId}", dataId);
            return;
        }

        var db = GetDatabase();
        if (db == null) return;

        var data = await Json();
        var subjectId = _subject != null ? await Helpers.ToId(_subject) : Constants.Public;
        if (subjectId == null) subjectId = Constants.Public;

        await db.RunAsync(
            "INSERT OR REPLACE INTO remotecache (id, subject, expires, data, complete) VALUES (?, ?, ?, ?, ?)",
            dataId, subjectId, expires, JsonSerializer.Serialize(data), _complete ? 1 : 0);
    }

    public async Task ClearCache()
    {
        var id = _id;
        if (id == null) return;
        if (!IsRemoteId(id)) return;

        var db = GetDatabase();
        if (db == null) return;
        await db.RunAsync("DELETE FROM remotecache WHERE id = ?", id);
    }

    protected Database? GetDatabase()
    {
        return AppState.Db;
    }

    public async Task<ActivityObject?> Owner()
    {
        if (_owner != null) return _owner;
        var id = await Id();
        if (id == null) return null;

        var db = GetDatabase();
        if (db != null)
        {
            var row = await db.GetRowAsync("SELECT owner FROM object WHERE id = ?", id);
            if (row != null && row["owner"] != null)
            {
                var ownerStr = row["owner"] as string;
                if (ownerStr != null)
                {
                    _owner = await ActivityObject.Get(ownerStr, Options());
                    if (_owner != null) return _owner;
                }
            }
        }

        string? ownerRef = null;
        foreach (var prop in new[] { "attributedTo", "actor", "owner" })
        {
            ownerRef = (await Prop(prop))?.GetValue<string>();
            if (ownerRef != null) break;
        }
        if (ownerRef != null)
        {
            _owner = new ActivityObject(ownerRef, Options());
        }
        return _owner;
    }

    public async Task<List<ActivityObject>> Addressees()
    {
        if (_addressees != null) return _addressees;
        var id = await Id();
        if (id == null) return new List<ActivityObject>();

        var db = GetDatabase();
        if (db != null)
        {
            var rows = await db.AllAsync("SELECT addresseeId FROM addressee_2 WHERE objectId = ?", id);
            if (rows.Count > 0)
            {
                _addressees = new List<ActivityObject>();
                foreach (var row in rows)
                {
                    var addresseeId = row["addresseeId"] as string;
                    if (addresseeId != null)
                    {
                        var ao = await ActivityObject.Get(addresseeId, Options());
                        if (ao != null) _addressees.Add(ao);
                    }
                }
                return _addressees;
            }
        }

        var addresseeIds = GuessAddressees(await Json());
        _addressees = addresseeIds.Select(aid => new ActivityObject(aid, Options())).ToList();
        return _addressees;
    }

    public async Task<bool> CanRead(string? subject)
    {
        var owner = await Owner();
        var addressees = await Addressees();
        var addresseeIds = new List<string>();
        foreach (var a in addressees)
        {
            var aid = await a.Id();
            if (aid != null) addresseeIds.Add(aid);
        }

        if (subject != null && Helpers.DomainIsBlocked(subject, AppState.BlockedDomains))
            return false;

        if (subject != null && owner != null)
        {
            var db = GetDatabase();
            if (db != null && await User.IsUser(owner, db))
            {
                var blockedProp = await owner.Prop("blocked");
                if (blockedProp != null)
                {
                    var blocked = new Collection(blockedProp, Options());
                    if (await blocked.HasMember(subject)) return false;
                }
            }
        }

        if (addresseeIds.Any(aid => Helpers.IsPublic(aid)))
            return true;

        if (subject == null) return false;

        var ownerId = owner != null ? await owner.Id() : null;
        if (subject == ownerId) return true;

        if (addresseeIds.Contains(subject)) return true;

        foreach (var addresseeId in addresseeIds)
        {
            var obj = new ActivityObject(addresseeId);
            if (await obj.IsCollection())
            {
                var coll = new Collection(await obj.Json());
                if (await coll.HasMember(subject)) return true;
            }
        }

        return false;
    }

    public async Task<bool> CanWrite(string? subject)
    {
        var owner = await Owner();
        var ownerId = owner != null ? await owner.Id() : null;
        if (subject == ownerId) return true;
        return false;
    }

    public async Task Replace(JsonObject replacement)
    {
        var db = GetDatabase();
        if (db == null) return;
        var id = await Id();
        if (id == null) return;
        await db.RunAsync("UPDATE object SET data = ? WHERE id = ?", JsonSerializer.Serialize(replacement), id);
        _json = replacement;
    }

    public static async Task CopyAddresseeProps(JsonObject to, JsonObject from)
    {
        foreach (var prop in new[] { "to", "cc", "bto", "bcc", "audience" })
        {
            if (!from.ContainsKey(prop)) continue;

            var merged = new List<JsonNode?>();
            if (to.ContainsKey(prop))
            {
                var toVal = to[prop];
                if (toVal is JsonArray toArr)
                    merged.AddRange(toArr);
                else
                    merged.Add(toVal);
            }
            var fromVal = from[prop];
            if (fromVal is JsonArray fromArr)
                merged.AddRange(fromArr);
            else
                merged.Add(fromVal);

            var ids = new List<string>();
            foreach (var item in merged)
            {
                if (item is JsonValue jv && jv.TryGetValue<string>(out var s))
                    ids.Add(s);
                else if (item is JsonObject jo && jo.ContainsKey("id"))
                {
                    var idStr = jo["id"]?.GetValue<string>();
                    if (idStr != null) ids.Add(idStr);
                }
            }

            var unique = ids.Distinct().ToList();
            if (unique.Count > 0)
            {
                to[prop] = new JsonArray(unique.Select(s => JsonValue.Create(s)).ToArray());
            }
        }
    }

    public async Task EnsureAddressee(object? addressee)
    {
        var id = await Helpers.ToId(addressee);
        if (id == null) return;
        var addressees = await Addressees();
        foreach (var a in addressees)
        {
            if ((await a.Id()) == id) return;
        }
        var db = GetDatabase();
        if (db == null) return;
        var objId = await Id();
        if (objId == null) return;
        await db.RunAsync("INSERT INTO addressee_2 (objectId, addresseeId) VALUES (?, ?)", objId, id);
    }
}

public class FetchOptions
{
    public object? Subject { get; set; }
    public Dictionary<string, ActivityObject>? Cache { get; set; }
    public Counter? Counter { get; set; }
    public bool SkipRemoteCache { get; set; }
}

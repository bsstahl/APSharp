using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Fedi;

public class User
{
    public string Username { get; set; }
    public string? Password { get; set; }
    public string? ActorId { get; set; }
    public string? PrivateKey { get; set; }

    public User(string username, string? password = null)
    {
        Username = username;
        Password = password;
    }

    public async Task Save(Database db)
    {
        ActorId = await ActivityObject.MakeId("Person");
        var data = new JsonObject
        {
            ["name"] = Username,
            ["id"] = ActorId,
            ["type"] = "Person",
            ["preferredUsername"] = Username,
            ["attributedTo"] = ActorId,
            ["to"] = new JsonArray(Constants.Public)
        };

        var props = new[] { "inbox", "outbox", "followers", "following", "liked" };
        foreach (var prop in props)
        {
            var coll = await Collection.Empty(ActorId, new List<string> { Constants.Public },
                new JsonObject { ["nameMap"] = new JsonObject { ["en"] = $"{Username}'s {prop}" } });
            data[prop] = await coll.Id();
        }

        var privProps = new[] { "blocked", "pendingFollowers", "pendingFollowing" };
        foreach (var prop in privProps)
        {
            var coll = await Collection.Empty(ActorId, new List<string>(),
                new JsonObject { ["nameMap"] = new JsonObject { ["en"] = $"{Username}'s {prop}" } });
            data[prop] = await coll.Id();
        }

        using var rsa = RSA.Create(2048);
        var privateKey = rsa.ExportRSAPrivateKeyPem();
        var publicKey = rsa.ExportRSAPublicKeyPem();

        var pkey = new ActivityObject(new JsonObject
        {
            ["type"] = "CryptographicKey",
            ["owner"] = ActorId,
            ["to"] = new JsonArray(Constants.Public),
            ["publicKeyPem"] = publicKey
        });
        await pkey.Save();
        data["publicKey"] = await pkey.Id();

        var person = new ActivityObject(data);
        await person.Save();

        var passwordHash = BCrypt.Net.BCrypt.HashPassword(Password);
        await db.RunAsync("INSERT INTO user (username, passwordHash, actorId, privateKey) VALUES (?, ?, ?, ?)",
            Username, passwordHash, ActorId, privateKey);
    }

    public static async Task<bool> IsUser(object? obj, Database db)
    {
        if (obj == null) return false;
        var id = await Helpers.ToId(obj);
        if (id == null) return false;
        var row = await db.GetRowAsync("SELECT actorId FROM user WHERE actorId = ?", id);
        return row != null;
    }

    public static async Task<bool> UsernameExists(string username, Database db)
    {
        var row = await db.GetRowAsync("SELECT username FROM user WHERE username = ?", username);
        return row != null;
    }

    public static async Task<User?> FromActorId(string actorId, Database db)
    {
        var row = await db.GetRowAsync("SELECT * FROM user WHERE actorId = ?", actorId);
        if (row == null) return null;
        return FromRow(row);
    }

    public static async Task<User?> FromUsername(string username, Database db)
    {
        var row = await db.GetRowAsync("SELECT * FROM user WHERE username = ?", username);
        if (row == null) return null;
        return FromRow(row);
    }

    public static User FromRow(Dictionary<string, object?> row)
    {
        var user = new User((string)row["username"]!);
        user.ActorId = (string)row["actorId"]!;
        user.PrivateKey = row["privateKey"] as string;
        return user;
    }

    public static async Task<User?> Authenticate(string username, string password, Database db)
    {
        var row = await db.GetRowAsync("SELECT * FROM user WHERE username = ?", username);
        if (row == null) return null;
        var passwordHash = (string)row["passwordHash"]!;
        if (!BCrypt.Net.BCrypt.Verify(password, passwordHash)) return null;
        return FromRow(row);
    }

    public async Task<ActivityObject> GetActor(Database db)
    {
        return new ActivityObject(ActorId);
    }

    public static async Task UpdateAllUsers(Database db)
    {
        var rows = await db.AllAsync("SELECT actorId FROM user");
        foreach (var row in rows)
        {
            var actorId = (string)row["actorId"]!;
            var actor = await ActivityObject.Get(actorId);
            if (actor == null) continue;
            if (await actor.Prop("attributedTo") == null)
            {
                Console.WriteLine($"Adding attributedTo to actor {actorId}");
                await actor.Patch(new JsonObject { ["attributedTo"] = actorId });
            }
            if (await actor.Prop("to") == null)
            {
                Console.WriteLine($"Adding to to actor {actorId}");
                await actor.Patch(new JsonObject { ["to"] = Constants.Public });
            }
        }
    }

    public static async Task UpdateAllKeys(Database db)
    {
        var rows = await db.AllAsync("SELECT * FROM user WHERE privateKey LIKE '-----BEGIN RSA PRIVATE KEY-----%'");
        foreach (var row in rows)
        {
            var actorId = (string)row["actorId"]!;
            var actor = new ActivityObject(actorId);
            var publicKey = new ActivityObject(await actor.Prop("publicKey") ?? new JsonObject());
            var newPublicKeyPem = Helpers.ToSpki((await publicKey.Prop("publicKeyPem") ?? "").ToString()!);
            var newPrivateKeyPem = Helpers.ToPkcs8((string)row["privateKey"]!);
            Console.WriteLine($"Updating keys for {actorId}");
            await publicKey.Patch(new JsonObject { ["publicKeyPem"] = newPublicKeyPem });
            await actor.Patch(new JsonObject { ["publicKey"] = await publicKey.Id() });
            await db.RunAsync("UPDATE user SET privateKey = ? WHERE actorId = ?", newPrivateKeyPem, actorId);
        }
    }

    public static async Task UpdateAllCollections(Database db)
    {
        var rows = await db.AllAsync("SELECT * FROM user");
        int count = 0;
        foreach (var row in rows)
        {
            var user = FromRow(row);
            var actor = await ActivityObject.Get(user.ActorId);
            if (actor == null) continue;

            var props = new[] { "inbox", "outbox", "followers", "following", "liked" };
            foreach (var prop in props)
            {
                var coll = await Collection.Get(await Helpers.ToId(await actor.Prop(prop)) ?? "", new FetchOptions { Subject = actor });
                if (coll != null)
                    count += await UpdateCollection(user, coll, actor, Constants.Public, db);
            }
            foreach (var prop in props)
            {
                var coll = await Collection.Get(await Helpers.ToId(await actor.Prop(prop)) ?? "", new FetchOptions { Subject = actor });
                if (coll == null) continue;
                var pageRef = await Helpers.ToId(await coll.Prop("first"));
                while (pageRef != null)
                {
                    var page = await ActivityObject.Get(pageRef, new FetchOptions { Subject = actor });
                    if (page != null)
                        count += await UpdateCollection(user, new Collection(await page.Json(), new FetchOptions { Subject = actor }), actor, prop == "inbox" ? null : Constants.Public, db);
                    pageRef = page != null ? await Helpers.ToId(await page.Prop("next")) : null;
                }
            }

            var priv = new[] { "blocked", "pendingFollowing", "pendingFollowers" };
            foreach (var prop in priv)
            {
                var coll = await Collection.Get(await Helpers.ToId(await actor.Prop(prop)) ?? "", new FetchOptions { Subject = actor });
                if (coll != null)
                    count += await UpdateCollection(user, coll, actor, null, db);
                if (coll == null) continue;
                var pageRef = await Helpers.ToId(await coll.Prop("first"));
                while (pageRef != null)
                {
                    var page = await ActivityObject.Get(pageRef, new FetchOptions { Subject = actor });
                    if (page != null)
                        count += await UpdateCollection(user, new Collection(await page.Json(), new FetchOptions { Subject = actor }), actor, null, db);
                    pageRef = page != null ? await Helpers.ToId(await page.Prop("next")) : null;
                }
            }
        }
        Console.WriteLine($"Updated {count} collections and collection pages");
    }

    public static async Task<int> UpdateCollection(User user, Collection coll, ActivityObject actor, string? to, Database db)
    {
        var patch = new JsonObject();
        var at = await coll.Prop("attributedTo");
        var atId = await Helpers.ToId(at);
        if (at == null || (at is JsonObject jo && jo.Count == 0) || atId != await actor.Id())
        {
            patch["attributedTo"] = await actor.Id();
        }
        var toProp = await coll.Prop("to");
        if (to != null && toProp == null)
        {
            patch["to"] = to;
        }
        else if (to == null && toProp != null)
        {
            patch["to"] = null;
        }
        if (patch.Count > 0)
        {
            Console.WriteLine($"Updating user collection {await coll.Id()} for {await actor.Id()} to correct permissions");
            await user.InternalUpdate(coll, patch, to, actor, db);
            return 1;
        }
        return 0;
    }

    public async Task InternalUpdate(ActivityObject ao, JsonObject patch, string? to, ActivityObject actor, Database db)
    {
        try
        {
            await ao.Patch(patch);
            var data = new JsonObject
            {
                ["id"] = await ActivityObject.MakeId("Update"),
                ["type"] = "Update",
                ["object"] = await ao.Id() ?? "",
                ["actor"] = await actor.Id() ?? ""
            };
            if (to != null) data["to"] = to;
            var activity = new Activity(data, new FetchOptions { Subject = actor });
            await activity.Save();
            var outbox = await Collection.Get(await Helpers.ToId(await actor.Prop("outbox")) ?? "", new FetchOptions { Subject = actor });
            if (outbox != null) await outbox.Prepend(activity);
            var inbox = await Collection.Get(await Helpers.ToId(await actor.Prop("inbox")) ?? "", new FetchOptions { Subject = actor });
            if (inbox != null) await inbox.Prepend(activity);
            _ = activity.Distribute();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in internalUpdate: {ex.Message}");
        }
    }

    public async Task<Activity> DoActivity(JsonObject data, Database db)
    {
        var actor = await GetActor(db);
        var ownerId = await actor.Id() ?? throw new Exception("No actor id");
        var outbox = new Collection(await actor.Prop("outbox") ?? new JsonObject(), new FetchOptions { Subject = actor });
        var typeStr = data["type"]?.GetValue<string>() ?? "Activity";
        data["id"] = await ActivityObject.MakeId(typeStr);
        data["actor"] = ownerId;
        var activity = new Activity(data, new FetchOptions { Subject = ownerId });
        await activity.Apply();
        await activity.Save();
        await outbox.Prepend(activity);
        var inbox = new Collection(await actor.Prop("inbox") ?? new JsonObject(), new FetchOptions { Subject = actor });
        await inbox.Prepend(activity);
        _ = activity.Distribute();
        return activity;
    }
}

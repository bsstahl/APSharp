using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fedi;

public class Activity : ActivityObject
{
    public Activity(object? data, FetchOptions? options = null) : base(data, options) { }

    public static new async Task<Activity?> Get(object? reference, FetchOptions? options = null)
    {
        var ao = await ActivityObject.Get(reference, options);
        if (ao != null)
        {
            return new Activity(await ao.Json(), options);
        }
        return null;
    }

    public override string DefaultType() => "Activity";

    public static bool DuckType(JsonObject data)
    {
        var props = new[] { "actor", "object", "target", "result", "origin", "instrument" };
        return props.Any(p => data.ContainsKey(p));
    }

    public async Task SetActor(object? actor)
    {
        await SetProp("actor", JsonValue.Create(await Helpers.ToId(actor) ?? ""));
    }

    public async Task Apply()
    {
        var activity = await Json();
        var actor = ActivityObject.GuessOwner(activity);
        var addressees = ActivityObject.GuessAddressees(activity);
        var actorObj = await ActivityObject.Get(actor, new FetchOptions { Subject = actor, Cache = _cache, Counter = _counter });
        if (actorObj == null) throw new Exception("Actor not found");

        var type = await Type() ?? "";
        var types = type.Split(',').Select(t => t.Trim()).ToArray();

        foreach (var item in types)
        {
            switch (item)
            {
                case "Follow":
                    await ApplyFollow(activity, actorObj);
                    break;
                case "Accept":
                    await ApplyAccept(activity, actorObj);
                    break;
                case "Reject":
                    await ApplyReject(activity, actorObj);
                    break;
                case "Create":
                    await ApplyCreate(activity, actorObj, addressees);
                    break;
                case "Update":
                    await ApplyUpdate(activity, actorObj);
                    break;
                case "Delete":
                    await ApplyDelete(activity, actorObj);
                    break;
                case "Add":
                    await ApplyAdd(activity, actorObj);
                    break;
                case "Remove":
                    await ApplyRemove(activity, actorObj);
                    break;
                case "Like":
                    await ApplyLike(activity, actorObj);
                    break;
                case "Block":
                    await ApplyBlock(activity, actorObj);
                    break;
                case "Announce":
                    await ApplyAnnounce(activity, actorObj);
                    break;
                case "Undo":
                    await ApplyUndo(activity, actorObj);
                    break;
            }
        }
    }

    private async Task ApplyFollow(JsonObject activity, ActivityObject actorObj)
    {
        var objectProp = await Prop("object");
        if (objectProp == null) throw new Exception("No object followed");
        var other = await ActivityObject.Get(objectProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        if (other == null) throw new Exception("No such object to follow");
        await other.EnsureCompleteAsync();
        var otherId = await other.Id();

        var following = new Collection(await actorObj.Prop("following") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        if (await following.HasMember(otherId)) throw new Exception("Already following");

        var pendingFollowing = new Collection(await actorObj.Prop("pendingFollowing") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        var found = await pendingFollowing.Find(async (act) => await Helpers.ToId(await act.Prop("object")) == otherId);
        if (found != null) throw new Exception("Already pending following");

        Collection? pendingFollowers = null;
        var isUser = await User.IsUser(other, AppState.Db!);
        if (isUser)
        {
            var actorId = await actorObj.Id();
            var followers = new Collection(await other.Prop("followers") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
            if (await followers.HasMember(actorId)) throw new Exception("Already followed");
            pendingFollowers = new Collection(await other.Prop("pendingFollowers") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
            var pfFound = await pendingFollowers.Find(async (act) => await Helpers.ToId(await act.Prop("object")) == actorId);
            if (pfFound != null) throw new Exception("Already pending follower");
        }

        await pendingFollowing.Prepend(this);
        if (isUser && pendingFollowers != null)
        {
            await pendingFollowers.Prepend(this);
        }
    }

    private async Task ApplyAccept(JsonObject activity, ActivityObject actorObj)
    {
        var objectProp = await Prop("object");
        if (objectProp == null) throw new Exception("No object accepted");
        var accepted = await Activity.Get(objectProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        if (accepted == null) return;

        var accType = await accepted.Type();
        if (accType == "Follow")
        {
            var pendingFollowers = new Collection(await actorObj.Prop("pendingFollowers") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
            await pendingFollowers.Expand();
            if (!await pendingFollowers.HasMember(await accepted.Id())) throw new Exception("Not awaiting acceptance for follow");

            var other = await ActivityObject.Get(await accepted.Prop("actor") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
            if (other == null) throw new Exception("Other actor not found");

            var isUser = await User.IsUser(other, AppState.Db!);
            Collection? pendingFollowing = null;
            if (isUser)
            {
                pendingFollowing = new Collection(await other.Prop("pendingFollowing") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
                await pendingFollowing.Expand();
                if (!await pendingFollowing.HasMember(await accepted.Id())) throw new Exception("Not awaiting acceptance for follow");
            }

            await pendingFollowers.Remove(accepted);
            var followers = new Collection(await actorObj.Prop("followers") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
            await followers.Prepend(other);
            if (isUser && pendingFollowing != null)
            {
                await pendingFollowing.Remove(accepted);
                var following = new Collection(await other.Prop("following") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
                await following.Prepend(actorObj);
            }
        }
    }

    private async Task ApplyReject(JsonObject activity, ActivityObject actorObj)
    {
        var objectProp = await Prop("object");
        if (objectProp == null) throw new Exception("No object followed");
        var rejected = await Activity.Get(objectProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        if (rejected == null) return;

        var rejType = await rejected.Type();
        if (rejType == "Follow")
        {
            var pendingFollowers = new Collection(await actorObj.Prop("pendingFollowers") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
            if (!await pendingFollowers.HasMember(await rejected.Id())) throw new Exception("Not awaiting acceptance for follow");

            var other = await ActivityObject.Get(await rejected.Prop("actor") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
            if (other == null) throw new Exception("Other actor not found");

            var isUser = await User.IsUser(other, AppState.Db!);
            Collection? pendingFollowing = null;
            if (isUser)
            {
                pendingFollowing = new Collection(await other.Prop("pendingFollowing") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
                if (!await pendingFollowing.HasMember(await rejected.Id())) throw new Exception("Not awaiting acceptance for follow");
            }

            await pendingFollowers.Remove(rejected);
            if (isUser && pendingFollowing != null)
            {
                await pendingFollowing.Remove(rejected);
            }
        }
    }

    private async Task ApplyCreate(JsonObject activity, ActivityObject actorObj, List<string> addressees)
    {
        var obj = activity["object"] as JsonObject;
        if (obj == null) throw new Exception("No object to create");
        obj["attributedTo"] = JsonValue.Create(await actorObj.Id() ?? "");
        await ActivityObject.CopyAddresseeProps(obj, activity);
        await ActivityObject.CopyAddresseeProps(activity, obj);
        obj["type"] = JsonValue.Create(obj["type"]?.GetValue<string>() ?? "Object");

        var summaryEn = $"A(n) {obj["type"]?.GetValue<string>()} by {await actorObj.Name()}";
        if (!new[] { "name", "nameMap", "summary", "summaryMap" }.Any(p => obj.ContainsKey(p)))
        {
            obj["summaryMap"] = new JsonObject { ["en"] = summaryEn };
        }

        foreach (var prop in new[] { "likes", "replies", "shares" })
        {
            var value = await Collection.Empty(await actorObj.Id(), addressees, new JsonObject { ["summaryMap"] = new JsonObject { ["en"] = $"{prop} of {summaryEn}" } });
            obj[prop] = JsonValue.Create(await value.Id() ?? "");
        }

        var types = obj["type"]?.GetValue<string>()?.Split(',').Select(t => t.Trim()).ToArray() ?? new[] { "Object" };
        if (types.Any(t => new[] { "Collection", "OrderedCollection" }.Contains(t)) && !obj.ContainsKey("items") && !obj.ContainsKey("orderedItems"))
        {
            obj["id"] = JsonValue.Create(await ActivityObject.MakeId(obj["type"]?.GetValue<string>() ?? "Object"));
            var pageProps = types.Contains("OrderedCollection")
                ? new JsonObject { ["type"] = "OrderedCollectionPage", ["orderedItems"] = new JsonArray() }
                : new JsonObject { ["type"] = "CollectionPage", ["items"] = new JsonArray() };
            pageProps["partOf"] = obj["id"]!.DeepClone();
            pageProps["attributedTo"] = JsonValue.Create(await actorObj.Id() ?? "");
            await ActivityObject.CopyAddresseeProps(pageProps, obj);
            var page = new ActivityObject(pageProps);
            await page.Save();
            obj["first"] = obj["last"] = JsonValue.Create(await page.Id() ?? "");
        }

        var saved = new ActivityObject(obj);
        await saved.Save();
        activity["object"] = JsonValue.Create(await saved.Id() ?? "");

        var inReplyToProp = await saved.Prop("inReplyTo");
        if (inReplyToProp != null)
        {
            var parent = await ActivityObject.Get(inReplyToProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
            if (parent != null)
            {
                var parentOwner = await parent.Owner();
                if (parentOwner != null && await User.IsUser(parentOwner, AppState.Db!))
                {
                    var replies = new Collection(await parent.Prop("replies") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
                    await replies.Prepend(saved);
                }
            }
        }
    }

    private async Task ApplyUpdate(JsonObject activity, ActivityObject actorObj)
    {
        var obj = activity["object"] as JsonObject;
        if (obj == null) throw new Exception("No object to update");
        if (!obj.ContainsKey("id")) throw new Exception("No id for object to update");

        var objectId = obj["id"]?.GetValue<string>();
        var target = await ActivityObject.Get(objectId, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        if (target == null) throw new Exception($"Unable to get object {objectId}");

        var objectOwner = await target.Owner();
        if (objectOwner == null || await objectOwner.Id() != await actorObj.Id())
            throw new Exception("You can't update an object you don't own");

        if (await User.IsUser(target, AppState.Db!))
        {
            foreach (var prop in new[] { "inbox", "outbox", "followers", "following", "pendingFollowers", "pendingFollowing", "liked", "blocked" })
            {
                if (obj.ContainsKey(prop) && await Helpers.ToId(obj[prop]) != await Helpers.ToId(await target.Prop(prop)))
                    throw new Exception($"Cannot update {prop} directly");
            }
        }

        if (await target.IsCollection())
        {
            foreach (var prop in new[] { "first", "last", "current" })
            {
                if (obj.ContainsKey(prop) && await Helpers.ToId(obj[prop]) != await Helpers.ToId(await target.Prop(prop)))
                    throw new Exception($"Cannot update {prop} directly");
            }
            if (obj.ContainsKey("totalItems") && obj["totalItems"]?.GetValue<int>() != (await target.Prop("totalItems"))?.GetValue<int>())
                throw new Exception("Cannot update totalItems directly");
        }

        if (await target.IsCollectionPage())
        {
            foreach (var prop in new[] { "prev", "next", "partOf" })
            {
                if (obj.ContainsKey(prop) && await Helpers.ToId(obj[prop]) != await Helpers.ToId(await target.Prop(prop)))
                    throw new Exception($"Cannot update {prop} directly");
            }
            if (obj.ContainsKey("startIndex") && obj["startIndex"]?.GetValue<int>() != (await target.Prop("startIndex"))?.GetValue<int>())
                throw new Exception("Cannot update startIndex directly");
        }

        if ((await target.IsCollection()) || (await target.IsCollectionPage()))
        {
            foreach (var prop in new[] { "items", "orderedItems" })
            {
                if (obj.ContainsKey(prop))
                {
                    var proposed = obj[prop] as JsonArray;
                    var current = await target.Prop(prop) as JsonArray;
                    if (current == null) throw new Exception($"Cannot insert {prop} directly");
                    if (proposed == null) throw new Exception($"Cannot insert scalar value for {prop}");
                    if (proposed.Count != current.Count) throw new Exception($"Cannot change size of {prop}");
                    for (var i = 0; i < current.Count; i++)
                    {
                        if (proposed[i]?.ToString() != current[i]?.ToString())
                            throw new Exception($"Cannot change values of {prop}");
                    }
                }
            }
        }

        foreach (var prop in new[] { "replies", "likes", "shares", "attributedTo" })
        {
            if (obj.ContainsKey(prop) && await Helpers.ToId(obj[prop]) != await Helpers.ToId(await target.Prop(prop)))
                throw new Exception($"Cannot update {prop} directly");
        }

        foreach (var prop in new[] { "published", "updated" })
        {
            if (obj.ContainsKey(prop) && obj[prop]?.ToString() != (await target.Prop(prop))?.ToString())
                throw new Exception($"Cannot update {prop} directly");
        }

        // Strip id and type from patch
        var patch = new JsonObject();
        foreach (var p in obj)
        {
            if (p.Key != "id" && p.Key != "type")
                patch[p.Key] = p.Value?.DeepClone();
        }
        await target.Patch(patch);
        activity["object"] = await target.Json();
    }

    private async Task ApplyDelete(JsonObject activity, ActivityObject actorObj)
    {
        var objectProp = await Prop("object");
        if (objectProp == null) throw new Exception("No object to delete");
        var objectId = await Helpers.ToId(objectProp);
        var target = await ActivityObject.Get(objectId, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        if (target == null || await target.Id() == null) throw new Exception("No id for object to delete");

        var objectOwner = await target.Owner();
        if (objectOwner == null || await objectOwner.Id() != await actorObj.Id())
            throw new Exception("You can't delete an object you don't own");

        var timestamp = DateTime.UtcNow.ToString("o");
        await target.Replace(new JsonObject
        {
            ["id"] = await target.Id(),
            ["formerType"] = await target.Type(),
            ["type"] = "Tombstone",
            ["published"] = await target.Prop("published"),
            ["updated"] = timestamp,
            ["deleted"] = timestamp,
            ["summaryMap"] = new JsonObject { ["en"] = $"A deleted {await target.Type()} by {await actorObj.Name()}" }
        });
    }

    private async Task ApplyAdd(JsonObject activity, ActivityObject actorObj)
    {
        var objectProp = await Prop("object");
        if (objectProp == null) throw new Exception("No object to add");
        var obj = await ActivityObject.Get(objectProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        if (obj == null || await obj.Id() == null) throw new Exception("No id for object to add");

        var targetProp = await Prop("target");
        if (targetProp == null) throw new Exception("No target to add to");
        var target = new Collection(targetProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        if (await target.Id() == null) throw new Exception("No id for object to add to");
        if (!await target.IsCollection()) throw new Exception("Can't add to a non-collection");

        var targetOwner = await target.Owner();
        if (targetOwner == null || await targetOwner.Id() != await actorObj.Id())
            throw new Exception("You can't add to an object you don't own");

        var actorJson = await actorObj.Json();
        foreach (var prop in new[] { "inbox", "outbox", "followers", "following", "liked" })
        {
            if (await target.Id() == await Helpers.ToId(actorJson[prop]))
                throw new Exception($"Can't add an object directly to your {prop}");
        }

        if (await target.HasMember(await obj.Id())) throw new Exception("Already a member");
        await target.Prepend(obj);
    }

    private async Task ApplyRemove(JsonObject activity, ActivityObject actorObj)
    {
        var objectProp = await Prop("object");
        if (objectProp == null) throw new Exception("No object to remove");
        var obj = await ActivityObject.Get(objectProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        if (obj == null || await obj.Id() == null) throw new Exception("No id for object to remove");

        var targetProp = await Prop("target");
        if (targetProp == null) throw new Exception("No target to remove from");
        var target = new Collection(targetProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        if (await target.Id() == null) throw new Exception("No id for object to remove from");
        if (!await target.IsCollection()) throw new Exception("Can't remove from a non-collection");

        var targetOwner = await target.Owner();
        if (targetOwner == null || await targetOwner.Id() != await actorObj.Id())
            throw new Exception("You can't remove from an object you don't own");

        var actorJson = await actorObj.Json();
        foreach (var prop in new[] { "inbox", "outbox", "followers", "following", "liked" })
        {
            if (await target.Id() == await Helpers.ToId(actorJson[prop]))
                throw new Exception($"Can't remove an object directly from your {prop}");
        }

        if (!await target.HasMember(await obj.Id())) throw new Exception("Not a member");
        await target.Remove(obj);
    }

    private async Task ApplyLike(JsonObject activity, ActivityObject actorObj)
    {
        var objectProp = await Prop("object");
        if (objectProp == null) throw new Exception("No object to like");
        var obj = await ActivityObject.Get(objectProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        if (obj == null) throw new Exception("Object not found");
        if (!await obj.CanRead(await actorObj.Id())) throw new Exception("Can't like an object you can't read");

        var liked = new Collection(await actorObj.Prop("liked") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        if (await liked.HasMember(await obj.Id())) throw new Exception("Already liked!");
        await liked.Prepend(obj);

        var objectOwner = await obj.Owner();
        if (objectOwner != null && await User.IsUser(objectOwner, AppState.Db!))
        {
            var likesProp = await obj.Prop("likes");
            Collection likes;
            if (likesProp != null)
            {
                likes = new Collection(likesProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
            }
            else
            {
                likes = await Collection.Empty(objectOwner, new List<string>(), null, null);
                await obj.Patch(new JsonObject { ["likes"] = JsonValue.Create(await likes.Id() ?? "") });
            }
            await likes.PrependData(activity);
        }
    }

    private async Task ApplyBlock(JsonObject activity, ActivityObject actorObj)
    {
        var objectProp = await Prop("object");
        if (objectProp == null) throw new Exception("No object to block");
        var blocked = new Collection(await actorObj.Prop("blocked") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        var other = await ActivityObject.Get(objectProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        if (other == null) throw new Exception("Object not found");
        if (await blocked.HasMember(await other.Id())) throw new Exception("Already blocked!");

        await blocked.Prepend(other);
        var followers = new Collection(await actorObj.Prop("followers") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        await followers.Remove(other);
        var following = new Collection(await actorObj.Prop("following") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        await following.Remove(other);

        if (await User.IsUser(other, AppState.Db!))
        {
            var otherFollowers = new Collection(await other.Prop("followers") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
            await otherFollowers.Remove(actorObj);
            var otherFollowing = new Collection(await other.Prop("following") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
            await otherFollowing.Remove(actorObj);
        }
    }

    private async Task ApplyAnnounce(JsonObject activity, ActivityObject actorObj)
    {
        var objectProp = await Prop("object");
        if (objectProp == null) throw new Exception("Nothing to announce");
        var obj = await ActivityObject.Get(objectProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        if (obj == null) throw new Exception("Object not found");
        var owner = await obj.Owner();
        if (owner != null && await User.IsUser(owner, AppState.Db!))
        {
            var shares = new Collection(await obj.Prop("shares") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
            await shares.Prepend(this);
        }
    }

    private async Task ApplyUndo(JsonObject activity, ActivityObject actorObj)
    {
        var objectProp = await Prop("object");
        if (objectProp == null) throw new Exception("Nothing to undo");
        var obj = await Activity.Get(objectProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
        if (obj == null) throw new Exception("Object not found");
        var owner = await obj.Owner();
        if (owner == null) throw new Exception("Object has no owner");
        if (await owner.Id() != await actorObj.Id()) throw new Exception("Cannot undo an object you do not own");

        var type = await obj.Type();
        switch (type)
        {
            case "Like":
                {
                    var likedObjectProp = await obj.Prop("object");
                    if (likedObjectProp == null) throw new Exception("Nothing liked");
                    var likedObject = await ActivityObject.Get(likedObjectProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
                    if (likedObject == null) throw new Exception("Object not found");
                    var liked = new Collection(await actorObj.Prop("liked") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
                    await liked.Remove(likedObject);
                    var likedObjectOwner = await likedObject.Owner();
                    if (likedObjectOwner != null && await User.IsUser(likedObjectOwner, AppState.Db!))
                    {
                        var likes = new Collection(await likedObject.Prop("likes") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
                        await likes.Remove(obj);
                    }
                    break;
                }
            case "Block":
                {
                    var blockedObjectProp = await obj.Prop("object");
                    if (blockedObjectProp == null) throw new Exception("Nothing blocked");
                    var blockedObject = await ActivityObject.Get(blockedObjectProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
                    if (blockedObject == null) throw new Exception("Object not found");
                    var blocked = new Collection(await actorObj.Prop("blocked") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
                    await blocked.Remove(blockedObject);
                    break;
                }
            case "Follow":
                {
                    var followedObjectProp = await obj.Prop("object");
                    if (followedObjectProp == null) throw new Exception("Nothing followed");
                    var followedObject = await ActivityObject.Get(followedObjectProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
                    if (followedObject == null) throw new Exception("Object not found");
                    var pendingFollowing = new Collection(await actorObj.Prop("pendingFollowing") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
                    if (await pendingFollowing.HasMember(await obj.Id()))
                    {
                        await pendingFollowing.Remove(obj);
                    }
                    else
                    {
                        var following = new Collection(await actorObj.Prop("following") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
                        await following.Remove(followedObject);
                    }
                    var followedObjectOwner = await followedObject.Owner();
                    if (followedObjectOwner != null && await User.IsUser(followedObjectOwner, AppState.Db!))
                    {
                        var pendingFollowers = new Collection(await followedObject.Prop("pendingFollowers") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
                        if (await pendingFollowers.HasMember(await obj.Id()))
                        {
                            await pendingFollowers.Remove(obj);
                        }
                        else
                        {
                            var followers = new Collection(await followedObject.Prop("followers") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
                            await followers.Remove(actorObj);
                        }
                    }
                    break;
                }
            case "Announce":
                {
                    var sharedObjectProp = await obj.Prop("object");
                    if (sharedObjectProp == null) throw new Exception("Nothing announced");
                    var sharedObject = await ActivityObject.Get(sharedObjectProp, new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
                    if (sharedObject == null) throw new Exception("Object not found");
                    var sharedObjectOwner = await sharedObject.Owner();
                    if (sharedObjectOwner != null && await User.IsUser(sharedObjectOwner, AppState.Db!))
                    {
                        await sharedObject.Expand();
                        var shares = new Collection(await sharedObject.Prop("shares") ?? new JsonObject(), new FetchOptions { Subject = actorObj, Cache = _cache, Counter = _counter });
                        await shares.Remove(obj);
                    }
                    break;
                }
        }
    }

    public async Task Distribute(List<string>? addressees = null)
    {
        var owner = await Owner();
        if (owner == null) return;
        var activity = await Expanded();
        if (addressees == null)
        {
            addressees = ActivityObject.GuessAddressees(activity);
        }

        var expanded = new List<string>();
        foreach (var addressee in addressees)
        {
            if (Helpers.IsPublic(addressee))
            {
                var followers = new Collection(await owner.Prop("followers") ?? new JsonObject(), new FetchOptions { Subject = owner, Cache = _cache, Counter = _counter });
                var members = await followers.Members() ?? new List<string>();
                expanded.AddRange(members);
            }
            else
            {
                var obj = new ActivityObject(addressee, new FetchOptions { Subject = owner, Cache = _cache, Counter = _counter });
                if (await obj.IsCollection())
                {
                    var coll = new Collection(addressee, new FetchOptions { Subject = owner, Cache = _cache, Counter = _counter });
                    var objOwner = await obj.Owner();
                    if (coll != null && objOwner != null && await objOwner.Id() == await owner.Id())
                    {
                        var members = await coll.Members();
                        expanded.AddRange(members);
                    }
                }
                else
                {
                    expanded.Add(addressee);
                }
            }
        }

        var ownerId = await owner.Id() ?? "";
        expanded = expanded
            .Where(v => v != null && v != ownerId)
            .Distinct()
            .ToList();

        var body = JsonSerializer.Serialize(activity);
        var db = AppState.Db!;
        var row = await db.GetRowAsync("SELECT privateKey FROM user WHERE actorId = ?", await owner.Id() ?? "");
        if (row == null) throw new Exception("Owner not found in database");
        var privateKey = row["privateKey"] as string ?? "";
        var keyId = await Helpers.ToId(await owner.Prop("publicKey")) ?? "";

        foreach (var addressee in expanded)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                if (!string.IsNullOrEmpty(privateKey))
                    await SendTo(addressee, body, privateKey, keyId, owner);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed delivery to {addressee}: {ex.Message}");
                }
            });
        }
    }

    private async Task SendTo(string addressee, string body, string privateKey, string? keyId, ActivityObject owner)
    {
        var other = await ActivityObject.Get(addressee, new FetchOptions { Subject = owner, Cache = _cache, Counter = _counter });
        if (other == null) return;

        if (await User.IsUser(other, AppState.Db!))
        {
            await other.Expand();
            var inbox = new Collection(await other.Prop("inbox") ?? new JsonObject(), new FetchOptions { Subject = owner, Cache = _cache, Counter = _counter });
            await inbox.PrependData(await Json());
        }
        else
        {
            var inboxProp = await other.Prop("inbox");
            if (inboxProp == null)
            {
                Console.WriteLine($"Cannot deliver to {addressee}: no 'inbox' property");
                return;
            }
            var inbox = await Helpers.ToId(inboxProp);
            if (inbox == null) return;

            var date = DateTime.UtcNow.ToString("R");
            var digest = Helpers.DigestBody(body);
            var httpSignature = new HttpSignature(keyId!, privateKey, "POST", inbox, date, digest);
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Date", date);
            client.DefaultRequestHeaders.Add("Digest", digest);
            client.DefaultRequestHeaders.Add("Signature", httpSignature.Header);
            var content = new StringContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/activity+json") { CharSet = "utf-8" };
            var res = await client.PostAsync(inbox, content);
            var resBody = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                throw new Exception($"Bad status {(int)res.StatusCode} for delivery to {inbox}: {resBody}");
            }
        }
    }
}

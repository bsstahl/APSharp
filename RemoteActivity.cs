using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fedi;

public class RemoteActivity : Activity
{
    public RemoteActivity(object? data, FetchOptions? options = null) : base(data, options) { }

    public async Task Apply(ActivityObject? remote = null, List<string>? addressees = null, params object?[] args)
    {
        var owner = args.Length > 0 ? args[0] : null;
        var ownerObj = owner != null ? await ActivityObject.Get(owner, new FetchOptions { Cache = _cache, Counter = _counter }) : null;
        if (ownerObj == null) throw new Exception("Owner not found");

        if (remote == null)
        {
            var actorProp = await Prop("actor");
            remote = await ActivityObject.Get(actorProp ?? new JsonObject(), new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        }
        if (remote == null) throw new Exception("Remote actor not found");

        if (addressees == null)
        {
            var json = await Json();
            addressees = ActivityObject.GuessAddressees(json);
        }

        var remoteObj = remote;
        var type = await Type() ?? "";
        var types = type.Split(',').Select(t => t.Trim()).ToArray();

        foreach (var item in types)
        {
            switch (item)
            {
                case "Follow":
                    await RemoteApplyFollow(ownerObj, remoteObj);
                    break;
                case "Create":
                    await RemoteApplyCreate(ownerObj, remoteObj);
                    break;
                case "Update":
                    await RemoteApplyUpdate(ownerObj, remoteObj);
                    break;
                case "Delete":
                    await RemoteApplyDelete(ownerObj, remoteObj);
                    break;
                case "Like":
                    await RemoteApplyLike(ownerObj, remoteObj);
                    break;
                case "Announce":
                    await RemoteApplyAnnounce(ownerObj, remoteObj);
                    break;
                case "Add":
                    await RemoteApplyAdd(ownerObj, remoteObj);
                    break;
                case "Remove":
                    await RemoteApplyRemove(ownerObj, remoteObj);
                    break;
                case "Accept":
                    await RemoteApplyAccept(ownerObj, remoteObj);
                    break;
                case "Reject":
                    await RemoteApplyReject(ownerObj, remoteObj);
                    break;
                case "Undo":
                    await RemoteApplyUndo(ownerObj, remoteObj);
                    break;
            }
        }
    }

    private async Task RemoteApplyFollow(ActivityObject ownerObj, ActivityObject remoteObj)
    {
        var objectProp = await Prop("object");
        var obj = await ActivityObject.Get(objectProp ?? new JsonObject(), new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        if (obj == null) return;
        if (await obj.Id() != await ownerObj.Id()) return;

        var followers = await Collection.Get(await ownerObj.Prop("followers") ?? new JsonObject(), new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        if (followers != null && await followers.HasMember(remoteObj)) throw new Exception("Already a follower");

        var pendingFollowers = await Collection.Get(await ownerObj.Prop("pendingFollowers") ?? new JsonObject(), new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        if (pendingFollowers != null && await pendingFollowers.HasMember(await Id())) throw new Exception("Already pending");

        if (pendingFollowers != null)
        {
            await pendingFollowers.Prepend(this);
        }
    }

    private async Task RemoteApplyCreate(ActivityObject ownerObj, ActivityObject remoteObj)
    {
        if (await Prop("object") is not JsonNode objNode) return;
        var ao = new ActivityObject(objNode, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        var owner = await ao.Owner();
        if (owner != null && await owner.Id() != await remoteObj.Id()) throw new Exception("Cannot create something you do not own!");

        await ao.Cache();
        var inReplyToProp = await ao.Prop("inReplyTo");
        if (inReplyToProp != null)
        {
            var inReplyTo = new ActivityObject(inReplyToProp, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
            await inReplyTo.Expand();
            var inReplyToOwner = await inReplyTo.Owner();
            if (inReplyToOwner != null && await inReplyToOwner.Id() == await ownerObj.Id())
            {
                if (!await inReplyTo.CanRead(await remoteObj.Id())) throw new Exception("Cannot reply to something you cannot read!");
                var replies = new Collection(await inReplyTo.Prop("replies") ?? new JsonObject(), new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
                if (!await replies.HasMember(ao))
                {
                    await replies.Prepend(ao);
                }
            }
        }
    }

    private async Task RemoteApplyUpdate(ActivityObject ownerObj, ActivityObject remoteObj)
    {
        if (await Prop("object") is not JsonNode objNode) return;
        var ao = new ActivityObject(objNode, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        var aoOwner = await ao.Owner();
        if (aoOwner != null && await aoOwner.Id() != await remoteObj.Id()) throw new Exception("Cannot update something you do not own!");

        await ao.ClearCache();
        var inReplyToProp = await ao.Prop("inReplyTo");
        if (inReplyToProp != null)
        {
            var inReplyTo = new ActivityObject(inReplyToProp, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
            await inReplyTo.Expand();
            var inReplyToOwner = await inReplyTo.Owner();
            if (inReplyToOwner != null && await inReplyToOwner.Id() == await ownerObj.Id())
            {
                if (!await inReplyTo.CanRead(await remoteObj.Id())) throw new Exception("Cannot reply to something you cannot read!");
                var replies = new Collection(await inReplyTo.Prop("replies") ?? new JsonObject(), new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
                if (!await replies.HasMember(ao))
                {
                    await replies.Prepend(ao);
                }
            }
        }
    }

    private async Task RemoteApplyDelete(ActivityObject ownerObj, ActivityObject remoteObj)
    {
        if (await Prop("object") is not JsonNode objNode) return;
        var ao = new ActivityObject(objNode, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        var aoOwner = await ao.Owner();
        if (aoOwner != null && await aoOwner.Id() != await remoteObj.Id()) throw new Exception("Cannot delete something you do not own!");

        await ao.ClearCache();
    }

    private async Task RemoteApplyLike(ActivityObject ownerObj, ActivityObject remoteObj)
    {
        if (await Prop("object") is not JsonNode objNode) return;
        var ao = new ActivityObject(objNode, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        var aoOwner = await ao.Owner();
        if (!await User.IsUser(aoOwner, AppState.Db!)) return;
        if (!await ao.CanRead(await remoteObj.Id())) throw new Exception("Cannot like something you cannot read!");

        await ao.Expand();
        var likes = new Collection(await ao.Prop("likes") ?? new JsonObject(), new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        if (!await likes.HasMember(this))
        {
            await likes.Prepend(this);
        }
    }

    private async Task RemoteApplyAnnounce(ActivityObject ownerObj, ActivityObject remoteObj)
    {
        if (await Prop("object") is not JsonNode objNode) return;
        var ao = new ActivityObject(objNode, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        var aoOwner = await ao.Owner();
        if (!await User.IsUser(aoOwner, AppState.Db!)) return;
        if (!await ao.CanRead(await remoteObj.Id())) throw new Exception("Cannot share something you cannot read!");

        await ao.Expand();
        var shares = new Collection(await ao.Prop("shares") ?? new JsonObject(), new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        if (!await shares.HasMember(this))
        {
            await shares.Prepend(this);
        }
    }

    private async Task RemoteApplyAdd(ActivityObject ownerObj, ActivityObject remoteObj)
    {
        if (await Prop("object") is not JsonNode objNode) return;
        var ao = new ActivityObject(objNode, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        var aoOwner = await ao.Owner();
        if (await User.IsUser(aoOwner, AppState.Db!))
        {
            if (!await ao.CanRead(await remoteObj.Id())) throw new Exception("Cannot add something you cannot read!");
        }

        var targetProp = await Prop("target");
        if (targetProp != null)
        {
            var target = new ActivityObject(targetProp, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
            var targetOwner = await target.Owner();
            if (await User.IsUser(targetOwner, AppState.Db!))
            {
                if (!await target.CanRead(await remoteObj.Id())) throw new Exception("Cannot add to something you cannot read!");
                if (!await target.CanWrite(await remoteObj.Id())) throw new Exception("Cannot add to something you do not own!");
            }
            else
            {
                await target.ClearCache();
            }
        }
    }

    private async Task RemoteApplyRemove(ActivityObject ownerObj, ActivityObject remoteObj)
    {
        if (await Prop("object") is not JsonNode objNode) return;
        var ao = new ActivityObject(objNode, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        var aoOwner = await ao.Owner();
        if (await User.IsUser(aoOwner, AppState.Db!))
        {
            if (!await ao.CanRead(await remoteObj.Id())) throw new Exception("Cannot remove something you cannot read!");
        }

        var targetProp = (await Prop("target")) ?? (await Prop("origin"));
        if (targetProp != null)
        {
            var target = new ActivityObject(targetProp, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
            var targetOwner = await target.Owner();
            if (await User.IsUser(targetOwner, AppState.Db!))
            {
                if (!await target.CanRead(await remoteObj.Id())) throw new Exception("Cannot remove from something you cannot read!");
                if (!await target.CanWrite(await remoteObj.Id())) throw new Exception("Cannot remove from something you do not own!");
            }
            else
            {
                await target.ClearCache();
            }
        }
    }

    private async Task RemoteApplyAccept(ActivityObject ownerObj, ActivityObject remoteObj)
    {
        if (await Prop("object") is not JsonNode objNode) return;
        var accepted = new ActivityObject(objNode, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        var accType = await accepted.Type();
        if (accType == "Follow")
        {
            var actorProp = await accepted.Prop("actor");
            if (actorProp == null) throw new Exception("No actor!");
            var actor = new ActivityObject(actorProp, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });

            var objectProp = await accepted.Prop("object");
            if (objectProp == null) throw new Exception("No object!");
            var obj = new ActivityObject(objectProp, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });

            if (await actor.Id() == await ownerObj.Id() && await obj.Id() == await remoteObj.Id())
            {
                var pendingFollowing = new Collection(await ownerObj.Prop("pendingFollowing") ?? new JsonObject(), new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
                if (!await pendingFollowing.HasMember(await accepted.Id())) throw new Exception("Not pending!");

                var following = new Collection(await ownerObj.Prop("following") ?? new JsonObject(), new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
                if (await following.HasMember(await obj.Id())) throw new Exception("Already following!");

                await pendingFollowing.Remove(accepted);
                await following.Prepend(obj);
            }
        }
    }

    private async Task RemoteApplyReject(ActivityObject ownerObj, ActivityObject remoteObj)
    {
        if (await Prop("object") is not JsonNode objNode) return;
        var rejected = new ActivityObject(objNode, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        var rejType = await rejected.Type();
        if (rejType == "Follow")
        {
            var actorProp = await rejected.Prop("actor");
            if (actorProp == null) throw new Exception("No actor!");
            var actor = new ActivityObject(actorProp, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });

            var objectProp = await rejected.Prop("object");
            if (objectProp == null) throw new Exception("No object!");
            var obj = new ActivityObject(objectProp, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });

            if (await actor.Id() == await ownerObj.Id() && await obj.Id() == await remoteObj.Id())
            {
                var pendingFollowing = new Collection(await ownerObj.Prop("pendingFollowing") ?? new JsonObject(), new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
                if (!await pendingFollowing.HasMember(await rejected.Id())) throw new Exception("Not pending!");

                var following = new Collection(await ownerObj.Prop("following") ?? new JsonObject(), new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
                if (await following.HasMember(await obj.Id())) throw new Exception("Already following!");

                await pendingFollowing.Remove(rejected);
            }
        }
    }

    private async Task RemoteApplyUndo(ActivityObject ownerObj, ActivityObject remoteObj)
    {
        if (await Prop("object") is not JsonNode objNode) return;
        var undone = new ActivityObject(objNode, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        var actorProp = await undone.Prop("actor");
        if (actorProp == null) throw new Exception("No actor!");
        var undoneActor = new ActivityObject(actorProp, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
        if (await remoteObj.Id() != await undoneActor.Id()) throw new Exception("Not your activity to undo!");

        var type = await undone.Type();
        switch (type)
        {
            case "Like":
                {
                    var objectProp = await undone.Prop("object");
                    if (objectProp == null) throw new Exception("Nothing liked");
                    var obj = new ActivityObject(objectProp, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
                    if (!await obj.CanRead(await remoteObj.Id())) throw new Exception("Cannot unlike something you cannot read!");

                    var objectOwner = await obj.Owner();
                    if (await User.IsUser(objectOwner, AppState.Db!))
                    {
                        await obj.Expand();
                        var likes = new Collection(await obj.Prop("likes") ?? new JsonObject(), new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
                        await likes.Remove(undone);
                    }
                    break;
                }
            case "Announce":
                {
                    var objectProp = await undone.Prop("object");
                    if (objectProp == null) throw new Exception("Nothing announced");
                    var obj = new ActivityObject(objectProp, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
                    if (!await obj.CanRead(await remoteObj.Id())) throw new Exception("Cannot unshare something you cannot read!");

                    var objectOwner = await obj.Owner();
                    if (await User.IsUser(objectOwner, AppState.Db!))
                    {
                        await obj.Expand();
                        var shares = new Collection(await obj.Prop("shares") ?? new JsonObject(), new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
                        await shares.Remove(undone);
                    }
                    break;
                }
            case "Follow":
                {
                    var objectProp = await undone.Prop("object");
                    if (objectProp == null) throw new Exception("Nothing followed");
                    var obj = new ActivityObject(objectProp, new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
                    if (!await obj.CanRead(await remoteObj.Id())) throw new Exception("Cannot unfollow something you cannot read!");

                    var objectOwner = await obj.Owner();
                    if (await User.IsUser(objectOwner, AppState.Db!))
                    {
                        var followers = new Collection(await obj.Prop("followers") ?? new JsonObject(), new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
                        await followers.Remove(undoneActor);
                        var pendingFollowers = new Collection(await obj.Prop("pendingFollowers") ?? new JsonObject(), new FetchOptions { Subject = ownerObj, Cache = _cache, Counter = _counter });
                        await pendingFollowers.Remove(undone);
                    }
                    break;
                }
        }
    }
}

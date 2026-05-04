using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fedi;

public class Collection : ActivityObject
{
    public Collection(object? data, FetchOptions? options = null) : base(data, options) { }

    public static new async Task<Collection?> Get(object? reference, FetchOptions? options = null)
    {
        var ao = await ActivityObject.Get(reference, options);
        if (ao != null)
        {
            return new Collection(await ao.Json(), options);
        }
        return null;
    }

    public async Task<bool> HasMember(object? obj, object? subject = null)
    {
        await EnsureCompleteAsync();
        var objectId = await Helpers.ToId(obj);
        if (objectId == null) return false;

        var match = (JsonNode? item) =>
        {
            if (item is JsonValue jv && jv.TryGetValue<string>(out var s))
                return s == objectId;
            if (item is JsonObject jo && jo.ContainsKey("id"))
                return jo["id"]?.GetValue<string>() == objectId;
            return false;
        };

        if (await HasProp("orderedItems"))
        {
            var orderedItems = await Prop("orderedItems");
            if (orderedItems is JsonArray oa)
                return oa.Any(i => match(i));
        }
        else if (await HasProp("items"))
        {
            var items = await Prop("items");
            if (items is JsonArray ia)
                return ia.Any(i => match(i));
        }
        else if (await HasProp("first"))
        {
            var firstId = (await Prop("first"))?.GetValue<string>();
            while (firstId != null)
            {
                var page = new ActivityObject(firstId);
                await page.EnsureCompleteAsync();
                var pageJson = await page.Json();
                if (pageJson.ContainsKey("orderedItems") && pageJson["orderedItems"] is JsonArray poa)
                {
                    if (poa.Any(i => match(i))) return true;
                }
                else if (pageJson.ContainsKey("items") && pageJson["items"] is JsonArray pia)
                {
                    if (pia.Any(i => match(i))) return true;
                }
                firstId = pageJson["next"]?.GetValue<string>();
            }
            return false;
        }
        return false;
    }

    public async Task PrependData(JsonObject data)
    {
        if (data == null) throw new ArgumentException("No data to prepend");
        if (!data.ContainsKey("id")) throw new ArgumentException("Cannot prepend data without an id");
        await Prepend(new ActivityObject(data));
    }

    public async Task Prepend(object? obj)
    {
        await EnsureCompleteAsync();
        var collection = await Json();
        var objectId = await Helpers.ToId(obj);
        if (objectId == null) throw new ArgumentException("Cannot prepend object without id");

        if (collection.ContainsKey("orderedItems"))
        {
            var orderedItems = collection["orderedItems"] as JsonArray ?? new JsonArray();
            var patch = new JsonObject
            {
                ["totalItems"] = (collection["totalItems"]?.GetValue<int>() ?? 0) + 1,
                ["orderedItems"] = new JsonArray(new[] { JsonValue.Create(objectId) }.Concat(orderedItems).ToArray())
            };
            await Patch(patch);
        }
        else if (collection.ContainsKey("items"))
        {
            var items = collection["items"] as JsonArray ?? new JsonArray();
            var patch = new JsonObject
            {
                ["totalItems"] = (collection["totalItems"]?.GetValue<int>() ?? 0) + 1,
                ["items"] = new JsonArray(new[] { JsonValue.Create(objectId) }.Concat(items).ToArray())
            };
            await Patch(patch);
        }
        else if (collection.ContainsKey("first"))
        {
            var first = new ActivityObject(collection["first"]!);
            await first.EnsureCompleteAsync();
            var firstJson = await first.Json();
            var ip = new[] { "orderedItems", "items" }.FirstOrDefault(p => firstJson.ContainsKey(p));
            if (ip == null) throw new Exception("No items or orderedItems in first page");

            var arr = firstJson[ip] as JsonArray ?? new JsonArray();
            if (arr.Count < Constants.MaxPageSize)
            {
                var patch = new JsonObject();
                patch[ip] = new JsonArray(new[] { JsonValue.Create(objectId) }.Concat(arr).ToArray());
                await first.Patch(patch);
                await Patch(new JsonObject { ["totalItems"] = (collection["totalItems"]?.GetValue<int>() ?? 0) + 1 });
            }
            else
            {
                var attributedTo = await Prop("attributedTo");
                if (attributedTo == null) throw new Exception($"No owner for collection {await Id()}");

                var props = new JsonObject
                {
                    ["type"] = firstJson["type"],
                    ["partOf"] = await Id(),
                    ["next"] = firstJson["id"],
                    ["attributedTo"] = JsonValue.Create(await Helpers.ToId(attributedTo) ?? "")
                };
                await CopyAddresseeProps(props, collection);
                props[ip] = new JsonArray { JsonValue.Create(objectId) };
                var newFirst = new ActivityObject(props);
                await newFirst.Save();
                await Patch(new JsonObject
                {
                    ["totalItems"] = (collection["totalItems"]?.GetValue<int>() ?? 0) + 1,
                    ["first"] = await newFirst.Id()
                });
                await first.Patch(new JsonObject { ["prev"] = await newFirst.Id() });
            }
        }
    }

    public async Task Remove(object? obj)
    {
        var collection = await Expanded();
        var objectId = await Helpers.ToId(obj);
        if (objectId == null) return;

        if (collection["orderedItems"] is JsonArray oa)
        {
            var i = oa.Select((n, idx) => new { n, idx }).FirstOrDefault(x =>
                (x.n is JsonValue jv && jv.TryGetValue<string>(out var s) && s == objectId) ||
                (x.n is JsonObject jo && jo["id"]?.GetValue<string>() == objectId))?.idx ?? -1;
            if (i != -1)
            {
                oa.RemoveAt(i);
                await Patch(new JsonObject
                {
                    ["totalItems"] = (collection["totalItems"]?.GetValue<int>() ?? 0) - 1,
                    ["orderedItems"] = oa
                });
            }
        }
        else if (collection["items"] is JsonArray ia)
        {
            var i = ia.Select((n, idx) => new { n, idx }).FirstOrDefault(x =>
                (x.n is JsonValue jv && jv.TryGetValue<string>(out var s) && s == objectId) ||
                (x.n is JsonObject jo && jo["id"]?.GetValue<string>() == objectId))?.idx ?? -1;
            if (i != -1)
            {
                ia.RemoveAt(i);
                await Patch(new JsonObject
                {
                    ["totalItems"] = (collection["totalItems"]?.GetValue<int>() ?? 0) - 1,
                    ["items"] = ia
                });
            }
        }
        else
        {
            var refId = collection["first"]?.GetValue<string>();
            while (refId != null)
            {
                var page = new ActivityObject(refId);
                var json = await page.Expanded();
                foreach (var prop in new[] { "items", "orderedItems" })
                {
                    if (json[prop] is JsonArray pa)
                    {
                        var i = pa.Select((n, idx) => new { n, idx }).FirstOrDefault(x =>
                            (x.n is JsonValue jv && jv.TryGetValue<string>(out var s) && s == objectId) ||
                            (x.n is JsonObject jo && jo["id"]?.GetValue<string>() == objectId))?.idx ?? -1;
                        if (i != -1)
                        {
                            pa.RemoveAt(i);
                            var patch = new JsonObject { [prop] = pa };
                            await page.Patch(patch);
                            await Patch(new JsonObject { ["totalItems"] = (collection["totalItems"]?.GetValue<int>() ?? 0) - 1 });
                            return;
                        }
                    }
                }
                refId = json["next"]?.GetValue<string>();
            }
        }
    }

    public async Task<List<string>> Members()
    {
        await Expand();
        if (await HasProp("orderedItems"))
        {
            var oi = await Prop("orderedItems");
            if (oi is JsonArray oa)
                return oa.Select(n => n?.GetValue<string>()!).Where(s => s != null).ToList();
        }
        else if (await HasProp("items"))
        {
            var it = await Prop("items");
            if (it is JsonArray ia)
                return ia.Select(n => n?.GetValue<string>()!).Where(s => s != null).ToList();
        }
        else if (await HasProp("first"))
        {
            var members = new List<string>();
            var refId = (await Prop("first"))?.GetValue<string>();
            while (refId != null)
            {
                var page = new ActivityObject(refId);
                await page.Expand();
                if (await page.HasProp("orderedItems"))
                {
                    var oi = await page.Prop("orderedItems");
                    if (oi is JsonArray oa)
                        members.AddRange(oa.Select(n => n?.GetValue<string>()!).Where(s => s != null));
                }
                else if (await page.HasProp("items"))
                {
                    var it = await page.Prop("items");
                    if (it is JsonArray ia)
                        members.AddRange(ia.Select(n => n?.GetValue<string>()!).Where(s => s != null));
                }
                refId = (await page.Prop("next"))?.GetValue<string>();
            }
            return members;
        }
        return new List<string>();
    }

    public static async Task<Collection> Empty(object? owner, List<string>? addressees, JsonObject? props = null, JsonObject? pageProps = null)
    {
        var id = await ActivityObject.MakeId("OrderedCollection");
        var ownerId = await Helpers.ToId(owner);
        if (ownerId == null) throw new ArgumentException("Owner required");

        var page = new ActivityObject(new JsonObject
        {
            ["type"] = "OrderedCollectionPage",
            ["orderedItems"] = new JsonArray(),
            ["partOf"] = id,
            ["attributedTo"] = ownerId,
            ["to"] = addressees != null ? new JsonArray(addressees.Select(s => JsonValue.Create(s)).ToArray()) : new JsonArray()
        });
        if (pageProps != null)
        {
            foreach (var p in pageProps)
                page = new ActivityObject(new JsonObject { [p.Key] = p.Value });
        }
        await page.Save();

        var coll = new ActivityObject(new JsonObject
        {
            ["id"] = id,
            ["type"] = "OrderedCollection",
            ["totalItems"] = 0,
            ["first"] = await page.Id(),
            ["last"] = await page.Id(),
            ["attributedTo"] = ownerId,
            ["to"] = addressees != null ? new JsonArray(addressees.Select(s => JsonValue.Create(s)).ToArray()) : new JsonArray()
        });
        if (props != null)
        {
            foreach (var p in props)
                await coll.SetProp(p.Key, p.Value);
        }
        await coll.Save();
        return new Collection(await coll.Json());
    }

    public async Task<JsonObject?> Find(Func<ActivityObject, Task<bool>> test)
    {
        var refId = (await Prop("first"))?.GetValue<string>();
        while (refId != null)
        {
            var page = new ActivityObject(refId);
            if (!await page.IsCollectionPage()) break;
            var items = (await page.Prop("items") ?? await page.Prop("orderedItems")) as JsonArray ?? new JsonArray();
            foreach (var item in items)
            {
                var itemObj = new ActivityObject(item!);
                var result = await test(itemObj);
                if (result) return await itemObj.Json();
            }
            refId = (await page.Prop("next"))?.GetValue<string>();
        }
        return null;
    }

    public override string DefaultType() => "OrderedCollection";

    private async Task ApplyAddresseeProps(JsonObject to, JsonObject from)
    {
        foreach (var prop in new[] { "to", "cc", "bto", "bcc", "audience" })
        {
            if (from.ContainsKey(prop))
                to[prop] = Helpers.DeepCopy(from[prop]!);
        }
    }
}

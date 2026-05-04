using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fedi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(o => { o.Cookie.Name = "fedi.session"; o.IdleTimeout = TimeSpan.FromDays(1); o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Lax; });
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader().SetPreflightMaxAge(TimeSpan.FromSeconds(86400))));

var app = builder.Build();

app.UseCors();
app.UseSession();

var config = new AppConfiguration
{
    Database = Environment.GetEnvironmentVariable("OPP_DATABASE") ?? ":memory:",
    Hostname = Environment.GetEnvironmentVariable("OPP_HOSTNAME") ?? "localhost",
    Port = int.TryParse(Environment.GetEnvironmentVariable("OPP_PORT"), out var p1) ? p1 : 65380,
    Key = Environment.GetEnvironmentVariable("OPP_KEY") ?? "localhost.key",
    Cert = Environment.GetEnvironmentVariable("OPP_CERT") ?? "localhost.crt",
    LogLevel = Environment.GetEnvironmentVariable("OPP_LOG_LEVEL") ?? "information",
    SessionSecret = Environment.GetEnvironmentVariable("OPP_SESSION_SECRET") ?? "insecure-session-secret",
    InviteCode = Environment.GetEnvironmentVariable("OPP_INVITE_CODE"),
    BlockList = Environment.GetEnvironmentVariable("OPP_BLOCK_LIST"),
    Origin = Environment.GetEnvironmentVariable("OPP_ORIGIN"),
    Name = Environment.GetEnvironmentVariable("OPP_NAME"),
    UploadDir = Environment.GetEnvironmentVariable("OPP_UPLOAD_DIR"),
    SqliteCache = int.TryParse(Environment.GetEnvironmentVariable("OPP_SQLITE3_CACHE"), out var sc) ? sc : 16384
};

AppState.Origin = config.Origin ?? (config.Port == 443 ? $"https://{config.Hostname}" : $"https://{config.Hostname}:{config.Port}");
AppState.Name = config.Name ?? new Uri(AppState.Origin).Host;
AppState.InviteCode = config.InviteCode;
AppState.UploadDir = !string.IsNullOrEmpty(config.UploadDir) ? config.UploadDir : Path.Combine(Path.GetTempPath(), Nanoid.Generate());
AppState.Logger = app.Logger;
AppState.BlockedDomains.Clear();
if (!string.IsNullOrEmpty(config.BlockList) && File.Exists(config.BlockList))
{
    foreach (var line in await File.ReadAllLinesAsync(config.BlockList))
    {
        var f = line.Split(',');
        if (f.Length > 0 && !string.IsNullOrWhiteSpace(f[0])) AppState.BlockedDomains.Add(f[0].Trim());
    }
}
Directory.CreateDirectory(AppState.UploadDir);

var db = new Database(config.Database);
AppState.Db = db;
await db.InitAsync(config);
await ServerModel.EnsureKeyAsync(db, AppState.Origin);
var server = await ServerModel.GetAsync(db);
if (server == null) throw new Exception("Server not initialized");

using var rsa = RSA.Create(); rsa.ImportFromPem(server.PrivateKey().ToCharArray());
var rsaKey = new RsaSecurityKey(rsa);
var tokenValidationParameters = new TokenValidationParameters
{
    ValidateIssuerSigningKey = true, IssuerSigningKey = rsaKey, ValidateIssuer = false, ValidateAudience = false, ValidateLifetime = true, ClockSkew = TimeSpan.Zero
};

app.Use(async (ctx, next) =>
{
    var ct = ctx.Request.ContentType ?? "";
    if (ct.Contains("json") || ct.Contains("ld+json"))
    {
        ctx.Request.EnableBuffering();
        using var r = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
        var b = await r.ReadToEndAsync();
        ctx.Items["rawBodyText"] = b;
        ctx.Request.Body.Position = 0;
    }
    await next();
});

app.Use(async (ctx, next) =>
{
    var c = new Counter();
    c.Set("db", "dur", 0); c.Set("db", "count", 0); c.Set("http", "dur", 0); c.Set("http", "count", 0);
    c.Set("crypto", "dur", 0); c.Set("crypto", "count", 0); c.Set("json", "dur", 0); c.Set("json", "count", 0);
    c.Set("app", "dur", 0); c.Set("cache", "dur", 0); c.Set("cache", "hit", 0); c.Set("cache", "miss", 0);
    ctx.Items["counter"] = c;
    ctx.Items["cache"] = new Dictionary<string, ActivityObject>();
    var st = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var oldBody = ctx.Response.Body;
    using var ms = new MemoryStream();
    ctx.Response.Body = ms;
    try { await next(); }
    finally
    {
        var et = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        c.Add("app", "dur", (int)(et - st));
        ctx.Response.Headers.Append("Server-Timing", c.ToHeader());
        ms.Seek(0, SeekOrigin.Begin);
        await ms.CopyToAsync(oldBody);
        ctx.Response.Body = oldBody;
    }
});

app.Use(async (ctx, next) =>
{
    await next();
    var c = ctx.Items["counter"] as Counter;
    var sub = (ctx.Items["auth"] as JwtAuth)?.Subject ?? "-";
    app.Logger.LogInformation("{Status} {Method} {Path} subject={Subject} dur={Dur}", ctx.Response.StatusCode, ctx.Request.Method, ctx.Request.Path, sub, c?.Get("app", "dur") ?? 0);
});

// helpers
JwtAuth? GetJwtAuth(HttpContext ctx)
{
    var h = ctx.Request.Headers.Authorization.FirstOrDefault();
    if (string.IsNullOrEmpty(h) || !h.StartsWith("Bearer ")) return null;
    try
    {
        var handler = new JwtSecurityTokenHandler();
        var prin = handler.ValidateToken(h[7..], tokenValidationParameters, out _);
        return new JwtAuth { Subject = prin.FindFirst("sub")?.Value, Type = prin.FindFirst("type")?.Value, Scope = prin.FindFirst("scope")?.Value };
    }
    catch { return null; }
}
string GenerateJwt(string type, string sub, string scope, string? clientId = null, string? redir = null, string? challenge = null)
{
    var claims = new List<Claim> { new("jti", Nanoid.Generate()), new("type", type), new("sub", sub), new("scope", scope), new("iss", AppState.Origin) };
    if (clientId != null) claims.Add(new Claim("client", clientId));
    if (redir != null) claims.Add(new Claim("redir", redir));
    if (challenge != null) claims.Add(new Claim("challenge", challenge));
    var exp = type switch { "authz" => DateTime.UtcNow.AddMinutes(10), "access" => DateTime.UtcNow.AddDays(1), "refresh" => DateTime.UtcNow.AddDays(30), _ => DateTime.UtcNow.AddHours(1) };
    var token = new JwtSecurityToken(claims: claims, expires: exp, signingCredentials: new SigningCredentials(rsaKey, SecurityAlgorithms.RsaSha256));
    return new JwtSecurityTokenHandler().WriteToken(token);
}
User? GetSessionUser(HttpContext ctx) => ctx.Session.GetString("user") is string u ? User.FromUsername(u, db).GetAwaiter().GetResult() : null;
void SetSessionUser(HttpContext ctx, User user) => ctx.Session.SetString("user", user.Username);
void RemoveSessionUser(HttpContext ctx) => ctx.Session.Remove("user");
string CsrfToken(HttpContext ctx) => ctx.Session.GetString("csrfToken") ?? Nanoid.Generate();

string Page(string title, string body, User? user = null)
{
    var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString();
    return $"<!DOCTYPE html><html><head><title>{title} - {AppState.Name}</title><link rel=\"stylesheet\" href=\"/bootstrap/css/bootstrap.min.css\"><style>.outer{{margin-bottom:100px}}.footer{{position:absolute;bottom:0;width:100%;padding:10px 0}}</style></head><body>" +
    $"<div class=\"container mx-auto outer\" style=\"max-width:600px;\"><nav class=\"navbar navbar-expand-lg navbar-light bg-light\"><a class=\"navbar-brand\" href=\"/\">{AppState.Name}</a><div class=\"collapse navbar-collapse\"><ul class=\"navbar-nav\">" +
    $"<li class=\"nav-item\"><a class=\"nav-link\" href=\"/\">Home</a></li>" +
    (user != null
        ? "<li class=\"nav-item\"><form action=\"/logout\" method=\"POST\" class=\"form-inline\"><button type=\"submit\" class=\"btn btn-link nav-link\">Logout</button></form></li>"
        : "<li class=\"nav-item\"><a class=\"nav-link\" href=\"/register\">Register</a></li><li class=\"nav-item\"><a class=\"nav-link\" href=\"/login\">Log in</a></li>") +
    $"</ul></div></nav><div class=\"container\"><div class=\"row\"><div class=\"col\"><h1>{title}</h1>{body}</div></div></div>" +
    $"<div class=\"footer bg-light\" style=\"max-width:600px;\"><div class=\"container text-center\"><p>One Page Pub {(version != null ? $"<span class=\"version\">{version}</span>" : "")} | <a href=\"https://github.com/evanp/onepage.pub\" target=\"_blank\">GitHub</a></p></div></div></div>" +
    "<script src=\"/popper/popper.min.js\"></script><script src=\"/bootstrap/js/bootstrap.min.js\"></script></body></html>";
}

// static files
app.UseStaticFiles();

// CSRF
app.Use(async (ctx, next) =>
{
    if (ctx.Session.GetString("csrfToken") == null) ctx.Session.SetString("csrfToken", Nanoid.Generate());
    await next();
});

// Routes
app.MapGet("/", async (HttpContext ctx) =>
{
    if (ctx.Request.Headers.Accept.ToString().Contains("text/html"))
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.WriteAsync(Page("Home", "<p>This is an <a href=\"https://www.w3.org/TR/activitypub/\">ActivityPub</a> server.</p><p>It is currently in development.</p>"));
    }
    else if (ctx.Request.Headers.Accept.ToString().Contains("json") || ctx.Request.Headers.Accept.ToString().Contains("activity+json") || ctx.Request.Headers.Accept.ToString().Contains("ld+json"))
    {
        ctx.Response.ContentType = "application/activity+json";
        await ctx.Response.WriteAsJsonAsync(server.ToJson(AppState.Name));
    }
    else { ctx.Response.StatusCode = 406; await ctx.Response.WriteAsync("Not Acceptable"); }
});

app.MapGet("/key", (HttpContext ctx) => { ctx.Response.ContentType = "application/activity+json"; return Results.Json(server.GetKeyJson()); });

app.MapGet("/queue", () => new { count = 0 });

app.MapGet("/register", (HttpContext ctx) =>
{
    var invite = !string.IsNullOrEmpty(AppState.InviteCode)
        ? "<div class=\"form-group row mb-3\"><label class=\"col-sm-4 col-form-label text-right\">Invite code</label><div class=\"col-sm-8\"><input type=\"text\" name=\"invitecode\" class=\"form-control\" placeholder=\"Invite code\" /></div></div>"
        : "";
    var body = $"<div class=\"container mx-auto\" style=\"max-width:600px;\"><form method=\"POST\" action=\"/register\">{invite}<div class=\"form-group row mb-3\"><label class=\"col-sm-4 col-form-label text-right\">Username</label><div class=\"col-sm-8\"><input type=\"text\" name=\"username\" class=\"form-control\" placeholder=\"Username\" /></div></div><div class=\"form-group row mb-3\"><label class=\"col-sm-4 col-form-label text-right\">Password</label><div class=\"col-sm-8\"><input type=\"password\" name=\"password\" class=\"form-control\"></div></div><div class=\"form-group row mb-3\"><label class=\"col-sm-4 col-form-label text-right\">Confirm</label><div class=\"col-sm-8\"><input type=\"password\" name=\"confirmation\" class=\"form-control\"></div></div><div class=\"form-group row\"><div class=\"col-sm-4\"></div><div class=\"col-sm-8\"><button type=\"submit\" class=\"btn btn-primary\">Register</button><a href='/' class=\"btn btn-secondary\">Cancel</a></div></div></form></div>";
    ctx.Response.ContentType = "text/html";
    return Results.Content(Page("Register", body), "text/html");
});

app.MapPost("/register", async (HttpContext ctx, [FromForm] string username, [FromForm] string password, [FromForm] string confirmation, [FromForm] string? invitecode) =>
{
    if (string.IsNullOrEmpty(username)) throw new BadHttpRequestException("Username is required");
    if (string.IsNullOrEmpty(password)) throw new BadHttpRequestException("Password is required");
    if (password != confirmation) throw new BadHttpRequestException("Passwords do not match");
    if (!string.IsNullOrEmpty(AppState.InviteCode) && invitecode != AppState.InviteCode) throw new BadHttpRequestException("Correct invite code required");
    if (await User.UsernameExists(username, db)) throw new BadHttpRequestException("Username already exists");
    var user = new User(username, password);
    await user.Save(db);
    var token = GenerateJwt("access", user.ActorId!, "read write");
    SetSessionUser(ctx, user);
    ctx.Response.ContentType = "text/html";
    await ctx.Response.WriteAsync(Page("Registered", $"<p>Registered <a class=\"actor\" href=\"{user.ActorId}\">{username}</a></p><p>Personal access token is <span class=\"token\">{token}</span></p>", user));
});

app.MapGet("/login", (HttpContext ctx) =>
{
    var body = "<div class=\"container mx-auto\" style=\"max-width:600px;\"><form method=\"POST\" action=\"/login\"><div class=\"form-group row mb-3\"><label class=\"col-sm-4 col-form-label text-right\">Username</label><div class=\"col-sm-8\"><input type=\"text\" name=\"username\" class=\"form-control\" placeholder=\"Username\" /></div></div><div class=\"form-group row mb-3\"><label class=\"col-sm-4 col-form-label text-right\">Password</label><div class=\"col-sm-8\"><input type=\"password\" name=\"password\" class=\"form-control\"></div></div><div class=\"form-group row\"><div class=\"col-sm-4\"></div><div class=\"col-sm-8\"><button type=\"submit\" class=\"btn btn-primary\">Login</button><a href='/' class=\"btn btn-secondary\">Cancel</a></div></div></form></div>";
    ctx.Response.ContentType = "text/html";
    return Results.Content(Page("Log in", body), "text/html");
});

app.MapPost("/login", async (HttpContext ctx, [FromForm] string username, [FromForm] string password) =>
{
    var user = await User.Authenticate(username, password, db);
    if (user == null) { ctx.Response.Redirect("/login?error=1"); return; }
    SetSessionUser(ctx, user);
    var redirect = ctx.Session.GetString("redirectTo");
    ctx.Session.Remove("redirectTo");
    ctx.Response.Redirect(redirect ?? "/login/success");
});

app.MapGet("/login/success", async (HttpContext ctx) =>
{
    var user = GetSessionUser(ctx);
    if (user == null) throw new BadHttpRequestException("Not authenticated");
    var token = GenerateJwt("access", user.ActorId!, "read write");
    ctx.Response.ContentType = "text/html";
    await ctx.Response.WriteAsync(Page("Logged in", $"<p>Logged in <a class=\"actor\" href=\"{user.ActorId}\">{user.Username}</a></p><p>Personal access token is <span class=\"token\">{token}</span></p>", user));
});

app.MapPost("/logout", (HttpContext ctx) => { RemoveSessionUser(ctx); ctx.Response.Redirect("/"); });

app.MapGet("/live", () => Results.Text("OK"));
app.MapGet("/ready", async () => await db.ReadyAsync() ? Results.Text("OK") : Results.Problem("Database not ready", statusCode: 500));

app.MapGet("/.well-known/webfinger", async (HttpContext ctx, string resource) =>
{
    if (string.IsNullOrEmpty(resource)) throw new BadHttpRequestException("Missing resource");
    object? jrd;
    if (resource.StartsWith("acct:"))
    {
        if (!resource.Contains('@')) throw new BadHttpRequestException("Resource must contain @");
        var parts = resource["acct:".Length..].Split('@');
        var username = parts[0]; var hostname = parts[1];
        if (hostname != ctx.Request.Host.Host) throw new BadHttpRequestException("Hostname does not match");
        if (username == hostname)
        {
            jrd = new { subject = resource, links = new[] { new { rel = "self", type = "application/activity+json", href = $"https://{hostname}/" } } };
        }
        else
        {
            var row = await db.GetRowAsync("SELECT username, actorId FROM user WHERE username = ?", username);
            if (row == null) throw new BadHttpRequestException("User not found");
            jrd = new { subject = resource, links = new[] { new { rel = "self", type = "application/activity+json", href = row["actorId"] } } };
        }
    }
    else if (resource.StartsWith("https:"))
    {
        if (!Uri.TryCreate(resource, UriKind.Absolute, out var uri) || uri.Host != ctx.Request.Host.Host) throw new BadHttpRequestException("Hostname mismatch");
        var user = await User.FromActorId(resource, db);
        if (user == null) throw new BadHttpRequestException("User not found");
        jrd = new { subject = $"acct:{user.Username}@{ctx.Request.Host.Host}", links = new[] { new { rel = "self", type = "application/activity+json", href = user.ActorId } } };
    }
    else throw new BadHttpRequestException("Unsupported protocol");
    ctx.Response.ContentType = "application/jrd+json";
    await ctx.Response.WriteAsJsonAsync(jrd);
});

app.MapGet("/.well-known/oauth-authorization-server", () => Results.Json(new
{
    issuer = AppState.Origin,
    authorization_endpoint = Helpers.MakeUrl("endpoint/oauth/authorize", AppState.Origin),
    token_endpoint = Helpers.MakeUrl("endpoint/oauth/token", AppState.Origin),
    registration_endpoint = Helpers.MakeUrl("endpoint/oauth/registration", AppState.Origin),
    scopes_supported = Constants.ScopesSupported,
    response_types_supported = Constants.ResponseTypesSupported,
    grant_types_supported = Constants.GrantTypesSupported,
    code_challenge_methods_supported = new[] { "S256" },
    token_endpoint_auth_methods_supported = Constants.TokenEndpointAuthMethodsSupported,
    activitypub_universal_client_id = true,
    client_id_metadata_document_supported = true
}));

app.MapPost("/endpoint/proxyUrl", async (HttpContext ctx) =>
{
    var auth = GetJwtAuth(ctx);
    if (auth == null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsync("Unauthorized"); return; }
    if (auth.Type != null && auth.Type != "access") { ctx.Response.StatusCode = 401; return; }
    if (!(auth.Scope?.Split(' ').Contains("read") ?? false)) { ctx.Response.StatusCode = 403; return; }
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var json = JsonNode.Parse(body) as JsonObject ?? new JsonObject();
    var id = json["id"]?.GetValue<string>();
    if (string.IsNullOrEmpty(id)) throw new BadHttpRequestException("Missing id");
    var counter = ctx.Items["counter"] as Counter;
    var cache = ctx.Items["cache"] as Dictionary<string, ActivityObject>;
    var actor = await ActivityObject.Get(auth.Subject, new FetchOptions { Counter = counter, Cache = cache });
    var obj = await ActivityObject.Get(id, new FetchOptions { Subject = actor, Counter = counter, Cache = cache });
    if (obj == null) { ctx.Response.StatusCode = 404; return; }
    ctx.Response.ContentType = "application/activity+json";
    await ctx.Response.WriteAsJsonAsync(await obj.Json());
});

app.MapGet("/endpoint/oauth/authorize", async (HttpContext ctx) =>
{
    var user = GetSessionUser(ctx);
    if (user == null) { ctx.Session.SetString("redirectTo", ctx.Request.GetEncodedPathAndQuery()); ctx.Response.Redirect("/login"); return; }
    var clientId = ctx.Request.Query["client_id"].FirstOrDefault();
    if (string.IsNullOrEmpty(clientId)) throw new BadHttpRequestException("Missing client_id");
    if (!Uri.TryCreate(clientId, UriKind.Absolute, out var cidUri) || cidUri.Scheme != "https")
        throw new BadHttpRequestException("Invalid client_id");
    ActivityObject? client = null;
    if (Helpers.IsPublic(clientId) || ActivityObject.IsRemoteId(clientId))
    {
        try
        {
            using var hc = new HttpClient();
            hc.DefaultRequestHeaders.Add("Accept", Constants.AcceptHeader);
            var resp = await hc.GetAsync(clientId);
            if (!resp.IsSuccessStatusCode) throw new BadHttpRequestException("Invalid client_id");
            var cjson = JsonNode.Parse(await resp.Content.ReadAsStringAsync()) as JsonObject ?? new JsonObject();
            if (cjson.ContainsKey("client_id"))
            {
                throw new NotImplementedException("CIMD client metadata parsing not yet fully ported");
            }
            else if (cjson.ContainsKey("id")) client = new ActivityObject(cjson);
            else throw new BadHttpRequestException("Invalid client_id");
        }
        catch { throw new BadHttpRequestException("Invalid client_id"); }
    }
    else client = await ActivityObject.Get(clientId);
    if (client == null) throw new BadHttpRequestException("Invalid client_id");
    var redirectUri = ctx.Request.Query["redirect_uri"].FirstOrDefault();
    if (string.IsNullOrEmpty(redirectUri)) throw new BadHttpRequestException("Missing redirect_uri");
    if (redirectUri != (await client.Prop("redirectURI"))?.ToString()) throw new BadHttpRequestException("Invalid redirect_uri");
    if (ctx.Request.Query["response_type"].FirstOrDefault() != "code") throw new BadHttpRequestException("Missing or invalid response_type");
    var scope = ctx.Request.Query["scope"].FirstOrDefault();
    if (string.IsNullOrEmpty(scope)) throw new BadHttpRequestException("Missing scope");
    var codeChallenge = ctx.Request.Query["code_challenge"].FirstOrDefault();
    if (string.IsNullOrEmpty(codeChallenge)) throw new BadHttpRequestException("Missing code_challenge");
    if (ctx.Request.Query["code_challenge_method"].FirstOrDefault() != "S256") throw new BadHttpRequestException("Unsupported code challenge value");
    var state = ctx.Request.Query["state"].FirstOrDefault();
    var body = $"<p>This app is asking to authorize access to your account.</p><ul><li>Client ID: {clientId}</li><li>Scope: {scope}</li></ul>" +
        $"<form method=\"POST\" action=\"/endpoint/oauth/authorize\">" +
        $"<input type=\"hidden\" name=\"csrf_token\" value=\"{CsrfToken(ctx)}\" />" +
        $"<input type=\"hidden\" name=\"client_id\" value=\"{clientId}\" />" +
        $"<input type=\"hidden\" name=\"redirect_uri\" value=\"{redirectUri}\" />" +
        $"<input type=\"hidden\" name=\"scope\" value=\"{scope}\" />" +
        $"<input type=\"hidden\" name=\"code_challenge\" value=\"{codeChallenge}\" />" +
        $"<input type=\"hidden\" name=\"state\" value=\"{state}\" />" +
        $"<input type=\"submit\" value=\"Authorize\" /></form>";
    ctx.Response.ContentType = "text/html";
    await ctx.Response.WriteAsync(Page("Authorize", body, user));
});

app.MapPost("/endpoint/oauth/authorize", async (HttpContext ctx, [FromForm] string csrf_token, [FromForm] string client_id, [FromForm] string redirect_uri, [FromForm] string scope, [FromForm] string code_challenge, [FromForm] string? state) =>
{
    var user = GetSessionUser(ctx);
    if (user == null) { ctx.Response.Redirect("/login"); return; }
    if (csrf_token != CsrfToken(ctx)) throw new BadHttpRequestException("Invalid CSRF token");
    var code = GenerateJwt("authz", user.ActorId!, scope, null, redirect_uri, code_challenge);
    var qs = System.Web.HttpUtility.ParseQueryString(string.Empty);
    qs["code"] = code;
    qs["state"] = state ?? "";
    ctx.Response.Redirect(redirect_uri + "?" + qs.ToString());
});

app.MapPost("/endpoint/oauth/token", async (HttpContext ctx) =>
{
    var ct = ctx.Request.ContentType ?? "";
    if (!ct.Contains("application/x-www-form-urlencoded")) throw new BadHttpRequestException("Invalid Content-Type");
    var form = await ctx.Request.ReadFormAsync();
    var grantType = form["grant_type"].FirstOrDefault();
    if (grantType != "authorization_code" && grantType != "refresh_token") throw new BadHttpRequestException("Invalid grant_type");
    if (grantType == "authorization_code")
    {
        var code = form["code"].FirstOrDefault();
        var redirectUri = form["redirect_uri"].FirstOrDefault();
        var clientId = form["client_id"].FirstOrDefault();
        var codeVerifier = form["code_verifier"].FirstOrDefault();
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(redirectUri) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(codeVerifier))
            throw new BadHttpRequestException("Missing parameters");
        var handler = new JwtSecurityTokenHandler();
        var principal = handler.ValidateToken(code, tokenValidationParameters, out _);
        var fields = principal.Claims.ToDictionary(c => c.Type, c => c.Value);
        if (fields.GetValueOrDefault("type") != "authz") throw new BadHttpRequestException("Invalid code");
        if (fields.GetValueOrDefault("client") != clientId) throw new BadHttpRequestException("Invalid client");
        if (fields.GetValueOrDefault("redir") != redirectUri) throw new BadHttpRequestException("Invalid redirect_uri");
        var expectedChallenge = Helpers.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier)));
        if (fields.GetValueOrDefault("challenge") != expectedChallenge) throw new BadHttpRequestException("Invalid code_verifier");
        if (fields.GetValueOrDefault("iss") != AppState.Origin) throw new BadHttpRequestException("Invalid issuer");
        var subject = fields.GetValueOrDefault("sub")!;
        var scope = fields.GetValueOrDefault("scope") ?? "";
        var token = GenerateJwt("access", subject, scope, clientId);
        var refreshToken = GenerateJwt("refresh", subject, scope, clientId);
        return Results.Json(new { access_token = token, token_type = "Bearer", scope, expires_in = 86400, refresh_token = refreshToken });
    }
    else
    {
        var refreshToken = form["refresh_token"].FirstOrDefault();
        if (string.IsNullOrEmpty(refreshToken)) throw new BadHttpRequestException("Missing refresh_token");
        var handler = new JwtSecurityTokenHandler();
        var principal = handler.ValidateToken(refreshToken, tokenValidationParameters, out _);
        var fields = principal.Claims.ToDictionary(c => c.Type, c => c.Value);
        if (fields.GetValueOrDefault("type") != "refresh") throw new BadHttpRequestException("Invalid code");
        if (fields.GetValueOrDefault("iss") != AppState.Origin) throw new BadHttpRequestException("Invalid issuer");
        var subject = fields.GetValueOrDefault("sub")!;
        var scope = fields.GetValueOrDefault("scope") ?? "";
        var clientId = fields.GetValueOrDefault("client");
        var token = GenerateJwt("access", subject, scope, clientId);
        var newRefresh = GenerateJwt("refresh", subject, scope, clientId);
        return Results.Json(new { access_token = token, token_type = "Bearer", scope, expires_in = 86400, refresh_token = newRefresh });
    }
});

app.MapPost("/endpoint/oauth/registration", async (HttpContext ctx) =>
{
    try
    {
        var ct = ctx.Request.ContentType ?? "";
        if (!ct.Contains("application/json")) throw new Exception("Invalid Content-Type");
        var body = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
        if (body == null) throw new Exception("Invalid body");
        // TODO: full clientFromCimd validation (redirect_uris, grant_types, response_types, scope, token_endpoint_auth_method)
        var clientName = body["client_name"]?.GetValue<string>();
        var redirectUris = body["redirect_uris"] is JsonArray arr ? arr.Select(n => n?.GetValue<string>()).Where(s => s != null).ToList() : new List<string?>();
        if (redirectUris.Count == 0 || redirectUris.Any(string.IsNullOrEmpty)) throw new Exception("redirect_uris required");
        var client = new ActivityObject(new JsonObject
        {
            ["@context"] = new JsonArray(Constants.Context.Select(c => JsonValue.Create(c)).ToArray()),
            ["name"] = clientName,
            ["redirectURI"] = redirectUris.Count == 1 ? JsonValue.Create(redirectUris[0]) : new JsonArray(redirectUris.Select(u => JsonValue.Create(u)).ToArray()),
            ["attributedTo"] = server.Id(),
            ["to"] = new JsonArray(Constants.Public)
        });
        await client.Save();
        return Results.Json(new { client_id = await client.Id(), redirect_uris = redirectUris, client_name = clientName }, statusCode: 201);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = "invalid_client_metadata", error_description = ex.Message }, statusCode: 400);
    }
});

app.MapPost("/endpoint/upload", async (HttpContext ctx, IFormFile file, [FromForm(Name = "object")] IFormFile objectFile) =>
{
    var auth = GetJwtAuth(ctx);
    if (auth == null) { ctx.Response.StatusCode = 401; return; }
    if (auth.Type != null && auth.Type != "access") { ctx.Response.StatusCode = 401; return; }
    if (!(auth.Scope?.Split(' ').Contains("write") ?? false)) { ctx.Response.StatusCode = 403; return; }
    var owner = new ActivityObject(auth.Subject);
    var ownerId = await owner.Id() ?? throw new BadHttpRequestException("Invalid owner");
    using var fs = file.OpenReadStream();
    using var ms = new MemoryStream();
    await fs.CopyToAsync(ms);
    var uploaded = new Upload(ms.ToArray(), file.ContentType);
    var objectStr = await new StreamReader(objectFile.OpenReadStream()).ReadToEndAsync();
    var data = JsonNode.Parse(objectStr) as JsonObject ?? new JsonObject();
    var type = data["type"]?.GetValue<string>() ?? "";
    if (ActivityObject.IsActivityType(type)) { /* ok */ }
    else if (ActivityObject.IsObjectType(type)) data = new JsonObject { ["type"] = "Create", ["object"] = data };
    else if (Activity.DuckType(data))
    {
        data["type"] = data["type"] is JsonValue jv ? new JsonArray(jv, "Activity") : (JsonNode?)"Activity";
    }
    else
    {
        data["type"] = data["type"] is JsonValue jv2 ? new JsonArray(jv2, "Object") : (JsonNode?)"Object";
        data = new JsonObject { ["type"] = "Create", ["object"] = data };
    }
    data["id"] = await ActivityObject.MakeId(data["type"]
        is JsonArray arr2 && arr2.Count > 0 ? arr2[0]?.GetValue<string>() ?? "Activity" :
        data["type"] is JsonValue jv3 ? jv3.GetValue<string>() : "Activity");
    data["actor"] = ownerId;
    if (data["object"] is JsonObject objNode)
    {
        objNode["url"] = new JsonObject
        {
            ["href"] = Helpers.MakeUrl($"/uploads/{uploaded.Relative}", AppState.Origin),
            ["type"] = "Link",
            ["mediaType"] = uploaded.MediaType
        };
    }
    var activity = new Activity(data);
    await activity.SetActor(ownerId);
    await activity.Apply();
    await activity.Save();
    uploaded.ObjectId = await Helpers.ToId(await activity.Prop("object")) ?? "";
    await uploaded.Save(db);
    var outbox = new Collection(await owner.Prop("outbox") ?? new JsonObject(), new FetchOptions { Subject = owner });
    await outbox.Prepend(activity);
    var inbox = new Collection(await owner.Prop("inbox") ?? new JsonObject(), new FetchOptions { Subject = owner });
    await inbox.Prepend(activity);
    _ = activity.Distribute();
    var output = new JsonObject { ["@context"] = new JsonArray(Constants.Context.Select(c => JsonValue.Create(c)).ToArray()) };
    var expanded = await activity.Expanded();
    foreach (var prop in expanded) output[prop.Key] = prop.Value;
    ctx.Response.StatusCode = 201;
    ctx.Response.ContentType = "application/activity+json";
    ctx.Response.Headers.Location = await activity.Id();
    await ctx.Response.WriteAsJsonAsync(output);
});

app.MapGet("/uploads/{*relative}", async (HttpContext ctx, string relative) =>
{
    var auth = GetJwtAuth(ctx);
    if (auth?.Type != null && auth.Type != "access") { ctx.Response.StatusCode = 401; return; }
    var uploaded = await Upload.FromRelative(relative, db);
    if (uploaded == null || !await uploaded.Readable()) { ctx.Response.StatusCode = 404; return; }
    var obj = new ActivityObject(uploaded.ObjectId);
    if (!await obj.CanRead(auth?.Subject))
    {
        ctx.Response.StatusCode = auth?.Subject != null ? 403 : 401;
        return;
    }
    ctx.Response.ContentType = obj.Prop("mediaType").GetAwaiter().GetResult()?.ToString() ?? "application/octet-stream";
    await ctx.Response.SendFileAsync(uploaded.FilePath());
});

app.MapGet("/{type}/{id}", async (HttpContext ctx, string type, string id) =>
{
    var full = Helpers.MakeUrl(ctx.Request.Path, AppState.Origin);
    var counter = ctx.Items["counter"] as Counter;
    var cache = ctx.Items["cache"] as Dictionary<string, ActivityObject>;
    var auth = GetJwtAuth(ctx);
    if (auth?.Type != null && auth.Type != "access") { ctx.Response.StatusCode = 401; return; }
    if (auth?.Scope != null && !auth.Scope.Split(' ').Contains("read")) { ctx.Response.StatusCode = 403; return; }
    var options = new FetchOptions { Subject = auth?.Subject, Counter = counter, Cache = cache };
    var obj = await ActivityObject.Get(full, options);
    if (obj == null) { ctx.Response.StatusCode = 404; return; }
    if (!await obj.CanRead(auth?.Subject))
    {
        ctx.Response.StatusCode = auth?.Subject != null ? 403 : 401;
        return;
    }
    var output = await obj.Expanded();
    var itemsProp = new[] { "items", "orderedItems" }.FirstOrDefault(p => output.ContainsKey(p) && output[p] is JsonArray);
    if (itemsProp != null && output[itemsProp] is JsonArray arr)
    {
        var filtered = new JsonArray();
        foreach (var item in arr)
        {
            var itemId = await Helpers.ToId(item);
            if (itemId == null) continue;
            var itemObj = await ActivityObject.Get(itemId, options);
            if (itemObj == null) { filtered.Add(new JsonObject { ["id"] = itemId }); continue; }
            if (await itemObj.CanRead(auth?.Subject)) filtered.Add(await itemObj.Expanded());
        }
        output[itemsProp] = filtered;
    }
    if (await User.IsUser(obj, db))
    {
        output["endpoints"] = new JsonObject(Helpers.StandardEndpoints(AppState.Origin).Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value))));
        foreach (var prop in new[] { "inbox", "outbox", "followers", "following", "liked" })
            if (output.ContainsKey(prop)) output[prop] = JsonValue.Create(await Helpers.ToId(output[prop]));
        var username = await obj.Prop("preferredUsername");
        if (username != null) output["webfinger"] = $"{username}@{new Uri(AppState.Origin).Host}";
        output["universalClientID"] = true;
    }
    if ((await obj.Type()) == "Tombstone") ctx.Response.StatusCode = 410;
    var result = new JsonObject { ["@context"] = new JsonArray(Constants.Context.Select(c => JsonValue.Create(c)).ToArray()) };
    foreach (var prop in output) result[prop.Key] = prop.Value;
    ctx.Response.ContentType = "application/activity+json";
    await ctx.Response.WriteAsJsonAsync(result);
});

app.MapPost("/{type}/{id}", async (HttpContext ctx, string type, string id) =>
{
    var full = Helpers.MakeUrl(ctx.Request.Path, AppState.Origin);
    var obj = new ActivityObject(full);
    var owner = await obj.Owner();
    if (await obj.Json() == null) throw new BadHttpRequestException("Invalid object");
    if (owner == null) throw new BadHttpRequestException("No owner found for object");
    var auth = GetJwtAuth(ctx);
    var sig = ctx.Request.Headers["Signature"].FirstOrDefault();
    var bodyText = ctx.Items["rawBodyText"] as string;
    if (full == await Helpers.ToId(await owner.Prop("outbox")))
    {
        if (auth?.Subject != await owner.Id()) { ctx.Response.StatusCode = 403; return; }
        if (!(auth?.Scope?.Split(' ').Contains("write") ?? false)) { ctx.Response.StatusCode = 403; return; }
        var body = string.IsNullOrEmpty(bodyText) ? new JsonObject() : (JsonNode.Parse(bodyText) as JsonObject ?? new JsonObject());
        var typeStr = body["type"]?.GetValue<string>() ?? "";
        if (ActivityObject.IsActivityType(typeStr)) { }
        else if (ActivityObject.IsObjectType(typeStr)) body = new JsonObject { ["type"] = "Create", ["object"] = body };
        else if (Activity.DuckType(body))
        {
            body["type"] = body["type"] is JsonValue jv ? new JsonArray(jv, "Activity") : (JsonNode?)"Activity";
        }
        else
        {
            body["type"] = body["type"] is JsonValue jv2 ? new JsonArray(jv2, "Object") : (JsonNode?)"Object";
            body = new JsonObject { ["type"] = "Create", ["object"] = body };
        }
        var user = await User.FromActorId(await owner.Id() ?? "", db);
        if (user == null) throw new BadHttpRequestException("User not found");
        var activity = await user.DoActivity(body, db);
        var output = new JsonObject { ["@context"] = new JsonArray(Constants.Context.Select(c => JsonValue.Create(c)).ToArray()) };
        var expanded = await activity.Expanded();
        foreach (var prop in expanded) output[prop.Key] = prop.Value;
        ctx.Response.StatusCode = 201;
        ctx.Response.ContentType = "application/activity+json";
        ctx.Response.Headers.Location = await activity.Id() ?? "";
        await ctx.Response.WriteAsJsonAsync(output);
    }
    else if (full == await Helpers.ToId(await owner.Prop("inbox")))
    {
        if (string.IsNullOrEmpty(sig)) { ctx.Response.StatusCode = 401; return; }
        try
        {
            var httpSig = new HttpSignature(sig);
            var remote = await httpSig.ValidateAsync(ctx, db, true);
            if (remote == null) remote = await httpSig.ValidateAsync(ctx, db, false);
            if (remote == null) { ctx.Response.StatusCode = 401; return; }
            var remoteId = await remote.Id();
            if (string.IsNullOrEmpty(remoteId)) { ctx.Response.StatusCode = 500; return; }
            if (Helpers.DomainIsBlocked(remoteId, AppState.BlockedDomains)) { ctx.Response.StatusCode = 403; return; }
            if (await User.IsUser(remote, db)) { ctx.Response.StatusCode = 403; return; }
            var body = string.IsNullOrEmpty(bodyText) ? new JsonObject() : (JsonNode.Parse(bodyText) as JsonObject ?? new JsonObject());
            var activity = new RemoteActivity(body);
            var actorProp = await activity.Prop("actor") ?? await activity.Prop("attributedTo");
            if (actorProp != null)
            {
                var actorId = await Helpers.ToId(actorProp);
                if (actorId != remoteId) { ctx.Response.StatusCode = 403; return; }
            }
            else await activity.SetActor(remoteId);
            await activity.Apply(null, null, await owner.Json());
            await activity.Cache();
            var inbox = new Collection(await owner.Prop("inbox") ?? new JsonObject(), new FetchOptions { Subject = owner });
            await inbox.Prepend(activity);
            ctx.Response.StatusCode = 202;
            ctx.Response.ContentType = "application/activity+json";
            await ctx.Response.WriteAsJsonAsync(body);
        }
        catch { ctx.Response.StatusCode = 401; return; }
    }
    else throw new BadHttpRequestException("You cannot POST to this object");
});

// Maintenance
var maintenance = new Func<string, Task>[] {
    async n => {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var before = await db.GetScalarAsync<int>("SELECT count(*) FROM remotecache WHERE expires < ?", ts);
        if (before > 0) { await db.RunAsync("DELETE FROM remotecache WHERE expires < ?", ts); app.Logger.LogInformation($"Deleted {before} stale rows from remote cache"); }
    },
    async n => {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var before = await db.GetScalarAsync<int>("SELECT count(*) FROM remote_failure WHERE expires < ?", ts);
        if (before > 0) { await db.RunAsync("DELETE FROM remote_failure WHERE expires < ?", ts); app.Logger.LogInformation($"Deleted {before} stale rows from remote failure"); }
    }
};

void RunMaintenance()
{
    foreach (var m in maintenance) _ = m("");
}

var maintenanceTimer = new Timer(_ => RunMaintenance(), null, TimeSpan.FromMilliseconds(Constants.MaintenanceInterval), TimeSpan.FromMilliseconds(Constants.MaintenanceInterval));

// Fixups
var fixups = new Func<Task>[] {
    () => User.UpdateAllUsers(db),
    () => User.UpdateAllKeys(db),
    () => User.UpdateAllCollections(db),
    async () => {
        app.Logger.LogInformation("Copying addressed remote data from object to remotecache");
        var affected = await db.RunAsync("INSERT OR IGNORE INTO remotecache (id, subject, expires, data, complete) SELECT o.id, a2.addresseeId, ?, o.data, TRUE FROM object o JOIN addressee_2 a2 ON o.id = a2.objectId WHERE o.id NOT LIKE ?", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30L * 24 * 60 * 60 * 1000, $"{AppState.Origin}%");
        app.Logger.LogInformation($"Rows affected: {affected}");
        await db.RunAsync("DELETE FROM addressee_2 WHERE EXISTS (select id from remotecache rc where rc.id = addressee_2.objectId)");
        await db.RunAsync("DELETE FROM object WHERE EXISTS (select id from remotecache rc where rc.id = object.id) AND object.id NOT LIKE ?", $"{AppState.Origin}%");
    }
};

foreach (var f in fixups) _ = Task.Run(async () => { try { await f(); } catch (Exception ex) { app.Logger.LogError(ex, "Fixup failed"); } });

// Cleanup
var cts = new CancellationTokenSource();
app.Lifetime.ApplicationStopping.Register(() =>
{
    app.Logger.LogInformation("Closing database");
    db.Dispose();
    app.Logger.LogInformation("Database closed");
});

// Run
if (string.IsNullOrEmpty(config.Origin))
{
    builder.WebHost.ConfigureKestrel(o =>
    {
        o.ListenAnyIP(config.Port, l => l.UseHttps(config.Cert, config.Key));
    });
}
else
{
    builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(config.Port));
}

app.Run();

class JwtAuth
{
    public string? Subject { get; set; }
    public string? Type { get; set; }
    public string? Scope { get; set; }
}

using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SkirClient;
using Skirout_Service;

// Starts a SkirRPC service on http://localhost:8787/myapi
//
// Run with:
//   dotnet run --project temp-scripts/start-service/start-service.csproj
//
// Use call-service to send requests to this service.

var store = new Dictionary<int, User>();
var gate = new object();

var service = new ServiceBuilder<UnitMeta>()
    .AddMethod(Methods.GetUser, (req, _) =>
    {
        User? user;
        lock (gate)
        {
            user = store.TryGetValue(req.UserId, out var found) ? found : null;
        }

        return Task.FromResult(new GetUserResponse
        {
            User = user,
        });
    })
    .AddMethod(Methods.AddUser, (req, _) =>
    {
        if (req.User.UserId == 0)
            throw new ServiceError(HttpErrorCode._400_BadRequest, "user_id must be non-zero");

        lock (gate)
        {
            store[req.User.UserId] = req.User;
        }

        return Task.FromResult(AddUserResponse.Default);
    })
    .Build();

var app = WebApplication.CreateBuilder(args).Build();

app.MapGet("/myapi", async (HttpContext ctx) =>
{
    // Percent-decode the query string and use it as the request body.
    string raw = ctx.Request.QueryString.HasValue
        ? ctx.Request.QueryString.Value![1..]
        : string.Empty;

    string decoded;
    try
    {
        decoded = Uri.UnescapeDataString(raw);
    }
    catch
    {
        decoded = raw;
    }

    RawResponse resp = await service.HandleRequest(decoded, new UnitMeta());
    return Results.Content(resp.Data, resp.ContentType, Encoding.UTF8, resp.StatusCode);
});

app.MapPost("/myapi", async (HttpContext ctx) =>
{
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    byte[] bytes = ms.ToArray();

    string body;
    try
    {
        body = new UTF8Encoding(false, true).GetString(bytes);
    }
    catch (DecoderFallbackException)
    {
        return Results.Content(
            "bad request: body is not valid UTF-8",
            "text/plain; charset=utf-8",
            Encoding.UTF8,
            400);
    }

    RawResponse resp = await service.HandleRequest(body, new UnitMeta());
    return Results.Content(resp.Data, resp.ContentType, Encoding.UTF8, resp.StatusCode);
});

app.Urls.Clear();
app.Urls.Add("http://0.0.0.0:8787");

Console.WriteLine("Listening on http://localhost:8787/myapi");
await app.RunAsync();

internal readonly record struct UnitMeta;

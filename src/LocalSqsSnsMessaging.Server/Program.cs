using LocalSqsSnsMessaging.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Warning);

var port = builder.Configuration.GetValue("port", 5050);
var region = builder.Configuration.GetValue("region", "us-east-1")!;
var accountId = builder.Configuration.GetValue("account-id", "000000000000")!;

builder.WebHost.UseUrls($"http://*:{port}");
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.Zero);

var registry = new BusRegistry(accountId, region, new Uri($"http://localhost:{port}"));

var app = builder.Build();

// Dashboard UI
app.MapGet("/_ui", Dashboard.ServeHtml);
app.MapGet("/_ui/dashboard.css", Dashboard.ServeCss);
app.MapGet("/_ui/dashboard.js", Dashboard.ServeJs);

// Dashboard API
app.MapGet("/_ui/api/state", (HttpContext ctx) =>
{
    var account = ctx.Request.Query["account"].FirstOrDefault();
    return DashboardApi.GetState(registry, account);
});
app.MapGet("/_ui/api/state/stream", (HttpContext ctx, CancellationToken ct) =>
{
    var account = ctx.Request.Query["account"].FirstOrDefault();
    return DashboardApi.StreamState(registry, account, ct);
});
app.MapGet("/_ui/api/queues/{name}/messages", (HttpContext ctx, string name) =>
{
    var account = ctx.Request.Query["account"].FirstOrDefault();
    return DashboardApi.GetQueueMessages(registry, account, name);
});
app.MapDelete("/_ui/api/queues/{name}/messages/{messageId}", (HttpContext ctx, string name, string messageId) =>
{
    var account = ctx.Request.Query["account"].FirstOrDefault();
    return DashboardApi.DeleteMessage(registry, account, name, messageId);
});
app.MapPost("/_ui/api/topics/{name}/publish", (HttpContext ctx, string name) =>
{
    var account = ctx.Request.Query["account"].FirstOrDefault();
    return DashboardApi.PublishToTopic(registry, account, name, ctx);
});
app.MapPost("/_ui/api/queues/{name}/redrive", (HttpContext ctx, string name) =>
{
    var account = ctx.Request.Query["account"].FirstOrDefault();
    return DashboardApi.StartRedrive(registry, account, name);
});
app.MapPost("/_ui/api/move-tasks/{taskHandle}/cancel", (HttpContext ctx, string taskHandle) =>
{
    var account = ctx.Request.Query["account"].FirstOrDefault();
    return DashboardApi.CancelRedrive(registry, account, taskHandle);
});

// Fallback: all other requests go to the AWS bridge middleware
var middleware = new AwsBridgeMiddleware(registry);
app.MapFallback(middleware.InvokeAsync);

Console.WriteLine($"LocalSqsSnsMessaging server listening on http://localhost:{port}");
Console.WriteLine($"  Region:     {region}");
Console.WriteLine($"  AccountId:  {accountId}");
Console.WriteLine($"  Dashboard:  http://localhost:{port}/_ui");
Console.WriteLine($"  Multi-account support enabled (12-digit access key = account ID)");

app.Run();

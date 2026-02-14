using LocalSqsSnsMessaging;
using LocalSqsSnsMessaging.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Warning);

var port = builder.Configuration.GetValue("port", 5050);
var region = builder.Configuration.GetValue("region", "us-east-1")!;
var accountId = builder.Configuration.GetValue("account-id", "000000000000")!;

builder.WebHost.UseUrls($"http://*:{port}");

var bus = new InMemoryAwsBus
{
    CurrentRegion = region,
    CurrentAccountId = accountId,
    ServiceUrl = new Uri($"http://localhost:{port}"),
    UsageTrackingEnabled = true
};

var app = builder.Build();

// Dashboard UI
app.MapGet("/_ui", Dashboard.ServeHtml);
app.MapGet("/_ui/dashboard.css", Dashboard.ServeCss);
app.MapGet("/_ui/dashboard.js", Dashboard.ServeJs);

// Dashboard API
app.MapGet("/_ui/api/state", () => DashboardApi.GetState(bus));
app.MapGet("/_ui/api/state/stream", (CancellationToken ct) => DashboardApi.StreamState(bus, ct));
app.MapGet("/_ui/api/queues/{name}/messages", (string name) => DashboardApi.GetQueueMessages(bus, name));

// Fallback: all other requests go to the AWS bridge middleware
var middleware = new AwsBridgeMiddleware(bus);
app.MapFallback(middleware.InvokeAsync);

Console.WriteLine($"LocalSqsSnsMessaging server listening on http://localhost:{port}");
Console.WriteLine($"  Region:    {region}");
Console.WriteLine($"  AccountId: {accountId}");
Console.WriteLine($"  Dashboard: http://localhost:{port}/_ui");

app.Run();

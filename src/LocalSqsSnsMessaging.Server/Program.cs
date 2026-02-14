using LocalSqsSnsMessaging;
using LocalSqsSnsMessaging.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
{
    Args = args
});

// Manually add configuration sources (empty builder has none)
builder.Configuration.AddCommandLine(args);
builder.Configuration.AddEnvironmentVariables();

var port = builder.Configuration.GetValue("port", 5050);
var region = builder.Configuration.GetValue("region", "us-east-1")!;
var accountId = builder.Configuration.GetValue("account-id", "000000000000")!;

builder.WebHost.UseKestrelCore();
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(port);
});

var bus = new InMemoryAwsBus
{
    CurrentRegion = region,
    CurrentAccountId = accountId,
    ServiceUrl = new Uri($"http://localhost:{port}")
};

var app = builder.Build();

var middleware = new AwsBridgeMiddleware(bus);

// Terminal middleware — handles every request, no routing needed
app.Run(middleware.InvokeAsync);

Console.WriteLine($"LocalSqsSnsMessaging server listening on http://localhost:{port}");
Console.WriteLine($"  Region:    {region}");
Console.WriteLine($"  AccountId: {accountId}");

app.Run();

using InMemoryAws.Tests.Verification.AppHost;

// The verification tests start this AppHost in their process
// (DistributedApplicationTestingBuilder); `aspire run` starts Floci alone.
var builder = DistributedApplication.CreateBuilder(args);

builder.AddFloci();

builder.Build().Run();

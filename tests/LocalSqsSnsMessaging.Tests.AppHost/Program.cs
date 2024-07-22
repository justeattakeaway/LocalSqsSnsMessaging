var builder = DistributedApplication.CreateBuilder(args);

builder.AddContainer("localstack", "localstack/localstack", "stable")
    .WithHttpEndpoint(targetPort: 4566)
    .WithEnvironment("SERVICES", "sqs,sns");
    
builder.Build().Run();
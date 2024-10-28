var builder = DistributedApplication.CreateBuilder(args);

var localstack = builder.AddContainer("localstack", "localstack/localstack", "stable")
    .WithHttpEndpoint(targetPort: 4566)
    .WithEnvironment("SERVICES", "sqs,sns");

var isRunningInCi = Environment.GetEnvironmentVariable("CI") == "true";

if (!isRunningInCi)
{
    localstack
    .WithContainerName("localsqssnsmessaging-localstack")
    .WithLifetime(ContainerLifetime.Persistent);
}
    
builder.Build().Run();
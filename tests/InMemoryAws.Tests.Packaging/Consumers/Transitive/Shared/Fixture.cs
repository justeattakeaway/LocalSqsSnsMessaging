using Amazon.Runtime;
using InMemoryAws;

namespace Shared;

public sealed class Fixture
{
    public InMemoryAwsCloud Cloud { get; } = new() { CurrentRegion = "eu-west-1" };

    // InMemoryAwsSdkHttpClientFactory is internal to each project, so a public API types it as the AWS SDK's base class.
    public HttpClientFactory CreateHttpClientFactory() => Cloud.CreateAwsSdkHttpClientFactory();
}

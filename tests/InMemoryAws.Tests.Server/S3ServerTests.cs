using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using InMemoryAws.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace InMemoryAws.Tests.Server;

/// <summary>S3 through the real server, over HTTP, with a real <see cref="AmazonS3Client"/> addressing buckets by path.</summary>
public sealed class S3ServerTests : IAsyncDisposable
{
    private WebApplication? _app;
    private int _port;

    [Before(Test)]
    public async Task Setup()
    {
        _port = GetAvailablePort();
        var registry = new CloudRegistry("000000000000", "us-east-1", new Uri($"http://localhost:{_port}"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = ["--hostBuilder:reloadConfigOnChange=false"] });
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(_port));
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        _app = builder.Build();
        var middleware = new AwsBridgeMiddleware(registry);
        _app.Map("{**path}", middleware.InvokeAsync);
        await _app.StartAsync();
    }

    [After(Test)]
    public async Task Cleanup()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync() => await Cleanup();

    private AmazonS3Client Client() =>
        new(new BasicAWSCredentials("000000000000", "fake"), new AmazonS3Config
        {
            ServiceURL = $"http://localhost:{_port}",
            AuthenticationRegion = "us-east-1",
            ForcePathStyle = true,
            MaxErrorRetry = 0,
        });

    [Test]
    public async Task PutGetListAndDeleteObjects()
    {
        using var client = Client();
        await client.PutBucketAsync("server-bucket");
        await client.PutObjectAsync(new PutObjectRequest { BucketName = "server-bucket", Key = "a/one.txt", ContentBody = "hello" });
        await client.PutObjectAsync(new PutObjectRequest { BucketName = "server-bucket", Key = "a/two words.txt", ContentBody = "world" });

        using (var get = await client.GetObjectAsync("server-bucket", "a/two words.txt"))
        using (var reader = new StreamReader(get.ResponseStream, Encoding.UTF8))
        {
            (await reader.ReadToEndAsync()).ShouldBe("world");
        }

        var head = await client.GetObjectMetadataAsync("server-bucket", "a/one.txt");
        head.ContentLength.ShouldBe(5);

        var list = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "server-bucket", Prefix = "a/" });
        list.S3Objects.Select(o => o.Key).ShouldBe(["a/one.txt", "a/two words.txt"]);

        var missing = await Should.ThrowAsync<AmazonS3Exception>(() => client.GetObjectAsync("server-bucket", "nope"));
        missing.ErrorCode.ShouldBe("NoSuchKey");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await client.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = "server-bucket",
            Objects = [new KeyVersion { Key = "a/one.txt" }, new KeyVersion { Key = "a/two words.txt" }],
        });
        await client.DeleteBucketAsync("server-bucket");
        (await client.ListBucketsAsync()).Buckets.ShouldBeNull();
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

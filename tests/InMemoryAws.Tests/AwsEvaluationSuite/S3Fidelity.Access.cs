using System.Net.Http.Headers;
using Amazon.Runtime;
using Amazon.S3;
using InMemoryAws.Http;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>
/// What the S3 access, replication and website ports share: the checks' <c>_error</c> helper, raw HTTP
/// to the website endpoint, anonymous clients that send a <c>Referer</c>, bounded polls, and the peer
/// region cross-region replication checks derive from <c>ec2:DescribeRegions</c>.
/// </summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// The regions <c>ec2:DescribeRegions</c> reports enabled for an account that has opted into none:
    /// the ones whose <c>OptInStatus</c> is <c>opt-in-not-required</c>.
    /// </summary>
    private static readonly string[] DefaultEnabledRegions =
    [
        "ap-northeast-1", "ap-northeast-2", "ap-northeast-3", "ap-south-1", "ap-southeast-1", "ap-southeast-2",
        "ca-central-1", "eu-central-1", "eu-north-1", "eu-west-1", "eu-west-2", "eu-west-3", "sa-east-1",
        "us-east-1", "us-east-2", "us-west-1", "us-west-2",
    ];

    private static int _anonymousClients;

    /// <summary>
    /// <c>_error(fn)</c>: the AWS error <paramref name="call"/> raises. An unexpected success fails the
    /// check, so a target that accepts what AWS refuses is never read as a code mismatch.
    /// </summary>
    public static async Task<AwsError> Error(Func<Task> call, string? whenSucceeded = null)
    {
        ArgumentNullException.ThrowIfNull(call);
        return await FidelityContext.CaptureError(call)
            ?? throw Fail(whenSucceeded ?? "expected a ClientError, but the call succeeded");
    }

    /// <summary>
    /// The code botocore reports for a refused HeadObject: S3 answers HEAD with no body, so botocore
    /// reports the HTTP status (<c>"404"</c>) as the code, with the reason phrase as the message.
    /// </summary>
    public static (string Code, string Message) HeadError(AwsError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var status = (int)error.StatusCode;
        return (status.ToString(System.Globalization.CultureInfo.InvariantCulture), ReasonPhrase(status));
    }

    /// <summary><c>_error(lambda: s3.head_object(...))</c>: botocore's (code, message) for a refused HeadObject.</summary>
    public static async Task<(string Code, string Message)> HeadError(IAmazonS3 s3, string bucket, string key, string? whenSucceeded = null)
    {
        ArgumentNullException.ThrowIfNull(s3);
        return HeadError(await Error(() => s3.GetObjectMetadataAsync(bucket, key), whenSucceeded));
    }

    private static string ReasonPhrase(int status) => status switch
    {
        301 => "Moved Permanently",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        412 => "Precondition Failed",
        _ => ((System.Net.HttpStatusCode)status).ToString(),
    };

    /// <summary>
    /// <c>_peer_region(ctx)</c>: a second region derived from the account's enabled regions, preferring
    /// one in the same geography as the check's region (the first, sorted), else the first of all.
    /// </summary>
    public static string PeerRegion(FidelityContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var usable = DefaultEnabledRegions.Where(r => r != ctx.Region).ToList();
        Ensure(usable.Count > 0, $"DescribeRegions reports no region enabled for this account other than {Py.Repr(ctx.Region)}");
        var geography = ctx.Region.Split('-')[0];
        var sameGeography = usable.Where(r => r.Split('-')[0] == geography).Order(StringComparer.Ordinal).ToList();
        return (sameGeography.Count > 0 ? sameGeography : [.. usable.Order(StringComparer.Ordinal)])[0];
    }

    /// <summary>
    /// <c>_anon_client(ctx, referer)</c>: a fresh unsigned S3 client (botocore's <c>UNSIGNED</c>, 5
    /// attempts) that sends <paramref name="referer"/> as its <c>Referer</c> header.
    /// </summary>
    public static AmazonS3Client AnonymousClient(FidelityContext ctx, string referer)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var client = ctx.S3Client(unsigned: true, maxAttempts: 5, accessKey: $"anon-probe-{Interlocked.Increment(ref _anonymousClients)}");
        client.BeforeRequestEvent += (_, e) =>
        {
            if (e is WebServiceRequestEventArgs request)
            {
                request.Headers["Referer"] = referer;
            }
        };
        return client;
    }

    /// <summary>
    /// <c>_http(ctx, method, url, headers, data)</c>: a raw HTTP request to the cloud (the website
    /// endpoint, here), never following a redirect. Returns the status, the headers with lower-case
    /// names, and the body; a 3xx, 4xx or 5xx is an answer, not an error.
    /// </summary>
    public static async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> Http(
        FidelityContext ctx,
        string method,
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        byte[]? data = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var http = ctx.RawHttpClient(AwsServiceType.S3);
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(url));
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (data is not null)
        {
            request.Content = new ByteArrayContent(data);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        using var response = await http.SendAsync(request);
        var answered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            answered[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
        }

        return ((int)response.StatusCode, answered, await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// A bounded poll: calls <paramref name="probe"/> until <paramref name="done"/> holds, waiting
    /// <paramref name="interval"/> of virtual time between calls; raises <paramref name="failure"/>'s
    /// message, given the last observation, when <paramref name="timeout"/> passes first.
    /// </summary>
    public static async Task<T> PollUntil<T>(
        Func<Task<T>> probe,
        Func<T, bool> done,
        TimeSpan timeout,
        TimeSpan interval,
        Func<T, string> failure)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(done);
        ArgumentNullException.ThrowIfNull(failure);
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var last = await probe();
            if (done(last))
            {
                return last;
            }

            if (clock.Elapsed >= timeout)
            {
                throw Fail(failure(last));
            }

            await FidelityTime.Delay(interval);
        }
    }

    /// <summary>
    /// <c>_try(fn)</c>: the error code <paramref name="call"/> raises, or <see cref="Succeeded"/> when it
    /// succeeds (a value no error code can equal).
    /// </summary>
    public static async Task<string> Try(Func<Task> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return await FidelityContext.CaptureError(call) is { } error ? error.Code ?? "unknown" : Succeeded;
    }

    /// <summary>What <see cref="Try"/> returns when the call succeeds.</summary>
    public const string Succeeded = "<the call succeeded>";

    /// <summary>A Python <c>(code, message)</c> pair as <c>repr</c> shows it.</summary>
    public static string Repr((string? Code, string? Message) pair) => $"({Py.Repr(pair.Code)}, {Py.Repr(pair.Message)})";

    /// <summary>A random URL-safe token, as Python's <c>secrets.token_urlsafe(32)</c> makes one.</summary>
    public static string TokenUrlSafe() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Helpers the conditional-write policy ports share.</summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// <c>_make_bucket(ctx, prefix)</c>: a bucket under <c>ctx.unique_name</c>, tagged with the check's
    /// name; cleanup removes the policy, then the bucket's contents.
    /// </summary>
    public static async Task<string> MakeTaggedBucket(FidelityContext ctx, string prefix, string check)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var s3 = ctx.S3;
        var bucket = await CreateBucket(ctx, prefix);
        ctx.OnCleanup(async () =>
        {
            try
            {
                await s3.DeleteBucketPolicyAsync(new Amazon.S3.Model.DeleteBucketPolicyRequest { BucketName = bucket });
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucket")
            {
            }
        });
        await s3.PutBucketTaggingAsync(new Amazon.S3.Model.PutBucketTaggingRequest
        {
            BucketName = bucket,
            TagSet = [new Amazon.S3.Model.Tag { Key = "sim2real", Value = check }],
        });
        return bucket;
    }

    /// <summary>
    /// <c>_await_policy(ctx, probe, expected_code)</c>: calls <paramref name="probe"/> on a fresh throwaway
    /// key (its attempt number) until it raises <paramref name="expectedCode"/>, so a freshly written policy
    /// is in force before the asserted call; the asserted key is never retried.
    /// </summary>
    public static async Task AwaitPolicy(FidelityContext ctx, Func<int, Task> probe, string expectedCode, double capSeconds = 20.0, double intervalSeconds = 2.0)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(probe);
        var timeout = Math.Max(1.0, Math.Min(capSeconds, ctx.TimeLeftSeconds - 5.0));
        var clock = FidelityTime.StartNew();
        string last;
        var attempt = 0;
        while (true)
        {
            attempt++;
            var error = await FidelityContext.CaptureError(() => probe(attempt));
            if (error?.Code == expectedCode)
            {
                return;
            }

            last = error is null ? "('SUCCESS', ...)" : Repr((error.Code, error.Message));
            if (clock.Elapsed.TotalSeconds + intervalSeconds >= timeout)
            {
                throw new TimeoutException($"expected {expectedCode} within {timeout:F0}s, last outcome: {last}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }
}

/// <summary>Helpers the Requester Pays and website ports share.</summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// Lifts only the policy settings of Block Public Access (the ACL blocks stay on), then grants
    /// anonymous <c>s3:GetObject</c> on <paramref name="resources"/> to requests whose <c>Referer</c> is
    /// <paramref name="referer"/>, as the checks' <c>_grant_public_read</c> helpers do.
    /// </summary>
    public static async Task GrantPublicRead(IAmazonS3 s3, string bucket, string sid, IReadOnlyList<string> resources, string referer)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentNullException.ThrowIfNull(resources);
        await s3.PutPublicAccessBlockAsync(new Amazon.S3.Model.PutPublicAccessBlockRequest
        {
            BucketName = bucket,
            PublicAccessBlockConfiguration = new Amazon.S3.Model.PublicAccessBlockConfiguration
            {
                BlockPublicAcls = true,
                IgnorePublicAcls = true,
                BlockPublicPolicy = false,
                RestrictPublicBuckets = false,
            },
        });
        var resource = resources.Count == 1
            ? (System.Text.Json.Nodes.JsonNode)resources[0]
            : new System.Text.Json.Nodes.JsonArray([.. resources.Select(r => (System.Text.Json.Nodes.JsonNode?)r)]);
        var policy = new System.Text.Json.Nodes.JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["Sid"] = sid,
                ["Effect"] = "Allow",
                ["Principal"] = "*",
                ["Action"] = "s3:GetObject",
                ["Resource"] = resource,
                ["Condition"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["StringEquals"] = new System.Text.Json.Nodes.JsonObject { ["aws:Referer"] = referer },
                },
            }),
        };
        await s3.PutBucketPolicyAsync(new Amazon.S3.Model.PutBucketPolicyRequest { BucketName = bucket, Policy = policy.ToJsonString() });
    }
}

/// <summary>Reading S3's errors off the wire where the .NET SDK hides them.</summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// Sends a raw (path-style, unsigned) request and returns its status and, for an error, S3's
    /// <c>Code</c> and <c>Message</c>. The .NET SDK answers some refusals as an empty success
    /// (GetBucketPolicy's <c>NoSuchBucketPolicy</c>, GetBucketReplication's 404), and can't resolve an
    /// endpoint for some bucket names at all, where boto3 surfaces S3's error; this reads the same wire
    /// response boto3 reads.
    /// </summary>
    public static async Task<(int Status, string? Code, string? Message)> Raw(FidelityContext ctx, string method, string pathAndQuery, string? body = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var http = ctx.RawHttpClient(AwsServiceType.S3);
        using var request = new HttpRequestMessage(new HttpMethod(method), pathAndQuery);
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/xml");
        }

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if ((int)response.StatusCode < 300 || text.Length == 0)
        {
            return ((int)response.StatusCode, null, null);
        }

        var error = System.Xml.Linq.XElement.Parse(text);
        return ((int)response.StatusCode, error.Element("Code")?.Value, error.Element("Message")?.Value);
    }

    /// <summary>
    /// <c>_error(lambda: s3.get_bucket_policy(...))</c> and the like, read off the wire with
    /// <see cref="Raw"/>: the (code, message) of the refusal; a success fails the check.
    /// </summary>
    public static async Task<AwsError> RawError(FidelityContext ctx, string method, string pathAndQuery, string? whenSucceeded = null, string? body = null)
    {
        var (status, code, message) = await Raw(ctx, method, pathAndQuery, body);
        if (status < 300)
        {
            throw Fail(whenSucceeded ?? "expected a ClientError, but the call succeeded");
        }

        return new AwsError(code, message, (System.Net.HttpStatusCode)status, new AmazonS3Exception(message));
    }
}

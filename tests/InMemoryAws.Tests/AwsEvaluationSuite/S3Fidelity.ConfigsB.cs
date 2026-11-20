using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>
/// What the CORS, storage class and restore ports share: browser preflights to an object URL, S3's XML
/// error documents, and the raw <c>x-amz-restore</c> header, which botocore reports as <c>Restore</c>
/// and the .NET SDK parses into other members.
/// </summary>
public static partial class S3Fidelity
{
    /// <summary>The <c>Access-Control-*</c> headers the CORS preflight checks compare.</summary>
    public static readonly string[] CorsHeaders =
    [
        "access-control-allow-origin",
        "access-control-allow-methods",
        "access-control-allow-headers",
        "access-control-max-age",
        "access-control-expose-headers",
        "access-control-allow-credentials",
    ];

    /// <summary>
    /// <c>_object_url(ctx, bucket, key)</c>: the unsigned, virtual-hosted object URL a browser fetches,
    /// the key quoted as <c>urllib.parse.quote</c> quotes it (slashes kept).
    /// </summary>
    public static string ObjectUrl(FidelityContext ctx, string bucket, string key)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(key);
        var quoted = string.Join("/", key.Split('/').Select(Uri.EscapeDataString));
        return $"https://{bucket}.s3.{ctx.Region}.amazonaws.com/{quoted}";
    }

    /// <summary>
    /// <c>_until_status(ctx, url, headers, want)</c>: repeats a preflight (<c>OPTIONS</c>) to
    /// <paramref name="url"/> every 2 seconds until it answers <paramref name="want"/> (a CORS change
    /// propagates); fails with the last answer once the budget (at most a third of the time left) runs out.
    /// </summary>
    public static async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> UntilPreflightStatus(
        FidelityContext ctx, string url, IReadOnlyDictionary<string, string> headers, int want, double budgetSeconds = 30.0)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        budgetSeconds = Math.Min(budgetSeconds, ctx.TimeLeftSeconds / 3);
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var answer = await Http(ctx, "OPTIONS", url, headers);
            if (answer.Status == want)
            {
                return answer;
            }

            if (clock.Elapsed.TotalSeconds > budgetSeconds)
            {
                var shown = "{" + string.Join(", ", headers.Select(h => $"{Py.Repr(h.Key)}: {Py.Repr(h.Value)}")) + "}";
                throw Fail($"the preflight with {shown} answered {answer.Status}, not {want}, for {budgetSeconds:F0}s: {Head(answer.Body, 300)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary><c>_xml_error(body)</c>: the (Code, Message, Method, ResourceType) of an S3 XML error body.</summary>
    public static (string? Code, string? Message, string? Method, string? ResourceType) XmlError(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        System.Xml.Linq.XElement root;
        try
        {
            root = System.Xml.Linq.XElement.Parse(System.Text.Encoding.UTF8.GetString(body));
        }
        catch (System.Xml.XmlException)
        {
            throw Fail($"the refusal body is not S3 XML: {Head(body, 300)}");
        }

        return (root.Element("Code")?.Value, root.Element("Message")?.Value, root.Element("Method")?.Value, root.Element("ResourceType")?.Value);
    }

    /// <summary>A (Code, Message, Method, ResourceType) tuple as Python's <c>repr</c> shows it.</summary>
    public static string Repr((string? Code, string? Message, string? Method, string? ResourceType) error) =>
        $"({Py.Repr(error.Code)}, {Py.Repr(error.Message)}, {Py.Repr(error.Method)}, {Py.Repr(error.ResourceType)})";

    /// <summary>
    /// <c>s3.head_object(Bucket=bucket, Key=key)</c>, with its <c>Restore</c> member: the raw
    /// <c>x-amz-restore</c> header, or null when the response has none.
    /// </summary>
    public static async Task<(GetObjectMetadataResponse Head, string? Restore)> HeadWithRestore(AmazonS3Client s3, string bucket, string key)
    {
        ArgumentNullException.ThrowIfNull(s3);
        string? restore = null;
        void Capture(object sender, ResponseEventArgs e)
        {
            if (e is WebServiceResponseEventArgs web && web.Request is GetObjectMetadataRequest request && request.Key == key && request.BucketName == bucket)
            {
                restore = web.ResponseHeaders?.FirstOrDefault(h => string.Equals(h.Key, "x-amz-restore", StringComparison.OrdinalIgnoreCase)).Value;
            }
        }

        s3.AfterResponseEvent += Capture;
        try
        {
            var head = await s3.GetObjectMetadataAsync(bucket, key);
            return (head, restore);
        }
        finally
        {
            s3.AfterResponseEvent -= Capture;
        }
    }

    /// <summary><c>s3.head_object(Bucket=bucket, Key=key).get("Restore")</c>.</summary>
    public static async Task<string?> RestoreHeader(AmazonS3Client s3, string bucket, string key) =>
        (await HeadWithRestore(s3, bucket, key)).Restore;

    /// <summary>
    /// <c>_poll_restore_header(s3, bucket, key)</c>: the object's <c>Restore</c> header once it has one,
    /// polling up to <paramref name="attempts"/> times <paramref name="delaySeconds"/> apart; the last
    /// value (null) otherwise.
    /// </summary>
    public static async Task<string?> PollRestoreHeader(AmazonS3Client s3, string bucket, string key, int attempts = 20, double delaySeconds = 3.0)
    {
        string? value = null;
        for (var i = 0; i < attempts; i++)
        {
            value = await RestoreHeader(s3, bucket, key);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(delaySeconds));
        }

        return value;
    }

    /// <summary>
    /// <c>s3.restore_object(Bucket=bucket, Key=key, RestoreRequest={"Days": days, ...})</c>, with
    /// <c>GlacierJobParameters.Tier</c> when <paramref name="tier"/> is given.
    /// </summary>
    public static Task<RestoreObjectResponse> Restore(IAmazonS3 s3, string bucket, string key, int days, string? tier = null)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var request = new RestoreObjectRequest { BucketName = bucket, Key = key, Days = days };
        if (tier is not null)
        {
            request.RetrievalTier = new GlacierJobTier(tier);
        }

        return s3.RestoreObjectAsync(request);
    }

    /// <summary><c>s3.put_object(Bucket=bucket, Key=key, Body=body, StorageClass=storageClass)</c>.</summary>
    public static Task<PutObjectResponse> PutWithClass(IAmazonS3 s3, string bucket, string key, byte[] body, string? storageClass = null)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var request = new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) };
        if (storageClass is not null)
        {
            request.StorageClass = new S3StorageClass(storageClass);
        }

        return s3.PutObjectAsync(request);
    }

    /// <summary>
    /// <c>_error(lambda: ...)</c> as most of these checks write it: botocore's (Code, Message) of the
    /// refusal (a HEAD's bare status as <c>("404", "Not Found")</c>); a success fails the check.
    /// </summary>
    public static Task<(string? Code, string? Message)> Refusal(Func<Task> call) =>
        ClientError(call, "expected a ClientError, but the call SUCCEEDED");
}

using System.Xml.Linq;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using InMemoryAws.Http;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>
/// What the core object and bucket ports share beyond <see cref="S3Fidelity"/>: the cross-service
/// checks' <c>_create_bucket</c>, the extra members of an S3 error document botocore puts in
/// <c>Error</c> (such as <c>BucketName</c>), the headers of an error response (which the .NET SDK
/// does not keep, so the request is sent again over raw HTTP), and running calls concurrently.
/// </summary>
public static class S3CoreFidelity
{
    private const string XsvcPrefix = "sim2real-xsvc-";

    /// <summary>
    /// The cross-service checks' <c>_create_bucket(ctx)</c>: a bucket named
    /// <c>sim2real-xsvc-bkt-</c> plus 12 hex digits, tagged as managed (best effort), emptied and
    /// deleted at cleanup.
    /// </summary>
    public static async Task<string> CreateXsvcBucket(FidelityContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var name = await S3Fidelity.CreateBucket(ctx, XsvcPrefix + "bkt-");
        try
        {
            await ctx.S3.PutBucketTaggingAsync(new PutBucketTaggingRequest
            {
                BucketName = name,
                TagSet =
                [
                    new Tag { Key = "sim2real:managed", Value = "true" },
                    new Tag { Key = "sim2real:prefix", Value = XsvcPrefix.TrimEnd('-') },
                ],
            });
        }
        catch (AmazonS3Exception)
        {
        }

        return name;
    }

    /// <summary>
    /// <c>exc.response["Error"][name]</c>: a member of the error document beyond Code and Message,
    /// such as <c>BucketName</c>, which the .NET SDK does not keep. The request is sent again over raw
    /// HTTP (path style, unsigned) and the member read from the error document it answers.
    /// </summary>
    public static async Task<string?> RawErrorDetail(FidelityContext ctx, HttpMethod method, string path, string? body, string name)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/xml");
        }

        using var response = await ctx.RawHttpClient(AwsServiceType.S3).SendAsync(request, ctx.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(ctx.CancellationToken);
        try
        {
            return XDocument.Parse(text).Root?.Element(name)?.Value;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends a request for an object over raw HTTP (path style, unsigned) and returns the response,
    /// so a check can read the headers of an error response, which botocore keeps in
    /// <c>ResponseMetadata.HTTPHeaders</c> and the .NET SDK does not.
    /// </summary>
    public static async Task<HttpResponseMessage> RawObjectRequest(
        FidelityContext ctx, HttpMethod method, string bucket, string key, string? versionId = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var path = $"{bucket}/{Uri.EscapeDataString(key)}";
        if (versionId is not null)
        {
            path += "?versionId=" + Uri.EscapeDataString(versionId);
        }

        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        return await ctx.RawHttpClient(AwsServiceType.S3).SendAsync(request, ctx.CancellationToken);
    }

    /// <summary>
    /// The body of a raw <c>GET /{bucket}?{subresource}</c>, for a check that asserts an element is
    /// absent, which the .NET SDK reports as a default value (a versioning <c>Status</c> of "Off").
    /// </summary>
    public static async Task<string> RawBucketSubresource(FidelityContext ctx, string bucket, string subresource)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        using var response = await ctx.RawHttpClient(AwsServiceType.S3).GetAsync(
            new Uri($"{bucket}?{subresource}", UriKind.Relative), ctx.CancellationToken);
        return await response.Content.ReadAsStringAsync(ctx.CancellationToken);
    }

    /// <summary>
    /// <c>headers.get(name)</c> over a raw response, whose headers botocore keeps lower-cased in one
    /// dictionary: the response's own headers and its content headers (<c>allow</c>,
    /// <c>last-modified</c>) alike.
    /// </summary>
    public static string? Header(HttpResponseMessage response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
        {
            return string.Join(", ", values);
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="error"/> is a bodiless 404 (a HEAD request's), which botocore reports
    /// as code <c>"404"</c> with message <c>"Not Found"</c> (the .NET SDK's code is <c>"NotFound"</c>).
    /// </summary>
    public static bool IsBareHead404(AwsError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.StatusCode == System.Net.HttpStatusCode.NotFound && (error.Code is "404" or "NotFound") && BotocoreMessage(error) == "Not Found";
    }

    /// <summary>
    /// <c>Error.Message</c> as botocore reports it: for a bodiless error response (a HEAD
    /// request's), the HTTP reason phrase (<c>"Not Found"</c>), where the .NET SDK writes a sentence
    /// of its own.
    /// </summary>
    public static string? BotocoreMessage(AwsError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Message is { } message
            && message.StartsWith("Error making request with Error Code", StringComparison.Ordinal)
            && message.EndsWith("No further error information was returned by the service.", StringComparison.Ordinal))
        {
            using var response = new HttpResponseMessage(error.StatusCode);
            return response.ReasonPhrase;
        }

        return error.Message;
    }

    /// <summary><c>name in headers</c> over a raw response.</summary>
    public static bool HasHeader(HttpResponseMessage response, string name) => Header(response, name) is not null;

    /// <summary>
    /// The checks' <c>_run_concurrent(fns)</c>: runs every function at once and returns each one's
    /// result, or the exception it raised.
    /// </summary>
    public static async Task<object[]> RunConcurrent<T>(FidelityContext ctx, IReadOnlyList<Func<Task<T>>> fns)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(fns);
        using (ctx.Concurrently())
        {
            return await Task.WhenAll(fns.Select(fn => Task.Run<object>(async () =>
            {
                try
                {
                    return await fn();
                }
#pragma warning disable CA1031 // The check reports every failure, whatever it is.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    return ex;
                }
            })));
        }
    }

    /// <summary><c>_concurrent_error_codes(results)</c>: each failure's error code, or its exception type's name.</summary>
    public static List<string?> ConcurrentErrorCodes(IEnumerable<object> results) =>
        [.. results.OfType<Exception>().Select(ex => ex is AmazonServiceException service ? AwsError.From(service).Code : ex.GetType().Name)];

    /// <summary>
    /// Every key <c>s3.get_paginator("list_objects_v2").paginate(Bucket=bucket, Prefix=prefix)</c>
    /// lists, in order.
    /// </summary>
    public static async Task<List<string>> ListAllKeys(IAmazonS3 s3, string bucket, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var keys = new List<string>();
        string? token = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, ContinuationToken = token });
            keys.AddRange((page.S3Objects ?? []).Select(o => o.Key));
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);
        return keys;
    }

    /// <summary>The names <c>s3.list_buckets(Prefix=prefix)</c> lists, every page.</summary>
    public static async Task<List<string>> ListBucketNames(IAmazonS3 s3, string prefix)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var names = new List<string>();
        string? token = null;
        do
        {
            var page = await s3.ListBucketsAsync(new ListBucketsRequest { Prefix = prefix, ContinuationToken = token });
            names.AddRange((page.Buckets ?? []).Select(b => b.BucketName));
            token = page.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(token));
        return names;
    }
}

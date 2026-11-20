using Amazon.S3;
using Amazon.S3.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>
/// What the second batch of core object ports shares: turning versioning on, and tag sets in the
/// shape botocore answers them (<c>[{"Key": ..., "Value": ...}]</c>), compared and printed as such.
/// </summary>
public static partial class S3Fidelity
{
    /// <summary><c>s3.put_bucket_versioning(Bucket=bucket, VersioningConfiguration={"Status": status})</c>.</summary>
    public static Task SetVersioning(IAmazonS3 s3, string bucket, VersionStatus status)
    {
        ArgumentNullException.ThrowIfNull(s3);
        return s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = status },
        });
    }

    /// <summary><c>s3.get_bucket_versioning(Bucket=bucket).get("Status")</c>; the SDK's "Off" is an absent Status.</summary>
    public static async Task<string?> VersioningStatus(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var status = (await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket })).VersioningConfig?.Status;
        return status is null || status == VersionStatus.Off ? null : status.Value;
    }

    /// <summary>A tag set as <c>[(Key, Value), ...]</c>, in the order S3 answered it.</summary>
    public static List<(string Key, string Value)> TagPairs(IEnumerable<Tag>? tags) =>
        [.. (tags ?? []).Select(t => (t.Key, t.Value))];

    /// <summary>Whether <paramref name="tags"/> is exactly <paramref name="expected"/>, in order.</summary>
    public static bool TagsAre(IEnumerable<Tag>? tags, params (string Key, string Value)[] expected) =>
        TagPairs(tags).SequenceEqual(expected);

    /// <summary>A tag set as botocore prints it: <c>[{'Key': 'k', 'Value': 'v'}]</c>.</summary>
    public static string TagRepr(IEnumerable<Tag>? tags) =>
        "[" + string.Join(", ", TagPairs(tags).Select(t => $"{{'Key': {Py.Repr(t.Key)}, 'Value': {Py.Repr(t.Value)}}}")) + "]";
}

/// <summary>Listing keys as boto3's <c>list_objects_v2</c> paginator lists them.</summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// <c>_list_keys(s3, bucket, prefix, **kwargs)</c>: every key under <paramref name="prefix"/>,
    /// across all pages, with the request's <c>EncodingType</c>. A key listed with
    /// <c>EncodingType=url</c> is as S3 encoded it: botocore decodes a listing only when it asked for
    /// the encoding itself, not when the caller did.
    /// </summary>
    public static async Task<List<string>> ListKeys(FidelityContext ctx, string bucket, string prefix, string? encodingType = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (encodingType is null)
        {
            return await S3CoreFidelity.ListAllKeys(ctx.S3, bucket, prefix);
        }

        var keys = new List<string>();
        string? token = null;
        do
        {
            var page = await RawListObjectsV2(ctx, bucket, prefix, encodingType: encodingType, continuationToken: token);
            keys.AddRange(page.Elements(S3Xml + "Contents").Select(c => (string)c.Element(S3Xml + "Key")!));
            token = (string?)page.Element(S3Xml + "IsTruncated") == "true" ? (string?)page.Element(S3Xml + "NextContinuationToken") : null;
        }
        while (token is not null);
        return keys;
    }

    /// <summary>The S3 response namespace.</summary>
    public static readonly System.Xml.Linq.XNamespace S3Xml = "http://s3.amazonaws.com/doc/2006-03-01/";

    /// <summary>
    /// A raw (path-style, unsigned) <c>ListObjectsV2</c>, its response document as S3 wrote it: the
    /// .NET SDK decodes the keys of a listing it asked to be url-encoded, which botocore leaves alone.
    /// </summary>
    public static async Task<System.Xml.Linq.XElement> RawListObjectsV2(
        FidelityContext ctx, string bucket, string prefix, string? delimiter = null, string? encodingType = null, string? continuationToken = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var query = "list-type=2&prefix=" + Uri.EscapeDataString(prefix);
        if (delimiter is not null)
        {
            query += "&delimiter=" + Uri.EscapeDataString(delimiter);
        }

        if (encodingType is not null)
        {
            query += "&encoding-type=" + Uri.EscapeDataString(encodingType);
        }

        if (continuationToken is not null)
        {
            query += "&continuation-token=" + Uri.EscapeDataString(continuationToken);
        }

        using var response = await ctx.RawHttpClient(InMemoryAws.Http.AwsServiceType.S3).GetAsync(new Uri($"{bucket}?{query}", UriKind.Relative), ctx.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(ctx.CancellationToken);
        FidelityAssert.Ensure(response.IsSuccessStatusCode, $"ListObjectsV2 failed: {(int)response.StatusCode} {text}");
        return System.Xml.Linq.XElement.Parse(text);
    }

    /// <summary>Python's <c>urllib.parse.unquote_plus</c>.</summary>
    public static string UnquotePlus(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Uri.UnescapeDataString(value.Replace('+', ' '));
    }
}

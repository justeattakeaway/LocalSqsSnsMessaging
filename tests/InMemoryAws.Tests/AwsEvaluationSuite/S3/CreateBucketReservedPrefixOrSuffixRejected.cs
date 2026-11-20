// Translated from behavioral/s3/check_create_bucket_reserved_prefix_or_suffix_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.create-bucket-reserved-prefix-or-suffix-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateBucketReservedPrefixOrSuffixRejected
{
    private const string Check = "create-bucket-reserved-prefix-or-suffix-rejected";
    private const string StartsReserved = "The requested bucket name starts with a reserved prefix";
    private const string EndsReserved = "The requested bucket name ends with a reserved suffix";
    private const string NotValid = "The specified bucket is not valid.";
    private const string MalformedXml = "The XML you provided was not well-formed or did not validate against our published schema";

    /// <summary>
    /// CreateBucket refuses a name under a reserved prefix or suffix, or with a leading or
    /// trailing hyphen, naming each refusal's own exact code and message; a valid name is created
    /// and is the only bucket listed under its prefix.
    /// Reference: https://docs.aws.amazon.com/AmazonS3/latest/userguide/bucketnamingrules.html
    /// </summary>
    [Test]
    [DisplayName("s3.create-bucket-reserved-prefix-or-suffix-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var baseName = FidelityContext.UniqueName("sim2real-behav-s3rsv-");
        await s3.PutBucketAsync(BucketParams(ctx, baseName));
        ctx.OnCleanup(() => DeleteBucket(s3, baseName));
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = baseName, TagSet = [new Tag { Key = "sim2real", Value = Check }] });
        await s3.HeadBucketAsync(new HeadBucketRequest { BucketName = baseName }); // independent read: the control bucket really exists

        // The xn-- prefix, and a leading or trailing hyphen, read as a structurally invalid name.
        await AssertInvalidName(ctx, s3, $"xn--{baseName}", "an xn-- prefix");
        await AssertInvalidName(ctx, s3, $"-{baseName}", "a leading hyphen");
        await AssertInvalidName(ctx, s3, $"{baseName}-", "a trailing hyphen");

        // sthree- and amzn-s3-demo- are reserved prefixes, refused as a posture decision (AccessDenied),
        // not a naming-shape decision.
        await AssertRefused(ctx, s3, $"sthree-{baseName}", "an sthree- prefix", "AccessDenied", StartsReserved);
        await AssertRefused(ctx, s3, $"amzn-s3-demo-{baseName}", "an amzn-s3-demo- prefix", "AccessDenied", StartsReserved);

        // -s3alias, --ol-s3 and .mrap are reserved suffixes (access point, Object Lambda and
        // Multi-Region Access Point aliases), also refused as AccessDenied.
        await AssertRefused(ctx, s3, $"{baseName}-s3alias", "a -s3alias suffix", "AccessDenied", EndsReserved);
        await AssertRefused(ctx, s3, $"{baseName}--ol-s3", "a --ol-s3 suffix", "AccessDenied", EndsReserved);
        await AssertRefused(ctx, s3, $"{baseName}.mrap", "a .mrap suffix", "AccessDenied", EndsReserved);

        // --x-s3 names a directory bucket; sending the general-purpose CreateBucketConfiguration body
        // for that name shape is refused as a schema mismatch, not a naming-shape decision.
        // The general-purpose body is sent in every region, us-east-1 included, so the request shape
        // does not depend on the region.
        var directoryName = $"{baseName}--x-s3";
        ctx.OnCleanup(() => DeleteBucket(s3, directoryName));
        // The .NET SDK can't resolve an endpoint for a name shaped like a directory bucket's
        // ("Unrecognized S3Express bucket name format"), so the request boto3 sends is sent raw.
        var malformed = await S3Fidelity.RawError(
            ctx,
            "PUT",
            directoryName,
            "expected a ClientError, but the call SUCCEEDED",
            $"<CreateBucketConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><LocationConstraint>{ctx.Region}</LocationConstraint></CreateBucketConfiguration>");
        Ensure(malformed.Code == "MalformedXML", $"a --x-s3 suffix: expected MalformedXML, got {Py.Repr(malformed.Code)} ({Py.Repr(malformed.Message)})");
        Ensure(malformed.Message == MalformedXml, $"a --x-s3 suffix: expected the exact message, got {Py.Repr(malformed.Message)}");

        // Effect absent: the suffixed names share the control's prefix, so a listing under it reads
        // the control alone; each prefixed name is listed under its own prefix and reads nothing.
        var names = await ListBucketNames(s3, baseName);
        Ensure(names.SequenceEqual([baseName]), $"ListBuckets under the control's prefix read [{string.Join(", ", names.Select(Py.Repr))}], expected only it");
        foreach (var prefixed in new[] { $"xn--{baseName}", $"-{baseName}", $"sthree-{baseName}", $"amzn-s3-demo-{baseName}" })
        {
            names = await ListBucketNames(s3, prefixed);
            Ensure(names.Count == 0, $"ListBuckets under the refused name {Py.Repr(prefixed)} read [{string.Join(", ", names.Select(Py.Repr))}]");
        }
    }

    private static PutBucketRequest BucketParams(FidelityContext ctx, string name)
    {
        var request = new PutBucketRequest { BucketName = name, UseClientRegion = false };
        if (ctx.Region != "us-east-1")
        {
            request.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
        }

        return request;
    }

    private static Task AssertInvalidName(FidelityContext ctx, IAmazonS3 s3, string name, string reason) =>
        AssertRefused(ctx, s3, name, reason, "InvalidBucketName", NotValid);

    private static async Task AssertRefused(FidelityContext ctx, IAmazonS3 s3, string name, string reason, string wantCode, string wantMessage)
    {
        // Only does anything if the create below unexpectedly lands.
        ctx.OnCleanup(() => DeleteBucket(s3, name));
        var error = await S3Fidelity.Error(() => s3.PutBucketAsync(BucketParams(ctx, name)), "expected a ClientError, but the call SUCCEEDED");
        Ensure(error.Code == wantCode, $"{reason}: expected {wantCode}, got {Py.Repr(error.Code)} ({Py.Repr(error.Message)})");
        Ensure(error.Message == wantMessage, $"{reason}: expected the exact message, got {Py.Repr(error.Message)}");
    }

    /// <summary>Teardown: empty then delete, tolerating a name no general purpose bucket was ever created under.</summary>
    private static async Task DeleteBucket(IAmazonS3 s3, string bucket)
    {
        try
        {
            await S3Fidelity.EmptyAndDelete(s3, bucket);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "NoSuchBucket" or "InvalidBucketName" or "InvalidAccessPointAlias")
        {
        }
        catch (Amazon.Runtime.AmazonClientException)
        {
            // The SDK can't resolve an endpoint for some reserved shapes (a directory bucket's --x-s3).
        }
    }

    /// <summary>Every bucket name ListBuckets returns under <paramref name="prefix"/>, paginated.</summary>
    private static async Task<List<string>> ListBucketNames(IAmazonS3 s3, string prefix)
    {
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

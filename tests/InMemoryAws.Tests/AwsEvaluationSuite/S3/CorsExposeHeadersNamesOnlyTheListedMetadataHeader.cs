// Translated from behavioral/s3/check_cors_expose_headers_names_only_the_listed_metadata_header.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-expose-headers-names-only-the-listed-metadata-header</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsExposeHeadersNamesOnlyTheListedMetadataHeader
{
    private const string Check = "cors-expose-headers-names-only-the-listed-metadata-header";
    private const string Site = "https://reader.example.org";
    private const string ObjectKey = "metadata/annotated.txt";
    private const string ListedMeta = "x-amz-meta-edition";
    private const string UnlistedMeta = "x-amz-meta-shelf";
    private static readonly byte[] ObjectBody = "two metadata headers, one of them exposed"u8.ToArray();

    /// <summary>
    /// Access-Control-Expose-Headers on a presigned GET names only the metadata header the rule's
    /// ExposeHeaders lists, while the unlisted one is still sent with the object.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-expose-headers-names-only-the-listed-metadata-header")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-behav-expose-");
        var put = new PutObjectRequest { BucketName = bucket, Key = ObjectKey, InputStream = new MemoryStream(ObjectBody), ContentType = "text/plain" };
        put.Metadata.Add("edition", "first");
        put.Metadata.Add("shelf", "upper");
        await s3.PutObjectAsync(put);
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration
            {
                Rules = [new CORSRule { AllowedOrigins = [Site], AllowedMethods = ["GET"], AllowedHeaders = ["content-type"], ExposeHeaders = [ListedMeta] }],
            },
        });
        var signedRead = S3Fidelity.Presign(ctx, "GET", bucket, ObjectKey, 600);
        var served = await S3Fidelity.AwaitAnswer(
            ctx,
            "GET",
            signedRead,
            new Dictionary<string, string> { ["Origin"] = Site },
            a => a.Headers.GetValueOrDefault("access-control-allow-origin") == Site,
            (a, budget) => $"after {budget:F0}s the GET answered {a.Status} with Allow-Origin {Py.Repr(a.Headers.GetValueOrDefault("access-control-allow-origin"))}, expected {Py.Repr(Site)}");
        Ensure(served.Body.SequenceEqual(ObjectBody), $"the GET served {S3Fidelity.Head(served.Body)}, expected {S3Fidelity.Head(ObjectBody)}");
        Ensure(
            served.Headers.GetValueOrDefault("access-control-expose-headers") == ListedMeta,
            $"Access-Control-Expose-Headers was {Py.Repr(served.Headers.GetValueOrDefault("access-control-expose-headers"))}, expected {Py.Repr(ListedMeta)}");
        Ensure(served.Headers.GetValueOrDefault(ListedMeta) == "first", $"the listed metadata header was {Py.Repr(served.Headers.GetValueOrDefault(ListedMeta))}, expected 'first'");

        // The rule governs what a browser may read, not what S3 sends: the unlisted header is there.
        Ensure(
            served.Headers.GetValueOrDefault(UnlistedMeta) == "upper",
            $"the unlisted metadata header was {Py.Repr(served.Headers.GetValueOrDefault(UnlistedMeta))}, expected 'upper' to be sent with the object anyway");
        var stored = await s3.GetObjectMetadataAsync(bucket, ObjectKey);
        var metadata = S3Fidelity.UserMetadata(stored.Metadata);
        Ensure(
            metadata.Count == 2 && metadata.GetValueOrDefault("edition") == "first" && metadata.GetValueOrDefault("shelf") == "upper",
            $"HeadObject reports the metadata as {Py.Repr(metadata)}, expected both entries");
    }
}

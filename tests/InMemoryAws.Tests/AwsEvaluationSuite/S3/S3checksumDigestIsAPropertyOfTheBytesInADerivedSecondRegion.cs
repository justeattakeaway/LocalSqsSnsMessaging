// Translated from behavioral/s3/check_s3checksum_digest_is_a_property_of_the_bytes_in_a_derived_second_region.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3checksum-digest-is-a-property-of-the-bytes-in-a-derived-second-region</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3checksumDigestIsAPropertyOfTheBytesInADerivedSecondRegion
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("a-checksum-is-a-function-of-the-bytes-and-of-nothing-else-", 96)));

    /// <summary>
    /// The same bytes get the same SHA256, ETag and ChecksumType in a second region.
    /// A wrong SHA256 is refused there with BadDigest and stores nothing: the digest is a function of
    /// the bytes alone, and verification must not exist only at the default regional endpoint.
    /// </summary>
    [Test]
    [DisplayName("s3.s3checksum-digest-is-a-property-of-the-bytes-in-a-derived-second-region")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3Home = ctx.S3;
        var other = S3Fidelity.SecondRegion(ctx);
        Ensure(other != ctx.Region, $"the derived second region is {Py.Repr(ctx.Region)} itself");
        var s3Other = ctx.S3Client(other);

        var want = S3Fidelity.Sha256B64(Payload);
        var wrong = S3Fidelity.Sha256B64([.. Payload, (byte)'x']);
        Ensure(wrong != want, "the wrong digest collided with the correct one");

        // --- The home-region object.
        var homeBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var homePut = await s3Home.PutObjectAsync(new PutObjectRequest { BucketName = homeBucket, Key = "same-bytes.bin", InputStream = new MemoryStream(Payload), ChecksumSHA256 = want });
        Ensure(homePut.ChecksumSHA256 == want, $"the {ctx.Region} upload echoed {Py.Repr(homePut.ChecksumSHA256)}, the client computed {Py.Repr(want)}");
        var homeLocation = await Location(s3Home, homeBucket);
        Ensure((homeLocation ?? "us-east-1") == ctx.Region, $"the home bucket reports LocationConstraint {Py.Repr(homeLocation)}, want {Py.Repr(ctx.Region)}");

        // --- The same bytes through the other region's endpoint.
        var otherBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-", region: other);
        var otherLocation = await Location(s3Other, otherBucket);
        Ensure((otherLocation ?? "us-east-1") == other, $"the second bucket reports LocationConstraint {Py.Repr(otherLocation)}, want {Py.Repr(other)}");
        Ensure(
            otherLocation != homeLocation,
            $"both buckets report LocationConstraint {Py.Repr(homeLocation)}; the two uploads must go to genuinely different regions for this comparison to mean anything");

        // --- Break exactly one thing, in the derived region: the digest value.
        var exc = await FidelityContext.CaptureError(() => s3Other.PutObjectAsync(new PutObjectRequest { BucketName = otherBucket, Key = "same-bytes.bin", InputStream = new MemoryStream(Payload), ChecksumSHA256 = wrong }));
        Ensure(
            exc is not null,
            $"the {other} endpoint accepted ChecksumSHA256={Py.Repr(wrong)} for a body whose true digest is {Py.Repr(want)}; verification is wired up in {ctx.Region} but not in {other}");
        Ensure(exc!.Code == "BadDigest", $"expected BadDigest for a mismatched SHA256 in {other}, got {Py.Repr(exc.Code)}");
        Ensure(
            exc.Message == "The SHA256 you specified did not match the calculated checksum.",
            $"expected the exact BadDigest message for a mismatched SHA256 in {other}, got {Py.Repr(exc.Message)}");
        Ensure(exc.StatusCode == HttpStatusCode.BadRequest, $"expected HTTP 400 for BadDigest in {other}, got {(int)exc.StatusCode}");

        // --- Effect absent, in that region.
        var missing = await FidelityContext.CaptureError(() => s3Other.GetObjectMetadataAsync(otherBucket, "same-bytes.bin"));
        Ensure(missing is not null, $"the refused PutObject stored the object anyway in {other}");
        Ensure(missing!.StatusCode == HttpStatusCode.NotFound, $"HeadObject in {other} returned {(int)missing.StatusCode}, expected 404");
        var listed = (await S3Fidelity.ListedObjects(s3Other, otherBucket)).Select(o => o.Key).ToList();
        Ensure(listed.Count == 0, $"the {other} bucket holds {Py.Repr(listed)} after the refused PUT");

        // --- POSITIVE CONTROL in the derived region, and the invariant across the two.
        var otherPut = await s3Other.PutObjectAsync(new PutObjectRequest { BucketName = otherBucket, Key = "same-bytes.bin", InputStream = new MemoryStream(Payload), ChecksumSHA256 = want });
        Ensure(otherPut.ChecksumSHA256 == want, $"the {other} upload echoed {Py.Repr(otherPut.ChecksumSHA256)}, the client computed {Py.Repr(want)}");
        using var homeGot = await s3Home.GetObjectAsync(new GetObjectRequest { BucketName = homeBucket, Key = "same-bytes.bin", ChecksumMode = ChecksumMode.ENABLED });
        using var otherGot = await s3Other.GetObjectAsync(new GetObjectRequest { BucketName = otherBucket, Key = "same-bytes.bin", ChecksumMode = ChecksumMode.ENABLED });
        var homeBody = await S3Fidelity.ReadBody(homeGot);
        var otherBody = await S3Fidelity.ReadBody(otherGot);
        Ensure(
            homeBody.AsSpan().SequenceEqual(otherBody) && otherBody.AsSpan().SequenceEqual(Payload),
            $"the two regions returned {homeBody.Length} and {otherBody.Length} bytes; both must be the {Payload.Length} that were uploaded");
        Ensure(
            homeGot.ChecksumSHA256 == otherGot.ChecksumSHA256 && otherGot.ChecksumSHA256 == want,
            $"{ctx.Region} reports {Py.Repr(homeGot.ChecksumSHA256)} and {other} reports {Py.Repr(otherGot.ChecksumSHA256)} for identical bytes; a checksum is a function of the bytes alone and both must be {Py.Repr(want)}");
        Ensure(
            homeGot.ChecksumType == otherGot.ChecksumType && otherGot.ChecksumType == ChecksumType.FULL_OBJECT,
            $"ChecksumType differs across regions: {Py.Repr(homeGot.ChecksumType?.Value)} in {ctx.Region} vs {Py.Repr(otherGot.ChecksumType?.Value)} in {other}");
        Ensure(homeGot.ETag == otherGot.ETag, $"the ETag of identical bytes differs across regions: {Py.Repr(homeGot.ETag)} vs {Py.Repr(otherGot.ETag)}");
    }

    /// <summary><c>get_bucket_location(...).get("LocationConstraint")</c>: null for us-east-1.</summary>
    private static async Task<string?> Location(IAmazonS3 s3, string bucket)
    {
        var value = (await s3.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket })).Location?.Value;
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

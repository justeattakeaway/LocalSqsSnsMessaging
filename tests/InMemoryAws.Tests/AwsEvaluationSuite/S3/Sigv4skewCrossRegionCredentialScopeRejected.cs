// Translated from behavioral/s3/check_sigv4skew_cross_region_credential_scope_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.sigv4skew-cross-region-credential-scope-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class Sigv4skewCrossRegionCredentialScopeRejected
{
    /// <summary>
    /// A correctly-timed PUT whose credential scope names the wrong region is refused with
    /// AuthorizationHeaderMalformed and writes nothing; the same request scoped correctly succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.sigv4skew-cross-region-credential-scope-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var region2 = S3Fidelity.SecondRegion(ctx);
        var s3b = ctx.S3Client(region2);
        var bucket2 = await S3Fidelity.NewBucket(ctx, region: region2);
        var payload = "sigv4skew-cross-region-scope-payload"u8.ToArray();
        const string key = "sigv4skew/scoped.txt";

        // Break exactly one thing: sign for ctx.region, send to region2's endpoint.
        var (url, host, path) = S3Fidelity.PathStyleTarget(region2, bucket2, key);
        var signedAt = await S3Fidelity.ServerNow(ctx, region2, bucket2);
        var (status, _, body) = await S3Fidelity.Send(ctx, "PUT", url, S3Fidelity.SignHeaders(ctx.Region, "PUT", host, path, payload, signedAt), payload);
        Ensure(
            status == 400,
            $"a PUT scoped to {Py.Repr(ctx.Region)} but sent to {Py.Repr(region2)} must be refused 400, got {status}: {S3Fidelity.Head(body, 300)}");
        var err = S3Fidelity.XmlFields(body) ?? throw Fail($"expected an <Error> document, got {S3Fidelity.Head(body)}");
        Ensure(
            err["Code"] == "AuthorizationHeaderMalformed",
            $"expected AuthorizationHeaderMalformed, got {Py.Repr(err["Code"])} ({Py.Repr(err.GetValueOrDefault("Message"))})");
        Ensure(
            err.GetValueOrDefault("Region") == region2,
            $"the refusal must name the region it expected ({Py.Repr(region2)}), got {Py.Repr(err.GetValueOrDefault("Region"))}");
        var expected = $"The authorization header is malformed; the region '{ctx.Region}' is wrong; expecting '{region2}'";
        Ensure(
            err["Message"] == expected,
            $"the message must name the region we signed for and the one expected: {Py.Repr(expected)}; got {Py.Repr(err["Message"])}");
        var (code, message) = await S3Fidelity.ErrorPair(() => s3b.GetObjectAsync(bucket2, key), "the call SUCCEEDED where real AWS raises an error");
        Ensure(code == "NoSuchKey", $"the mis-scoped PUT must not have written {Py.Repr(key)}; get_object answered {Py.Repr(code)}");
        Ensure(message == "The specified key does not exist.", $"get_object of the absent key must say 'The specified key does not exist.', got {Py.Repr(message)}");

        // Restore: the same request scoped to region2 succeeds.
        (url, host, path) = S3Fidelity.PathStyleTarget(region2, bucket2, key);
        (status, _, body) = await S3Fidelity.Send(ctx, "PUT", url, S3Fidelity.SignHeaders(region2, "PUT", host, path, payload, signedAt), payload);
        Ensure(status == 200, $"the same request scoped to {Py.Repr(region2)} must succeed, got {status}: {S3Fidelity.Head(body, 300)}");
        (url, host, path) = S3Fidelity.PathStyleTarget(region2, bucket2, key);
        var (getStatus, _, got) = await S3Fidelity.Send(ctx, "GET", url, S3Fidelity.SignHeaders(region2, "GET", host, path, [], await S3Fidelity.ServerNow(ctx, region2, bucket2)));
        Ensure(getStatus == 200, $"signed GET of the correctly-scoped object returned {getStatus}");
        Ensure(got.SequenceEqual(payload), $"the object served {S3Fidelity.Head(got)}, expected the bytes we wrote {S3Fidelity.Head(payload)}");
    }
}

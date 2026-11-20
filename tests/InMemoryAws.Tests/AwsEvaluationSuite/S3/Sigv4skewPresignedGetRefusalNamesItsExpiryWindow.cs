// Translated from behavioral/s3/check_sigv4skew_presigned_get_refusal_names_its_expiry_window.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.sigv4skew-presigned-get-refusal-names-its-expiry-window</summary>
[Category("AwsEvaluationSuite")]
public sealed class Sigv4skewPresignedGetRefusalNamesItsExpiryWindow
{
    /// <summary>
    /// A presigned GET serves the object before its expiry, then is refused naming that expiry. Both
    /// outcomes are read against S3's own clock, not this host's, with the refusal polled for.
    /// </summary>
    [Test]
    [DisplayName("s3.sigv4skew-presigned-get-refusal-names-its-expiry-window")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        var payload = "sigv4skew-presigned-expiry-payload"u8.ToArray();
        const string key = "sigv4skew/expiring.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(payload) });
        const int expiresIn = 15;
        var url = S3Fidelity.Presign(ctx, "GET", bucket, key, expiresIn);
        var query = S3Fidelity.SignedQuery(url).ToDictionary(p => p.Key, p => p.Value);
        Ensure(
            query.GetValueOrDefault("X-Amz-Expires") == "15",
            "the presigned URL must carry the expiry we asked for as a signed parameter; " +
            $"X-Amz-Expires was {Py.Repr(query.GetValueOrDefault("X-Amz-Expires"))} in {Py.Repr(query.Keys.Order(StringComparer.Ordinal).ToList())}");
        var signedAt = S3Fidelity.AmzInstant(query["X-Amz-Date"]);
        var expiresAt = signedAt.AddSeconds(expiresIn);

        var (status, headers, got) = await S3Fidelity.Send(ctx, "GET", url);
        Ensure(status == 200, $"a presigned GET inside its window must serve the object, got {status}");
        Ensure(got.SequenceEqual(payload), $"presigned GET served {S3Fidelity.Head(got)}, expected {S3Fidelity.Head(payload)}");
        var servedAt = DateTimeOffset.Parse(headers["date"], System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;
        Ensure(
            servedAt < expiresAt,
            $"the target served this URL at {servedAt:O}, which is not before the expiry it signed " +
            $"({expiresAt:O}); the positive half of this check never happened");

        // Poll past the expiry on S3's clock.
        var clock = FidelityTime.StartNew();
        byte[] body = [];
        status = 200;
        while (clock.Elapsed < TimeSpan.FromSeconds(180))
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
            (status, _, body) = await S3Fidelity.Send(ctx, "GET", url);
            if (status != 200)
            {
                break;
            }
        }

        Ensure(
            status == 403,
            "a presigned GET must stop being honoured once its expiry passes; the URL still answered " +
            $"{status} after polling past {expiresAt:O}");
        var err = S3Fidelity.XmlFields(body) ?? throw Fail($"expected an <Error> document, got {S3Fidelity.Head(body)}");
        Ensure(err["Code"] == "AccessDenied", $"expected AccessDenied for an expired presigned GET, got {Py.Repr(err["Code"])}");
        Ensure(err["Message"] == "Request has expired", $"the refusal must say the request expired, got {Py.Repr(err["Message"])}");
        Ensure(
            err.GetValueOrDefault("X-Amz-Expires") == "15",
            $"the refusal must name the signed window; X-Amz-Expires was {Py.Repr(err.GetValueOrDefault("X-Amz-Expires"))}, expected '15'");
        Ensure(
            err.GetValueOrDefault("Expires") == S3Fidelity.IsoSeconds(expiresAt),
            "the named expiry must be X-Amz-Date + X-Amz-Expires to the second: expected " +
            $"{Py.Repr(S3Fidelity.IsoSeconds(expiresAt))}, got {Py.Repr(err.GetValueOrDefault("Expires"))}");
        Ensure(
            string.CompareOrdinal(err.GetValueOrDefault("ServerTime"), err.GetValueOrDefault("Expires")) >= 0,
            "a 'Request has expired' refusal must report a ServerTime at or after the expiry it names; " +
            $"got ServerTime={Py.Repr(err.GetValueOrDefault("ServerTime"))} against Expires={Py.Repr(err.GetValueOrDefault("Expires"))}");

        var fresh = S3Fidelity.Presign(ctx, "GET", bucket, key, 300);
        (status, _, got) = await S3Fidelity.Send(ctx, "GET", fresh);
        Ensure(status == 200, $"a freshly presigned GET of the same object returned {status}");
        Ensure(got.SequenceEqual(payload), $"freshly presigned GET served {S3Fidelity.Head(got)}, expected {S3Fidelity.Head(payload)}");
    }
}

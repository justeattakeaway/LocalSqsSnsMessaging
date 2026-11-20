// Translated from behavioral/s3/check_sigv4skew_window_enforced_in_derived_second_region_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.sigv4skew-window-enforced-in-derived-second-region-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class Sigv4skewWindowEnforcedInDerivedSecondRegionRejected
{
    /// <summary>
    /// S3 enforces the same signing-skew window in a second, derived region as in ctx.region: the tolerance
    /// the home region reports is the expectation the other region is held to.
    /// </summary>
    [Test]
    [DisplayName("s3.sigv4skew-window-enforced-in-derived-second-region-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var homeBucket = await S3Fidelity.NewBucket(ctx);
        var payload = "sigv4skew-second-region-payload"u8.ToArray();

        // The home region's window, read off its own refusal.
        var (url, host, path) = S3Fidelity.PathStyleTarget(ctx.Region, homeBucket, "sigv4skew/home-skewed.txt");
        var homeSkewedAt = (await S3Fidelity.ServerNow(ctx, ctx.Region, homeBucket)).AddSeconds(-1500);
        var (status, _, body) = await S3Fidelity.Send(ctx, "PUT", url, S3Fidelity.SignHeaders(ctx.Region, "PUT", host, path, payload, homeSkewedAt), payload);
        Ensure(status == 403, $"the home-region skewed PUT must be refused 403, got {status}");
        var homeErr = S3Fidelity.XmlFields(body) ?? throw Fail($"expected an <Error> document, got {S3Fidelity.Head(body)}");
        Ensure(homeErr["Code"] == "RequestTimeTooSkewed", $"expected RequestTimeTooSkewed in {Py.Repr(ctx.Region)}, got {Py.Repr(homeErr["Code"])}");
        Ensure(
            homeErr["Message"] == "The difference between the request time and the current time is too large.",
            $"unexpected RequestTimeTooSkewed message {Py.Repr(homeErr["Message"])}");
        var homeWindowMs = int.Parse(homeErr["MaxAllowedSkewMilliseconds"], System.Globalization.CultureInfo.InvariantCulture);

        var region2 = S3Fidelity.SecondRegion(ctx);
        var s3b = ctx.S3Client(region2);
        var bucket2 = await S3Fidelity.NewBucket(ctx, region: region2);
        const string skewedKey = "sigv4skew/skewed.txt";
        const string controlKey = "sigv4skew/control.txt";

        // Break exactly one thing in region2: the X-Amz-Date, 1500 s behind its clock.
        (url, host, path) = S3Fidelity.PathStyleTarget(region2, bucket2, skewedKey);
        var skewedAt = (await S3Fidelity.ServerNow(ctx, region2, bucket2)).AddSeconds(-1500);
        (status, _, body) = await S3Fidelity.Send(ctx, "PUT", url, S3Fidelity.SignHeaders(region2, "PUT", host, path, payload, skewedAt), payload);
        Ensure(status == 403, $"a PUT signed 1500 s behind {region2}'s clock must be refused 403, got {status}: {S3Fidelity.Head(body, 300)}");
        var err = S3Fidelity.XmlFields(body) ?? throw Fail($"expected an <Error> document, got {S3Fidelity.Head(body)}");
        Ensure(
            err["Code"] == "RequestTimeTooSkewed",
            $"expected RequestTimeTooSkewed in {Py.Repr(region2)} exactly as in {Py.Repr(ctx.Region)}, got " +
            $"{Py.Repr(err["Code"])} ({Py.Repr(err.GetValueOrDefault("Message"))})");
        Ensure(
            err["Message"] == "The difference between the request time and the current time is too large.",
            $"unexpected RequestTimeTooSkewed message {Py.Repr(err["Message"])}");
        Ensure(
            err["RequestTime"] == S3Fidelity.AmzDate(skewedAt),
            $"the {region2} refusal must echo the X-Amz-Date we signed ({Py.Repr(S3Fidelity.AmzDate(skewedAt))}), got {Py.Repr(err["RequestTime"])}");
        Ensure(
            int.Parse(err["MaxAllowedSkewMilliseconds"], System.Globalization.CultureInfo.InvariantCulture) == homeWindowMs,
            $"{region2} reported a tolerance of {Py.Repr(err["MaxAllowedSkewMilliseconds"])} ms while " +
            $"{ctx.Region} reported {homeWindowMs} ms; the window must not vary by region");
        var (code, message) = await S3Fidelity.ErrorPair(() => s3b.GetObjectAsync(bucket2, skewedKey), "the call SUCCEEDED where real AWS raises an error");
        Ensure(code == "NoSuchKey", $"the skewed PUT must not have written {Py.Repr(skewedKey)} in {region2}; get_object answered {Py.Repr(code)}");
        Ensure(message == "The specified key does not exist.", $"get_object of the absent key must say 'The specified key does not exist.', got {Py.Repr(message)}");

        // Positive control in region2: 240 s behind is inside the window.
        (url, host, path) = S3Fidelity.PathStyleTarget(region2, bucket2, controlKey);
        var insideAt = (await S3Fidelity.ServerNow(ctx, region2, bucket2)).AddSeconds(-240);
        Ensure(240 * 1000 < homeWindowMs, $"the control offset 240000 ms must be inside the {homeWindowMs} ms reported window");
        (status, _, body) = await S3Fidelity.Send(ctx, "PUT", url, S3Fidelity.SignHeaders(region2, "PUT", host, path, payload, insideAt), payload);
        Ensure(status == 200, $"a PUT signed 240 s behind {region2}'s clock is inside its window and must succeed, got {status}: {S3Fidelity.Head(body, 300)}");
        (url, host, path) = S3Fidelity.PathStyleTarget(region2, bucket2, controlKey);
        var (getStatus, _, got) = await S3Fidelity.Send(ctx, "GET", url, S3Fidelity.SignHeaders(region2, "GET", host, path, [], await S3Fidelity.ServerNow(ctx, region2, bucket2)));
        Ensure(getStatus == 200, $"signed GET of the {region2} control object returned {getStatus}");
        Ensure(got.SequenceEqual(payload), $"the {region2} control object served {S3Fidelity.Head(got)}, expected {S3Fidelity.Head(payload)}");
    }
}

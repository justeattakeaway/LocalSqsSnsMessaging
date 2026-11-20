// Translated from behavioral/s3/check_sigv4skew_future_dated_put_outside_window_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.sigv4skew-future-dated-put-outside-window-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class Sigv4skewFutureDatedPutOutsideWindowRejected
{
    /// <summary>
    /// A PutObject signed 1500 s ahead of S3's clock is refused RequestTimeTooSkewed, no write, while one
    /// signed 240 s ahead, inside the reported window, succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.sigv4skew-future-dated-put-outside-window-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var payload = "sigv4skew-future-dated-payload"u8.ToArray();
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        const string skewedKey = "sigv4skew/skewed.txt";
        const string controlKey = "sigv4skew/control.txt";

        // Break exactly one thing: the X-Amz-Date, 1500 s ahead the target's own clock.
        var skewedAt = (await S3Fidelity.ServerNow(ctx, ctx.Region, bucket)).AddSeconds(+1500);
        var (url, host, path) = S3Fidelity.PathStyleTarget(ctx.Region, bucket, skewedKey);
        var (status, _, body) = await S3Fidelity.Send(ctx, "PUT", url, S3Fidelity.SignHeaders(ctx.Region, "PUT", host, path, payload, skewedAt), payload);
        Ensure(status == 403, $"a PUT signed 1500 s ahead S3's clock must be refused 403, got {status}: {S3Fidelity.Head(body, 300)}");
        var err = S3Fidelity.XmlFields(body) ?? throw Fail($"expected an <Error> document, got {S3Fidelity.Head(body)}");
        Ensure(err["Code"] == "RequestTimeTooSkewed", $"expected RequestTimeTooSkewed, got {Py.Repr(err["Code"])} ({Py.Repr(err.GetValueOrDefault("Message"))})");
        Ensure(
            err["Message"] == "The difference between the request time and the current time is too large.",
            $"unexpected RequestTimeTooSkewed message {Py.Repr(err["Message"])}");
        Ensure(
            err["RequestTime"] == S3Fidelity.AmzDate(skewedAt),
            $"the refusal must echo the X-Amz-Date we signed ({Py.Repr(S3Fidelity.AmzDate(skewedAt))}), got {Py.Repr(err["RequestTime"])}");
        var serverTime = err["ServerTime"];
        Ensure(
            serverTime.Length > 0 && string.CompareOrdinal(serverTime, S3Fidelity.IsoSeconds(skewedAt)) < 0,
            "a future-dated refusal must report a ServerTime BEFORE the request time; got " +
            $"ServerTime={Py.Repr(serverTime)} against RequestTime={Py.Repr(err["RequestTime"])}");
        var windowMs = int.Parse(err["MaxAllowedSkewMilliseconds"], System.Globalization.CultureInfo.InvariantCulture);
        Ensure(
            windowMs is > 0 and < 1500 * 1000,
            $"S3 reported a tolerance of {windowMs} ms; for this to be a skew rejection at all the " +
            "1500000 ms offset we signed has to fall outside it");
        var (code, message) = await S3Fidelity.ErrorPair(() => s3.GetObjectAsync(bucket, skewedKey), "the call SUCCEEDED where real AWS raises an error");
        Ensure(code == "NoSuchKey", $"the skewed PUT must not have written {Py.Repr(skewedKey)}; get_object answered {Py.Repr(code)}");
        Ensure(message == "The specified key does not exist.", $"get_object of the absent key must say 'The specified key does not exist.', got {Py.Repr(message)}");

        // Positive control: 240 s ahead is still inside the window S3 just reported.
        var insideAt = (await S3Fidelity.ServerNow(ctx, ctx.Region, bucket)).AddSeconds(+240);
        Ensure(240 * 1000 < windowMs, $"the control offset 240000 ms must be inside the {windowMs} ms window S3 reported");
        (url, host, path) = S3Fidelity.PathStyleTarget(ctx.Region, bucket, controlKey);
        (status, _, body) = await S3Fidelity.Send(ctx, "PUT", url, S3Fidelity.SignHeaders(ctx.Region, "PUT", host, path, payload, insideAt), payload);
        Ensure(status == 200, $"a PUT signed 240 s ahead S3's clock is inside its window and must succeed, got {status}: {S3Fidelity.Head(body, 300)}");

        // Read the bytes back over the same raw signed path: 200 is acceptance, not effect.
        (url, host, path) = S3Fidelity.PathStyleTarget(ctx.Region, bucket, controlKey);
        var (getStatus, _, got) = await S3Fidelity.Send(ctx, "GET", url, S3Fidelity.SignHeaders(ctx.Region, "GET", host, path, [], await S3Fidelity.ServerNow(ctx, ctx.Region, bucket)));
        Ensure(getStatus == 200, $"signed GET of the control object returned {getStatus}: {S3Fidelity.Head(got, 300)}");
        Ensure(got.SequenceEqual(payload), $"control object served {S3Fidelity.Head(got)}, expected the bytes we wrote {S3Fidelity.Head(payload)}");
    }
}

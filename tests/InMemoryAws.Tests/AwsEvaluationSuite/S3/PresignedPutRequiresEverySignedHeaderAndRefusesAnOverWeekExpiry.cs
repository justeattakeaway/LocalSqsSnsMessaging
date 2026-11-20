// Translated from behavioral/s3/check_presigned_put_requires_every_signed_header_and_refuses_an_over_week_expiry.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.presigned-put-requires-every-signed-header-and-refuses-an-over-week-expiry</summary>
[Category("AwsEvaluationSuite")]
public sealed class PresignedPutRequiresEverySignedHeaderAndRefusesAnOverWeekExpiry
{
    /// <summary>
    /// A presigned PUT omitting a signed header is refused, as is X-Amz-Expires over one week. Omitting the
    /// signed SSE header gives 403 SignatureDoesNotMatch and stores nothing; resending it succeeds. Expiry
    /// of 604801 s gives 400 AuthorizationQueryParametersError.
    /// </summary>
    [Test]
    [DisplayName("s3.presigned-put-requires-every-signed-header-and-refuses-an-over-week-expiry")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const int weekSeconds = 604800;
        var payload = "presigned-put-payload-signed-headers-matter"u8.ToArray();
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);

        // ---------------- (a) signed headers ----------------
        const string controlKey = "presigned/control.bin";
        const string subjectKey = "presigned/subject.bin";
        var sseHeader = new Dictionary<string, string> { ["x-amz-server-side-encryption"] = "AES256" };
        var controlUrl = S3Fidelity.Presign(ctx, "PUT", bucket, controlKey, 900, signedHeaders: sseHeader);
        Ensure(
            controlUrl.Contains("X-Amz-SignedHeaders", StringComparison.Ordinal),
            "the presigned PUT URL carries no X-Amz-SignedHeaders query parameter, so there is no " +
            $"signed header to omit: {controlUrl.Split('?')[0]}?…");
        Ensure(
            controlUrl.Contains("server-side-encryption", StringComparison.OrdinalIgnoreCase),
            "X-Amz-SignedHeaders does not name x-amz-server-side-encryption, so the header this check " +
            $"omits was never signed: {controlUrl}");

        // Beat 1 — positive control. Without it, "the header-less PUT was refused" cannot be told from
        // "every presigned PUT is refused".
        var (status, _, body) = await S3Fidelity.Send(ctx, "PUT", controlUrl, sseHeader, payload);
        Ensure(status == 200, $"the presigned PUT that resends every signed header returned {status}: {S3Fidelity.Head(body)}");
        var head = await s3.GetObjectMetadataAsync(bucket, controlKey);
        Ensure(
            head.ServerSideEncryptionMethod == ServerSideEncryptionMethod.AES256,
            $"the control object reports ServerSideEncryption {Py.Repr(head.ServerSideEncryptionMethod?.Value)}, " +
            "expected 'AES256' — the signed header was accepted but not applied");
        Ensure((await S3Fidelity.GetBytes(s3, bucket, controlKey)).SequenceEqual(payload), "the control object's bytes do not match the presigned PUT body");

        // Beat 2 — break exactly one thing: omit the signed header. Same URL shape, same body.
        var subjectUrl = S3Fidelity.Presign(ctx, "PUT", bucket, subjectKey, 900, signedHeaders: sseHeader);
        (status, _, body) = await S3Fidelity.Send(ctx, "PUT", subjectUrl, data: payload);
        Ensure(
            status == 403,
            "a presigned PUT that OMITS the signed x-amz-server-side-encryption header returned " +
            $"{status}, expected 403 — the signature is not being verified over the signed headers, so " +
            $"any holder of the URL can upload without the encryption the issuer required: {S3Fidelity.Head(body)}");
        Ensure(
            S3Fidelity.XmlCodeMessage(body) == ("SignatureDoesNotMatch", S3Fidelity.SignatureMismatchMessage),
            $"the refused presigned PUT returned no SignatureDoesNotMatch error: {S3Fidelity.Head(body, 300)}");

        // Beat 3 — effect absent, by an independent read.
        Ensure(
            (await S3Fidelity.HeadError(s3, bucket, subjectKey)).Code == "404",
            $"{Py.Repr(subjectKey)} exists after the presigned PUT that omitted a signed header was refused");

        // Beat 4 — restore: the identical URL, with the header put back, succeeds.
        (status, _, body) = await S3Fidelity.Send(ctx, "PUT", subjectUrl, sseHeader, payload);
        Ensure(
            status == 200,
            $"re-sending the SAME presigned URL with the signed header restored returned {status}: " +
            $"{S3Fidelity.Head(body)} — the refusal above was not about the missing header");
        Ensure(
            (await S3Fidelity.GetBytes(s3, bucket, subjectKey)).SequenceEqual(payload),
            $"{Py.Repr(subjectKey)} does not hold the presigned PUT body after the restore leg");

        // ---------------- (b) the one-week expiry ceiling ----------------
        // An always-invalid-input negative: X-Amz-Expires can never become legal, so there is no
        // restore beat by construction. The positive control is still mandatory.
        var validGet = S3Fidelity.Presign(ctx, "GET", bucket, controlKey, 900);
        (status, _, body) = await S3Fidelity.Send(ctx, "GET", validGet);
        Ensure(
            status == 200 && body.SequenceEqual(payload),
            $"the in-window presigned GET returned {status} with {S3Fidelity.Head(body, 120)}; expected 200 and the " +
            "object's bytes");
        var overWeekGet = S3Fidelity.Presign(ctx, "GET", bucket, controlKey, weekSeconds + 1);
        Ensure(
            overWeekGet.Contains($"X-Amz-Expires={weekSeconds + 1}", StringComparison.Ordinal),
            $"the URL does not carry X-Amz-Expires={weekSeconds + 1}, so the ceiling is not being tested: {overWeekGet}");
        (status, _, body) = await S3Fidelity.Send(ctx, "GET", overWeekGet);
        Ensure(
            status == 400,
            $"a presigned GET with X-Amz-Expires={weekSeconds + 1} returned {status}, expected 400 — SigV4 " +
            $"query authentication caps the window at {weekSeconds}s, and a target that ignores the cap " +
            $"issues effectively permanent URLs: {S3Fidelity.Head(body)}");
        Ensure(
            S3Fidelity.XmlCodeMessage(body) == ("AuthorizationQueryParametersError",
                "X-Amz-Expires must be less than a week (in seconds); that is, the given X-Amz-Expires " +
                $"must be less than {weekSeconds} seconds"),
            $"the over-a-week presigned GET returned no AuthorizationQueryParametersError error: {S3Fidelity.Head(body, 300)}");
        Ensure(
            body.AsSpan().IndexOf(payload) < 0,
            "the refused over-a-week presigned GET still returned the object's bytes");
    }
}

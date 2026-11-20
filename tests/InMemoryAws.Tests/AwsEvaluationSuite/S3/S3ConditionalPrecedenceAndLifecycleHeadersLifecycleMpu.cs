// Translated from behavioral/s3/check_s3_conditional_precedence_and_lifecycle_headers_lifecycle_mpu.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-conditional-precedence-and-lifecycle-headers-lifecycle-mpu</summary>
/// <remarks>The raw headers botocore keeps in <c>ResponseMetadata.HTTPHeaders</c> are read from the response.</remarks>
[Category("AwsEvaluationSuite")]
public sealed class S3ConditionalPrecedenceAndLifecycleHeadersLifecycleMpu
{
    private const int Days = 3; // deliberately not 1, so a target hardcoding a 1-day window fails
    private const string RuleId = "sim2real-s3cond-abort-mpu-3d";
    private const string Prefix = "staging/";

    /// <summary>
    /// An AbortIncompleteMultipartUpload rule stamps x-amz-abort-date and x-amz-abort-rule-id on
    /// each matching upload, on CreateMultipartUpload and again on ListParts, and on no other upload.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-conditional-precedence-and-lifecycle-headers-lifecycle-mpu")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-s3cond-lcmpu-");
        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration
            {
                Rules =
                [
                    new LifecycleRule
                    {
                        Id = RuleId,
                        Filter = new LifecycleFilter { Prefix = Prefix },
                        Status = LifecycleRuleStatus.Enabled,
                        AbortIncompleteMultipartUpload = new LifecycleRuleAbortIncompleteMultipartUpload { DaysAfterInitiation = Days },
                    },
                ],
            },
        });
        var rule = (await s3.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = bucket }))
            .Configuration.Rules.Where(r => r.Id == RuleId).Single();
        var abortRule = S3Fidelity.Boto(rule).GetValueOrDefault("AbortIncompleteMultipartUpload");
        Ensure(
            Py.Equal(abortRule, new Dictionary<string, object?> { ["DaysAfterInitiation"] = Days }),
            $"the rule did not round-trip, got {Py.Repr(S3Fidelity.Boto(rule))}");

        // Bracket the create with local timestamps to bound the server-computed abort date.
        const string key = Prefix + "matching-upload.bin";
        var t0 = FidelityTime.UtcNow;
        var (created, hdrs) = await S3Fidelity.WithHeaders(s3, () => s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key }));
        var t1 = FidelityTime.UtcNow;
        var uploadId = created.UploadId;
        ctx.OnCleanup(() => Abort(s3, bucket, key, uploadId));

        Ensure(
            hdrs.GetValueOrDefault("x-amz-abort-rule-id") == RuleId,
            $"CreateMultipartUpload under {Py.Repr(Prefix)} must carry x-amz-abort-rule-id={Py.Repr(RuleId)}, "
            + $"got {Py.Repr(hdrs.GetValueOrDefault("x-amz-abort-rule-id"))}");
        var rawDate = hdrs.GetValueOrDefault("x-amz-abort-date");
        Ensure(
            rawDate is not null,
            $"CreateMultipartUpload under {Py.Repr(Prefix)} must carry x-amz-abort-date; headers were "
            + $"{Py.Repr(hdrs.Keys.Order(StringComparer.Ordinal).ToList())}");
        var abortDate = ParseHttpDate(rawDate);

        // The server's own Date header bounds the create on S3's clock; the local t0/t1 bound it on
        // ours. Take the wider of the two so ordinary clock skew cannot fail a faithful target.
        // S3 adds DaysAfterInitiation to the creation time and rounds up to the next midnight UTC.
        var serverDate = ParseHttpDate(hdrs["date"]);
        var earliest = (t0 < serverDate ? t0 : serverDate) + TimeSpan.FromDays(Days);
        var serverPlus = serverDate + TimeSpan.FromSeconds(1);
        var latest = (t1 > serverPlus ? t1 : serverPlus) + TimeSpan.FromDays(Days + 1);
        Ensure(
            earliest <= abortDate && abortDate <= latest,
            $"x-amz-abort-date {abortDate:O} must fall {Days}-{Days + 1} days after the upload was "
            + $"created (local {t0:O}..{t1:O}, server {serverDate:O})");
        Ensure(
            (abortDate.Hour, abortDate.Minute, abortDate.Second) == (0, 0, 0),
            $"x-amz-abort-date must be rounded to midnight UTC, got {Py.Repr(rawDate)}");
        Ensure(
            created.AbortDate is { } parsed && new DateTimeOffset(S3Fidelity.Utc(parsed)) == abortDate && created.AbortRuleId == RuleId,
            $"the SDK-parsed AbortDate/AbortRuleId {created.AbortDate:O}/"
            + $"{Py.Repr(created.AbortRuleId)} disagree with the raw headers {Py.Repr(rawDate)}/{Py.Repr(RuleId)}");

        var part = new byte[1024];
        Array.Fill(part, (byte)'p');
        await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartNumber = 1, InputStream = new MemoryStream(part) });
        var (listed, lhdrs) = await S3Fidelity.WithHeaders(s3, () => s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId }));
        Ensure(
            lhdrs.GetValueOrDefault("x-amz-abort-rule-id") == RuleId,
            $"ListParts must carry x-amz-abort-rule-id={Py.Repr(RuleId)}, got {Py.Repr(lhdrs.GetValueOrDefault("x-amz-abort-rule-id"))}");
        Ensure(
            lhdrs.GetValueOrDefault("x-amz-abort-date") is not null,
            $"ListParts must carry x-amz-abort-date; headers were {Py.Repr(lhdrs.Keys.Order(StringComparer.Ordinal).ToList())}");
        var listedDate = ParseHttpDate(lhdrs["x-amz-abort-date"]);
        Ensure(
            listedDate == abortDate,
            $"ListParts x-amz-abort-date {listedDate:O} differs from CreateMultipartUpload's "
            + $"{abortDate:O}; the date must be fixed at initiation, not recomputed per read");
        var partNumbers = (listed.Parts ?? []).Select(p => p.PartNumber).ToList();
        Ensure(partNumbers.SequenceEqual([1]), $"ListParts must show the one uploaded part, got {Py.Repr(partNumbers)}");

        const string missKey = "outside/control-upload.bin";
        var (miss, mhdrs) = await S3Fidelity.WithHeaders(s3, () => s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = missKey }));
        var missId = miss.UploadId;
        ctx.OnCleanup(() => Abort(s3, bucket, missKey, missId));
        var stray = Stray(mhdrs);
        Ensure(
            stray.Count == 0,
            $"an upload at {Py.Repr(missKey)} does not match prefix {Py.Repr(Prefix)} yet CreateMultipartUpload "
            + $"returned {Py.Repr(stray)}");
        var (_, mlist) = await S3Fidelity.WithHeaders(s3, () => s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = missKey, UploadId = missId }));
        stray = Stray(mlist);
        Ensure(stray.Count == 0, $"ListParts on the prefix-miss upload returned {Py.Repr(stray)}");
    }

    private static Dictionary<string, string> Stray(Dictionary<string, string> headers) =>
        new[] { "x-amz-abort-date", "x-amz-abort-rule-id" }.Where(headers.ContainsKey).ToDictionary(h => h, h => headers[h], StringComparer.Ordinal);

    /// <summary><c>email.utils.parsedate_to_datetime(value)</c> for an HTTP date.</summary>
    private static DateTimeOffset ParseHttpDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>Best-effort abort of one multipart upload (teardown only).</summary>
    private static async Task Abort(IAmazonS3 s3, string bucket, string key, string uploadId)
    {
        if (await FidelityContext.CaptureError(() => s3.AbortMultipartUploadAsync(bucket, key, uploadId)) is { Code: not ("NoSuchBucket" or "NoSuchKey" or "NoSuchUpload") } error)
        {
            throw error.Exception;
        }
    }
}

// Translated from behavioral/s3/check_object_lock_event_hold_requires_mode_duration_and_floor_date_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.S3;
using Amazon.S3.Model;
using InMemoryAws.Http;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-lock-event-hold-requires-mode-duration-and-floor-date-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectLockEventHoldRequiresModeDurationAndFloorDateRejected
{
    private const string Check = "object-lock-event-hold-requires-mode-duration-and-floor-date-rejected";
    private const string ModeRequiredMsg = "x-amz-object-lock-mode is required when x-amz-object-lock-event-hold is supplied.";
    private const string DurationRequiredMsg = "x-amz-object-lock-event-hold:ON must be supplied with exactly one event hold duration (days or years).";
    private const string FloorMsg = "The retain until date must be equal to or later than the current time plus the event hold duration.";

    /// <summary>
    /// Placing an object under an Object Lock event hold requires a retention Mode and exactly one event
    /// hold duration, and the retain-until date must already cover the hold duration; HeadObject and
    /// GetObject round-trip the hold state and can release it.
    /// </summary>
    [Test]
    [DisplayName("s3.object-lock-event-hold-requires-mode-duration-and-floor-date-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-behav-evthold-");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "sim2real", Value = Check }] });

        const string heldKey = "event-held.txt";
        var now = FidelityTime.UtcNow.UtcDateTime;
        var retainUntil = S3Fidelity.WholeSeconds(now + TimeSpan.FromDays(2) + TimeSpan.FromMinutes(10));

        var put = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = heldKey,
            ContentBody = "payload",
            ObjectLockMode = ObjectLockMode.Governance,
            ObjectLockRetainUntilDate = retainUntil,
            ObjectLockEventHold = "ON",
            ObjectLockEventHoldDurationDays = 1,
        });
        var versionId = put.VersionId;

        var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = heldKey });
        CheckHold("HeadObject", head.ObjectLockMode?.Value, head.ObjectLockEventHold?.Value, head.ObjectLockEventHoldDurationDays, head.ObjectLockRetainUntilDate);
        using (var get = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = heldKey }))
        {
            CheckHold("GetObject", get.ObjectLockMode?.Value, get.ObjectLockEventHold?.Value, get.ObjectLockEventHoldDurationDays, get.ObjectLockRetainUntilDate);
        }

        void CheckHold(string label, string? mode, string? eventHold, int? days, DateTime? gotUntil)
        {
            Ensure(mode == "GOVERNANCE", $"{label} mode {Py.Repr(mode)}");
            Ensure(eventHold == "ON", $"{label} event hold {Py.Repr(eventHold)}");
            Ensure(days == 1, $"{label} duration days {Py.Repr(days)}");
            Ensure(
                gotUntil is not null && Math.Abs((S3Fidelity.Utc(gotUntil) - retainUntil).TotalSeconds) <= 2,
                $"{label} retain-until {gotUntil:O}, expected near {retainUntil:O}");
        }

        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = heldKey,
            VersionId = versionId,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = retainUntil, EventHold = "OFF" },
        });
        var released = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = heldKey });
        Ensure(released.ObjectLockEventHold == "OFF", $"releasing the hold left ObjectLockEventHold={Py.Repr(released.ObjectLockEventHold?.Value)}");
        Ensure(released.ObjectLockMode == "GOVERNANCE", $"releasing the hold must not change the retention mode, got {Py.Repr(released.ObjectLockMode?.Value)}");

        var (code, message, _, _) = await Error(() => s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "no-mode.txt",
            ContentBody = "x",
            ObjectLockEventHold = "ON",
        }));
        Ensure(code == "InvalidRequest", $"event hold without a mode must be InvalidRequest, got {Py.Repr(code)}");
        Ensure(message == ModeRequiredMsg, $"unexpected message {Py.Repr(message)}");

        (code, message, _, _) = await Error(() => s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "no-duration.txt",
            ContentBody = "x",
            ObjectLockMode = ObjectLockMode.Governance,
            ObjectLockRetainUntilDate = retainUntil,
            ObjectLockEventHold = "ON",
        }));
        Ensure(code == "InvalidRequest", $"event hold without a duration must be InvalidRequest, got {Py.Repr(code)}");
        Ensure(message == DurationRequiredMsg, $"unexpected message {Py.Repr(message)}");

        var tooEarly = S3Fidelity.WholeSeconds(now + TimeSpan.FromMinutes(30));

        // The .NET SDK keeps no error element past Code and Message, so this PutObject, the one whose
        // ArgumentName and ArgumentValue are graded, is sent as the SDK sends it, over raw HTTP.
        (code, message, var argName, var argValue) = await ErrorWithArgument(ctx, bucket, "too-early.txt", "x", new Dictionary<string, string>
        {
            ["x-amz-object-lock-mode"] = "GOVERNANCE",
            ["x-amz-object-lock-retain-until-date"] = tooEarly.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["x-amz-object-lock-event-hold"] = "ON",
            ["x-amz-object-lock-event-hold-duration-days"] = "1",
        });
        Ensure(code == "InvalidArgument", $"a retain-until date under the hold floor must be InvalidArgument, got {Py.Repr(code)}");
        Ensure(message == FloorMsg, $"unexpected message {Py.Repr(message)}");
        Ensure(argName == "x-amz-object-lock-retain-until-date", $"unexpected ArgumentName {Py.Repr(argName)}");
        Ensure(argValue == tooEarly.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), $"unexpected ArgumentValue {Py.Repr(argValue)}");

        var listed = await ListKeys(s3, bucket);
        Ensure(listed.SequenceEqual([heldKey]), $"a refused creation left the bucket listing {Py.Repr(listed)}");
    }

    /// <summary>Every key, paginated.</summary>
    private static async Task<List<string>> ListKeys(IAmazonS3 s3, string bucket)
    {
        var keys = new List<string>();
        var request = new ListObjectsV2Request { BucketName = bucket };
        do
        {
            var page = await s3.ListObjectsV2Async(request);
            keys.AddRange((page.S3Objects ?? []).Select(o => o.Key));
            request.ContinuationToken = page.NextContinuationToken;
        }
        while (request.ContinuationToken is not null);

        return [.. keys.Order(StringComparer.Ordinal)];
    }

    /// <summary>(code, message) of the ClientError fn() raises; raises AssertionError on an unexpected success.</summary>
    private static async Task<(string? Code, string? Message, string? ArgumentName, string? ArgumentValue)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn) ?? throw Fail("expected the call to be rejected, but it succeeded");
        return (error.Code, error.Message, null, null);
    }

    /// <summary>
    /// (code, message, ArgumentName, ArgumentValue) of the error a PutObject with
    /// <paramref name="headers"/> raises, sent over raw HTTP with its body's Content-MD5; raises
    /// AssertionError on an unexpected success.
    /// </summary>
    private static async Task<(string? Code, string? Message, string? ArgumentName, string? ArgumentValue)> ErrorWithArgument(
        FidelityContext ctx, string bucket, string key, string body, Dictionary<string, string> headers)
    {
        var http = ctx.RawHttpClient(AwsServiceType.S3);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{bucket}/{key}")
        {
            Content = new StringContent(body),
        };
        request.Content.Headers.ContentType = null;
        request.Content.Headers.ContentMD5 = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(body));
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await http.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {(int)response.StatusCode}");
        }

        var document = System.Xml.Linq.XDocument.Parse(await response.Content.ReadAsStringAsync());
        string? Element(string name) => document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        return (Element("Code"), Element("Message"), Element("ArgumentName"), Element("ArgumentValue"));
    }
}

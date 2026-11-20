// Translated from behavioral/events__s3__sqs/check_s3_queue_notification_filter_delivers_only_matching_url_encoded_keys.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs.S3QueueNotificationKit;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.s3-queue-notification-filter-delivers-only-matching-url-encoded-keys</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3QueueNotificationFilterDeliversOnlyMatchingUrlEncodedKeys
{
    /// <summary>Bounded convergence for a delete to reach its terminal not-found answer.</summary>
    private const double GoneS = 40.0;

    /// <summary>
    /// A prefix and suffix filtered S3 queue notification delivers only matching keys, each as one
    /// ObjectCreated:Put record carrying the URL-encoded key, the object's size and its MD5 eTag;
    /// near-miss keys are never delivered, through a later matching control and a quiet window after it.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.s3-queue-notification-filter-delivers-only-matching-url-encoded-keys")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await CreateBucket(ctx, tagStrictly: true);
        var queue = await CreateQueue(ctx, tolerant: true);

        var notification = new List<QueueConfiguration>
        {
            QueueConfig(queue.Arn, ["s3:ObjectCreated:*"], null, ("prefix", "incoming/"), ("suffix", ".csv")),
        };
        await SetQueuePolicy(ctx, queue.Url, SqsPolicyForS3(queue.Arn, bucket.Arn));
        // The configuration is accepted once S3 may send to the queue (retried through propagation).
        await PutNotificationAfterPolicy(ctx, bucket.Name, notification);

        // Near misses first: wrong suffix, wrong prefix, and the prefix without its trailing slash.
        foreach (var miss in new[] { "incoming/report.csv.txt", "archive/incoming/report.csv", "incomingreport.csv" })
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = miss, ContentBody = "miss" });
        }

        var matches = new List<(string Key, byte[] Body)>
        {
            ("incoming/Q3 report+final ü.csv", RandomNumberGenerator.GetBytes(100 + RandomNumberGenerator.GetInt32(900))),
            ("incoming/deep/nested/éte.csv", RandomNumberGenerator.GetBytes(1 + RandomNumberGenerator.GetInt32(99))),
            // The last write is a matching control: its delivery bounds the near misses' absence.
            ("incoming/zz-control.csv", "control"u8.ToArray()),
        };
        foreach (var (key, body) in matches)
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = key, InputStream = new MemoryStream(body) });
        }

        var wantKeys = matches.Select(m => QuotePlus(m.Key)).ToHashSet(StringComparer.Ordinal);
        var records = await ReceiveRecords(
            ctx,
            queue.Url,
            rs => wantKeys.IsSubsetOf(rs.Select(r => Str(Object(r), "key")).OfType<string>()),
            Py.Repr(wantKeys.Order(StringComparer.Ordinal).ToList()));
        var got = Distinct(records)
            .Select(r => Row(Str(r, "eventName"), Str(Object(r), "key"), Size(Object(r)), Str(Object(r), "eTag"), Str(r["s3"]!["bucket"], "name")))
            .Order(StringComparer.Ordinal)
            .ToList();
        // Event keys are URL-encoded: space -> '+', '+' -> '%2B', non-ASCII -> its UTF-8 octets.
#pragma warning disable CA5351 // The check compares the eTag with the body's MD5.
        var want = matches
            .Select(m => Row("ObjectCreated:Put", QuotePlus(m.Key), m.Body.Length, Convert.ToHexStringLower(MD5.HashData(m.Body)), bucket.Name))
            .Order(StringComparer.Ordinal)
            .ToList();
#pragma warning restore CA5351
        Ensure(got.SequenceEqual(want), $"expected exactly the filtered records [{string.Join(", ", want)}], got [{string.Join(", ", got)}]");

        // Tear down in reverse dependency order and prove each resource gone.
        await GoneBucket(ctx, bucket.Name, ["404"], GoneS);
        await GoneQueue(ctx, queue.Name, queue.Url, GoneS);
    }

    /// <summary>A record's (eventName, key, size, eTag, bucket) tuple, rendered as Python would print it.</summary>
    private static string Row(string? name, string? key, long? size, string? etag, string? bucket) =>
        $"({Py.Repr(name)}, {Py.Repr(key)}, {(size is { } s ? s.ToString(CultureInfo.InvariantCulture) : "None")}, {Py.Repr(etag)}, {Py.Repr(bucket)})";

    private static long? Size(JsonObject obj) => obj["size"] is JsonValue v && v.TryGetValue<long>(out var size) ? size : null;

    /// <summary><c>urllib.parse.quote_plus(key, safe="/")</c>.</summary>
    private static string QuotePlus(string key)
    {
        var output = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(key))
        {
            var c = (char)b;
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~' or '/')
            {
                output.Append(c);
            }
            else if (c == ' ')
            {
                output.Append('+');
            }
            else
            {
                output.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return output.ToString();
    }
}

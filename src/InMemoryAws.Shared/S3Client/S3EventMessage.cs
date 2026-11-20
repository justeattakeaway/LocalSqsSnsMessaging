using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InMemoryAws.S3;

/// <summary>
/// One object event, as S3 describes it to its destinations: what happened (<c>ObjectCreated:Put</c>,
/// ...), to which version of which key, and the request that did it.
/// </summary>
/// <param name="EventName">The event, without the <c>s3:</c> prefix, e.g. <c>ObjectCreated:Put</c>.</param>
/// <param name="Bucket">The bucket's name.</param>
/// <param name="Region">The bucket's region.</param>
/// <param name="Key">The object's key, as written (not encoded).</param>
/// <param name="Time">When the event happened.</param>
/// <param name="RequestId">The request's id (<c>x-amz-request-id</c>).</param>
/// <param name="HostId">The request's extended id (<c>x-amz-id-2</c>).</param>
/// <param name="Account">The account that made the request, which owns the bucket.</param>
/// <param name="OwnerId">The bucket owner's canonical user id.</param>
/// <param name="SourceIp">Where the request came from.</param>
internal sealed record S3ObjectEvent(
    string EventName,
    string Bucket,
    string Region,
    string Key,
    DateTime Time,
    string RequestId,
    string HostId,
    string Account,
    string OwnerId,
    string SourceIp)
{
    /// <summary>The object's size, for an event about its data; null for a delete.</summary>
    public long? Size { get; init; }

    /// <summary>The object's ETag, unquoted; null for a delete.</summary>
    public string? ETag { get; init; }

    /// <summary>The version's id, in a bucket that has ever been versioned; otherwise null.</summary>
    public string? VersionId { get; init; }

    /// <summary>The position of a create or delete in the key's history (upper-case hex); null for other events.</summary>
    public string? Sequencer { get; init; }

    /// <summary>For a create, how the object was written as EventBridge names it (<c>PutObject</c>, <c>POST Object</c>, ...).</summary>
    public string? Reason { get; init; }

    /// <summary>For a restore, the object's storage class.</summary>
    public string? StorageClass { get; init; }

    public string BucketArn => "arn:aws:s3:::" + Bucket;
}

/// <summary>
/// The messages S3 sends its notification destinations: the <c>s3:TestEvent</c> a new destination
/// receives, the event record (<c>{"Records": [...]}</c>) queues and topics receive, and the
/// EventBridge event (<c>source</c> <c>aws.s3</c>) put on the default bus.
/// </summary>
internal static class S3EventMessage
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The subject S3 publishes to an SNS topic with.</summary>
    public const string Subject = "Amazon S3 Notification";

    /// <summary>The flat test event S3 sends a destination when a configuration adds it.</summary>
    public static string TestEvent(string bucket, DateTime time, string requestId, string hostId) =>
        new JsonObject
        {
            ["Service"] = "Amazon S3",
            ["Event"] = "s3:TestEvent",
            ["Time"] = FormatTime(time),
            ["Bucket"] = bucket,
            ["RequestId"] = requestId,
            ["HostId"] = hostId,
        }.ToJsonString(Options);

    /// <summary>The event record message (version 2.1) a queue or topic configuration receives.</summary>
    public static string Records(S3ObjectEvent e, string? configurationId)
    {
        var @object = new JsonObject { ["key"] = EncodeKey(e.Key) };
        if (e.Size is { } size)
        {
            @object["size"] = size;
        }

        if (e.ETag is { } etag)
        {
            @object["eTag"] = etag;
        }

        if (e.VersionId is { } versionId)
        {
            @object["versionId"] = versionId;
        }

        if (e.Sequencer is { } sequencer)
        {
            @object["sequencer"] = sequencer;
        }

        var record = new JsonObject
        {
            ["eventVersion"] = "2.1",
            ["eventSource"] = "aws:s3",
            ["awsRegion"] = e.Region,
            ["eventTime"] = FormatTime(e.Time),
            ["eventName"] = e.EventName,
            ["userIdentity"] = new JsonObject { ["principalId"] = "AWS:" + e.Account },
            ["requestParameters"] = new JsonObject { ["sourceIPAddress"] = e.SourceIp },
            ["responseElements"] = new JsonObject
            {
                ["x-amz-request-id"] = e.RequestId,
                ["x-amz-id-2"] = e.HostId,
            },
            ["s3"] = new JsonObject
            {
                ["s3SchemaVersion"] = "1.0",
                ["configurationId"] = configurationId,
                ["bucket"] = new JsonObject
                {
                    ["name"] = e.Bucket,
                    ["ownerIdentity"] = new JsonObject { ["principalId"] = e.OwnerId },
                    ["arn"] = e.BucketArn,
                },
                ["object"] = @object,
            },
        };
        if (e.EventName.StartsWith("ObjectRestore:", StringComparison.Ordinal) && e.StorageClass is not null)
        {
            record["glacierEventData"] = new JsonObject
            {
                ["restoreEventData"] = new JsonObject { ["lifecycleRestoreStorageClass"] = e.StorageClass },
            };
        }

        return new JsonObject { ["Records"] = new JsonArray(record) }.ToJsonString(Options);
    }

    /// <summary>
    /// The EventBridge <c>detail-type</c> and <c>detail</c> of an event, or null for an event S3
    /// doesn't send to EventBridge.
    /// </summary>
    public static (string DetailType, JsonObject Detail)? EventBridge(S3ObjectEvent e)
    {
        var family = e.EventName.Split(':')[0];
        var detailType = e.EventName switch
        {
            "ObjectTagging:Put" => "Object Tags Added",
            "ObjectTagging:Delete" => "Object Tags Deleted",
            "ObjectAcl:Put" => "Object ACL Updated",
            "ObjectRestore:Post" => "Object Restore Initiated",
            "ObjectRestore:Completed" => "Object Restore Completed",
            "ObjectRestore:Delete" => "Object Restore Expired",
            _ => family switch
            {
                "ObjectCreated" => "Object Created",
                "ObjectRemoved" => "Object Deleted",
                _ => null,
            },
        };
        if (detailType is null)
        {
            return null;
        }

        var @object = new JsonObject { ["key"] = e.Key };
        if (e.Size is { } size && detailType is "Object Created" or "Object Restore Initiated" or "Object Restore Completed")
        {
            @object["size"] = size;
        }

        if (e.ETag is { } etag)
        {
            @object["etag"] = etag;
        }

        if (e.VersionId is { } versionId)
        {
            @object["version-id"] = versionId;
        }

        if (e.Sequencer is { } sequencer)
        {
            @object["sequencer"] = sequencer;
        }

        var detail = new JsonObject
        {
            ["version"] = "0",
            ["bucket"] = new JsonObject { ["name"] = e.Bucket },
            ["object"] = @object,
            ["request-id"] = e.RequestId,
            ["requester"] = e.Account,
            ["source-ip-address"] = e.SourceIp,
        };
        switch (detailType)
        {
            case "Object Created":
                detail["reason"] = e.Reason ?? e.EventName["ObjectCreated:".Length..] switch
                {
                    "Put" => "PutObject",
                    "Post" => "POST Object",
                    "Copy" => "CopyObject",
                    var other => other,
                };
                break;
            case "Object Deleted":
                detail["reason"] = e.Reason ?? "DeleteObject";
                detail["deletion-type"] = e.EventName == "ObjectRemoved:DeleteMarkerCreated" ? "Delete Marker Created" : "Permanently Deleted";
                break;
            case "Object Restore Initiated":
                detail["source-storage-class"] = e.StorageClass;
                break;
        }

        return (detailType, detail);
    }

    /// <summary>
    /// A key as event records carry it: URL-encoded as a form value, so a space is <c>+</c>, and
    /// every byte of its UTF-8 but letters, digits, <c>-_.</c> and <c>/</c> is <c>%XX</c> (S3 escapes
    /// <c>~</c> too).
    /// </summary>
    public static string EncodeKey(string key)
    {
        var output = new StringBuilder(key.Length);
        foreach (var b in Encoding.UTF8.GetBytes(key))
        {
            var c = (char)b;
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.' or '/')
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

    /// <summary>An event time: UTC to the millisecond, e.g. <c>2024-03-04T05:06:07.089Z</c>.</summary>
    public static string FormatTime(DateTime utc) =>
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

// Translated from behavioral/s3/check_directory_bucket_rejects_object_tagging_versioning_notification_and_replication.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.directory-bucket-rejects-object-tagging-versioning-notification-and-replication</summary>
[Category("AwsEvaluationSuite")]
public sealed class DirectoryBucketRejectsObjectTaggingVersioningNotificationAndReplication
{
    private const string Check = "directory-bucket-rejects-object-tagging-versioning-notification-and-replication";

    /// <summary>
    /// S3 Express One Zone Availability-Zone IDs per region. Zone IDs (not names) are physical and
    /// identical in every account, so this table holds for any account.
    /// </summary>
    private static readonly Dictionary<string, string[]> S3ExpressAzIds = new()
    {
        ["us-east-1"] = ["use1-az4", "use1-az5", "use1-az6"],
        ["us-east-2"] = ["use2-az1", "use2-az2"],
        ["us-west-2"] = ["usw2-az1", "usw2-az3", "usw2-az4"],
        ["ap-south-1"] = ["aps1-az1", "aps1-az3"],
        ["ap-northeast-1"] = ["apne1-az1", "apne1-az4"],
        ["eu-central-1"] = ["euc1-az1", "euc1-az2"],
        ["eu-west-1"] = ["euw1-az1", "euw1-az3"],
        ["eu-north-1"] = ["eun1-az1", "eun1-az2", "eun1-az3"],
    };

    /// <summary>
    /// A directory bucket refuses PutObjectTagging, PutBucketVersioning,
    /// PutBucketNotificationConfiguration and PutBucketReplication with their exact errors, while a
    /// general-purpose twin accepts the same tagging, versioning and notification calls.
    /// </summary>
    [Test]
    [DisplayName("s3.directory-bucket-rejects-object-tagging-versioning-notification-and-replication")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var directory = await DirectoryBucket(ctx, s3, "sim2real-s3x-cfg-");
        var twin = await GeneralPurposeBucket(ctx, s3, "sim2real-s3x-cfg-gp-");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = directory, Key = "a.txt", ContentBody = "hi" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = twin, Key = "a.txt", ContentBody = "hi" });

        var result = await Error(() => s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = directory,
            Key = "a.txt",
            Tagging = new Tagging { TagSet = [new Tag { Key = "k", Value = "v" }] },
        }));
        Ensure(
            result == ("NotImplemented", "This bucket does not support Object Tags."),
            $"PutObjectTagging on a directory bucket returned {result}");
        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = twin,
            Key = "a.txt",
            Tagging = new Tagging { TagSet = [new Tag { Key = "k", Value = "v" }] },
        });
        var twinTags = (await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = twin, Key = "a.txt" })).Tagging
            .Select(t => (t.Key, t.Value)).Order().ToList();
        Ensure(twinTags.SequenceEqual([("k", "v")]), $"the general-purpose twin's tag did not land: {Py.Repr(twinTags.Select(t => new[] { t.Key, t.Value }).ToList())}");

        result = await Error(() => s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = directory,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        }));
        Ensure(
            result == ("InvalidArgument", "VersioningConfiguration is not valid, expected CreateBucketConfiguration"),
            $"PutBucketVersioning on a directory bucket returned {result}");
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = twin,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        Ensure(
            (await s3.GetBucketVersioningAsync(twin)).VersioningConfig?.Status == VersionStatus.Enabled,
            "the general-purpose twin's PutBucketVersioning did not take effect");

        result = await Error(() => s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = directory }));
        Ensure(
            result == ("InvalidArgument", "NotificationConfiguration is not valid, expected CreateBucketConfiguration"),
            $"PutBucketNotificationConfiguration on a directory bucket returned {result}");
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = twin });
        var twinNotification = await s3.GetBucketNotificationAsync(twin);
        var configured = new List<string>();
        if (twinNotification.LambdaFunctionConfigurations is { Count: > 0 })
        {
            configured.Add("LambdaFunctionConfigurations");
        }

        if (twinNotification.QueueConfigurations is { Count: > 0 })
        {
            configured.Add("QueueConfigurations");
        }

        if (twinNotification.TopicConfigurations is { Count: > 0 })
        {
            configured.Add("TopicConfigurations");
        }

        Ensure(
            configured.Count == 0,
            $"the general-purpose twin's empty notification configuration read back {Py.Repr(configured)}");

        result = await Error(() => s3.PutBucketReplicationAsync(new PutBucketReplicationRequest
        {
            BucketName = directory,
            Configuration = new ReplicationConfiguration
            {
                Role = $"arn:aws:iam::{ctx.Account}:role/sim2real-does-not-need-to-exist",
                Rules =
                [
                    new ReplicationRule
                    {
                        Id = "r1",
                        Status = ReplicationRuleStatus.Enabled,
                        Priority = 1,
                        DeleteMarkerReplication = new DeleteMarkerReplication { Status = DeleteMarkerReplicationStatus.Disabled },
                        Filter = new ReplicationRuleFilter { Prefix = string.Empty },
                        Destination = new ReplicationDestination { BucketArn = $"arn:aws:s3:::{FidelityContext.UniqueName("sim2real-s3x-cfg-dest-")}" },
                    },
                ],
            },
        }));
        Ensure(
            result == ("InvalidArgument", "ReplicationConfiguration is not valid, expected CreateBucketConfiguration"),
            $"PutBucketReplication on a directory bucket returned {result}");
    }

    /// <summary>Create an S3 Express directory bucket, registering its teardown in the same breath.</summary>
    private static async Task<string> DirectoryBucket(FidelityContext ctx, AmazonS3Client s3, string prefix)
    {
        if (!S3ExpressAzIds.TryGetValue(ctx.Region, out var azs))
        {
            throw Fail($"S3 Express One Zone AZ IDs not known for region {Py.Repr(ctx.Region)}");
        }

        var baseName = FidelityContext.UniqueName(prefix);
        AwsError? last = null;
        foreach (var az in azs)
        {
            var name = $"{baseName}--{az}--x-s3";
            last = await FidelityContext.CaptureError(() => s3.PutBucketAsync(new PutBucketRequest
            {
                BucketName = name,
                UseClientRegion = false,
                PutBucketConfiguration = new PutBucketConfiguration
                {
                    Location = new LocationInfo { Type = LocationType.AvailabilityZone, Name = az },
                    BucketInfo = new BucketInfo { DataRedundancy = DataRedundancy.SingleAvailabilityZone, Type = BucketType.Directory },
                },
            }));
            if (last is not null)
            {
                continue;
            }

            ctx.OnCleanup(() => EmptyAndDeleteDirectoryBucket(s3, name));
            return name;
        }

        throw Fail($"could not create a directory bucket in any of {Py.Repr(azs)}: {last}");
    }

    /// <summary>Directory buckets support neither versioning nor ListObjectVersions.</summary>
    private static async Task EmptyAndDeleteDirectoryBucket(AmazonS3Client s3, string bucket)
    {
        var paginator = s3.Paginators.ListObjectsV2(new ListObjectsV2Request { BucketName = bucket });
        await foreach (var page in paginator.Responses)
        {
            foreach (var obj in page.S3Objects ?? [])
            {
                await s3.DeleteObjectAsync(bucket, obj.Key);
            }
        }

        await s3.DeleteBucketAsync(bucket);
    }

    private static async Task<string> GeneralPurposeBucket(FidelityContext ctx, AmazonS3Client s3, string prefix)
    {
        var name = await S3Fidelity.CreateBucket(ctx, prefix, s3);
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = name, TagSet = [new Tag { Key = "sim2real", Value = Check }] });
        return name;
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success raises.</summary>
    private static async Task<(string?, string?)> Error(Func<Task> fn)
    {
        var err = await FidelityContext.CaptureError(fn);
        if (err is null)
        {
            throw Fail("expected the call to be refused, but it succeeded: {}");
        }

        return (err.Code, err.Message);
    }
}

// Translated from behavioral/events__s3__sqs/check_obs_s3_3_object_acl_put_reaches_both_paths.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs.ObsS3EventPaths;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.obs-s3-3-object-acl-put-reaches-both-paths</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObsS33ObjectAclPutReachesBothPaths
{
    private const string Check = "obs-s3-3-object-acl-put-reaches-both-paths";

    /// <summary>
    /// PutObjectAcl reaches both delivery paths: classic <c>ObjectAcl:Put</c> and EventBridge
    /// "Object ACL Updated", both naming the request and the object's ETag; the grant reads back at S3.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.obs-s3-3-object-acl-put-reaches-both-paths")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var paths = new ObsS3EventPaths(ctx, Check);
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var ev = ctx.Events;
        var account = ctx.Account;
        const string partition = "aws";

        var bucket = await paths.Bucket(s3);
        // A new bucket defaults to BucketOwnerEnforced, which rejects every PutObjectAcl.
        await s3.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
        {
            BucketName = bucket,
            OwnershipControls = new OwnershipControls { Rules = [new OwnershipControlsRule { ObjectOwnership = ObjectOwnership.BucketOwnerPreferred }] },
        });
        var own = (await s3.GetBucketOwnershipControlsAsync(new GetBucketOwnershipControlsRequest { BucketName = bucket })).OwnershipControls.Rules
            .Select(r => r.ObjectOwnership?.Value).ToList();
        Ensure(own.SequenceEqual(["BucketOwnerPreferred"]), $"ownership read back {Py.Repr(own)}");

        var (curl, carn) = await paths.Queue(sqs);
        await Allow(sqs, curl, carn, "s3.amazonaws.com", $"arn:{partition}:s3:::{bucket}", account);
        var (eurl, _) = await paths.RuleToQueue(ev, sqs, new JsonObject
        {
            ["source"] = new JsonArray("aws.s3"),
            ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket) } },
        }, account);
        await paths.WireBucket(s3, bucket, [(carn, ["s3:ObjectCreated:Put", "s3:ObjectAcl:Put"])]);
        var (classic, bridge) = (new Inbox(sqs, curl, "classic"), new Inbox(sqs, eurl, "eventbridge"));

        async Task<string> Fire(int i)
        {
            var k = $"warm-{i}";
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = k, ContentBody = "sim2real-warm" });
            return k;
        }

        await WarmUp(Fire, [(classic, k => Classic(k, "ObjectCreated:Put")), (bridge, k => Bridge(k, "Object Created"))]);

        var key = FidelityContext.UniqueName("acl-");
        var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "sim2real-acl" });
        var etag = put.ETag.Trim('"');
        await paths.Await(classic, Classic(key, "ObjectCreated:Put"), "classic ObjectCreated:Put warm-up");
        await paths.Await(bridge, Bridge(key, "Object Created"), "EventBridge 'Object Created' warm-up");

        var acl = await s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = bucket, Key = key });
        var owner = acl.Owner;
        var before = acl.Grants.Select(g => (g.Grantee?.CanonicalUser, g.Permission?.Value)).ToHashSet();
        Ensure(before.SetEquals([(owner.Id, "FULL_CONTROL")]), $"initial object ACL grants {Py.Repr(before.ToList())}");

        var resp = await s3.PutObjectAclAsync(new PutObjectAclRequest
        {
            BucketName = bucket,
            Key = key,
            AccessControlPolicy = new S3AccessControlList
            {
                Owner = new Owner { Id = owner.Id },
                Grants =
                [
                    new S3Grant { Grantee = new S3Grantee { CanonicalUser = owner.Id }, Permission = S3Permission.FULL_CONTROL },
                    new S3Grant { Grantee = new S3Grantee { CanonicalUser = owner.Id }, Permission = S3Permission.READ_ACP },
                ],
            },
        });
        var req = resp.ResponseMetadata.RequestId;
        var after = (await s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = bucket, Key = key })).Grants
            .Select(g => (g.Grantee?.CanonicalUser, g.Permission?.Value)).ToHashSet();
        Ensure(
            after.SetEquals([(owner.Id, "FULL_CONTROL"), (owner.Id, "READ_ACP")]),
            $"the ACL written must be readable back; GetObjectAcl reports {Py.Repr(after.ToList())}");

        var crec = (await paths.Await(classic, Classic(key, "ObjectAcl:Put"), "classic ObjectAcl:Put"))[0];
        var eenv = (await paths.Await(bridge, Bridge(key, "Object ACL Updated"), "EventBridge 'Object ACL Updated'"))[0];
        Ensure((string?)crec["eventSource"] == "aws:s3", $"eventSource was {Py.Repr((string?)crec["eventSource"])}");
        Ensure((string?)crec["s3"]!["bucket"]!["name"] == bucket, $"bucket {Py.Repr((string?)crec["s3"]!["bucket"]!["name"])}");
        Ensure(
            (string?)crec["s3"]!["object"]!["eTag"] == etag,
            $"classic ObjectAcl:Put eTag {Py.Repr((string?)crec["s3"]!["object"]!["eTag"])} != object ETag {Py.Repr(etag)}");
        Ensure(
            (string?)crec["responseElements"]!["x-amz-request-id"] == req,
            $"classic ObjectAcl:Put names request {Py.Repr((string?)crec["responseElements"]!["x-amz-request-id"])}, but PutObjectAcl was request {Py.Repr(req)}");
        Ensure((string?)eenv["source"] == "aws.s3", $"source was {Py.Repr((string?)eenv["source"])}");
        Ensure((string?)eenv["detail"]!["bucket"]!["name"] == bucket, $"bucket {eenv["detail"]!["bucket"]!.ToJsonString()}");
        Ensure(
            (string?)eenv["detail"]!["object"]!["etag"] == etag,
            $"'Object ACL Updated' etag {Py.Repr((string?)eenv["detail"]!["object"]!["etag"])} != {Py.Repr(etag)}");
        Ensure(
            (string?)eenv["detail"]!["request-id"] == req,
            $"'Object ACL Updated' names request {Py.Repr((string?)eenv["detail"]!["request-id"])}, expected {Py.Repr(req)}");

        var cnames = Classic(key)(classic.Seen).Select(r => (string?)r["eventName"]).ToHashSet();
        var enames = Bridge(key)(bridge.Seen).Select(e => (string?)e["detail-type"]).ToHashSet();
        Ensure(cnames.SetEquals(["ObjectCreated:Put", "ObjectAcl:Put"]), $"classic path delivered event names {Py.Repr(cnames.Order().ToList())} for {Py.Repr(key)}");
        Ensure(enames.SetEquals(["Object Created", "Object ACL Updated"]), $"EventBridge path delivered detail-types {Py.Repr(enames.Order().ToList())} for {Py.Repr(key)}");
    }
}

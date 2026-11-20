// Translated from behavioral/events__s3__sqs/check_obs_s3_3_acl_rejected_under_bucket_owner_enforced_emits_no_event.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs.ObsS3EventPaths;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.obs-s3-3-acl-rejected-under-bucket-owner-enforced-emits-no-event</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObsS33AclRejectedUnderBucketOwnerEnforcedEmitsNoEvent
{
    private const string Check = "obs-s3-3-acl-rejected-under-bucket-owner-enforced-emits-no-event";

    private const string AclRefusal = "The bucket does not allow ACLs";

    /// <summary>
    /// Under BucketOwnerEnforced, PutObjectAcl is rejected, the ACL is unchanged and neither the
    /// classic nor the EventBridge path carries an ACL event for it; after BucketOwnerPreferred the
    /// same call succeeds and both paths deliver an event naming the successful request id.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.obs-s3-3-acl-rejected-under-bucket-owner-enforced-emits-no-event")]
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
        var own = (await s3.GetBucketOwnershipControlsAsync(new GetBucketOwnershipControlsRequest { BucketName = bucket })).OwnershipControls.Rules
            .Select(r => r.ObjectOwnership?.Value).ToList();
        Ensure(own.SequenceEqual(["BucketOwnerEnforced"]), $"a new bucket must default to BucketOwnerEnforced; ownership reads {Py.Repr(own)}");
        var (curl, carn) = await paths.Queue(sqs);
        await Allow(sqs, curl, carn, "s3.amazonaws.com", $"arn:{partition}:s3:::{bucket}", account);
        var (eurl, _) = await paths.RuleToQueue(ev, sqs, new JsonObject
        {
            ["source"] = new JsonArray("aws.s3"),
            ["detail-type"] = new JsonArray("Object ACL Updated", "Object Tags Added"),
            ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket) } },
        }, account);
        await paths.WireBucket(s3, bucket, [(carn, ["s3:ObjectAcl:Put", "s3:ObjectTagging:Put"])]);
        var (classic, bridge) = (new Inbox(sqs, curl, "classic"), new Inbox(sqs, eurl, "eventbridge"));

        async Task<string> Fire(int i)
        {
            var k = $"warm-{i}";
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = k, ContentBody = "sim2real-warm" });
            await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest { BucketName = bucket, Key = k, Tagging = new Tagging { TagSet = [new Tag { Key = "w", Value = "1" }] } });
            return k;
        }

        await WarmUp(Fire, [(classic, k => Classic(k, "ObjectTagging:Put")), (bridge, k => Bridge(k, "Object Tags Added"))]);

        var key = FidelityContext.UniqueName("acl-");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "sim2real-acl-rejected" });
        var owner = (await s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = bucket, Key = key })).Owner.Id;
        var policy = new S3AccessControlList
        {
            Owner = new Owner { Id = owner },
            Grants =
            [
                new S3Grant { Grantee = new S3Grantee { CanonicalUser = owner }, Permission = S3Permission.FULL_CONTROL },
                new S3Grant { Grantee = new S3Grantee { CanonicalUser = owner }, Permission = S3Permission.READ_ACP },
            ],
        };

        // --- the rejected call ----------------------------------------------------------------
        var rejectedIds = new List<string>();
        var err = await FidelityContext.CaptureError(() => s3.PutObjectAclAsync(new PutObjectAclRequest { BucketName = bucket, Key = key, AccessControlPolicy = policy }))
            ?? throw Fail("the call was expected to be rejected but succeeded");
        rejectedIds.Add(err.Exception.RequestId);
        var (code, status, message) = (err.Code, (int)err.StatusCode, err.Message);
        Ensure(
            (code, status, message) == ("AccessControlListNotSupported", 400, AclRefusal),
            $"PutObjectAcl under BucketOwnerEnforced returned {Py.Repr(code)}/{status} {Py.Repr(message)}, expected " +
            $"AccessControlListNotSupported/400 {Py.Repr(AclRefusal)}");
        var grants = (await s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = bucket, Key = key })).Grants
            .Select(g => (g.Grantee?.CanonicalUser, g.Permission?.Value)).ToHashSet();
        Ensure(grants.SetEquals([(owner, "FULL_CONTROL")]), $"a rejected PutObjectAcl changed the ACL to {Py.Repr(grants.ToList())}");

        // --- barrier: a later event on the same routes arrives --------------------------------
        var t0 = FidelityTime.StartNew();
        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest { BucketName = bucket, Key = key, Tagging = new Tagging { TagSet = [new Tag { Key = "k", Value = "v" }] } });
        await paths.Await(classic, Classic(key, "ObjectTagging:Put"), "classic ObjectTagging:Put (barrier)");
        await paths.Await(bridge, Bridge(key, "Object Tags Added"), "EventBridge 'Object Tags Added' (barrier)");
        var window = AbsenceWindow(t0.Elapsed.TotalSeconds);
        await classic.Soak(window / 2);
        await bridge.Soak(window / 2);
        var cAcl = Classic(key, "ObjectAcl:Put")(classic.Seen);
        var eAcl = Bridge(key, "Object ACL Updated")(bridge.Seen);
        Ensure(
            cAcl.Count == 0 && eAcl.Count == 0,
            $"a REJECTED PutObjectAcl was delivered: classic {cAcl.Count} ObjectAcl:Put, EventBridge " +
            $"{eAcl.Count} 'Object ACL Updated' over {window:F0}s past the barrier");

        // --- closure: repair the one thing, and the same queues receive the ACL event ---------
        await s3.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
        {
            BucketName = bucket,
            OwnershipControls = new OwnershipControls { Rules = [new OwnershipControlsRule { ObjectOwnership = ObjectOwnership.BucketOwnerPreferred }] },
        });
        // The ownership change is the only thing repaired; its propagation is retried on exactly the
        // code it cures, and every refused attempt's request id joins the rejected set.
        var started = FidelityTime.StartNew();
        string okReq;
        while (true)
        {
            var (resp, error) = await FidelityContext.Capture(() => s3.PutObjectAclAsync(new PutObjectAclRequest { BucketName = bucket, Key = key, AccessControlPolicy = policy }));
            if (resp is not null)
            {
                okReq = resp.ResponseMetadata.RequestId;
                break;
            }

            if (error!.Code != "AccessControlListNotSupported" || started.Elapsed.TotalSeconds >= 30.0)
            {
                throw error.Exception;
            }

            rejectedIds.Add(error.Exception.RequestId);
            await ctx.Delay(TimeSpan.FromSeconds(3));
        }

        grants = (await s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = bucket, Key = key })).Grants
            .Select(g => (g.Grantee?.CanonicalUser, g.Permission?.Value)).ToHashSet();
        Ensure(grants.SetEquals([(owner, "FULL_CONTROL"), (owner, "READ_ACP")]), $"repaired ACL reads {Py.Repr(grants.ToList())}");
        await paths.Await(classic, Classic(key, "ObjectAcl:Put"), "classic ObjectAcl:Put after repair (closure)");
        await paths.Await(bridge, Bridge(key, "Object ACL Updated"), "EventBridge ACL event after repair (closure)");
        await classic.Soak(2);
        await bridge.Soak(2);
        var cIds = Classic(key, "ObjectAcl:Put")(classic.Seen).Select(r => (string?)r["responseElements"]!["x-amz-request-id"]).ToHashSet();
        var eIds = Bridge(key, "Object ACL Updated")(bridge.Seen).Select(e => (string?)e["detail"]!["request-id"]).ToHashSet();
        Ensure(
            cIds.SetEquals([okReq]) && eIds.SetEquals([okReq]),
            $"every ACL event must name the successful request {Py.Repr(okReq)} (the rejected one was " +
            $"{Py.Repr(rejectedIds)}); classic named {Py.Repr(cIds.Order().ToList())}, EventBridge named {Py.Repr(eIds.Order().ToList())}");
    }
}

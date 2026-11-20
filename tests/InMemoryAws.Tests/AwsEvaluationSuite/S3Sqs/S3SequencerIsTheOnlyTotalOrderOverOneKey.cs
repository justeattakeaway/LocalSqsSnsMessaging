// Translated from behavioral/s3__sqs/check_s3_sequencer_is_the_only_total_order_over_one_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs;

/// <summary>s3__sqs.s3-sequencer-is-the-only-total-order-over-one-key</summary>
[Category("AwsEvaluationSuite")]
public sealed partial class S3SequencerIsTheOnlyTotalOrderOverOneKey
{
    private const string Check = "s3-sequencer-is-the-only-total-order-over-one-key";
    private const int Puts = 16;
    private const int Deletes = 8;
    private const int Ops = Puts + Deletes;

    // One key with both a space and a '+': in a notification body S3 URL-encodes a space as '+'
    // and a literal '+' as '%2B'.
    //
    // Every destination in this check is prefix-scoped, and the prefixes are disjoint by construction.
    // That is not cosmetic: S3 refuses two destinations that share an event type with overlapping
    // prefixes, with the SAME error code (InvalidArgument) as it uses for an undeliverable destination.
    // Overlapping prefixes would therefore have made the negative arm below pass for the wrong reason.
    private const string Ord = "ord/";
    private const string Deny = "deny/";
    private const string Ctl = "ctl/";
    private const string Key = Ord + "seq test+key.txt";
    private const string KeyEncoded = Ord + "seq+test%2Bkey.txt";

    // InvalidArgument also covers overlapping destinations, so the message is asserted:
    //   destination not granted sqs:SendMessage -> "Unable to validate the following destination
    //                                              configurations"
    //   two destinations overlapping            -> "Configuration is ambiguously defined. Cannot have
    //                                              overlapping suffixes ..."
    private const string Undeliverable = "Unable to validate the following destination configurations";

    // Fixed seed: the interleaving of PUTs and DELETEs in the submitted plan is the same every run.
    private const int PlanSeed = 20240917;

    // The silence window is a fixed length sampled by several short long-polls.
    private const double SilenceWindowS = 10.0;
    private const int SilencePollS = 3;

    [GeneratedRegex("^[0-9A-F]+$")]
    private static partial Regex Hex();

    /// <summary>
    /// For 24 concurrent operations on one key, the notification `sequencer` is a total order that
    /// matches ListObjectVersions and whose maximum is the key's final state; a destination S3
    /// cannot deliver to is refused synchronously, with no partial effect.
    /// </summary>
    /// <remarks>Reference: https://docs.aws.amazon.com/AmazonS3/latest/userguide/notification-content-structure.html</remarks>
    [Test]
    [DisplayName("s3__sqs.s3-sequencer-is-the-only-total-order-over-one-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var account = ctx.Account;
        var bucket = FidelityContext.UniqueName("sim2real-behav-seq-");
        var create = new PutBucketRequest { BucketName = bucket, UseClientRegion = false };
        if (ctx.Region != "us-east-1")
        {
            create.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
        }

        await s3.PutBucketAsync(create);
        ctx.OnCleanup(() => EmptyAndDeleteBucket(s3, bucket));
        var bucketArn = $"arn:aws:s3:::{bucket}";
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });

        var good = await CreateQueue(ctx, sqs);
        // The negative arm's destination: created now and left WITHOUT an S3-granting policy for the
        // whole run. It doubles as the effect-absent probe for the refused configuration.
        var ungranted = await CreateQueue(ctx, sqs);
        // The control's destination: granted now, wired in only at the end, because an SQS policy
        // change can take over 90s to become visible to S3's destination validator.
        var control = await CreateQueue(ctx, sqs);
        foreach (var q in new[] { good, control })
        {
            await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = q.Url,
                Attributes = new() { ["Policy"] = AllowS3Send(q.Arn, bucketArn, account).ToJsonString() },
            });
        }

        static QueueConfiguration Dest(string qid, string arn, string prefix, List<EventType> events) => new()
        {
            Id = qid,
            Queue = arn,
            Events = events,
            Filter = new Filter { S3KeyFilter = new S3KeyFilter { FilterRules = [new FilterRule { Name = "prefix", Value = prefix }] } },
        };

        QueueConfiguration OrdDest() => Dest("ord", good.Arn, Ord, ["s3:ObjectCreated:*", "s3:ObjectRemoved:*"]);

        // The queue policy has to become effective before S3 will accept a destination that uses it,
        // and no API reports that it has. Retry THE OPERATION inside a bounded deadline -- never sleep
        // a guessed interval and hope.
        await RetryUntilAccepted(
            ctx,
            () => s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = bucket, QueueConfigurations = [OrdDest()] }),
            timeoutS: BudgetS(ctx, 90.0),
            description: "PutBucketNotificationConfiguration accepting the S3-granted queue");

        // PutBucketNotificationConfiguration sends one s3:TestEvent to every new destination, so an
        // unfiltered drain would count it as data. Wait for it explicitly, then discard it.
        await DrainTestEvent(ctx, sqs, good.Url, timeoutS: BudgetS(ctx, 60.0));

        // ---- act: 24 concurrent operations on ONE key ------------------------------------------
        var plan = Enumerable.Range(0, Puts).Select(i => ("PUT", i)).Concat(Enumerable.Range(0, Deletes).Select(i => ("DEL", i))).ToArray();
        // Python's random.Random(seed).shuffle; the order differs, but it is just as fixed.
        new Random(PlanSeed).Shuffle(plan);

        async Task<Issued> Op((string Kind, int I) spec)
        {
            if (spec.Kind == "PUT")
            {
                var body = string.Concat(Enumerable.Repeat($"p{spec.I:D3}", 10));
                var resp = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, ContentBody = body });
                return new Issued("PUT", resp.VersionId, resp.ETag.Trim('"'), body.Length);
            }

            var deleted = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = Key });
            return new Issued("DEL", deleted.VersionId, null, null);
        }

        Issued[] issued;
        using (ctx.Concurrently())
        {
            issued = await Task.WhenAll(plan.Select(p => Task.Run(() => Op(p), cancellationToken)));
        }

        Ensure(issued.Length == Ops, $"only {issued.Length} of {Ops} operations returned");
        var byVersion = new Dictionary<string, Issued>(StringComparer.Ordinal);
        foreach (var o in issued)
        {
            byVersion[o.Version] = o;
        }

        Ensure(
            byVersion.Count == Ops,
            $"{Ops} operations on a versioned key produced only {byVersion.Count} distinct version " +
            "ids -- every PUT and every DELETE on a versioning-enabled bucket mints its own version");

        // ---- the version history, as S3 reports it ---------------------------------------------
        var versions = new List<string>();
        var markers = new List<string>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var page in s3.Paginators.ListVersions(new ListVersionsRequest { BucketName = bucket }).Responses)
        {
            foreach (var v in page.Versions ?? [])
            {
                (v.IsDeleteMarker == true ? markers : versions).Add(v.VersionId);
                listed.Add(v.VersionId);
            }
        }

        // ---- drain the notifications, deduping -- delivery is AT-LEAST-ONCE --------------------
        var events = new Dictionary<(string Name, string? Version, string Sequencer), List<JsonNode>>();
        var drainS = BudgetS(ctx, 180.0);
        var drainStarted = FidelityTime.StartNew();
        while (drainStarted.Elapsed.TotalSeconds < drainS && events.Count < Ops)
        {
            var wait = (int)Math.Min(10, Math.Max(1, drainS - drainStarted.Elapsed.TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = good.Url, MaxNumberOfMessages = 10, WaitTimeSeconds = wait });
            foreach (var msg in resp.Messages ?? [])
            {
                foreach (var rec in JsonNode.Parse(msg.Body)?["Records"]?.AsArray() ?? [])
                {
                    var obj = rec!["s3"]!["object"]!;
                    var key = ((string)rec["eventName"]!, (string?)obj["versionId"], (string)obj["sequencer"]!);
                    if (!events.TryGetValue(key, out var list))
                    {
                        events[key] = list = [];
                    }

                    list.Add(rec);
                }

                await sqs.DeleteMessageAsync(good.Url, msg.ReceiptHandle);
            }
        }

        if (events.Count < Ops && BudgetSpent(ctx))
        {
            throw new TimeoutException($"{Ops} distinct notifications: the run budget ended first; not graded");
        }

        Ensure(
            events.Count == Ops,
            $"only {events.Count} of {Ops} distinct notifications arrived within {drainS:F0}s (S3 documents " +
            "event delivery as typically within seconds, so this window separates BROKEN from SLOW). The " +
            "version ids that produced no event were " +
            $"{Py.Repr(byVersion.Keys.Except(events.Keys.Select(k => k.Version!)).Order(StringComparer.Ordinal).ToList())}. This is not a coalescing " +
            "allowance: real AWS was measured to emit one event per operation for exactly this " +
            "workload.");

        // ---- assert: the sequencer is well-formed and a TOTAL order ----------------------------
        var seqs = events.Keys.Select(k => k.Sequencer).ToList();
        var malformed = seqs.Where(s => string.IsNullOrEmpty(s) || !Hex().IsMatch(s)).ToList();
        Ensure(
            malformed.Count == 0,
            $"{malformed.Count} notification(s) carried a sequencer that is not uppercase hexadecimal: " +
            $"{Py.Repr(malformed.Take(5).ToList())}. A timestamp, a UUID or a decimal counter is not a sequencer.");
        var width = seqs.Max(s => s.Length);
        var padded = seqs.Select(s => SeqKey(s, width)).ToList();
        Ensure(
            padded.Distinct(StringComparer.Ordinal).Count() == Ops,
            $"the {Ops} sequencers collapse to {padded.Distinct(StringComparer.Ordinal).Count()} distinct positions once " +
            $"right-padded to width {width} -- a sequencer that collides across concurrent operations " +
            "on the same key is not a total order, and every consumer that uses it to reject stale " +
            $"events will drop a live one. Sequencers: {Py.Repr(seqs.Order(StringComparer.Ordinal).ToList())}");
        var ordered = events.Keys.OrderBy(k => SeqKey(k.Sequencer, width), StringComparer.Ordinal).ToList();

        // ---- assert: that order IS the version history, per family -----------------------------
        // ListObjectVersions returns each family newest-first, so oldest-first is the reversal.
        var seqPuts = ordered.Where(k => k.Name.StartsWith("ObjectCreated", StringComparison.Ordinal)).Select(k => k.Version).ToList();
        var seqDels = ordered.Where(k => k.Name.StartsWith("ObjectRemoved", StringComparison.Ordinal)).Select(k => k.Version).ToList();
        var versionsReversed = Enumerable.Reverse(versions).ToList();
        var markersReversed = Enumerable.Reverse(markers).ToList();
        Ensure(
            seqPuts.SequenceEqual(versionsReversed),
            $"the sequencer order of the {seqPuts.Count} ObjectCreated events disagrees with the " +
            $"object-version history. sequencer order (oldest first): {Py.Repr(seqPuts)}; " +
            $"ListObjectVersions reversed: {Py.Repr(versionsReversed)}. The sequencer must encode the " +
            "STORAGE commit order, so these are two witnesses of one fact.");
        Ensure(
            seqDels.SequenceEqual(markersReversed),
            $"the sequencer order of the {seqDels.Count} ObjectRemoved events disagrees with the " +
            $"delete-marker history. sequencer order (oldest first): {Py.Repr(seqDels)}; DeleteMarkers " +
            $"reversed: {Py.Repr(markersReversed)}");

        // ---- assert: the negative space -- no phantom event, no missing event -------------------
        var eventVersions = events.Keys.Select(k => k.Version!).ToHashSet(StringComparer.Ordinal);
        Ensure(
            eventVersions.SetEquals(listed) && listed.SetEquals(byVersion.Keys),
            "the notification stream and the version history describe different sets of operations. " +
            $"events-only: {Py.Repr(eventVersions.Except(listed).Order(StringComparer.Ordinal).ToList())}; " +
            $"history-only: {Py.Repr(listed.Except(eventVersions).Order(StringComparer.Ordinal).ToList())}; " +
            $"never-issued: {Py.Repr(eventVersions.Except(byVersion.Keys).Order(StringComparer.Ordinal).ToList())}");

        // ---- assert: the greatest sequencer is the operation the key actually ended at ----------
        // This is the only assertion that binds the two event families into ONE order, and it binds
        // that order to observable object state rather than to another list.
        var last = ordered[^1];
        if (last.Name.StartsWith("ObjectRemoved", StringComparison.Ordinal))
        {
            // botocore reports a HEAD error's bare status as its code ("404") and its reason phrase as
            // the message; S3Fidelity.HeadError reads the .NET SDK's error the same way.
            var headErr = await S3Fidelity.HeadError(s3, bucket, Key);
            Ensure(
                headErr == ("404", "Not Found"),
                $"the greatest sequencer belongs to {Py.Repr(last.Name)} (version {Py.Repr(last.Version)}), so the key's " +
                "current version must be a delete marker and HeadObject must be a 404 'Not Found'; " +
                $"it raised {headErr}");
            var cur = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = Key });
            var latest = (cur.Versions ?? []).Where(m => m.IsDeleteMarker == true && m.IsLatest == true).Select(m => m.VersionId).ToList();
            Ensure(
                latest.SequenceEqual([last.Version!]),
                $"the greatest sequencer is version {Py.Repr(last.Version)} but IsLatest points at " +
                $"{Py.Repr(latest)} -- the sequencer order and the current version " +
                "must be the same order");
        }
        else
        {
            var head = await s3.GetObjectMetadataAsync(bucket, Key);
            Ensure(
                head.VersionId == last.Version,
                $"the greatest sequencer belongs to PUT version {Py.Repr(last.Version)} but the key's current " +
                $"version is {Py.Repr(head.VersionId)} -- the sequencer total order must agree with which " +
                "write won");
        }

        // ---- assert: each event describes ITS OWN operation -------------------------------------
        foreach (var (name, version, sequencer) in ordered)
        {
            var rec = events[(name, version, sequencer)][0];
            var obj = rec["s3"]!["object"]!;
            var origin = byVersion[version!];
            Ensure(
                (string?)obj["key"] == KeyEncoded,
                $"event for {Py.Repr(version)} reported key {Py.Repr((string?)obj["key"])}, expected the URL-encoded form " +
                $"{Py.Repr(KeyEncoded)} (a space encodes as '+' and a literal '+' as '%2B')");
            Ensure(
                (string?)rec["s3"]?["bucket"]?["arn"] == bucketArn,
                $"event for {Py.Repr(version)} reported bucket arn {Py.Repr((string?)rec["s3"]?["bucket"]?["arn"])}, expected " +
                $"{Py.Repr(bucketArn)}");
            if (origin.Kind == "PUT")
            {
                var size = obj["size"] is JsonValue sv && sv.TryGetValue<long>(out var s) ? s : (long?)null;
                Ensure(
                    size == origin.Size,
                    $"event for PUT {Py.Repr(version)} reported size {obj["size"]?.ToJsonString() ?? "None"}, but " +
                    $"{origin.Size} bytes were written");
                Ensure(
                    (string?)obj["eTag"] == origin.ETag,
                    $"event for PUT {Py.Repr(version)} reported eTag {Py.Repr((string?)obj["eTag"])}, but the PutObject " +
                    $"response returned {Py.Repr(origin.ETag)} -- the event must describe THAT write");
                Ensure(
                    name.StartsWith("ObjectCreated", StringComparison.Ordinal),
                    $"version {Py.Repr(version)} was created by PutObject but its event is {Py.Repr(name)}");
            }
            else
            {
                Ensure(
                    name == "ObjectRemoved:DeleteMarkerCreated",
                    $"version {Py.Repr(version)} is a delete marker but its event is {Py.Repr(name)}");
            }
        }

        // ---- assert: eventTime is an ISO-8601 UTC timestamp ---------------------------------------
        // S3 documents `sequencer` as the ordering key but guarantees nothing about eventTime skew,
        // so eventTimes that happen to be distinct and monotone are accepted when well-formed.
        var times = ordered.Select(k => (string?)events[k][0]["eventTime"]).ToList();
        var monotoneAndDistinct = times.Distinct().Count() == times.Count && times.SequenceEqual(times.Order(StringComparer.Ordinal));
        Ensure(
            !monotoneAndDistinct || times.All(t => t is { Length: >= 20 } && t[10] == 'T' && t.EndsWith('Z')),
            $"eventTime values are not ISO-8601 UTC timestamps: {Py.Repr(times)}");

        // ---- assert: duplicates are duplicates, not colliding distinct events -------------------
        var bySeq = new Dictionary<string, HashSet<(string, string?)>>(StringComparer.Ordinal);
        foreach (var (name, version, sequencer) in events.Keys)
        {
            var seqKey = SeqKey(sequencer, width);
            if (!bySeq.TryGetValue(seqKey, out var set))
            {
                bySeq[seqKey] = set = [];
            }

            set.Add((name, version));
        }

        var colliding = bySeq.Where(p => p.Value.Count > 1).ToList();
        Ensure(
            colliding.Count == 0,
            $"sequencer(s) shared by DIFFERENT (eventName, versionId) pairs: {string.Join(", ", colliding.Select(c => c.Key))}. A repeated " +
            "sequencer may only ever be an at-least-once redelivery of the SAME event.");

        // ---- negative: S3 refuses a destination it cannot deliver to, synchronously -------------
        // Chosen deliberately over "revoke the queue policy and assert silence": an SQS policy change
        // takes an unbounded time to become effective, so an assertion of NON-delivery after a revoke
        // is a race. Configuration-time validation is synchronous, so this arm is deterministic.
        var refusal = await Error(() => s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations = [OrdDest(), Dest("deny", ungranted.Arn, Deny, ["s3:ObjectCreated:*"])],
        }));
        Ensure(
            refusal.Code == "InvalidArgument",
            "configuring a queue destination whose resource policy does not grant " +
            $"sqs:SendMessage to s3.amazonaws.com was rejected with {Py.Repr(refusal.Code)}, expected " +
            "'InvalidArgument'. S3 validates delivery permission when the configuration is written, " +
            "not when the first event fires.");
        Ensure(
            refusal.Message == Undeliverable,
            $"the refusal message was {Py.Repr(refusal.Message)}, which does not name the destination as " +
            "unvalidatable. S3 reuses InvalidArgument for an overlapping-prefix configuration too, so " +
            "the code alone does not establish WHY this was refused -- and a check that accepted any " +
            $"InvalidArgument here would pass while proving something else entirely. Expected {Py.Repr(Undeliverable)}.");

        // effect-absent: the refused configuration did not partially apply
        var live = await s3.GetBucketNotificationAsync(bucket);
        var liveArns = (live.QueueConfigurations ?? []).Select(q => q.Queue).ToList();
        Ensure(
            liveArns.SequenceEqual([good.Arn]),
            $"after the refused PutBucketNotificationConfiguration the live configuration lists " +
            $"{Py.Repr(liveArns)}; a rejected configuration must leave the previous one exactly as it was, " +
            "and must never install the destination it just refused");

        // effect-absent, observed at the destination: the ungranted queue received nothing at all.
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Deny + "probe.txt", ContentBody = "after-refusal" });
        var silent = await Collect(sqs, ungranted.Url, windowS: SilenceWindowS);
        Ensure(
            silent.Count == 0,
            $"the queue whose configuration S3 REFUSED received {silent.Count} message(s): " +
            $"{Py.Repr(silent.Select(m => m.Body.Length > 200 ? m.Body[..200] : m.Body).ToList())}. This is meaningful only because the granted queue " +
            $"demonstrably received all {Ops} events above, which proves the pipeline was live.");

        // control: the SAME two-destination shape is accepted when the second destination's policy does
        // grant S3 -- so the refusal above was about the missing permission, and not about the ARN, the
        // event list, or configuring a second destination at all.
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations = [OrdDest(), Dest("ctl", control.Arn, Ctl, ["s3:ObjectCreated:*"])],
        });
        var accepted = await s3.GetBucketNotificationAsync(bucket);
        var acceptedArns = (accepted.QueueConfigurations ?? []).Select(q => q.Queue).ToList();
        Ensure(
            acceptedArns.Order(StringComparer.Ordinal).SequenceEqual(new[] { good.Arn, control.Arn }.Order(StringComparer.Ordinal)),
            $"the control configuration reported {Py.Repr(acceptedArns)}, expected the granted queue " +
            "and the control queue");
    }

    private sealed record Queue(string Name, string Url, string Arn);

    private sealed record Issued(string Kind, string Version, string? ETag, int? Size);

    /// <summary>
    /// A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s so the poll fails
    /// with its own message.
    /// </summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    /// <summary>
    /// Return the AWS error (code, message) raised by fn(); raise if fn() succeeds. S3 answers
    /// InvalidArgument for both an undeliverable and an overlapping destination, so the message is
    /// what distinguishes them.
    /// </summary>
    private static async Task<AwsError> Error(Func<Task> fn) =>
        await FidelityContext.CaptureError(fn)
        ?? throw Fail(
            "the call was expected to be rejected by the service but it SUCCEEDED -- a target that " +
            "accepts this input is more broken than one that returns the wrong error code");

    /// <summary>
    /// The documented comparison rule: right-zero-pad to equal length, then compare as strings. Not
    /// `int(sequencer, 16)`: a shorter sequencer is a coarser position, not a smaller number.
    /// </summary>
    private static string SeqKey(string sequencer, int width) => sequencer.PadRight(width, '0');

    private static JsonObject AllowS3Send(string queueArn, string bucketArn, string account) => new()
    {
        ["Version"] = "2012-10-17",
        ["Statement"] = new JsonArray(new JsonObject
        {
            ["Sid"] = "AllowS3Send",
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["Service"] = "s3.amazonaws.com" },
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = queueArn,
            ["Condition"] = new JsonObject
            {
                ["ArnLike"] = new JsonObject { ["aws:SourceArn"] = bucketArn },
                ["StringEquals"] = new JsonObject { ["aws:SourceAccount"] = account },
            },
        }),
    };

    private static async Task<Queue> CreateQueue(FidelityContext ctx, AmazonSQSClient sqs)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-seq-q-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = await S3Fidelity.NotificationQueueArn(sqs, url);
        return new Queue(name, url, arn);
    }

    /// <summary>
    /// Retry the operation until the service accepts it, within a bounded deadline. Used for a
    /// propagation window that exposes no condition to poll: the operation is its own probe.
    /// </summary>
    private static async Task RetryUntilAccepted(FidelityContext ctx, Func<Task> fn, double timeoutS, string description)
    {
        var started = FidelityTime.StartNew();
        var attempts = new List<string?>();
        while (true)
        {
            var err = await FidelityContext.CaptureError(fn);
            if (err is null)
            {
                return;
            }

            attempts.Add(err.Code);
            if (started.Elapsed.TotalSeconds >= timeoutS)
            {
                if (BudgetSpent(ctx))
                {
                    throw new TimeoutException($"{description}: the run budget ended first; not graded", err.Exception);
                }

                throw new FidelityAssertionException(
                    $"{description} never succeeded within {timeoutS}s over " +
                    $"{attempts.Count} attempts; codes seen: {Py.Repr(attempts)}",
                    err.Exception);
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>Wait for the s3:TestEvent every new destination receives, then discard it.</summary>
    private static async Task DrainTestEvent(FidelityContext ctx, AmazonSQSClient sqs, string url, double timeoutS)
    {
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < timeoutS)
        {
            var wait = (int)Math.Min(5, Math.Max(1, timeoutS - started.Elapsed.TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = wait });
            var seenTest = false;
            foreach (var msg in resp.Messages ?? [])
            {
                JsonNode? body;
                try
                {
                    body = JsonNode.Parse(msg.Body);
                }
                catch (System.Text.Json.JsonException)
                {
                    body = new JsonObject();
                }

                if ((string?)body?["Event"] == "s3:TestEvent")
                {
                    seenTest = true;
                }

                await sqs.DeleteMessageAsync(url, msg.ReceiptHandle);
            }

            if (seenTest)
            {
                return;
            }
        }

        if (BudgetSpent(ctx))
        {
            throw new TimeoutException("the s3:TestEvent: the run budget ended first; not graded");
        }

        throw Fail(
            "the s3:TestEvent that PutBucketNotificationConfiguration sends to every new destination " +
            $"never arrived within {timeoutS:F0}s -- the notification pipeline is not wired, so nothing measured " +
            "afterwards would mean anything");
    }

    /// <summary>Every message seen over a bounded window; emptiness counts only beside a positive control.</summary>
    private static async Task<List<Message>> Collect(AmazonSQSClient sqs, string url, double windowS)
    {
        var seen = new List<Message>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < windowS)
        {
            var wait = (int)Math.Min(SilencePollS, Math.Max(1, windowS - started.Elapsed.TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = wait });
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(msg);
                await sqs.DeleteMessageAsync(url, msg.ReceiptHandle);
            }
        }

        return seen;
    }

    private static async Task EmptyAndDeleteBucket(AmazonS3Client s3, string bucket)
    {
        var targets = new List<KeyVersion>();
        await foreach (var page in s3.Paginators.ListVersions(new ListVersionsRequest { BucketName = bucket }).Responses)
        {
            targets.AddRange((page.Versions ?? []).Select(o => new KeyVersion { Key = o.Key, VersionId = o.VersionId }));
        }

        for (var i = 0; i < targets.Count; i += 1000)
        {
            await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = targets.Skip(i).Take(1000).ToList() });
        }

        await s3.DeleteBucketAsync(bucket);
    }
}

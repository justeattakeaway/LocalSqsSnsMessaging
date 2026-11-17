// Translated from behavioral/sns/check_topic_policy_protocol_condition_blocks_a_real_signed_subscribe.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.topic-policy-protocol-condition-blocks-a-real-signed-subscribe</summary>
[Category("AwsEvaluationSuite")]
public sealed class TopicPolicyProtocolConditionBlocksARealSignedSubscribe
{
    private const string Scenario = "sns-topic-policy-condition";
    private const string DenySid = "sim2realDenyEmailProtocol";

    /// <summary>
    /// A topic-policy Deny on SNS:Subscribe conditioned on sns:protocol == "email" refuses an email
    /// Subscribe with AuthorizationError and creates no subscription, while an sqs Subscribe still
    /// succeeds; restoring the original policy lets the identical email Subscribe succeed.
    /// </summary>
    [Test]
    [DisplayName("sns.topic-policy-protocol-condition-blocks-a-real-signed-subscribe")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);

        var originalPolicy = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes?.GetValueOrDefault("Policy");
        Ensure(!string.IsNullOrEmpty(originalPolicy),
            "a freshly created topic must expose a default Policy document through " +
            $"GetTopicAttributes; got '{originalPolicy}' — the restore leg needs it verbatim");
        var originalDoc = JsonNode.Parse(originalPolicy)!.AsObject();
        Ensure(originalDoc["Statement"] is JsonArray { Count: > 0 } or JsonObject,
            $"the default topic policy must carry at least one statement, got {originalDoc.ToJsonString()}");

        var emailBefore = $"sim2real-behav-sns-allowed-{Guid.NewGuid().ToString("N")[..10]}@example.com";
        var emailDenied = $"sim2real-behav-sns-denied-{Guid.NewGuid().ToString("N")[..10]}@example.com";

        // ---- beat 1: the positive control, before anything is changed -------------------------
        await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "email", Endpoint = emailBefore });
        // A pending email subscription has no resolvable SubscriptionArn, so it cannot be
        // unsubscribed individually; DeleteTopic (already registered) removes it.
        var listed = await Endpoints(ctx, topicArn);
        Ensure(listed.GetValueOrDefault(emailBefore) == "email",
            "control: an email Subscribe must succeed and appear in ListSubscriptionsByTopic under " +
            $"the default topic policy; endpoints listed were [{string.Join(", ", listed.Keys.Order(StringComparer.Ordinal))}]");

        // ---- beat 2: break exactly one thing — add the condition-scoped Deny ------------------
        var deniedDoc = originalDoc.DeepClone().AsObject();
        var statements = originalDoc["Statement"] is JsonArray array
            ? (JsonArray)array.DeepClone()
            : new JsonArray(originalDoc["Statement"]!.DeepClone());
        statements.Add(new JsonObject
        {
            ["Sid"] = DenySid,
            ["Effect"] = "Deny",
            ["Principal"] = new JsonObject { ["AWS"] = "*" },
            ["Action"] = "SNS:Subscribe",
            ["Resource"] = topicArn,
            ["Condition"] = new JsonObject { ["StringEquals"] = new JsonObject { ["sns:protocol"] = "email" } },
        });
        deniedDoc["Statement"] = statements;
        await SetPolicy(ctx, topicArn, deniedDoc.ToJsonString(), expectSidPresent: true);

        // ---- beat 3: the specific refusal on the identical call -------------------------------
        var (code, message) = await Error(() => sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "email", Endpoint = emailDenied }));
        Ensure(code == "AuthorizationError",
            "expected an email Subscribe to be refused with Code 'AuthorizationError' while the " +
            "topic policy denies SNS:Subscribe under StringEquals sns:protocol=email; got Code " +
            $"'{code}' and Message '{message}'");
        var callerArn = ctx.CallerArn;
        var expectedAuthMessage =
            $"User: {callerArn} is not authorized to perform: SNS:Subscribe on resource: " +
            $"{topicArn} with an explicit deny in a resource-based policy";
        Ensure(message == expectedAuthMessage, $"unexpected AuthorizationError message for the denied email Subscribe: '{message}'");

        // ---- beat 4: the condition is scoped — an sqs Subscribe still succeeds ----------------
        // SNS auto-confirms an sqs-protocol subscription without verifying the queue exists, so this
        // control needs no SQS resource and keeps the check single-service.
        var parts = topicArn.Split(':');
        var queueArn = $"arn:aws:sqs:{parts[3]}:{parts[4]}:{FidelityContext.UniqueName("sim2real-behav-sns-q-")}";
        var sqsSub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true
        })).SubscriptionArn;
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sqsSub }));
        Ensure(!string.IsNullOrEmpty(sqsSub) && sqsSub != "pending confirmation",
            "positive control: an sqs Subscribe must still succeed while the Deny is scoped to " +
            "sns:protocol=email — without this leg, 'denies email' cannot be told apart from " +
            $"'denies every Subscribe'; got SubscriptionArn '{sqsSub}'");
        var afterControl = await Endpoints(ctx, topicArn);
        Ensure(afterControl.GetValueOrDefault(queueArn) == "sqs",
            "positive control: the sqs subscription must be visible in ListSubscriptionsByTopic; " +
            $"endpoints listed were [{string.Join(", ", afterControl.Keys.Order(StringComparer.Ordinal))}]");

        // ---- beat 5: effect absent, read through a different operation ------------------------
        Ensure(!afterControl.ContainsKey(emailDenied),
            "a Subscribe refused with AuthorizationError must create no subscription: " +
            $"'{emailDenied}' is present in ListSubscriptionsByTopic anyway (protocol " +
            $"'{afterControl.GetValueOrDefault(emailDenied)}')");

        // ---- beat 6: restore the original document verbatim, and the identical call succeeds ---
        await SetPolicy(ctx, topicArn, originalPolicy, expectSidPresent: false);
        await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "email", Endpoint = emailDenied });
        var restored = await Endpoints(ctx, topicArn);
        Ensure(restored.GetValueOrDefault(emailDenied) == "email",
            "restore: with the Deny statement removed, the identical email Subscribe must succeed " +
            $"and be listed; endpoints listed were [{string.Join(", ", restored.Keys.Order(StringComparer.Ordinal))}]");
    }

    /// <summary>{Endpoint: Protocol} for every subscription on the topic, following NextToken.</summary>
    private static async Task<Dictionary<string, string>> Endpoints(FidelityContext ctx, string topicArn)
    {
        var sns = ctx.Sns;
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        string? token = null;
        while (true)
        {
            var page = await sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn, NextToken = token });
            foreach (var sub in page.Subscriptions)
            {
                output[sub.Endpoint] = sub.Protocol;
            }

            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return output;
            }
        }
    }

    /// <summary>Install a topic Policy and poll an independent read until it reflects the change.</summary>
    private static async Task SetPolicy(FidelityContext ctx, string topicArn, string policy, bool expectSidPresent)
    {
        var sns = ctx.Sns;
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = topicArn, AttributeName = "Policy", AttributeValue = policy });
        await PollUntil(
            async () => (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes?.GetValueOrDefault("Policy") ?? "",
            stored => stored.Contains(DenySid, StringComparison.Ordinal) == expectSidPresent,
            60.0, 2.0,
            $"GetTopicAttributes reports the {DenySid} statement {(expectSidPresent ? "present" : "absent")}");
    }

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Tags = [new Tag { Key = "sim2real", Value = Scenario }, new Tag { Key = "sim2real:managed", Value = "true" }],
        })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Bounded retry; RAISES on the deadline so a timeout can never read as a pass.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string description)
    {
        var deadline = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: not satisfied within {timeoutS}s, last value '{value}'");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }

    /// <summary>(Code, Message) for a refused call. RAISES on unexpected success.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (_, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail("expected this call to be refused, but it succeeded; an over-permissive target must " +
                         "not be able to report a missing error code instead of an unexpected success")
            : (error.Code, error.Message);
    }
}

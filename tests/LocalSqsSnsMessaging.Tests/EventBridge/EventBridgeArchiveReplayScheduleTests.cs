using System.Text.Json.Nodes;
using Amazon.SQS;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Eb = LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging.Tests.EventBridge;

/// <summary>
/// Archives, replays and scheduled rules run on the bus's <see cref="TimeProvider"/>. These drive
/// the in-memory EventBridge client directly on a <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class EventBridgeArchiveReplayScheduleTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    private static async Task<(InMemoryAwsBus Bus, FakeTimeProvider Time, InternalEventBridgeClient Eb, AmazonSQSClient Sqs, string QueueUrl, string QueueArn)> CreateAsync()
    {
        var time = new FakeTimeProvider(Start);
        var bus = new InMemoryAwsBus { TimeProvider = time };
        var sqs = bus.CreateSqsClient();
        var queueUrl = (await sqs.CreateQueueAsync("q")).QueueUrl;
        var queueArn = (await sqs.GetQueueAttributesAsync(queueUrl, ["QueueArn"])).Attributes["QueueArn"];
        return (bus, time, new InternalEventBridgeClient(bus), sqs, queueUrl, queueArn);
    }

    private static async Task<List<JsonObject>> DrainAsync(AmazonSQSClient sqs, string queueUrl)
    {
        var bodies = new List<JsonObject>();
        while ((await sqs.ReceiveMessageAsync(new Amazon.SQS.Model.ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10 })).Messages is { Count: > 0 } messages)
        {
            foreach (var message in messages)
            {
                bodies.Add(JsonNode.Parse(message.Body)!.AsObject());
                await sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle);
            }
        }
        return bodies;
    }

    [Test]
    public async Task RateRule_FiresScheduledEventsOnTheTimeProvider_UntilDisabledOrDeleted()
    {
        var (_, time, eb, sqs, queueUrl, queueArn) = await CreateAsync();
        using var _ = sqs;
        var ruleArn = (await eb.PutRuleAsync(new Eb.PutRuleRequest { Name = "tick", ScheduleExpression = "rate(1 minute)" })).RuleArn;
        await eb.PutTargetsAsync(new Eb.PutTargetsRequest { Rule = "tick", Targets = [new Eb.Target { Id = "q", Arn = queueArn }] });

        time.Advance(TimeSpan.FromMinutes(3));
        var fired = await DrainAsync(sqs, queueUrl);

        fired.Select(e => e["time"]!.GetValue<string>()).ShouldBe(["2026-01-05T09:01:00Z", "2026-01-05T09:02:00Z", "2026-01-05T09:03:00Z"]);
        fired.ShouldAllBe(e => e["detail-type"]!.GetValue<string>() == "Scheduled Event" && e["source"]!.GetValue<string>() == "aws.events");
        fired[0]["resources"]!.AsArray().Single()!.GetValue<string>().ShouldBe(ruleArn);
        fired[0]["detail"]!.AsObject().Count.ShouldBe(0);

        await eb.DisableRuleAsync(new Eb.DisableRuleRequest { Name = "tick" });
        time.Advance(TimeSpan.FromMinutes(2));
        (await DrainAsync(sqs, queueUrl)).ShouldBeEmpty();

        await eb.EnableRuleAsync(new Eb.EnableRuleRequest { Name = "tick" });
        time.Advance(TimeSpan.FromMinutes(1));
        (await DrainAsync(sqs, queueUrl)).Count.ShouldBe(1);

        // EventBridge deletes only a rule without targets.
        await eb.RemoveTargetsAsync(new Eb.RemoveTargetsRequest { Rule = "tick", Ids = ["q"] });
        await eb.DeleteRuleAsync(new Eb.DeleteRuleRequest { Name = "tick" });
        time.Advance(TimeSpan.FromMinutes(5));
        (await DrainAsync(sqs, queueUrl)).ShouldBeEmpty();
    }

    [Test]
    public async Task Replay_RunsOnTheTimeProvider_AndRedeliversOnlyTheWindowWithItsReplayName()
    {
        var (_, time, eb, sqs, queueUrl, queueArn) = await CreateAsync();
        using var _ = sqs;
        var busArn = (await eb.CreateEventBusAsync(new Eb.CreateEventBusRequest { Name = "orders" })).EventBusArn;
        await eb.PutRuleAsync(new Eb.PutRuleRequest { Name = "r", EventBusName = "orders", EventPattern = """{"source":["shop"]}""" });
        await eb.PutTargetsAsync(new Eb.PutTargetsRequest { Rule = "r", EventBusName = "orders", Targets = [new Eb.Target { Id = "q", Arn = queueArn }] });
        var archiveArn = (await eb.CreateArchiveAsync(new Eb.CreateArchiveRequest { ArchiveName = "a", EventSourceArn = busArn })).ArchiveArn;

        Eb.PutEventsRequestEntry Event(string id, DateTimeOffset at) => new()
        {
            EventBusName = "orders", Source = "shop", DetailType = "Order", Detail = $$"""{"id":"{{id}}"}""", Time = at.UtcDateTime
        };
        await eb.PutEventsAsync(new Eb.PutEventsRequest { Entries = [Event("inside", Start.AddMinutes(-30)), Event("outside", Start.AddHours(-2))] });
        (await DrainAsync(sqs, queueUrl)).Count.ShouldBe(2);
        (await eb.DescribeArchiveAsync(new Eb.DescribeArchiveRequest { ArchiveName = "a" })).EventCount.ShouldBe(2);

        var started = await eb.StartReplayAsync(new Eb.StartReplayRequest
        {
            ReplayName = "rp",
            EventSourceArn = archiveArn,
            EventStartTime = Start.AddHours(-1).UtcDateTime,
            EventEndTime = Start.UtcDateTime,
            Destination = new Eb.ReplayDestination { Arn = busArn }
        });
        started.State.ShouldBe("STARTING");

        time.Advance(ReplayResource.StepInterval);
        (await eb.DescribeReplayAsync(new Eb.DescribeReplayRequest { ReplayName = "rp" })).State.ShouldBe("RUNNING");
        var replayed = (await DrainAsync(sqs, queueUrl)).ShouldHaveSingleItem();
        replayed["detail"]!["id"]!.GetValue<string>().ShouldBe("inside");
        replayed["replay-name"]!.GetValue<string>().ShouldBe("rp");

        time.Advance(ReplayResource.StepInterval);
        var completed = await eb.DescribeReplayAsync(new Eb.DescribeReplayRequest { ReplayName = "rp" });
        completed.State.ShouldBe("COMPLETED");
        completed.ReplayEndTime.ShouldBe(Start.AddSeconds(2).UtcDateTime);
        completed.EventLastReplayedTime.ShouldBe(Start.AddMinutes(-30).UtcDateTime);

        // The managed rule's replay-name exclusion keeps replayed events out of the archive.
        (await eb.DescribeArchiveAsync(new Eb.DescribeArchiveRequest { ArchiveName = "a" })).EventCount.ShouldBe(2);
        var refusal = await Should.ThrowAsync<AwsServiceException>(async () => await eb.CancelReplayAsync(new Eb.CancelReplayRequest { ReplayName = "rp" }));
        refusal.ErrorCode.ShouldBe("IllegalStatusException");
    }

    [Test]
    public async Task CancelledReplay_SettlesCancelledOnTheNextStep_WithoutRedelivering()
    {
        var (_, time, eb, sqs, queueUrl, queueArn) = await CreateAsync();
        using var _ = sqs;
        var busArn = (await eb.CreateEventBusAsync(new Eb.CreateEventBusRequest { Name = "orders" })).EventBusArn;
        await eb.PutRuleAsync(new Eb.PutRuleRequest { Name = "r", EventBusName = "orders", EventPattern = """{"source":["shop"]}""" });
        await eb.PutTargetsAsync(new Eb.PutTargetsRequest { Rule = "r", EventBusName = "orders", Targets = [new Eb.Target { Id = "q", Arn = queueArn }] });
        var archiveArn = (await eb.CreateArchiveAsync(new Eb.CreateArchiveRequest { ArchiveName = "a", EventSourceArn = busArn })).ArchiveArn;
        await eb.PutEventsAsync(new Eb.PutEventsRequest { Entries = [new Eb.PutEventsRequestEntry { EventBusName = "orders", Source = "shop", DetailType = "Order", Detail = "{}" }] });
        await DrainAsync(sqs, queueUrl);

        await eb.StartReplayAsync(new Eb.StartReplayRequest
        {
            ReplayName = "rp",
            EventSourceArn = archiveArn,
            EventStartTime = Start.AddHours(-1).UtcDateTime,
            EventEndTime = Start.AddHours(1).UtcDateTime,
            Destination = new Eb.ReplayDestination { Arn = busArn }
        });
        (await eb.CancelReplayAsync(new Eb.CancelReplayRequest { ReplayName = "rp" })).State.ShouldBe("CANCELLING");

        time.Advance(ReplayResource.StepInterval);
        (await eb.DescribeReplayAsync(new Eb.DescribeReplayRequest { ReplayName = "rp" })).State.ShouldBe("CANCELLED");
        time.Advance(TimeSpan.FromMinutes(1));
        (await DrainAsync(sqs, queueUrl)).ShouldBeEmpty();
    }

    [Test]
    [Arguments("cron(0 12 * * ? *)", "2026-01-05T11:59:30Z", "2026-01-05T12:00:00Z")]
    [Arguments("cron(0 12 * * ? *)", "2026-01-05T12:00:00Z", "2026-01-06T12:00:00Z")]
    [Arguments("cron(0/15 * * * ? *)", "2026-01-05T09:07:00Z", "2026-01-05T09:15:00Z")]
    [Arguments("cron(0 18 ? * MON-FRI *)", "2026-01-09T18:00:00Z", "2026-01-12T18:00:00Z")]
    [Arguments("cron(15 10 ? * 6L 2026)", "2026-01-01T00:00:00Z", "2026-01-30T10:15:00Z")]
    [Arguments("cron(0 9 ? * 2#1 *)", "2026-01-06T00:00:00Z", "2026-02-02T09:00:00Z")]
    [Arguments("cron(0 0 L * ? *)", "2026-02-01T00:00:00Z", "2026-02-28T00:00:00Z")]
    [Arguments("cron(0 8 1W * ? *)", "2026-02-01T00:00:00Z", "2026-02-02T08:00:00Z")]
    [Arguments("cron(0 8 1W * ? *)", "2026-02-02T09:00:00Z", "2026-03-02T08:00:00Z")]
    [Arguments("rate(5 minutes)", "2026-01-05T09:00:00Z", "2026-01-05T09:05:00Z")]
    public async Task Schedule_NextAfter(string expression, string after, string expected)
    {
        EventBridgeSchedule.TryParse(expression, out var schedule).ShouldBeTrue();

        var next = schedule.NextAfter(DateTimeOffset.Parse(after, System.Globalization.CultureInfo.InvariantCulture));

        next.ShouldBe(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Schedule_InTheLastAllowedYearNeverFiresAgain()
    {
        EventBridgeSchedule.TryParse("cron(0 0 1 1 ? 2025)", out var schedule).ShouldBeTrue();

        schedule.NextAfter(Start).ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Replay_RedeliversAnEventWhoseDetailNestsDeeperThanTheJsonDefault()
    {
        var (_, time, eb, sqs, queueUrl, queueArn) = await CreateAsync();
        using var _ = sqs;
        var busArn = (await eb.CreateEventBusAsync(new Eb.CreateEventBusRequest { Name = "orders" })).EventBusArn;
        await eb.PutRuleAsync(new Eb.PutRuleRequest { Name = "r", EventBusName = "orders", EventPattern = """{"source":["shop"]}""" });
        await eb.PutTargetsAsync(new Eb.PutTargetsRequest { Rule = "r", EventBusName = "orders", Targets = [new Eb.Target { Id = "q", Arn = queueArn }] });
        var archiveArn = (await eb.CreateArchiveAsync(new Eb.CreateArchiveRequest { ArchiveName = "a", EventSourceArn = busArn })).ArchiveArn;

        // PutEvents accepts a Detail up to 1000 levels deep; System.Text.Json reads 64 by default.
        var detail = string.Concat(Enumerable.Repeat("{\"a\":", 200)) + "1" + new string('}', 200);
        await eb.PutEventsAsync(new Eb.PutEventsRequest { Entries = [new Eb.PutEventsRequestEntry { EventBusName = "orders", Source = "shop", DetailType = "Deep", Detail = detail }] });
        await sqs.PurgeQueueAsync(queueUrl);
        await eb.StartReplayAsync(new Eb.StartReplayRequest
        {
            ReplayName = "rp",
            EventSourceArn = archiveArn,
            EventStartTime = Start.AddHours(-1).UtcDateTime,
            EventEndTime = Start.AddHours(1).UtcDateTime,
            Destination = new Eb.ReplayDestination { Arn = busArn }
        });

        time.Advance(ReplayResource.StepInterval);

        var replayed = await sqs.ReceiveMessageAsync(new Amazon.SQS.Model.ReceiveMessageRequest { QueueUrl = queueUrl });
        replayed.Messages.ShouldHaveSingleItem().Body.ShouldContain("\"replay-name\":\"rp\"");
    }

    [Test]
    public async Task Replay_SkipsEventsTheArchiveNoLongerRetains()
    {
        var (_, time, eb, sqs, queueUrl, queueArn) = await CreateAsync();
        using var _ = sqs;
        var busArn = (await eb.CreateEventBusAsync(new Eb.CreateEventBusRequest { Name = "orders" })).EventBusArn;
        await eb.PutRuleAsync(new Eb.PutRuleRequest { Name = "r", EventBusName = "orders", EventPattern = """{"source":["shop"]}""" });
        await eb.PutTargetsAsync(new Eb.PutTargetsRequest { Rule = "r", EventBusName = "orders", Targets = [new Eb.Target { Id = "q", Arn = queueArn }] });
        var archiveArn = (await eb.CreateArchiveAsync(new Eb.CreateArchiveRequest { ArchiveName = "a", EventSourceArn = busArn, RetentionDays = 1 })).ArchiveArn;
        await eb.PutEventsAsync(new Eb.PutEventsRequest { Entries = [new Eb.PutEventsRequestEntry { EventBusName = "orders", Source = "shop", DetailType = "Old", Detail = "{}" }] });
        await DrainAsync(sqs, queueUrl);

        time.Advance(TimeSpan.FromDays(2));
        await eb.StartReplayAsync(new Eb.StartReplayRequest
        {
            ReplayName = "rp",
            EventSourceArn = archiveArn,
            EventStartTime = Start.AddHours(-1).UtcDateTime,
            EventEndTime = Start.AddHours(1).UtcDateTime,
            Destination = new Eb.ReplayDestination { Arn = busArn }
        });
        time.Advance(ReplayResource.StepInterval);

        (await DrainAsync(sqs, queueUrl)).ShouldBeEmpty();
    }

    [Test]
    public async Task ConcurrentPutRules_OfOneSchedule_FireItOncePerPeriod()
    {
        var (_, time, eb, sqs, queueUrl, queueArn) = await CreateAsync();
        using var _ = sqs;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            eb.PutRuleAsync(new Eb.PutRuleRequest { Name = "tick", ScheduleExpression = "rate(1 minute)" }))));
        await eb.PutTargetsAsync(new Eb.PutTargetsRequest { Rule = "tick", Targets = [new Eb.Target { Id = "q", Arn = queueArn }] });

        time.Advance(TimeSpan.FromMinutes(1));

        (await DrainAsync(sqs, queueUrl)).Count.ShouldBe(1);
    }

    [Test]
    [Arguments("rate(512409557 hours)")]
    [Arguments("rate(10000000 days)")]
    [Arguments("rate(2147483647 days)")]
    public async Task RateRule_WithAPeriodPastTheCalendar_IsStoredAndNeverFires(string expression)
    {
        var (_, time, eb, sqs, queueUrl, queueArn) = await CreateAsync();
        using var _ = sqs;
        await eb.PutRuleAsync(new Eb.PutRuleRequest { Name = "never", ScheduleExpression = expression });
        await eb.PutTargetsAsync(new Eb.PutTargetsRequest { Rule = "never", Targets = [new Eb.Target { Id = "q", Arn = queueArn }] });

        time.Advance(TimeSpan.FromDays(400));

        (await DrainAsync(sqs, queueUrl)).ShouldBeEmpty();
        (await eb.DescribeRuleAsync(new Eb.DescribeRuleRequest { Name = "never" })).ScheduleExpression.ShouldBe(expression);
    }

    [Test]
    public void Schedule_LastDayOfWeek_IsSaturday()
    {
        EventBridgeSchedule.TryParse("cron(0 12 ? * L *)", out var schedule).ShouldBeTrue();

        // 2026-01-05 is a Monday.
        schedule.NextAfter(Start).ShouldBe(new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero));
    }
}

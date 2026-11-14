// Translated from behavioral/events/check_create_archive_and_describe.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.create-archive-and-describe</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateArchiveAndDescribe
{
    /// <summary>CreateArchive transitions to ENABLED and is reflected in DescribeArchive.</summary>
    [Test]
    [DisplayName("events.create-archive-and-describe")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busResp = await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = "behav" }] });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var archiveName = FidelityContext.UniqueName("sim2real-behav-archive-");
        await ev.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = archiveName, EventSourceArn = busResp.EventBusArn });
        ctx.OnCleanup(() => ev.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = archiveName }));
        var deadline = FidelityTime.StartNew();
        DescribeArchiveResponse? d = null;
        var settled = false;
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            d = await ev.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archiveName });
            if (d.State?.Value is "ENABLED" or "CREATE_FAILED")
            {
                settled = true;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        if (!settled)
        {
            throw Fail($"poll condition not met within 30s: {d}");
        }

        var desc = d!;
        Ensure(desc.State?.Value == "ENABLED", $"archive did not become ENABLED: {desc}");
        Ensure(
            desc.ArchiveName == archiveName,
            $"DescribeArchive returned a different name: expected '{archiveName}', got '{desc.ArchiveName}'");
    }
}

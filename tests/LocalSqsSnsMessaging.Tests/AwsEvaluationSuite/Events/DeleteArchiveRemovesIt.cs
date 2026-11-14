// Translated from behavioral/events/check_delete_archive_removes_it.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.delete-archive-removes-it</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteArchiveRemovesIt
{
    /// <summary>
    /// DeleteArchive removes the archive: an independent describe 404s, a mutation on the
    /// dead id also 404s, and the freed name is reusable by a fresh CreateArchive.
    /// </summary>
    [Test]
    [DisplayName("events.delete-archive-removes-it")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busResp = await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var archiveName = FidelityContext.UniqueName("sim2real-behav-archive-");
        await ev.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = archiveName, EventSourceArn = busResp.EventBusArn });
        ctx.OnCleanup(() => ev.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = archiveName }));
        await ev.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = archiveName });

        static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
        {
            var error = await FidelityContext.CaptureError(fn);
            if (error is null)
            {
                throw Fail("expected a ClientError against the deleted archive, call succeeded");
            }

            return (error.Code, error.Message);
        }

        var gone = $"Archive {archiveName} does not exist.";
        var (describeCode, describeMessage) = await Error(() => ev.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archiveName }));
        Ensure(
            describeCode == "ResourceNotFoundException",
            $"expected ResourceNotFoundException from DescribeArchive, got {describeCode}");
        Ensure(describeMessage == gone, $"expected '{gone}' from DescribeArchive, got '{describeMessage}'");

        var (updateCode, updateMessage) = await Error(
            () => ev.UpdateArchiveAsync(new UpdateArchiveRequest { ArchiveName = archiveName, Description = "post-delete-mutation-probe" }));
        Ensure(
            updateCode == "ResourceNotFoundException",
            $"expected ResourceNotFoundException from UpdateArchive on a deleted archive, got {updateCode}");
        Ensure(updateMessage == gone, $"expected '{gone}' from UpdateArchive, got '{updateMessage}'");

        await ev.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = archiveName, EventSourceArn = busResp.EventBusArn });
        var recreated = await ev.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archiveName });
        Ensure(
            recreated.ArchiveName == archiveName,
            $"expected the freed archive name to be reusable, got {recreated}");
    }
}

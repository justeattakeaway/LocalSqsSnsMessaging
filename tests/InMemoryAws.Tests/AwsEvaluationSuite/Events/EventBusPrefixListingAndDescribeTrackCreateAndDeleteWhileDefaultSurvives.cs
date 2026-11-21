// Translated from behavioral/events/check_event_bus_prefix_listing_and_describe_track_create_and_delete_while_default_survives.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.event-bus-prefix-listing-and-describe-track-create-and-delete-while-default-survives</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventBusPrefixListingAndDescribeTrackCreateAndDeleteWhileDefaultSurvives
{
    /// <summary>
    /// ListEventBuses(NamePrefix=name) lists a created bus with its Name and Arn, and returns
    /// EventBuses [] for that prefix once DeleteEventBus removes it; DescribeEventBus then fails
    /// ResourceNotFoundException with its exact message, while the default bus stays listed.
    /// </summary>
    [Test]
    [DisplayName("events.event-bus-prefix-listing-and-describe-track-create-and-delete-while-default-survives")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var name = FidelityContext.UniqueName("sim2real-behav-bus-");
        var created = await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = name });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name }));

        var before = (await ev.ListEventBusesAsync(new ListEventBusesRequest { NamePrefix = name })).EventBuses ?? [];
        Ensure(before.Count == 1, $"expected exactly one bus for prefix '{name}', got {Show(before)}");
        Ensure(before[0].Name == name, $"expected Name '{name}', got '{before[0].Name}'");
        Ensure(before[0].Arn == created.EventBusArn, $"expected Arn '{created.EventBusArn}', got '{before[0].Arn}'");

        await ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name });
        var after = (await ev.ListEventBusesAsync(new ListEventBusesRequest { NamePrefix = name })).EventBuses ?? [];
        Ensure(after.Count == 0, $"expected EventBuses [] for prefix '{name}' after delete, got {Show(after)}");

        await ExpectError(
            () => ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = name }),
            "ResourceNotFoundException", $"Event bus {name} does not exist.");

        var allNames = await ListNames(ev);
        Ensure(
            allNames.Contains("default"),
            $"default bus must still be listed by an unfiltered ListEventBuses, got {Py.Repr(allNames)}");
    }

    /// <summary>Assert fn() raises exactly (code, message).</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected {code} but the call succeeded: {result}");
        }

        Ensure(
            (error.Code, error.Message) == (code, message),
            $"expected ('{code}', '{message}'), got ('{error.Code}', '{error.Message}')");
    }

    /// <summary>Every bus Name from a fully paginated ListEventBuses.</summary>
    private static async Task<List<string>> ListNames(IAmazonEventBridge ev)
    {
        var names = new List<string>();
        var request = new ListEventBusesRequest();
        while (true)
        {
            var resp = await ev.ListEventBusesAsync(request);
            names.AddRange((resp.EventBuses ?? []).Select(b => b.Name));
            if (string.IsNullOrEmpty(resp.NextToken))
            {
                return names;
            }

            request = new ListEventBusesRequest { NextToken = resp.NextToken };
        }
    }

    private static string Show(List<EventBus> buses) =>
        "[" + string.Join(", ", buses.Select(b => $"{{'Name': '{b.Name}', 'Arn': '{b.Arn}'}}")) + "]";
}

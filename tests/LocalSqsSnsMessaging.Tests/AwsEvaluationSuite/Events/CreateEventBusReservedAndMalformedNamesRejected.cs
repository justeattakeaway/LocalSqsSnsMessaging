// Translated from behavioral/events/check_create_event_bus_reserved_and_malformed_names_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.create-event-bus-reserved-and-malformed-names-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateEventBusReservedAndMalformedNamesRejected
{
    private const string Check = "create-event-bus-reserved-and-malformed-names-rejected";

    /// <summary>
    /// CreateEventBus refuses an "aws." name, a name with "/" (partner buses only) and one over 256
    /// chars, each with its own message and no bus left behind; a well-formed name is accepted.
    /// </summary>
    [Test]
    [DisplayName("events.create-event-bus-reserved-and-malformed-names-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeValidation, "CreateEventBus accepts a name starting with 'aws.' instead of failing with ValidationException")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var token = FidelityContext.UniqueName("sim2real-behav-bus-");

        var control = $"{token}-ok";
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = control, Tags = [new Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = control }));
        Ensure((await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = control })).Name == control, "positive control bus was not created");

        var tooLong = (token + "-" + new string('x', 256))[..257];
        (string Name, string Expected)[] cases =
        [
            ($"aws.{token}", "EventBus name starting with 'aws.' is not valid."),
            ($"{token}/child", "Event bus name must not contain '/'."),
            (tooLong, $"1 validation error detected: Value '{tooLong}' at 'name' failed to satisfy " +
                      "constraint: Member must have length less than or equal to 256"),
        ];
        foreach (var (name, expected) in cases)
        {
            ctx.OnCleanup(() => DeleteIfPresent(events, name));
            var (code, message) = await Error(() => events.CreateEventBusAsync(new CreateEventBusRequest { Name = name }));
            Ensure(code == "ValidationException", $"CreateEventBus('{name[..Math.Min(60, name.Length)]}'...) gave '{code}' ('{message}')");
            Ensure(message == expected, $"CreateEventBus('{name[..Math.Min(60, name.Length)]}'...) message '{message}', expected '{expected}'");
        }

        var listed = await ListNames(events, token);
        Ensure(
            listed.SequenceEqual([control]),
            $"only the well-formed bus may exist under the run prefix; ListEventBuses returned [{string.Join(", ", listed.Select(n => $"'{n}'"))}]");
        var (awsCode, awsMessage) = await Error(() => events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = $"aws.{token}" }));
        Ensure(
            (awsCode, awsMessage) == ("ValidationException", "EventBus name starting with 'aws.' is not valid."),
            "a custom bus in the 'aws.' namespace can never exist, so DescribeEventBus must refuse the " +
            $"name the same way; got '{awsCode}' '{awsMessage}'");
    }

    private static async Task<List<string>> ListNames(IAmazonEventBridge events, string prefix)
    {
        var names = new List<string>();
        string? token = null;
        while (true)
        {
            var resp = await events.ListEventBusesAsync(new ListEventBusesRequest { NamePrefix = prefix, NextToken = string.IsNullOrEmpty(token) ? null : token });
            names.AddRange((resp.EventBuses ?? []).Select(b => b.Name));
            token = resp.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return [.. names.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>Teardown for a bus that should never exist; deletes it if it was wrongly created.</summary>
    private static async Task DeleteIfPresent(IAmazonEventBridge events, string name)
    {
        var error = await FidelityContext.CaptureError(() => events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = name }));
        if (error is not null)
        {
            if (error.Code is "ResourceNotFoundException" or "ValidationException")
            {
                return;
            }

            throw error.Exception;
        }

        await events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name });
    }

    /// <summary>(code, message) of a call that must fail; an unexpected success raises, naming the result.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }
}

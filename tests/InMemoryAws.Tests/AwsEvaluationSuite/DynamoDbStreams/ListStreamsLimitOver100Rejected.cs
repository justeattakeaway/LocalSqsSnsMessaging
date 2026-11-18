// Translated from behavioral/dynamodbstreams/check_list_streams_limit_over_100_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBStreams.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDbStreams;

/// <summary>dynamodbstreams.list-streams-limit-over-100-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListStreamsLimitOver100Rejected
{
    private static readonly (string Code, string Message) OverLimit = (
        "ValidationException",
        "One or more parameter values were invalid: Limit cannot be greater than the "
        + "maximum allowed size: 100");

    /// <summary>
    /// ListStreams rejects a Limit above the maximum of 100 with ValidationException, and the same
    /// call at the boundary, Limit=100, succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodbstreams.list-streams-limit-over-100-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var streams = ctx.DynamoStreams;

        await ExpectError(() => streams.ListStreamsAsync(new ListStreamsRequest { Limit = 101 }), OverLimit);

        // Positive control: the same call at the maximum is accepted (it raises otherwise).
        await streams.ListStreamsAsync(new ListStreamsRequest { Limit = 100 });
    }

    /// <summary>Assert <paramref name="call"/> raises exactly (code, message).</summary>
    private static async Task ExpectError(Func<Task<ListStreamsResponse>> call, (string Code, string Message) expected)
    {
        var (result, error) = await FidelityContext.Capture(call);
        if (error is null)
        {
            throw Fail($"expected {expected.Code} but the call succeeded: {result}");
        }

        var got = (error.Code, error.Message);
        Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
    }
}

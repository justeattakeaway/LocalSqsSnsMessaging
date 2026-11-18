// Translated from behavioral/dynamodb/check_global_table_already_exists.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.global-table-already-exists</summary>
[Category("AwsEvaluationSuite")]
public sealed class GlobalTableAlreadyExists
{
    private const string CheckId = "global-table-already-exists";

    private const string LegacyVersionMessage =
        "One or more parameter values were invalid: DynamoDB global tables version 2017.11.29 is not "
        + "supported. We recommend using DynamoDB global tables version 2019.11.21, instead of version "
        + "2017.11.29 (Legacy).";

    /// <summary>
    /// CreateGlobalTable never succeeds a second time for one name: where the legacy (2017.11.29) API
    /// still creates, the duplicate is refused GlobalTableAlreadyExistsException; where it is retired,
    /// the version-support ValidationException repeats identically on a second attempt.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.global-table-already-exists")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-gt-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES },
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await ddb.WaitForTableExists(tableName, delaySeconds: 2, maxAttempts: 250);

        CreateGlobalTableRequest CreateRequest() => new()
        {
            GlobalTableName = tableName,
            ReplicationGroup = [new Replica { RegionName = ctx.Region }],
        };

        var firstError = await FidelityContext.CaptureError(() => ddb.CreateGlobalTableAsync(CreateRequest()));
        var firstCode = firstError?.Code;
        var firstMessage = firstError?.Message ?? string.Empty;

        if (firstError is null)
        {
            // Legacy global tables ARE creatable here -- exercise the real
            // already-exists boundary on a duplicate attempt.
            await PollUntil(
                async () => (await ddb.DescribeGlobalTableAsync(new DescribeGlobalTableRequest { GlobalTableName = tableName })).GlobalTableDescription,
                d => d.GlobalTableStatus?.Value == "ACTIVE",
                timeoutSeconds: 180,
                intervalSeconds: 3.0,
                description: $"global table {tableName} ACTIVE");
            var (code, _) = await ErrorCodeAndMessage(() => ddb.CreateGlobalTableAsync(CreateRequest()));
            // Code only: the exact message of this refusal is not known, because current regions
            // refuse a legacy CreateGlobalTable outright.
            Ensure(code == "GlobalTableAlreadyExistsException", $"expected GlobalTableAlreadyExistsException, got {code}");
        }
        else
        {
            // Legacy (2017.11.29) global tables can no longer be created at all
            // here -- CreateGlobalTable must reject deterministically and
            // IDENTICALLY on every attempt (never silently succeeding on a retry).
            // The rejection must be the version gate specifically, not just any
            // ValidationException, or this would pass against a target that rejects
            // CreateGlobalTable for an unrelated reason (e.g. a malformed request).
            Ensure(firstCode == "ValidationException", $"expected ValidationException, got {firstCode}");
            Ensure(firstMessage == LegacyVersionMessage, $"expected message {Py.Repr(LegacyVersionMessage)}, got: {Py.Repr(firstMessage)}");
            var (secondCode, secondMessage) = await ErrorCodeAndMessage(() => ddb.CreateGlobalTableAsync(CreateRequest()));
            Ensure(secondCode == "ValidationException", $"expected the SAME deterministic rejection on a repeated attempt, got {secondCode}");
            Ensure(
                secondMessage == firstMessage,
                $"expected the identical rejection message on a repeated attempt, got: {Py.Repr(firstMessage)} then {Py.Repr(secondMessage)}");

            // Effect-absent: two rejected CreateGlobalTable calls must not have
            // registered the table as a global table on the side despite the error.
            var (notFoundCode, notFoundMessage) = await ErrorCodeAndMessage(() => ddb.DescribeGlobalTableAsync(new DescribeGlobalTableRequest { GlobalTableName = tableName }));
            Ensure(
                notFoundCode == "GlobalTableNotFoundException",
                $"CreateGlobalTable was rejected but the table is registered as a global table anyway (describe_global_table returned {Py.Repr(notFoundCode)} instead of GlobalTableNotFoundException)");
            var expectedNotFound = $"Global table not found: Global table with name: '{tableName}' does not exist.";
            Ensure(notFoundMessage == expectedNotFound, $"expected message {Py.Repr(expectedNotFound)}, got {Py.Repr(notFoundMessage)}");
        }
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds = 60, double intervalSeconds = 1.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"{description} not met within {timeoutSeconds}s. Last result: {Py.Repr(last)}");
    }

    /// <summary>
    /// Call fn(); return its error code and message. An unexpected SUCCESS raises, so an
    /// emulator that accepts what AWS refuses never reads as a code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }
}

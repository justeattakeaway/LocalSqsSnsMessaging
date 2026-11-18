// Translated from behavioral/dynamodb/check_gsi_scan_unused_filter_expression_value_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-scan-unused-filter-expression-value-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiScanUnusedFilterExpressionValueRejected
{
    private const string CheckId = "gsi-scan-unused-filter-expression-value-rejected";
    private const string Idx = "by-tag";
    private const string Filter = "tag = :none_tag AND resolvedAt > :zero";

    /// <summary>
    /// id (S) hash key on the base table; tag (S) is the GSI's own hash key (never empty: a GSI key
    /// attribute rejects the empty string).
    /// </summary>
    private static List<Dictionary<string, AttributeValue>> Items() =>
    [
        Item(("id", S("item1")), ("tag", S("none")), ("resolvedAt", N("1640995200"))),
        Item(("id", S("item2")), ("tag", S("none")), ("resolvedAt", N("1672531200"))),
        Item(("id", S("item3")), ("tag", S("active")), ("resolvedAt", N("0"))),
    ];

    private static Dictionary<string, AttributeValue> Values() => Item((":none_tag", S("none")), (":zero", N("0")));

    private static List<string> Match() =>
        [.. Items().Where(it => it["tag"].S == "none" && long.Parse(it["resolvedAt"].N, CultureInfo.InvariantCulture) > 0).Select(it => it["id"].S).Order(StringComparer.Ordinal)];

    private static string TupleRepr(IEnumerable<string> values)
    {
        var list = values.ToList();
        return "(" + string.Join(", ", list.Select(v => Py.Repr(v))) + (list.Count == 1 ? ",)" : ")");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// Poll <paramref name="fn"/> until <paramref name="pred"/> holds: a GSI is eventually consistent, so this waits rather than
    /// sleeping a fixed amount.
    /// </summary>
    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutSeconds, double intervalSeconds = 2.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw Fail($"{description} not met within {timeoutSeconds}s. Last: {Py.Repr(value)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
            value = await fn();
        }

        return value;
    }

    /// <summary>
    /// A Scan of a global secondary index with an ExpressionAttributeValue the FilterExpression
    /// never references is refused ValidationException naming the unused key, while a
    /// FilterExpression that uses every value is served from the index.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-scan-unused-filter-expression-value-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsiscanfx-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S), new AttributeDefinition("tag", ScalarAttributeType.S)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Idx,
                    KeySchema = [new KeySchemaElement("tag", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal)
            {
                [table] = [.. Items().Select(item => new WriteRequest { PutRequest = new PutRequest { Item = item } })],
            },
        });
        Ensure((resp.UnprocessedItems ?? []).Count == 0, $"BatchWriteItem left {Py.Repr(resp.UnprocessedItems)} unprocessed while seeding");

        // --- positive control: a FilterExpression using every value is served from the index, once
        // --- the write has propagated into it (a GSI is eventually consistent) ---
        var match = Match();
        var good = await Poll(
            () => ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = Idx, FilterExpression = Filter, ExpressionAttributeValues = Values() }),
            r => r.Count == match.Count,
            timeoutSeconds: 120,
            description: $"the {match.Count} matching item(s) propagating into {Idx}");
        var goodIds = good.Items.Select(it => it["id"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(goodIds.SequenceEqual(match), $"the control scan via {Py.Repr(Idx)} returned {TupleRepr(goodIds)}, expected {TupleRepr(match)}");

        // --- an ExpressionAttributeValue the filter never references is refused ---
        var values = Values();
        values[":unused"] = S("x");
        var (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            IndexName = Idx,
            FilterExpression = Filter,
            ExpressionAttributeValues = values,
        }));
        Ensure(code == "ValidationException", $"the index scan with an unused value was refused {Py.Repr(code)} ({Py.Repr(message)}), expected ValidationException");
        const string expected = "Value provided in ExpressionAttributeValues unused in expressions: keys: {:unused}";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
    }
}

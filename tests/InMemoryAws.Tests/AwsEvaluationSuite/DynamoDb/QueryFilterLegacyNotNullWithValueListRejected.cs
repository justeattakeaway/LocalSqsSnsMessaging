// Translated from behavioral/dynamodb/check_query_filter_legacy_not_null_with_value_list_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-legacy-not-null-with-value-list-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterLegacyNotNullWithValueListRejected
{
    private const string Check = "query-filter-legacy-not-null-with-value-list-rejected";

    private static readonly (string Sk, string? Memo)[] Rows =
    [
        ("s0", null),
        ("s1", "a"),
        ("s2", "b"),
    ];

    private static readonly string[] WithMemo = [.. Rows.Where(r => r.Memo is not null).Select(r => r.Sk).Order(StringComparer.Ordinal)];

    private static readonly (string Code, string Message) Err = ("ValidationException",
        "One or more parameter values were invalid: " +
        "Invalid number of argument(s) for the NOT_NULL ComparisonOperator");

    private static string[] Ids(QueryResponse resp) => [.. resp.Items.Select(i => i["sk"].S).Order(StringComparer.Ordinal)];

    /// <summary>Python's repr of a tuple of strings.</summary>
    private static string TupleRepr(string[] values) =>
        values.Length == 1 ? $"({Py.Repr(values[0])},)" : "(" + string.Join(", ", values.Select(v => Py.Repr(v))) + ")";

    /// <summary>The AWS error (code, message) fn() raises. Raises when the call succeeds instead.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message ?? "");
    }

    /// <summary>
    /// Query's legacy QueryFilter NOT_NULL supplied with an AttributeValueList is refused with
    /// ValidationException; a well-formed NOT_NULL filter still answers over the same partition.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-legacy-not-null-with-value-list-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        foreach (var row in Rows)
        {
            var item = Item(("pk", S("p1")), ("sk", S(row.Sk)));
            if (row.Memo is not null)
            {
                item["memo"] = S(row.Memo);
            }

            await ddb.PutItemAsync(t, item);
        }

        QueryRequest Request(Condition memo) => new()
        {
            TableName = t,
            KeyConditions = new() { ["pk"] = new Condition { AttributeValueList = [S("p1")], ComparisonOperator = ComparisonOperator.EQ } },
            QueryFilter = new() { ["memo"] = memo },
            ConsistentRead = true,
        };

        // Positive control: the well-formed NOT_NULL filter answers correctly.
        var good = await ddb.QueryAsync(Request(new Condition { ComparisonOperator = ComparisonOperator.NOT_NULL }));
        Ensure(Ids(good).SequenceEqual(WithMemo), $"the control NOT_NULL query returned {TupleRepr(Ids(good))}, expected {TupleRepr(WithMemo)}");

        // Break exactly one thing: NOT_NULL takes zero operands.
        var (code, msg) = await Error(() => ddb.QueryAsync(Request(new Condition
        {
            ComparisonOperator = ComparisonOperator.NOT_NULL,
            AttributeValueList = [S("x")],
        })));
        Ensure((code, msg) == Err, $"expected {Py.Repr(Err)}, got {Py.Repr((code, msg))}");

        // A rejected query changes nothing: the control still answers exactly as before.
        var again = await ddb.QueryAsync(Request(new Condition { ComparisonOperator = ComparisonOperator.NOT_NULL }));
        Ensure(
            Ids(again).SequenceEqual(WithMemo) && again.ScannedCount == Rows.Length,
            $"after the rejection the control query returned {TupleRepr(Ids(again))} over {Py.Repr(again.ScannedCount)} scanned, expected {TupleRepr(WithMemo)} over {Rows.Length}");
    }
}

// Translated from behavioral/dynamodb/check_query_exclusive_start_key_never_written_item_returns_empty_page_and_missing_sort_key_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-exclusive-start-key-never-written-item-returns-empty-page-and-missing-sort-key-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryExclusiveStartKeyNeverWrittenItemReturnsEmptyPageAndMissingSortKeyRejected
{
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// Query against the base table accepts an ExclusiveStartKey naming a sort key value
    /// that was never written, past the last real item, and returns an empty page; the same
    /// call with an ExclusiveStartKey missing the table's sort key is a ValidationException.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-exclusive-start-key-never-written-item-returns-empty-page-and-missing-sort-key-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = "true" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 90);

        for (var i = 0; i < 10; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("x")), ("sk", S($"{i:000}"))));
        }

        QueryRequest KeyCondition(Dictionary<string, AttributeValue>? exclusiveStartKey = null, bool? consistentRead = null) => new()
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("x"))),
            ExclusiveStartKey = exclusiveStartKey,
            ConsistentRead = consistentRead,
        };

        // Positive control: an ExclusiveStartKey resuming mid-range from a sort key that was never
        // written still continues forward from that position, returning the real items after it.
        var mid = await ddb.QueryAsync(KeyCondition(Item(("pk", S("x")), ("sk", S("004a"))), consistentRead: true));
        var midSks = mid.Items.Select(i => i["sk"].S).ToList();
        Ensure(
            midSks.SequenceEqual(["005", "006", "007", "008", "009"]),
            "positive control: expected a never-written mid-range key to resume with " +
            $"['005'..'009'], got {Py.Repr(midSks)}");

        // Behaviour under test: an ExclusiveStartKey naming a sort key past the last real item,
        // never written, is accepted and returns an empty page -- not an error.
        var pastEnd = await ddb.QueryAsync(KeyCondition(Item(("pk", S("x")), ("sk", S("999"))), consistentRead: true));
        Ensure(pastEnd.Items.Count == 0, $"expected an empty page resuming past the last item, got {Py.Repr(pastEnd.Items)}");
        Ensure(pastEnd.Count == 0, $"expected Count 0, got {Py.Repr(pastEnd.Count)}");
        Ensure(
            pastEnd.LastEvaluatedKey is not { Count: > 0 },
            $"expected no LastEvaluatedKey on the final empty page, got {Py.Repr(pastEnd.LastEvaluatedKey)}");

        // Negative: an ExclusiveStartKey missing the table's sort key is rejected outright.
        var (code, message) = await Error(() => ddb.QueryAsync(KeyCondition(Item(("pk", S("x"))))));
        Ensure(code == ErrValidation, $"Query with ExclusiveStartKey missing sk: expected {ErrValidation}, got {code}");
        Ensure(message == "The provided starting key is invalid", $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        // Every call above was a read: confirm none of them wrote, deleted or corrupted an item.
        var final = await ddb.QueryAsync(KeyCondition(consistentRead: true));
        var finalSks = final.Items.Select(i => i["sk"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(
            finalSks.SetEquals(Enumerable.Range(0, 10).Select(i => $"{i:000}")),
            $"expected the original 10 items unchanged after the calls above, got {Py.Repr(finalSks.Order(StringComparer.Ordinal).ToList())}");
    }

    /// <summary><c>(Code, Message)</c> for the error <paramref name="fn"/> raises; an unexpected success raises too.</summary>
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
}

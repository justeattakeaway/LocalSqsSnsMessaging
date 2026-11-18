// Translated from behavioral/dynamodb/check_query_key_condition_duplicate_sort_key_clause_and_nested_path_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-key-condition-duplicate-sort-key-clause-and-nested-path-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryKeyConditionDuplicateSortKeyClauseAndNestedPathRejected
{
    private const string Check = "query-key-condition-duplicate-sort-key-clause-and-nested-path-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>The AWS error (code, message) fn() raises. Raises when the call SUCCEEDS.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it SUCCEEDED: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// A KeyConditionExpression naming the sort key in two ANDed clauses (a range split across
    /// two comparisons rather than BETWEEN) is rejected, and so is one addressing a key attribute
    /// through a nested path; neither reaches the table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-key-condition-duplicate-sort-key-clause-and-nested-path-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);
        await ddb.PutItemAsync(table, Item(("pk", S("p")), ("sk", S("m"))));

        // Positive control: the same partition, a legal single-clause-per-key condition, succeeds.
        var control = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p AND sk > :lo",
            ExpressionAttributeValues = Item((":p", S("p")), (":lo", S("a"))),
            ConsistentRead = true,
        });
        Ensure(control.Count == 1, $"positive control: expected 1 item, got {control.Count}");

        // Two ANDed clauses both naming the sort key (a hand-rolled range instead of BETWEEN).
        var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p AND sk > :lo AND sk < :hi",
            ExpressionAttributeValues = Item((":p", S("p")), (":lo", S("a")), (":hi", S("z"))),
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code} ({Py.Repr(message)})");
        const string expectedDuplicate = "Invalid KeyConditionExpression: KeyConditionExpressions must only contain one condition per key";
        Ensure(message == expectedDuplicate, $"expected message {Py.Repr(expectedDuplicate)}, got {Py.Repr(message)}");

        // A nested path (#sk.x) addressing the sort key, which is a scalar, not a document.
        var (code2, message2) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p AND #s.x = :v",
            ExpressionAttributeNames = Names(("#s", "sk")),
            ExpressionAttributeValues = Item((":p", S("p")), (":v", S("y"))),
        }));
        Ensure(code2 == ErrValidation, $"expected {ErrValidation}, got {code2} ({Py.Repr(message2)})");
        const string expectedNested = "Invalid KeyConditionExpression: KeyConditionExpressions cannot have conditions on nested attributes";
        Ensure(message2 == expectedNested, $"expected message {Py.Repr(expectedNested)}, got {Py.Repr(message2)}");

        // A nested path (#pk.x) addressing the partition key is rejected the same way.
        var (code3, message3) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "#p.x = :p AND sk = :v",
            ExpressionAttributeNames = Names(("#p", "pk")),
            ExpressionAttributeValues = Item((":p", S("p")), (":v", S("m"))),
        }));
        Ensure(code3 == ErrValidation, $"expected {ErrValidation}, got {code3} ({Py.Repr(message3)})");
        Ensure(message3 == expectedNested, $"expected message {Py.Repr(expectedNested)}, got {Py.Repr(message3)}");

        // Read back: the table still holds exactly the one item the positive control proved,
        // never more (nothing the three rejected calls would have written) and never less.
        var final = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        Ensure(final.Count == 1, $"expected the table unchanged at 1 item, got {final.Count}");
        Ensure(
            Py.Equal(final.Items, new[] { Item(("pk", S("p")), ("sk", S("m"))) }),
            $"expected only the original item, got {Py.Repr(final.Items)}");
    }
}

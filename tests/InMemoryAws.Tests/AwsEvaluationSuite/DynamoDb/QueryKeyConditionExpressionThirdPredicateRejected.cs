// Translated from behavioral/dynamodb/check_query_key_condition_expression_third_predicate_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-key-condition-expression-third-predicate-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryKeyConditionExpressionThirdPredicateRejected
{
    private const string Check = "query-key-condition-expression-third-predicate-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>The AWS error (code, message) fn() raises. Raises when the call SUCCEEDS.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected the call to be rejected, but it was accepted");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// A KeyConditionExpression with a third ANDed clause, or a second clause naming a non-sort-key
    /// attribute, is rejected with ValidationException and its exact message, not applied as a filter.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-key-condition-expression-third-predicate-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-idx-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("status", ScalarAttributeType.S),
                new AttributeDefinition("createdAt", ScalarAttributeType.S),
                new AttributeDefinition("alternate", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "status-index",
                    KeySchema = [new KeySchemaElement("status", KeyType.HASH), new KeySchemaElement("createdAt", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["summary"] },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "alternate-index",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("alternate", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        // Positive control: the identical two-predicate form (hash + range, both the GSI's
        // own key schema) is accepted, so the rejections below are shown to be about the
        // THIRD predicate, not about this wiring in general.
        var control = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            IndexName = "status-index",
            KeyConditionExpression = "#s = :status AND #c > :createdAt",
            ExpressionAttributeNames = Names(("#s", "status"), ("#c", "createdAt")),
            ExpressionAttributeValues = Item((":status", S("open")), (":createdAt", S("2020-01-01"))),
        });
        Ensure(control.Count == 0, $"positive control: expected 0 items on an empty index, got {control.Count}");

        // Two predicates: the index hash key ANDed with a clause naming a real attribute that is NOT
        // this index's sort key (it is the LSI's). The second clause is read as the sort-key condition,
        // so the refusal names the sort key it expected.
        var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            IndexName = "status-index",
            KeyConditionExpression = "#s = :status AND alternate = :alternate",
            ExpressionAttributeNames = Names(("#s", "status")),
            ExpressionAttributeValues = Item((":status", S("open")), (":alternate", S("a1"))),
        }));
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code} ({Py.Repr(message)})");
        const string expected = "Query condition missed key schema element: createdAt";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        // Three predicates: hash, range, and a third ANDed clause.
        var (code2, message2) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            IndexName = "status-index",
            KeyConditionExpression = "#s = :status AND #c > :createdAt AND alternate = :alternate",
            ExpressionAttributeNames = Names(("#s", "status"), ("#c", "createdAt")),
            ExpressionAttributeValues = Item(
                (":status", S("open")),
                (":createdAt", S("2020-01-01")),
                (":alternate", S("a1"))),
        }));
        Ensure(code2 == ErrValidation, $"expected {ErrValidation}, got {code2} ({Py.Repr(message2)})");
        const string expected2 =
            "The number of query conditions (3) exceeds the number of key attributes defined in the " +
            "schema (2)";
        Ensure(message2 == expected2, $"expected message {Py.Repr(expected2)}, got {Py.Repr(message2)}");

        // Final control: the two-predicate form still works after both rejections above.
        var controlAfter = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            IndexName = "status-index",
            KeyConditionExpression = "#s = :status",
            ExpressionAttributeNames = Names(("#s", "status")),
            ExpressionAttributeValues = Item((":status", S("open"))),
        });
        Ensure(controlAfter.Count == 0, $"positive control after rejections: expected 0 items, got {controlAfter.Count}");
    }
}

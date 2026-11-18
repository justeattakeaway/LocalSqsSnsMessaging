// Translated from behavioral/dynamodb/check_query_base_table_primary_key_in_filter_expression_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-base-table-primary-key-in-filter-expression-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryBaseTablePrimaryKeyInFilterExpressionRejected
{
    private const string CheckId = "query-base-table-primary-key-in-filter-expression-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>The (code, message) <c>fn</c> is refused with. An unexpected success raises, naming it.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    /// <summary>
    /// A base-table Query whose FilterExpression names the table's own hash or range key raises
    /// ValidationException naming that attribute, while a filter on a non-key attribute matches.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-base-table-primary-key-in-filter-expression-rejected")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("s1")), ("status", S("ok"))));

        // Positive control: the same query shape with a FilterExpression on a non-key attribute
        // matches the seeded item, so the refusals below are about the selected key, not the
        // call shape or FilterExpression itself.
        var control = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "pk = :p",
            FilterExpression = "#s = :v",
            ExpressionAttributeNames = Names(("#s", "status")),
            ExpressionAttributeValues = Item((":p", S("p1")), (":v", S("ok"))),
        });
        Ensure(control.Count == 1, $"positive control: expected the seeded item, got Count={control.Count}");

        var (code, message) = await Error(async () => await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "pk = :p",
            FilterExpression = "pk = :p2",
            ExpressionAttributeValues = Item((":p", S("p1")), (":p2", S("p1"))),
        }));
        Ensure(code == ErrValidation, $"hash key in FilterExpression: expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        Ensure(
            message == "Filter Expression can only contain non-primary key attributes: Primary key attribute: pk",
            $"hash key in FilterExpression: unexpected message {Py.Repr(message)}");

        (code, message) = await Error(async () => await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "pk = :p",
            FilterExpression = "sk = :s",
            ExpressionAttributeValues = Item((":p", S("p1")), (":s", S("s1"))),
        }));
        Ensure(code == ErrValidation, $"range key in FilterExpression: expected {ErrValidation}, got {code}: {Py.Repr(message)}");
        Ensure(
            message == "Filter Expression can only contain non-primary key attributes: Primary key attribute: sk",
            $"range key in FilterExpression: unexpected message {Py.Repr(message)}");

        // Nothing either rejected call could have written: the table still holds only the seed.
        var final = (await ddb.ScanAsync(new ScanRequest { TableName = t, ConsistentRead = true })).Items;
        Ensure(
            Py.Equal(final, new[] { Item(("pk", S("p1")), ("sk", S("s1")), ("status", S("ok"))) }),
            $"expected only the seed item to remain, got {Py.Repr(final)}");
    }
}

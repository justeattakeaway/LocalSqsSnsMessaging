// Translated from behavioral/dynamodb/check_lsi_query_sort_key_value_type_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-query-sort-key-value-type-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiQuerySortKeyValueTypeMismatchRejected
{
    private const string CheckId = "lsi-query-sort-key-value-type-mismatch-rejected";
    private const string ErrValidation = "ValidationException";
    private const string SchemaTypeMismatch = "One or more parameter values were invalid: Condition parameter type does not match schema type";

    /// <summary>
    /// Querying an LSI with a key-condition value whose type (S) mismatches the LSI sort key's
    /// declared type (N) is rejected with ValidationException; the correctly typed value finds the
    /// item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.lsi-query-sort-key-value-type-mismatch-rejected")]
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
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(table, Item(("pk", S("item")), ("sk", S("1")), ("lsk", N("5"))));

        var (result, error) = await FidelityContext.Capture(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi1",
            KeyConditionExpression = "pk = :pk AND lsk = :lsk",
            ExpressionAttributeValues = Item((":pk", S("item")), (":lsk", S("5"))),
        }));
        if (error is null)
        {
            throw Fail($"a Query whose LSI sort-key value type (S) mismatches the schema type (N) must be rejected, but it SUCCEEDED and returned {Py.Repr(result)}");
        }

        Ensure(error.Code == ErrValidation, $"expected {ErrValidation}, got {error.Code}");
        Ensure(error.Message == SchemaTypeMismatch, $"expected AWS's exact ValidationException message, got {Py.Repr(error.Message)}");

        var control = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi1",
            KeyConditionExpression = "pk = :pk AND lsk = :lsk",
            ExpressionAttributeValues = Item((":pk", S("item")), (":lsk", N("5"))),
            ConsistentRead = true,
        });
        Ensure(control.Count == 1, $"the correctly typed control query must find the item, got {control.Count} items");
        Ensure(control.Items[0]["sk"].S == "1", $"the control resolved sk={Py.Repr(control.Items[0]["sk"].S)}, expected '1'");
    }
}

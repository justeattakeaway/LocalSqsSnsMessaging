// Translated from behavioral/dynamodb/check_query_capacity_gsi_rejects_consistent_read_lsi_accepts_it.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-capacity-gsi-rejects-consistent-read-lsi-accepts-it</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryCapacityGsiRejectsConsistentReadLsiAcceptsIt
{
    private const string CheckId = "query-capacity-gsi-rejects-consistent-read-lsi-accepts-it";
    private const string ErrValidation = "ValidationException";
    private const string Gsi = "gsi1";
    private const string Lsi = "lsi1";

    /// <summary>The error code and message <paramref name="fn"/> raises; an unexpected success fails loudly instead.</summary>
    private static async Task<(string? Code, string? Message)> Code(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS refuses this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    /// <summary>
    /// ConsistentRead=True on an ALL-projection GSI Query is refused, the boundary that is why a
    /// GSI's ReturnConsumedCapacity is only ever measured at eventual consistency, while the
    /// identical flag on the sibling ALL-projection LSI is accepted.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-capacity-gsi-rejects-consistent-read-lsi-accepts-it")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ccgsilsi-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Gsi,
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = Lsi,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        await ddb.PutItemAsync(table, Item(("pk", S("P")), ("sk", S("S")), ("gpk", S("G")), ("lsk", S("L"))));

        // --- break exactly one thing: ConsistentRead=True against the GSI ---
        var (code, message) = await Code(async () => await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Gsi,
            ConsistentRead = true,
            KeyConditionExpression = "gpk = :g",
            ExpressionAttributeValues = Item((":g", S("G"))),
        }));
        Ensure(code == ErrValidation, $"a strongly consistent Query against a GSI must be refused with {ErrValidation}; got {code}");
        Ensure(
            message == "Consistent reads are not supported on global secondary indexes",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        // --- positive control: the identical flag against the sibling LSI is accepted ---
        var lsiStrong = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Lsi,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("P"))),
        });
        Ensure(
            Py.Equal(lsiStrong.Items, new[] { Item(("pk", S("P")), ("sk", S("S")), ("gpk", S("G")), ("lsk", S("L"))) }),
            $"ConsistentRead=True against the ALL-projection LSI must succeed and return the item written just before it, got {Py.Repr(lsiStrong.Items)}");
    }
}

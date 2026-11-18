// Translated from behavioral/dynamodb/check_gsi_query_omits_attributes_the_projection_excludes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-query-omits-attributes-the-projection-excludes</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiQueryOmitsAttributesTheProjectionExcludes
{
    private const string CheckId = "gsi-query-omits-attributes-the-projection-excludes";
    private const string ErrValidation = "ValidationException";
    private static readonly string[] ExpectedAttributes = ["pk", "sk", "gsi1pk", "kept"];

    /// <summary>
    /// A GSI with INCLUDE ['kept'] returns only the keys plus 'kept' while the base item holds
    /// 'dropped', and a ProjectionExpression naming 'dropped' on the index is refused
    /// ValidationException with AWS's message.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-query-omits-attributes-the-projection-excludes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsiproj-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gsi1pk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gsi1pk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["kept"] },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var clock = FidelityTime.StartNew();
        Dictionary<string, string> indexStatus = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(300))
        {
            var described = (await ddb.DescribeTableAsync(table)).Table;
            indexStatus = (described.GlobalSecondaryIndexes ?? []).ToDictionary(i => i.IndexName, i => i.IndexStatus.Value, StringComparer.Ordinal);
            if (indexStatus.GetValueOrDefault("gsi1") == "ACTIVE")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(indexStatus.GetValueOrDefault("gsi1") == "ACTIVE", $"gsi1 never became ACTIVE within 300s: {Py.Repr(indexStatus)}");

        const string pk = "CUSTOMER#1";
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("sk", S("S1")), ("gsi1pk", S("A")), ("kept", S("yes")), ("dropped", S("no"))));

        clock = FidelityTime.StartNew();
        List<Dictionary<string, AttributeValue>> items = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(120))
        {
            items = (await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = "gsi1",
                KeyConditionExpression = "gsi1pk = :g",
                ExpressionAttributeValues = Item((":g", S("A"))),
            })).Items;
            if (items.Count > 0)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(items.Count == 1, $"the index query did not return exactly the one item: {Py.Repr(items)}");
        var idxItem = items[0];

        Ensure(
            idxItem.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(ExpectedAttributes),
            $"the GSI projection returned the wrong attribute set: {Py.Repr(idxItem.Keys.Order(StringComparer.Ordinal).ToList())}, expected ['gsi1pk', 'kept', 'pk', 'sk']");
        Ensure(!idxItem.ContainsKey("dropped"), $"a non-projected attribute leaked out of the index: {Py.Repr(idxItem)}");
        Ensure(idxItem["kept"].S == "yes", $"the projected attribute is missing or wrong: {Py.Repr(idxItem)}");

        var baseItem = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(pk)), ("sk", S("S1"))),
            ConsistentRead = true,
        });
        Ensure(
            baseItem.Item["dropped"].S == "no",
            $"the excluded attribute is not on the base item, so the absence asserted above is vacuous: {Py.Repr(baseItem.Item)}");

        // AWS refuses to be asked for a non-projected attribute rather than silently omitting it.
        var (namedResponse, refused) = await FidelityContext.Capture(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "gsi1",
            KeyConditionExpression = "gsi1pk = :g",
            ExpressionAttributeValues = Item((":g", S("A"))),
            ProjectionExpression = "pk, dropped",
        }));
        var named = namedResponse?.Items;
        Ensure(
            named is null,
            $"a ProjectionExpression naming a non-projected attribute must be refused, but the query was accepted and returned {Py.Repr(named)}");
        Ensure(
            refused!.Code == ErrValidation,
            $"expected {Py.Repr(ErrValidation)} when asking a GSI for an attribute it does not project, got {Py.Repr(refused.Code)} ({Py.Repr(refused.Message)})");
        Ensure(
            refused.Message == "One or more parameter values were invalid: Global secondary index gsi1 does not project [dropped]",
            $"expected AWS's exact ValidationException message, got {Py.Repr(refused.Message)}");
    }
}

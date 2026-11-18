// Translated from behavioral/dynamodb/check_legacy_attribute_updates_dotted_name_is_literal.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-attribute-updates-dotted-name-is-literal</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyAttributeUpdatesDottedNameIsLiteral
{
    private const string CheckId = "legacy-attribute-updates-dotted-name-is-literal";

    /// <summary>
    /// Legacy AttributeUpdates keys an attribute by its literal name: a dot in that
    /// name creates one top-level attribute rather than reaching inside a nested map.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-attribute-updates-dotted-name-is-literal")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real-behav", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(t, Item(("pk", S("movie")), ("info", M(("plot", S("original"))))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("movie"))),
            AttributeUpdates = new Dictionary<string, AttributeValueUpdate>(StringComparer.Ordinal)
            {
                ["info.plot"] = new AttributeValueUpdate { Value = S("rewritten"), Action = AttributeAction.PUT },
            },
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("movie"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got.GetValueOrDefault("info.plot"), S("rewritten")), $"expected a literal top-level attribute named 'info.plot', got {Py.Repr(got)}");
        Ensure(
            Py.Equal(got.GetValueOrDefault("info"), M(("plot", S("original")))),
            $"AttributeUpdates keyed by 'info.plot' must not touch the nested info.plot path, got {Py.Repr(got.GetValueOrDefault("info"))}");
    }
}

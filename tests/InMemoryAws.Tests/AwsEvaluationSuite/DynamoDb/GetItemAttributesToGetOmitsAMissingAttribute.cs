// Translated from behavioral/dynamodb/check_get_item_attributes_to_get_omits_a_missing_attribute.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.get-item-attributes-to-get-omits-a-missing-attribute</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetItemAttributesToGetOmitsAMissingAttribute
{
    /// <summary>
    /// The legacy AttributesToGet naming an attribute the item does not have OMITS it rather than
    /// erroring -- an over-strict engine rejects the read instead.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.get-item-attributes-to-get-omits-a-missing-attribute")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("a")), ("v", S("x"))));
        var item = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("a"))),
            AttributesToGet = ["v", "no-such-attribute"],
            ConsistentRead = true,
        })).Item;
        Ensure(
            Py.Equal(item, Item(("v", S("x")))),
            $"a missing attribute is omitted, not an error, and nothing else is added: got {Py.Repr(item)}");
    }
}

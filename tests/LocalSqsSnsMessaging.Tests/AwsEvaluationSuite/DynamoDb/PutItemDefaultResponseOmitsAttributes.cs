// Translated from behavioral/dynamodb/check_put_item_default_response_omits_attributes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-default-response-omits-attributes</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemDefaultResponseOmitsAttributes
{
    /// <summary>The response members boto3 would hand back (the ones present), sorted, without ResponseMetadata.</summary>
    private static List<string> Keys(PutItemResponse res)
    {
        var keys = new List<string>();
        if (res.Attributes is not null)
        {
            keys.Add("Attributes");
        }

        if (res.ConsumedCapacity is not null)
        {
            keys.Add("ConsumedCapacity");
        }

        if (res.ItemCollectionMetrics is not null)
        {
            keys.Add("ItemCollectionMetrics");
        }

        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    /// <summary>
    /// With no ReturnValues and no ReturnConsumedCapacity, an overwriting PutItem answers with NO
    /// Attributes and no capacity -- the defaults are NONE, so a chatty response is a divergence.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-default-response-omits-attributes")]
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
        await ddb.PutItemAsync(table, Item(("pk", S("a")), ("v", N("1"))));
        var res = await ddb.PutItemAsync(table, Item(("pk", S("a")), ("v", N("2"))));
        var keys = Keys(res);
        Ensure(
            !keys.Contains("Attributes"),
            $"ReturnValues defaults to NONE, so an overwrite returns no Attributes, got {Py.Repr(keys)}");
        Ensure(
            !keys.Contains("ConsumedCapacity"),
            $"ReturnConsumedCapacity defaults to NONE, so no capacity is reported, got {Py.Repr(keys)}");
    }
}

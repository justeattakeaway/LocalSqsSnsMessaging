// Translated from behavioral/dynamodb/check_expression_attribute_names.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.expression-attribute-names</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExpressionAttributeNames
{
    /// <summary>
    /// ExpressionAttributeNames allows using reserved words as attribute names, and
    /// GetItem's ProjectionExpression actually restricts the returned attributes to the
    /// aliased set rather than returning the whole item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.expression-attribute-names")]
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
            Tags = [new Tag { Key = "sim2real", Value = "expression-attribute-names" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("ean")), ("status", S("active")), ("name", S("test")), ("secret", S("unrequested"))));
        var resp = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("ean"))),
            ProjectionExpression = "#s, #n",
            ExpressionAttributeNames = Names(("#s", "status"), ("#n", "name")),
            ConsistentRead = true,
        });
        var got = resp.Item;
        var keys = got.Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(Py.Equal(got.GetValueOrDefault("status"), S("active")), $"expected status=active via #s alias, got {Py.Repr(got.GetValueOrDefault("status"))}");
        Ensure(Py.Equal(got.GetValueOrDefault("name"), S("test")), $"expected name=test via #n alias, got {Py.Repr(got.GetValueOrDefault("name"))}");
        Ensure(!got.ContainsKey("secret"), $"ProjectionExpression '#s, #n' must exclude unaliased attributes, got keys {Py.Repr(keys)}");
        Ensure(got.Count == 2 && got.ContainsKey("status") && got.ContainsKey("name"), $"expected exactly the aliased attributes, got keys {Py.Repr(keys)}");
    }
}

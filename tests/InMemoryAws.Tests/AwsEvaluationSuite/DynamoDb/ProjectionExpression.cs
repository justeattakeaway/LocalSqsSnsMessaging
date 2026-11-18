// Translated from behavioral/dynamodb/check_projection_expression.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.projection-expression</summary>
[Category("AwsEvaluationSuite")]
public sealed class ProjectionExpression
{
    private const string CheckId = "projection-expression";

    /// <summary>ProjectionExpression limits attributes returned by GetItem.</summary>
    [Test]
    [DisplayName("dynamodb.projection-expression")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("proj")), ("a", S("1")), ("b", S("2")), ("c", S("3"))));
        var resp = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("proj"))),
            ProjectionExpression = "pk, a",
            ConsistentRead = true,
        });
        var got = resp.Item;
        var keys = Py.Repr(got.Keys.Order(StringComparer.Ordinal).ToList());
        Ensure(got.ContainsKey("a"), $"expected projected attribute a present, got keys {keys}");
        Ensure(got.ContainsKey("pk"), $"expected key attribute pk present, got keys {keys}");
        Ensure(!got.ContainsKey("b"), $"expected non-projected attribute b absent, got keys {keys}");
        Ensure(!got.ContainsKey("c"), $"expected non-projected attribute c absent, got keys {keys}");
    }
}

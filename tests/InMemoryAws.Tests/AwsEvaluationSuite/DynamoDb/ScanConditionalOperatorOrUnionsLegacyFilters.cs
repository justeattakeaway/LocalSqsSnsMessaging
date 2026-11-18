// Translated from behavioral/dynamodb/check_scan_conditional_operator_or_unions_legacy_filters.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-conditional-operator-or-unions-legacy-filters</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanConditionalOperatorOrUnionsLegacyFilters
{
    /// <summary>
    /// ConditionalOperator=OR over two legacy ScanFilter entries returns items matching EITHER,
    /// not their intersection.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-conditional-operator-or-unions-legacy-filters")]
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
        await ddb.PutItemAsync(table, Item(("pk", S("a")), ("colour", S("red"))));
        await ddb.PutItemAsync(table, Item(("pk", S("b")), ("colour", S("blue"))));
        await ddb.PutItemAsync(table, Item(("pk", S("c")), ("colour", S("green"))));
        var res = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConditionalOperator = ConditionalOperator.OR,
            ScanFilter = new()
            {
                ["colour"] = new Condition { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [S("red")] },
                ["pk"] = new Condition { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [S("b")] },
            },
        });
        var got = res.Items.Select(i => i["pk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(got.SequenceEqual(["a", "b"]), $"OR must union the two filters, got {Py.Repr(got)}");
    }
}

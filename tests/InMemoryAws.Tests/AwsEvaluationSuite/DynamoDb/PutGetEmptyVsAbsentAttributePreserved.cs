// Translated from behavioral/dynamodb/check_put_get_empty_vs_absent_attribute_preserved.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-get-empty-vs-absent-attribute-preserved</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutGetEmptyVsAbsentAttributePreserved
{
    private const string CheckId = "put-get-empty-vs-absent-attribute-preserved";

    /// <summary>
    /// An empty String/Binary attribute is stored and returned distinctly from an
    /// attribute that was never written (absent), not coerced or dropped.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-get-empty-vs-absent-attribute-preserved")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx);
        var pk = FidelityContext.UniqueName("item-");
        await ddb.PutItemAsync(table, Item(
            ("pk", S(pk)),
            ("emptyStr", S("")),
            ("emptyBin", B([])),
            ("present", S("x"))));
            // "absentAttr" intentionally never written
        var resp = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true });
        var item = resp.Item;
        Ensure(item is not null, "item not found after PutItem");
        Ensure(item.ContainsKey("emptyStr"), "empty string attribute was dropped");
        Ensure(item["emptyStr"].S == "", "empty string attribute was coerced");
        Ensure(item.ContainsKey("emptyBin"), "empty binary attribute was dropped");
        Ensure(item["emptyBin"].B is not null && Bytes(item["emptyBin"].B).Length == 0, "empty binary attribute was coerced");
        Ensure(
            item.ContainsKey("present") && item["present"].S == "x",
            $"present attribute mismatch: {Py.Repr(item.GetValueOrDefault("present"))}");
        Ensure(!item.ContainsKey("absentAttr"), "absent attribute materialized on read-back");
    }

    private static async Task<string> CreateTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        return name;
    }
}

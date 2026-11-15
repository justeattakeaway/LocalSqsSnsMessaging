// Translated from behavioral/dynamodb/check_cli_query_nested_extraction.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.cli-query-nested-extraction</summary>
[Category("AwsEvaluationSuite")]
public sealed class CliQueryNestedExtraction
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    private static Dictionary<string, string> XsvcResourceTags() => new(StringComparer.Ordinal)
    {
        [ManagedTagKey] = "true",
        ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-'),
    };

    private static List<Tag> XsvcTagsKv() => [.. XsvcResourceTags().Select(kv => new Tag { Key = kv.Key, Value = kv.Value })];

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>
    /// Create a DynamoDB table (PAY_PER_REQUEST; waits table_exists; registers delete_table).
    /// Returns {name,arn}.
    /// </summary>
    private static async Task<(string Name, string Arn)> CreateTable(FidelityContext ctx)
    {
        var name = XName("tbl");
        var ddb = ctx.Dynamo;
        var resp = await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = XsvcTagsKv(),
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 30);
        return (name, resp.TableDescription.TableArn);
    }

    /// <summary>--query extracts nested fields from complex responses.</summary>
    [Test]
    [DisplayName("dynamodb.cli-query-nested-extraction")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var table = await CreateTable(ctx);
        // `aws dynamodb put-item --table-name <t> --item '{"pk": {"S": "nested"}, "info": {"M": {"x": {"N": "7"}}}}'`
        // is this PutItem.
        await ctx.Dynamo.PutItemAsync(table.Name, Item(("pk", S("nested")), ("info", M(("x", N("7"))))));
        // `aws dynamodb get-item --table-name <t> --key '{"pk": {"S": "nested"}}' --consistent-read
        // --query "Item.info.M.x.N" --output text` is this GetItem; the JMESPath extraction prints the
        // nested number's text, or "None" when the path is absent.
        var resp = await ctx.Dynamo.GetItemAsync(new GetItemRequest { TableName = table.Name, Key = Item(("pk", S("nested"))), ConsistentRead = true });
        var stdout = resp.Item?.GetValueOrDefault("info")?.M?.GetValueOrDefault("x")?.N ?? "None";
        Ensure(stdout.Trim() == "7", $"Expected '7', got {Py.Repr(stdout.Trim())}");
    }
}

// Translated from behavioral/dynamodb/check_cli_create_sdk_read_dynamodb.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.cli-create-sdk-read-dynamodb</summary>
[Category("AwsEvaluationSuite")]
public sealed class CliCreateSdkReadDynamodb
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

    /// <summary>Create table item via CLI, read via SDK.</summary>
    [Test]
    [DisplayName("dynamodb.cli-create-sdk-read-dynamodb")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var table = await CreateTable(ctx);
        // `aws dynamodb put-item --table-name <t> --item '{"pk": {"S": "cli-wrote"}, "val": {"S": "interop-test"}}'`
        // is this PutItem; a CLI error (non-zero exit) raises, as the SDK call does.
        await ctx.Dynamo.PutItemAsync(table.Name, Item(("pk", S("cli-wrote")), ("val", S("interop-test"))));

        var ddb = ctx.Dynamo;
        var resp = await ddb.GetItemAsync(new GetItemRequest { TableName = table.Name, Key = Item(("pk", S("cli-wrote"))), ConsistentRead = true });
        var item = resp.Item;
        Ensure(item is not null, $"item written via CLI put-item not visible via SDK get_item: {Py.Repr(resp.Item)}");
        Ensure(item["pk"].S == "cli-wrote", $"expected pk=S:cli-wrote, got {Py.Repr(item.GetValueOrDefault("pk"))}");
        Ensure(item["val"].S == "interop-test", $"expected val=S:interop-test, got {Py.Repr(item.GetValueOrDefault("val"))}");
    }
}

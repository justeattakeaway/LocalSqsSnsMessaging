// Translated from behavioral/dynamodb/check_cli_dynamodb_put_get_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.cli-dynamodb-put-get-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class CliDynamodbPutGetItem
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

    /// <summary>DynamoDB PutItem + GetItem via CLI with typed attribute values.</summary>
    [Test]
    [DisplayName("dynamodb.cli-dynamodb-put-get-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var table = await CreateTable(ctx);
        // `aws dynamodb put-item --table-name <t> --item '{"pk": {"S": "cli-key"}, "data": {"N": "42"}}'`
        // and `aws dynamodb get-item --table-name <t> --key '{"pk": {"S": "cli-key"}}'` are this PutItem
        // and GetItem; the CLI's JSON output is the GetItem response.
        await ctx.Dynamo.PutItemAsync(table.Name, Item(("pk", S("cli-key")), ("data", N("42"))));
        var result = await ctx.Dynamo.GetItemAsync(new GetItemRequest { TableName = table.Name, Key = Item(("pk", S("cli-key"))) });
        var item = result.Item ?? [];
        var pkValue = item.GetValueOrDefault("pk")?.S;
        Ensure(pkValue == "cli-key", $"Expected pk.S='cli-key', got {Py.Repr(pkValue)} (item={Py.Repr(item)})");
        var dataValue = item.GetValueOrDefault("data")?.N;
        Ensure(dataValue == "42", $"Expected data.N='42', got {Py.Repr(dataValue)} (item={Py.Repr(item)})");
    }
}

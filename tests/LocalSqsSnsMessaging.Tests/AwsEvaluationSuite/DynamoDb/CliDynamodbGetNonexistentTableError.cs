// Translated from behavioral/dynamodb/check_cli_dynamodb_get_nonexistent_table_error.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.cli-dynamodb-get-nonexistent-table-error</summary>
[Category("AwsEvaluationSuite")]
public sealed class CliDynamodbGetNonexistentTableError
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

    /// <summary>
    /// GetItem on non-existent table via CLI must fail; the identical call against a real table
    /// (positive control) must succeed, so the failure is attributable to the missing table and not to
    /// broken CLI wiring or credentials.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.cli-dynamodb-get-nonexistent-table-error")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var table = await CreateTable(ctx);
        // `aws dynamodb get-item --table-name <t> --key '{"pk":{"S":"x"}}'` is this GetItem; a CLI
        // error is a non-zero exit with "An error occurred (CODE) when calling ..." on stderr, and
        // the code is AwsError.Code.
        var control = await FidelityContext.CaptureError(() => ctx.Dynamo.GetItemAsync(new GetItemRequest { TableName = table.Name, Key = Item(("pk", S("x"))) }));
        Ensure(control is null, $"Positive control failed: {control}");

        var proc = await FidelityContext.CaptureError(() => ctx.Dynamo.GetItemAsync(new GetItemRequest { TableName = "does-not-exist-sim2real", Key = Item(("pk", S("x"))) }));
        Ensure(proc is not null, "Expected failure for non-existent table");
        Ensure(proc.Code == "ResourceNotFoundException", $"Expected ResourceNotFoundException in stderr, got: {proc}");
    }
}

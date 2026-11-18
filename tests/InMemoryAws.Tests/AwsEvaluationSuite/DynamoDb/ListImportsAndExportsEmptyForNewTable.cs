// Translated from behavioral/dynamodb/check_list_imports_and_exports_empty_for_new_table.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.list-imports-and-exports-empty-for-new-table</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListImportsAndExportsEmptyForNewTable
{
    /// <summary>
    /// Poll DescribeTable until ACTIVE. CreateTable is eventually consistent, so a
    /// transient ResourceNotFoundException counts as "not yet", never a failure.
    /// </summary>
    private static async Task<string> WaitForTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 90.0, double intervalSeconds = 1.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            try
            {
                status = (await ddb.DescribeTableAsync(tableName)).Table.TableStatus.Value;
            }
            catch (ResourceNotFoundException)
            {
                status = "PENDING";
            }

            if (status == "ACTIVE")
            {
                return status;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never went ACTIVE in {timeoutSeconds}s (last={status ?? "None"})");
    }

    /// <summary>Create a pk (S) + sk (S) composite-key table, register teardown, wait ACTIVE.</summary>
    private static async Task<string> CreateSimpleTable(IAmazonDynamoDB ddb, FidelityContext ctx)
    {
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-tbl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await WaitForTableActive(ddb, tableName);
        return tableName;
    }

    /// <summary>A freshly created table has no import or export jobs associated with it.</summary>
    [Test]
    [DisplayName("dynamodb.list-imports-and-exports-empty-for-new-table")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = await CreateSimpleTable(ddb, ctx);
        var tableArn = (await ddb.DescribeTableAsync(tableName)).Table.TableArn;

        var imports = await ddb.ListImportsAsync(new ListImportsRequest { TableArn = tableArn });
        var importList = imports.ImportSummaryList ?? [];
        Ensure(importList.Count == 0, $"expected no import jobs for a new table, got {Py.Repr(importList)}");

        var exports = await ddb.ListExportsAsync(new ListExportsRequest { TableArn = tableArn });
        var exportList = exports.ExportSummaries ?? [];
        Ensure(exportList.Count == 0, $"expected no export jobs for a new table, got {Py.Repr(exportList)}");
    }
}

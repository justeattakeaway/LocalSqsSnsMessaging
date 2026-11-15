// Translated from behavioral/dynamodb/check_delete_table_with_active_gsi_reports_deleting.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-table-with-active-gsi-reports-deleting</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteTableWithActiveGsiReportsDeleting
{
    /// <summary>
    /// DeleteTable on an ACTIVE table carrying an ACTIVE GSI answers TableStatus
    /// 'DELETING' with no GlobalSecondaryIndexes member, and the table then settles to gone.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-table-with-active-gsi-reports-deleting")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("gsi_pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gsi_pk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));

        await WaitTableAndGsiActive(ddb, table, "gsi1");

        var resp = (await ddb.DeleteTableAsync(table)).TableDescription;
        Ensure(
            resp.TableStatus == TableStatus.DELETING,
            $"DeleteTable on an ACTIVE table with a GSI answered TableStatus {Py.Repr(resp.TableStatus)}, expected 'DELETING'");
        Ensure(
            resp.GlobalSecondaryIndexes is null,
            $"DeleteTable response carried GlobalSecondaryIndexes {Py.Repr(resp.GlobalSecondaryIndexes?.Select(g => g.IndexName))}, expected the field to be omitted");

        await WaitTableDeleted(ddb, table);
        var (code, message) = await Error(() => ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table }));
        Ensure(code == "ResourceNotFoundException", $"DescribeTable on the deleted table raised {Py.Repr(code)}");
        var expected = $"Requested resource not found: Table: {table} not found";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
    }

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static async Task<TableDescription> WaitTableAndGsiActive(IAmazonDynamoDB ddb, string table, string indexName, double timeoutSeconds = 300.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            last = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (last.TableStatus == TableStatus.ACTIVE
                && (last.GlobalSecondaryIndexes ?? []).Any(g => g.IndexName == indexName && g.IndexStatus == IndexStatus.ACTIVE))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"table/index not both ACTIVE within {timeoutSeconds}s. Last: {last?.TableStatus}");
    }

    private static async Task WaitTableDeleted(IAmazonDynamoDB ddb, string table, double timeoutSeconds = 120.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table }));
            if (error is not null)
            {
                if (error.Code == "ResourceNotFoundException")
                {
                    return;
                }

                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"table {table} still describable after {timeoutSeconds}s");
    }

    private static async Task<(string? Code, string Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            return (error.Code, error.Message ?? "");
        }

        throw Fail("the call succeeded; real AWS rejects it");
    }
}

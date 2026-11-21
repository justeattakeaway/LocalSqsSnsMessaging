// Translated from behavioral/dynamodb/check_describe_table_after_delete_table_reads_deleting_until_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.describe-table-after-delete-table-reads-deleting-until-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeTableAfterDeleteTableReadsDeletingUntilNotFound
{
    private const string CheckId = "describe-table-after-delete-table-reads-deleting-until-not-found";

    /// <summary>cap_s, shortened when the runner stops the check sooner, keeping 5 s for the failure.</summary>
    private static double WaitCap(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>A cleanup deleting the table, backing off while a create or tag update holds it; a failure raises.</summary>
    private static Func<Task> DropTable(IAmazonDynamoDB ddb, string name)
    {
        async Task<string> Status()
        {
            try
            {
                return (await ddb.DescribeTableAsync(name)).Table.TableStatus.Value;
            }
            catch (ResourceNotFoundException)
            {
                return "GONE";
            }
        }

        return async () =>
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                // A table just created can still read not-found, so GONE still sends the delete.
                if (await Status() == "DELETING")
                {
                    return;
                }

                try
                {
                    await ddb.DeleteTableAsync(name);
                    return;
                }
                catch (ResourceNotFoundException)
                {
                    return;
                }
                catch (ResourceInUseException) when (attempt != 7)
                {
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(Math.Min(3 * Math.Pow(2, attempt), 60)));
            }
        };
    }

    /// <summary>CreateTable, its delete registered at once, then DescribeTable polled to ACTIVE.</summary>
    private static async Task<TableDescription> NewTable(FidelityContext ctx, IAmazonDynamoDB ddb, CreateTableRequest request)
    {
        await ddb.CreateTableAsync(request);
        ctx.OnCleanup(DropTable(ddb, request.TableName));
        var clock = FidelityTime.StartNew();
        var deadline = WaitCap(ctx, 240.0);
        TableDescription table;
        while (true)
        {
            try
            {
                table = (await ddb.DescribeTableAsync(request.TableName)).Table;
            }
            catch (ResourceNotFoundException) when (clock.Elapsed.TotalSeconds < deadline)
            {
                // DescribeTable can read not-found for a moment after CreateTable.
                table = new TableDescription { TableStatus = TableStatus.CREATING };
            }

            if (table.TableStatus == TableStatus.ACTIVE || clock.Elapsed.TotalSeconds >= deadline)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(table.TableStatus == TableStatus.ACTIVE, $"table {request.TableName} settled at {Py.Repr(table.TableStatus?.Value)}");
        return table;
    }

    /// <summary>
    /// DeleteTable answers DELETING, any DescribeTable that still finds the table reports DELETING,
    /// and the reads end in ResourceNotFoundException with the exact message; a delete seen at once is
    /// correct, so no DELETING read is required.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.describe-table-after-delete-table-reads-deleting-until-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-dtdr-");
        await NewTable(ctx, ddb, new CreateTableRequest
        {
            TableName = name,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
        });

        var answered = (await ddb.DeleteTableAsync(name)).TableDescription.TableStatus?.Value;
        Ensure(answered == "DELETING", $"DeleteTable answered TableStatus={Py.Repr(answered)}");
        var clock = FidelityTime.StartNew();
        var deadline = WaitCap(ctx, 180.0);
        var seen = new List<string?>();
        AwsError err;
        while (true)
        {
            var (response, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(name));
            if (error is not null)
            {
                err = error;
                break;
            }

            var status = response!.Table.TableStatus?.Value;
            seen.Add(status);
            Ensure(status == "DELETING", $"DescribeTable after DeleteTable reported {Py.Repr(status)}; reads so far {Py.Repr(seen)}");
            Ensure(clock.Elapsed.TotalSeconds < deadline, $"the table was still DELETING after {seen.Count} reads");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        var expected = ("ResourceNotFoundException", $"Requested resource not found: Table: {name} not found");
        Ensure((err.Code, err.Message) == expected, $"the deleted table read {Py.Repr((err.Code, err.Message))}");
    }
}

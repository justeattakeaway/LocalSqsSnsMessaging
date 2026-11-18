// Translated from behavioral/dynamodb/check_put_item_on_deleting_table_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-on-deleting-table-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemOnDeletingTableRejected
{
    /// <summary>Return (code, message) of the ClientError <paramref name="fn"/> raises; an accepted call fails the check.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected the call to be refused, but it succeeded");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// After DeleteTable, PutItem on the table is refused within 120 s with ResourceNotFoundException ("Requested
    /// resource not found") and DescribeTable raises the same code with the exact message; AWS may accept writes
    /// while the table is DELETING.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-on-deleting-table-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-delfa-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await ddb.WaitForTableExists(tableName, delaySeconds: 2, maxAttempts: 250);

        // Positive control: PutItem succeeds while the table is ACTIVE.
        await ddb.PutItemAsync(tableName, Item(("pk", S("seed")), ("val", S("before-delete"))));

        await ddb.DeleteTableAsync(tableName);

        string? code = null;
        string? message = null;
        var refused = false;
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < 120.0)
        {
            // A table in DELETING may still accept the write; keep polling until it refuses.
            var error = await FidelityContext.CaptureError(() => ddb.PutItemAsync(tableName, Item(("pk", S("post-delete")), ("val", S("x")))));
            if (error is not null)
            {
                code = error.Code;
                message = error.Message;
                if (code == "ResourceNotFoundException")
                {
                    refused = true;
                    break;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3.0));
        }

        if (!refused)
        {
            throw Fail($"PutItem was not refused within 120s after DeleteTable; last code {Py.Repr(code)}");
        }

        Ensure(code == "ResourceNotFoundException", $"expected ResourceNotFoundException, saw {Py.Repr(code)}");
        Ensure(message == "Requested resource not found", $"unexpected PutItem message {Py.Repr(message)}");

        // Effect absent: the table itself is gone too, confirming the rejection matches real state.
        var (describeCode, describeMessage) = await Error(() => ddb.DescribeTableAsync(tableName));
        Ensure(describeCode == "ResourceNotFoundException", $"expected ResourceNotFoundException, saw {Py.Repr(describeCode)}");
        var expected = $"Requested resource not found: Table: {tableName} not found";
        Ensure(describeMessage == expected, $"unexpected DescribeTable message {Py.Repr(describeMessage)}");
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}

// Translated from behavioral/dynamodb/check_deletion_protection_blocks_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deletion-protection-blocks-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeletionProtectionBlocksDelete
{
    private const string Check = "deletion-protection-blocks-delete";

    /// <summary>Bound on each asynchronous table transition; below the harness ceiling.</summary>
    private const double PollBudgetSeconds = 180.0;

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds = PollBudgetSeconds, double intervalSeconds = 1.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"{description} not met within {timeoutSeconds}s. Last result: {Py.Repr(last)}");
    }

    /// <summary>Grade a ClientError on code, HTTP status class, and a non-empty message.</summary>
    private static void AssertError(AwsError exc, string[] codes, int status = 400)
    {
        var code = exc.Code;
        var msg = exc.Message ?? "";
        var gotStatus = (int)exc.StatusCode;
        Ensure(codes.Contains(code), $"expected error code in {Py.Repr(codes)}, got {Py.Repr(code)}: {Py.Repr(msg)}");
        Ensure(gotStatus / 100 == status / 100, $"expected HTTP {status / 100}xx with {code}, got {gotStatus}");
        Ensure(msg.Trim().Length > 0, $"expected a non-empty message with {code}");
    }

    /// <summary>Call fn() and grade the ClientError it raises; an accepted call is itself a failure.</summary>
    private static async Task<AwsError> ExpectError(Func<Task<object>> fn, string[] codes, int status = 400)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            AssertError(error, codes, status);
            return error;
        }

        throw Fail($"expected {Py.Repr(codes)}, but the call succeeded: {result}");
    }

    private static async Task WaitTableDeleted(IAmazonDynamoDB ddb, string name, double timeoutSeconds = PollBudgetSeconds)
    {
        async Task<string> Probe()
        {
            var (resp, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(new DescribeTableRequest { TableName = name }));
            if (error is not null)
            {
                if (error.Code == "ResourceNotFoundException")
                {
                    return "GONE";
                }

                throw error.Exception;
            }

            return resp!.Table.TableStatus.Value;
        }

        await PollUntil(Probe, s => s == "GONE", timeoutSeconds: timeoutSeconds, intervalSeconds: 1.0, description: $"table {name} deleted");
    }

    private static async Task<List<string>> ListAllTables(IAmazonDynamoDB ddb)
    {
        var names = new List<string>();
        string? start = null;
        do
        {
            var page = await ddb.ListTablesAsync(new ListTablesRequest { ExclusiveStartTableName = start });
            names.AddRange(page.TableNames ?? []);
            start = page.LastEvaluatedTableName;
        }
        while (start is not null);
        return names;
    }

    /// <summary>
    /// A deletion-protected table rejects DeleteTable with ValidationException and keeps its
    /// data; disabling the flag via UpdateTable then lets DeleteTable succeed.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deletion-protection-blocks-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            DeletionProtectionEnabled = true,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });

        // Cleanup must clear deletion protection before it can delete the table.
        async Task Cleanup()
        {
            var error = await FidelityContext.CaptureError(() => ddb.UpdateTableAsync(new UpdateTableRequest { TableName = t, DeletionProtectionEnabled = false }));
            if (error is not null)
            {
                if (error.Code == "ResourceNotFoundException")
                {
                    return;
                }

                throw error.Exception;
            }

            error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(t));
            if (error is not null && error.Code != "ResourceNotFoundException")
            {
                throw error.Exception;
            }
        }

        ctx.OnCleanup(Cleanup);
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 90);

        var described = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = t })).Table;
        Ensure(described.DeletionProtectionEnabled == true, Py.Repr(described.DeletionProtectionEnabled));
        var liveNames = await ListAllTables(ddb);
        Ensure(liveNames.Contains(t), $"live table {t} not listed by ListTables");

        var item = Item(("pk", S("kept")), ("v", S(FidelityContext.UniqueName("v-"))));
        await ddb.PutItemAsync(t, item);

        var exc = await ExpectError(async () => await ddb.DeleteTableAsync(t), ["ValidationException"]);
        var message = exc.Message;
        Ensure(
            message == "Resource cannot be deleted as it is currently protected against deletion. Disable deletion protection first.",
            $"the protected DeleteTable: got the message {Py.Repr(message)}");

        // Table and its data must still exist, untouched, after the rejected delete attempt.
        var stillActive = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = t })).Table;
        Ensure(stillActive.TableStatus == TableStatus.ACTIVE, Py.Repr(stillActive.TableStatus));
        var kept = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("kept"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(kept, item), $"the item changed across the rejected DeleteTable: expected {Py.Repr(item)}, got {Py.Repr(kept)}");

        await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = t, DeletionProtectionEnabled = false });
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = t })).Table,
            tbl => tbl.DeletionProtectionEnabled == false,
            intervalSeconds: 2.0,
            description: "DeletionProtectionEnabled cleared");

        await ddb.DeleteTableAsync(t);
        await WaitTableDeleted(ddb, t);
        exc = await ExpectError(async () => await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = t }), ["ResourceNotFoundException"]);
        message = exc.Message;
        Ensure(message == $"Requested resource not found: Table: {t} not found", $"DescribeTable on the deleted table: got the message {Py.Repr(message)}");
        var names = await ListAllTables(ddb);
        Ensure(!names.Contains(t), $"deleted table {t} still listed by ListTables");
        exc = await ExpectError(async () => await ddb.PutItemAsync(t, Item(("pk", S("after-delete")))), ["ResourceNotFoundException"]);
        message = exc.Message;
        Ensure(message == "Requested resource not found", $"PutItem on the deleted table: got the message {Py.Repr(message)}");
    }
}

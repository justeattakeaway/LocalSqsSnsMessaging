// Translated from behavioral/dynamodb/check_deletion_protection_turned_on_by_update_table_refuses_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deletion-protection-turned-on-by-update-table-refuses-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeletionProtectionTurnedOnByUpdateTableRefusesDelete
{
    private const string Check = "deletion-protection-turned-on-by-update-table-refuses-delete";
    private static readonly int[] Seqs = [3, 1, 2];

/// <summary>Bound on each asynchronous table transition; below the harness ceiling.</summary>
    private const double PollBudgetSeconds = 40.0;

    private const string ProtectedMessage =
        "Resource cannot be deleted as it is currently protected against deletion. " +
        "Disable deletion protection first.";

    /// <summary>Bound on each wait; a target that needs longer fails with the last answer it gave.</summary>
    private const double WaitSeconds = 40.0;

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

    /// <summary>DescribeTable once the table is ACTIVE with the wanted deletion-protection flag.</summary>
    private static Task<TableDescription> Settled(IAmazonDynamoDB ddb, string t, bool @protected) =>
        PollUntil(
            async () => (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = t })).Table,
            tbl => tbl.TableStatus == TableStatus.ACTIVE && tbl.DeletionProtectionEnabled == @protected,
            intervalSeconds: 2.0,
            description: $"table ACTIVE with DeletionProtectionEnabled={(@protected ? "True" : "False")}");

    /// <summary>Poll DescribeTable until the table is ACTIVE; on expiry fail with the last status read.</summary>
    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string table, double timeoutSeconds = WaitSeconds)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var status = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw Fail($"table {table} not ACTIVE within {timeoutSeconds:F0}s; last TableStatus {Py.Repr(status)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>
    /// A composite-key table created unprotected deletes only after its flag is cleared again:
    /// UpdateTable turning protection on makes DeleteTable fail with ValidationException and keeps every
    /// item, and turning it off lets the same DeleteTable succeed.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deletion-protection-turned-on-by-update-table-refuses-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("owner", KeyType.HASH), new KeySchemaElement("seq", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("owner", ScalarAttributeType.S), new AttributeDefinition("seq", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });

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
        await WaitTableActive(ddb, t);
        var flag = (await Settled(ddb, t, false)).DeletionProtectionEnabled;
        Ensure(flag == false, $"a table created without the flag reports DeletionProtectionEnabled={Py.Repr(flag)}");

        var items = Seqs
            .Select(n => Item(("owner", S("u-1")), ("seq", N(n.ToString(CultureInfo.InvariantCulture))), ("v", S($"v{n}"))))
            .ToList();
        foreach (var item in items)
        {
            await ddb.PutItemAsync(t, item);
        }

        await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = t, DeletionProtectionEnabled = true });
        flag = (await Settled(ddb, t, true)).DeletionProtectionEnabled;
        Ensure(flag == true, $"after UpdateTable turned it on, DeletionProtectionEnabled={Py.Repr(flag)}");

        foreach (var attempt in new[] { "first", "repeated" })
        {
            var exc = await ExpectError(async () => await ddb.DeleteTableAsync(t), ["ValidationException"]);
            var message = exc.Message;
            Ensure(message == ProtectedMessage, $"the {attempt} protected DeleteTable: got the message {Py.Repr(message)}");
        }

        var still = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = t })).Table;
        Ensure(
            still.TableStatus == TableStatus.ACTIVE && still.DeletionProtectionEnabled == true,
            $"({Py.Repr(still.TableStatus)}, {Py.Repr(still.DeletionProtectionEnabled)})");
        var kept = (await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "#o = :o",
            ExpressionAttributeNames = Names(("#o", "owner")),
            ExpressionAttributeValues = Item((":o", S("u-1"))),
            ConsistentRead = true,
        })).Items;
        var want = items.OrderBy(i => int.Parse(i["seq"].N, CultureInfo.InvariantCulture)).ToList();
        Ensure(Py.Equal(kept, want), $"the items changed across the rejected DeleteTable: expected {Py.Repr(want)}, got {Py.Repr(kept)}");

        await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = t, DeletionProtectionEnabled = false });
        await Settled(ddb, t, false);
        await ddb.DeleteTableAsync(t);
        await WaitTableDeleted(ddb, t);
        var gone = await ExpectError(async () => await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = t }), ["ResourceNotFoundException"]);
        Ensure(gone.Message == $"Requested resource not found: Table: {t} not found", $"DescribeTable on the deleted table: got the message {Py.Repr(gone.Message)}");
        gone = await ExpectError(
            async () => await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = t, DeletionProtectionEnabled = true }),
            ["ResourceNotFoundException"]);
        Ensure(gone.Message == $"Requested resource not found: Table: {t} not found", $"UpdateTable on the deleted table: got the message {Py.Repr(gone.Message)}");
    }
}

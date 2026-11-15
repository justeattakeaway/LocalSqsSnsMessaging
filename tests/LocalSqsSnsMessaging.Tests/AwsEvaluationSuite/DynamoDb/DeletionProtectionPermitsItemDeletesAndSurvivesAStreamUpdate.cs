// Translated from behavioral/dynamodb/check_deletion_protection_permits_item_deletes_and_survives_a_stream_update.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.deletion-protection-permits-item-deletes-and-survives-a-stream-update</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeletionProtectionPermitsItemDeletesAndSurvivesAStreamUpdate
{
    private const string Check = "deletion-protection-permits-item-deletes-and-survives-a-stream-update";

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
    /// Deletion protection guards the table, not its items: DeleteItem and a BatchWriteItem delete
    /// still remove items, an UpdateTable enabling a stream keeps the flag set and DeleteTable refused,
    /// and only clearing the flag lets the table go.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.deletion-protection-permits-item-deletes-and-survives-a-stream-update")]
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

            await Settled(ddb, t, false);
            error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(t));
            if (error is not null && error.Code != "ResourceNotFoundException")
            {
                throw error.Exception;
            }
        }

        ctx.OnCleanup(Cleanup);
        await WaitTableActive(ddb, t);
        var flag = (await Settled(ddb, t, true)).DeletionProtectionEnabled;
        Ensure(flag == true, $"a table created protected reports DeletionProtectionEnabled={Py.Repr(flag)}");

        var keys = Enumerable.Range(0, 4).Select(i => $"row-{i}").ToList();
        foreach (var k in keys)
        {
            await ddb.PutItemAsync(t, Item(("pk", S(k)), ("v", S(k.ToUpperInvariant()))));
        }

        var old = (await ddb.DeleteItemAsync(new DeleteItemRequest { TableName = t, Key = Item(("pk", S(keys[0]))), ReturnValues = ReturnValue.ALL_OLD })).Attributes;
        Ensure(Py.Equal(old, Item(("pk", S(keys[0])), ("v", S("ROW-0")))), $"DeleteItem ALL_OLD returned {Py.Repr(old)}");
        var unprocessed = (await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal)
            {
                [t] = [.. keys[1..3].Select(k => new WriteRequest { DeleteRequest = new DeleteRequest { Key = Item(("pk", S(k))) } })],
            },
        })).UnprocessedItems;
        Ensure(unprocessed is null || unprocessed.Count == 0, $"BatchWriteItem left deletes unprocessed: {Py.Repr(unprocessed)}");
        var left = (await ddb.ScanAsync(new ScanRequest { TableName = t, ConsistentRead = true })).Items.Select(i => i["pk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(left.SequenceEqual([keys[3]]), $"expected only {Py.Repr(keys[3])} left after the item deletes, got {Py.Repr(left)}");

        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = t,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.KEYS_ONLY },
        });
        var tbl = await PollUntil(
            async () => (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = t })).Table,
            d => d.TableStatus == TableStatus.ACTIVE && d.StreamSpecification?.StreamEnabled == true,
            intervalSeconds: 2.0,
            description: "stream enabled and table ACTIVE");
        Ensure(tbl.DeletionProtectionEnabled == true, $"enabling a stream cleared deletion protection: {Py.Repr(tbl.DeletionProtectionEnabled)}");
        Ensure(
            tbl.StreamSpecification.StreamEnabled == true && tbl.StreamSpecification.StreamViewType == StreamViewType.KEYS_ONLY,
            $"{{'StreamEnabled': {Py.Repr(tbl.StreamSpecification.StreamEnabled)}, 'StreamViewType': {Py.Repr(tbl.StreamSpecification.StreamViewType)}}}");
        var streamArn = tbl.LatestStreamArn;
        Ensure(streamArn.StartsWith(tbl.TableArn + "/stream/", StringComparison.Ordinal), $"unexpected stream ARN shape for {t}");

        var exc = await ExpectError(async () => await ddb.DeleteTableAsync(t), ["ValidationException"]);
        var message = exc.Message;
        Ensure(message == ProtectedMessage, $"the protected DeleteTable: got the message {Py.Repr(message)}");
        var kept = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S(keys[3]))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(kept, Item(("pk", S(keys[3])), ("v", S("ROW-3")))), $"the remaining item changed: {Py.Repr(kept)}");

        await ddb.UpdateTableAsync(new UpdateTableRequest { TableName = t, DeletionProtectionEnabled = false });
        await Settled(ddb, t, false);
        await ddb.DeleteTableAsync(t);
        await WaitTableDeleted(ddb, t);
        exc = await ExpectError(async () => await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = t }), ["ResourceNotFoundException"]);
        message = exc.Message;
        Ensure(message == $"Requested resource not found: Table: {t} not found", $"DescribeTable on the deleted table: got the message {Py.Repr(message)}");
        exc = await ExpectError(
            async () => await ddb.DeleteItemAsync(new DeleteItemRequest { TableName = t, Key = Item(("pk", S(keys[3]))) }),
            ["ResourceNotFoundException"]);
        message = exc.Message;
        Ensure(message == "Requested resource not found", $"DeleteItem on the deleted table: got the message {Py.Repr(message)}");
    }
}

// Translated from behavioral/dynamodb/check_key_size_limits_on_batch_get_delete_update_and_binary_keys.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.key-size-limits-on-batch-get-delete-update-and-binary-keys</summary>
[Category("AwsEvaluationSuite")]
public sealed class KeySizeLimitsOnBatchGetDeleteUpdateAndBinaryKeys
{
    private const int HashMax = 2048;
    private const int RangeMax = 1024;
    private const string HashMsg = "One or more parameter values were invalid: Size of hashkey has exceeded the maximum size limit of2048 bytes";
    private const string RangeMsg = "One or more parameter values were invalid: Aggregated size of all range keys has exceeded the size limit of 1024 bytes";
    private const string NotFoundMsg = "Requested resource not found";

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }

    private static Dictionary<string, AttributeValue> With(Dictionary<string, AttributeValue> item, params (string Name, AttributeValue Value)[] extra)
    {
        var copy = new Dictionary<string, AttributeValue>(item, StringComparer.Ordinal);
        foreach (var (name, value) in extra)
        {
            copy[name] = value;
        }

        return copy;
    }

    /// <summary>
    /// Over-size hash/range keys are refused by BatchGetItem, DeleteItem, UpdateItem and (decoded
    /// B length) GetItem; at-limit keys work; a missing table answers not-found first.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.key-size-limits-on-batch-get-delete-update-and-binary-keys")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var hrTable = FidelityContext.UniqueName("sim2real-behav-ddb-keysize-hr-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = hrTable,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(hrTable));
        var bTable = FidelityContext.UniqueName("sim2real-behav-ddb-keysize-b-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = bTable,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.B)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(bTable));
        await WaitActive(ddb, ctx, hrTable);
        await WaitActive(ddb, ctx, bTable);

        var bigHash = new string('a', HashMax + 1);
        var bigRange = new string('b', RangeMax + 1);
        Dictionary<string, AttributeValue> LimitKey() => Item(("pk", S(new string('a', HashMax))), ("sk", S(new string('b', RangeMax))));
        var seeded = With(LimitKey(), ("v", S("at-limit")));
        await ddb.PutItemAsync(hrTable, seeded);

        // Positive control: BatchGetItem with at-limit keys returns the item.
        var resp = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal)
            {
                [hrTable] = new KeysAndAttributes { Keys = [LimitKey()], ConsistentRead = true },
            },
        });
        var gotList = resp.Responses.GetValueOrDefault(hrTable);
        Ensure(Py.Equal(gotList, new[] { seeded }), $"BatchGetItem with at-limit keys returned {Py.Repr(gotList)}");

        var arms = new (string Label, string Msg, Func<Task<object>> Call)[]
        {
            ("BatchGetItem over-size hash key", HashMsg, async () => await ddb.BatchGetItemAsync(new BatchGetItemRequest
            {
                RequestItems = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal)
                {
                    [hrTable] = new KeysAndAttributes { Keys = [Item(("pk", S(bigHash)), ("sk", S("s")))] },
                },
            })),
            ("BatchGetItem over-size range key", RangeMsg, async () => await ddb.BatchGetItemAsync(new BatchGetItemRequest
            {
                RequestItems = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal)
                {
                    [hrTable] = new KeysAndAttributes { Keys = [Item(("pk", S("h")), ("sk", S(bigRange)))] },
                },
            })),
            ("DeleteItem over-size hash key", HashMsg, async () => await ddb.DeleteItemAsync(hrTable, Item(("pk", S(bigHash)), ("sk", S("s"))))),
            ("UpdateItem over-size range key", RangeMsg, async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = hrTable,
                Key = Item(("pk", S("h")), ("sk", S(bigRange))),
                UpdateExpression = "SET x = :v",
                ExpressionAttributeValues = Item((":v", S("1"))),
            })),
            ("GetItem B hash key of 2049 decoded bytes", HashMsg, async () => await ddb.GetItemAsync(bTable, Item(("pk", B(Encoding.ASCII.GetBytes(new string('a', HashMax + 1))))))),
        };
        foreach (var (label, msg, call) in arms)
        {
            var (code, gotMsg) = await Error(call);
            Ensure((code, gotMsg) == ("ValidationException", msg), $"{label} gave {Py.Repr(code)}: {Py.Repr(gotMsg)}");
        }

        var count = (await ddb.ScanAsync(new ScanRequest { TableName = hrTable, Select = Select.COUNT, ConsistentRead = true })).Count;
        Ensure(count == 1, $"the refused key-size requests left {Py.Repr(count)} items, expected 1");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = hrTable,
            Key = LimitKey(),
            UpdateExpression = "SET x = :v",
            ExpressionAttributeValues = Item((":v", S("1"))),
        });
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = hrTable, Key = LimitKey(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got, With(seeded, ("x", S("1")))), $"UpdateItem with at-limit keys left {Py.Repr(got)}");
        await ddb.DeleteItemAsync(hrTable, LimitKey());
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = hrTable, Key = LimitKey(), ConsistentRead = true })).Item;
        Ensure(got is null, $"DeleteItem with at-limit keys left {Py.Repr(got)}");

        // A B key exactly at the limit (decoded) is accepted on write and read.
        Dictionary<string, AttributeValue> BLimit() => Item(("pk", B(Encoding.ASCII.GetBytes(new string('a', HashMax)))));
        var bResp = await ddb.GetItemAsync(new GetItemRequest { TableName = bTable, Key = BLimit(), ConsistentRead = true });
        Ensure(bResp.Item is null, $"GetItem with an at-limit B key on an empty table returned {Py.Repr(bResp.Item)}");
        await ddb.PutItemAsync(bTable, With(BLimit(), ("v", S("b"))));
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = bTable, Key = BLimit(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got, With(BLimit(), ("v", S("b")))), $"the at-limit B-keyed item read back as {Py.Repr(got)}");

        var missing = FidelityContext.UniqueName("sim2real-behav-ddb-keysize-missing-");
        var (missingCode, missingMsg) = await Error(async () => await ddb.GetItemAsync(missing, Item(("pk", S(bigHash)))));
        Ensure(
            (missingCode, missingMsg) == ("ResourceNotFoundException", NotFoundMsg),
            $"GetItem with an over-size key on a missing table gave {Py.Repr(missingCode)}: {Py.Repr(missingMsg)}");
    }
}

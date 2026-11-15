// Translated from behavioral/dynamodb/check_execute_transaction_replay_bills_exactly_wcu_then_rcu.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.execute-transaction-replay-bills-exactly-wcu-then-rcu</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExecuteTransactionReplayBillsExactlyWcuThenRcu
{
    private const string CheckId = "execute-transaction-replay-bills-exactly-wcu-then-rcu";

    /// <summary>
    /// ReturnConsumedCapacity=INDEXES on a 1-item ExecuteTransaction write reports an exact
    /// WriteCapacityUnits with no ReadCapacityUnits key; the same-token replay reports the
    /// mirrored ReadCapacityUnits with no WriteCapacityUnits key, and the item is applied once.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.execute-transaction-replay-bills-exactly-wcu-then-rcu")]
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var token = Guid.NewGuid().ToString("N");
        List<ParameterizedStatement> Statements() =>
        [
            new ParameterizedStatement
            {
                Statement = $"INSERT INTO \"{t}\" VALUE {{'pk': ?, 'v': ?}}",
                Parameters = [S("et-bill-1"), N("1")],
            },
        ];

        var first = await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = Statements(),
            ClientRequestToken = token,
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var cc1 = first.ConsumedCapacity[0];
        Ensure(
            cc1.WriteCapacityUnits == 2,
            $"the first use of a fresh token on a 1-item write transaction must bill 2 WCU, got {Repr(cc1)}");
        Ensure(
            cc1.ReadCapacityUnits is null,
            $"a fresh-token write transaction must carry no ReadCapacityUnits key, got {Repr(cc1)}");
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("et-bill-1"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(item["v"], N("1")), Py.Repr(item));

        var replay = await ddb.ExecuteTransactionAsync(new ExecuteTransactionRequest
        {
            TransactStatements = Statements(),
            ClientRequestToken = token,
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
        });
        var cc2 = replay.ConsumedCapacity[0];
        Ensure(
            cc2.ReadCapacityUnits == 2,
            $"a deduplicated replay of a 1-item write transaction must bill 2 RCU, got {Repr(cc2)}");
        Ensure(
            cc2.WriteCapacityUnits is null,
            $"a deduplicated replay must carry no WriteCapacityUnits key, got {Repr(cc2)}");
        var replayedItem = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("et-bill-1"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(replayedItem["v"], N("1")), "deduplicated replay must not alter the item");

        var scanned = await ddb.ScanAsync(new ScanRequest { TableName = t, ConsistentRead = true });
        Ensure(scanned.Count == 1, $"the item must be applied exactly once, scan reported {scanned.Count}");
    }

    /// <summary>The ConsumedCapacity entry as boto3 hands it back: only the members present.</summary>
    private static string Repr(ConsumedCapacity cc)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (cc.TableName is not null)
        {
            dict["TableName"] = cc.TableName;
        }

        if (cc.CapacityUnits is { } units)
        {
            dict["CapacityUnits"] = units;
        }

        if (cc.ReadCapacityUnits is { } read)
        {
            dict["ReadCapacityUnits"] = read;
        }

        if (cc.WriteCapacityUnits is { } write)
        {
            dict["WriteCapacityUnits"] = write;
        }

        if (cc.Table is not null)
        {
            dict["Table"] = cc.Table.CapacityUnits;
        }

        return Py.Repr(dict);
    }
}

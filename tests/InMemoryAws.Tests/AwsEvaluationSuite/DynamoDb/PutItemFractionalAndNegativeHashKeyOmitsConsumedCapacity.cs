// Translated from behavioral/dynamodb/check_put_item_fractional_and_negative_hash_key_omits_consumed_capacity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-fractional-and-negative-hash-key-omits-consumed-capacity</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemFractionalAndNegativeHashKeyOmitsConsumedCapacity
{
    private const string CheckId = "put-item-fractional-and-negative-hash-key-omits-consumed-capacity";

    /// <summary>A ConsumedCapacity as boto3 hands it to the check (only the members present).</summary>
    private static Dictionary<string, object?>? Cc(ConsumedCapacity? cc)
    {
        if (cc is null)
        {
            return null;
        }

        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (cc.TableName is not null)
        {
            d["TableName"] = cc.TableName;
        }

        if (cc.CapacityUnits is { } c)
        {
            d["CapacityUnits"] = c;
        }

        if (cc.ReadCapacityUnits is { } r)
        {
            d["ReadCapacityUnits"] = r;
        }

        if (cc.WriteCapacityUnits is { } w)
        {
            d["WriteCapacityUnits"] = w;
        }

        if (cc.Table is not null)
        {
            d["Table"] = cc.Table.CapacityUnits;
        }

        if (cc.GlobalSecondaryIndexes is not null)
        {
            d["GlobalSecondaryIndexes"] = cc.GlobalSecondaryIndexes.Keys.ToList();
        }

        if (cc.LocalSecondaryIndexes is not null)
        {
            d["LocalSecondaryIndexes"] = cc.LocalSecondaryIndexes.Keys.ToList();
        }

        return d;
    }

    /// <summary>
    /// A fractional N (3.14) and a negative N (-100) hash key each round-trip through PutItem
    /// and GetItem exactly; ReturnConsumedCapacity=NONE omits ConsumedCapacity on both calls.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-fractional-and-negative-hash-key-omits-consumed-capacity")]
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
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var items = new List<(string Key, Dictionary<string, AttributeValue> Item)>
        {
            ("3.14", Item(("pk", N("3.14")), ("label", S("frac")))),
            ("-100", Item(("pk", N("-100")), ("label", S("neg")))),
        };

        foreach (var (key, item) in items)
        {
            var putResp = await ddb.PutItemAsync(new PutItemRequest { TableName = t, Item = item, ReturnConsumedCapacity = ReturnConsumedCapacity.NONE });
            Ensure(
                putResp.ConsumedCapacity is null,
                $"PutItem of pk={Py.Repr(key)} with ReturnConsumedCapacity=NONE must omit ConsumedCapacity, got {Py.Repr(Cc(putResp.ConsumedCapacity))}");
        }

        foreach (var (key, item) in items)
        {
            var getResp = await ddb.GetItemAsync(new GetItemRequest
            {
                TableName = t,
                Key = Item(("pk", item["pk"])),
                ConsistentRead = true,
                ReturnConsumedCapacity = ReturnConsumedCapacity.NONE,
            });
            Ensure(
                Py.Equal(getResp.Item, item),
                $"GetItem for pk={Py.Repr(key)} must read back the exact stored item, got {Py.Repr(getResp.Item)}");
            Ensure(
                getResp.ConsumedCapacity is null,
                $"GetItem of pk={Py.Repr(key)} with ReturnConsumedCapacity=NONE must omit ConsumedCapacity, got {Py.Repr(Cc(getResp.ConsumedCapacity))}");
        }

        // Positive control: the same two calls with ReturnConsumedCapacity=TOTAL return the table's
        // one-unit charge, so the omission above is the option's effect.
        var controlItem = Item(("pk", N("42")), ("label", S("control")));
        var putTotal = await ddb.PutItemAsync(new PutItemRequest { TableName = t, Item = controlItem, ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL });
        var putCc = Cc(putTotal.ConsumedCapacity);
        Ensure(
            Py.Equal(putCc, new Dictionary<string, object?> { ["TableName"] = t, ["CapacityUnits"] = 1.0 }),
            $"PutItem of a small item with ReturnConsumedCapacity=TOTAL must charge 1.0 unit, got {Py.Repr(putCc)}");
        var getTotal = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = t,
            Key = Item(("pk", controlItem["pk"])),
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var getCc = Cc(getTotal.ConsumedCapacity);
        Ensure(
            Py.Equal(getCc, new Dictionary<string, object?> { ["TableName"] = t, ["CapacityUnits"] = 1.0 }),
            $"a strongly consistent GetItem of a small item with ReturnConsumedCapacity=TOTAL must charge 1.0 unit, got {Py.Repr(getCc)}");
    }
}

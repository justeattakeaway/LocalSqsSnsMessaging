// Translated from behavioral/dynamodb/check_consumed_capacity_halves_for_eventually_consistent_read.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.consumed-capacity-halves-for-eventually-consistent-read</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConsumedCapacityHalvesForEventuallyConsistentRead
{
    private const string CheckId = "consumed-capacity-halves-for-eventually-consistent-read";

    /// <summary>
    /// A strongly consistent read of a sub-4KB item bills exactly 1.0 RCU and the eventually consistent read
    /// of it exactly 0.5, and a read without ReturnConsumedCapacity reports no ConsumedCapacity.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.consumed-capacity-halves-for-eventually-consistent-read")]
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
        await ddb.PutItemAsync(t, Item(("pk", S("k")), ("a", S("small"))));

        var strong = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("k"))),
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        Ensure(strong.Item is { Count: > 0 }, "strongly consistent read must return the item it is billing for");
        var strongCu = strong.ConsumedCapacity?.CapacityUnits;
        Ensure(strongCu == 1.0, $"strongly consistent sub-4KB read must bill 1.0, got {Py.Repr(strongCu)}");

        var eventual = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = t,
            Key = Item(("pk", S("k"))),
            ConsistentRead = false,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var eventualCu = eventual.ConsumedCapacity?.CapacityUnits;
        Ensure(eventualCu == 0.5, $"eventually consistent sub-4KB read must bill 0.5, got {Py.Repr(eventualCu)}");
        Ensure(
            eventualCu * 2 == strongCu,
            $"eventual read must cost exactly half a strong read, got {Py.Repr(eventualCu)} vs {Py.Repr(strongCu)}");

        // Omitting ReturnConsumedCapacity must suppress the field entirely, so a
        // responder cannot pass the assertions above by always populating it.
        var silent = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("k"))), ConsistentRead = true });
        Ensure(
            silent.ConsumedCapacity is null,
            $"ConsumedCapacity must be absent when not requested, got {Py.Repr(Boto(silent.ConsumedCapacity))}");
    }

    /// <summary>The ConsumedCapacity as boto3 hands it to the check: a dict of its present members.</summary>
    private static Dictionary<string, object?>? Boto(ConsumedCapacity? cc)
    {
        if (cc is null)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (cc.TableName is not null)
        {
            result["TableName"] = cc.TableName;
        }

        if (cc.CapacityUnits is not null)
        {
            result["CapacityUnits"] = cc.CapacityUnits;
        }

        return result;
    }
}

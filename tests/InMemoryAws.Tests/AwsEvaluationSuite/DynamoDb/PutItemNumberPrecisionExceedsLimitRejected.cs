// Translated from behavioral/dynamodb/check_put_item_number_precision_exceeds_limit_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-item-number-precision-exceeds-limit-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutItemNumberPrecisionExceedsLimitRejected
{
    /// <summary>The Error of a call that MUST be refused; RAISES if the call succeeds.</summary>
    private static async Task<AwsError> Error(Func<Task<PutItemResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
    }

    /// <summary>
    /// A Number value exceeding DynamoDB's 38-digit precision limit is
    /// rejected by PutItem with a validation error rather than silently
    /// truncated or stored, while a normal-precision number round-trips fine.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-item-number-precision-exceeds-limit-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-numprec-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await ddb.WaitForTableExists(tableName, delaySeconds: 2, maxAttempts: 250);

        // 39 significant digits: exceeds the 38-digit precision DynamoDB supports for Number.
        var oversizedNumber = new string('1', 39);
        var badKey = Item(("pk", S("oversized")));
        var err = await Error(() => ddb.PutItemAsync(tableName, Item(("pk", S("oversized")), ("num", N(oversizedNumber)))));
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {Py.Repr(err.Code)}");
        const string expected = "1 validation error detected: Attempting to store more than 38 significant digits in a Number";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");

        // Effect absent: no item was written for the rejected PutItem.
        var resp = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = badKey, ConsistentRead = true });
        Ensure(resp.Item is null, $"rejected PutItem must not create an item, got {Py.Repr(resp.Item)}");

        // Positive control: a normal-precision number is accepted and round-trips correctly.
        var goodKey = Item(("pk", S("normal")));
        await ddb.PutItemAsync(tableName, Item(("pk", S("normal")), ("num", N("12345"))));
        resp = await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = goodKey, ConsistentRead = true });
        Ensure(resp.Item["num"].N == "12345", $"normal-precision number did not round-trip, got {Py.Repr(resp.Item["num"])}");
    }
}

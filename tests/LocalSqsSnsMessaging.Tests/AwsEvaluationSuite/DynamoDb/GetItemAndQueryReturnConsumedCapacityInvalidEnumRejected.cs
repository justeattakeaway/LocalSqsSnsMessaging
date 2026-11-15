// Translated from behavioral/dynamodb/check_get_item_and_query_return_consumed_capacity_invalid_enum_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.get-item-and-query-return-consumed-capacity-invalid-enum-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetItemAndQueryReturnConsumedCapacityInvalidEnumRejected
{
    private const string CheckId = "get-item-and-query-return-consumed-capacity-invalid-enum-rejected";

    private static readonly (string Code, string Message) Validation = (
        "ValidationException",
        "1 validation error detected: Value 'BOGUS' at 'returnConsumedCapacity' failed to satisfy "
        + "constraint: Member must satisfy enum value set: [INDEXES, TOTAL, NONE]");

    /// <summary>
    /// An out-of-enum ReturnConsumedCapacity is ValidationException on GetItem and Query alike,
    /// and a well-formed call with the same key still reads the item correctly.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.get-item-and-query-return-consumed-capacity-invalid-enum-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-rccenum-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("control")), ("data", S("v"))));

        await ExpectError(
            () => ddb.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("control"))),
                ReturnConsumedCapacity = new ReturnConsumedCapacity("BOGUS"),
            }),
            Validation.Code,
            Validation.Message);
        var controlGet = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("control"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            ConsistentRead = true,
        });
        Ensure(
            Py.Equal(controlGet.Item, Item(("pk", S("control")), ("data", S("v")))),
            $"the rejected GetItem must leave the item untouched, read back {Py.Repr(controlGet.Item)}");
        Ensure(
            controlGet.ConsumedCapacity.CapacityUnits == 1.0,
            $"a well-formed, strongly consistent GetItem of this small item must still bill 1.0 RCU, got {Repr(controlGet.ConsumedCapacity)}");

        await ExpectError(
            () => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :v",
                ExpressionAttributeValues = Item((":v", S("control"))),
                ReturnConsumedCapacity = new ReturnConsumedCapacity("BOGUS"),
            }),
            Validation.Code,
            Validation.Message);
        var controlQuery = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :v",
            ExpressionAttributeValues = Item((":v", S("control"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            ConsistentRead = true,
        });
        Ensure(controlQuery.Count == 1, $"the rejected Query must leave the item queryable, got Count={controlQuery.Count}");
        Ensure(
            Py.Equal(controlQuery.Items[0], Item(("pk", S("control")), ("data", S("v")))),
            $"the rejected Query must leave the item untouched, read back {Py.Repr(controlQuery.Items[0])}");
        Ensure(
            controlQuery.ConsumedCapacity.CapacityUnits == 1.0,
            $"a well-formed, strongly consistent Query of this small item must still bill 1.0 RCU, got {Repr(controlQuery.ConsumedCapacity)}");
    }

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var got = await Error(fn);
        Ensure(got == (code, message), $"expected {Py.Repr((code, message))}, got {Py.Repr(got)}");
    }

    private static string Repr(ConsumedCapacity c) =>
        Py.Repr(new Dictionary<string, object?>(StringComparer.Ordinal) { ["TableName"] = c.TableName, ["CapacityUnits"] = c.CapacityUnits });
}

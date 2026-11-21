// Translated from behavioral/dynamodb/check_scan_return_consumed_capacity_invalid_enum_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-return-consumed-capacity-invalid-enum-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanReturnConsumedCapacityInvalidEnumRejected
{
    private const string Check = "scan-return-consumed-capacity-invalid-enum-rejected";

    private static readonly (string Code, string Message) Validation = (
        "ValidationException",
        "1 validation error detected: Value 'BOGUS' at 'returnConsumedCapacity' failed to satisfy "
        + "constraint: Member must satisfy enum value set: [INDEXES, TOTAL, NONE]");

    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
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

    /// <summary>The members of a ConsumedCapacity that boto3 would hand back, as a dict.</summary>
    private static Dictionary<string, object?> Boto(ConsumedCapacity c)
    {
        var members = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (c.TableName is not null)
        {
            members["TableName"] = c.TableName;
        }

        if (c.CapacityUnits is not null)
        {
            members["CapacityUnits"] = c.CapacityUnits;
        }

        if (c.ReadCapacityUnits is not null)
        {
            members["ReadCapacityUnits"] = c.ReadCapacityUnits;
        }

        if (c.WriteCapacityUnits is not null)
        {
            members["WriteCapacityUnits"] = c.WriteCapacityUnits;
        }

        if (c.Table is not null)
        {
            members["Table"] = c.Table;
        }

        if (c.GlobalSecondaryIndexes is not null)
        {
            members["GlobalSecondaryIndexes"] = c.GlobalSecondaryIndexes;
        }

        if (c.LocalSecondaryIndexes is not null)
        {
            members["LocalSecondaryIndexes"] = c.LocalSecondaryIndexes;
        }

        return members;
    }

    /// <summary>
    /// An out-of-enum ReturnConsumedCapacity on Scan is ValidationException naming the member and
    /// echoing the value, and the table is still fully scannable with a well-formed call.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-return-consumed-capacity-invalid-enum-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-rccenum-scan-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("control")), ("data", S("v"))));

        await ExpectError(
            () => ddb.ScanAsync(new ScanRequest { TableName = table, ReturnConsumedCapacity = new ReturnConsumedCapacity("BOGUS") }),
            Validation.Code,
            Validation.Message);

        var control = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            ConsistentRead = true,
        });
        Ensure(control.Count == 1, $"the rejected Scan must leave the table scannable, got Count={control.Count}");
        Ensure(
            Py.Equal(control.Items, new[] { Item(("pk", S("control")), ("data", S("v"))) }),
            $"the rejected Scan must leave the item untouched, read back {Py.Repr(control.Items)}");
        var consumed = Boto(control.ConsumedCapacity ?? new ConsumedCapacity());
        Ensure(
            new SortedSet<string>(consumed.Keys, StringComparer.Ordinal).SetEquals(["TableName", "CapacityUnits"]),
            $"ReturnConsumedCapacity=TOTAL must report table-level capacity only, got {Py.Repr(consumed)}");
        Ensure(
            control.ConsumedCapacity?.TableName == table,
            $"the reported capacity must name the scanned table, got {Py.Repr(consumed)}");
    }
}

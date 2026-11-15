// Translated from behavioral/dynamodb/check_zero_at_exponent_boundary_rejected_one_step_past_range.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.zero-at-exponent-boundary-rejected-one-step-past-range</summary>
[Category("AwsEvaluationSuite")]
public sealed class ZeroAtExponentBoundaryRejectedOneStepPastRange
{
    private const string Check = "zero-at-exponent-boundary-rejected-one-step-past-range";
    private const string ErrValidation = "ValidationException";

    private const string OverflowMsg =
        "1 validation error detected: Number overflow. Attempting to store a number with "
        + "magnitude larger than supported range";

    private const string UnderflowMsg =
        "1 validation error detected: Number underflow. Attempting to store a number with "
        + "magnitude smaller than supported range";

    private static readonly string[] Accepted = ["0E125", "0E-130"];

    /// <summary>
    /// A zero's exponent is range-checked at the exact boundary: one exponent past the
    /// accepted ceiling or floor is rejected even though the value is still mathematically zero.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.zero-at-exponent-boundary-rejected-one-step-past-range")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        // --- positive control: zero at the accepted ceiling and floor is still accepted and
        // canonicalised to "0" ---
        foreach (var raw in Accepted)
        {
            var key = $"ok-{raw}";
            await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S(key)), ("v", N(raw))) });
            var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(key))), ConsistentRead = true })).Item;
            Ensure(Py.Equal(got["v"], N("0")), $"{Py.Repr(raw)} is mathematically zero and must canonicalise to \"0\", got {Py.Repr(got["v"])}");
            Ensure(decimal.Parse(got["v"].N, NumberStyles.Float, CultureInfo.InvariantCulture) == 0, $"{Py.Repr(raw)} must round-trip as zero, got {Py.Repr(got["v"])}");
        }

        // --- one exponent past the ceiling is ValidationException: overflow, even though the
        // value is zero ---
        var (code, message) = await Error(() => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("over")), ("v", N("0E126"))) }));
        Ensure(code == ErrValidation, $"0E126 must be rejected with {ErrValidation}, got {code}");
        Ensure(message == OverflowMsg, $"expected AWS's exact overflow message, got {Py.Repr(message)}");
        var leaked = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("over"))), ConsistentRead = true });
        Ensure(leaked.Item is null, $"rejected write must not create the item, found {Py.Repr(leaked.Item)}");

        // --- one exponent past the floor is ValidationException: underflow, even though the
        // value is zero ---
        (code, message) = await Error(() => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("under")), ("v", N("0E-131"))) }));
        Ensure(code == ErrValidation, $"0E-131 must be rejected with {ErrValidation}, got {code}");
        Ensure(message == UnderflowMsg, $"expected AWS's exact underflow message, got {Py.Repr(message)}");
        leaked = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("under"))), ConsistentRead = true });
        Ensure(leaked.Item is null, $"rejected write must not create the item, found {Py.Repr(leaked.Item)}");

        // Control still works after every rejection.
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("ctrl")), ("v", N("42"))) });
        var ok = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("ctrl"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(ok["v"], N("42")), $"control write must still succeed, got {Py.Repr(ok)}");
    }

    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }
}

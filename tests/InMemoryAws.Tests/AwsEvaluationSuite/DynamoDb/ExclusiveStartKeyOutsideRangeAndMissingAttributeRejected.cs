// Translated from behavioral/dynamodb/check_exclusive_start_key_outside_range_and_missing_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.exclusive-start-key-outside-range-and-missing-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ExclusiveStartKeyOutsideRangeAndMissingAttributeRejected
{
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// Query rejects an ExclusiveStartKey whose sort key falls outside the
    /// KeyConditionExpression's range, in both scan directions, and Scan rejects one
    /// missing a key attribute -- both ValidationException, and neither call writes.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_Query.html#DDB-Query-request-ExclusiveStartKey
    /// </summary>
    [Test]
    [DisplayName("dynamodb.exclusive-start-key-outside-range-and-missing-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = "true" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 60);

        for (var i = 0; i < 10; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("x")), ("sk", S(i.ToString("D3", CultureInfo.InvariantCulture)))));
        }

        QueryRequest KeyCondition(Dictionary<string, AttributeValue> exclusiveStartKey) => new()
        {
            TableName = table,
            ExclusiveStartKey = exclusiveStartKey,
            KeyConditionExpression = "pk = :p AND sk BETWEEN :lo AND :hi",
            ExpressionAttributeValues = Item((":p", S("x")), (":lo", S("003")), (":hi", S("006"))),
        };

        // Positive control, forward: an ExclusiveStartKey inside the BETWEEN range continues normally.
        var forwardRequest = KeyCondition(Item(("pk", S("x")), ("sk", S("004"))));
        forwardRequest.ConsistentRead = true;
        var forwardOk = await ddb.QueryAsync(forwardRequest);
        var forwardSks = forwardOk.Items.Select(i => i["sk"].S).ToList();
        Ensure(
            forwardSks.SequenceEqual(["005", "006"]),
            $"positive control: expected an in-range ExclusiveStartKey to continue forward with ['005', '006'], got {Py.Repr(forwardSks)}");

        // Negative, forward: an ExclusiveStartKey above the range's upper bound is rejected.
        var (code, message) = await Error(() => ddb.QueryAsync(KeyCondition(Item(("pk", S("x")), ("sk", S("020"))))));
        Ensure(code == ErrValidation, $"forward out-of-range ExclusiveStartKey: expected {ErrValidation}, got {code}");
        Ensure(
            message == "The provided starting key does not match the range key predicate",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        // Positive control, backward: an in-range ExclusiveStartKey continues normally descending.
        var backwardRequest = KeyCondition(Item(("pk", S("x")), ("sk", S("005"))));
        backwardRequest.ConsistentRead = true;
        backwardRequest.ScanIndexForward = false;
        var backwardOk = await ddb.QueryAsync(backwardRequest);
        var backwardSks = backwardOk.Items.Select(i => i["sk"].S).ToList();
        Ensure(
            backwardSks.SequenceEqual(["004", "003"]),
            $"positive control: expected an in-range ExclusiveStartKey to continue backward with ['004', '003'], got {Py.Repr(backwardSks)}");

        // Negative, backward: an ExclusiveStartKey below the range's lower bound is rejected too.
        (code, message) = await Error(() =>
        {
            var request = KeyCondition(Item(("pk", S("x")), ("sk", S("002"))));
            request.ScanIndexForward = false;
            return ddb.QueryAsync(request);
        });
        Ensure(code == ErrValidation, $"backward out-of-range ExclusiveStartKey: expected {ErrValidation}, got {code}");
        Ensure(
            message == "The provided starting key does not match the range key predicate",
            $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        // Positive control: Scan accepts a well-formed, complete ExclusiveStartKey.
        var scanOk = await ddb.ScanAsync(new ScanRequest { TableName = table, ExclusiveStartKey = Item(("pk", S("x")), ("sk", S("003"))) });
        Ensure(scanOk.Items is not null, $"positive control: expected a complete ExclusiveStartKey to be accepted, got {Py.Repr(scanOk.Items)}");

        // Negative: Scan rejects an ExclusiveStartKey missing the table's range key attribute.
        (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest { TableName = table, ExclusiveStartKey = Item(("pk", S("x"))) }));
        Ensure(code == ErrValidation, $"Scan with an ExclusiveStartKey missing sk: expected {ErrValidation}, got {code}");
        Ensure(message == "The provided starting key is invalid", $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");

        // Every rejected call above is a read: confirm none of them wrote, deleted or corrupted an item.
        var final = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        var finalSks = final.Items.Select(i => i["sk"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(
            finalSks.SetEquals(Enumerable.Range(0, 10).Select(i => i.ToString("D3", CultureInfo.InvariantCulture))),
            $"expected the original 10 items unchanged after the rejected calls, got {Py.Repr(finalSks.Order(StringComparer.Ordinal).ToList())}");
    }

    /// <summary><c>(Code, Message)</c> for the <c>ClientError</c> <c>fn</c> raises; an unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }
}

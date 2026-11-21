// Translated from behavioral/dynamodb/check_scan_filter_size_function_name_is_case_sensitive.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-size-function-name-is-case-sensitive</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterSizeFunctionNameIsCaseSensitive
{
    private const string Check = "scan-filter-size-function-name-is-case-sensitive";

    private static readonly (string Code, string Message) TitleCaseRejected =
        ("ValidationException", "Invalid FilterExpression: Invalid function name; function: Size");

    private static readonly (string Code, string Message) UpperCaseRejected =
        ("ValidationException", "Invalid FilterExpression: Invalid function name; function: SIZE");

    /// <summary>(code, message) of the error fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"unexpected message {Py.Repr(gotMessage)}");
    }

    /// <summary>
    /// A Scan FilterExpression rejects size() under any other case as an unknown function name,
    /// naming the exact spelling it was given, while the correctly-cased call still matches.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-size-function-name-is-case-sensitive")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbcase-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(table, Item(("pk", S("a")), ("s", S("abc"))));

        await ExpectError(
            () => ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                FilterExpression = "Size(s) = :n",
                ExpressionAttributeValues = Item((":n", N("3"))),
            }),
            TitleCaseRejected);
        await ExpectError(
            () => ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                FilterExpression = "SIZE(s) = :n",
                ExpressionAttributeValues = Item((":n", N("3"))),
            }),
            UpperCaseRejected);

        // Positive control: the correctly-cased call matches the seeded item.
        var ok = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConsistentRead = true,
            FilterExpression = "size(s) = :n",
            ExpressionAttributeValues = Item((":n", N("3"))),
        });
        var pks = new SortedSet<string>(ok.Items.Select(i => i["pk"].S), StringComparer.Ordinal);
        Ensure(pks.SetEquals(["a"]), $"size(s) = 3 selected {Py.Repr(pks)}, expected ['a']");
    }
}

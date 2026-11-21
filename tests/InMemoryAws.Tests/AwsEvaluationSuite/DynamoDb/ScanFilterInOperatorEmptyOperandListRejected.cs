// Translated from behavioral/dynamodb/check_scan_filter_in_operator_empty_operand_list_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-in-operator-empty-operand-list-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterInOperatorEmptyOperandListRejected
{
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// FilterExpression 'IN ()' with no operands is rejected; the same Binary attribute still
    /// matches a non-empty IN list on a later scan.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-in-operator-empty-operand-list-rejected")]
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
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(t), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(t, Item(("pk", S("u1")), ("blob", B([0x00, 0x01, 0x02]))));

        var (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest
        {
            TableName = t,
            FilterExpression = "#b IN ()",
            ExpressionAttributeNames = Names(("#b", "blob")),
        }));
        Ensure(
            (code, message) == ("ValidationException", "Invalid FilterExpression: Syntax error; token: \")\", near: \"()\""),
            $"empty IN operand list gave ({Py.Repr(code)}, {Py.Repr(message)})");

        var resp = await ddb.ScanAsync(new ScanRequest
        {
            TableName = t,
            FilterExpression = "#b IN (:v1)",
            ExpressionAttributeNames = Names(("#b", "blob")),
            ExpressionAttributeValues = Item((":v1", B([0x00, 0x01, 0x02]))),
            ConsistentRead = true,
        });
        var pks = new SortedSet<string>(resp.Items.Select(item => item["pk"].S), StringComparer.Ordinal);
        Ensure(pks.SetEquals(["u1"]), $"a non-empty IN after the refusal expected {{'u1'}}, got {Py.Repr(pks)}");
    }
}

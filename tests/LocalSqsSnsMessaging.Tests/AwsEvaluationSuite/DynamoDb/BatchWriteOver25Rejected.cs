// Translated from behavioral/dynamodb/check_batch_write_over_25_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-write-over-25-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchWriteOver25Rejected
{
    private const string Check = "batch-write-over-25-rejected";
    private const string ErrValidation = "ValidationException";

    private static readonly string NullMembers = string.Join(
        ", ",
        new[] { "n", "b", "iNT", "dECIMAL", "dOUBLE", "sS", "nS", "bS", "iNTSET", "dECIMALSET", "dOUBLESET", "m", "l", "nULL", "bOOL" }
            .Select(m => $"{m}=null"));

    /// <summary>
    /// BatchWriteItem with >25 items raises ValidationException and writes nothing,
    /// while a request at the 25-item limit on the same table succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-write-over-25-rejected")]
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
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var controlRequests = Enumerable.Range(0, 25)
            .Select(i => new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S($"bw-ctrl-{i.ToString("000", CultureInfo.InvariantCulture)}"))) } })
            .ToList();
        var controlResp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = new() { [table] = controlRequests } });
        Ensure(
            controlResp.UnprocessedItems is not { Count: > 0 },
            $"expected 25-item control batch fully accepted, got unprocessed: {Py.Repr(controlResp.UnprocessedItems?.Keys)}");
        var gotControl = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("bw-ctrl-000"))), ConsistentRead = true })).Item;
        Ensure(gotControl is not null, "expected control item bw-ctrl-000 to be written");

        var keys = Enumerable.Range(0, 26).Select(i => $"bw-over-{i.ToString("000", CultureInfo.InvariantCulture)}").ToList();
        var requests = keys.Select(k => new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", S(k))) } }).ToList();
        var error = await FidelityContext.CaptureError(() => ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = new() { [table] = requests } }));
        var (code, message) = (error?.Code, error?.Message);
        Ensure(code == ErrValidation, $"expected {ErrValidation}, got {code ?? "None"}");
        var expected = OverLimitMessage(table, keys);
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        var gotOver = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("bw-over-000"))), ConsistentRead = true })).Item;
        Ensure(gotOver is null, $"expected rejected batch to write nothing, found item: {Py.Repr(gotOver)}");
    }

    /// <summary>
    /// The ValidationException message AWS returns for an over-limit batch of string-key puts:
    /// it renders every request of the batch, in order.
    /// </summary>
    private static string OverLimitMessage(string table, IEnumerable<string> keys)
    {
        var writes = string.Join(
            ", ",
            keys.Select(k =>
                $"WriteRequest(putRequest=PutRequest(item={{pk=AttributeValue(s={k}, {NullMembers})}}, " +
                "workloadProfileName=null), deleteRequest=null)"));
        return
            $"1 validation error detected: Value '{{{table}=[{writes}]}}' at 'requestItems' failed to satisfy " +
            "constraint: Map value must satisfy constraint: [Member must have length less than or equal to 25, " +
            "Member must have length greater than or equal to 1]";
    }
}

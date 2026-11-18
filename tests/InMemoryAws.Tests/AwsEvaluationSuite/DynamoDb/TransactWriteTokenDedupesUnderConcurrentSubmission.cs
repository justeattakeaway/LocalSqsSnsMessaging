// Translated from behavioral/dynamodb/check_transact_write_token_dedupes_under_concurrent_submission.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-token-dedupes-under-concurrent-submission</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteTokenDedupesUnderConcurrentSubmission
{
    private const string Check = "transact-write-token-dedupes-under-concurrent-submission";
    private const int Submitters = 6;
    private const int ItemsPerTransaction = 12;
    private const int Rounds = 4;

    /// <summary>
    /// One ClientRequestToken submitted concurrently by several callers applies its transaction exactly
    /// once to every item; at least one submitter succeeds and any refusal is TransactionInProgressException.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-token-dedupes-under-concurrent-submission")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-txtoken-");
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

        // A wide transaction and a fresh token per round maximise the overlap window.
        for (var roundNo = 0; roundNo < Rounds; roundNo++)
        {
            var token = Guid.NewGuid().ToString();
            var prefix = $"round-{roundNo}";
            var items = Enumerable.Range(0, ItemsPerTransaction)
                .Select(i => new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = Item(("pk", S($"{prefix}#{i}"))),
                        UpdateExpression = "ADD applied :one",
                        ExpressionAttributeValues = Item((":one", N("1"))),
                    },
                })
                .ToList();

            async Task<string?> Submit()
            {
                var error = await FidelityContext.CaptureError(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
                {
                    TransactItems = items,
                    ClientRequestToken = token,
                }));
                return error?.Code;
            }

            string?[] results;
            using (ctx.Concurrently())
            {
                results = await Task.WhenAll(Enumerable.Range(0, Submitters).Select(_ => Task.Run(Submit, cancellationToken)));
            }

            var refusals = results.Where(c => c is not null).ToList();

            Ensure(refusals.Count < Submitters, $"round {roundNo}: every submission was refused: {Py.Repr(refusals)}");
            var other = refusals.Where(c => c != "TransactionInProgressException").ToList();
            Ensure(
                other.Count == 0,
                $"round {roundNo}: an overlapping submission of one token may only be refused with TransactionInProgressException, got {Py.Repr(refusals)}");

            var counts = new Dictionary<int, string>();
            for (var i = 0; i < ItemsPerTransaction; i++)
            {
                var item = (await ddb.GetItemAsync(new GetItemRequest
                {
                    TableName = table,
                    Key = Item(("pk", S($"{prefix}#{i}"))),
                    ConsistentRead = true,
                })).Item;
                Ensure(
                    item is not null,
                    $"round {roundNo}: item {i} of a transaction that {Submitters} callers submitted is absent altogether");
                counts[i] = item["applied"].N;
            }

            var over = counts.Where(e => e.Value != "1").ToDictionary(e => e.Key, e => e.Value);
            Ensure(
                over.Count == 0,
                $"round {roundNo}: one token means one application however many callers submitted it "
                + "concurrently, and it must hold for EVERY item in the transaction. These items were "
                + $"applied a different number of times: {Py.Repr(over)} (expected every one at 1 after "
                + $"{Submitters.ToString(CultureInfo.InvariantCulture)} concurrent submissions)");
        }
    }
}

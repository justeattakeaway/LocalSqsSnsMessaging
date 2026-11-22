// Translated from behavioral/dynamodb/check_concurrent_conditional_put_distinct_writers_winner_matches_read.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.concurrent-conditional-put-distinct-writers-winner-matches-read</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentConditionalPutDistinctWritersWinnerMatchesRead
{
    private const string Check = "concurrent-conditional-put-distinct-writers-winner-matches-read";
    private const int Racers = 20;
    private static readonly (string, string) Ccf = ("ConditionalCheckFailedException", "The conditional request failed");

    private static Dictionary<string, AttributeValue> Key() => Item(("pk", S("race")));

    /// <summary>
    /// 20 conditional PutItems on one new key, each with a distinct writer id: exactly one
    /// succeeds, the other 19 are refused ConditionalCheckFailedException with its exact message,
    /// and a consistent read returns the one writer id that was told it succeeded.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.concurrent-conditional-put-distinct-writers-winner-matches-read")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-condwin-");
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

        var writerIds = Enumerable.Range(0, Racers).Select(i => $"writer-{i}").ToList();

        async Task<(string WriterId, AwsError? Error)> Racer(string writerId) =>
            (writerId, await FidelityContext.CaptureError(() => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("race")), ("writer", S(writerId))),
                ConditionExpression = "attribute_not_exists(pk)",
            })));

        (string WriterId, AwsError? Error)[] outcomes;
        using (ctx.Concurrently())
        {
            outcomes = await Task.WhenAll(writerIds.Select(w => Task.Run(() => Racer(w))));
        }

        var wins = outcomes.Where(o => o.Error is null).Select(o => o.WriterId).ToList();
        var losses = outcomes.Where(o => o.Error is not null).Select(o => (o.WriterId, o.Error!.Code, o.Error.Message)).ToList();

        Ensure(wins.Count == 1, $"expected exactly 1 winner among {Racers} writers, got {Py.Repr(wins)}");
        Ensure(losses.Count == Racers - 1, $"expected {Racers - 1} refusals, got {losses.Count}: {Py.Repr(losses)}");
        foreach (var (writerId, code, message) in losses)
        {
            Ensure(
                (code, message) == Ccf,
                $"writer {Py.Repr(writerId)} was refused with ({Py.Repr(code)}, {Py.Repr(message)}), expected {Py.Repr(Ccf)}");
        }

        var landed = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(landed is not null, "independent consistent GetItem found no item after one PutItem won");
        var writer = landed.GetValueOrDefault("writer");
        Ensure(
            Py.Equal(writer, S(wins[0])),
            $"the item's writer is {Py.Repr(writer)}, expected the winner's own id {{'S': {Py.Repr(wins[0])}}}");
    }
}

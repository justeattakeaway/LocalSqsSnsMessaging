// Translated from behavioral/dynamodb/check_partiql_update_growth_past_400kb_matches_update_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.partiql-update-growth-past-400kb-matches-update-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartiqlUpdateGrowthPast400kbMatchesUpdateItem
{
    private const string CheckId = "partiql-update-growth-past-400kb-matches-update-item";
    private const string ErrValidation = "ValidationException";
    private const int KB = 1024;

    /// <summary>
    /// The botocore error code and message <paramref name="fn"/> raises. An unexpected SUCCESS
    /// raises rather than returning a falsy pair: a responder that ACCEPTS a growth real AWS
    /// refuses is the strongest signal this check can produce.
    /// </summary>
    private static async Task<(string? Code, string? Message)> CodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    /// <summary>
    /// A PartiQL UPDATE that would grow an item past 400 KB raises ValidationException
    /// carrying the exact same code and message as the classic UpdateItem API for the same
    /// growth, and leaves nothing written; PartiQL UPDATE itself works on the same table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.partiql-update-growth-past-400kb-matches-update-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-partiql-growth-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        var oversized = new string('x', 401 * KB);

        async Task<Dictionary<string, AttributeValue>> Get(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item ?? [];

        // Positive control: PartiQL UPDATE works at all on this table, independent of size.
        await ddb.PutItemAsync(table, Item(("pk", S("control"))));
        await ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET note = ? WHERE pk = ?",
            Parameters = [S("ok"), S("control")],
        });
        var control = await Get("control");
        Ensure(control.GetValueOrDefault("note")?.S == "ok", $"positive control PartiQL UPDATE/GetItem round-trip failed, got {Py.Repr(control)}");

        // The classic UpdateItem answer for this exact growth, on its own item.
        await ddb.PutItemAsync(table, Item(("pk", S("via-update-item"))));
        var (classicCode, classicMessage) = await CodeAndMessage(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("via-update-item"))),
            UpdateExpression = "SET bigattr = :v",
            ExpressionAttributeValues = Item((":v", S(oversized))),
        }));
        Ensure(classicCode == ErrValidation, $"UpdateItem SET of a 401KB value must be {ErrValidation}, got {Py.Repr(classicCode)}");
        Ensure(
            classicMessage == "Item size to update has exceeded the maximum allowed size",
            $"UpdateItem SET of a 401KB value must carry the exact AWS message, got {Py.Repr(classicMessage)}");
        var afterClassic = await Get("via-update-item");
        Ensure(!afterClassic.ContainsKey("bigattr"), $"a rejected UpdateItem SET must leave no trace, item is {Py.Repr(afterClassic)}");

        // The PartiQL UPDATE answer for the identical growth, on a different item.
        await ddb.PutItemAsync(table, Item(("pk", S("via-partiql"))));
        var (partiqlCode, partiqlMessage) = await CodeAndMessage(() => ddb.ExecuteStatementAsync(new ExecuteStatementRequest
        {
            Statement = $"UPDATE \"{table}\" SET bigattr = ? WHERE pk = ?",
            Parameters = [S(oversized), S("via-partiql")],
        }));
        Ensure(partiqlCode == ErrValidation, $"PartiQL UPDATE growing past 400KB must be {ErrValidation}, got {Py.Repr(partiqlCode)}");
        var afterPartiql = await Get("via-partiql");
        Ensure(!afterPartiql.ContainsKey("bigattr"), $"a rejected PartiQL UPDATE must leave no trace, item is {Py.Repr(afterPartiql)}");

        // The two paths must name the exact same refusal for the exact same growth.
        Ensure(
            partiqlCode == classicCode,
            $"PartiQL UPDATE error code {Py.Repr(partiqlCode)} differs from UpdateItem error code {Py.Repr(classicCode)} for the identical growth");
        Ensure(
            partiqlMessage == classicMessage,
            $"PartiQL UPDATE message {Py.Repr(partiqlMessage)} differs from UpdateItem's message {Py.Repr(classicMessage)} for the identical growth");
    }
}

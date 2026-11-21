// Translated from behavioral/dynamodb/check_update_item_list_index_set_target_charge_below_plain_set.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-list-index-set-target-charge-below-plain-set</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemListIndexSetTargetChargeBelowPlainSet
{
    private const string Check = "update-item-list-index-set-target-charge-below-plain-set";
    private const int DeltaFromPlainSet = 5;

    /// <summary>The seed every attempt starts from: pk plus an existing one-element list "d".</summary>
    private static Dictionary<string, AttributeValue> Seed() => Item(("d", L(S("z"))));

    private static async Task CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string name)
    {
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 30);
    }

    /// <summary>
    /// UpdateItem's size ceiling for a list-index SET target sits a fixed number of bytes
    /// below a plain whole-attribute SET's ceiling on the same pre-existing one-element list.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-list-index-set-target-charge-below-plain-set")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddblistidx-");
        await CreateTable(ctx, ddb, table);
        var probe = new UpdateSizeBisection(ddb);

        var (plainLo, plainHi) = await probe.Bisect(table, Seed(), n => new("SET v = :v", Item((":v", S(new string('x', n))))), verify: false);
        Ensure(plainHi == plainLo + 1, $"plain SET bisection did not converge: lo={plainLo} hi={plainHi}");

        var (idxLo, idxHi) = await probe.Bisect(table, Seed(), n => new("SET d[0] = :v", Item((":v", S(new string('x', n))))), verify: false);
        Ensure(idxHi == idxLo + 1, $"list-index SET bisection did not converge: lo={idxLo} hi={idxHi}");

        // Measured relative to the plain whole-attribute SET boundary on the same preexisting item.
        Ensure(
            plainLo - idxLo == DeltaFromPlainSet,
            $"list-index SET ceiling {idxLo} was {plainLo - idxLo} byte(s) below the plain SET " +
            $"ceiling {plainLo}, expected {DeltaFromPlainSet}");

        // Positive control: the accepted list-index boundary actually lands at that index, and the
        // next byte up leaves the existing element untouched -- proven by a read.
        var acceptedPk = probe.NextPk();
        await ddb.PutItemAsync(table, Item(("pk", S(acceptedPk)), ("d", L(S("z")))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(acceptedPk))),
            UpdateExpression = "SET d[0] = :v",
            ExpressionAttributeValues = Item((":v", S(new string('x', idxLo)))),
        });
        var acceptedItem = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(acceptedPk))), ConsistentRead = true })).Item;
        var acceptedElem = acceptedItem["d"].L[0].S;
        Ensure(
            acceptedElem.Length == idxLo,
            $"the accepted {idxLo}-byte list-index SET did not read back at that length: {Py.Repr(acceptedElem)}");

        var refusedPk = probe.NextPk();
        await ddb.PutItemAsync(table, Item(("pk", S(refusedPk)), ("d", L(S("z")))));
        var got = await UpdateSizeBisection.CodeAndMessage(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(refusedPk))),
            UpdateExpression = "SET d[0] = :v",
            ExpressionAttributeValues = Item((":v", S(new string('x', idxHi)))),
        }));
        Ensure(got == UpdateSizeBisection.OverLimit, $"refusal at {idxHi} bytes was {Py.Repr(got)}, expected {Py.Repr(UpdateSizeBisection.OverLimit)}");
        var refusedItem = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(refusedPk))), ConsistentRead = true })).Item;
        Ensure(
            refusedItem["d"].L[0].S == "z",
            $"a refused list-index SET must leave the existing element untouched, got {Py.Repr(refusedItem)}");
    }
}

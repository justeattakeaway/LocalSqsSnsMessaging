// Translated from behavioral/dynamodb/check_update_item_nested_path_set_target_charge_below_plain_set.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-nested-path-set-target-charge-below-plain-set</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemNestedPathSetTargetChargeBelowPlainSet
{
    private const string Check = "update-item-nested-path-set-target-charge-below-plain-set";
    private const int DeltaFromPlainSet = 5;

    /// <summary>The seed every attempt starts from: pk plus a pre-existing nested map "m" (containing "x").</summary>
    private static Dictionary<string, AttributeValue> Seed() => Item(("m", M(("x", S("z")))));

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
    /// UpdateItem's size ceiling for a standalone nested-path SET target (m.x) sits a fixed
    /// number of bytes below a plain whole-attribute SET's ceiling on an item carrying the
    /// same pre-existing map.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-nested-path-set-target-charge-below-plain-set")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbnestedpath-");
        await CreateTable(ctx, ddb, table);
        var probe = new UpdateSizeBisection(ddb);

        var (plainLo, plainHi) = await probe.Bisect(table, Seed(), n => new("SET v = :v", Item((":v", S(new string('x', n))))), verify: false);
        Ensure(plainHi == plainLo + 1, $"plain SET bisection did not converge: lo={plainLo} hi={plainHi}");

        var (nestedLo, nestedHi) = await probe.Bisect(table, Seed(), n => new("SET m.x = :v", Item((":v", S(new string('x', n))))), verify: false);
        Ensure(nestedHi == nestedLo + 1, $"nested-path SET bisection did not converge: lo={nestedLo} hi={nestedHi}");

        Ensure(
            plainLo - nestedLo == DeltaFromPlainSet,
            $"a standalone nested-path SET ceiling was {Py.Repr(nestedLo)}, {plainLo - nestedLo} byte(s) " +
            $"below the plain whole-attribute SET ceiling {Py.Repr(plainLo)}, expected {DeltaFromPlainSet}");

        // Positive control: the accepted nested-path boundary actually lands at m.x, and the next
        // byte up leaves the existing nested value untouched -- proven by a read.
        var acceptedPk = probe.NextPk();
        await ddb.PutItemAsync(table, Item(("pk", S(acceptedPk)), ("m", M(("x", S("z"))))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(acceptedPk))),
            UpdateExpression = "SET m.x = :v",
            ExpressionAttributeValues = Item((":v", S(new string('x', nestedLo)))),
        });
        var acceptedItem = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(acceptedPk))), ConsistentRead = true })).Item;
        var acceptedVal = acceptedItem["m"].M["x"].S;
        Ensure(
            acceptedVal.Length == nestedLo,
            $"the accepted {nestedLo}-byte nested-path SET did not read back at that length: {Py.Repr(acceptedVal)}");

        var refusedPk = probe.NextPk();
        await ddb.PutItemAsync(table, Item(("pk", S(refusedPk)), ("m", M(("x", S("z"))))));
        var got = await UpdateSizeBisection.CodeAndMessage(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(refusedPk))),
            UpdateExpression = "SET m.x = :v",
            ExpressionAttributeValues = Item((":v", S(new string('x', nestedHi)))),
        }));
        Ensure(got == UpdateSizeBisection.OverLimit, $"refusal at {nestedHi} bytes was {Py.Repr(got)}, expected {Py.Repr(UpdateSizeBisection.OverLimit)}");
        var refusedItem = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(refusedPk))), ConsistentRead = true })).Item;
        Ensure(
            refusedItem["m"].M["x"].S == "z",
            $"a refused nested-path SET must leave the existing value untouched, got {Py.Repr(refusedItem)}");
    }
}

using Shouldly;
using MessageAttributeValue = LocalSqsSnsMessaging.Sns.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.Sns;

public class SnsFilterPolicyTests
{
    private static SnsSubscription Subscription(string policy, string scope = SnsFilterPolicy.MessageAttributesScope) => new()
    {
        SubscriptionArn = "sub",
        TopicArn = "arn:aws:sns:us-east-1:000000000000:topic",
        EndPoint = "arn:aws:sqs:us-east-1:000000000000:queue",
        Protocol = "sqs",
        Raw = true,
        FilterPolicyScope = scope,
        FilterPolicy = policy
    };

    private static Dictionary<string, MessageAttributeValue> Attributes(params (string Name, string Type, string Value)[] attributes) =>
        attributes.ToDictionary(a => a.Name, a => new MessageAttributeValue { DataType = a.Type, StringValue = a.Value });

    [Test]
    [Arguments("""{"colour": ["red", "blue"]}""", "colour", "String", "red", true)]
    [Arguments("""{"colour": ["red", "blue"]}""", "colour", "String", "green", false)]
    [Arguments("""{"colour": [{"anything-but": ["red"]}]}""", "colour", "String", "green", true)]
    [Arguments("""{"colour": [{"anything-but": ["red"]}]}""", "colour", "String", "red", false)]
    [Arguments("""{"colour": [{"prefix": "bl"}]}""", "colour", "String", "blue", true)]
    [Arguments("""{"colour": [{"suffix": "ue"}]}""", "colour", "String", "blue", true)]
    [Arguments("""{"colour": [{"equals-ignore-case": "BLUE"}]}""", "colour", "String", "blue", true)]
    [Arguments("""{"price": [{"numeric": [">=", 100, "<", 200]}]}""", "price", "Number", "150", true)]
    [Arguments("""{"price": [{"numeric": [">=", 100, "<", 200]}]}""", "price", "Number", "200", false)]
    [Arguments("""{"price": [100]}""", "price", "Number", "100", true)]
    [Arguments("""{"price": [100]}""", "price", "String", "100", false)]
    [Arguments("""{"colour": [{"exists": true}]}""", "colour", "String", "red", true)]
    [Arguments("""{"colour": [{"exists": false}]}""", "size", "String", "large", true)]
    [Arguments("""{"colour": [{"exists": true}]}""", "size", "String", "large", false)]
    [Arguments("""{"tags": ["sale"]}""", "tags", "String.Array", """["new", "sale"]""", true)]
    [Arguments("""{"tags": ["sale"]}""", "tags", "String.Array", """["new"]""", false)]
    [Arguments("""{"tags": ["sale"]}""", "tags", "Binary", "c2FsZQ==", false)]
    public void AttributeScope_MatchesPerPolicy(string policy, string attributeName, string type, string value, bool expected)
    {
        var subscription = Subscription(policy);

        SnsFilterPolicy.Matches(subscription, "body", Attributes((attributeName, type, value))).ShouldBe(expected);
    }

    [Test]
    public void AttributeScope_OrBranches_MatchWhenAnyBranchMatches()
    {
        var subscription = Subscription("""{"$or": [{"colour": ["red"]}, {"size": ["large"]}]}""");

        SnsFilterPolicy.Matches(subscription, "body", Attributes(("size", "String", "large"))).ShouldBeTrue();
        SnsFilterPolicy.Matches(subscription, "body", Attributes(("size", "String", "small"))).ShouldBeFalse();
    }

    [Test]
    public void EmptyPolicy_MatchesEverything()
    {
        SnsFilterPolicy.Matches(Subscription(""), "body", null).ShouldBeTrue();
        SnsFilterPolicy.Matches(Subscription("{}"), "body", null).ShouldBeTrue();
    }

    [Test]
    [Arguments("""{"order": {"status": ["shipped"]}}""", """{"order": {"status": "shipped"}}""", true)]
    [Arguments("""{"order": {"status": ["shipped"]}}""", """{"order": {"status": "placed"}}""", false)]
    [Arguments("""{"order": {"status": ["shipped"]}}""", """{"order": "shipped"}""", false)]
    [Arguments("""{"items": {"sku": ["abc"]}}""", """{"items": [{"sku": "xyz"}, {"sku": "abc"}]}""", true)]
    [Arguments("""{"total": [{"numeric": [">", 10]}]}""", """{"total": 12.5}""", true)]
    [Arguments("""{"active": [true]}""", """{"active": true}""", true)]
    [Arguments("""{"active": [true]}""", """{"active": "true"}""", false)]
    [Arguments("""{"ref": [null]}""", """{"ref": null}""", true)]
    [Arguments("""{"order": {"status": ["shipped"]}}""", "not json", false)]
    [Arguments("""{"order": {"status": ["shipped"]}}""", """["shipped"]""", false)]
    public void BodyScope_MatchesAgainstJsonBody(string policy, string body, bool expected)
    {
        var subscription = Subscription(policy, SnsFilterPolicy.MessageBodyScope);

        SnsFilterPolicy.Matches(subscription, body, Attributes(("order", "String", "shipped"))).ShouldBe(expected);
    }

    [Test]
    [Arguments("not json")]
    [Arguments("""["array"]""")]
    [Arguments("""{"colour": "red"}""")]
    [Arguments("""{"colour": []}""")]
    [Arguments("""{"colour": [["red"]]}""")]
    [Arguments("""{"colour": [{"prefix": "a", "suffix": "b"}]}""")]
    [Arguments("""{"colour": [{"wildcard": "a*"}]}""")]
    [Arguments("""{"$or": {"colour": ["red"]}}""")]
    public void Validate_RejectsMalformedPolicies(string policy)
    {
        Should.Throw<InternalInvalidParameterException>(() => SnsFilterPolicy.Validate(policy, SnsFilterPolicy.MessageAttributesScope));
    }

    [Test]
    public void Validate_RejectsNestingForAttributeScope_ButAllowsItForBodyScope()
    {
        const string nested = """{"order": {"status": ["shipped"]}}""";

        Should.Throw<InternalInvalidParameterException>(() => SnsFilterPolicy.Validate(nested, SnsFilterPolicy.MessageAttributesScope));
        Should.NotThrow(() => SnsFilterPolicy.Validate(nested, SnsFilterPolicy.MessageBodyScope));
    }

    [Test]
    public void Validate_RejectsMoreThanFiveLevelsOfNesting()
    {
        const string sixLevels = """{"a": {"b": {"c": {"d": {"e": {"f": ["x"]}}}}}}""";
        const string fiveLevels = """{"a": {"b": {"c": {"d": {"e": ["x"]}}}}}""";

        Should.Throw<InternalInvalidParameterException>(() => SnsFilterPolicy.Validate(sixLevels, SnsFilterPolicy.MessageBodyScope));
        Should.NotThrow(() => SnsFilterPolicy.Validate(fiveLevels, SnsFilterPolicy.MessageBodyScope));
    }
}

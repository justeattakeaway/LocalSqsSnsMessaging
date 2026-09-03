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
    [Arguments("""{"colour": [{"wildcard": "b*e"}]}""", "colour", "String", "blue", true)]
    [Arguments("""{"colour": [{"wildcard": "b*e"}]}""", "colour", "String", "red", false)]
    [Arguments("""{"colour": [{"anything-but": {"wildcard": "*ue"}}]}""", "colour", "String", "blue", false)]
    [Arguments("""{"source_ip": [{"cidr": "10.0.0.0/24"}]}""", "source_ip", "String", "10.0.0.42", true)]
    [Arguments("""{"source_ip": [{"cidr": "10.0.0.0/24"}]}""", "source_ip", "String", "10.1.0.42", false)]
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
    [Arguments("""{"colour": [{"regex": "a.*"}]}""")]
    [Arguments("""{"$or": {"colour": ["red"]}}""")]
    // Malformed operands: the operator is known but its argument isn't the right shape.
    [Arguments("""{"price": [{"numeric": [">", 100, "<"]}]}""")]
    [Arguments("""{"price": [{"numeric": ["~", 100]}]}""")]
    [Arguments("""{"price": [{"numeric": [">", "100"]}]}""")]
    [Arguments("""{"price": [{"numeric": ["<", 100, ">", 200]}]}""")]
    [Arguments("""{"price": [{"numeric": 100}]}""")]
    [Arguments("""{"colour": [{"exists": "yes"}]}""")]
    [Arguments("""{"colour": [{"prefix": 5}]}""")]
    [Arguments("""{"colour": [{"suffix": ["a", "b"]}]}""")]
    [Arguments("""{"colour": [{"equals-ignore-case": true}]}""")]
    [Arguments("""{"colour": [{"wildcard": ["a*"]}]}""")]
    [Arguments("""{"colour": [{"anything-but": []}]}""")]
    [Arguments("""{"colour": [{"anything-but": {"regex": "x"}}]}""")]
    [Arguments("""{"colour": [{"anything-but": {"prefix": 1}}]}""")]
    [Arguments("""{"colour": [{"anything-but": [["red"]]}]}""")]
    public void Validate_RejectsMalformedPolicies(string policy)
    {
        Should.Throw<InternalInvalidParameterException>(() => SnsFilterPolicy.Validate(policy, SnsFilterPolicy.MessageAttributesScope));
    }

    [Test]
    [Arguments("""{"colour": ["red", 1, true, null]}""")]
    [Arguments("""{"colour": [{"prefix": "a"}, {"suffix": "b"}, {"equals-ignore-case": "c"}, {"wildcard": "a*b"}]}""")]
    [Arguments("""{"colour": [{"prefix": {"equals-ignore-case": "a"}}]}""")]
    [Arguments("""{"source_ip": [{"cidr": "10.0.0.0/8"}]}""")]
    [Arguments("""{"price": [{"numeric": ["=", 100]}, {"numeric": [">=", 1, "<", 10]}]}""")]
    [Arguments("""{"colour": [{"exists": false}]}""")]
    [Arguments("""{"colour": [{"anything-but": "red"}, {"anything-but": 3}, {"anything-but": ["red", 2]}]}""")]
    [Arguments("""{"colour": [{"anything-but": {"prefix": "a"}}, {"anything-but": {"suffix": ["b", "c"]}}, {"anything-but": {"wildcard": "*x"}}]}""")]
    [Arguments("""{"$or": [{"colour": ["red"]}, {"size": ["large"]}], "shape": ["round"]}""")]
    public void Validate_AcceptsWellFormedPolicies(string policy)
    {
        Should.NotThrow(() => SnsFilterPolicy.Validate(policy, SnsFilterPolicy.MessageAttributesScope));
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

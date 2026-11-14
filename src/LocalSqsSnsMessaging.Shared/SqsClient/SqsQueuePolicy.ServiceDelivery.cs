using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>Whether a queue's policy lets an AWS service, such as SNS, deliver to it.</summary>
internal static partial class SqsQueuePolicy
{
    /// <summary>The condition keys that scope a grant to the resource a service acts for.</summary>
    private static readonly string[] SourceConditionKeys = ["aws:SourceArn", "aws:SourceAccount", "aws:SourceOwner"];

    /// <summary>
    /// Whether <paramref name="policy"/> lets the service in <paramref name="request"/>
    /// (<see cref="RequestContext.Service"/>) make the request, such as SNS sending a topic's
    /// notification to the queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Deny statement that applies refuses the delivery. Beyond that, a matching Allow is needed
    /// only when the policy addresses deliveries from that service: when an Allow statement names the
    /// service principal, or grants to everyone on an <c>aws:SourceArn</c>, <c>aws:SourceAccount</c>
    /// or <c>aws:SourceOwner</c> condition. A policy like that says which sources may deliver, so a
    /// source it does not admit is refused, as on AWS. A queue whose policy says nothing about the
    /// service keeps receiving: AWS would refuse it, but every such delivery succeeded before the
    /// library evaluated queue policies, and a queue created in a test rarely has one.
    /// </para>
    /// <para>
    /// As for callers, a statement the library cannot evaluate never refuses: a Deny with an
    /// unsupported condition is skipped, and an Allow with one is taken to grant.
    /// </para>
    /// </remarks>
    public static bool AdmitsServiceDelivery(string policy, RequestContext request)
    {
        if (request.Service is not { } service || Parse(policy) is not { } document)
        {
            return true;
        }

        var allows = new List<JsonObject>();
        foreach (var statement in Statements(document))
        {
            var effect = AsString(statement["Effect"]);
            if (string.Equals(effect, "Deny", StringComparison.Ordinal) && Applies(statement, request) == true)
            {
                return false;
            }

            if (string.Equals(effect, "Allow", StringComparison.Ordinal))
            {
                allows.Add(statement);
            }
        }

        return !allows.Exists(statement => AddressesService(statement, service))
               || allows.Exists(statement => Applies(statement, request) != false);
    }

    /// <summary>
    /// Whether an Allow statement is about deliveries from <paramref name="service"/>: it names the
    /// service principal, or grants to everyone on a condition about the source resource.
    /// </summary>
    private static bool AddressesService(JsonObject statement, string service)
    {
        var principal = statement["Principal"];
        if (principal is JsonObject named && Values(named["Service"]).Contains(service, StringComparer.Ordinal))
        {
            return true;
        }

        return principal is not null
               && NamesEveryone(principal)
               && statement["Condition"] is JsonObject operators
               && operators.Any(op => op.Value is JsonObject keys
                   && keys.Any(key => SourceConditionKeys.Contains(key.Key, StringComparer.OrdinalIgnoreCase)));
    }
}

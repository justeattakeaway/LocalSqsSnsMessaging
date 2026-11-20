namespace InMemoryAws;

/// <summary>Whether a resource's policy grants an AWS service a request outright.</summary>
internal sealed partial class ResourcePolicy
{
    /// <summary>
    /// Whether the policy grants the service request in <paramref name="request"/>: no Deny statement
    /// applies to it, and an Allow statement does. This is the check a service makes of a destination
    /// before relying on it (S3 validating a notification destination), so unlike
    /// <see cref="EvaluateServiceDelivery"/> a policy that says nothing about the service does not
    /// grant it.
    /// </summary>
    /// <remarks>As elsewhere, an Allow statement the library cannot evaluate is taken to grant.</remarks>
    public bool GrantsServiceRequest(RequestContext request) =>
        !DeniesExplicitly(request)
        && Statements().Any(statement =>
            string.Equals(AsString(statement["Effect"]), "Allow", StringComparison.Ordinal)
            && Applies(statement, request) != false);
}

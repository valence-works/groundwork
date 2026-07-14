using Groundwork.Core.Manifests;

namespace Groundwork.Core.Validation;

internal static class IdentityPolicyAdmission
{
    public static GroundworkDiagnostic? Validate(IdentityPolicy? policy, string target)
    {
        if (policy is null)
        {
            return GroundworkDiagnostic.Error(
                "GW-UNIT-007",
                "Storage unit identity policy is required.",
                target);
        }

        if (!Enum.IsDefined(policy.StringCasePolicy))
        {
            return GroundworkDiagnostic.Error(
                "GW-UNIT-013",
                $"String identity case policy '{policy.StringCasePolicy}' is not supported.",
                $"{target}.stringCasePolicy");
        }

        return policy.Kind != StorageIdentityKind.String &&
               policy.StringCasePolicy != StringIdentityCasePolicy.Ordinal
            ? GroundworkDiagnostic.Error(
                "GW-UNIT-013",
                $"String identity case policy '{policy.StringCasePolicy}' is valid only for string identities.",
                $"{target}.stringCasePolicy")
            : null;
    }
}

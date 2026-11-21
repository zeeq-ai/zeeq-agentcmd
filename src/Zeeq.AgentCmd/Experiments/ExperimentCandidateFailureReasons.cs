namespace Zeeq.AgentCmd;

/// <summary>
/// Closed set of <see cref="ExecutionFailure.ConfigurationFailure.Reason"/> values for strict candidate
/// preparation. Every value means that no provider request was sent.
/// </summary>
/// <remarks>
/// Values are stable machine codes. Workers can map them to a job state without parsing free text, and
/// they never contain source, template or provider details.
/// </remarks>
public static class ExperimentCandidateFailureReasons
{
    /// <summary>The candidate was null.</summary>
    public const string Null = "experiment_candidate_null";

    /// <summary>The runtime has no experiment options, so no candidate can be verified.</summary>
    public const string ExperimentsDisabled = "experiment_candidate_experiments_disabled";

    /// <summary>The candidate identity or expected fingerprint was blank.</summary>
    public const string Invalid = "experiment_candidate_invalid";

    /// <summary>The publication was authored for another compiled command revision.</summary>
    public const string StaleCommandRevision = "experiment_candidate_stale_command_revision";

    /// <summary>The publication was invalid or authored for another contract signature.</summary>
    public const string InvalidRelease = "experiment_candidate_invalid_release";

    /// <summary>Resolution selected the configured default for another reason, such as an unavailable source.</summary>
    public const string ResolvedDefault = "experiment_candidate_resolved_default";

    /// <summary>A configured-default candidate resolved to a treatment.</summary>
    public const string ResolvedTreatment = "experiment_candidate_resolved_treatment";

    /// <summary>The selected view came from another publication revision.</summary>
    public const string RevisionMismatch = "experiment_candidate_revision_mismatch";

    /// <summary>The selected view belongs to another experiment.</summary>
    public const string ExperimentMismatch = "experiment_candidate_experiment_mismatch";

    /// <summary>The selector chose another variant.</summary>
    public const string VariantMismatch = "experiment_candidate_variant_mismatch";

    /// <summary>The selector chose the variant in a shadow role.</summary>
    public const string RoleMismatch = "experiment_candidate_role_mismatch";

    /// <summary>
    /// The selected candidate could not be prepared: an override, input validation, a blank render, settings,
    /// or a route policy failed.
    /// </summary>
    public const string PreparationFailed = "experiment_candidate_preparation_failed";

    /// <summary>The host default fingerprint was missing or differed from the expected fingerprint.</summary>
    public const string DefaultFingerprintMismatch =
        "experiment_candidate_default_fingerprint_mismatch";

    /// <summary>An explicit call-site profile would replace the candidate's own route.</summary>
    public const string RouteOverridden = "experiment_candidate_route_overridden";
}

/// <summary>Carries a closed strict-candidate rejection from preparation to the outcome envelope.</summary>
internal sealed class ExperimentCandidateRejectedException(string reason) : Exception(reason)
{
    /// <summary>One of <see cref="ExperimentCandidateFailureReasons"/>.</summary>
    public string Reason { get; } = reason;
}

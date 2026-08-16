namespace Informant;

/// <summary>Accepts every new structural finding from the latest matching run artifact</summary>
public static class AcceptFindingsCommand
{
    /// <summary>Updates the configured finding ledger without refreshing the repository or contacting a model</summary>
    public static int Run(InformantConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        (string artifactPath, FindingRunArtifact artifact) = FindingArtifactFile.LoadLatest(config.ReportDirectory, config.RepositoryUrl, config.Branch);
        int accepted = new FindingStateStore(config.FindingStateFilePath).AcceptNew(artifact);

        Console.WriteLine($"Accepted {accepted} new finding{(accepted == 1 ? "" : "s")} from {Path.GetFileName(artifactPath)} into {config.FindingStateFilePath}");
        return 0;
    }
}

namespace Informant.Tests;

/// <summary>Serializes tests that temporarily change process-wide Informant configuration environment variables</summary>
[CollectionDefinition("Informant configuration environment", DisableParallelization = true)]
public sealed class InformantConfigurationEnvironmentCollection;

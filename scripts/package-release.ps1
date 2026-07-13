[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("win-x64", "linux-x64")]
    [string]$Runtime,

    [string]$OutputDirectory = "artifacts",

    [string]$ExpectedTag,

    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$metadataPath = Join-Path $repositoryRoot "Directory.Build.props"
$metadata = [xml](Get-Content -Raw -LiteralPath $metadataPath)
$version = [string]$metadata.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version))
{
    throw "Directory.Build.props does not contain a Version value"
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedTag) -and $ExpectedTag -ne "v$version")
{
    throw "Release tag '$ExpectedTag' does not match project version '$version'; expected 'v$version'"
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory, $repositoryRoot)
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null

$packageName = "BugSwatter-$version-$Runtime"
$archiveExtension = if ($Runtime -eq "win-x64") { ".zip" } else { ".tar.gz" }
$archivePath = Join-Path $resolvedOutput "$packageName$archiveExtension"
if (Test-Path -LiteralPath $archivePath)
{
    throw "Release archive already exists: $archivePath"
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "BugSwatter-package-$([Guid]::NewGuid().ToString('N'))"
$archiveCompleted = $false
try
{
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $packageDirectory = Join-Path $temporaryRoot $packageName
    New-Item -ItemType Directory -Path $packageDirectory | Out-Null

    if (-not $NoRestore)
    {
        dotnet restore (Join-Path $repositoryRoot "BugSwatter.slnx")
        if ($LASTEXITCODE -ne 0)
        {
            throw "dotnet restore failed with exit code $LASTEXITCODE"
        }
    }

    foreach ($application in "Informant", "Marshal")
    {
        $projectPath = Join-Path $repositoryRoot "src/$application/$application.csproj"
        $publishDirectory = Join-Path $temporaryRoot $application
        dotnet publish $projectPath -c Release -r $Runtime --no-self-contained --no-restore -p:DebugSymbols=false -p:DebugType=None -o $publishDirectory
        if ($LASTEXITCODE -ne 0)
        {
            throw "dotnet publish failed for $application on $Runtime with exit code $LASTEXITCODE"
        }

        $executableName = if ($Runtime -eq "win-x64") { "$application.exe" } else { $application }
        $executablePath = Join-Path $publishDirectory $executableName
        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf))
        {
            throw "Published executable not found: $executablePath"
        }

        Copy-Item -LiteralPath $executablePath -Destination (Join-Path $packageDirectory $executableName)
    }

    foreach ($fileName in "README.md", "DOCUMENTATION.md", "SECURITY.md", "CONTRIBUTING.md", "LICENSE", "NOTICE")
    {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $fileName) -Destination (Join-Path $packageDirectory $fileName)
    }

    if ($Runtime -eq "win-x64")
    {
        Compress-Archive -LiteralPath $packageDirectory -DestinationPath $archivePath -CompressionLevel Optimal
    }
    else
    {
        if (-not $IsWindows)
        {
            chmod +x (Join-Path $packageDirectory "Informant") (Join-Path $packageDirectory "Marshal")
            if ($LASTEXITCODE -ne 0)
            {
                throw "chmod failed with exit code $LASTEXITCODE"
            }
        }

        tar -czf $archivePath -C $temporaryRoot $packageName
        if ($LASTEXITCODE -ne 0)
        {
            throw "tar failed with exit code $LASTEXITCODE"
        }
    }

    $archiveCompleted = $true
    Write-Host "Created $archivePath"
}
finally
{
    if (-not $archiveCompleted -and (Test-Path -LiteralPath $archivePath))
    {
        Remove-Item -LiteralPath $archivePath -Force
    }

    if (Test-Path -LiteralPath $temporaryRoot)
    {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
        $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        if (-not $resolvedTemporaryRoot.StartsWith($resolvedSystemTemp, $comparison) -or [IO.Path]::GetFileName($resolvedTemporaryRoot) -notlike "BugSwatter-package-*")
        {
            throw "Refusing to remove unexpected temporary path: $resolvedTemporaryRoot"
        }

        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}

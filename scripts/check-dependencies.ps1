[CmdletBinding()]
param(
    [string]$Solution = "BugSwatter.slnx"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repositoryRoot
try
{
    dotnet restore $Solution
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet restore failed with exit code $LASTEXITCODE"
    }

    $assetFiles = Get-ChildItem -Path "src", "tests" -Filter "project.assets.json" -Recurse -File
    if ($assetFiles.Count -eq 0)
    {
        throw "No resolved project.assets.json files were found after restore"
    }

    $packages = foreach ($assetFile in $assetFiles)
    {
        $assets = Get-Content -Raw -LiteralPath $assetFile.FullName | ConvertFrom-Json -AsHashtable
        foreach ($library in $assets.libraries.GetEnumerator())
        {
            if ($library.Value.type -ne "package")
            {
                continue
            }

            $separator = $library.Key.LastIndexOf('/')
            if ($separator -lt 1)
            {
                throw "Unexpected package identity '$($library.Key)' in $($assetFile.FullName)"
            }

            [pscustomobject]@{
                Project = Split-Path -Leaf (Split-Path -Parent $assetFile.DirectoryName)
                Id = $library.Key.Substring(0, $separator)
                Version = $library.Key.Substring($separator + 1)
            }
        }
    }

    $resolved = $packages | Sort-Object Id, Version -Unique
    $newtonsoft = $resolved | Where-Object { $_.Id -ieq "Newtonsoft.Json" }
    if ($newtonsoft)
    {
        $newtonsoft | Format-Table Id, Version -AutoSize
        Write-Error "Dependency policy failed: Newtonsoft.Json is present in the resolved dependency graph"
        exit 1
    }

    $nonMicrosoft = $resolved | Where-Object {
        $_.Id -notmatch '^(Microsoft|System|Azure)\.' -and $_.Id -ne 'NETStandard.Library' -and $_.Id -notmatch '^runtime\..*\.System\.'
    }

    Write-Host "Dependency policy passed: Newtonsoft.Json is absent from $($assetFiles.Count) resolved project graphs"
    Write-Host "Resolved non-Microsoft packages:"
    if ($nonMicrosoft)
    {
        $nonMicrosoft | Format-Table Id, Version -AutoSize
    }
    else
    {
        Write-Host "  (none)"
    }
}
finally
{
    Pop-Location
}

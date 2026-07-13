[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repositoryRoot
try
{
    $trackedFiles = @(git ls-files)
    if ($LASTEXITCODE -ne 0)
    {
        throw "git ls-files failed with exit code $LASTEXITCODE"
    }

    $legacyReviewerName = "Slim" + "Shady"
    $rules = @(
        [pscustomobject]@{ Name = "private IPv4 address"; Pattern = '\b(?:10\.(?:[0-9]{1,3}\.){2}[0-9]{1,3}|192\.168\.(?:[0-9]{1,3}\.)[0-9]{1,3}|172\.(?:1[6-9]|2[0-9]|3[01])\.(?:[0-9]{1,3}\.)[0-9]{1,3})\b' },
        [pscustomobject]@{ Name = "Windows user-profile path"; Pattern = '(?i)\b[A-Z]:\\Users\\[^\\\r\n]+\\' },
        [pscustomobject]@{ Name = "private local source path"; Pattern = '(?i)\b[A-Z]:\\source\\myp\\' },
        [pscustomobject]@{ Name = "private-key header"; Pattern = '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----' },
        [pscustomobject]@{ Name = "GitHub token"; Pattern = '(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,})' },
        [pscustomobject]@{ Name = "OpenAI-style secret key"; Pattern = '\bsk-[A-Za-z0-9_-]{20,}\b' },
        [pscustomobject]@{ Name = "long access key"; Pattern = '(?i)\b(?:accesskey|accountkey|sharedaccesskey)=[A-Za-z0-9+/=_-]{24,}' },
        [pscustomobject]@{ Name = "legacy reviewer name"; Pattern = [regex]::Escape($legacyReviewerName) }
    )

    $violations = @();
    foreach ($file in $trackedFiles)
    {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf))
        {
            continue
        }

        foreach ($rule in $rules)
        {
            foreach ($match in Select-String -LiteralPath $file -Pattern $rule.Pattern -AllMatches -ErrorAction Stop)
            {
                $violations += [pscustomobject]@{ File = $file; Line = $match.LineNumber; Rule = $rule.Name }
            }
        }
    }

    if ($violations.Count -gt 0)
    {
        $violations | Sort-Object File, Line, Rule -Unique | Format-Table File, Line, Rule -AutoSize
        throw "Public-content policy failed with $($violations.Count) potential disclosure(s)"
    }

    Write-Host "Public-content policy passed: $($trackedFiles.Count) tracked files contain no forbidden private-network, local-path, legacy-name or high-signal secret markers"
}
finally
{
    Pop-Location
}

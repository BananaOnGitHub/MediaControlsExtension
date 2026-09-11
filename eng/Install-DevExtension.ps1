<#
.SYNOPSIS
Deploys the Media Controls extension using the canonical WAP packaging pipeline.
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments)]
    $RemainingArgs
)

& "$PSScriptRoot\Deploy-Package.ps1" -Aot @RemainingArgs

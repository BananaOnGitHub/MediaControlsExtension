<#
.SYNOPSIS
Uninstalls the Media Controls package using the canonical WAP packaging pipeline.
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments)]
    $RemainingArgs
)

& "$PSScriptRoot\Uninstall-Package.ps1" -PreserveApplicationData @RemainingArgs


[CmdletBinding()]
param(
    [string]$Filter = "Category!=IntentionalRed"
)

# NB-12: the local gate while the public push is not approved. Restore, build and test run
# one after another; every step's exit code is checked, and the test step's exit code is
# the script's own. Nothing is piped. The default runs everything except the IntentionalRed
# canary (SPEC R11 final gate); unlike CI it includes Kabul=OzelVault and Kabul=RetrieveSet.

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$solution = Join-Path $PSScriptRoot "../../Oom.sln"

# LocalGateKabul tests this script by running it; inside the gate it would start the gate
# again without end, so the gate's own run always leaves that one class out.
$Filter = "($Filter)&FullyQualifiedName!~Oom.Tests.Kabul.LocalGateKabul"

& dotnet restore $solution --disable-build-servers
if ($LASTEXITCODE -ne 0) { [Console]::Error.WriteLine("yerel-kapi: restore başarısız ($LASTEXITCODE)"); exit $LASTEXITCODE }

& dotnet build $solution -c Release --no-restore --disable-build-servers
if ($LASTEXITCODE -ne 0) { [Console]::Error.WriteLine("yerel-kapi: build başarısız ($LASTEXITCODE)"); exit $LASTEXITCODE }

& dotnet test $solution -c Release --no-build --filter $Filter
$testExit = $LASTEXITCODE
if ($testExit -ne 0) { [Console]::Error.WriteLine("yerel-kapi: test başarısız ($testExit)") }
exit $testExit

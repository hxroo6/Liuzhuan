param([string]$OutputDirectory='F:/appdata/Temp/liuzhuan-storage-scale')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$env:NUGET_PACKAGES='F:/dev-cache/nuget-packages'
New-Item -ItemType Directory -Force $OutputDirectory|Out-Null
function Checked([scriptblock]$Action){& $Action;if($LASTEXITCODE -ne 0){throw 'Storage/scale verification failed'}}
Checked {& F:/dotnet10/dotnet.exe build (Join-Path $PSScriptRoot 'StorageScaleChecks.csproj') --nologo}
$fixture=Join-Path $PSScriptRoot 'bin/Debug/net10.0-windows/StorageScaleChecks.dll'
Checked {& F:/dotnet10/dotnet.exe $fixture $OutputDirectory |Tee-Object (Join-Path $OutputDirectory 'storage.log')}
Checked {& F:/dotnet10/dotnet.exe $fixture $OutputDirectory --concurrency |Tee-Object (Join-Path $OutputDirectory 'concurrency.log')}
Checked {& F:/dotnet10/dotnet.exe build (Join-Path $repo 'scripts/DesktopUiChecks/DesktopUiChecks.csproj') --nologo}
$ui=Join-Path $repo 'scripts/DesktopUiChecks/bin/Debug/net10.0-windows/DesktopUiChecks.dll'
Checked {& F:/dotnet10/dotnet.exe $ui (Join-Path $OutputDirectory 'storage-ui') --storage-only}
foreach($case in @(@(1000,'1'),@(10000,'1.5'),@(50000,'2'))){
    $count=$case[0];$scale=$case[1]
    Checked {& F:/dotnet10/dotnet.exe $ui (Join-Path $OutputDirectory "scale-$count") --performance-only $count $scale |Tee-Object (Join-Path $OutputDirectory "scale-$count.log")}
}
@('Liuzhuan/Services/StorageMaintenance.cs','Liuzhuan/Services/DataStore.cs','Liuzhuan/Services/ThumbnailBudget.cs','Liuzhuan/Services/Lan/MaterialLibraryService.cs')|
    ForEach-Object {Get-FileHash -LiteralPath (Join-Path $repo $_)}|Select-Object Path,Hash|ConvertTo-Json|Set-Content (Join-Path $OutputDirectory 'source-hashes.json')
Write-Output "Isolated fixture checks finished: $OutputDirectory. No user files were cleaned. DPI exports are not physical multi-monitor acceptance."

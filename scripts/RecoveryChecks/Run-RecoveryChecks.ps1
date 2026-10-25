param([string]$OutputDirectory='F:/appdata/Temp/liuzhuan-m48-recovery')
$ErrorActionPreference='Stop'
$recoveryRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$recoveryRoot=Join-Path $OutputDirectory ('run-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $recoveryRoot | Out-Null
$env:NUGET_PACKAGES='F:/dev-cache/nuget-packages'
& F:/dotnet10/dotnet.exe build (Join-Path $PSScriptRoot 'RecoveryChecks.csproj') --nologo
if($LASTEXITCODE -ne 0){throw 'Recovery host build failed'}
& F:/dotnet10/dotnet.exe (Join-Path $PSScriptRoot 'bin/Debug/net10.0-windows/RecoveryChecks.dll') $recoveryRoot | Tee-Object -FilePath (Join-Path $recoveryRoot 'server-checks.log')
if($LASTEXITCODE -ne 0){throw 'Production server recovery checks failed'}
$recoveryCache='F:/gradle-home/caches/modules-2/files-2.1'
function Recovery-Jar([string]$Relative) {return (Get-ChildItem -LiteralPath (Join-Path $recoveryCache $Relative) -Recurse -Filter '*.jar' | Select-Object -First 1).FullName}
$recoveryStd=Recovery-Jar 'org.jetbrains.kotlin/kotlin-stdlib/2.0.21'
$recoveryCoroutines=Recovery-Jar 'org.jetbrains.kotlinx/kotlinx-coroutines-core-jvm/1.9.0'
$recoveryAnnotations=Recovery-Jar 'org.jetbrains/annotations/23.0.0'
$recoveryCompiler=@((Recovery-Jar 'org.jetbrains.kotlin/kotlin-compiler-embeddable/2.0.21'),$recoveryStd,(Recovery-Jar 'org.jetbrains.kotlin/kotlin-script-runtime/2.0.21'),(Recovery-Jar 'org.jetbrains.kotlin/kotlin-reflect/1.6.10'),(Recovery-Jar 'org.jetbrains.intellij.deps/trove4j/1.0.20200330'),$recoveryAnnotations,$recoveryCoroutines) -join ';'
$recoveryRuntime=@($recoveryStd,$recoveryCoroutines,$recoveryAnnotations,(Recovery-Jar 'com.squareup.okhttp3/okhttp/4.12.0'),(Recovery-Jar 'com.squareup.okio/okio-jvm/3.6.0'),'F:/gradle-8.11.1/lib/groovy-3.0.22.jar','F:/gradle-8.11.1/lib/groovy-json-3.0.22.jar') -join ';'
$recoverySource=Join-Path $recoveryRepo 'AndroidApp/app/src/main/java/com/liuzhuan/app'
$recoverySources=@('DurableTransfers.kt','TransferTasks.kt','RecoveryHistory.kt','TransferWork.kt','PowerSignals.kt','core/Hex.kt','clipboard/ClipboardEvent.kt') | ForEach-Object {Join-Path $recoverySource $_}
$recoverySources+=Join-Path $recoverySource 'TransferTaskStates.kt'
$recoverySources+=Join-Path $recoverySource 'TransferPolicy.kt'
$recoverySources+=@((Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.kt').FullName)
$recoverySources+=(Join-Path $recoveryRepo 'scripts/AutoReceiptChecks/JsonApiAdapter.kt')
$recoverySources | ForEach-Object {Get-FileHash -LiteralPath $_} | Select-Object Path,Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $recoveryRoot 'source-hashes.json')
$recoveryJar=Join-Path $recoveryRoot 'engine-checks.jar'
& F:/jdk-21.0.12+8/bin/java.exe -cp $recoveryCompiler org.jetbrains.kotlin.cli.jvm.K2JVMCompiler -no-stdlib -no-reflect -classpath $recoveryRuntime -d $recoveryJar @recoverySources 2>&1 | Tee-Object -FilePath (Join-Path $recoveryRoot 'engine-build.log')
if($LASTEXITCODE -ne 0){throw 'Production Android engine compilation failed'}
& F:/jdk-21.0.12+8/bin/java.exe -cp "$recoveryJar;$recoveryRuntime" com.liuzhuan.checks.locks.AndroidLockLeaseCheckKt 2>&1 | Tee-Object -FilePath (Join-Path $recoveryRoot 'lock-checks.log')
if($LASTEXITCODE -ne 0){throw 'Android recovery lock lease checks failed'}
& F:/jdk-21.0.12+8/bin/java.exe -cp "$recoveryJar;$recoveryRuntime" com.liuzhuan.checks.admission.AndroidTransferAdmissionCheckKt (Join-Path $recoveryRoot 'admission') 2>&1 | Tee-Object -FilePath (Join-Path $recoveryRoot 'admission-checks.log')
if($LASTEXITCODE -ne 0){throw 'Production Android admission checks failed'}
foreach($historyPhase in @('seed','verify')) {
    & F:/jdk-21.0.12+8/bin/java.exe -cp "$recoveryJar;$recoveryRuntime" com.liuzhuan.checks.history.AndroidHistoryCheckKt $historyPhase (Join-Path $recoveryRoot 'history')
    if($LASTEXITCODE -ne 0){throw 'Cross-process history retention failed'}
}
function Recovery-Port { $socket=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$socket.Start();$port=$socket.LocalEndpoint.Port;$socket.Stop();return $port }
$recoveryPort=Recovery-Port
$recoveryWsPort=Recovery-Port
$recoveryReady=Join-Path $recoveryRoot 'host-ready.json'
$recoveryStop=Join-Path $recoveryRoot 'host.stop'
$recoveryServer=Join-Path $recoveryRoot 'server'
$recoveryPhone=Join-Path $recoveryRoot 'phone'
function Start-RecoveryHost {
    if(Test-Path -LiteralPath $recoveryReady){Remove-Item -LiteralPath $recoveryReady}
    $proc=Start-Process -FilePath 'F:/dotnet10/dotnet.exe' -ArgumentList @(('"'+(Join-Path $PSScriptRoot 'bin/Debug/net10.0-windows/RecoveryChecks.dll')+'"'),$recoveryServer,'--host',$recoveryPort,$recoveryWsPort,$recoveryReady,$recoveryStop) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $recoveryRoot ('host-'+[Guid]::NewGuid().ToString('N')+'.log'))
    $deadline=(Get-Date).AddSeconds(15)
    while(-not (Test-Path -LiteralPath $recoveryReady)){if($proc.HasExited -or (Get-Date) -gt $deadline){throw 'Isolated host did not start'};Start-Sleep -Milliseconds 50}
    return $proc
}
$recoveryProcess=$null
try {
    $recoveryProcess=Start-RecoveryHost
    & F:/jdk-21.0.12+8/bin/java.exe -cp "$recoveryJar;$recoveryRuntime" AndroidEngineCheckKt upload-pause $recoveryPhone $recoveryReady 2>&1 | Tee-Object -FilePath (Join-Path $recoveryRoot 'engine-checks.log')
    if($LASTEXITCODE -ne 0){throw 'Android upload pause fixture failed'}
    # Intentional crash of this isolated test host, never the user's running app.
    $recoveryProcess.Kill();$recoveryProcess.WaitForExit()
    $recoveryProcess=Start-RecoveryHost
    & F:/jdk-21.0.12+8/bin/java.exe -cp "$recoveryJar;$recoveryRuntime" AndroidEngineCheckKt upload-resume-download-pause $recoveryPhone $recoveryReady 2>&1 | Tee-Object -FilePath (Join-Path $recoveryRoot 'engine-checks.log') -Append
    if($LASTEXITCODE -ne 0){throw 'Android restart/resume fixture failed'}
    $uploaded=@(Get-ChildItem -LiteralPath (Join-Path $recoveryServer 'uploads') -Filter '*_phone-source.bin');if($uploaded.Count -ne 1){throw 'Expected exactly one uploaded fixture'}
    if((Get-FileHash -LiteralPath $uploaded.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $recoveryPhone 'phone-source.bin')).Hash){throw 'Resumed upload checksum mismatch'}
    & F:/jdk-21.0.12+8/bin/java.exe '-Dfixture.failPublished=true' -cp "$recoveryJar;$recoveryRuntime" AndroidEngineCheckKt download-resume $recoveryPhone $recoveryReady (Join-Path $recoveryServer 'source.bin') 2>&1 | Tee-Object -FilePath (Join-Path $recoveryRoot 'engine-checks.log') -Append
    if($LASTEXITCODE -ne 0){throw 'Android publication recovery fixture failed'}
    Write-Output "Recovery evidence: $recoveryRoot"
}finally {
    if($recoveryProcess -and -not $recoveryProcess.HasExited){New-Item -ItemType File -Path $recoveryStop -Force | Out-Null;if(-not $recoveryProcess.WaitForExit(5000)){$recoveryProcess.Kill()}}
}

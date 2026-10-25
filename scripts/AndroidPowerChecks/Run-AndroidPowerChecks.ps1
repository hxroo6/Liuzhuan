param([string]$OutputDirectory = 'F:/appdata/Temp/liuzhuan-android-power-checks')
$ErrorActionPreference = 'Stop'
$powerRepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$powerJava = 'F:/jdk-21.0.12+8/bin/java.exe'
$powerCache = 'F:/gradle-home/caches/modules-2/files-2.1'
function Find-PowerJar([string]$Relative) {
    $powerJar=Get-ChildItem -LiteralPath (Join-Path $powerCache $Relative) -Recurse -Filter '*.jar' | Select-Object -First 1
    if(-not $powerJar) { throw "Cached dependency missing: $Relative" }
    return $powerJar.FullName
}
$powerStdlib=Find-PowerJar 'org.jetbrains.kotlin/kotlin-stdlib/2.0.21'
$powerCoroutines=Find-PowerJar 'org.jetbrains.kotlinx/kotlinx-coroutines-core-jvm/1.9.0'
$powerAnnotations=Find-PowerJar 'org.jetbrains/annotations/23.0.0'
$powerCompiler=@((Find-PowerJar 'org.jetbrains.kotlin/kotlin-compiler-embeddable/2.0.21'),$powerStdlib,
    (Find-PowerJar 'org.jetbrains.kotlin/kotlin-script-runtime/2.0.21'),
    (Find-PowerJar 'org.jetbrains.kotlin/kotlin-reflect/1.6.10'),
    (Find-PowerJar 'org.jetbrains.intellij.deps/trove4j/1.0.20200330'),$powerAnnotations,$powerCoroutines) -join ';'
$powerRuntime=@($powerStdlib,$powerCoroutines,$powerAnnotations,(Find-PowerJar 'com.squareup.okhttp3/okhttp/4.12.0'),(Find-PowerJar 'com.squareup.okio/okio-jvm/3.6.0')) -join ';'
$powerSourceRoot=Join-Path $powerRepoRoot 'AndroidApp/app/src/main/java/com/liuzhuan/app'
$powerSources=@('TransferTasks.kt','TransferWork.kt','PowerSignals.kt','net/HttpClients.kt','net/RetryBackoff.kt','core/LibraryBudget.kt','core/BoundedRead.kt','core/KeepAlivePolicy.kt','core/RecentFileEvents.kt','core/Hex.kt','core/LanRouteState.kt') | ForEach-Object { Join-Path $powerSourceRoot $_ }
$powerSources+=Join-Path $powerSourceRoot 'TransferTaskStates.kt'
$powerSources+=Join-Path $powerSourceRoot 'TransferPolicy.kt'
$powerSources+=(Join-Path $powerRepoRoot 'scripts/AndroidPrivacyChecks/AndroidOsStub.kt')
$powerSources+=(Join-Path $PSScriptRoot 'PowerCheck.kt')
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$powerJar=Join-Path $OutputDirectory 'power-checks.jar'
& $powerJava -cp $powerCompiler org.jetbrains.kotlin.cli.jvm.K2JVMCompiler -no-stdlib -no-reflect -classpath $powerRuntime -d $powerJar @powerSources 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'compile.log')
if($LASTEXITCODE -ne 0) { throw 'Power check compilation failed' }
& $powerJava -cp "$powerJar;$powerRuntime" com.liuzhuan.checks.PowerCheckKt 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'checks.log')
if($LASTEXITCODE -ne 0) { throw 'Power checks failed' }
$powerSyncSources=@('data/MaterialItem.kt','data/MaterialRepository.kt') | ForEach-Object { Join-Path $powerSourceRoot $_ }
$powerSyncSources+=@((Get-ChildItem -LiteralPath (Join-Path $powerRepoRoot 'scripts/AndroidSyncChecks') -Filter '*.kt').FullName)
$powerSyncJar=Join-Path $OutputDirectory 'sync-checks.jar'
& $powerJava -cp $powerCompiler org.jetbrains.kotlin.cli.jvm.K2JVMCompiler -no-stdlib -no-reflect -classpath $powerRuntime -d $powerSyncJar @powerSyncSources 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'sync-compile.log')
if($LASTEXITCODE -ne 0) { throw 'Sync check compilation failed' }
& $powerJava -cp "$powerSyncJar;$powerRuntime" MaterialRepositoryCheckKt 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'sync-checks.log')
if($LASTEXITCODE -ne 0) { throw 'Production repository regression checks failed' }
Write-Output "Evidence saved to $OutputDirectory"

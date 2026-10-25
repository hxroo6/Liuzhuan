param([string]$OutputDirectory='F:/appdata/Temp/liuzhuan-task-history-checks')
$ErrorActionPreference='Stop'
$historyRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$historyCache='F:/gradle-home/caches/modules-2/files-2.1'
function History-Jar([string]$relative){(Get-ChildItem -LiteralPath (Join-Path $historyCache $relative) -Recurse -Filter '*.jar'|Select-Object -First 1).FullName}
$historyStd=History-Jar 'org.jetbrains.kotlin/kotlin-stdlib/2.0.21'
$historyCoroutines=History-Jar 'org.jetbrains.kotlinx/kotlinx-coroutines-core-jvm/1.9.0'
$historyAnnotations=History-Jar 'org.jetbrains/annotations/23.0.0'
$historyCompiler=@((History-Jar 'org.jetbrains.kotlin/kotlin-compiler-embeddable/2.0.21'),$historyStd,(History-Jar 'org.jetbrains.kotlin/kotlin-script-runtime/2.0.21'),(History-Jar 'org.jetbrains.kotlin/kotlin-reflect/1.6.10'),(History-Jar 'org.jetbrains.intellij.deps/trove4j/1.0.20200330'),$historyAnnotations,$historyCoroutines)-join ';'
$historyRuntime=@($historyStd,$historyCoroutines,$historyAnnotations,'F:/gradle-8.11.1/lib/groovy-3.0.22.jar','F:/gradle-8.11.1/lib/groovy-json-3.0.22.jar')-join ';'
$historySourceRoot=Join-Path $historyRepo 'AndroidApp/app/src/main/java/com/liuzhuan/app'
$historySources=@('TransferTasks.kt','RecoveryHistory.kt')|ForEach-Object {Join-Path $historySourceRoot $_}
$historyStates=Join-Path $historySourceRoot 'TransferTaskStates.kt'
if(Test-Path -LiteralPath $historyStates){$historySources+=$historyStates}
$historySources+=@((Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.kt').FullName)
$historySources+=Join-Path $historyRepo 'scripts/AutoReceiptChecks/JsonApiAdapter.kt'
New-Item -ItemType Directory -Force -Path $OutputDirectory|Out-Null
$historyJar=Join-Path $OutputDirectory 'task-history-checks.jar'
& F:/jdk-21.0.12+8/bin/java.exe -cp $historyCompiler org.jetbrains.kotlin.cli.jvm.K2JVMCompiler -no-stdlib -no-reflect -classpath $historyRuntime -d $historyJar @historySources 2>&1|Tee-Object -FilePath (Join-Path $OutputDirectory 'compile.log')
if($LASTEXITCODE -ne 0){throw 'Production task history compilation failed'}
foreach($historyPhase in @('io-probe','seed','verify','verify-hidden')) {
    & F:/jdk-21.0.12+8/bin/java.exe '-Dfile.encoding=UTF-8' '-Dsun.stdout.encoding=UTF-8' '-Dsun.stderr.encoding=UTF-8' -cp "$historyJar;$historyRuntime" com.liuzhuan.checks.taskhistory.TaskHistoryCheckKt $historyPhase (Join-Path $OutputDirectory 'fixture') 2>&1|Tee-Object -FilePath (Join-Path $OutputDirectory 'checks.log') -Append
    if($LASTEXITCODE -ne 0){throw "Production task history $historyPhase failed"}
}
Write-Output "Task history evidence: $OutputDirectory"

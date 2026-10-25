param([string]$OutputDirectory='F:/appdata/Temp/liuzhuan-transfer-query-checks')
$ErrorActionPreference='Stop'
$queryRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$queryCache='F:/gradle-home/caches/modules-2/files-2.1'
function Find-QueryJar([string]$Relative) {
    $jar=Get-ChildItem -LiteralPath (Join-Path $queryCache $Relative) -Recurse -Filter '*.jar' | Select-Object -First 1
    if(-not $jar){throw "Cached Kotlin dependency missing: $Relative"}
    return $jar.FullName
}
$queryStd=Find-QueryJar 'org.jetbrains.kotlin/kotlin-stdlib/2.0.21'
$queryCoroutines=Find-QueryJar 'org.jetbrains.kotlinx/kotlinx-coroutines-core-jvm/1.9.0'
$queryAnnotations=Find-QueryJar 'org.jetbrains/annotations/23.0.0'
$queryCompiler=@((Find-QueryJar 'org.jetbrains.kotlin/kotlin-compiler-embeddable/2.0.21'),$queryStd,
    (Find-QueryJar 'org.jetbrains.kotlin/kotlin-script-runtime/2.0.21'),(Find-QueryJar 'org.jetbrains.kotlin/kotlin-reflect/1.6.10'),
    (Find-QueryJar 'org.jetbrains.intellij.deps/trove4j/1.0.20200330'),$queryAnnotations,$queryCoroutines)-join ';'
$queryRuntime=@($queryStd,$queryCoroutines,$queryAnnotations)-join ';'
$queryProduction=Join-Path $queryRepo 'AndroidApp/app/src/main/java/com/liuzhuan/app'
$querySources=@('TransferTasks.kt','TransferTaskStates.kt','TransferTaskQuery.kt')|ForEach-Object{Join-Path $queryProduction $_}
$querySources+=Join-Path $PSScriptRoot 'TransferQueryCheck.kt'
New-Item -ItemType Directory -Force -Path $OutputDirectory|Out-Null
$querySources|ForEach-Object{Get-FileHash -LiteralPath $_}|Select-Object Path,Hash|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $OutputDirectory 'source-hashes.json')
$queryJar=Join-Path $OutputDirectory 'query-checks.jar'
& F:/jdk-21.0.12+8/bin/java.exe -cp $queryCompiler org.jetbrains.kotlin.cli.jvm.K2JVMCompiler -no-stdlib -no-reflect -classpath $queryRuntime -d $queryJar @querySources 2>&1|Tee-Object -FilePath (Join-Path $OutputDirectory 'compile.log')
if($LASTEXITCODE -ne 0){throw 'Production transfer query compilation failed'}
& F:/jdk-21.0.12+8/bin/java.exe -cp "$queryJar;$queryRuntime" TransferQueryCheckKt 2>&1|Tee-Object -FilePath (Join-Path $OutputDirectory 'checks.log')
if($LASTEXITCODE -ne 0){throw 'Production transfer query checks failed'}
Write-Output "Evidence: $OutputDirectory"

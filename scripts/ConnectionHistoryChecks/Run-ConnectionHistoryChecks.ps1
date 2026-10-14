param([string]$OutputDirectory='F:/appdata/Temp/liuzhuan-connection-history-checks')
$ErrorActionPreference='Stop'
$historyRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$historyJava='F:/jdk-21.0.12+8/bin/java.exe'
$historyCache='F:/gradle-home/caches/modules-2/files-2.1'
function Find-HistoryJar([string]$Relative) {
    $historyDependency=Get-ChildItem -LiteralPath (Join-Path $historyCache $Relative) -Recurse -Filter '*.jar' | Select-Object -First 1
    if(-not $historyDependency){throw "Cached dependency missing: $Relative"}
    return $historyDependency.FullName
}
$historyStdlib=Find-HistoryJar 'org.jetbrains.kotlin/kotlin-stdlib/2.0.21'
$historyAnnotations=Find-HistoryJar 'org.jetbrains/annotations/23.0.0'
$historyCompiler=@((Find-HistoryJar 'org.jetbrains.kotlin/kotlin-compiler-embeddable/2.0.21'),$historyStdlib,
    (Find-HistoryJar 'org.jetbrains.kotlin/kotlin-script-runtime/2.0.21'),
    (Find-HistoryJar 'org.jetbrains.kotlin/kotlin-reflect/1.6.10'),
    (Find-HistoryJar 'org.jetbrains.intellij.deps/trove4j/1.0.20200330'),$historyAnnotations,
    (Find-HistoryJar 'org.jetbrains.kotlinx/kotlinx-coroutines-core-jvm/1.9.0')) -join ';'
$historyRuntime=@($historyStdlib,$historyAnnotations,'F:/gradle-8.11.1/lib/groovy-3.0.22.jar','F:/gradle-8.11.1/lib/groovy-json-3.0.22.jar') -join ';'
$historySources=@((Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.kt').FullName)
$historySources+=@('HistoryPasswordCipher.kt','ConnectionProfiles.kt') | ForEach-Object {Join-Path $historyRepo "AndroidApp/app/src/main/java/com/liuzhuan/app/core/$_"}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$historyJar=Join-Path $OutputDirectory 'history-checks.jar'
& $historyJava -cp $historyCompiler org.jetbrains.kotlin.cli.jvm.K2JVMCompiler -no-stdlib -no-reflect -classpath $historyRuntime -d $historyJar @historySources
if($LASTEXITCODE -ne 0){throw 'History checks compile failed'}
& $historyJava -cp "$historyJar;$historyRuntime" com.liuzhuan.checks.HistoryCheckKt
if($LASTEXITCODE -ne 0){throw 'History checks failed'}

param([string]$OutputDirectory='F:/appdata/Temp/liuzhuan-m57-transfer-policy')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../JvmCheckEnvironment.ps1')
$jvm=Get-JvmCheckEnvironment
$policyRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$policyCache=$jvm.Cache
function Policy-Jar([string]$Relative){(Get-ChildItem -LiteralPath (Join-Path $policyCache $Relative) -Filter '*.jar' -Recurse | Select-Object -First 1).FullName}
$policyStd=Policy-Jar 'org.jetbrains.kotlin/kotlin-stdlib/2.0.21'
$policyCor=Policy-Jar 'org.jetbrains.kotlinx/kotlinx-coroutines-core-jvm/1.9.0'
$policyAnn=Policy-Jar 'org.jetbrains/annotations/23.0.0'
$policyRuntime=@($policyStd,$policyCor,$policyAnn)-join $jvm.Separator
$policyCompiler=@((Policy-Jar 'org.jetbrains.kotlin/kotlin-compiler-embeddable/2.0.21'),$policyStd,(Policy-Jar 'org.jetbrains.kotlin/kotlin-script-runtime/2.0.21'),(Policy-Jar 'org.jetbrains.kotlin/kotlin-reflect/1.6.10'),(Policy-Jar 'org.jetbrains.intellij.deps/trove4j/1.0.20200330'),$policyAnn,$policyCor)-join $jvm.Separator
$policyBase=Join-Path $policyRepo 'AndroidApp/app/src/main/java/com/liuzhuan/app'
$policySources=@('TransferPolicy.kt','clipboard/ClipboardEvent.kt','clipboard/ClipboardEventQueue.kt','clipboard/AutomaticClipboardDelivery.kt','core/Hex.kt')|ForEach-Object{Join-Path $policyBase $_}
$policySources+=Join-Path $PSScriptRoot 'TransferPolicyCheck.kt'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
& $jvm.Java -cp $policyCompiler org.jetbrains.kotlin.cli.jvm.K2JVMCompiler -no-stdlib -no-reflect -classpath $policyRuntime -d (Join-Path $OutputDirectory 'policy.jar') @policySources 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'compile.log')
if($LASTEXITCODE -ne 0){throw 'Transfer policy compilation failed'}
& $jvm.Java -cp "$(Join-Path $OutputDirectory 'policy.jar')$($jvm.Separator)$policyRuntime" com.liuzhuan.app.TransferPolicyCheckKt 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'checks.log')
if($LASTEXITCODE -ne 0){throw 'Transfer policy checks failed'}

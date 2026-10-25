function Get-JvmCheckEnvironment {
    param([string]$JavaHome=$env:JAVA_HOME,[string]$GradleUserHome=$env:GRADLE_USER_HOME,[string]$GradleHome=$env:GRADLE_HOME)
    if(!$JavaHome){$JavaHome='F:/jdk-21.0.12+8'}
    if(!$GradleUserHome){$GradleUserHome='F:/gradle-home'}
    $java=Join-Path $JavaHome 'bin/java';if(Test-Path -LiteralPath ($java+'.exe')){$java+='.exe'}
    if(!(Test-Path -LiteralPath $java)){throw 'Set JAVA_HOME to an existing JDK 21 directory.'}
    if(!$GradleHome -and (Test-Path -LiteralPath 'F:/gradle-8.11.1')){$GradleHome='F:/gradle-8.11.1'}
    $groovyJson=if($GradleHome){Get-ChildItem (Join-Path $GradleHome 'lib') -Filter 'groovy-json-*.jar' | Select-Object -First 1}
    if(!$groovyJson){$groovyJson=Get-ChildItem (Join-Path $GradleUserHome 'wrapper/dists') -Recurse -Filter 'groovy-json-*.jar' | Select-Object -First 1}
    if(!$groovyJson){throw 'Build Android once with the Gradle wrapper to prepare its Groovy runtime.'}
    $groovy=Get-ChildItem $groovyJson.DirectoryName -Filter 'groovy-*.jar' | Where-Object {$_.Name -match '^groovy-[0-9]'} | Select-Object -First 1
    if(!$groovy){throw 'Gradle Groovy core runtime is missing.'}
    @{Java=$java;Cache=(Join-Path $GradleUserHome 'caches/modules-2/files-2.1');Groovy=$groovy.FullName;GroovyJson=$groovyJson.FullName;Separator=[IO.Path]::PathSeparator}
}

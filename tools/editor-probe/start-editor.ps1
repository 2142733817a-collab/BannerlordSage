param(
    [Parameter(Mandatory=$true)][string]$SessionPath,
    [string]$RuntimeDirectory,
    [string[]]$ModuleNames=@('Native','BannerlordSage.EditorProbe')
)
$ErrorActionPreference='Stop'
$taskSessionPath=[IO.Path]::GetFullPath($SessionPath)
if((Get-Item -LiteralPath $taskSessionPath).Length -gt 16384){throw 'Oversized editor session.'}
$taskConfig=Get-Content -LiteralPath $taskSessionPath -Raw | ConvertFrom-Json
if([string]::IsNullOrWhiteSpace($taskConfig.token)){throw 'Missing local bridge credential.'}
$taskSceneName=Split-Path -Leaf ([string]$taskConfig.sceneDir).TrimEnd('\','/')
if($taskSceneName -notmatch '^[A-Za-z][A-Za-z0-9_]{0,79}$'){throw 'Invalid bound scene name.'}
if(-not $RuntimeDirectory){$RuntimeDirectory=[string]$taskConfig.gameDir}
$taskEditorDir=Join-Path ([IO.Path]::GetFullPath($RuntimeDirectory)) 'bin\Win64_Shipping_wEditor'
$taskExe=Join-Path $taskEditorDir 'Bannerlord.exe'
if(-not (Test-Path -LiteralPath $taskExe)){throw 'Editor executable is missing.'}
if(Get-Process -Name Bannerlord,Bannerlord.Native,TaleWorlds.MountAndBlade.Launcher -ErrorAction SilentlyContinue){
    throw 'An editor/game/launcher is already running. Save and close it before launching another bridge instance.'
}
foreach($taskModule in $ModuleNames){
    if($taskModule -notmatch '^[A-Za-z][A-Za-z0-9_.]{0,79}$' -or
       -not (Test-Path -LiteralPath (Join-Path $RuntimeDirectory ('Modules\'+$taskModule+'\SubModule.xml')))){
        throw 'A selected module name or manifest is invalid.'
    }
}
if('BannerlordSage.EditorProbe' -notin $ModuleNames){throw 'Select the bridge module.'}
$taskEnv=@{
    BANNERSAGE_EDITOR_PROBE_TOKEN=[string]$taskConfig.token
    BANNERSAGE_EDITOR_PROBE_PORT=[string]$taskConfig.port
    BANNERSAGE_EDITOR_PROBE_SCENE_DIR=[string]$taskConfig.sceneDir
    BANNERSAGE_EDITOR_PROBE_SCENE_NAME=$taskSceneName
    BANNERSAGE_EDITOR_PROBE_OUTPUT=[string]$taskConfig.outputDir
}
$taskOldEnv=@{}
try{
    foreach($taskKey in $taskEnv.Keys){
        $taskOldEnv[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey,'Process')
        [Environment]::SetEnvironmentVariable($taskKey,$taskEnv[$taskKey],'Process')
    }
    $taskModuleArgument='_MODULES_*'+($ModuleNames -join '*')+'*_MODULES_'
    $taskProcess=Start-Process -FilePath $taskExe -ArgumentList '/singleplayer',$taskModuleArgument -WorkingDirectory $taskEditorDir -WindowStyle Normal -PassThru
    @{pid=$taskProcess.Id;exe=$taskExe;startedAt=[DateTime]::UtcNow.ToString('o')} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskConfig.outputDir 'process.json') -Encoding UTF8
    Write-Output ('Started editor PID '+$taskProcess.Id)
}finally{
    foreach($taskKey in $taskOldEnv.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskOldEnv[$taskKey],'Process')}
}

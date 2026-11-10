param([Parameter(Mandatory=$true)][string]$PayloadRoot,[string]$SourceExe,[switch]$Background)
$ErrorActionPreference='Stop'
if($Background){$env:GRAYDESK_OCR_BACKGROUND='1'}
$env:PSModulePath=(Join-Path $PSHOME 'Modules')+';'+(Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules')
$testRoot=[Environment]::GetEnvironmentVariable('GRAYDESK_COMPAT_TEST_ROOT','Process')
$testing=![string]::IsNullOrWhiteSpace($testRoot)
$reportLines=[Collections.Generic.List[string]]::new()
$reportLines.Add('灰Desk PP-OCR 免安装兼容检测')
$reportLines.Add('时间：'+(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$success=$false;$work=$null;$prime=$null;$probe=$null;$started=$null;$sourceExe=$SourceExe;$stoppedOld=$false
$desktop=if($testing){Join-Path $testRoot 'desktop'}elseif($Background){Join-Path $env:LOCALAPPDATA 'GrayDeskV2\diagnostics\ocr-background'}else{[Environment]::GetFolderPath('DesktopDirectory')}
New-Item -ItemType Directory -Path $desktop -Force | Out-Null
$report=Join-Path $desktop '灰Desk-OCR兼容检测结果.txt'
if(!$testing -and !$Background){
    Add-Type -AssemblyName System.Windows.Forms
    $form=New-Object System.Windows.Forms.Form
    $form.Text='灰Desk · OCR兼容检测';$form.Width=460;$form.Height=165;$form.StartPosition='CenterScreen';$form.ControlBox=$false;$form.TopMost=$true
    $label=New-Object System.Windows.Forms.Label;$label.Left=20;$label.Top=24;$label.Width=410;$label.Height=70;$label.Text='正在检查，请稍候。不会安装或修改系统运行库。'
    $form.Controls.Add($label);$form.Show();[System.Windows.Forms.Application]::DoEvents()
}
function Stage([string]$message){if(!$testing -and !$Background){$label.Text=$message;[System.Windows.Forms.Application]::DoEvents()}}
function Stop-Owned([Diagnostics.Process]$process){if($process -and !$process.HasExited){$process.Kill();[void]$process.WaitForExit(5000)}}
function QuotePs([string]$value){return "'"+$value.Replace("'","''")+"'"}
function NewStart([string]$file,[string]$arguments,[string]$bundle,[string]$private){
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$file;$start.Arguments=$arguments;$start.WorkingDirectory=Split-Path -Parent $file;$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR']=$bundle
    $start.EnvironmentVariables['GRAYDESK_DIAGNOSTIC_ROOT']=$private
    return $start
}
try{
    if(![Environment]::Is64BitOperatingSystem){throw '需要64位Windows。'}
    $runtimeManifest=Get-Content -LiteralPath (Join-Path $PayloadRoot 'runtime-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if(@($runtimeManifest).Count -ne 12){throw '兼容检测包的运行库清单不完整。'}
    foreach($entry in $runtimeManifest){
        if($entry.Name -notmatch '^[A-Za-z0-9_]+\.dll$'){throw '运行库清单格式错误。'}
        $file=Join-Path (Join-Path $PayloadRoot 'runtime') $entry.Name
        if(!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.SHA256){throw '兼容检测包的运行库校验失败。'}
    }
    $expectedHash=(Get-Content -LiteralPath (Join-Path $PayloadRoot 'app.sha256') -Raw).Trim()
    if($expectedHash -notmatch '^[A-Fa-f0-9]{64}$'){throw '程序校验清单错误。'}
    Stage '正在检查灰Desk版本和模型文件…'
    if($testing){$sourceExe=Join-Path $testRoot 'app\灰Desk.exe';$localRoot=Join-Path $testRoot 'local'}else{
        $key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GrayDesk'
        $entry=Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if(!$sourceExe -and $entry.InstallLocation){$sourceExe=Join-Path $entry.InstallLocation '灰Desk.exe'}
        if(!$sourceExe -or !(Test-Path -LiteralPath $sourceExe -PathType Leaf)){
            $processes=@(Get-CimInstance Win32_Process -Filter "Name='灰Desk.exe'" -ErrorAction SilentlyContinue)
            foreach($process in $processes){if($process.ExecutablePath -and (Test-Path -LiteralPath $process.ExecutablePath)){
                $version=(Get-Item -LiteralPath $process.ExecutablePath).VersionInfo.ProductVersion
                if($version -eq '2.2.0-test.20261029.1'){$sourceExe=$process.ExecutablePath;break}
            }}
        }
        $localRoot=Join-Path $env:LOCALAPPDATA 'GrayDeskV2'
    }
    if(!$sourceExe -or !(Test-Path -LiteralPath $sourceExe -PathType Leaf)){throw '未找到普通版灰Desk。请先打开它，再运行本工具。'}
    $sourceExe=[IO.Path]::GetFullPath($sourceExe);$appDir=Split-Path -Parent $sourceExe
    $version=(Get-Item -LiteralPath $sourceExe).VersionInfo.ProductVersion
    $reportLines.Add('程序版本：'+$version)
    if($version -ne '2.2.0-test.20261029.1' -or (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash -ne $expectedHash){throw '程序不是已核验的普通版 .20261029.1 版本，未作任何修改。'}
    $modelHashes=[ordered]@{
        'Models\ch_PP-OCRv5_rec_mobile.onnx'='5825FC7EBF84AE7A412BE049820B4D86D77620F204A041697B0494669B1742C5'
        'Models\ppocrv5_dict.txt'='D1979E9F794C464C0D2E0B70A7FE14DD978E9DC644C0E71F14158CDF8342AF1B'
        'Models\v5\ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx'='54379AE5174D026780215FC748A7F31910DEE36818E63D49E17DC598ECC82DF7'
        'Models\v5\ch_PP-OCRv5_mobile_det.onnx'='4D97C44A20D30A81AAD087D6A396B08F786C4635742AFC391F6621F5C6AE78AE'
    }
    foreach($entry in $modelHashes.GetEnumerator()){
        $file=Join-Path $appDir $entry.Key
        if(!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Value){throw ('模型缺失或损坏：'+$entry.Key+'；未修改原程序。')}
    }
    $reportLines.Add('模型与程序文件校验：通过')
    $tempBase=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    $work=[IO.Path]::GetFullPath((Join-Path $tempBase ('GrayDesk-OCR-Compat-'+[Guid]::NewGuid().ToString('N'))))
    if(!$work.StartsWith($tempBase,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($work) -notmatch '^GrayDesk-OCR-Compat-[0-9a-f]{32}$'){throw '临时目录校验失败。'}
    New-Item -ItemType Directory -Path (Join-Path $work 'app') -Force | Out-Null
    Stage '正在临时目录复制程序和模型，不会修改原程序…'
    Copy-Item -LiteralPath $sourceExe -Destination (Join-Path $work 'app\灰Desk.exe')
    Copy-Item -LiteralPath (Join-Path $appDir 'Models') -Destination (Join-Path $work 'app\Models') -Recurse
    $testExe=Join-Path $work 'app\灰Desk.exe';$bundle=Join-Path $work 'bundle';$private=Join-Path $work 'private'
    Stage '正在准备隔离运行库，不会更改系统…'
    $primeStart=NewStart $testExe '--ocr-worker-test-hang' $bundle $private
    $primeStart.RedirectStandardInput=$true;$prime=[Diagnostics.Process]::Start($primeStart)
    $native=$null
    for($i=0;$i -lt 200;$i++){
        Start-Sleep -Milliseconds 100
        $native=Get-ChildItem -LiteralPath $bundle -Filter 'onnxruntime.dll' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if($native){break};if($prime.HasExited){throw ('兼容环境准备失败，退出码：'+$prime.ExitCode)}
        if(!$testing -and !$Background){[System.Windows.Forms.Application]::DoEvents()}
    }
    if(!$native){throw '兼容环境准备超时。'}
    foreach($entry in $runtimeManifest){Copy-Item -LiteralPath (Join-Path (Join-Path $PayloadRoot 'runtime') $entry.Name) -Destination $native.DirectoryName -Force}
    Stop-Owned $prime;$prime.Dispose();$prime=$null
    Stage '正在用测试图片验证PP-OCR，预计需要几十秒…'
    $probeOutput=Join-Path $work 'ocr-result';New-Item -ItemType Directory -Path $probeOutput -Force | Out-Null
    $probeStart=NewStart $testExe ('--verify-ocr-isolation "'+$probeOutput+'"') $bundle $private
    $probe=[Diagnostics.Process]::Start($probeStart);$clock=[Diagnostics.Stopwatch]::StartNew()
    while(!$probe.HasExited -and $clock.Elapsed.TotalSeconds -lt 120){Start-Sleep -Milliseconds 200;if(!$testing -and !$Background){[System.Windows.Forms.Application]::DoEvents()}}
    if(!$probe.HasExited){Stop-Owned $probe;throw '兼容测试超时，未修改原程序。'}
    $resultPath=Join-Path $probeOutput 'OCR隔离验收.json'
    if(!(Test-Path -LiteralPath $resultPath)){throw ('兼容测试没有返回结果，退出码：'+$probe.ExitCode)}
    $ocr=Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $reportLines.Add('隔离运行库PP-OCR测试：'+$(if($ocr.Passed){'通过'}else{'失败'}))
    foreach($test in $ocr.Tests){$reportLines.Add('测试 '+$test.Name+'：'+$(if($test.Passed){'通过'}else{'失败'}))}
    if(!$ocr.Passed -or $probe.ExitCode -ne 0){
        $logDir=Join-Path $private 'logs';if(Test-Path -LiteralPath $logDir){$last=Get-ChildItem -LiteralPath $logDir -Filter '*.log' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if($last){foreach($line in @(Get-Content -LiteralPath $last.FullName -Tail 120 -Encoding UTF8)){if($line -match 'OCR工作进程失败 kind=([A-Za-z]+) type=([A-Za-z.]+) exit=(0x[0-9A-Fa-f]+|running)'){$reportLines.Add('工作进程错误：'+$Matches[1]+' / '+$Matches[2]+' / '+$Matches[3])}}}}
        throw '隔离运行库未通过测试，原程序没有改动。请将桌面的结果文件发回排查。'
    }
    Stage '检测通过，正在生成免安装兼容启动入口…'
    $id=[Guid]::NewGuid().ToString('N');$cacheBase=[IO.Path]::GetFullPath((Join-Path $localRoot 'CompatCache'));$stable=[IO.Path]::GetFullPath((Join-Path $cacheBase ('run-'+$id)))
    if(!$stable.StartsWith($cacheBase.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($stable) -notmatch '^run-[0-9a-f]{32}$'){throw '兼容缓存路径校验失败。'}
    New-Item -ItemType Directory -Path $stable -Force | Out-Null
    Copy-Item -LiteralPath $bundle -Destination (Join-Path $stable 'bundle') -Recurse
    $stableBundle=Join-Path $stable 'bundle';$launchScript=Join-Path $stable 'launch.ps1'
    if($Background){
        [ordered]@{ExecutableHash=$expectedHash;Bundle=$stableBundle}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $localRoot 'ocr-compat.json') -Encoding UTF8
        $reportLines.Add('后台修复通过；已保存兼容缓存，原进程无需退出。');$success=$true;exit 0
    }
    $content='$ErrorActionPreference=''Stop''' + "`r`n" + '$env:PSModulePath=(Join-Path $PSHOME ''Modules'')+'';''+(Join-Path $env:ProgramFiles ''WindowsPowerShell\Modules'')' + "
`n" + '$exe='+ (QuotePs $sourceExe) + "`r`n" + '$bundle='+ (QuotePs $stableBundle) + "`r`n" +
        'if(!(Test-Path -LiteralPath $exe) -or (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne '+(QuotePs $expectedHash)+'){Add-Type -AssemblyName System.Windows.Forms;[void][System.Windows.Forms.MessageBox]::Show(''灰Desk程序已变更，请重新运行OCR兼容检测。'');exit 1}' + "`r`n" +
        '$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=$bundle' + "`r`n" + 'Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)' + "`r`n"
    [IO.File]::WriteAllText($launchScript,$content,(New-Object Text.UTF8Encoding($true)))
    $launcher=Join-Path $desktop ('灰Desk-OCR兼容启动-'+(Get-Date -Format 'HHmmss')+'.cmd')
    if($testing){$cmd='@echo off'+"`r`n"+'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+$launchScript+'"'+"`r`n"}
    else{$cmd='@echo off'+"`r`n"+'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%LOCALAPPDATA%\GrayDeskV2\CompatCache\run-'+$id+'\launch.ps1"'+"`r`n"}
    [IO.File]::WriteAllText($launcher,$cmd,[Text.Encoding]::ASCII)
    [ordered]@{ExecutableHash=$expectedHash;Bundle=$stableBundle}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $localRoot 'ocr-compat.json') -Encoding UTF8
    $reportLines.Add('已生成兼容启动入口：'+[IO.Path]::GetFileName($launcher))
    # Stop only the exact previously identified theme executable, not remote-control software.
    foreach($running in @(Get-CimInstance Win32_Process -Filter "Name='灰Desk.exe'" | Where-Object {$_.ExecutablePath -eq $sourceExe})){
        Stop-Process -Id $running.ProcessId -Force -ErrorAction Stop;$stoppedOld=$true
    }
    $start=NewStart $sourceExe '' $stableBundle (Join-Path $work 'private')
    if(!$testing -and !$Background){$start.EnvironmentVariables.Remove('GRAYDESK_DIAGNOSTIC_ROOT')}
    $start.CreateNoWindow=$false;$started=[Diagnostics.Process]::Start($start)
    Start-Sleep -Seconds 4;$started.Refresh()
    if($started.HasExited){throw '兼容测试通过，但自动重启失败；可使用桌面兼容启动入口重试。'}
    $reportLines.Add('兼容模式已启动，系统运行库和旧程序文件未被修改。')
    if($testing){Stop-Owned $started;$started.Dispose();$started=$null}
    $success=$true
}catch{
    $reportLines.Add('处理结果：未完成。'+$_.Exception.Message)
    if($stoppedOld -and (!$started -or $started.HasExited)){
        try{Start-Process -FilePath $sourceExe -WorkingDirectory (Split-Path -Parent $sourceExe) | Out-Null;$reportLines.Add('已尝试恢复原程序。')}catch{$reportLines.Add('原程序恢复失败，请手动从原快捷方式打开。')}
    }
}finally{
    try{Stop-Owned $prime;Stop-Owned $probe}catch{}
    if($work){
        $base=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\';$resolved=[IO.Path]::GetFullPath($work)
        if($resolved.StartsWith($base,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -match '^GrayDesk-OCR-Compat-[0-9a-f]{32}$'){
            Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    [IO.File]::WriteAllLines($report,$reportLines,(New-Object Text.UTF8Encoding($true)))
    if(!$testing -and !$Background){$form.Close();$message=if($success){'兼容测试通过，已启动灰Desk。普通版下次启动也会沿用已验证的兼容环境。'}else{'兼容处理未完成，原程序未改动。请将桌面的“灰Desk-OCR兼容检测结果.txt”发回。'};[void][System.Windows.Forms.MessageBox]::Show($message,'灰Desk OCR兼容检测')}
}
if(!$success){exit 1}

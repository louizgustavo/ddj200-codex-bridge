param([ValidateSet('Build','Test','Publish')][string]$Action='Test')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$bridge=Join-Path $root 'src\DDJ200.Bridge\DDJ200.csproj'
$tray=Join-Path $root 'src\DDJ200.Tray\DDJ200.Tray.csproj'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DDJ_BRIDGE_PROFILE=Join-Path $root 'src\DDJ200.Bridge\config\controller-profile.json'
$env:DDJ_BRIDGE_DATA=Join-Path $root 'artifacts\test-data'
$env:DDJ_CODEX_CONFIG=Join-Path $root 'src\DDJ200.Bridge\config\test-codex-config.toml'
New-Item -ItemType Directory -Path $env:DDJ_BRIDGE_DATA -Force | Out-Null

dotnet build $bridge -c Release --nologo
if($LASTEXITCODE-ne0){exit $LASTEXITCODE}
dotnet build $tray -c Release --nologo
if($LASTEXITCODE-ne0){exit $LASTEXITCODE}
if($Action-eq'Build'){return}

$exe=Join-Path $root 'src\DDJ200.Bridge\bin\Release\net8.0\ddj200.exe'
foreach($suite in @('surface12-self-test','analog-surface-self-test','bounded-log-self-test','micro-self-test')){
  & $exe $suite
  if($LASTEXITCODE-ne0){exit $LASTEXITCODE}
}
if($Action-ne'Publish'){return}

$stage=Join-Path $root 'stage\app'
$publish=Join-Path $root 'publish'
$artifacts=Join-Path $root 'artifacts'
foreach($target in @($stage,$publish,$artifacts)){
  $full=[IO.Path]::GetFullPath($target)
  if(-not $full.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw "Unsafe build path: $full"}
  if(Test-Path -LiteralPath $full){Remove-Item -LiteralPath $full -Recurse -Force}
  New-Item -ItemType Directory -Path $full -Force|Out-Null
}
$bridgePublish=Join-Path $publish 'bridge'
$trayPublish=Join-Path $publish 'tray'
dotnet publish $bridge -c Release -r win-x64 --self-contained true --nologo -o $bridgePublish -p:RestoreSources=https://api.nuget.org/v3/index.json -p:DebugType=None -p:DebugSymbols=false
if($LASTEXITCODE-ne0){exit $LASTEXITCODE}
dotnet publish $tray -c Release -r win-x64 --self-contained true --nologo -o $trayPublish -p:RestoreSources=https://api.nuget.org/v3/index.json -p:DebugType=None -p:DebugSymbols=false
if($LASTEXITCODE-ne0){exit $LASTEXITCODE}
Copy-Item -Path (Join-Path $trayPublish '*') -Destination $stage -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $stage 'bridge') -Force|Out-Null
Copy-Item -Path (Join-Path $bridgePublish '*') -Destination (Join-Path $stage 'bridge') -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $stage 'config') -Force | Out-Null
Copy-Item -LiteralPath $env:DDJ_BRIDGE_PROFILE -Destination (Join-Path $stage 'config\controller-profile.json') -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md'),(Join-Path $root 'CHANGELOG.md'),(Join-Path $root 'SECURITY.md'),(Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $stage -Force
Copy-Item -LiteralPath 'C:\Program Files\dotnet\LICENSE.txt' -Destination (Join-Path $stage 'DOTNET-LICENSE.txt') -Force
Copy-Item -LiteralPath 'C:\Program Files\dotnet\ThirdPartyNotices.txt' -Destination (Join-Path $stage 'DOTNET-ThirdPartyNotices.txt') -Force

$manifest=Get-ChildItem -LiteralPath $stage -Recurse -File|Sort-Object FullName|ForEach-Object{
  [pscustomobject]@{path=$_.FullName.Substring($stage.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
[pscustomobject]@{product='DDJ-200 Codex Bridge';version='1.0.0';architecture='win-x64';signed=$false;files=$manifest}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding utf8

$isccCandidates=@($env:ISCC_EXE,(Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'))|Where-Object{$_ -and (Test-Path -LiteralPath $_)}
$iscc=$isccCandidates|Select-Object -First 1
if(-not $iscc){throw 'Inno Setup 6.7.3 ISCC.exe not found. Install it or set ISCC_EXE to its absolute path.'}
$isccHash=(Get-FileHash -LiteralPath $iscc -Algorithm SHA256).Hash.ToLowerInvariant()
if($isccHash-ne'0a8757031b33777e4c9cbffee40f11a5062b36d25cbe144c1db73b6102b80ad7'){throw "The verified Inno Setup 6.7.3 compiler is required; ISCC.exe hash was $isccHash"}
& $iscc (Join-Path $root 'installer\DDJ200-Codex-Bridge.iss')
if($LASTEXITCODE-ne0){exit $LASTEXITCODE}
$setup=Join-Path $artifacts 'DDJ200-Codex-Bridge-1.0.0-Setup.exe'
if(-not(Test-Path -LiteralPath $setup)){throw 'Expected installer was not generated'}
$setupHash=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$setupHash  DDJ200-Codex-Bridge-1.0.0-Setup.exe"|Set-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS.txt') -Encoding ascii
$signature=Get-AuthenticodeSignature -LiteralPath $setup
[pscustomobject]@{file=(Split-Path -Leaf $setup);status=$signature.Status.ToString();signatureType=$signature.SignatureType.ToString();signed=($signature.Status-eq'Valid')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $artifacts 'SIGNATURE-STATUS.json') -Encoding utf8
[pscustomobject]@{installer=$setup;sha256=$setupHash;signed=($signature.Status-eq'Valid');stageFiles=$manifest.Count}

param([string]$UsbIpInstaller)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$directory=Join-Path $root '.deps'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$target=Join-Path $directory 'USBip-0.9.8.0-x64.exe'
if($UsbIpInstaller){Copy-Item -LiteralPath $UsbIpInstaller -Destination $target -Force}
elseif(-not(Test-Path -LiteralPath $target)){
  Invoke-WebRequest 'https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.8.0/USBip-0.9.8.0-x64.exe' -OutFile $target
}
$expected='81f426741f7ee2ed991febe24a22daca8400b6ae2f171054e3fb404897e15d39'
if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()-ne$expected){throw 'USBip SHA-256 mismatch; do not package this file.'}
$signature=Get-AuthenticodeSignature -LiteralPath $target
if($signature.Status-ne'Valid'){throw 'USBip Authenticode validation failed.'}
Write-Output "Verified USBip 0.9.8.0 x64: $target (not installed)"

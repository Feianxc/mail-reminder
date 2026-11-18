[CmdletBinding()]
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$projectRoot = $PSScriptRoot
$releasePath = Join-Path $projectRoot 'release\MailReminder-1.3.0'
$checkPath = Join-Path $projectRoot '.build'
$transportExe = Join-Path $PSScriptRoot '.build\TransportTests.exe'
$uiExe = Join-Path $PSScriptRoot '.build\UiTests.exe'
$dotnetCommand = Get-Command -Name dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (!$dotnetCommand) { throw 'Build requires a .NET SDK. Install the SDK and ensure dotnet is available on PATH.' }
$dotnetPath = $dotnetCommand.Source
$installedSdks = @(& $dotnetPath --list-sdks)
$sdkExit = $LASTEXITCODE
if ($sdkExit -ne 0) { throw "dotnet --list-sdks failed with exit code $sdkExit" }
$compilerPath = $installedSdks | ForEach-Object {
    if ($_ -match '^(?<version>\d+\.\d+\.\d+(?:-\S+)?)\s+\[(?<root>.+)\]$') {
        [pscustomobject]@{
            Version = [version]($Matches.version -replace '-.*$', '')
            Path = Join-Path (Join-Path $Matches.root $Matches.version) 'Roslyn\bincore\csc.dll'
        }
    }
} | Sort-Object Version -Descending | Where-Object { Test-Path -LiteralPath $_.Path -PathType Leaf } |
    Select-Object -First 1 -ExpandProperty Path
if (!$compilerPath) { throw 'No C# compiler was found in the installed .NET SDKs. Install a complete .NET SDK.' }
$referencePath = @(${env:ProgramFiles(x86)}, $env:ProgramFiles) | Where-Object { $_ } | Select-Object -Unique |
    ForEach-Object { Join-Path $_ 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2' } |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ 'mscorlib.dll') -PathType Leaf } | Select-Object -First 1
if (!$referencePath) {
    throw 'Build requires the .NET Framework 4.7.2 Developer Pack (targeting pack). End users only need .NET Framework 4.7.2 or later.'
}
$nodePath = $null
if ($Test) {
    $nodeCommand = Get-Command -Name node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (!$nodeCommand) { throw 'Relay tests require Node.js 22 or later. Install Node.js and ensure node is available on PATH.' }
    $nodePath = $nodeCommand.Source
    $nodeVersion = (& $nodePath --version | Out-String).Trim()
    $nodeExit = $LASTEXITCODE
    if ($nodeExit -ne 0 -or $nodeVersion -notmatch '^v(?<major>\d+)\.' -or [int]$Matches.major -lt 22) {
        throw 'Relay tests require Node.js 22 or later. Check the node executable selected on PATH.'
    }
}
New-Item -ItemType Directory -Path $releasePath, $checkPath -Force | Out-Null
$references = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.Xml.dll', 'System.Net.Http.dll', 'System.IO.Compression.dll', 'WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll', 'System.Xaml.dll') |
    ForEach-Object { '/reference:' + (Join-Path $referencePath $_) }
$common = @('/nologo', '/noconfig', '/nostdlib+', '/langversion:7.3', '/optimize+', '/warn:4', '/platform:anycpu') + $references

function Invoke-Compiler {
    param([string[]]$CompilerArguments)
    & $dotnetPath $compilerPath @common @CompilerArguments
    $compilerExit = $LASTEXITCODE
    if ($compilerExit -ne 0) { throw "C# compiler failed with exit code $compilerExit" }
}

$iconPath = Join-Path $checkPath 'OfficeSignal.ico'
Add-Type -AssemblyName System.Drawing
$bitmap = [System.Drawing.Bitmap]::new(32, 32)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)
$brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(32, 91, 208))
$graphics.FillEllipse($brush, 0, 0, 32, 32)
$pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 2.5)
$graphics.DrawArc($pen, 9, 7, 14, 18, 180, 180)
$graphics.DrawLine($pen, 9, 16, 8, 23)
$graphics.DrawLine($pen, 23, 16, 24, 23)
$graphics.DrawLine($pen, 8, 23, 24, 23)
$graphics.DrawArc($pen, 13, 23, 6, 5, 0, 180)
$icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
$iconStream = [System.IO.File]::Create($iconPath)
try { $icon.Save($iconStream) }
finally { $iconStream.Dispose(); $icon.Dispose(); $pen.Dispose(); $brush.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }

$exePath = Join-Path $releasePath '邮件提醒助手.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$resources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'ui') -Filter '*.xaml' | ForEach-Object { '/resource:' + $_.FullName + ',OfficeSignal.UI.' + $_.Name })
$assetsPath = Join-Path $projectRoot 'assets'
if (Test-Path -LiteralPath $assetsPath) {
    $resources += @(Get-ChildItem -LiteralPath $assetsPath -Filter '*.png' | ForEach-Object { '/resource:' + $_.FullName + ',OfficeSignal.Assets.' + $_.Name })
}
Invoke-Compiler (@('/target:winexe', ('/out:' + $exePath), ('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')), ('/win32icon:' + $iconPath)) + $sources + $resources)
Copy-Item -LiteralPath (Join-Path $projectRoot 'OfficeSignal.exe.config') -Destination ($exePath + '.config')
$teamPath = Join-Path $projectRoot 'team.json'
if (!(Test-Path -LiteralPath $teamPath)) {
    $roomKeyBytes = [byte[]]::new(32)
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($roomKeyBytes) } finally { $random.Dispose() }
    $teamConfig = [ordered]@{ RoomName = '办公室'; BroadcastAddress = '192.168.20.255'; Port = 45820; RoomKey = [Convert]::ToBase64String($roomKeyBytes) }
    $teamConfig | ConvertTo-Json | Set-Content -LiteralPath $teamPath -Encoding utf8
}
$teamReadback = Get-Content -LiteralPath $teamPath -Raw | ConvertFrom-Json
if ([Convert]::FromBase64String($teamReadback.RoomKey).Length -ne 32 -or $teamReadback.Port -ne 45820) { throw 'Invalid team configuration.' }
Copy-Item -LiteralPath $teamPath -Destination (Join-Path $releasePath 'team.json')
Copy-Item -LiteralPath (Join-Path $projectRoot '使用说明.txt') -Destination (Join-Path $releasePath '使用说明.txt')

if ($Test) {
    Invoke-Compiler @('/target:exe', ('/out:' + $transportExe), (Join-Path $projectRoot 'src\LanNode.cs'), (Join-Path $projectRoot 'tests\TransportTests.cs'))
    & $transportExe
    $transportExit = $LASTEXITCODE
    if ($transportExit -ne 0) { throw "Transport tests failed with exit code $transportExit" }
    $updateExe = Join-Path $checkPath 'UpdateTests.exe'
    Invoke-Compiler @('/target:exe', '/main:OfficeSignal.UpdateTests', ('/out:' + $updateExe), (Join-Path $projectRoot 'src\OnlineUpdate.cs'), (Join-Path $projectRoot 'tests\UpdateTests.cs'))
    & $updateExe $checkPath
    $updateExit = $LASTEXITCODE
    if ($updateExit -ne 0) { throw "Update tests failed with exit code $updateExit" }
    $relayExe = Join-Path $checkPath 'RelayClientTests.exe'
    Invoke-Compiler @('/target:exe', '/main:RelayClientTests', ('/out:' + $relayExe), (Join-Path $projectRoot 'src\LanNode.cs'), (Join-Path $projectRoot 'src\AppConfig.cs'), (Join-Path $projectRoot 'src\RelayClient.cs'), (Join-Path $projectRoot 'tests\RelayClientTests.cs'))
    & $relayExe $projectRoot $nodePath
    $relayExit = $LASTEXITCODE
    if ($relayExit -ne 0) { throw "Relay client tests failed with exit code $relayExit" }
    $resourceExe = Join-Path $checkPath 'ThemeResourceTests.exe'
    Invoke-Compiler (@('/target:exe', '/main:OfficeSignal.ThemeResourceTests', ('/out:' + $resourceExe)) + $sources + $resources + (Join-Path $projectRoot 'tests\ThemeResourceTests.cs'))
    Push-Location -LiteralPath $projectRoot
    try {
        & '.\.build\ThemeResourceTests.exe'
        $resourceExit = $LASTEXITCODE
    }
    finally { Pop-Location }
    if ($resourceExit -ne 0) { throw "Theme resource tests failed with exit code $resourceExit" }
    Invoke-Compiler (@('/target:exe', '/main:OfficeSignal.UiTests', ('/out:' + $uiExe)) + $sources + $resources + (Join-Path $projectRoot 'tests\UiTests.cs'))
    Copy-Item -LiteralPath (Join-Path $projectRoot 'OfficeSignal.exe.config') -Destination ($uiExe + '.config')
    & $uiExe $checkPath
    $uiExit = $LASTEXITCODE
    if ($uiExit -ne 0) { throw "UI tests failed with exit code $uiExit" }
}
$zipPath = Join-Path $projectRoot 'release\邮件提醒助手-1.3.0.zip'
$packageFiles = @($exePath, ($exePath + '.config'), (Join-Path $releasePath 'team.json'), (Join-Path $releasePath '使用说明.txt'))
Compress-Archive -LiteralPath $packageFiles -DestinationPath $zipPath -Force
Write-Output "Build pass. Portable application and ZIP are in $releasePath"

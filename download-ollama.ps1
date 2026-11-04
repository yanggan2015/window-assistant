# Background download of OllamaSetup.exe (resumable, multi-mirror, auto-retry). ASCII only.
$curl = "C:\Windows\System32\curl.exe"
$dest = Join-Path $env:TEMP "OllamaSetup.exe"
$log  = Join-Path $env:TEMP "ollama-dl.log"
$expected = 1675999728

$mirrors = @(
  "https://gh-proxy.com/https://github.com/ollama/ollama/releases/download/v0.40.14/OllamaSetup.exe",
  "https://ghfast.top/https://github.com/ollama/ollama/releases/download/v0.40.14/OllamaSetup.exe",
  "https://github.com/ollama/ollama/releases/download/v0.40.14/OllamaSetup.exe"
)

function Log($m){ Add-Content -LiteralPath $log -Value ("{0}  {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $m) }

Log "start dest=$dest expected=$expected"
$round = 0
while($true){
  $sz = if(Test-Path -LiteralPath $dest){ (Get-Item -LiteralPath $dest).Length } else { 0 }
  if($sz -ge $expected){ Log "DONE size=$sz"; break }

  $round++
  $made = $false
  foreach($u in $mirrors){
    $sz = if(Test-Path -LiteralPath $dest){ (Get-Item -LiteralPath $dest).Length } else { 0 }
    Log "round=$round url=$u offset=$sz"
    & $curl -s -L -C - --retry 4 --retry-delay 3 --connect-timeout 20 --max-time 300 -o $dest $u
    $code = $LASTEXITCODE
    $newSz = if(Test-Path -LiteralPath $dest){ (Get-Item -LiteralPath $dest).Length } else { 0 }
    Log "curl exit=$code size=$newSz"
    if($newSz -gt $sz){ $made = $true }
    if($code -eq 0 -and $newSz -ge $expected){ break }
  }
  if(-not $made){ Log "no progress; sleep 25s"; Start-Sleep -Seconds 25 }
  Start-Sleep -Seconds 3
}

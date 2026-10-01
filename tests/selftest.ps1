# Regresni test publikovaneho zipfill.exe. Spusteni:
#   powershell -ExecutionPolicy Bypass -File tests/selftest.ps1
# Vytvori testovaci zip v %TEMP%, projde scan/extract/verify a na konci docasna data smaze.
# Navratovy kod = pocet neuspesnych kontrol.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$exe  = Join-Path $PSScriptRoot '..\bin\zipfill.exe'
$base = Join-Path $env:TEMP ("zipfill-selftest-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$dest = Join-Path $base 'dest'
$zipPath = Join-Path $base 'test.zip'
$script:fails = 0

function Check($name, $ok) {
    if ($ok) { Write-Host "PASS  $name" } else { Write-Host "FAIL  $name" -ForegroundColor Red; $script:fails++ }
}
function Run($mode, $target, $report, [string[]]$extra = @(), $zip = $zipPath, [switch]$reportIsFullPath) {
    $r = if ($reportIsFullPath) { $report } else { Join-Path $base $report }
    # PowerShell 5.1 by pri 'Stop' bral stderr nativniho programu jako vyjimku
    $ErrorActionPreference = 'Continue'
    & $exe $mode $zip $target $r @extra 2>$null | Out-Null
    return $LASTEXITCODE
}
function Long($p) { '\\?\' + $p }   # .NET Framework potrebuje prefix pro cesty nad 260 znaku
function ReportFile($report, $file) {
    # kazdy beh zapisuje reporty do vlastni nove podslozky <rezim>-<cas>-<nahodne>
    $runs = @(Get-ChildItem -LiteralPath (Join-Path $base $report) -Directory)
    if ($runs.Count -ne 1) { return '' }
    return Get-Content -LiteralPath (Join-Path $runs[0].FullName $file) -Raw
}
function WriteFile($path, $text) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $text)
}

$nfd = 'Ci' + [char]0x0301 + 'l.txt'        # jak ho ulozi macOS
$nfc = 'C' + [char]0x00ED + 'l.txt'         # jak ho zapise Pruzkumnik / ma git
$deep = (1..10 | ForEach-Object { 'hodne-dlouha-slozka-cislo-{0:D2}' -f $_ }) -join '/'
$longName = ('x' * 236) + '.txt'             # platne jmeno 240 znaku (docasne jmeno nesmi prekrocit limit 255)

try {
    New-Item -ItemType Directory -Path $base | Out-Null

    # --- testovaci zip ---
    $entries = [ordered]@{
        'proj/My project/ProjectSettings/ProjectVersion.txt' = 'm_EditorVersion: 6000.0.0f1'
        "proj/My project/Assets/$nfd"                        = 'diakritika'
        'proj/My project/Assets/new.txt'                     = 'novy'
        'proj/My project/Assets/keep.txt'                    = 'zip verze'
        'proj/My project/Temp/x.tmp'                         = 'temp'
        'proj/My project/Logs/l.log'                         = 'log'
        'proj/.git/index.lock'                               = 'lock'
        'proj/.git/lfs/tmp/123'                              = 'lfs tmp'
        'proj/sub/.DS_Store'                                 = 'ds'
        '__MACOSX/proj/My project/Assets/._new.txt'          = 'resource fork'
        'proj/same.txt'                                      = 'AAAA'
        "proj/$longName"                                     = 'dlouhe jmeno'
        "proj/$deep/file.txt"                                = 'hluboko'
    }
    $zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create', [Text.Encoding]::UTF8)
    try {
        foreach ($k in $entries.Keys) {
            $w = New-Object IO.StreamWriter($zip.CreateEntry($k).Open())
            $w.Write($entries[$k]); $w.Dispose()
        }
        $link = $zip.CreateEntry('proj/link')
        $link.ExternalAttributes = [int](0xA1FF -shl 16)   # unixovy symlink
        $w = New-Object IO.StreamWriter($link.Open()); $w.Write('My project'); $w.Dispose()
    } finally { $zip.Dispose() }

    # --- stav "po Pruzkumniku": neco je, neco chybi, neco lokalne zmenene ---
    WriteFile "$dest\proj\My project\ProjectSettings\ProjectVersion.txt" 'm_EditorVersion: 6000.0.0f1'
    WriteFile "$dest\proj\My project\Assets\$nfc" 'diakritika'
    WriteFile "$dest\proj\My project\Assets\keep.txt" 'puvodni lokalni zmena'   # jina velikost
    WriteFile "$dest\proj\same.txt" 'BBBB'                                      # stejna velikost, jiny obsah

    # A) scan hlasi chybejici soubory
    Check 'scan: kod 1 pri chybejicich souborech' ((Run 'scan' $dest 'rA') -eq 1)
    $sum = ReportFile 'rA' 'scan-summary.txt'
    Check 'scan: chybi presne 3 soubory (new.txt, hluboky soubor, dlouhe jmeno)' ($sum -match 'Chybejici soubory: 3 ')
    Check 'scan: 1 soubor s jinou velikosti' ($sum -match 'jen report, neprepisuje se\): 1')
    Check 'scan: NFD jmeno nalezeno na disku jako NFC' ($sum -match 'na disku nalezena 1, chybi 0')
    $skipCount = if ($sum -match 'Unity Temp/Logs\): (\d+)') { [int]$Matches[1] } else { -1 }
    Check "scan: 5 umyslne preskocenych (Temp, Logs, index.lock, lfs tmp, .DS_Store) - skutecne $skipCount" ($skipCount -eq 5)

    # B) spatna cilova slozka -> extract zablokovan
    Check 'extract do spatne slozky: kod 3' ((Run 'extract' "$dest\proj" 'rB') -eq 3)
    Check 'extract do spatne slozky: nic nezapsano' (-not (Test-Path "$dest\proj\proj"))

    # C) extract do spravne slozky
    Check 'extract: kod 1 (keep.txt se lisi a zustava)' ((Run 'extract' $dest 'rC') -eq 1)
    Check 'extract: new.txt doplnen se spravnym obsahem' ([IO.File]::ReadAllText("$dest\proj\My project\Assets\new.txt") -eq 'novy')
    Check 'extract: soubor s cestou nad 260 znaku doplnen' ([IO.File]::ReadAllText((Long "$dest\proj\$($deep.Replace('/', '\'))\file.txt")) -eq 'hluboko')
    Check 'extract: soubor s 240znakovym jmenem doplnen' ([IO.File]::ReadAllText((Long "$dest\proj\$longName")) -eq 'dlouhe jmeno')
    Check 'extract: lokalne zmeneny keep.txt neprepsan' ([IO.File]::ReadAllText("$dest\proj\My project\Assets\keep.txt") -eq 'puvodni lokalni zmena')
    Check 'extract: same.txt neprepsan' ([IO.File]::ReadAllText("$dest\proj\same.txt") -eq 'BBBB')
    $cil = @(Get-ChildItem -LiteralPath "$dest\proj\My project\Assets" | Where-Object { $_.Name -like 'C*l.txt' })
    Check 'extract: zadny NFD duplikat vedle NFC souboru' (($cil.Count -eq 1) -and ($cil[0].Name -eq $nfc))
    foreach ($p in 'My project\Temp', 'My project\Logs', '.git\index.lock', '.git\lfs', 'sub\.DS_Store', 'link') {
        Check "extract: preskoceno a nevytvoreno '$p'" (-not (Test-Path -LiteralPath "$dest\proj\$p"))
    }
    Check 'extract: __MACOSX nevytvoren' (-not (Test-Path "$dest\__MACOSX"))
    $tmpLeft = @(Get-ChildItem -LiteralPath $dest -Recurse -Force -Filter '.zf-*.tmp' -ErrorAction SilentlyContinue)
    Check 'extract: nezustaly docasne soubory' ($tmpLeft.Count -eq 0)

    # D) verify odhali stejne velky soubor s jinym obsahem
    WriteFile "$dest\proj\My project\Assets\keep.txt" 'zip verze'
    Check 'verify: kod 1 pri CRC neshode' ((Run 'verify' $dest 'rD') -eq 1)
    Check 'verify: same.txt v crc-bad reportu' ((ReportFile 'rD' 'verify-crc-bad.txt') -match 'proj/same.txt')

    # E) vse sedi -> kod 0
    WriteFile "$dest\proj\same.txt" 'AAAA'
    Check 'verify: kod 0 kdyz vse odpovida zipu' ((Run 'verify' $dest 'rE') -eq 0)

    # F) umyslne rozbaleni do prazdne slozky
    $empty = Join-Path $base 'empty'
    New-Item -ItemType Directory -Path $empty | Out-Null
    Check 'scan prazdne slozky: kod 1 (varovani)' ((Run 'scan' $empty 'rF1') -eq 1)
    Check 'extract --prazdny-cil: kod 0' ((Run 'extract' $empty 'rF2' @('--prazdny-cil')) -eq 0)
    Check 'extract --prazdny-cil: jmeno zapsano v NFC' (Test-Path -LiteralPath "$empty\proj\My project\Assets\$nfc")
    Check 'neznamy prepinac: kod 2' ((Run 'scan' $empty 'rF3' @('--neco')) -eq 2)

    # G) reporty nesmi jit do cilove slozky
    Check 'reporty uvnitr cile: kod 2' ((Run 'scan' $dest "$dest\proj\reports" -reportIsFullPath) -eq 2)
    Check 'reporty uvnitr cile: nic nevytvoreno' (-not (Test-Path "$dest\proj\reports"))
    Check 'reporty = cilova slozka: kod 2' ((Run 'scan' $dest $dest -reportIsFullPath) -eq 2)
    Check 'reporty jako \\?\ alias cile: kod 2' ((Run 'scan' $dest ('\\?\' + "$dest\proj") -reportIsFullPath) -eq 2)
    Check 'cil jako \\?\ zapis, reporty uvnitr: kod 2' ((Run 'scan' ('\\?\' + $dest) "$dest\x" -reportIsFullPath) -eq 2)

    # G2) opakovane behy do stejne slozky pro reporty nic neprepisuji
    Run 'scan' $dest 'rG2' | Out-Null
    Run 'scan' $dest 'rG2' | Out-Null
    $runs = @(Get-ChildItem -LiteralPath (Join-Path $base 'rG2') -Directory)
    Check 'dva behy = dve samostatne slozky reportu' (($runs.Count -eq 2) -and (@($runs | Where-Object { Test-Path (Join-Path $_.FullName 'scan-summary.txt') }).Count -eq 2))

    # H) fatalni chyba ma deklarovany kod
    Check 'neexistujici zip: kod 4' ((Run 'scan' $dest 'rH' @() (Join-Path $base 'neni.zip')) -eq 4)

    # I) zip jen se slozkami do spatneho/prazdneho cile -> extract neproveden
    $dirsZip = Join-Path $base 'dirs.zip'
    $z = [IO.Compression.ZipFile]::Open($dirsZip, 'Create')
    try { $z.CreateEntry('jen/slozky/') | Out-Null } finally { $z.Dispose() }
    $empty2 = Join-Path $base 'empty2'
    New-Item -ItemType Directory -Path $empty2 | Out-Null
    Check 'zip jen se slozkami: extract kod 3' ((Run 'extract' $empty2 'rI' @() $dirsZip) -eq 3)
    Check 'zip jen se slozkami: nic nevytvoreno' (@(Get-ChildItem -LiteralPath $empty2 -Force).Count -eq 0)
}
finally {
    if (Test-Path $base) { cmd /c rd /s /q "\\?\$base" }
}

Write-Host ''
if ($script:fails -eq 0) { Write-Host 'VSECHNY KONTROLY PROSLY' -ForegroundColor Green } else { Write-Host "NEUSPESNYCH KONTROL: $script:fails" -ForegroundColor Red }
exit $script:fails

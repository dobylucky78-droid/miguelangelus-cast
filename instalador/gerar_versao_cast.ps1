# Gera uma versão ASSINADA do MiguelAngelus Cast: publica o programa, assina o .exe e a .dll, monta o instalador,
# assina o instalador e copia para a área de trabalho (com número e com o nome fixo do site) e a pasta da pendrive.
# Antes: abrir o SimplySign Desktop e entrar (e-mail + código do app SimplySign no celular).
# Uso:  powershell -ExecutionPolicy Bypass -File cast\instalador\gerar_versao_cast.ps1
$ErrorActionPreference = 'Stop'
$cast = Split-Path $PSScriptRoot -Parent                    # ...\Lumen\cast
$lumen = Split-Path $cast -Parent                           # ...\Lumen
$signtool = Join-Path $lumen 'instalador\signtool\signtool.exe'
$assinante = 'Open Source Developer Marcio Aurelio Rios Martins'
$carimbo = 'http://time.certum.pl'
$iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'

function Assinar($arquivo) {
  Write-Host "Assinando $arquivo"
  & $signtool sign /n $assinante /fd sha256 /tr $carimbo /td sha256 /d 'MiguelAngelus Cast' $arquivo
  if ($LASTEXITCODE) { throw "Falhou ao assinar $arquivo (o SimplySign Desktop está aberto e conectado?)" }
  & $signtool verify /pa /q $arquivo
  if ($LASTEXITCODE) { throw "Assinatura não conferiu: $arquivo" }
}

# versão: a do .iss tem de ser a mesma do .csproj
$iss = Join-Path $PSScriptRoot 'MiguelAngelusCast.iss'
$versao = ([regex]'#define Versao "([^"]+)"').Match((Get-Content $iss -Raw)).Groups[1].Value
$csproj = Join-Path $cast 'programa\MiguelAngelusCast.csproj'
$vproj = ([regex]'<Version>([^<]+)</Version>').Match((Get-Content $csproj -Raw)).Groups[1].Value
if ($versao -ne $vproj) { throw "Versões diferentes: instalador $versao, programa $vproj" }
if (-not (Test-Path (Join-Path $cast 'programa\ffmpeg\ffmpeg.exe'))) { throw 'Falta o FFmpeg em programa\ffmpeg (veja o README)' }
if (-not (Test-Path (Join-Path $cast 'programa\modelos\face_recognition_sface_2021dec.onnx'))) { throw 'Falta o modelo SFace em programa\modelos (veja o README)' }
$nomeInstalador = "Instalar MiguelAngelus Cast $versao.exe"

# 1) publicar
$saida = Join-Path $PSScriptRoot 'saida'
if (Test-Path $saida) { Remove-Item -Recurse -Force $saida }
Push-Location (Join-Path $cast 'programa')
dotnet publish -c Release -o $saida | Select-Object -Last 1
if ($LASTEXITCODE) { throw 'dotnet publish falhou' }
Pop-Location
# sobras do publish que não servem ao programa
Get-ChildItem $saida -Include '*.lib', '*.pdb', 'Microsoft.Web.WebView2.*.xml' -Recurse | Remove-Item -Force

# 2) assinar o programa
Assinar (Join-Path $saida 'MiguelAngelusCast.exe')
Assinar (Join-Path $saida 'MiguelAngelusCast.dll')

# 3) instalador
& $iscc /Q $iss
if ($LASTEXITCODE) { throw 'Inno Setup falhou' }
$instalador = Join-Path $PSScriptRoot $nomeInstalador
Assinar $instalador

# 4) cópias
$mesa = Split-Path $lumen -Parent
Copy-Item $instalador (Join-Path $mesa $nomeInstalador) -Force
# nome fixo, para anexar também na versão do GitHub: o link
# https://github.com/dobylucky78-droid/miguelangelus-cast/releases/latest/download/Instalar-MiguelAngelus-Cast.exe
# sempre baixa a mais nova (a atualização automática prefere o anexo com número; são o mesmo arquivo)
Copy-Item $instalador (Join-Path $mesa 'Instalar-MiguelAngelus-Cast.exe') -Force
$pendrive = Join-Path $mesa 'Pendrive MiguelAngelus'
if (Test-Path $pendrive) { Copy-Item $instalador (Join-Path $pendrive $nomeInstalador) -Force }
Write-Host ("Pronto: $nomeInstalador assinado por $assinante ({0:N0} MB)" -f ((Get-Item $instalador).Length / 1MB))

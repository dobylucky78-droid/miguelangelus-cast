# MiguelAngelus Cast

Programa de código aberto para **transmitir celebrações** (Missas, adorações, terços) ao vivo,
feito pela Pascom da Paróquia São José Operário (Ilha do Governador, RJ).

Não é um concorrente do OBS: é um programa simples, pensado no jeito de transmitir uma Missa —
com o **roteiro da celebração** (cada momento já sabe a cena, a câmera e a posição), a **capa da live**
gerada com a cor litúrgica, e o **controle pelo celular** do [MiguelAngelus](https://github.com/dobylucky78-droid/miguelangelus).
Funciona junto com o MiguelAngelus ou sozinho.

## Situação

| Versão | O que tem |
|---|---|
| **0.0.1** (em construção) | Mesa de corte: cenas (câmera NDI, imagem, cor, barras de teste), preview e programa, transições (corte, esmaecer, virar página) · **gravar e transmitir** para YouTube, Facebook e Instagram ao mesmo tempo (codifica uma vez; se um destino cai, só ele reconecta) |
| próximas da v0 | som (limitador, atraso, mesa abaixa nos vídeos) · roteiro e perfis de celebração · capa da live · criar as lives no YouTube · celular |
| 0.1 | câmera PTZ **seguindo o celebrante** (rosto cadastrado com autorização) — testes em `kit-rastreio/` |
| **0.1.9** | cenas com camadas, som por cena, tela/janela e página da internet · **telão inteiro do MiguelAngelus** por NDI · roteiro que **segue o MiguelAngelus** (prepara a próxima cena faltando N slides, câmera livre prepara) · seguir/achar uma **pessoa cadastrada** por momento · fundos temáticos · miniaturas ao vivo · Stream Dock com retorno nas teclas |
| **0.1.10** | **posição da câmera PTZ salva na cena** (o Cast lê as coordenadas da câmera e guarda na cena, nada é gravado na câmera) · cena no Preview já leva a câmera livre para a posição · chamar a posição pelo Stream Deck · achar/seguir uma pessoa a partir da posição da cena · o momento do roteiro vale mais que a cena |
| **0.1.11** | **instalador** (como o do MiguelAngelus) e **atualização automática** pelo GitHub, só com instalador assinado |

## Baixar

O programa pronto (assinado) fica em **Releases**: baixe o **`Instalar-MiguelAngelus-Cast.exe`** e instale (não pede administrador). A partir da 0.1.11 o Cast avisa quando sai versão nova e se atualiza sozinho, quando você mandar. Até a 0.1.10 ele vinha em `.zip`.

## Pastas

- `programa/` — o MiguelAngelus Cast (.NET 8, tela em HTML com WebView2)
  - `Motor/` — vídeo: fontes, mistura, transições
  - `web/` — a tela
- `kit-rastreio/` — kit de testes do rastreio da PTZ (gera telemetria para ajustar a 0.1)
- `marca/` — ícone
- `instalador/` — instalador (Inno Setup 6) e `gerar_versao_cast.ps1`, que publica, assina e monta a versão

## Compilar

Precisa do .NET 8 SDK e do **FFmpeg LGPL** (build `ffmpeg-master-latest-win64-lgpl-shared` do BtbN, no GitHub): copie `ffmpeg.exe`, as DLLs e o `LICENSE.txt` para `programa/ffmpeg/` (essa pasta não vai para o repositório). Use a versão **LGPL**, nunca a GPL: o Cast é MIT. Para rodar, o computador precisa do **NDI Runtime** (vem no NDI Tools) para as câmeras NDI
e do **WebView2** (já vem no Windows 10/11).

**Modelo de reconhecimento (SFace):** o arquivo `face_recognition_sface_2021dec.onnx` (37 MB) passa do limite de envio do GitHub e não está no repositório. Baixe do OpenCV Zoo (https://github.com/opencv/opencv_zoo/tree/main/models/face_recognition_sface) e coloque em `programa/modelos/` (e em `kit-rastreio/modelos/`). O detector YuNet já está aqui. No zip do Releases ele já vem.

```
cd programa
dotnet publish -c Release -o ..\saida
```

O programa sai "self-contained": não precisa instalar o .NET no computador da igreja.

## Dados e privacidade

- Ajustes e cenas ficam em `%LOCALAPPDATA%\MiguelAngelusCast`, só naquele computador.
- Chaves e autorizações das plataformas ficam criptografadas no computador e **nunca** vão para o repositório nem para o backup.
- O rosto cadastrado para o rastreio (0.1) é dado sensível (LGPD): só com autorização por escrito, fica só no computador e pode ser apagado.

## Licença

MIT — veja `LICENSE`. Componentes de terceiros mantêm as suas licenças (FFmpeg: LGPL 2.1, roda como programa separado; WebView2: Microsoft; NDI: termos da Vizrt NDI AB;
modelos do kit de rastreio: YuNet MIT, SFace Apache 2.0).

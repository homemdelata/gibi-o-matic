# Dependências de terceiros

Toda dependência precisa de licença compatível com MIT (sem AGPL/GPL). Atualize esta tabela ao adicionar ou remover pacotes.

| Componente | Versão | Uso | Licença | Status |
|---|---|---|---|---|
| Avalonia (+ Desktop, Themes.Fluent) | 12.1.4 | UI | MIT | em uso (F0) |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM | MIT | em uso (F0) |
| Microsoft.Extensions.DependencyInjection (+ Abstractions) / Configuration (+ Abstractions, UserSecrets, EnvironmentVariables) / Logging | 10.0.x | DI, configuração, log | MIT | em uso (F0/F2) |
| System.Security.Cryptography.ProtectedData | 10.0.x | DPAPI (segredos no Windows) | MIT | em uso (F0) |
| Microsoft.Data.Sqlite | 10.0.x | índice | MIT | em uso (F0) |
| Dapper | 2.1.89 | acesso a dados | Apache-2.0 | em uso (F0) |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.File | 4.4 / 10.0 / 7.0 | log em arquivo | Apache-2.0 | em uso (F0) |
| xUnit v3 | 4.0.2 | testes | Apache-2.0 | em uso (F0) |
| Avalonia.Headless | 12.1.4 | só `tools/Screenshots` (capturas de tela da documentação); não vai no app | MIT | em uso (F6) |
| Velopack (biblioteca) e vpk (ferramenta, `dotnet-tools.json`) | 1.2.161 | instalador, atualizações (GitHub Releases) e AppImage | MIT | em uso (F6) |
| MinVer | 8.0.0 | versão dos assemblies a partir da tag `v*` do git; só no build, não vai no app | Apache-2.0 | em uso (F6) |
| Anybody (Etcetera Type Co., TTFs estáticos Bold e ExtraBold) | — | fonte de títulos e números grandes (embutida, `Assets/Fonts`); substituiu a Bricolage Grotesque | SIL OFL 1.1 | em uso (F7) |
| IBM Plex Sans, IBM Plex Mono | — | fontes de texto (embutidas, `Assets/Fonts`) | SIL OFL 1.1 (Reserved Font Name "Plex") | em uso (F0) |
| Lucide (ícones folder-plus, folder-open, rotate-cw) | — | ícones, como geometria em `Themes/Icons.axaml` | ISC | em uso (F0) |
| PDFium (`bblanchon.PDFium.Win32` e `.Linux`) | 157.0.8100 | renderizar páginas do PDF (capa, CBZ), Info do PDF, extrair o JPEG original das páginas; chamado por P/Invoke próprio | Apache-2.0 (binários); PDFium: BSD-3-Clause | em uso (F1) |
| SharpCompress | 1.0.0 | ler CBZ e CBR (RAR) | MIT (a descompressão de RAR deriva do unrar, cuja licença só proíbe recriar o compressor RAR) | em uso (F1) |
| SkiaSharp (+ NativeAssets.Linux) | 3.119.4 | capas e miniaturas; a mesma versão do Avalonia.Skia | MIT | em uso (F1) |
| Avalonia.Controls.WebView | 12.1.0 | leitor: EPUB de texto, com o motor web do sistema (WebView2 no Windows, WebKitGTK no Linux); sem Chromium embutido | MIT | em uso (F3.5) |
| Lucide (ícones do leitor: chevron-right, chevrons, book-open, maximize, list, columns) | — | ícones, como geometria em `Themes/Icons.axaml` | ISC | em uso (F3.5) |
| MediaDevices | 1.10.0 | Kindle por USB (MTP / Windows Portable Devices); a 1.10 tem alvo `net7.0` comum (a 2.0 só tem `net*-windows`), então o projeto continua multiplataforma e o MTP só é usado no Windows | MIT (confirmado no pacote) | em uso (F4) |
| KCC (Kindle Comic Converter), `kcc-c2e` | 12.0.0 | conversão para Kindle, chamado como **processo externo**. **Não é distribuído com o app**: o usuário instala, ou o app baixa o arquivo do release oficial no GitHub quando o usuário pede, conferindo o SHA-256 fixado no código. O executável do KCC inclui PyMuPDF (AGPL) e 7-Zip; como o app só o executa e não o redistribui, nada disso entra no app | ISC (KCC) | em uso (F4) |
| KindleGen (do Kindle Previewer, Amazon) | — | usado pelo KCC para gerar MOBI; vem com o Kindle Previewer, que o usuário instala | licença da Amazon (não redistribuído) | dependência externa (F4) |

As licenças das fontes (`OFL-*.txt`) acompanham os arquivos em `src/GibiOMatic.App/Assets/Fonts/`. As fontes são distribuídas sem modificação.

**Não usar**: MuPDF / PyMuPDF (AGPL), Calibre como biblioteca (GPL).

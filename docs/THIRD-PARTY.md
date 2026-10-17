# Dependências de terceiros

Toda dependência precisa de licença compatível com MIT (sem AGPL/GPL). Atualize esta tabela ao adicionar ou remover pacotes.

| Componente | Versão | Uso | Licença | Status |
|---|---|---|---|---|
| Avalonia (+ Desktop, Themes.Fluent) | 12.1.4 | UI | MIT | em uso (F0) |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM | MIT | em uso (F0) |
| Microsoft.Extensions.DependencyInjection / Configuration / Configuration.UserSecrets / Logging | 10.0.x | DI, configuração, log | MIT | em uso (F0) |
| System.Security.Cryptography.ProtectedData | 10.0.x | DPAPI (segredos no Windows) | MIT | em uso (F0) |
| Microsoft.Data.Sqlite | 10.0.x | índice | MIT | em uso (F0) |
| Dapper | 2.1.89 | acesso a dados | Apache-2.0 | em uso (F0) |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.File | 4.4 / 10.0 / 7.0 | log em arquivo | Apache-2.0 | em uso (F0) |
| xUnit v3 | 4.0.2 | testes | Apache-2.0 | em uso (F0) |
| Bricolage Grotesque | — | fonte de títulos (embutida, `Assets/Fonts`) | SIL OFL 1.1 | em uso (F0) |
| IBM Plex Sans, IBM Plex Mono | — | fontes de texto (embutidas, `Assets/Fonts`) | SIL OFL 1.1 (Reserved Font Name "Plex") | em uso (F0) |
| Lucide (ícones folder-plus, folder-open, rotate-cw) | — | ícones, como geometria em `Themes/Icons.axaml` | ISC | em uso (F0) |
| UglyToad.PdfPig | — | inspecionar PDF / extrair imagens | Apache-2.0 | planejado (F1) |
| PDFium (via PDFtoImage ou PDFiumCore) | — | renderizar páginas de PDF | BSD-3 / Apache-2.0 / MIT (confirmar) | planejado (F1) |
| SharpCompress | — | CBR (RAR) | MIT | planejado (F1) |
| SkiaSharp | — | imagens (incluído no Avalonia) | MIT | planejado |
| MediaDevices | — | MTP no Windows | MIT (confirmar) | planejado (F4) |
| KCC (Kindle Comic Converter) | — | conversão para Kindle (processo externo) | ISC | planejado (F4) |

As licenças das fontes (`OFL-*.txt`) acompanham os arquivos em `src/GibiOMatic.App/Assets/Fonts/`. As fontes são distribuídas sem modificação.

**Não usar**: MuPDF / PyMuPDF (AGPL), Calibre como biblioteca (GPL).

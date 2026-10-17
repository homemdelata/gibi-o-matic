# Decisões

Registro curto de decisões (estilo ADR). Mais recentes no fim. Formato: **data · decisão** — motivo.

- **2026-10-17 · App desktop próprio em vez do Calibre.** O Calibre faz muito mais do que o necessário e não tem os fluxos de quadrinhos (encadernados, Komga, Kindle).
- **2026-10-17 · C# + .NET 10 + Avalonia 11.** O usuário conhece C#; é multiplataforma (Linux no futuro) e leve. Electron e app web foram descartados.
- **2026-10-17 · Nome Gibi-o-matic; licença MIT.**
- **2026-10-17 · Sem migração do Calibre.** O usuário está baixando de novo todos os originais; os metadados serão refeitos.
- **2026-10-17 · Arquivo: os arquivos no disco são a fonte da verdade.** Um `metadata.json` por livro; o índice SQLite é só cache reconstruível e fica **fora** do Google Drive, porque SQLite em pasta sincronizada corrompe.
- **2026-10-17 · Originais imutáveis.** Metadados nunca são embutidos no original; ficam no `metadata.json` e nas cópias dos destinos.
- **2026-10-17 · Formatos preferidos para o original principal: CBZ > PDF > EPUB > CBR.**
- **2026-10-17 · "Destinos" configuráveis** (Komga, Kindle; outros no backlog), cada um com regras "quais livros" e "quando" e um "Avançado" escondido.
- **2026-10-17 · Komga: padrão `{Série}/…`, sem pasta de editora; pasta de one-shots configurável no destino** (padrão `_oneshots`).
- **2026-10-17 · Aprovação automática de metadados ligada, com regras estritas** (SPEC §8.2) e marcação para conferência.
- **2026-10-17 · Livros que não são quadrinhos: opção por destino**, conforme a capacidade do tipo. Komga sim (EPUB reflow); Kindle a fazer.
- **2026-10-17 · Sem dependências AGPL/GPL.** PDFium e PdfPig no lugar do MuPDF.
- **2026-10-17 · Kindle via KCC chamado como processo externo**; entrega por MTP no Windows; Linux a fazer.
- **2026-10-17 · F0: Avalonia 11.3 (não 12).** O Avalonia 12 já saiu, mas a decisão registrada é a 11; migrar fica para uma reavaliação futura, sem pressa.
- **2026-10-17 · Testes com xUnit v3 sobre Microsoft.Testing.Platform** (`"test": { "runner": ... }` no `global.json`). O `dotnet test` do .NET 10 não aceita mais o VSTest com o xUnit v3. Comando: `dotnet test --solution GibiOMatic.sln`.
- **2026-10-17 · Fontes estáticas, não variáveis.** O Avalonia 11 não seleciona eixos de fontes variáveis. Bricolage Grotesque vem do repositório do autor (ateliertriay/bricolage), IBM Plex Sans do IBM/plex e IBM Plex Mono do google/fonts.
- **2026-10-17 · `gibiomatic-library.json`** = `{ formatVersion, id, createdAt }`. O id é um ULID gerado por código próprio (sem pacote); o id curto do livro é formado pelos 6 últimos caracteres do ULID, em minúsculas.
- **2026-10-17 · Índice SQLite**: migrações numeradas via `PRAGMA user_version`; um índice escrito por versão mais nova do app é descartado e reconstruído (é só cache). Conexões sem *pooling*, para o arquivo ser liberado logo após cada operação.
- **2026-10-17 · Abrir uma biblioteca recria `Livros/` e `.gibiomatic/` se faltarem**; nunca mexe nos livros. Criar só é recusado se a pasta já tiver uma biblioteca.
- **2026-10-17 · `settings.json`**: chaves desconhecidas (de versões mais novas) são preservadas ao salvar; um arquivo inválido é copiado para `settings.json.bad` e o app segue com os padrões.
- **2026-10-17 · Segredos**: um arquivo por segredo em `%APPDATA%\GibiOMatic\secrets\`, cifrado com DPAPI (usuário atual). No Linux, um *stub* que não guarda nada (libsecret a fazer).
- **2026-10-17 · Textos da interface**: `Strings.resx` (pt-BR, cultura neutra) e `Strings.en.resx`, com classe gerada pelo MSBuild e usada via `x:Static`. Trocar o idioma exige reiniciar o app.

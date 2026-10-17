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

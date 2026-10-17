# Gibi-o-matic

App desktop (C# / Avalonia) para organizar uma coleção pessoal de quadrinhos digitais:

- **Arquivo** — guarda os originais (ex.: compras do Humble Bundle) intocados, com metadados corretos ao lado.
- **Metadados** — busca em ComicVine e Open Library (ISBN), com revisão "atual → proposto" e editor manual.
- **Komga** — publica cópias em CBZ com `ComicInfo.xml`, organizadas por editora/série.
- **Kindle** — gera versões AZW3 via Kindle Comic Converter, prontas para copiar por USB.

> Status: em desenvolvimento — fases F0 (fundação) e F1 (importação e biblioteca) concluídas. Veja [`docs/ROADMAP.md`](docs/ROADMAP.md) e [`docs/SPEC.md`](docs/SPEC.md).

## Desenvolvimento
Requer o .NET 10 SDK (versão fixada em `global.json`).

```
dotnet build GibiOMatic.sln
dotnet test --solution GibiOMatic.sln
dotnet run --project src/GibiOMatic.App
```

## Licença
MIT — veja [`LICENSE`](LICENSE).

## Design
Protótipo da interface: [`docs/DESIGN.md`](docs/DESIGN.md) e [`docs/design/prototype/`](docs/design/prototype/).

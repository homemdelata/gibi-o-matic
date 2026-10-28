# Gibi-o-matic

App desktop (C# / .NET 10 / Avalonia 12) para organizar uma coleção pessoal de quadrinhos digitais comprados (Humble Bundle etc.):
arquivo de originais intocados + metadados corretos → cópias de leitura geradas para **destinos** (Komga, Kindle).
Código aberto, licença MIT: `github.com/homemdelata/gibi-o-matic`.

Se existir um `CLAUDE.local.md` (fora do git), ele traz o contexto da máquina e da coleção de quem está usando; siga-o também.

## Leia antes de implementar
| Documento | Para quê |
|---|---|
| `docs/SPEC.md` | **Fonte da verdade** funcional e técnica. Em caso de dúvida, ela manda. |
| `docs/ROADMAP.md` | Fases com tarefas, critérios de pronto e status. Comece por aqui para saber o que fazer agora. |
| `docs/DESIGN.md` + `docs/design/prototype/` | Interface: tokens de cor/tipografia, componentes e as telas do protótipo (HTML de referência visual). |
| `docs/RESEARCH.md` | Descobertas já validadas (APIs, formatos de arquivo, Kindle, Komga), com fontes. Evita repetir pesquisa. |
| `docs/DECISIONS.md` | Decisões já tomadas (não reabrir sem motivo). Acrescente novas decisões técnicas aqui. |
| `docs/THIRD-PARTY.md` | Dependências e licenças. Atualize ao adicionar pacote. |
| `CONTRIBUTING.md` | Regras para quem contribui (em inglês). |
| `reference/python/` | Implementações de referência **validadas** (nota de qualidade, PDF→imagens). Portar a lógica para C#; não usar em produção. |

## Como trabalhar
- **Uma fase por vez**, na ordem do `ROADMAP.md`. Ao concluir uma fase:
  1. build e testes verdes;
  2. marque as tarefas e o status no `ROADMAP.md`;
  3. faça o commit e o push;
  4. **pare** e explique ao usuário como testar o que foi entregue (passos concretos).

  Só siga para a próxima fase depois do OK dele.
- **Decisões de produto** (comportamento, telas, textos): pergunte ao usuário. **Decisões técnicas pequenas**: decida e registre em `docs/DECISIONS.md`.
- Se algo na SPEC estiver errado ou for inviável, diga e proponha a alteração; não desvie em silêncio.
- **Commits**: pequenos, mensagem em pt-BR, com as linhas de atribuição que o ambiente indicar.

## Idioma e estilo
- **Código, identificadores e comentários em inglês**. Textos da interface em `resx`, com **pt-BR** (padrão) e **en**.
- Documentação de projeto (`docs/`) em pt-BR.
- Arquivos para o público em **inglês**: `README.md`, `CONTRIBUTING.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md` e `.github/`.
  O `README.pt-BR.md` é a tradução do `README.md`: mudou um, mude o outro.
- C# moderno: nullable habilitado, `async`/`await` com `CancellationToken`, MVVM com CommunityToolkit.Mvvm (source generators), injeção de dependência (Microsoft.Extensions.DependencyInjection), sem lógica nas Views.

## Regras invioláveis
1. **Nunca alterar arquivos originais** da biblioteca. Originais são somente leitura depois da importação; toda escrita de metadados vai para `metadata.json`.
2. **Nunca versionar** quadrinhos (CBZ/CBR/PDF/EPUB/AZW3), chaves de API ou dados pessoais. Os testes usam arquivos **sintéticos gerados no próprio teste**: PDFs, CBZs e EPUBs mínimos.
3. **Sem dependências AGPL/GPL** (ex.: MuPDF/PyMuPDF, mesmo que o script de referência use). Verifique a licença antes de adicionar qualquer pacote e registre em `docs/THIRD-PARTY.md`.
4. **Chave do ComicVine nunca no código nem no repositório.**
   - Em desenvolvimento ela fica nos *user secrets* do .NET, fora do repositório:
     - `UserSecretsId` = `3c6e08f6-15a8-4547-9a68-52954af24a45` (coloque exatamente esse valor em `<UserSecretsId>` no `.csproj` do `GibiOMatic.App` e de projetos de teste de integração que precisarem);
     - chave de configuração `ComicVine:ApiKey`;
     - arquivo `%APPDATA%\Microsoft\UserSecrets\3c6e08f6-15a8-4547-9a68-52954af24a45\secrets.json`.
     - Leia via `IConfiguration` (`AddUserSecrets`) só em Debug. Nunca imprima a chave em logs ou na saída do terminal.
   - Também aceite a variável de ambiente `GIBIOMATIC_COMICVINE_KEY`, para o CI e para outros desenvolvedores.
   - Em produção: o usuário digita a chave nas Configurações, e ela é guardada protegida (DPAPI no Windows).
   - Testes que chamam a API de verdade ficam marcados como integração e são pulados quando não há chave.
5. **APIs externas**: User-Agent próprio, cache local das respostas, limite de requisições e novas tentativas com espera (*backoff*). Nunca martelar o ComicVine.
6. **Não mexer na biblioteca real, nas pastas de destino (Komga) nem no Kindle do usuário** sem pedir antes. Para testes manuais, use uma pasta de teste.
7. Operações em lote: simulação primeiro, depois confirmação e log.

## Stack e comandos
- **.NET 10 SDK.** Se `dotnet --list-sdks` não mostrar 10.x, peça autorização e instale com `winget install Microsoft.DotNet.SDK.10`. A versão fica fixada no `global.json`.
- Avalonia 12, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite + Dapper, Serilog, xUnit. Gerenciamento central de pacotes (`Directory.Packages.props`).
- Comandos:
  - `dotnet build GibiOMatic.sln`
  - `dotnet test --solution GibiOMatic.sln` (Microsoft.Testing.Platform, ver `global.json`)
  - `dotnet run --project src/GibiOMatic.App`
  - `powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-Runtime linux-x64]` → instalador Velopack em `artifacts\releases\<rid>\` (`Gibi-o-matic-win-Setup.exe`, `Gibi-o-matic.AppImage`)
  - `dotnet run --project tools/Screenshots` → regera as capturas de `docs/images` (biblioteca fictícia, pt-BR); com `-- docs/images en`, as capturas `-en` do `README.md`
- **Versão**: vem da última tag `vX.Y.Z` do git (MinVer). Para lançar uma versão: `git tag v0.6.0` e `git push origin v0.6.0`; o workflow `release.yml` gera os instaladores e cria um release **rascunho** no GitHub, que precisa ser publicado à mão para os apps instalados verem a atualização.

## Estrutura da solução
```
GibiOMatic.sln
global.json  Directory.Build.props  Directory.Packages.props
src/
  GibiOMatic.App/            # Avalonia: Views, ViewModels, temas, Assets (fontes), Resources (resx)
  GibiOMatic.Core/           # domínio, regras de qualidade, interfaces de serviço
  GibiOMatic.Library/        # formato em disco, metadata.json, índice SQLite, importação
  GibiOMatic.Metadata/       # IMetadataSource: OpenLibrary, ComicVine, GoogleBooks; identificação
  GibiOMatic.Conversion/     # PDF/EPUB/CBZ/CBR → páginas, montagem de CBZ, ComicInfo.xml, execução do KCC
  GibiOMatic.Destinations/   # IDestinationType, Komga, Kindle
tests/
  GibiOMatic.*.Tests/
tools/  docs/  reference/  .github/
```

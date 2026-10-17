# Gibi-o-matic

App desktop (C# / .NET 10 / Avalonia 11) para organizar uma coleção pessoal de quadrinhos digitais comprados (Humble Bundle etc.):
arquivo de originais intocados + metadados corretos → cópias de leitura geradas para **destinos** (Komga, Kindle).
Licença MIT. Repositório privado por enquanto (`github.com/homemdelata/gibi-o-matic`), com intenção de abrir no futuro.

## Leia antes de implementar
| Documento | Para quê |
|---|---|
| `docs/SPEC.md` | **Fonte da verdade** funcional e técnica. Em caso de dúvida, ela manda. |
| `docs/ROADMAP.md` | Fases F0–F6 com tarefas, critérios de pronto e status. Comece por aqui para saber o que fazer agora. |
| `docs/DESIGN.md` + `docs/design/prototype/` | Interface: tokens de cor/tipografia, componentes e as telas do protótipo (HTML de referência visual). |
| `docs/RESEARCH.md` | Descobertas já validadas (APIs, formatos de arquivo, Kindle, Komga), com fontes. Evita repetir pesquisa. |
| `docs/DECISIONS.md` | Decisões já tomadas (não reabrir sem motivo). Acrescente novas decisões técnicas aqui. |
| `docs/THIRD-PARTY.md` | Dependências e licenças. Atualize ao adicionar pacote. |
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
- Converse com o usuário em **português (Brasil)**. Ele conhece C# e pode ler o código.
- **Código, identificadores e comentários em inglês** (o projeto pode virar open source). Textos da interface em `resx`, com **pt-BR** (padrão) e **en**.
- Documentação (`docs/`) em pt-BR.
- C# moderno: nullable habilitado, `async`/`await` com `CancellationToken`, MVVM com CommunityToolkit.Mvvm (source generators), injeção de dependência (Microsoft.Extensions.DependencyInjection), sem lógica nas Views.

## Regras invioláveis
1. **Nunca alterar arquivos originais** da biblioteca. Originais são somente leitura depois da importação; toda escrita de metadados vai para `metadata.json`.
2. **Nunca versionar** quadrinhos (CBZ/CBR/PDF/EPUB/AZW3), chaves de API ou dados pessoais. Os testes usam arquivos **sintéticos gerados no próprio teste**: PDFs, CBZs e EPUBs mínimos.
3. **Sem dependências AGPL/GPL** (ex.: MuPDF/PyMuPDF, mesmo que o script de referência use). Verifique a licença antes de adicionar qualquer pacote e registre em `docs/THIRD-PARTY.md`.
4. **Chave do ComicVine nunca no código nem no repositório.**
   - Em desenvolvimento ela **já está guardada** nos *user secrets* do .NET, fora do repositório:
     - `UserSecretsId` = `3c6e08f6-15a8-4547-9a68-52954af24a45` (coloque exatamente esse valor em `<UserSecretsId>` no `.csproj` do `GibiOMatic.App` e de projetos de teste de integração que precisarem);
     - chave de configuração `ComicVine:ApiKey`;
     - arquivo `%APPDATA%\Microsoft\UserSecrets\3c6e08f6-15a8-4547-9a68-52954af24a45\secrets.json`.
     - Leia via `IConfiguration` (`AddUserSecrets`) só em Debug. Nunca imprima a chave em logs ou na saída do terminal.
   - Também aceite a variável de ambiente `GIBIOMATIC_COMICVINE_KEY`, para o CI e para outros desenvolvedores.
   - Em produção: o usuário digita a chave nas Configurações, e ela é guardada protegida (DPAPI no Windows).
   - Testes que chamam a API de verdade ficam marcados como integração e são pulados quando não há chave.
5. **APIs externas**: User-Agent próprio, cache local das respostas, limite de requisições e novas tentativas com espera (*backoff*). Nunca martelar o ComicVine.
6. **Não mexer nas pastas reais do usuário** (`G:\Meu Drive\...`, `Z:\`, o Kindle) sem pedir antes. Para testes manuais, use uma pasta de teste (ex.: `D:\gibiomatic-teste\`).
7. Operações em lote: simulação primeiro, depois confirmação e log.

## Stack e comandos
- **.NET 10 SDK.** Se `dotnet --list-sdks` não mostrar 10.x, peça autorização e instale com `winget install Microsoft.DotNet.SDK.10`. A máquina tem SDKs 6/7/8 instalados. Fixe a versão com `global.json`.
- Avalonia 11, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite + Dapper, Serilog, xUnit. Gerenciamento central de pacotes (`Directory.Packages.props`).
- Comandos (depois da F0):
  - `dotnet build GibiOMatic.sln`
  - `dotnet test GibiOMatic.sln`
  - `dotnet run --project src/GibiOMatic.App`
- Ambiente: Windows 11; shells PowerShell 5.1 e Git Bash. Caminhos com espaços (`G:\Meu Drive`) precisam de aspas.

## Estrutura da solução (alvo — criada na F0)
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
docs/  reference/  .github/workflows/
```

## Contexto do usuário (para decisões e testes)
- Coleção: ~660 livros, quase tudo de quadrinhos (Image, IDW, BOOM!, Dynamite, Dark Horse…), mais ~25 livros de arte e design. Os originais **estão sendo baixados de novo** do Humble Bundle, em PDF, CBZ e EPUB. Não há migração do Calibre; os metadados de tudo serão refeitos.
- Arquivo (backup) no Google Drive: `G:\Meu Drive\...` (pasta final a definir pelo usuário). A antiga biblioteca do Calibre, `G:\Meu Drive\Calibre`, não deve ser usada.
- **Komga** roda em Docker (Portainer) num servidor em casa; a pasta dele está mapeada como `Z:\` e hoje só tem arquivos de teste.
- **Kindle Colorsoft**, que usa transferência USB por MTP.
- Leitura no tablet via Komga.

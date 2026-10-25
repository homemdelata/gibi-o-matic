# Contribuindo

Obrigado pelo interesse! Este guia resume o que é preciso para compilar, testar e mandar mudanças.
Em caso de dúvida, [`docs/SPEC.md`](docs/SPEC.md) é a fonte da verdade, e [`docs/DECISIONS.md`](docs/DECISIONS.md)
explica por que as coisas são como são.

## Ambiente

- **.NET 10 SDK.** A versão fica fixada em `global.json`.
- Windows 10/11 ou Linux. O envio para o Kindle por USB (MTP) só existe no Windows.

```
dotnet build GibiOMatic.sln
dotnet test --solution GibiOMatic.sln
dotnet run --project src/GibiOMatic.App
```

Os testes usam o Microsoft.Testing.Platform (veja `global.json`); `dotnet test` precisa do `--solution`.

### Chave do ComicVine

Os testes não chamam APIs de verdade, exceto os de integração, que são pulados quando não há chave. Para rodar
o app ou esses testes com o ComicVine, use uma destas opções (nunca coloque a chave no código ou no repositório):

- *user secrets* do .NET, só em Debug:
  `dotnet user-secrets set ComicVine:ApiKey <chave> --project src/GibiOMatic.App`;
- a variável de ambiente `GIBIOMATIC_COMICVINE_KEY`.

## Estrutura

| Projeto | Conteúdo |
|---|---|
| `src/GibiOMatic.App` | Avalonia: Views, ViewModels (MVVM com CommunityToolkit.Mvvm), temas, textos (`Resources/Strings*.resx`), bandeja, atualizações |
| `src/GibiOMatic.Core` | domínio, nota de qualidade, configurações, segredos |
| `src/GibiOMatic.Library` | formato em disco, `metadata.json`, índice SQLite, Entrada (importação e pasta observada), identificação, edição |
| `src/GibiOMatic.Metadata` | fontes (ComicVine, Open Library, Google Books), cache, limites de requisições, identificação |
| `src/GibiOMatic.Conversion` | leitura de PDF, CBZ, CBR e EPUB, montagem de CBZ, `ComicInfo.xml`, KCC |
| `src/GibiOMatic.Destinations` | destinos Komga e Kindle e sincronização |
| `tests/` | um projeto de testes por projeto; `GibiOMatic.Testing` gera os arquivos sintéticos |
| `tools/` | `publish.ps1` (instaladores), `Screenshots` (capturas da documentação), `IconGen` (ícone) |

## Regras

1. **Os originais nunca são alterados.** Depois da importação eles são só leitura; os metadados vão para o
   `metadata.json` e para as cópias dos destinos.
2. **Nada de quadrinhos, chaves ou dados pessoais no repositório.** Os testes geram PDFs, CBZs, CBRs e EPUBs
   sintéticos na hora (`tests/GibiOMatic.Testing`).
3. **Sem dependências GPL ou AGPL** (por exemplo, MuPDF/PyMuPDF e o Calibre como biblioteca). Confira a licença
   antes de adicionar um pacote e registre-o em [`docs/THIRD-PARTY.md`](docs/THIRD-PARTY.md). As versões ficam
   em `Directory.Packages.props`.
4. **APIs externas:** User-Agent próprio, cache, limite de requisições e novas tentativas com espera.
5. **Operações em lote:** primeiro uma prévia, depois a confirmação, e tudo registrado no log.

## Estilo

- Código, identificadores e comentários em **inglês**; documentação (`docs/`) em português.
- Textos da interface sempre no `resx`, em **pt-BR** (`Strings.resx`) e **inglês** (`Strings.en.resx`). Um teste
  confere que as duas línguas têm as mesmas chaves e os mesmos marcadores.
- C# moderno: nullable, `async`/`await` com `CancellationToken`, injeção de dependência, nenhuma lógica nas Views.
- Cores só pelos tokens de `Themes/Tokens.axaml`, com `DynamicResource`. Os dois temas têm as mesmas chaves e o
  contraste dos textos é testado (WCAG AA). Um botão sem texto precisa de `AutomationProperties.Name`.
- Avisos do compilador são erros (`TreatWarningsAsErrors`).

## Mudanças

- Commits pequenos, com mensagem em português.
- Decisões técnicas novas vão para [`docs/DECISIONS.md`](docs/DECISIONS.md); dependências, para
  [`docs/THIRD-PARTY.md`](docs/THIRD-PARTY.md).
- O CI (`.github/workflows/ci.yml`) compila e roda os testes no Windows e no Linux.
- Mudou uma tela? Regere as capturas com `dotnet run --project tools/Screenshots`, que usa uma biblioteca
  fictícia numa pasta neutra.

## Versões

A versão vem da última tag `vX.Y.Z` do git (MinVer). Para lançar uma versão:

```
git tag v0.6.0
git push origin v0.6.0
```

O workflow `release.yml` roda os testes, gera o instalador do Windows e o AppImage do Linux (Velopack) e cria
um release **rascunho** no GitHub. Publicar o rascunho libera a atualização para os apps instalados. Para gerar
os pacotes na sua máquina: `powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-Runtime linux-x64]`.

Para testar a atualização sem o GitHub, defina a variável de ambiente `GIBIOMATIC_UPDATE_SOURCE` com a pasta
dos pacotes (`artifacts\releases\win-x64`): o app instalado passa a procurar versões novas nela. Instale uma
versão, gere outra (depois de um commit novo) na mesma pasta e abra o app.

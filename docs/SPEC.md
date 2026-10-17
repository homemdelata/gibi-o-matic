# Gibi-o-matic — Especificação

> Versão 0.2 · 2026-10-17 · status: rascunho para revisão
> Protótipo de interface: Claude Design (link privado do autor)

## 1. Visão

Gibi-o-matic é um app desktop para quem compra quadrinhos digitais (Humble Bundle, lojas DRM-free etc.) e quer:

1. **guardar os originais** em segurança, com metadados corretos;
2. **ler com conforto** no tablet (via servidor como o Komga) e em e-readers (Kindle);
3. **sem trabalho manual repetitivo**: metadados buscados automaticamente, revisados em poucos cliques, e cópias de leitura geradas sozinhas.

### 1.1 Objetivos
- Substituir o Calibre no fluxo de quadrinhos, com uma interface focada só nisso.
- Nunca alterar os arquivos originais.
- Funcionar em Windows desde a primeira versão; Linux depois (macOS se sair de graça).
- Ser utilizável por pessoas não técnicas: padrões bons, opções avançadas escondidas.
- Poder ser distribuído publicamente no futuro (licenças de dependências permissivas, chaves de API do próprio usuário, interface em pt-BR e inglês).

### 1.2 Fora do escopo (por enquanto)
- Leitor de quadrinhos embutido (o Komga e o Kindle fazem isso).
- Loja, download automático de compras, remoção de DRM.
- Livros de texto "reflow" como foco. Eles podem ser guardados no arquivo e enviados a destinos que os aceitem (seção 9.1), mas sem conversões especiais na primeira versão.
- Modo servidor/web.
- Migração do Calibre: a coleção será reconstruída a partir dos originais baixados de novo (seção 10).

## 2. Conceitos

| Termo | Significado |
|---|---|
| **Arquivo** (biblioteca) | Pasta raiz onde ficam os originais e os metadados. No caso do autor, no Google Drive. |
| **Livro** | Uma unidade da coleção: edição avulsa (#1), encadernado (Vol. 1/TPB), omnibus, artbook. Tem 1 original principal e, opcionalmente, formatos extras. |
| **Original** | O arquivo como foi baixado (PDF, CBZ, CBR, EPUB). Somente leitura após a importação. |
| **Entrada** | Pasta observada onde chegam downloads novos. |
| **Revisão** | Fila de livros cujos metadados precisam de confirmação humana. |
| **Destino** | Lugar para onde cópias de leitura são geradas: um servidor Komga, um Kindle etc. Cada destino tem tipo, configurações e regras próprias. |
| **Fonte de metadados** | Serviço consultado para preencher metadados (Open Library, ComicVine…). |

## 3. Fluxo geral

```
ENTRADA ──importar──► ARQUIVO (original + metadata.json) ──gerar──► DESTINOS
   │                        ▲         │                              ├─ Komga (CBZ + ComicInfo.xml)
   └─ busca automática ─────┘         └─ Revisão / Editor            └─ Kindle (AZW3 via KCC)
```

1. Um arquivo chega na Entrada.
2. O app identifica o livro (metadados embutidos + fontes online) e calcula uma confiança.
3. Confiança alta: o livro entra direto na biblioteca. Confiança baixa: vai para a Revisão.
4. Aprovar ou editar metadados marca as cópias dos destinos como desatualizadas.
5. Destinos automáticos regeneram sozinhos; destinos manuais esperam o usuário.

## 4. O Arquivo (formato em disco)

### 4.1 Princípios
- **Fonte da verdade = arquivos no disco**, não o banco. Cada livro tem um `metadata.json` ao lado do original. O índice SQLite é só cache e pode ser reconstruído a qualquer momento varrendo as pastas.
- **O índice fica fora do Drive**, em `%LOCALAPPDATA%\GibiOMatic\` (Linux: `~/.local/share/gibiomatic/`). SQLite dentro de pasta sincronizada é fonte clássica de corrupção.
- **Escritas atômicas**: grava em `*.tmp` e renomeia.
- **Originais imutáveis**: o hash SHA-256 é gravado na importação e verificado sob demanda.

### 4.2 Estrutura

```
<Arquivo>/
  gibiomatic-library.json            # id da biblioteca, versão do formato
  Livros/
    Image/
      Saga/
        Saga, Vol. 1 [k3f9a2]/
          Saga, Vol. 1.pdf         # original (conteúdo intocado; só o nome é limpo)
          cover.jpg
          metadata.json
    BOOM! Studios/
      House of Slaughter/
        House of Slaughter, Vol. 1 [p81xq0]/
          ...
    _Sem editora/
      ...
  .gibiomatic/
    history/<id>/<timestamp>.json  # versões anteriores do metadata.json (últimas 20)
```

- `[k3f9a2]` = id curto e estável do livro (6 caracteres de um ULID). Garante nomes únicos e permite achar o livro mesmo depois de renomear.
- A pasta é **renomeada** quando editora, série ou título mudam. No Drive isso é um "mover", barato.
- O nome do original vira `{Título}.{ext}`. O conteúdo nunca é alterado; o nome de download fica registrado em `metadata.json`.

### 4.3 `metadata.json`

(Os comentários `//` abaixo são só explicativos; o arquivo real é JSON puro.)

```json
{
  "schema": 1,
  "id": "01JABCK3F9A2...",
  "kind": "tpb",                      // issue | tpb | omnibus | artbook | book
  "title": "Saga, Vol. 1",
  "series": { "name": "Saga", "number": "1", "volume": null },
  "date": { "year": 2012, "month": 10, "day": 10 },
  "publisher": "Image Comics",
  "imprint": null,
  "credits": [
    { "role": "writer", "name": "Brian K. Vaughan" },
    { "role": "artist", "name": "Fiona Staples" }
  ],
  "summary": "…",
  "language": "en",
  "identifiers": { "isbn": "9781607066019", "comicvine": "4000-123456" },
  "tags": [],
  "files": [
    {
      "role": "original",
      "name": "Saga, Vol. 1.pdf",
      "downloadedAs": "Saga_Vol1_Image.pdf",
      "format": "pdf",
      "sha256": "…",
      "bytes": 412345678,
      "source": "Humble Bundle",
      "quality": "original"            // original | converted | unknown
    }
  ],
  "review": {
    "status": "approved",              // pending | approved
    "approvedAt": "2026-10-17T14:00:00Z",
    "matchedBy": "isbn",               // isbn | comicvine-volume | manual
    "confidence": 0.99
  },
  "createdAt": "…",
  "updatedAt": "…"
}
```

**Funções de crédito** (`role`): `writer`, `artist` (lápis), `inker`, `colorist`, `letterer`, `cover`, `editor`, `translator`, `other`. "Autores", nos lugares que só aceitam um campo, significa os `writer`.

## 5. Qualidade dos metadados

Usada na Biblioteca (selos e filtros) e para ordenar a Revisão. Pontuação de 0 a 100, com pesos na ordem de prioridade do autor:

| Campo | Problemas detectados |
|---|---|
| Título (peso 1,0) | só minúsculas, palavras coladas, "_" ou extensão, "Untitled", lixo como "(prop)", tudo maiúsculo |
| Autores (0,9) | ausente, "Desconhecido"/"Various", vários nomes grudados num só, duplicados, nome de uma palavra só |
| Data (0,7) | ausente, suspeita (igual à data de compra no Humble, repetida em muitos livros) |
| Série (0,7) | o título indica série mas o campo está vazio; número diferente do título |
| Secundários (0,3) | ISBN inválido, sem editora |

Faixas: **Ruim** (< 60), **Revisar** (60–84), **OK** (≥ 85). A regra já existe em Python (`calibre-metadados/scripts/analyze.py`) e será portada.

## 6. Entrada (importação)

1. Observa a pasta de entrada (FileSystemWatcher) e também aceita arrastar e soltar.
2. Para cada arquivo: espera a escrita terminar, calcula o SHA-256 e detecta o formato pelo conteúdo.
3. **Duplicados**:
   - mesmo hash de um original existente: avisa e ignora;
   - mesmo livro identificado com arquivo diferente: pergunta *Substituir o atual* / *Manter os dois* (formato extra) / *Ignorar*. Sugere "Substituir" quando o novo arquivo é de um formato preferido (ver seção 10).
4. Lê metadados embutidos: `ComicInfo.xml` (CBZ/CBR), OPF (EPUB), Info/XMP (PDF), ISBN na página de créditos (opcional, fase posterior).
5. Roda a identificação (seção 8) e mostra o resultado na tela Entrada.
6. Ao confirmar: copia o original para o Arquivo, extrai a capa e grava o `metadata.json`. O arquivo na Entrada é movido para `Entrada/_importados/`, não apagado.

## 7. Revisão e Editor

- **Revisão**: fila ordenada pela nota de qualidade. Para cada livro: candidatos com % de confiança e tabela *campo · atual · proposto*, com uma caixa de seleção por campo. Ações: Aprovar e próximo, Pular, Editar à mão.
- **Editor**: todos os campos da seção 4.3, créditos com função, capa (trocar ou usar a página 1), prévia dos nomes que cada destino vai gerar.
- **Edição em lote**: selecionar N livros e editar os campos em comum. Campos vazios não alteram nada. "Numerar em sequência" serve para o número da série.
- Toda gravação gera uma entrada em `.gibiomatic/history` e pode ser desfeita.

## 8. Fontes de metadados e identificação

### 8.1 Fontes (ordem configurável)
| Fonte | Uso | Observações |
|---|---|---|
| **Open Library** | por ISBN | sem chave; testada com sucesso (Deadly Class Vol. 11, Saga) |
| **ComicVine** | encadernados e avulsas, créditos por função | chave pessoal do usuário; respeitar limites de uso e manter cache local das respostas |
| **Google Books** | por ISBN, opcional | API v1 com limite agressivo sem chave; desligada por padrão |

### 8.2 Algoritmo
1. **ISBN válido** (embutido ou existente): Open Library e, em seguida, a busca do ComicVine por esse ISBN. Se bater, a confiança fica ≥ 0,95.
2. **Título e série**: normaliza o título (separa a série do número: "Deadly Class, Vol. 11: A Fond Farewell" → série "Deadly Class", nº 11, tipo TPB) e busca *volumes* no ComicVine.
   - No ComicVine, encadernados costumam ser volumes próprios ("Deadly Class: Reagan Youth"). É preciso considerar os volumes da série e também os volumes "Série: Subtítulo".
   - Filtra por editora conhecida, idioma (descarta edições estrangeiras, como a *House of Slaughter* alemã da Splitter) e ano.
   - Pontua cada candidato por similaridade de título, número, editora, ano e número de páginas.
3. **Aprovação automática segura**: ligada por padrão, mas só quando **todas** as condições abaixo valem. Qualquer outro caso vai para a Revisão, com os candidatos.
   - **Evidência forte**, de um destes tipos:
     - o ISBN do arquivo bate com o resultado;
     - **duas fontes independentes concordam** em título, número e editora;
     - ou, só no ComicVine: série, número, editora e ano batem exatamente e existe **um único** candidato acima de 0,5.
   - **Nenhum campo existente "bom" seria sobrescrito por um diferente.** Por exemplo, uma data já preenchida que discorda vai para a Revisão.
   - **A confiança calculada é ≥ 0,95.**
   - **Garantias:**
     - todo livro aprovado automaticamente fica marcado como `matchedBy: auto` e aparece no filtro "Aprovados automaticamente", para conferência por amostragem;
     - tudo pode ser desfeito pelo histórico;
     - a configuração "Sempre revisar" desliga a aprovação automática.

   Entre 0,50 e 0,95, o livro vai para a Revisão com candidatos; abaixo de 0,50, vai para a Revisão sem sugestão.
4. **Créditos**: só as funções criativas (roteiro, arte, arte-final, cores, letras, capa). Editores ficam fora por padrão; o plugin do Calibre os misturava com os autores.

### 8.3 Lições do diagnóstico do Calibre (2026-10-17)
- O plugin do ComicVine 0.14.2 conecta, mas busca só *edições avulsas*, e por isso erra os encadernados ("Saga, Vol. 01" → *Cable #73*).
- A fonte Google do Calibre recebe HTTP 500; a Amazon falha por bloqueio de busca.
- Conclusão: a identificação de encadernados precisa de lógica própria, que é o diferencial do app.

## 9. Destinos

### 9.1 Modelo comum
```csharp
public interface IDestinationType {
    string Id { get; }                        // "komga", "kindle"
    DestinationSettings DefaultSettings();
    IDestination Create(DestinationSettings s);
}
public interface IDestination {
    Task<DestinationStatus> GetStatusAsync(CancellationToken ct);
    bool Includes(Book book);                 // regra "quais livros"
    Task<OutputState> GenerateAsync(Book book, IProgress<double> p, CancellationToken ct);
    Task DeliverAsync(IEnumerable<Book> books, CancellationToken ct); // Kindle: copiar via USB
}
```
- **Regras de todo destino**: *Quais livros* (toda a biblioteca / escolhidos / séries) e *Quando* (automático ao aprovar / manual).
- **Capacidades por tipo**: cada tipo declara que tipos de livro aceita (`issue`, `tpb`, `omnibus`, `artbook`, `book`). Se o tipo aceita livros que não são HQ (`book`), a opção "Incluir livros que não são quadrinhos" aparece no destino, **desligada por padrão**. Se não aceita, a opção nem aparece.
  - **Komga**: aceita, porque lê EPUB reflow desde a versão 1.8, com leitor próprio. Livros de texto vão como EPUB, sem conversão.
  - **Kindle**: o KCC só converte imagens. Livros de texto exigiriam outro conversor ou o Send to Kindle, por isso ficam **a fazer** (backlog). Até lá, a opção não aparece.
- **Estado por livro e destino** (tabela no índice): hash dos metadados mais hash do original usados na última geração. Se mudar, o livro fica "desatualizado".
- As configurações de destino são **por máquina** (`%APPDATA%\GibiOMatic\settings.json`), porque caminhos como `Z:\` e o Kindle conectado são locais.

### 9.2 Destino Komga
- **Como o Komga monta as séries**: cada subpasta que contém livros vira uma série, em qualquer profundidade, e cada arquivo vira um livro da série da pasta onde está. Os livros de uma pasta de one-shots viram, cada um, uma série de um livro só. O nome dessa pasta é a opção "One-Shots directory" da biblioteca no Komga.
- **Saída padrão** (modelo editável em Avançado):
  - livros com série: `{Pasta}/{Série}/{Série} v{Número:00} ({Ano}).cbz`;
  - one-shots: `{Pasta}/{Pasta de one-shots}/{Título} ({Ano}).cbz`.
- **Por que não `Editora/Série/`**: a pasta da editora não contém arquivos, então em tese o Komga a ignora, mas isso **não foi testado**. A editora já vai no `ComicInfo.xml` e o Komga permite filtrar por ela. Fica como modelo opcional, a validar na F3.
- **One-shot**: um livro sem série, ou cuja série tem um livro só (graphic novel independente, como *Plutona*). Vem da identificação (ComicVine com 1 edição) e pode ser marcado à mão no Editor ("É one-shot").
- **Configuração "Pasta de one-shots"** do destino, espelhando a opção *One-Shots directory* do Komga:
  - padrão `_oneshots`;
  - vazia = sem tratamento especial, e os one-shots ficam cada um na sua própria pasta de série;
  - o assistente e a tela do destino lembram de configurar o mesmo nome no Komga;
  - se o endereço e a chave do Komga estiverem configurados, o app lê a opção da biblioteca e avisa se os nomes forem diferentes.
- **Geração do CBZ**:
  - original CBZ: copia e injeta ou substitui o `ComicInfo.xml`;
  - original CBR: extrai e reempacota como CBZ;
  - original PDF: por página, se ela for uma única imagem JPEG cobrindo a página toda, extrai os bytes originais sem recompressão; senão (texto ou vetores por cima, como os balões do Radiant Black), renderiza na resolução da maior imagem e salva em JPEG de qualidade 92. Validado no protótipo em Python com CLOSET, Dead Lucky e Radiant Black;
  - original EPUB: extrai as imagens na ordem do *spine*.
- **ComicInfo.xml**: `Title, Series, Number, Volume, Count, Summary, Year, Month, Day, Writer, Penciller, Inker, Colorist, Letterer, CoverArtist, Editor, Publisher, Imprint, Genre, Tags, Web, PageCount, LanguageISO, Format (TPB/Issue), GTIN`.
- **Atualização do servidor (opcional)**: com URL e chave de API do Komga, chama o scan da biblioteca ao terminar. Os endpoints ainda vão ser confirmados na documentação do Komga.
- **Padrão**: toda a biblioteca, automático.

### 9.3 Destino Kindle
- **Conversão**: Kindle Comic Converter (KCC, `kcc-c2e`) com o perfil do aparelho (Colorsoft = `KCS`, 1272×1696, cor). Entrada: o CBZ gerado para o Komga, se existir, ou um CBZ temporário.
- **Formato**: AZW3/MOBI (cabo USB, recomendado) ou EPUB (Send to Kindle, com compressão da Amazon).
- **Entrega**:
  - Windows: o Kindle 2024+ usa **MTP**; cópia para `documents/` via Windows Portable Devices (por exemplo, o NuGet `MediaDevices`).
  - Linux: **a fazer**. Até lá, o botão "Abrir pasta" permite arrastar à mão, e esse botão existe em todos os sistemas.
- **Quadro a quadro**: o Kindle oferece "Visualização de painéis" (menu Aa, dois toques) em arquivos do KCC. Detecção real de quadros fica no backlog.
- **Padrão**: livros escolhidos, manual.
- **Riscos**: o KCC precisa do KindleGen para gerar MOBI/AZW3. O KindleGen vem no Kindle Previewer, que só existe para Windows e macOS, então no Linux a saída pode ficar limitada a EPUB.
- **Licença do KCC**: ISC, permissiva. Pode ser distribuído junto com o app, mantendo o aviso de copyright. Como é chamado como processo externo, não impõe nada à licença do GibiOMatic.

### 9.4 Backlog de tipos
Kavita, Kobo (KEPUB via KCC), Pasta simples (copiar em CBZ/PDF/EPUB), servidor OPDS, Calibre-Web.

## 10. Carga inicial (importação em massa)

Não há migração do Calibre. A coleção é reconstruída a partir dos originais baixados de novo do Humble Bundle, e os metadados de todos os livros são refeitos.

1. O usuário aponta a Entrada para a pasta com os downloads, que podem ser centenas de arquivos.
2. A importação roda **em lotes**, em segundo plano, com progresso e possibilidade de pausar e retomar. O estado fica no índice, então fechar o app não perde nada.
3. A identificação respeita os limites das APIs, com fila, cache e reintentos. A carga inicial pode levar horas, e isso é esperado.
4. Ao terminar, mostra um resumo:
   - quantos foram aprovados automaticamente;
   - quantos estão na Revisão;
   - quantos são duplicados, por exemplo o mesmo livro em PDF e em CBZ.
5. **Duplicados de formato**: quando o mesmo livro chega em mais de um formato, o melhor vira o original principal e os outros ficam como formatos extras. A ordem de preferência é CBZ > PDF > EPUB > CBR, configurável.
6. A biblioteca do Calibre não é lida nem alterada. Ela continua como backup até o usuário decidir apagá-la.

## 11. Configurações

- **Gerais** (`%APPDATA%\GibiOMatic\settings.json`): idioma (pt-BR, en), tema (escuro, claro, sistema), pasta do Arquivo, pasta de Entrada, fontes de metadados (ordem e liga/desliga), limiar de aprovação automática, destinos.
- **Segredos** (chaves de API) **não vão para arquivos de texto**: Windows DPAPI (`ProtectedData`) e, no Linux, libsecret.
- **Da biblioteca** (`gibiomatic-library.json`, no Drive): só o que é da coleção, independente da máquina (versão do formato, id).

## 12. Interface

Baseada no protótipo do Claude Design. Telas:

| Tela | Conteúdo |
|---|---|
| Biblioteca | Grade de capas (séries ou livros), selos de qualidade, filtros (Precisa revisão, Sem data, Sem série, Autor desconhecido, Completos), busca, painel do livro selecionado |
| Entrada | Arquivos novos com o resultado da identificação, tratamento de duplicados, importar |
| Revisão | Fila, candidatos, comparação campo a campo |
| Editor | Um livro ou lote, créditos por função, prévia dos nomes de saída |
| Destinos | Cartões com status e regras; "Adicionar destino" |
| Detalhe do destino | Ação principal (Kindle: escolher e enviar) e configurações com "Avançado" recolhido |
| Assistente de destino | 3 passos: tipo → detalhes → nome e regras |
| Configurações | Pastas, Metadados, Geral |

- **Visual**: tema escuro por padrão, amarelo de impressão (`#F5C542`) como destaque, Bricolage Grotesque para títulos, IBM Plex Sans/Mono para o resto.
- **Acessibilidade**: navegação por teclado, contraste AA, áreas de toque ≥ 44 px.
- **Idiomas**: `resx`, com pt-BR e en desde o início.

## 13. Arquitetura técnica

- **.NET 10 (LTS)**. O suporte ao .NET 8 termina em nov/2026; é preciso instalar o SDK 10.
- **Avalonia 11** com MVVM (CommunityToolkit.Mvvm).

```
GibiOMatic.sln
  src/
    GibiOMatic.App/              # Avalonia: Views, ViewModels, temas, recursos (resx)
    GibiOMatic.Core/             # domínio (Book, Credit, Destination…), regras de qualidade, serviços
    GibiOMatic.Library/          # formato em disco, metadata.json, índice SQLite, importação (entrada e carga em massa)
    GibiOMatic.Metadata/         # IMetadataSource: OpenLibrary, ComicVine, GoogleBooks; identificação
    GibiOMatic.Conversion/       # leitura de PDF/EPUB/CBZ/CBR, montagem de CBZ, ComicInfo.xml, execução do KCC
    GibiOMatic.Destinations/     # IDestinationType + Komga + Kindle (MTP)
  tests/
    GibiOMatic.Core.Tests/ …     # xUnit; amostras sintéticas, nunca HQs reais no repositório
```

| Necessidade | Biblioteca | Licença |
|---|---|---|
| UI | Avalonia, CommunityToolkit.Mvvm | MIT |
| Banco/índice | Microsoft.Data.Sqlite + Dapper | MIT / Apache-2.0 |
| Renderizar PDF | PDFium (via PDFtoImage ou PDFiumCore) | BSD/Apache |
| Extrair imagens do PDF sem perda | UglyToad.PdfPig | Apache-2.0 |
| CBR (RAR) | SharpCompress | MIT |
| Imagens | SkiaSharp (já vem com o Avalonia) | MIT |
| Log | Serilog | Apache-2.0 |
| MTP (Windows) | MediaDevices | MIT (a confirmar) |

**Evitar** MuPDF/PyMuPDF (AGPL), que obrigaria o app inteiro a ser AGPL.

**Processos externos**: o KCC é chamado como linha de comando, com caminho configurável e timeout, e o progresso é lido da saída.

## 14. Integridade e segurança

- Originais imutáveis, com hash conferido e verificação opcional ("verificar arquivo").
- Toda operação em lote tem simulação, confirmação e log em `%LOCALAPPDATA%\GibiOMatic\logs`.
- Nenhum segredo no repositório, nem nos arquivos do Drive.
- O app não envia dados para lugar nenhum além das fontes de metadados escolhidas.

## 15. Fases de entrega

| Fase | Entrega | Pronto quando |
|---|---|---|
| **F0 · Fundação** | Solução .NET, CI no GitHub Actions (build e testes), configurações, formato do Arquivo e índice | o app abre, aponta para uma pasta e cria a biblioteca vazia |
| **F1 · Importação + Biblioteca** | Importação manual e em massa (seção 10), cópia para o Arquivo, capa, metadados embutidos, grade, filtros e nota de qualidade, painel | os originais baixados aparecem na biblioteca com nota de qualidade |
| **F2 · Metadados** | Open Library + ComicVine, identificação, aprovação automática segura, Revisão, Editor (um livro e lote), histórico e desfazer | a coleção inteira passa pela identificação e a fila de Revisão é tratável |
| **F3 · Destino Komga** | Montagem de CBZ (PDF/EPUB/CBZ/CBR), ComicInfo.xml, modelo de nomes, estado de sincronização, scan opcional | a biblioteca aparece no Komga com séries e metadados corretos |
| **F4 · Destino Kindle** | Integração com o KCC, perfis, envio via MTP no Windows | um livro escolhido chega ao Colorsoft e abre com a visualização de painéis |
| **F5 · Entrada automática** | Pasta observada, identificação na chegada, duplicados e substituição | um download novo do Humble vai do download ao Komga sem passos manuais, ou só com a aprovação |
| **F6 · Polimento** | Inglês, tema claro, instalador Windows, build Linux, documentação de usuário | pronto para outra pessoa instalar sozinha |

### Backlog
Detecção real de quadros (Panel View com regiões por quadro), outros destinos (seção 9.4), leitura de ISBN por OCR da página de créditos, sugestão de séries faltantes, atualização automática do app, modo servidor/Docker.

## 16. Questões em aberto

1. Endpoints e autenticação da API do Komga para disparar o scan.
2. Comportamento do KCC sem KindleGen no Linux.
3. Pastas de editora no Komga (`Editora/Série/`): testar na F3.

**Decididas (2026-10-17)**:
- Nome: **Gibi-o-matic** (repositório `gibi-o-matic`, namespace `GibiOMatic`).
- Licença MIT (arquivo `LICENSE`).
- Sem migração do Calibre; carga inicial a partir dos originais.
- Aprovação automática ligada, com as regras de segurança da seção 8.2.
- Livros que não são HQ: opção por destino, conforme a capacidade do tipo.
- Cópia para o Kindle no Linux: a fazer.

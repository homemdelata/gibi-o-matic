# Gibiteca — Especificação

> Versão 0.1 · 2026-10-17 · status: rascunho para revisão
> Protótipo de interface: Claude Design (link privado do autor)

## 1. Visão

Gibiteca é um app desktop para quem compra quadrinhos digitais (Humble Bundle, lojas DRM-free etc.) e quer:

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
- Livros de texto "reflow" como foco. Eles podem ser guardados no arquivo, mas sem conversões especiais.
- Modo servidor/web.

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
- **O índice fica fora do Drive**, em `%LOCALAPPDATA%\Gibiteca\` (Linux: `~/.local/share/gibiteca/`). SQLite dentro de pasta sincronizada é fonte clássica de corrupção.
- **Escritas atômicas**: grava em `*.tmp` e renomeia.
- **Originais imutáveis**: o hash SHA-256 é gravado na importação e verificado sob demanda.

### 4.2 Estrutura

```
<Arquivo>/
  gibiteca-library.json            # id da biblioteca, versão do formato
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
  .gibiteca/
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
      "quality": "original"            // original | converted-by-calibre | unknown
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
   - mesmo livro identificado com arquivo diferente: pergunta *Substituir o atual* / *Manter os dois* (formato extra) / *Ignorar*. Sugere "Substituir" quando o atual tem `quality: converted-by-calibre`.
4. Lê metadados embutidos: `ComicInfo.xml` (CBZ/CBR), OPF (EPUB), Info/XMP (PDF), ISBN na página de créditos (opcional, fase posterior).
5. Roda a identificação (seção 8) e mostra o resultado na tela Entrada.
6. Ao confirmar: copia o original para o Arquivo, extrai a capa e grava o `metadata.json`. O arquivo na Entrada é movido para `Entrada/_importados/`, não apagado.

## 7. Revisão e Editor

- **Revisão**: fila ordenada pela nota de qualidade. Para cada livro: candidatos com % de confiança e tabela *campo · atual · proposto*, com uma caixa de seleção por campo. Ações: Aprovar e próximo, Pular, Editar à mão.
- **Editor**: todos os campos da seção 4.3, créditos com função, capa (trocar ou usar a página 1), prévia dos nomes que cada destino vai gerar.
- **Edição em lote**: selecionar N livros e editar os campos em comum. Campos vazios não alteram nada. "Numerar em sequência" serve para o número da série.
- Toda gravação gera uma entrada em `.gibiteca/history` e pode ser desfeita.

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
3. **Limiares**: ≥ 0,90 aprova automaticamente (configurável; pode ficar sempre em "revisar"); de 0,50 a 0,90 vai para a Revisão com candidatos; abaixo disso, Revisão sem sugestão.
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
- **Estado por livro e destino** (tabela no índice): hash dos metadados mais hash do original usados na última geração. Se mudar, o livro fica "desatualizado".
- As configurações de destino são **por máquina** (`%APPDATA%\Gibiteca\settings.json`), porque caminhos como `Z:\` e o Kindle conectado são locais.

### 9.2 Destino Komga
- **Saída**: CBZ com `ComicInfo.xml`, em `{Pasta}/{Editora}/{Série}/{Série} v{Número:00} ({Ano}).cbz` (modelo editável em Avançado).
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
  - Linux: libmtp/gvfs (a validar). Como alternativa em qualquer sistema, existe o botão "Abrir pasta" para arrastar à mão.
- **Quadro a quadro**: o Kindle oferece "Visualização de painéis" (menu Aa, dois toques) em arquivos do KCC. Detecção real de quadros fica no backlog.
- **Padrão**: livros escolhidos, manual.
- **Riscos**: o KCC precisa do KindleGen para gerar MOBI/AZW3. O KindleGen vem no Kindle Previewer, que só existe para Windows e macOS, então no Linux a saída pode ficar limitada a EPUB. Também falta confirmar se a licença do KCC permite distribuí-lo junto com o app; senão, o app baixa ou aponta para uma instalação existente.

### 9.4 Backlog de tipos
Kavita, Kobo (KEPUB via KCC), Pasta simples (copiar em CBZ/PDF/EPUB), servidor OPDS, Calibre-Web.

## 10. Migração do Calibre (uma vez)

1. Lê o `metadata.db` do Calibre em **modo somente leitura**, como os scripts de análise já fazem.
2. Para cada livro, escolhe o melhor original:
   - tem `ORIGINAL_EPUB`: ele é o original da editora (o EPUB foi reprocessado pelo Calibre) — 25 casos;
   - tem PDF e EPUB convertido pelo Calibre a partir de PDF: o PDF é o original e o EPUB é descartado — ~40 casos;
   - EPUB sem assinatura de conversor: original da editora — ~550 casos;
   - EPUB convertido pelo Calibre sem outra fonte: importa com `quality: converted-by-calibre` e entra na lista "baixar de novo".
3. Converte os metadados: título, autores (como `writer`; nomes grudados com `|` são separados), série e índice, data (`0101-01-01` vira vazio), editora, ISBN (só os válidos), sinopse, tags e capa.
4. Roda primeiro em **simulação**, com um relatório do que seria feito, e só grava depois de confirmar.
5. Não toca na biblioteca do Calibre. Ela continua existindo como backup até o usuário decidir apagá-la.

## 11. Configurações

- **Gerais** (`%APPDATA%\Gibiteca\settings.json`): idioma (pt-BR, en), tema (escuro, claro, sistema), pasta do Arquivo, pasta de Entrada, fontes de metadados (ordem e liga/desliga), limiar de aprovação automática, destinos.
- **Segredos** (chaves de API) **não vão para arquivos de texto**: Windows DPAPI (`ProtectedData`) e, no Linux, libsecret.
- **Da biblioteca** (`gibiteca-library.json`, no Drive): só o que é da coleção, independente da máquina (versão do formato, id).

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
Gibiteca.sln
  src/
    Gibiteca.App/              # Avalonia: Views, ViewModels, temas, recursos (resx)
    Gibiteca.Core/             # domínio (Book, Credit, Destination…), regras de qualidade, serviços
    Gibiteca.Library/          # formato em disco, metadata.json, índice SQLite, importação, migração do Calibre
    Gibiteca.Metadata/         # IMetadataSource: OpenLibrary, ComicVine, GoogleBooks; identificação
    Gibiteca.Conversion/       # leitura de PDF/EPUB/CBZ/CBR, montagem de CBZ, ComicInfo.xml, execução do KCC
    Gibiteca.Destinations/     # IDestinationType + Komga + Kindle (MTP)
  tests/
    Gibiteca.Core.Tests/ …     # xUnit; amostras sintéticas, nunca HQs reais no repositório
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
- Toda operação em lote tem simulação, confirmação e log em `%LOCALAPPDATA%\Gibiteca\logs`.
- Nenhum segredo no repositório, nem nos arquivos do Drive.
- O app não envia dados para lugar nenhum além das fontes de metadados escolhidas.

## 15. Fases de entrega

| Fase | Entrega | Pronto quando |
|---|---|---|
| **F0 · Fundação** | Solução .NET, CI no GitHub Actions (build e testes), configurações, formato do Arquivo e índice | o app abre, aponta para uma pasta e cria a biblioteca vazia |
| **F1 · Migração + Biblioteca** | Importador do Calibre (simulação e execução), grade, filtros e nota de qualidade, painel | os 661 livros aparecem com as mesmas notas da análise em Python |
| **F2 · Metadados** | Open Library + ComicVine, identificação, Revisão, Editor (um livro e lote), histórico e desfazer | o lote 1 da análise (39 livros "Desconhecido") é corrigido pelo app |
| **F3 · Destino Komga** | Montagem de CBZ (PDF/EPUB/CBZ/CBR), ComicInfo.xml, modelo de nomes, estado de sincronização, scan opcional | a biblioteca aparece no Komga com séries e metadados corretos |
| **F4 · Destino Kindle** | Integração com o KCC, perfis, envio via MTP no Windows | um livro escolhido chega ao Colorsoft e abre com a visualização de painéis |
| **F5 · Entrada** | Pasta observada, identificação na chegada, duplicados e substituição | um download novo do Humble vai do download ao Komga sem passos manuais além da aprovação |
| **F6 · Polimento** | Inglês, tema claro, instalador Windows, build Linux, documentação de usuário | pronto para outra pessoa instalar sozinha |

### Backlog
Detecção real de quadros (Panel View com regiões por quadro), outros destinos (seção 9.4), leitura de ISBN por OCR da página de créditos, sugestão de séries faltantes, atualização automática do app, modo servidor/Docker.

## 16. Questões em aberto

1. Nome definitivo do app ("Gibiteca" é provisório) e licença (MIT? GPL?).
2. Endpoints e autenticação da API do Komga para disparar o scan.
3. KCC: licença para distribuir junto e comportamento sem KindleGen no Linux.
4. MTP no Linux: qual biblioteca/abordagem.
5. Aprovação automática ligada por padrão, ou tudo passa pela Revisão no começo?
6. Livros que não são HQ (artbooks, livros de design/games): entram nos destinos? Proposta: não, por padrão.

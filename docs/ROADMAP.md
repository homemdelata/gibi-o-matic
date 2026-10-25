# Roadmap

Status: ⬜ não iniciada · 🟨 em andamento · ✅ concluída. Seções `§` referem-se ao `SPEC.md`.

| Fase | Nome | Status |
|---|---|---|
| F0 | Fundação | ✅ |
| F1 | Importação + Biblioteca | ✅ |
| F2 | Metadados | ✅ |
| F3 | Destino Komga | ✅ |
| F3.5 | Leitor | ✅ |
| F3.6 | Universos | ✅ |
| F4 | Destino Kindle | ✅ (falta o teste do usuário com o Kindle: em 23/10 o Windows não reconheceu o aparelho; **testar de novo**) |
| F5 | Entrada automática | ✅ |
| F6 | Polimento e distribuição | ✅ |
| F7 | Revamp visual | ✅ (falta o teste do usuário) |

Ao terminar cada fase: build e testes verdes → atualizar este arquivo → commit e push → **parar e explicar ao usuário como testar**.

---

## F0 · Fundação

**Objetivo**: esqueleto sólido do app, rodando, com o visual do protótipo e a biblioteca vazia criada no disco.

- [x] Verificar o .NET 10 SDK; se não houver, pedir autorização e instalar (`winget install Microsoft.DotNet.SDK.10`). Criar o `global.json`.
- [x] `GibiOMatic.sln` com os projetos `src/` e `tests/` do `CLAUDE.md`.
  - `Directory.Build.props`: `Nullable=enable`, `TreatWarningsAsErrors=true`, `LangVersion=latest`, `ImplicitUsings`.
  - `Directory.Packages.props`: versões centralizadas.
- [x] **Shell da interface (Avalonia)**:
  - janela principal com o menu lateral do protótipo (Biblioteca, Entrada, Revisão, Editor, Destinos, Configurações);
  - páginas vazias com título;
  - navegação via ViewModels.
- [x] **Tema**: tokens de `docs/DESIGN.md` como recursos (cores, raios, espaçamentos), estilos dos componentes básicos (botão primário e secundário, chip, card, campo de texto, item de menu) e fontes embutidas (Bricolage Grotesque, IBM Plex Sans/Mono; licença OFL — registrar em `THIRD-PARTY.md`).
- [x] **i18n**: `resx` com pt-BR (padrão) e en; troca de idioma nas Configurações (pode exigir reiniciar).
- [x] **Configurações** (§11):
  - `ISettingsService` persistindo em `%APPDATA%\GibiOMatic\settings.json`, com escrita atômica;
  - `ISecretStore` com implementação DPAPI no Windows e um *stub* para Linux, a fazer.
- [x] **Formato da biblioteca** (§4):
  - criar e abrir uma biblioteca, gerando `gibiomatic-library.json` (id, versão do formato), `Livros/` e `.gibiomatic/`;
  - índice SQLite em `%LOCALAPPDATA%\GibiOMatic\index\<libraryId>.db`, com migrações de schema versionadas;
  - "reconstruir índice" varrendo os `metadata.json`.
- [x] **Primeira execução**: tela de boas-vindas → "Criar biblioteca nova" ou "Abrir existente" → escolher a pasta → app abre na Biblioteca (vazia, com estado vazio amigável). Na próxima abertura, lembra a biblioteca.
- [x] Log com Serilog em `%LOCALAPPDATA%\GibiOMatic\logs`.
- [x] Testes: serialização das configurações, criação e abertura da biblioteca, reconstrução do índice com `metadata.json` sintéticos.
- [x] **CI**: `.github/workflows/ci.yml` com build e testes em `windows-latest` e `ubuntu-latest`.
- [x] Iniciar `docs/DECISIONS.md` e `docs/THIRD-PARTY.md` com o que for adicionado.

**Pronto quando**:
- o app abre no visual do protótipo;
- o usuário cria uma biblioteca numa pasta de teste;
- ao fechar e abrir, a biblioteca é lembrada;
- o CI fica verde.

---

## F1 · Importação + Biblioteca

**Objetivo**: trazer os originais baixados para o Arquivo e vê-los na Biblioteca com nota de qualidade.

- [x] **Detecção de formato** pelo conteúdo (*magic bytes*): PDF, ZIP/CBZ, RAR/CBR, EPUB.
- [x] **Importação manual** (§6): botão "Importar arquivos…" e arrastar e soltar.
  - Calcula o SHA-256;
  - detecta duplicado exato (mesmo hash);
  - copia para `Livros/{Editora}/{Série}/{Título} [{id6}]/` (sem metadados ainda, cai em `_Sem editora/…`), com o nome limpo e o `downloadedAs` registrado;
  - escreve o `metadata.json` (§4.3) de forma atômica.
- [x] **Importação em massa** (§10):
  - fila persistida no índice, em segundo plano, com progresso, pausar e retomar;
  - sobrevive ao fechamento do app;
  - resumo final.
- [x] **Duplicados de formato**: o mesmo livro em vários formatos vira um livro só, com o original principal escolhido pela preferência CBZ > PDF > EPUB > CBR (configurável) e os demais como formatos extras. Na F1, a identificação do "mesmo livro" usa ISBN embutido ou título normalizado e número; a F2 refina.
- [x] **Metadados embutidos**: `ComicInfo.xml` (CBZ/CBR), OPF (EPUB) e Info/XMP (PDF) → preenchimento inicial do `metadata.json`.
- [x] **Capa** (`cover.jpg`):
  - CBZ/CBR: primeira imagem em ordem natural;
  - EPUB: a imagem de capa declarada no OPF;
  - PDF: renderizar a página 1 com PDFium (ver §13 e `reference/python/pdf2epub.py`).
  - Gerar miniaturas em cache local.
- [x] **Nota de qualidade** (§5): portar `reference/python/analyze.py` para `GibiOMatic.Core`, com testes cobrindo cada regra (títulos colados, "Desconhecido", autores grudados, "Untitled", série no título sem campo série etc.).
- [x] **Tela Biblioteca** (protótipo `Main.dc.html`):
  - grade virtualizada de capas com selo OK, Revisar ou Ruim;
  - alternância Séries/Livros;
  - busca;
  - filtros (Precisa revisão, Sem data, Sem série, Autor desconhecido, Completos);
  - cartões de resumo;
  - painel de detalhes do livro selecionado: o que falta, arquivos e ações, ainda desabilitadas.
- [x] **Tela Entrada**, versão manual (protótipo `Entrada.dc.html`): lista do que está sendo importado e tratamento de duplicados (Substituir, Manter os dois, Ignorar).
- [x] Desempenho: abrir uma biblioteca de 1000 livros em menos de 2 s com o índice pronto, e rolagem fluida.

**Pronto quando**: o usuário importa a pasta com os downloads novos e vê todos na Biblioteca, com capa e nota de qualidade, e os originais no Arquivo com o hash conferido.

---

## F2 · Metadados (§7, §8)

- [x] `IMetadataSource`: Open Library (ISBN), ComicVine (busca de volumes e edições, detalhes, créditos por função) e Google Books (opcional, desligado por padrão). Cada um com cache, limite de requisições e novas tentativas.
- [x] Identificação:
  - normalização do título (série, número, tipo TPB/avulsa);
  - candidatos;
  - pontuação;
  - filtros de editora, idioma e ano;
  - detecção de one-shot.
- [x] Aprovação automática segura (§8.2, item 3), com a marcação `matchedBy: auto` e o filtro "Aprovados automaticamente".
- [x] **Tela Revisão** (`Revisao.dc.html`): fila por gravidade, candidatos, tabela campo a campo com caixas de seleção, Aprovar e próximo, Pular.
- [x] **Editor** (`Editor.dc.html`):
  - um livro e lote;
  - créditos por função;
  - capa (trocar ou usar a página 1);
  - "É one-shot";
  - prévia dos nomes de saída por destino.
- [x] Histórico (`.gibiomatic/history`) e desfazer.
- [x] Renomear a pasta do livro quando editora, série ou título mudarem.
- [x] Configurações → Metadados: ordem e liga/desliga das fontes, chave do ComicVine (DPAPI), "Sempre revisar".

**Pronto quando**: a coleção inteira passa pela identificação, e a fila de Revisão é tratável em poucos cliques por livro.

---

## F3 · Destino Komga (§9.1, §9.2)

- [x] Modelo de destinos: `IDestinationType`/`IDestination`, regras "quais livros" e "quando", capacidades por tipo, estado de sincronização por livro (hashes).
- [x] Montagem do CBZ a partir de CBZ, CBR, PDF e EPUB:
  - PDF: extrai o JPEG original sem recompressão quando a página é uma imagem única que a cobre inteira; senão, renderiza (§9.2);
  - escreve o `ComicInfo.xml`.
- [x] Modelos de nome (padrão sem pasta de editora) e pasta de one-shots configurável.
- [x] Opção "Incluir livros que não são quadrinhos" (EPUB de texto vai direto).
- [x] Opcional: URL e chave do Komga para disparar o scan da biblioteca e comparar a configuração de one-shots. Endpoints confirmados no código-fonte do Komga (ver `RESEARCH.md`).
- [x] **Telas**: Destinos (cartões), detalhe do destino Komga e assistente "Adicionar destino" (`Destinos.dc.html`, `NovoDestino.dc.html`).
- [x] Estado da cópia de cada livro por destino no painel da Biblioteca; "Incluir em …" / "Tirar de …" na seleção, para destinos "Livros que eu escolher".
- [x] Testar com o usuário se `Editora/Série/` funciona no Komga dele (questão em aberto 3): testado no Komga do usuário, funcionou.

**Pronto quando**: a biblioteca aparece no Komga do usuário com séries, one-shots e metadados corretos, e editar um livro atualiza a cópia automaticamente.

---

## F3.5 · Leitor (pedido do usuário)

Objetivo: abrir um livro dentro do app para conferir qual edição é (capa grande, páginas internas, créditos) antes de escolher o metadado. Não substitui o Komga nem o Kindle para ler.

- [x] Leitor de imagens próprio para CBZ, CBR, PDF e EPUB de quadrinhos (uma imagem por página): só a página atual e as vizinhas na memória, com renderização em segundo plano.
- [x] EPUB de texto no `Avalonia.Controls.WebView` (WebView2 no Windows, WebKitGTK no Linux), com sumário, capítulo anterior/próximo, tamanho da fonte e tema escuro.
- [x] Segurança:
  - EPUB servido de dentro do arquivo, sem extrair para o disco, sem JavaScript, sem acesso à internet e sem navegar para fora do livro;
  - PDF renderizado pelo PDFium sem JavaScript/XFA, sem formulários e sem executar ações (links, arquivos anexos).
- [x] Recursos:
  - navegação por setas, clique nas laterais, roda do mouse, Home/End e contador "12 / 148";
  - ajuste à altura (padrão), à largura e tamanho real;
  - zoom com Ctrl+roda e arrastar;
  - faixa de miniaturas recolhível;
  - tela cheia (F11, Esc fecha);
  - página dupla, com a capa sozinha;
  - leitura da direita para a esquerda;
  - lembrar a última página de cada livro (no índice, nunca no original).
- [x] "Abrir" no painel da Biblioteca (capa, botão e cada arquivo) e na Revisão (capa e "Abrir livro"). O leitor abre numa janela própria, que pode ficar ao lado da Revisão enquanto os candidatos são comparados.
- [x] Status de leitura (pedido do usuário depois do primeiro teste):
  - duplo clique num card da Biblioteca abre o leitor;
  - "lendo" depois de mais de 5 páginas vistas numa sessão (abrir só para ver a capa não conta); "lido" ao chegar à última página de um livro já iniciado, ou marcado à mão;
  - filtros Lendo, Lidos e Não lidos;
  - no painel: Marcar como lido / não lido e Limpar progresso;
  - faixa "Continuar lendo" no topo da Biblioteca (até 8 livros, o mais recente primeiro) e barra de progresso nos cards em leitura.

**Pronto quando**: os quatro formatos abrem no leitor a partir da Biblioteca e da Revisão, e um livro reaberto volta à página em que estava.

---

## F3.6 · Universos (pedido do usuário)

Objetivo: agrupar séries do mesmo universo ou linha editorial (ex.: Radiant Black, Radiant Red e Radiant Pink no Massive-Verse). O ComicVine não tem esse campo, então o preenchimento é manual.

- [x] Campo `universes` (lista) no `metadata.json`; lido do `SeriesGroup` do ComicInfo na importação.
- [x] Editor: universos como etiquetas removíveis, com sugestões dos que já existem; no lote, adicionar ou tirar um universo vale para todos os livros, sem mexer nos outros universos de cada um.
- [x] Biblioteca: modo "Universos" (universo → séries → livros), busca por universo e universos no painel do livro.
- [x] Komga: universos no `SeriesGroup` do ComicInfo, que o Komga transforma em coleções.

**Pronto quando**: o usuário marca as séries de um universo em lote, vê o universo na Biblioteca e a coleção aparece no Komga depois de sincronizar.

---

## F4 · Destino Kindle (§9.3)

- [x] Integração com o KCC (`kcc-c2e`):
  - localizar ou configurar o executável (o baixado pelo app, o escolhido à mão ou o do PATH);
  - perfil do aparelho (Colorsoft = `KCS`), com cor (`--forcecolor`);
  - formato MOBI (que também é AZW3);
  - progresso pelas etapas que o KCC imprime e timeout.
  - Distribuir o KCC junto: **não** (o executável inclui PyMuPDF, AGPL). O app baixa o release oficial quando o usuário pede, com o SHA-256 conferido.
- [x] **Preparar sem o Kindle conectado** (pedido do usuário):
  - os livros escolhidos ("Livros que eu escolher") são convertidos em segundo plano, um por vez, numa pasta de preparo local (padrão `%LOCALAPPDATA%\GibiOMatic\kindle`, configurável; nunca no Drive);
  - status por livro: Na fila, Preparando, Pronto para enviar, No Kindle, Desatualizado (metadados mudaram depois de preparar → refaz sozinho).
- [x] **Enviar quando conectar**: entrega via MTP no Windows (detectar o Kindle, copiar para `documents/`). Ao conectar, uma faixa oferece "Kindle conectado: N livros prontos. Enviar?", com a lista e confirmação. O app lembra o que já enviou e não manda de novo. Depois de enviada, a cópia preparada é apagada (reenviar converte de novo). Linux: **a fazer**. O botão "Abrir pasta" funciona sempre.
- [x] **O app nunca apaga nada do Kindle sozinho.** Tirar um livro da lista não apaga do aparelho.
- [x] **Mini gerenciador do Kindle**: listar os livros da pasta `documents/` (marcando os que o app enviou) e apagar os escolhidos, só por ação manual e com confirmação.
- [x] **Tela do destino Kindle** (`DestinoKindle.dc.html`):
  - aba Enviar (a lista com o status do preparo, enviar);
  - aba No Kindle (o mini gerenciador);
  - aba Configurações com "Avançado" recolhido;
  - aba Histórico.
- [x] Os livros entram na lista pela Biblioteca (pedido do usuário): menu do botão direito num livro ("Pôr na lista do Kindle", e também abrir, marcar como lido/não lido, limpar progresso, editar e revisar metadados) ou o botão no painel; a tela do Kindle mostra só a lista. O painel mostra o estado da cópia.

**Pronto quando**: um livro escolhido chega ao Colorsoft do usuário e abre com a visualização de painéis.

---

## F5 · Entrada automática (§6)

- [x] Pasta observada (FileSystemWatcher, subpastas incluídas) com varredura ao abrir e a cada 5 minutos; um arquivo só entra quando para de mudar e ninguém está escrevendo nele.
- [x] Identificação na chegada; aprovados vão direto para a biblioteca e para os destinos automáticos. Os arquivos da pasta entram sem confirmação; os adicionados à mão continuam pedindo "Importar".
- [x] Substituição de original por versão melhor: a sugestão da Entrada é aplicada sozinha (decisão do usuário).
- [x] Mover os processados para `_importados/` (com as subpastas); idênticos ao que já está na biblioteca também (decisão do usuário); os com erro ficam. Subpastas que ficam vazias são apagadas (pedido do usuário).
- [x] Rodar em segundo plano (pedido do usuário), para a pasta continuar sendo observada com a janela fechada:
  - ícone na bandeja do sistema (Windows; Linux onde houver suporte), com menu (abrir, pausar a Entrada, sair) e o estado atual na dica;
  - fechar a janela deixa o app na bandeja (padrão); na primeira vez, um aviso explica e deixa escolher entre continuar na bandeja e fechar o app; a escolha fica nas Configurações → Geral;
  - opção de iniciar com o sistema (Windows: chave `Run` do usuário; Linux: `~/.config/autostart`), desligada por padrão; ao ligar, escolhe entre abrir só a bandeja (padrão) ou a janela;
  - aviso do sistema em resumo, por lote processado ("2 arquivos processados na Entrada · 1 livro aprovado · 1 para revisar"); clicar abre a Revisão (se há o que revisar), a Entrada (se houve erro) ou a Biblioteca;
  - uma instância só: abrir o app de novo traz a janela que já está rodando.
- [x] Barra lateral: linhas "Entrada" e "Metadados" no cartão de status, em todas as telas (pedido do usuário); a barra de metadados saiu de cima da Biblioteca.
- [x] Tela Entrada: cartão com a pasta observada (ou o aviso de que está desligada ou não foi encontrada) e a marca "automático" nos arquivos que vieram dela.

**Pronto quando**: um download novo do Humble vai do download ao Komga sem passos manuais, ou só com a aprovação.

---

## F6 · Polimento e distribuição

- [x] Interface em inglês completa (conferida: todas as chaves nas duas línguas, nada fixo no código), tema claro e "seguir o sistema".
- [x] Versão pela tag do git (MinVer) e `tools/publish.ps1` (build *self-contained* em `.zip`/`.tar.gz`).
- [x] Instalador para Windows e AppImage para Linux com Velopack (decisão do usuário), atualização automática pelo GitHub Releases e release gerado pelo CI numa tag `v*` (rascunho).
- [x] Teste do usuário (2026-10-25): instalação por cima e atualização automática a partir de uma pasta local (`GIBIOMATIC_UPDATE_SOURCE`), de 0.1.0-alpha.0.98 para 0.1.0-alpha.0.99.
- [x] Documentação de usuário (README com capturas geradas por `tools/Screenshots`) e `CONTRIBUTING.md`.
- [x] Revisão de acessibilidade (teclado, contraste): contraste AA dos dois temas e nome acessível de botões sem texto viraram testes; os botões da barra do leitor ganharam nome. Em aberto: botões de ícone com 34 px, abaixo dos 44 px da SPEC §12 (proposta ao usuário).

---

## F7 · Revamp visual (pedido do usuário)

Visual "Aranhaverso sutil", especificado em `docs/design/revamp/REVAMP.md`.

- [x] Ícone novo (`tools/IconGen`), `.ico` e logo da barra lateral (também na tela de boas-vindas).
- [x] Fonte Anybody e tokens novos dos dois temas (escuro azul-noite, claro papel creme), com os testes de contraste. Ajustes para o AA: `BadText` novo; no claro, `Bad` `#E8448A` e `OkText` `#0A6A85`.
- [x] Forma e controles, com a seleção com deslocamento (ciano à esquerda, magenta à direita).
- [x] Pilha de capas nas séries e universos, com testes da escolha das capas (`CoverStackTests`).
- [x] Retícula decorativa e enfeite do título.
- [x] Passada em todas as telas (Entrada, Revisão, Editor, Destinos, Configurações, leitor, boas-vindas e o aviso de fechar).
- [x] Capturas regeradas (o `tools/Screenshots` voltou a funcionar: o atualizador do Velopack quebrava o shell), `DESIGN.md` e `DECISIONS.md` atualizados.

**Pronto quando**: o usuário aprova os dois temas no app.

---

## Ajustes depois da F7 (pedidos do usuário)

- [x] Entrada: depois de importar, o arquivo vai para a Lixeira (padrão), para `_importados` ou é apagado, à escolha nas Configurações.
- [x] Biblioteca: livros não revisados ficam em `Livros/_a revisar/` e só vão para `{Editora}/{Série}/` depois de aprovados.

---

## Backlog
- Detecção real de quadros (Panel View com regiões por quadro).
- Destinos: Kavita, Kobo, Pasta simples, OPDS, Calibre-Web.
- Kindle: livros de texto; MTP no Linux.
- MOBI/AZW3 como originais (o usuário tem alguns): importação com metadados do cabeçalho (EXTH), capa, leitor (imagens e texto, sem DRM), CBZ para o Komga quando for quadrinho, envio direto ao Kindle.
- OCR do ISBN na página de créditos.
- Sugerir volumes faltantes de uma série.
- Modo servidor/Docker.
- Assinar o instalador (certificado de código), para o Windows não avisar "editor desconhecido".
- Associar os tipos de arquivo (CBZ, CBR, PDF, EPUB; opcional, por tipo) ao app, para ele ser o leitor padrão: abrir um arquivo pelo Explorer abre no leitor, esteja ou não na biblioteca (fora dela, oferece importar). Windows: registro por usuário (ou pelo instalador); Linux: `.desktop` com `MimeType`.

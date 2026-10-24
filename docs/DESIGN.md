# Design

O protótipo original foi feito no Claude Design (canvas privado do autor: https://claude.ai/artifact/RfPus5Wyu69J33vCsJMzxJ).
Uma cópia dos arquivos fica em `docs/design/prototype/`. Cada `.dc.html` é uma tela em HTML com estilos inline: **referência visual, não código a reaproveitar**. Abra no navegador para ver. Os links entre telas e algumas interações funcionam no Claude Design; abertas direto no navegador, as telas podem não renderizar por completo, porque dependem do runtime do canvas. Nesse caso, leia o markup.

## Telas
| Arquivo | Tela | Fase |
|---|---|---|
| `Main.dc.html` | Biblioteca | F1 |
| `Entrada.dc.html` | Entrada | F1 (manual), F5 (automática) |
| `Revisao.dc.html` | Revisão de metadados | F2 |
| `Editor.dc.html` | Editor (um livro e lote) | F2 |
| `Destinos.dc.html` | Lista de destinos | F3 |
| `NovoDestino.dc.html` | Assistente "Adicionar destino" (3 passos) | F3 |
| `DestinoKindle.dc.html` | Detalhe do destino Kindle (Enviar e Configurações) | F4 |
| `Configuracoes.dc.html` | Configurações (Pastas, Metadados, Geral) | F0/F2 |

Os dados nas telas (títulos, números) são exemplos da coleção do usuário. As capas são placeholders coloridos com retícula.

O protótipo define o **layout** das telas. O **visual** atual vem do revamp da F7 (24/10/2026), especificado em [`design/revamp/REVAMP.md`](design/revamp/REVAMP.md), com a Biblioteca redesenhada (`design/revamp/Biblioteca.dc.html`) e o ícone (`design/revamp/icone.svg`, fonte da verdade do desenho). Onde o protótipo e este documento divergem em cores, fontes ou forma, vale este documento.

## Identidade
**"Gibi impresso no escuro, com falha de registro"** (visual "Aranhaverso sutil"):
- fundo azul-noite; o tema claro é papel de gibi creme com tinta azul-noite;
- o amarelo-limão é o único destaque de ação;
- o ciano e o magenta do ícone aparecem **só** como deslocamento de impressão nas coisas selecionadas e em poucos enfeites (barrinhas do título, retícula decorativa);
- o resto é sóbrio: nada inclinado, nada em itálico, títulos sem efeito.

O ícone é um balão de fala em forma de G, amarelo, com falha de registro, sobre um quadrado azul-noite com retícula azul. `tools/IconGen` o desenha com SkiaSharp e gera `Assets/gibiomatic.ico` (16–256 px, com menos detalhes nos tamanhos pequenos) e `Assets/logo.png` (256 px, logo da barra lateral).

## Tokens
Ficam em `src/GibiOMatic.App/Themes/Tokens.axaml`, um dicionário por tema com as **mesmas chaves** (há teste para isso e para o contraste AA do texto). Sempre use `DynamicResource`.

### Cores
| Token | Escuro (padrão) | Claro (papel creme) | Uso |
|---|---|---|---|
| `Bg` | `#060B1C` | `#F4EEDD` | fundo da janela |
| `Surface` | `#0B1630` | `#FBF6E9` | menu lateral, painel de detalhes, chips inativos |
| `Card` | `#0F1D3D` | `#FFFBF0` | cartões |
| `Input` | `#0B1630` | `#FFFBF0` | campos, botões secundários |
| `Hover` | `#13244D` | `#EAE2CB` | hover, fundo de item selecionado em listas |
| `Border` | `#1E3266` | `#D9CFB4` | divisórias, bordas de cartões |
| `BorderStrong` | `#2A4580` | `#B9AD8E` | bordas de campos, botões secundários e chips |
| `Text` | `#F1F4FA` | `#0B1630` | texto principal |
| `TextSecondary` | `#CFD8EC` | `#26314F` | itens de menu inativos |
| `TextMuted` | `#9DB0D6` | `#4A5470` | legendas, metadados, rótulos de seção |
| `TextFaint` | `#8496BE` | `#5A6380` | placeholders |
| `Accent` | `#FFF27A` | `#FFF27A` | botão primário, seleção, chip marcado (o limão do ícone) |
| `AccentHover` | `#FFF7A8` | `#FFF7A8` | hover do primário |
| `OnAccent` | `#0B1630` | `#0B1630` | texto sobre o amarelo e sobre os selos |
| `AccentText` | `#FFF27A` | `#145E92` | o destaque como texto, ícone, foco e progresso (no creme, o amarelo não tem contraste) |
| `AccentBorder` | transparente | `#0B1630` | borda de 1,5 px do primário e dos chips marcados, para o limão não sumir no creme |
| `Ok` / `OkText` | `#19E3FF` / `#19E3FF` | `#0FB5D6` / `#0A6A85` | OK, conectado (o ciano do ícone) |
| `Warn` / `WarnText` | `#FFA45C` / `#FFB074` | `#D9692B` / `#A3480F` | Revisar, desatualizado (laranja) |
| `Bad` / `BadText` | `#FF2D87` / `#FF6FB0` | `#E8448A` / `#A3134D` | Ruim, baixa confiança (o magenta do ícone) |
| `BadSoft` | `#FF6FB0` | `#E8448A` | preenchimento sob texto escuro (contador da Revisão) |
| `GlitchCyan` / `GlitchMagenta` | `#19E3FF` / `#FF2D87` | `#0FB5D6` / `#E0186E` | deslocamento da seleção e enfeites |
| `HalftoneDot` | `#1D7FC0` | `#1D7FC0` | retícula decorativa |

- Amarelo significa ação e seleção. "Revisar" é laranja (`Warn`), nunca amarelo.
- As cores de status sempre vêm com texto ou ícone. Como texto, use as variantes `*Text`; os preenchimentos (selos, bolinhas, pílulas) usam `Ok`, `Warn` e `Bad`, com texto `OnAccent`.
- Ajustes de contraste em relação à spec do revamp: no tema claro, `Bad` virou `#E8448A` (com `#C2185B`, o texto do selo RUIM ficava com 3,05:1) e `OkText` virou `#0A6A85` (`#0B6E8A` dava 4,496:1 contra `Hover`); no escuro, `BadText` existe porque `#FF2D87` sobre `Hover` dá 4,31:1.

### Sombras (`BoxShadows`, por tema)
| Recurso | Valor (escuro) | Uso |
|---|---|---|
| `SelectionShadowCover` | ciano `-6 0 0 3`, magenta `6 0 0 3`, `Accent` `0 0 0 3` | capa selecionada (grade e painel de detalhes) |
| `SelectionShadowRing` | o mesmo, com 4 e 2 px | item selecionado da fila da Revisão |
| `SelectionShadow` | ciano `-3 0 0 0`, magenta `3 0 0 0` | item ativo do menu, candidato escolhido, miniatura atual do leitor, escolha do assistente |
| `SelectionShadowSmall` | ciano `-2 0 0 0`, magenta `2 0 0 0` | chips marcados, segmentos, abas, ferramenta ativa do leitor, item atual do sumário |
| `CoverShadow` | `5 5 0 0 #8C000000` (claro: `4 4 0 0 #380B1630`) | capas não selecionadas: sombra dura, de impressão |

O Avalonia desenha as sombras na ordem da lista, a última por cima (o contrário do CSS): o anel amarelo vem por último. Um controle cuja seleção sai dos próprios limites precisa de `ClipToBounds="False"` (os botões de capa e os itens do menu já têm).

### Tipografia
| Papel | Fonte | Tamanho/peso |
|---|---|---|
| Título de página (`h1`) | Anybody | 38 px / 800, letter-spacing −0,015em, **sem sombra nem deslocamento**; enfeite à direita (`TitleMark`: barrinha magenta 30×4 e ciano 16×3, recuada 8 px) |
| Título de seção ou cartão (`h2`, `h3`) | Anybody | 24 e 20 px / 800 |
| Números dos cartões de resumo | Anybody | 36 px / 800 |
| Nome do app no menu | Anybody | 21 px / 800 |
| Texto | IBM Plex Sans | 14 px / 400; rótulos 12,5 px |
| Ênfase | IBM Plex Sans | 600; botões primários e itens ativos, 700 |
| Rótulo de seção | IBM Plex Sans | 12 px / 600, maiúsculas (`UpperCase`), letter-spacing 0,1em, `TextMuted` |
| Caminhos, contagens, formatos, % | IBM Plex Mono | 12–13 px; selos 10,5 px / 600 |

As fontes são OFL e vão embutidas como TTFs estáticos (o Avalonia não seleciona eixos de fontes variáveis). A Anybody vai sempre em pé, nos pesos 700 e 800.

### Forma e espaço
| Recurso | Valor |
|---|---|
| `RadiusControl`, `RadiusField` (botões, campos, itens de menu) | 6 |
| `RadiusCard` (cartões, linha de arquivo) | 8 |
| `RadiusCardLarge` (escolhas grandes, ícone de página vazia) | 10 |
| `RadiusCover` (capas) | 4 |
| `RadiusBadge` (selos, segmentos) | 4 |
| `RadiusPill` (chips, pílulas) | 99 |
| `BorderControl` (bordas de controles e cartões) | 1,5 |

- Altura de controles: 44 px (`ControlHeight`); chips, 38 px.
- Espaçamento base de 4 px; gaps comuns: 8, 12, 16, 22, 28.
- Layout: menu lateral de 248 px, conteúdo fluido e painel de detalhes de 340 px (270 em janelas estreitas).
- Capas em proporção 2:3. Todo cartão da grade reserva 12 px no topo e à direita, onde as séries desenham os volumes de trás.

## Componentes
- **Seleção com deslocamento (a marca do visual)**: tudo que está selecionado, atual ou marcado ganha amarelo e um filete ciano à esquerda e outro magenta à direita (sombras acima). **Nada mais usa esse efeito**: nem hover, nem foco, nem botões primários, nem títulos. O foco do teclado é o contorno `AccentText` de sempre.
- **Item de menu**: 44 px, raio 6, ícone de traço de 18 px, rótulo e contador opcional (mono, ou pílula: Entrada em `Ok`, Revisão em `BadSoft`, texto `OnAccent` 700). Ativo: fundo `Accent`, texto e ícone `OnAccent`, peso 700 e filetes; margem lateral de 4 px para não cortar.
- **Quadro de status do menu**: fundo `Bg`, borda 1,5 `Border`, raio 8.
- **Botão primário**: fundo `Accent`, texto `OnAccent` 700, borda 1,5 `AccentBorder`, sem sombra. **Secundário**: fundo `Input`, borda 1,5 `BorderStrong`.
- **Campos e busca**: fundo `Input`, borda 1,5 `BorderStrong`, raio 6, lupa em `TextMuted`.
- **Chip de filtro**: pílula de 38 px. Inativo: fundo `Surface`, borda 1,5 `BorderStrong`, contador mono `TextMuted`. Marcado: fundo `Accent`, borda `AccentBorder`, peso 700 e filetes.
- **Segmentos** (Séries/Livros/Universos, abas do Kindle, decisões da Entrada): caixa com borda 1,5 `BorderStrong`, raio 6 e padding 4/6; o segmento escolhido é `Accent` com filetes.
- **Cartões de resumo**: fundo `Card`, borda 1,5 `Border`, raio 8, sem sombra. "Precisa revisão" tem filete superior de 3 px em `GlitchMagenta` e "prioridade" em `BadText`.
- **Capa na grade**: sombra dura; selecionada, `SelectionShadowCover`. **Selo**: canto superior direito, fundo na cor do status (OK `Ok`, REVISAR `Warn`, RUIM `Bad`), texto `OnAccent`, mono 10,5 px / 600, raio 4.
- **Série e universo (pilha de capas)**: até duas capas de trás, dos volumes seguintes na ordem de leitura (volumes sem número vão depois dos numerados), deslocadas (6, −6) e (12, −12), escurecidas 15% e 30%, com raio `RadiusCover` e borda 1,5 `Bg`. Selo de quantidade no canto inferior esquerdo: 28 px, fundo `Accent`, raio 6, ícone de pilha e número em mono 700 14 px. Linha de baixo: "Série · 3 livros · Editora".
- **Painel de detalhes**: fundo `Surface`; capa com a seleção; checks dos campos como ícones de traço 2,4 na cor do status; arquivo como cartão `Card` com raio 8 e etiqueta de formato com fundo `Border`.
- **Retícula decorativa** (`Border.halftone`): manchas de pontos que somem num degradê radial, atrás do conteúdo e sem clique. Topo direito da área principal (`HalftoneDot`, grade 9, opacidade 0,7), canto inferior esquerdo do menu (`GlitchMagenta`, grade 8, 0,22) e topo direito do painel de detalhes (`GlitchCyan`, grade 8, 0,2); no tema claro, metade da opacidade. A retícula das capas de placeholder continua a mesma.
- **Cartão de destino**: ícone em quadrado de 44 px, nome, tipo, chips de regras e faixa de status com bolinha colorida.
- **Linha de confiança**: "94% · título + número + editora", em mono, na cor do nível (≥ 80 `Ok`, ≥ 40 `Warn`, abaixo disso `Bad`).
- **"Avançado"**: botão que expande ("▸ Avançado" / "▾ Avançado") e revela uma seção com fundo `Surface`.
- **Ícones**: de traço, 2 px, cantos arredondados (no estilo Lucide), como geometria em `Themes/Icons.axaml`.

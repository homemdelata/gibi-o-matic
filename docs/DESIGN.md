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

## Identidade
**"Gibi impresso no escuro"**: interface escura para as capas se destacarem, amarelo de impressão como única cor de destaque e retícula (*halftone*) como textura discreta nas capas de placeholder.

## Tokens

### Cores (tema escuro, padrão)
| Token | Valor | Uso |
|---|---|---|
| `Bg` | `#121317` | fundo da janela |
| `Surface` | `#17181D` | menu lateral, painéis laterais |
| `Card` | `#1A1B21` | cartões, linhas de lista |
| `Input` | `#1D1F25` | campos, botões secundários com fundo |
| `Hover/Selected` | `#262931` | item de menu ativo, hover, chips neutros |
| `Border` | `#262931` | divisórias |
| `BorderStrong` | `#2E313A` | bordas de campos e botões secundários |
| `Text` | `#ECEDEF` | texto principal |
| `TextSecondary` | `#C9CCD3` | itens de menu inativos |
| `TextMuted` | `#A3A8B3` | legendas, metadados |
| `TextFaint` | `#8C919C` | placeholders, desabilitado |
| `Accent` | `#F5C542` | botão primário, seleção, ícone ativo, foco |
| `AccentHover` | `#FFD970` | links em hover |
| `OnAccent` | `#17181D` | texto sobre o amarelo |
| `Ok` | `#4CC38A` (texto `#6FD9A5`) | selo OK, conectado, alta confiança |
| `Warn` | `#F08A4B` (texto `#F5A574`) | Revisar, desatualizado |
| `Bad` | `#F2677A` | Ruim, baixa confiança |

As cores de status sempre aparecem acompanhadas de texto ou ícone, nunca sozinhas. O tema claro fica para a F6 (§12); defina os tokens de forma que ele seja só outro dicionário de recursos.

### Tipografia
| Papel | Fonte | Tamanho/peso |
|---|---|---|
| Título de página | Bricolage Grotesque | 34 px / 800, letter-spacing −0,025em |
| Título de seção/cartão | Bricolage Grotesque | 20–26 px / 700 |
| Números grandes (cartões de resumo) | Bricolage Grotesque | 30–40 px / 700 |
| Texto | IBM Plex Sans | 14 px / 400; rótulos 12,5 px |
| Ênfase | IBM Plex Sans | 600 |
| Rótulo de seção | IBM Plex Sans | 12 px / maiúsculas, letter-spacing 0,08em, cor `TextMuted` |
| Caminhos, contagens, formatos, % | IBM Plex Mono | 12–13 px |

Todas as fontes são do Google Fonts, com licença OFL, e vão embutidas no app.

### Forma e espaço
- Raios: campos e botões 9–11 px; cartões 14–16 px; chips e pílulas 99 px; capas 10 px.
- Altura de controles: 42–46 px, com área de toque ≥ 44 px.
- Espaçamento base 4 px; gaps comuns: 8, 12, 16, 22, 28.
- Layout: menu lateral de 232–260 px, conteúdo fluido e painel de detalhes de 320–360 px quando houver.
- Capas em proporção 2:3, com sombra `0 8px 22px rgba(0,0,0,.4)`; a selecionada tem contorno de 3 px `Accent` com offset de 3 px.

## Componentes
- **Item de menu**: ícone de traço de 18 px, rótulo e contador opcional (mono, ou pílula colorida para pendências). Quando ativo, fundo `Hover/Selected`, ícone `Accent` e peso 600.
- **Botão primário**: fundo `Accent`, texto `OnAccent` 600. **Secundário**: transparente, borda `BorderStrong`.
- **Chip de filtro**: pílula de 38 px. Ativo: fundo `Accent`. Inativo: borda `BorderStrong`, texto `TextSecondary`, contador mono.
- **Selo da capa**: canto superior direito, mono 10,5 px, fundo `Bg`, texto na cor do status (OK, REVISAR, RUIM).
- **Cartão de destino**: ícone em quadrado de 44 px, nome, tipo, chips de regras e faixa de status com bolinha colorida.
- **Linha de confiança**: "94% · título + número + editora", em mono, na cor do nível (≥ 80 `Ok`, ≥ 40 `Warn`, abaixo disso `Bad`).
- **"Avançado"**: botão que expande ("▸ Avançado" / "▾ Avançado") e revela uma seção com fundo `Surface`.
- **Ícones**: de traço, 2 px, cantos arredondados (no estilo Lucide). Sugestão: o pacote `Lucide.Avalonia` ou SVGs próprios; verificar a licença.

# Revamp visual "Aranhaverso sutil"

Especificação para levar ao app o visual escolhido pelo usuário em 24/10/2026 (opção **B2** do canvas de revamp, com o ícone **3a**).
Esta pasta tem as referências:

| Arquivo | O que é |
|---|---|
| `icone.svg` | Ícone novo. **Fonte da verdade** do desenho; abra no navegador. |
| `Biblioteca.dc.html` | Tela da Biblioteca no visual novo. É referência visual, não código para reaproveitar: leia o markup (cores, medidas, ordem dos elementos). Aberto direto no navegador, pode não renderizar por inteiro, porque depende do runtime do Claude Design. |

Só a Biblioteca foi desenhada. As outras telas seguem as mesmas regras, sem mudar o layout que já têm.

## Ideia
O de antes era "gibi impresso no escuro"; agora é **"gibi impresso no escuro, com falha de registro"**.
- O fundo é azul-noite, não mais cinza.
- O amarelo-limão é o único destaque de ação.
- O ciano e o magenta do ícone aparecem **só como deslocamento de impressão em coisas selecionadas** e em poucos enfeites.
- O resto é sóbrio: nada inclinado, nada em itálico, títulos sem efeito.

## 1. Ícone
O ícone é um balão de fala em forma de G, amarelo, com falha de registro, sobre um quadrado azul-noite com retícula azul. A geometria e as cores estão em `icone.svg` (viewBox 64).

**Reescreva o `tools/IconGen`** para desenhar esse ícone com SkiaSharp. Ele hoje desenha o quadrado amarelo com o traço.
- Desenhe, em ordem:
  1. o quadrado `#0B1630` com raio 14/64;
  2. a retícula `#1D7FC0`: grade de 3 unidades girada 30°, pontos de raio 0,85, com opacidade que some do canto superior direito até 65% da diagonal;
  3. os "fantasmas" do G em `#19E3FF` (x −2,4) e `#FF2D87` (x +2,4), com `SKBlendMode.Screen`;
  4. o G amarelo `#FFF27A` em 4 fatias horizontais: y 0–21 desloca 0; 21–28 desloca −3,5; 28–37 desloca +3; 37–64 desloca 0;
  5. as barrinhas: magenta `x41 y23,5 13×2` e ciano `x7 y34 9×1,6`.
- **Tamanhos pequenos precisam de simplificação.** Ainda assim, confira cada um:

  | Tamanho | Retícula | Fatias | Barrinhas | Fantasmas |
  |---|---|---|---|---|
  | ≤ 24 px | não | não (G inteiro) | não | sim, com deslocamento de pelo menos 1 px |
  | 32 px | sim | sim | não | sim |
  | ≥ 48 px | sim | sim | sim | sim |

- **Saídas:**
  - `src/GibiOMatic.App/Assets/gibiomatic.ico` (16–256 px, como hoje);
  - um PNG de 256 px para o logo da barra lateral, em `Assets/logo.png`, por exemplo.
- **Barra lateral (`ShellView.axaml`):** troque o `Border` amarelo com o `StrokeIcon` (`Icon.Logo`) por uma `Image` de 40×40 com esse PNG, em interpolação de alta qualidade. Remova `Icon.Logo` se ficar sem uso.
- O tray, o `.exe` e os instaladores Velopack já usam o `.ico`. Confira o tray em 16 px.

## 2. Tokens (`Themes/Tokens.axaml`)

### Tema escuro (padrão)
| Token | Novo valor | Antes |
|---|---|---|
| `Bg` | `#060B1C` | `#121317` |
| `Surface` (menu lateral, painel de detalhes) | `#0B1630` | `#17181D` |
| `Card` | `#0F1D3D` | `#1A1B21` |
| `Input` | `#0B1630` | `#1D1F25` |
| `Hover` | `#13244D` | `#262931` |
| `Border` | `#1E3266` | `#262931` |
| `BorderStrong` | `#2A4580` | `#2E313A` |
| `Text` | `#F1F4FA` | |
| `TextSecondary` | `#CFD8EC` | |
| `TextMuted` | `#9DB0D6` | |
| `TextFaint` | `#8496BE` | |
| `Accent` / `AccentText` | `#FFF27A` | `#F5C542` |
| `AccentHover` / `AccentTextHover` | `#FFF7A8` | |
| `OnAccent` | `#0B1630` | |
| `Ok` / `OkText` | `#19E3FF` (o ciano do ícone) | verde |
| `Warn` / `WarnText` | `#FFA45C` / `#FFB074` (continua laranja) | |
| `Bad` | `#FF2D87`; texto `#FF6FB0` (crie `BadText` se precisar) | |
| **novo** `GlitchCyan` | `#19E3FF` | |
| **novo** `GlitchMagenta` | `#FF2D87` | |
| **novo** `HalftoneDot` | `#1D7FC0` | |

No mockup, o selo REVISAR aparece amarelo. **Não siga isso:** "Revisar" continua `Warn` (laranja), para o amarelo continuar significando ação e seleção.

### Tema claro: papel creme (decisão do usuário)
O tema claro é papel de gibi creme, com tinta azul-noite.
| Grupo | Valores |
|---|---|
| Superfícies | `Bg #F4EEDD` (o creme), `Surface #FBF6E9`, `Card #FFFBF0`, `Input #FFFBF0`, `Hover #EAE2CB` |
| Bordas | `Border #D9CFB4`, `BorderStrong #B9AD8E` |
| Texto (tinta azul-noite) | `Text #0B1630`, `TextSecondary #26314F`, `TextMuted #4A5470`, `TextFaint #5A6380` |
| Destaque | `Accent #FFF27A`, o mesmo limão do ícone; `OnAccent #0B1630`; `AccentText #145E92`, azul, porque o amarelo não tem contraste como texto no creme |
| **novo** `AccentBorder` | `#0B1630`: borda de 1,5 px dos botões primários e dos chips marcados, para o limão não sumir no creme. No tema escuro, `Transparent`. |
| Glitch | `GlitchCyan #0FB5D6`, `GlitchMagenta #E0186E`, `HalftoneDot #1D7FC0` |
| Status | `Ok #0FB5D6` com `OkText #0B6E8A`; `Warn` e `WarnText` atuais; `Bad #C2185B` |

### Regras para os tokens
- Os dois temas precisam ter **as mesmas chaves**. Os testes de contraste (AA nos dois temas) e o de chaves iguais, em `GibiOMatic.App.Tests`, precisam continuar verdes. Ajuste algum tom se um par falhar, e diga qual.
- Crie também, por tema, os `BoxShadows` da seleção (seção 4) como recursos, para usar com `DynamicResource`.

### Forma
| Recurso | Valor |
|---|---|
| `RadiusControl` e `RadiusField` | 6 |
| `RadiusCard` | 8 |
| `RadiusCardLarge` | 10 |
| `RadiusCover` | 4 |
| Selos | 4 |
| Chips | pílula de 99, como hoje |
| Bordas de controles e cartões | 1,5 px |

## 3. Tipografia
- **Títulos e números grandes: Anybody** (Google Fonts, OFL), pesos 700 e 800, **sempre em pé**. Ela substitui a Bricolage Grotesque.
  - Use **TTFs estáticos** (pasta `static/` do pacote do Google Fonts), conforme a decisão "Fontes estáticas, não variáveis".
  - Acrescente `OFL-Anybody.txt` e registre em `docs/THIRD-PARTY.md`.
  - Se a Bricolage ficar sem uso, remova os arquivos e a entrada.
- O texto continua em IBM Plex Sans; caminhos e números pequenos continuam em IBM Plex Mono.

| Papel | Estilo |
|---|---|
| Título de página | Anybody 800, 38 px, letter-spacing −0,015em, **sem sombra nem deslocamento** |
| Enfeite do título | Logo à direita do título, duas barrinhas empilhadas: magenta 30×4 e ciano 16×3, recuada 8 px. Use `GlitchMagenta` e `GlitchCyan`. |
| Título de seção ou cartão | Anybody 800, 20–24 px |
| Números dos cartões de resumo | Anybody 800, 36 px |
| Rótulo de seção | Plex Sans 600, 12 px, maiúsculas, letter-spacing 0,1em, `TextMuted` |
| Nome do app no menu | Anybody 800, 21 px |

## 4. Seleção com deslocamento (a marca do visual)
Tudo que está **selecionado, atual ou marcado** ganha amarelo e um filete ciano à esquerda e outro magenta à direita, como a falha de registro do ícone.
**Nada mais usa esse efeito.** Ficam de fora o hover, o foco, os botões primários e os títulos.

| Elemento | Como fica |
|---|---|
| Capa selecionada (`cover-card` selecionado e capa do painel de detalhes) | `BoxShadow`: `0 0 0 3 Accent, -6 0 0 3 GlitchCyan, 6 0 0 3 GlitchMagenta` |
| Item ativo do menu | Fundo `Accent`, texto `OnAccent` peso 700, ícone `OnAccent`. Sombras `-3 0 0 0 GlitchCyan, 3 0 0 0 GlitchMagenta`. Margem lateral de 4 px para não cortar. |
| Chip marcado e segmento selecionado | Fundo `Accent` com sombras `-2 0 0 0` / `2 0 0 0` |
| Outros estados de seleção (`queue-item.selected`, `candidate.selected`, `thumb.current`, `toc.current`, `tool.on`…) | A mesma regra, proporcional ao tamanho: filetes de 2–3 px nos controles, 6 px nas capas |

- O foco do teclado continua sendo o contorno `AccentText` de sempre. Não pode depender do deslocamento.
- **Capas não selecionadas:** sombra dura `5 5 0 0 #8C000000` no lugar da sombra difusa. No tema claro, use uma sombra dura mais clara.

## 5. Séries e universos na Biblioteca (pilha de capas)
Hoje o cartão de série é uma capa com um cartão cinza genérico atrás. Passa a ser **uma pilha com as capas dos próximos volumes**:
- **Camadas de trás.**
  - Com 2 livros, há 1 camada atrás; com 3 ou mais, 2.
  - Deslocamentos: (6, −6) para a 1ª camada e (12, −12) para a 2ª.
  - Cada camada mostra a miniatura do volume (ou a cor de placeholder com retícula), escurecida: uma sobreposição preta de ~15% na 1ª e ~30% na 2ª.
  - As camadas têm raio `RadiusCover` e borda de 1,5 px na cor `Bg`, para separar uma da outra.
- **Alinhamento:** **todo** cartão da grade, avulso ou série, reserva 12 px no topo e à direita, para as capas ficarem alinhadas.
- **Selo de quantidade:** canto inferior esquerdo da capa da frente, 28 px de altura, fundo `Accent`, raio 6, ícone de pilha mais o número em Plex Mono 700 14 px, sombra suave. Mantenha o `ToolTip` e o `AutomationProperties.Name` que já existem ("3 livros na série").
- **Linha de baixo:** "Série · 3 livros · Editora". Os textos vão em `Strings.resx` (pt-BR) e `Strings.en.resx`.
- **ViewModel:**
  - `SeriesCardViewModel`, e o equivalente de universo, ganham até 2 capas de trás, dos volumes seguintes na ordem de leitura.
  - Reaproveite o `CoverViewModel` (miniatura ou cor de placeholder).
  - Escreva testes de unidade da escolha das capas: 1 livro, 2 livros, 3 ou mais, e volumes sem número.
- O cartão de universo usa a mesma pilha.

## 6. Componentes (`Themes/Controls.axaml` e telas)

### Menu e cartões
- **Menu lateral:**
  - fundo `Surface`;
  - itens com 44 px de altura e raio 6;
  - contador de Entrada numa pílula `Ok` (ciano) e de Revisão numa pílula `Bad` em tom de texto (`#FF6FB0`), as duas com texto `OnAccent`;
  - o quadro de status embaixo fica com fundo `Bg`, borda 1,5 `Border` e raio 8.
- **Cartões de resumo:** fundo `Card`, borda 1,5 `Border`, raio 8, sem sombra. "Precisa revisão" leva uma borda superior de 3 px em `GlitchMagenta` e o texto "prioridade" em `BadText`.

### Botões e campos
| Controle | Visual |
|---|---|
| Primário | Fundo `Accent`, texto `OnAccent` 700, raio 6, borda 1,5 `AccentBorder`, **sem sombra** |
| Secundário | Fundo `Input`, borda 1,5 `BorderStrong`, raio 6 |
| Busca e campos | Fundo `Input`, borda 1,5 `BorderStrong`, raio 6, ícone de lupa em `TextMuted` |
| Chips inativos | Fundo `Surface`, borda 1,5 `BorderStrong`, contador em mono `TextMuted` |

### Selos e painel de detalhes
- **Selos das capas:** fundo na cor do status, texto `OnAccent`, Plex Mono 600 10,5 px, raio 4. OK = `Ok` (ciano), RUIM = `Bad`, REVISAR = `Warn`.
- **Painel de detalhes:**
  - fundo `Surface`;
  - checks dos campos em `Ok` (ciano), com traço de 2,4;
  - linha do arquivo como cartão com fundo `Card` e raio 8;
  - etiqueta de formato (CBZ) com fundo `Border`.

## 7. Retícula decorativa
São três manchas de pontos que somem num degradê radial, todas com `IsHitTestVisible="False"` e atrás do conteúdo.
- Use um `DrawingBrush` em mosaico, como o `HalftoneBrush`, com `OpacityMask` de `RadialGradientBrush`.

| Onde | Pontos | Opacidade |
|---|---|---|
| Topo direito da área principal, atrás do título (~520×360) | `HalftoneDot`, grade 9, raio 1,5 | 0,7 |
| Canto inferior esquerdo do menu lateral (~300×300) | `GlitchMagenta`, grade 8 | 0,22 |
| Topo direito do painel de detalhes (~260×260) | `GlitchCyan`, grade 8 | 0,2 |

- No tema claro, use mais ou menos metade dessas opacidades.
- A retícula das capas de placeholder continua como está.

## 8. Escopo e ordem sugerida
Trate o trabalho como uma fase nova no `ROADMAP.md` ("F7 · Revamp visual") e siga o fluxo do `CLAUDE.md`: build e testes verdes, commits pequenos em pt-BR e parada no fim para o usuário testar.

1. Ícone: `IconGen`, `.ico`, logo da barra lateral.
2. Fontes e tokens dos dois temas, mais os testes de contraste.
3. Forma e controles (`Controls.axaml`), incluindo a seleção com deslocamento.
4. Pilha de séries e universos, com os testes do ViewModel.
5. Retícula decorativa e enfeite do título.
6. Passada em **todas** as telas: Entrada, Revisão, Editor, Destinos, Configurações, leitor, boas-vindas e diálogos. Procure cores fixas no XAML, raios antigos e sombras difusas.
7. Regere as capturas (`dotnet run --project tools/Screenshots`) e atualize o `README.md` se precisar.
8. Atualize o `docs/DESIGN.md`: identidade, tokens, tipografia, forma e componentes, apontando para esta pasta. Registre no `docs/DECISIONS.md`:
   - a fonte Anybody;
   - a regra da seleção com deslocamento;
   - o OK em ciano e o Ruim em magenta;
   - a paleta do tema claro.

## O que perguntar ao usuário (não decida sozinho)
- O tema claro: mostre uma captura dele pronto antes do commit final.
- Qualquer mudança de layout ou de texto além do descrito aqui.

# Pesquisa e descobertas

Tudo aqui foi verificado em 2026-10-16/17, numa sessão de análise da antiga biblioteca Calibre do usuário. Revalide o que depender de serviços externos antes de confiar cegamente.

## Metadados

### Por que o Calibre não servia
- Plugin ComicVine 0.14.2 do Calibre: conecta e a chave funciona, mas busca só **edições avulsas** ("issues"). Para encadernados ele erra:
  - "Saga, Vol. 01" → *Cable #73*;
  - "Deadly Class, Vol. 11" → a edição avulsa #11 (2015) em vez do TPB (2022);
  - "House of Slaughter #1" → a edição **alemã** da Splitter.
- O plugin também lista editores e letristas como "autores".
- A fonte Google do Calibre recebe **HTTP 500**. A fonte Amazon falha porque depende de busca no Google (bloqueada) ou no Bing (resultados brasileiros irrelevantes).

### ComicVine API
- Base: `https://comicvine.gamespot.com/api/`. Parâmetros: `api_key`, `format=json`, `field_list` para reduzir a resposta. Exige User-Agent próprio.
- Busca: `search/?resources=volume&query=...` funcionou. Exemplo para "Deadly Class":
  - a série de avulsas é "Deadly Class" (Image, 2014, 56 edições);
  - **os encadernados aparecem como volumes próprios**: "Deadly Class: Reagan Youth" (2014, 1 edição), "Deadly Class: The Snake Pit", "Deadly Class: Carousel" etc.;
  - edições estrangeiras também aparecem (Urban Comics, Cross Cult), e é preciso filtrar por editora e idioma.
- Limites de uso: há limite por recurso por hora (documentação oficial; confirmar o número atual). Use cache, limite de requisições e espera entre tentativas.
- Termos: a chave é pessoal; cada usuário do app usa a sua.

### Open Library
- `https://openlibrary.org/api/books?bibkeys=ISBN:<isbn>&format=json&jscmd=data` acertou de primeira:
  - `9781534321236` → *Deadly Class, Vol. 11* (Rick Remender, Wes Craig, 2022);
  - `9781607067238` → *Saga* (Brian K. Vaughan, Fiona Staples, 2012).
- Sem chave. Bom primeiro passo quando há ISBN.

### Google Books
- `https://www.googleapis.com/books/v1/volumes?q=isbn:...` respondeu **429** (muitas requisições) sem chave. Por isso fica opcional e desligado por padrão.

### Problemas típicos de metadados (da análise de 661 livros)
- Títulos que são nomes de arquivo (`somethingiskillingthechildren vol3`, `wyndbookone flightoftheprince`) com autor "Desconhecido".
- Vários autores grudados num só (o Calibre troca vírgula por `|`): `James Tynion IV| Chris O\'Halloran| Gavin Fullerton`.
- A data do Humble Bundle (10–14/12/2015) usada como data de publicação.
- Séries vazias em títulos com "Vol. N".
- Autores com 5 a 12 nomes, misturando roteiristas com coloristas e letristas.
- ISBNs falsos (`hum-irredeemable_vol3`, códigos Diamond como `MAY160548`).
- A regra de pontuação está em `reference/python/analyze.py`.

## Arquivos dos quadrinhos

### PDFs do Humble
Observado em 3 livros:
- **CLOSET Vol. 1** (114 p.) e **Dead Lucky Vol. 2** (87 p.): cada página é **um único JPEG** cobrindo a página inteira (cerca de 1988×3056), sem texto nem vetores. Dá para extrair os bytes originais sem perda.
- **Radiant Black Vol. 5** (170 p.): só 17 páginas são assim. As demais têm várias imagens sobrepostas (PNG com máscara) e **letreiramento vetorial** por cima, então precisam ser **renderizadas**: na resolução da maior imagem (cerca de 2000×3060), JPEG qualidade 92.
- Heurística validada (`reference/python/pdf2epub.py`): imagem única + cobre a página (tolerância de 2 pt) + sem rotação + sem texto + sem desenhos vetoriais + JPEG RGB/cinza sem máscara → copiar os bytes. Senão → renderizar.
- Tamanhos: PDF de 104 MB → saída de 105 MB (cópia sem perda); 381 MB → 255 MB (renderizado).
- O script usa PyMuPDF (**AGPL — não usar no app**). Em C#: PdfPig (Apache-2.0) para inspecionar e extrair imagens, PDFium para renderizar.

### EPUBs
- A conversão PDF→EPUB do Calibre gera EPUB reflow com PNGs de até 50 MB por página e arquivos 2–3× maiores que o PDF. Evitar.
- EPUBs de editora/Humble geralmente têm uma imagem por página (BOOM usa `hum-<livro>-0001.xhtml`; Image usa `OPS/fb.opf`; IDW inclui `com.apple.ibooks.display-options.xml`). Para extrair páginas, siga a ordem do *spine* do OPF.
- EPUB de layout fixo tem `rendition:layout = pre-paginated` no OPF.

## Kindle Colorsoft
- Os Kindles de 2024 em diante usam **MTP** em vez de armazenamento USB. No Windows, o MTP é nativo: o Kindle aparece como dispositivo portátil, e é preciso escolher "Transferir arquivos" na notificação de USB do Kindle e usar cabo de dados.
- O **KCC (Kindle Comic Converter)** tem o perfil **`KCS` = Kindle Colorsoft (1272×1696)** e recomenda **MOBI** (dual MOBI/AZW3) para Kindles modernos. Linha de comando: `kcc-c2e [opções] [entrada]`, com `-p` (perfil), `-f` (formato) e `-o` (saída).
- **Visualização de painéis**: em arquivos do KCC, liga-se no Kindle (menu **Aa**) e funciona com dois toques. Navega por regiões da página, não pelos quadros reais.
- O **Send to Kindle** comprime as imagens e não aceita MOBI/AZW3. O Colorsoft não lê CBZ.
- **Licença do KCC: ISC** (permissiva; manter o aviso de copyright). O KCC usa o KindleGen, que vem no Kindle Previewer (só Windows e macOS), para gerar MOBI.

## Komga
- Cria uma **série para cada subpasta que contém livros**, em qualquer profundidade. Cada arquivo vira um livro da série da sua pasta.
  - Não confirmado: se pastas sem arquivos, como `Editora/`, são ignoradas. Testar.
- **One-Shots directory** (opção da biblioteca): qualquer pasta cujo caminho contenha o texto configurado (ex.: `_oneshots`) gera uma série de um livro só por arquivo. Prefixar com `/` faz casar só pelo início do nome da pasta.
  - Para one-shots, o título e a sinopse da série vêm do livro, o status fica "Ended" e a contagem fica em 1.
- Formatos: CBZ, CBR, PDF, EPUB. Importa metadados do `ComicInfo.xml` (CBZ/CBR) e do OPF (EPUB).
- EPUB: suportado desde a 1.8.0, com leitor web próprio para reflow. Desde a 1.9.0, EPUBs pré-paginados só de imagens usam o leitor de quadrinhos.
- API REST para disparar o scan: **a confirmar** na referência da API.
- Sincronização com o KOReader: OPDS v1 e o papel `KOREADER_SYNC`.

## Fontes
- KCC: https://github.com/ciromattia/kcc · licença: https://github.com/ciromattia/kcc/blob/master/LICENSE.txt
- Komga: https://komga.org/docs/guides/scan-analysis-refresh · https://komga.org/docs/guides/oneshots · https://komga.org/docs/introduction · https://komga.org/blog/ebook-support · https://komga.org/docs/guides/koreader
- MTP nos Kindles novos: https://goodereader.com/blog/kindle/new-kindle-e-readers-no-longer-appear-on-computers
- Calibre FAQ: https://manual.calibre-ebook.com/faq.html
- MobileRead (Colorsoft + KCC): https://www.mobileread.com/forums/showthread.php?p=4461987

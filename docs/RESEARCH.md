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

### Amazon e Goodreads (2026-10-25)
- **Amazon:** não há API utilizável. A Product Advertising API exige conta de afiliado com vendas, e ler as páginas vai contra os termos e é bloqueado. Mas o link já ajuda: na edição **impressa**, o ASIN do link (`/dp/1607066017`, `/gp/product/…`) é o próprio **ISBN-10**. Na edição Kindle, o ASIN começa com `B0` e não é ISBN.
- **Goodreads:** a API pública fechou em dezembro de 2020. O link (`/book/show/15704307-saga-volume-1`) só tem o número interno do Goodreads, e ler a página vai contra os termos. O ISBN aparece na página, em "Detalhes da edição".
- Por isso a Revisão usa dos dois só o ISBN: o do link da Amazon, ou um ISBN colado pelo usuário. A busca é feita pelo Open Library e pelo Google Books.

### ComicVine API
- Base: `https://comicvine.gamespot.com/api/`. Parâmetros: `api_key`, `format=json`, `field_list` para reduzir a resposta. Exige User-Agent próprio.
- Busca: `search/?resources=volume&query=...` funcionou. Exemplo para "Deadly Class":
  - a série de avulsas é "Deadly Class" (Image, 2014, 56 edições);
  - **os encadernados aparecem como volumes próprios**: "Deadly Class: Reagan Youth" (2014, 1 edição), "Deadly Class: The Snake Pit", "Deadly Class: Carousel" etc.;
  - edições estrangeiras também aparecem (Urban Comics, Cross Cult), e é preciso filtrar por editora e idioma.
- Limites de uso: há limite por recurso por hora (documentação oficial; confirmar o número atual). Use cache, limite de requisições e espera entre tentativas.
- Termos: a chave é pessoal; cada usuário do app usa a sua.
- **Verificado em 2026-10-17 com chamadas reais** (F2):
  - Limite documentado: 200 requisições por recurso (`search`, `issues`, `issue`…) por hora, mais detecção de velocidade. O app espaça 1,2 s entre chamadas e para em 190 por recurso por hora.
  - O encadernado é um volume próprio com 1 edição, e o **nome da edição** traz o número do TPB: "Deadly Class: A Fond Farewell Part One" → edição "Volume 11"; "Deadly Class: Reagan Youth" → "Vol. 1". A descrição do volume diz "Trade paperback collecting …".
  - `issues/?filter=volume:A|B|C` traz as edições de vários volumes numa chamada; `filter=volume:A|B,issue_number:11` combina os dois filtros (funcionou).
  - Detalhe da edição (`issue/4000-ID`): `person_credits` com `role` por pessoa, às vezes várias separadas por vírgula ("letterer, designer"); `store_date` (data de venda) e `cover_date` (meses à frente). O app usa `store_date`.
  - Respostas de erro vêm com HTTP 200 e `status_code`: 100 = chave inválida, 107 = limite excedido, 101 = não encontrado.
  - Não há busca por ISBN no ComicVine; o ISBN vai para a Open Library, e o título dela orienta a busca no ComicVine.
  - Custo por livro: 1 busca + 1 ou 2 listagens de edições + 1 detalhe ≈ 200 livros por hora.

### Open Library
- `https://openlibrary.org/api/books?bibkeys=ISBN:<isbn>&format=json&jscmd=data` acertou de primeira:
  - `9781534321236` → *Deadly Class, Vol. 11* (Rick Remender, Wes Craig, 2022);
  - `9781607067238` → *Saga* (Brian K. Vaughan, Fiona Staples, 2012).
- Sem chave. Bom primeiro passo quando há ISBN.
- Os autores vêm **sem função** (`9781534321236` lista o roteirista Rick Remender e o desenhista Wes Craig como autores), por isso o app não usa esses nomes como créditos; os créditos vêm do ComicVine.

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
- O script usa PyMuPDF (**AGPL — não usar no app**). Em C#, o próprio PDFium faz as duas coisas: `FPDFPage_CountObjects`/`FPDFPage_GetObject`, `FPDFPageObj_GetBounds`/`GetMatrix`, `FPDFImageObj_GetImageFilter` e `GetImageDataRaw` (bytes do JPEG sem decodificar) para inspecionar e extrair, e `FPDF_RenderPageBitmap` para renderizar.

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
- API REST (conferida no código-fonte, `LibraryController.kt` do gotson/komga):
  - autenticação por chave de API no cabeçalho `X-API-Key` (a chave é criada na conta do usuário, no Komga);
  - `GET /api/v1/libraries` → lista com `id`, `name`, `root` e `oneshotsDirectory` (entre outros);
  - `POST /api/v1/libraries/{id}/scan` (parâmetro opcional `deep`) → 202; exige papel ADMIN, senão 403.
- Sincronização com o KOReader: OPDS v1 e o papel `KOREADER_SYNC`.

## Fontes
- KCC: https://github.com/ciromattia/kcc · licença: https://github.com/ciromattia/kcc/blob/master/LICENSE.txt
- Komga: https://github.com/gotson/komga/blob/master/komga/src/main/kotlin/org/gotson/komga/interfaces/api/rest/LibraryController.kt · https://komga.org/docs/guides/scan-analysis-refresh · https://komga.org/docs/guides/oneshots · https://komga.org/docs/introduction · https://komga.org/blog/ebook-support · https://komga.org/docs/guides/koreader
- MTP nos Kindles novos: https://goodereader.com/blog/kindle/new-kindle-e-readers-no-longer-appear-on-computers
- Calibre FAQ: https://manual.calibre-ebook.com/faq.html
- MobileRead (Colorsoft + KCC): https://www.mobileread.com/forums/showthread.php?p=4461987

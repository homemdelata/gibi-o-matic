"""Converte PDF de HQ em EPUB3 de layout fixo (uma página por tela).

Uso: python pdf2epub.py <entrada.pdf> <saida.epub> [--title T] [--author A]

Por página:
- se a página é só uma imagem JPEG RGB/cinza cobrindo a página toda, a imagem
  original é copiada byte a byte (sem perda nem recompressão);
- senão (várias imagens, texto, vetores, CMYK...), a página é renderizada
  na resolução da maior imagem dela e salva como JPEG de alta qualidade.
"""
import argparse, html, io, uuid, zipfile, datetime
import pymupdf
from PIL import Image

JPEG_QUALITY = 92
MAX_HEIGHT = 3200          # limite ao renderizar páginas compostas
DEFAULT_HEIGHT = 2400      # páginas sem imagens (só vetor/texto)


def page_image(doc, page):
    """Retorna (bytes_jpeg, largura, altura, modo) para a página."""
    infos = page.get_image_info(xrefs=True)
    r = page.rect
    has_text = bool(page.get_text().strip())
    has_vectors = len(page.get_drawings()) > 0
    if len(infos) == 1 and not has_text and not has_vectors:
        info = infos[0]
        b = pymupdf.Rect(info['bbox'])
        covers = (b.x0 <= r.x0 + 2 and b.y0 <= r.y0 + 2 and b.x1 >= r.x1 - 2 and b.y1 >= r.y1 - 2)
        upright = abs(info['transform'][1]) < 1e-6 and abs(info['transform'][2]) < 1e-6 \
            and info['transform'][0] > 0 and info['transform'][3] > 0
        if covers and upright and info['xref']:
            raw = doc.extract_image(info['xref'])
            if raw['ext'] in ('jpeg', 'jpg') and raw.get('cs-name', '') not in ('DeviceCMYK',) and raw['colorspace'] in (1, 3) \
                    and not raw.get('smask'):
                return raw['image'], raw['width'], raw['height'], 'original'
    # renderiza: altura alvo = maior imagem da página (sem upscaling exagerado)
    target_h = DEFAULT_HEIGHT
    sizes = [doc.extract_image(i['xref'])['height'] * r.height / max(1, pymupdf.Rect(i['bbox']).height)
             for i in infos if i['xref']]
    if sizes:
        target_h = max(sizes)
    target_h = min(MAX_HEIGHT, max(1200, target_h))
    zoom = target_h / r.height
    pix = page.get_pixmap(matrix=pymupdf.Matrix(zoom, zoom), colorspace=pymupdf.csRGB, alpha=False)
    img = Image.frombytes('RGB', (pix.width, pix.height), pix.samples)
    buf = io.BytesIO()
    img.save(buf, 'JPEG', quality=JPEG_QUALITY, optimize=True, subsampling=0)
    return buf.getvalue(), pix.width, pix.height, 'renderizada'


def build_epub(pdf_path, out_path, title, author):
    doc = pymupdf.open(pdf_path)
    book_id = f'urn:uuid:{uuid.uuid4()}'
    now = datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')
    stats = {'original': 0, 'renderizada': 0}
    pages = []
    with zipfile.ZipFile(out_path, 'w') as z:
        z.writestr(zipfile.ZipInfo('mimetype'), 'application/epub+zip', compress_type=zipfile.ZIP_STORED)
        z.writestr('META-INF/container.xml', '<?xml version="1.0"?>\n<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>', compress_type=zipfile.ZIP_DEFLATED)
        for n, page in enumerate(doc, 1):
            data, w, h, mode = page_image(doc, page)
            stats[mode] += 1
            name = f'p{n:04d}'
            z.writestr(f'OEBPS/img/{name}.jpg', data, compress_type=zipfile.ZIP_STORED)
            z.writestr(f'OEBPS/{name}.xhtml', f'''<?xml version="1.0" encoding="utf-8"?>
<!DOCTYPE html>
<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
<head><title>{n}</title><meta name="viewport" content="width={w}, height={h}"/>
<style>html,body{{margin:0;padding:0;width:{w}px;height:{h}px;overflow:hidden}}img{{display:block;width:{w}px;height:{h}px}}</style></head>
<body><img src="img/{name}.jpg" alt="Página {n}"/></body></html>''', compress_type=zipfile.ZIP_DEFLATED)
            pages.append((name, w, h))
        first_w, first_h = pages[0][1], pages[0][2]
        t, a = html.escape(title), html.escape(author)
        manifest = '\n'.join(
            f'<item id="x{nm}" href="{nm}.xhtml" media-type="application/xhtml+xml"/>'
            f'<item id="i{nm}" href="img/{nm}.jpg" media-type="image/jpeg"{" properties=\"cover-image\"" if i == 0 else ""}/>'
            for i, (nm, _, _) in enumerate(pages))
        spine = '\n'.join(f'<itemref idref="x{nm}"/>' for nm, _, _ in pages)
        z.writestr('OEBPS/content.opf', f'''<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid" prefix="rendition: http://www.idpf.org/vocab/rendition/#">
<metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
<dc:identifier id="bookid">{book_id}</dc:identifier><dc:title>{t}</dc:title><dc:creator>{a}</dc:creator><dc:language>en</dc:language>
<meta property="dcterms:modified">{now}</meta>
<meta property="rendition:layout">pre-paginated</meta><meta property="rendition:orientation">auto</meta><meta property="rendition:spread">landscape</meta>
<meta name="cover" content="i{pages[0][0]}"/><meta name="original-resolution" content="{first_w}x{first_h}"/><meta name="fixed-layout" content="true"/><meta name="book-type" content="comic"/>
</metadata>
<manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/><item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
{manifest}
</manifest>
<spine toc="ncx">
{spine}
</spine></package>''', compress_type=zipfile.ZIP_DEFLATED)
        z.writestr('OEBPS/nav.xhtml', f'''<?xml version="1.0" encoding="utf-8"?>
<!DOCTYPE html><html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>{t}</title></head>
<body><nav epub:type="toc"><ol><li><a href="{pages[0][0]}.xhtml">{t}</a></li></ol></nav></body></html>''', compress_type=zipfile.ZIP_DEFLATED)
        z.writestr('OEBPS/toc.ncx', f'''<?xml version="1.0" encoding="utf-8"?>
<ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><head><meta name="dtb:uid" content="{book_id}"/></head>
<docTitle><text>{t}</text></docTitle><navMap><navPoint id="n1" playOrder="1"><navLabel><text>{t}</text></navLabel><content src="{pages[0][0]}.xhtml"/></navPoint></navMap></ncx>''', compress_type=zipfile.ZIP_DEFLATED)
    return len(pages), stats


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('pdf'); ap.add_argument('epub')
    ap.add_argument('--title', default='Sem título'); ap.add_argument('--author', default='Desconhecido')
    a = ap.parse_args()
    n, stats = build_epub(a.pdf, a.epub, a.title, a.author)
    print(f'{n} páginas: {stats["original"]} originais, {stats["renderizada"]} renderizadas')

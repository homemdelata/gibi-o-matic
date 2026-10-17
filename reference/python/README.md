# Implementações de referência (Python)

Scripts escritos e validados durante a análise da antiga biblioteca Calibre. **Servem para portar a lógica para C#**, não para rodar no app.

| Arquivo | O que faz | Portar para |
|---|---|---|
| `analyze.py` | Nota de qualidade dos metadados (0–100) e lista de problemas por campo (título, autores, data, série, secundários). Lê um CSV exportado do Calibre, mas a lógica de regras é independente disso. | `GibiOMatic.Core` (F1), SPEC §5 |
| `pdf2epub.py` | PDF de quadrinho → páginas: extrai o JPEG original quando a página é uma imagem única que a cobre inteira, senão renderiza. Gera EPUB de layout fixo. | `GibiOMatic.Conversion` (F1 capa, F3 CBZ), SPEC §9.2 |

⚠️ `pdf2epub.py` usa **PyMuPDF (AGPL)**. No app use PdfPig e PDFium (ver `docs/THIRD-PARTY.md`). Copie a heurística, não a biblioteca.

Detalhes e resultados dos testes estão em `docs/RESEARCH.md`, seção "PDFs do Humble".

"""Pontua a qualidade dos metadados de cada livro (0 = péssimo, 100 = ok).
Uso: python analyze.py <books.csv> <saida.csv>"""
import csv, re, sys, collections

src, out = sys.argv[1], sys.argv[2]
rows = list(csv.DictReader(open(src, encoding='utf-8-sig')))

# peso por campo, na ordem de prioridade do usuário
NUM_RE = re.compile(r'(?:\bVol(?:ume)?\.?|\bBook|\bIssue|\bPart|\bTPB|#)\s*0*(\d+(?:\.\d+)?)', re.I)
PLACEHOLDER_AUTHORS = {'desconhecido', 'unknown', 'various', 'vários', 'varios', 'autor desconhecido'}

date_count = collections.Counter(r['pubdate'][:10] for r in rows)

def valid_isbn(v):
    v = v.replace('-', '')
    if re.fullmatch(r'\d{13}', v):
        return sum(int(d) * (1 if i % 2 == 0 else 3) for i, d in enumerate(v)) % 10 == 0
    if re.fullmatch(r'\d{9}[\dX]', v):
        return sum((10 - i) * (10 if d == 'X' else int(d)) for i, d in enumerate(v)) % 11 == 0
    return False

def guess_series(title):
    m = re.match(r'^(.*?)(?:,|:|\s-)?\s*(?:Vol(?:ume)?\.?|Book|Issue|#)\s*0*(\d+)', title, re.I)
    return (m.group(1).strip(' ,:-'), m.group(2)) if m else (None, None)

result = []
for r in rows:
    probs = []  # (campo, gravidade 1-3, descrição)
    t, a = r['title'], r['authors'] or ''
    authors = [x.strip() for x in a.split(' & ') if x.strip()]

    # --- TÍTULO / NOME DO ARQUIVO (o Calibre deriva o nome do arquivo do título + autor)
    if t == t.lower() and re.search(r'[a-z]', t):
        probs.append(('titulo', 3, 'título todo em minúsculas'))
    if re.search(r'[a-z]{18,}', t) and ' ' not in t.strip():
        probs.append(('titulo', 3, 'palavras coladas (parece nome de arquivo)'))
    if '_' in t or re.search(r'\.(epub|pdf|cbz|cbr)$', t, re.I):
        probs.append(('titulo', 3, 'contém _ ou extensão de arquivo'))
    if t.strip().lower() in ('untitled', 'sem título', 'unknown', ''):
        probs.append(('titulo', 3, 'título genérico (Untitled)'))
    if '  ' in t or t != t.strip():
        probs.append(('titulo', 1, 'espaços duplos/sobrando'))
    if t.isupper() and len(t) > 4 or re.match(r'^[A-Z]{3,}(?: [A-Z]{2,})+', t):
        probs.append(('titulo', 1, 'título TODO EM MAIÚSCULAS'))
    if re.search(r'\(prop\)|\bprop\b|\[.*?\]', t, re.I):
        probs.append(('titulo', 2, 'lixo no título (prop/colchetes)'))
    if re.search(r'Vol(?:ume)?\.?\s*0\d', t, re.I):
        probs.append(('titulo', 1, 'numeração com zero à esquerda (Vol. 01)'))

    # --- AUTORES
    if not authors or any(x.lower() in PLACEHOLDER_AUTHORS for x in authors):
        probs.append(('autores', 3, f'autor genérico/ausente ({a or "vazio"})'))
    single = [x for x in authors if ' ' not in x and x.lower() not in PLACEHOLDER_AUTHORS]
    if single:
        probs.append(('autores', 2, f'autor com nome único: {", ".join(single)}'))
    glued = [x for x in authors if '|' in x]
    if glued:
        probs.append(('autores', 3, f'vários autores grudados num só: {glued[0]}'))
    if chr(92) in a:
        probs.append(('autores', 2, 'barra invertida no nome do autor'))
    if len(set(authors)) != len(authors) or any(len(set(x.split('| '))) != len(x.split('| ')) for x in authors):
        probs.append(('autores', 2, 'autor duplicado'))
    if len(authors) >= 5:
        probs.append(('autores', 1, f'{len(authors)} autores (provável inclusão de coloristas/letristas)'))

    # --- DATA DE PUBLICAÇÃO
    d = r['pubdate'][:10]
    if d.startswith('0101'):
        probs.append(('data', 3, 'sem data de publicação'))
    elif '2015-12-09' <= d <= '2015-12-14':
        probs.append(('data', 2, f'data {d} é provavelmente a do Humble Bundle, não a original'))
    elif date_count[d] >= 5:
        probs.append(('data', 1, f'data {d} repetida em {date_count[d]} livros (suspeita)'))
    elif d[:10] == r['timestamp'][:10]:
        probs.append(('data', 2, 'data igual à data em que foi adicionado'))

    # --- SÉRIE
    s, gs_idx = r['series'], None
    gname, gnum = guess_series(t)
    if not s and gname:
        probs.append(('serie', 3 if not gname == '' else 2, f'título indica série "{gname}" #{gnum}, mas campo série vazio'))
    if s:
        m = NUM_RE.search(t)
        if m and float(m.group(1)) != float(r['series_index'] or 0):
            probs.append(('serie', 2, f'índice da série ({r["series_index"]}) difere do número no título ({m.group(1)})'))

    # --- SECUNDÁRIOS (não entram tanto na nota, mas ajudam a buscar metadados)
    isbns = re.findall(r'isbn:(\S+(?: \S+)?)', r['idents'] or '')
    if isbns and not any(valid_isbn(x) for x in isbns):
        probs.append(('ident', 1, f'ISBN inválido ({isbns[0]})'))
    if not r['publisher']:
        probs.append(('editora', 1, 'sem editora'))

    weight = {'titulo': 1.0, 'autores': 0.9, 'data': 0.7, 'serie': 0.7, 'ident': 0.3, 'editora': 0.3}
    penalty = sum(sev * 8 * weight[f] for f, sev, _ in probs)
    score = max(0, round(100 - penalty))
    fields = sorted({f for f, sev, _ in probs if f in ('titulo', 'autores', 'data', 'serie')})
    result.append({
        'id': r['id'], 'nota': score,
        'faixa': 'RUIM' if score < 60 else 'MÉDIO' if score < 85 else 'BOM',
        'revisado': 'sim' if r['revisado'] == '1' else '',
        'campos_com_problema': ', '.join(fields),
        'titulo': t, 'autores': a, 'data_pub': '' if d.startswith('0101') else d,
        'serie': f"{s} #{r['series_index']}" if s else '', 'editora': r['publisher'],
        'serie_sugerida': f'{gname} #{gnum}' if (not s and gname) else '',
        'problemas': ' | '.join(p for _, _, p in probs), 'pasta': r['path'],
    })

result.sort(key=lambda x: (x['nota'], x['titulo']))
with open(out, 'w', newline='', encoding='utf-8-sig') as f:
    w = csv.DictWriter(f, fieldnames=list(result[0]), delimiter=';')
    w.writeheader(); w.writerows(result)

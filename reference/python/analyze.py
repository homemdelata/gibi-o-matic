"""Scores the metadata quality of each book (0 = terrible, 100 = ok).
Usage: python analyze.py <books.csv> <output.csv>"""
import csv, re, sys, collections

src, out = sys.argv[1], sys.argv[2]
rows = list(csv.DictReader(open(src, encoding='utf-8-sig')))

# weight per field, in the user's order of priority
NUM_RE = re.compile(r'(?:\bVol(?:ume)?\.?|\bBook|\bIssue|\bPart|\bTPB|#)\s*0*(\d+(?:\.\d+)?)', re.I)
# Placeholder values found in the Calibre library, which was in Portuguese ('desconhecido' = unknown, 'vários' = various).
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
    probs = []  # (field, severity 1-3, description)
    t, a = r['title'], r['authors'] or ''
    authors = [x.strip() for x in a.split(' & ') if x.strip()]

    # --- TITLE / FILE NAME (Calibre derives the file name from title + author)
    if t == t.lower() and re.search(r'[a-z]', t):
        probs.append(('title', 3, 'title all lowercase'))
    if re.search(r'[a-z]{18,}', t) and ' ' not in t.strip():
        probs.append(('title', 3, 'words run together (looks like a file name)'))
    if '_' in t or re.search(r'\.(epub|pdf|cbz|cbr)$', t, re.I):
        probs.append(('title', 3, 'contains _ or a file extension'))
    if t.strip().lower() in ('untitled', 'sem título', 'unknown', ''):  # 'sem título' = untitled
        probs.append(('title', 3, 'generic title (Untitled)'))
    if '  ' in t or t != t.strip():
        probs.append(('title', 1, 'double/extra spaces'))
    if t.isupper() and len(t) > 4 or re.match(r'^[A-Z]{3,}(?: [A-Z]{2,})+', t):
        probs.append(('title', 1, 'title in ALL CAPS'))
    if re.search(r'\(prop\)|\bprop\b|\[.*?\]', t, re.I):
        probs.append(('title', 2, 'junk in the title (prop/brackets)'))
    if re.search(r'Vol(?:ume)?\.?\s*0\d', t, re.I):
        probs.append(('title', 1, 'numbering with a leading zero (Vol. 01)'))

    # --- AUTHORS
    if not authors or any(x.lower() in PLACEHOLDER_AUTHORS for x in authors):
        probs.append(('authors', 3, f'generic/missing author ({a or "empty"})'))
    single = [x for x in authors if ' ' not in x and x.lower() not in PLACEHOLDER_AUTHORS]
    if single:
        probs.append(('authors', 2, f'single-word author name: {", ".join(single)}'))
    glued = [x for x in authors if '|' in x]
    if glued:
        probs.append(('authors', 3, f'several authors glued into one: {glued[0]}'))
    if chr(92) in a:
        probs.append(('authors', 2, 'backslash in the author name'))
    if len(set(authors)) != len(authors) or any(len(set(x.split('| '))) != len(x.split('| ')) for x in authors):
        probs.append(('authors', 2, 'duplicate author'))
    if len(authors) >= 5:
        probs.append(('authors', 1, f'{len(authors)} authors (colorists/letterers probably included)'))

    # --- PUBLICATION DATE
    d = r['pubdate'][:10]
    if d.startswith('0101'):
        probs.append(('date', 3, 'no publication date'))
    elif '2015-12-09' <= d <= '2015-12-14':
        probs.append(('date', 2, f'date {d} is probably the Humble Bundle one, not the original'))
    elif date_count[d] >= 5:
        probs.append(('date', 1, f'date {d} repeated in {date_count[d]} books (suspicious)'))
    elif d[:10] == r['timestamp'][:10]:
        probs.append(('date', 2, 'date equal to the date it was added'))

    # --- SERIES
    s, gs_idx = r['series'], None
    gname, gnum = guess_series(t)
    if not s and gname:
        probs.append(('series', 3 if not gname == '' else 2, f'title implies series "{gname}" #{gnum}, but the series field is empty'))
    if s:
        m = NUM_RE.search(t)
        if m and float(m.group(1)) != float(r['series_index'] or 0):
            probs.append(('series', 2, f'series index ({r["series_index"]}) differs from the number in the title ({m.group(1)})'))

    # --- SECONDARY (they weigh less in the score, but help when searching for metadata)
    isbns = re.findall(r'isbn:(\S+(?: \S+)?)', r['idents'] or '')
    if isbns and not any(valid_isbn(x) for x in isbns):
        probs.append(('ident', 1, f'invalid ISBN ({isbns[0]})'))
    if not r['publisher']:
        probs.append(('publisher', 1, 'no publisher'))

    weight = {'title': 1.0, 'authors': 0.9, 'date': 0.7, 'series': 0.7, 'ident': 0.3, 'publisher': 0.3}
    penalty = sum(sev * 8 * weight[f] for f, sev, _ in probs)
    score = max(0, round(100 - penalty))
    fields = sorted({f for f, sev, _ in probs if f in ('title', 'authors', 'date', 'series')})
    result.append({
        'id': r['id'], 'score': score,
        'band': 'POOR' if score < 60 else 'FAIR' if score < 85 else 'GOOD',
        'reviewed': 'yes' if r['revisado'] == '1' else '',  # 'revisado' = a custom column in the Calibre library
        'fields_with_problems': ', '.join(fields),
        'title': t, 'authors': a, 'pubdate': '' if d.startswith('0101') else d,
        'series': f"{s} #{r['series_index']}" if s else '', 'publisher': r['publisher'],
        'suggested_series': f'{gname} #{gnum}' if (not s and gname) else '',
        'problems': ' | '.join(p for _, _, p in probs), 'folder': r['path'],
    })

result.sort(key=lambda x: (x['score'], x['title']))
with open(out, 'w', newline='', encoding='utf-8-sig') as f:
    w = csv.DictWriter(f, fieldnames=list(result[0]), delimiter=';')
    w.writeheader(); w.writerows(result)

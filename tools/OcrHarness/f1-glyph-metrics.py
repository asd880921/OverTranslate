"""The four F1 metrics, computed off the harness's own outputs.

Reads what --estimate-precision, --glyph-samples and --glyph-samples-ocr wrote and prints the
tables the F1 report is built from. It never evaluates the estimate itself: every glyph height in
here came out of the production function through one of those three modes, and a formula
re-implemented in an analysis script is a formula that can quietly disagree with the one shipping.

Usage: python f1-glyph-metrics.py <l1aDir> <samplesDir>
"""
import io
import math
import os
import sys
from collections import defaultdict

L1A_DIR, SAMPLES_DIR = sys.argv[1], sys.argv[2]

GENERAL_GATE = 0.88   # OcrTextBlockGrouper.MinTextSizeRatio
TIGHT_GATE = 0.80     # GroupingProfile.ComicArticle.TightlySetMinTextSizeRatio


def read_tsv(path):
    with io.open(path, encoding='utf-8', newline='') as f:
        rows = [line.rstrip('\r\n').split('\t') for line in f if line.strip()]
    head = rows[0]
    return [dict(zip(head, r)) for r in rows[1:]]


def num(v):
    return None if v in ('null', '') else float(v)


def mean(xs):
    return sum(xs) / len(xs)


def sd(xs):
    """Sample standard deviation; 0 for a single value, which the separation code must handle."""
    if len(xs) < 2:
        return 0.0
    m = mean(xs)
    return math.sqrt(sum((x - m) ** 2 for x in xs) / (len(xs) - 1))


def cv(xs):
    m = mean(xs)
    return None if m == 0 else sd(xs) / m


def separation(a, b):
    """|mean difference| over the pooled sd, or None where that sd is zero.

    Zero pooled sd means both groups are constants; the ratio is then undefined or infinite and
    neither is a number to put in a column with the others, so it is reported as n/a with the raw
    means beside it rather than filled in with a large stand-in.

    "Zero" has to be judged against the size of the values, not against 0.0 exactly: a group of
    constants computed in floating point comes back with an sd around 1e-15, which is zero in
    every sense that matters and yet divides into a difference of ten to give 1e15. Reading that
    as "separation of a quadrillion" is how a degenerate case gets mistaken for a strong result.
    """
    pooled = math.sqrt((sd(a) ** 2 + sd(b) ** 2) / 2)
    scale = max(abs(mean(a)), abs(mean(b)), 1e-12)
    if pooled <= scale * 1e-9:
        return None
    return abs(mean(a) - mean(b)) / pooled


def h2(title):
    print()
    print('=' * 78)
    print(title)
    print('=' * 78)


# ---------------------------------------------------------------- L1-a: the formula alone

def l1a_steps(rows, key_fields, axis):
    """Largest relative jump between adjacent steps of one axis, per held-constant group."""
    groups = defaultdict(list)
    for r in rows:
        groups[tuple(r[f] for f in key_fields)].append(r)

    out = []
    for key, items in sorted(groups.items()):
        items.sort(key=lambda r: float(r[axis]))
        worst = (0.0, None, None)
        for a, b in zip(items, items[1:]):
            ea, eb = num(a['glyphHeight']), num(b['glyphHeight'])
            if ea is None or eb is None or ea == 0:
                continue
            rel = abs(eb - ea) / ea
            if rel > worst[0]:
                worst = (rel, a, b)
        out.append((key, worst, items))
    return out


h2('L1-a  formula sensitivity — boxes typed in, no font, no ink, no OCR')
print('The only thing varying here is W, H or n, so every difference below is the formula.')

for name, axis, fields in (
    ('width across 2H (fine)', 'width', ('script', 'height', 'glyphs')),
    ('width swept 10..400', 'width', ('script', 'height', 'glyphs')),
    ('height across W/2', 'height', ('script', 'width', 'glyphs')),
):
    path = os.path.join(L1A_DIR, {
        'width across 2H (fine)': 'out-width-boundary.tsv',
        'width swept 10..400': 'out-width-sweep.tsv',
        'height across W/2': 'out-height-boundary.tsv',
    }[name])
    rows = read_tsv(path)
    print()
    print('--- %s ---' % name)
    print('%-6s %-9s %-7s %10s %10s  %s' % ('script', 'held', 'n', 'maxJump%', 'atStep', 'where'))
    for key, (rel, a, b), items in l1a_steps(rows, fields, axis):
        if a is None:
            print('%-6s %-9s %-7s %10s' % (key[0], key[1], key[2], 'flat'))
            continue
        step = float(b[axis]) - float(a[axis])
        print('%-6s %-9s %-7s %10.2f %10.4g  %s=%s->%s  est %.4f->%.4f  src %s->%s' % (
            key[0], key[1], key[2], rel * 100, step, axis, a[axis], b[axis],
            num(a['glyphHeight']), num(b['glyphHeight']), a['source'], b['source']))

print()
print('--- n stepped by one, W and H held ---')
rows = read_tsv(os.path.join(L1A_DIR, 'out-glyphs.tsv'))
groups = defaultdict(dict)
for r in rows:
    groups[(r['script'], r['width'], r['height'])][int(r['glyphs'])] = r

print('%-6s %-9s %-7s %9s %9s %9s %9s  %s' % (
    'script', 'W', 'H', 'n=3', 'n=4', 'jump%', 'maxJump%', 'notes'))
for key, byn in sorted(groups.items()):
    e3, e4 = num(byn[3]['glyphHeight']), num(byn[4]['glyphHeight'])
    jump = abs(e4 - e3) / e3 * 100 if e3 else float('nan')
    worst, at = 0.0, None
    for n in range(5, max(byn) + 1):
        a, b = num(byn[n - 1]['glyphHeight']), num(byn[n]['glyphHeight'])
        if a and abs(b - a) / a > worst:
            worst, at = abs(b - a) / a, n
    print('%-6s %-9s %-7s %9.4f %9.4f %9.2f %9.2f  n=%s->%s, src at n=4 %s, at n=200 %s' % (
        key[0], key[1], key[2], e3, e4, jump, worst * 100, at - 1 if at else '-', at or '-',
        byn[4]['source'], byn[max(byn)]['source']))


# ---------------------------------------------------------------- L1-b: drawn samples

samples = read_tsv(os.path.join(SAMPLES_DIR, 'samples.tsv'))

h2('L1-b  drawn samples — known font, size and string, two box definitions')
print('layout box = advance width x the font line height (leading included).')
print('ink box    = the rectangle the marks landed in.')
print('NEITHER is a detection box. Differences here carry font and string shape, not only formula.')

print()
print('coverage (an estimate at all), by script:')
print('%-9s %7s %9s %9s' % ('script', 'rows', 'estimated', 'null'))
for script in sorted({r['script'] for r in samples}):
    rows = [r for r in samples if r['script'] == script]
    est = [r for r in rows if num(r['layout_est']) is not None]
    print('%-9s %7d %9d %9d' % (script, len(rows), len(est), len(rows) - len(est)))

estimable = [r for r in samples if num(r['layout_est']) is not None]


def strata(rows, box):
    """Same font, same size, same script: everything that should read as one type size."""
    out = defaultdict(list)
    for r in rows:
        out[(r['font'], int(r['sizePx']), r['script'])].append(r)
    return out


for box in ('layout', 'ink'):
    print()
    print('--- same-size consistency, %s box: CV over every length and string shape ---' % box)
    print('%-18s %-5s %-6s %5s %8s %8s %8s   %s' % (
        'font', 'size', 'script', 'rows', 'CV_all', 'CV_n>=4', 'CV_n<4', 'paths taken'))
    for key, rows in sorted(strata(estimable, box).items()):
        vals = [num(r['%s_est' % box]) for r in rows]
        long_ = [num(r['%s_est' % box]) for r in rows if int(r['glyphs']) >= 4]
        short = [num(r['%s_est' % box]) for r in rows if int(r['glyphs']) < 4]
        paths = defaultdict(int)
        for r in rows:
            paths[r['%s_src' % box]] += 1
        print('%-18s %-5d %-6s %5d %8s %8s %8s   %s' % (
            key[0], key[1], key[2], len(rows),
            '%.4f' % cv(vals) if cv(vals) is not None else 'n/a',
            '%.4f' % cv(long_) if long_ and cv(long_) is not None else 'n/a',
            '%.4f' % cv(short) if short and cv(short) is not None else 'n/a',
            ' '.join('%s=%d' % kv for kv in sorted(paths.items()))))

    print()
    print('--- same-size consistency by string shape, %s box (n>=4 only) ---' % box)
    print('%-18s %-5s %-6s %-11s %5s %8s %8s' % (
        'font', 'size', 'script', 'content', 'rows', 'CV', 'mean'))
    by_content = defaultdict(list)
    for r in estimable:
        if int(r['glyphs']) >= 4:
            by_content[(r['font'], int(r['sizePx']), r['script'], r['content'])].append(r)
    for key, rows in sorted(by_content.items()):
        vals = [num(r['%s_est' % box]) for r in rows]
        c = cv(vals)
        print('%-18s %-5d %-6s %-11s %5d %8s %8.3f' % (
            key[0], key[1], key[2], key[3], len(rows),
            '%.4f' % c if c is not None else 'n/a', mean(vals)))

    print()
    print('--- adjacent-size separation, %s box ---' % box)
    print('%-18s %-6s %-9s %7s %8s %8s %8s' % (
        'font', 'script', 'sizes', 'sep', 'meanA', 'meanB', 'ratio'))
    sizes = sorted({int(r['sizePx']) for r in estimable})
    for font in sorted({r['font'] for r in estimable}):
        for script in sorted({r['script'] for r in estimable if r['font'] == font}):
            for a, b in zip(sizes, sizes[1:]):
                ga = [num(r['%s_est' % box]) for r in estimable
                      if r['font'] == font and r['script'] == script and int(r['sizePx']) == a]
                gb = [num(r['%s_est' % box]) for r in estimable
                      if r['font'] == font and r['script'] == script and int(r['sizePx']) == b]
                if not ga or not gb:
                    continue
                s = separation(ga, gb)
                print('%-18s %-6s %-9s %7s %8.3f %8.3f %8.3f' % (
                    font, script, '%d/%d' % (a, b),
                    '%.2f' % s if s is not None else 'n/a (sd=0)',
                    mean(ga), mean(gb), min(mean(ga), mean(gb)) / max(mean(ga), mean(gb))))

    print()
    print('--- pair ratios against the size gates, %s box ---' % box)
    print('same stratum = pairs that ARE one type size, so a ratio under the gate is a false split.')
    print('adjacent size = pairs that are NOT, so a ratio at or over the gate is a missed distinction.')
    print('%-18s %-6s %-13s %7s %9s %9s %9s %9s' % (
        'font', 'script', 'pairs of', 'n', 'median', 'p05', '<0.88', '<0.80'))
    for font in sorted({r['font'] for r in estimable}):
        for script in sorted({r['script'] for r in estimable if r['font'] == font}):
            for a in sizes:
                rows = [r for r in estimable
                        if r['font'] == font and r['script'] == script and int(r['sizePx']) == a]
                vals = [num(r['%s_est' % box]) for r in rows]
                ratios = sorted(min(x, y) / max(x, y)
                                for i, x in enumerate(vals) for y in vals[i + 1:] if max(x, y) > 0)
                if not ratios:
                    continue
                print('%-18s %-6s %-13s %7d %9.4f %9.4f %8.1f%% %8.1f%%' % (
                    font, script, 'same %dpx' % a, len(ratios),
                    ratios[len(ratios) // 2], ratios[max(0, int(len(ratios) * 0.05))],
                    100 * sum(1 for v in ratios if v < GENERAL_GATE) / len(ratios),
                    100 * sum(1 for v in ratios if v < TIGHT_GATE) / len(ratios)))
            for a, b in zip(sizes, sizes[1:]):
                va = [num(r['%s_est' % box]) for r in estimable
                      if r['font'] == font and r['script'] == script and int(r['sizePx']) == a]
                vb = [num(r['%s_est' % box]) for r in estimable
                      if r['font'] == font and r['script'] == script and int(r['sizePx']) == b]
                if not va or not vb:
                    continue
                ratios = sorted(min(x, y) / max(x, y) for x in va for y in vb if max(x, y) > 0)
                print('%-18s %-6s %-13s %7d %9.4f %9.4f %8.1f%% %8.1f%%   >=0.88: %.1f%%' % (
                    font, script, '%d vs %d' % (a, b), len(ratios),
                    ratios[len(ratios) // 2], ratios[max(0, int(len(ratios) * 0.05))],
                    100 * sum(1 for v in ratios if v < GENERAL_GATE) / len(ratios),
                    100 * sum(1 for v in ratios if v < TIGHT_GATE) / len(ratios),
                    100 * sum(1 for v in ratios if v >= GENERAL_GATE) / len(ratios)))

print()
print('--- what the scripts with no estimate fall back to (ink box height) ---')
print('TextSizeRatio compares LayoutBounds.Height when either side has no estimate OR the two')
print('scripts differ, so these pairs are judged, not skipped. Ink height stands in for the')
print('detection box here; the real one is in L2.')
noest = [r for r in samples if num(r['layout_est']) is None]
print('rows with no estimate: %d of %d (%.1f%%)' % (
    len(noest), len(samples), 100 * len(noest) / len(samples)))
for script in sorted({r['script'] for r in noest}):
    rows = [r for r in noest if r['script'] == script]
    print('  %-8s %4d rows, ink height mean %.2f' % (
        script, len(rows), mean([float(r['inkH']) for r in rows])))



h2('H1-H3 support — the two single-path series, next to the one that ships')
print('boxEst    = height x 0.82, the value the estimate holds before anything challenges it.')
print('pitchCand = W/n x coefficient, which the trace computes whether or not the branch read it.')
print('Both columns are the production function\'s own trace output. Neither is a proposal: this')
print('is what "all Box" and "all Pitch" would have scored on the same rows, nothing more.')

for box in ('layout', 'ink'):
    print()
    print('--- consistency and gate behaviour by series, %s box (Latin only: CJK never leaves Box) ---' % box)
    print('%-18s %-5s %-10s %5s %8s %9s %9s' % (
        'font', 'size', 'series', 'rows', 'CV', 'medRatio', '<0.88'))
    for font in sorted({r['font'] for r in estimable}):
        for size in sorted({int(r['sizePx']) for r in estimable}):
            rows = [r for r in estimable
                    if r['font'] == font and int(r['sizePx']) == size and r['script'] == 'Latin']
            if len(rows) < 5:
                continue
            for series, column in (('current', '%s_est' % box),
                                   ('boxOnly', '%s_boxEst' % box),
                                   ('pitchOnly', '%s_pitchCand' % box)):
                vals = [num(r[column]) for r in rows if num(r[column]) is not None]
                if not vals:
                    continue
                ratios = sorted(min(x, y) / max(x, y)
                                for i, x in enumerate(vals) for y in vals[i + 1:] if max(x, y) > 0)
                c = cv(vals)
                print('%-18s %-5d %-10s %5d %8s %9.4f %8.1f%%' % (
                    font, size, series, len(vals),
                    '%.4f' % c if c is not None else 'n/a',
                    ratios[len(ratios) // 2],
                    100 * sum(1 for v in ratios if v < GENERAL_GATE) / len(ratios)))

    print()
    print('--- adjacent-size separation by series, %s box (Latin) ---' % box)
    print('%-18s %-9s %-10s %7s %9s' % ('font', 'sizes', 'series', 'sep', 'meanRatio'))
    sizes = sorted({int(r['sizePx']) for r in estimable})
    for font in sorted({r['font'] for r in estimable}):
        for a, b in zip(sizes, sizes[1:]):
            for series, column in (('current', '%s_est' % box),
                                   ('boxOnly', '%s_boxEst' % box),
                                   ('pitchOnly', '%s_pitchCand' % box)):
                ga = [num(r[column]) for r in estimable if r['font'] == font
                      and r['script'] == 'Latin' and int(r['sizePx']) == a and num(r[column]) is not None]
                gb = [num(r[column]) for r in estimable if r['font'] == font
                      and r['script'] == 'Latin' and int(r['sizePx']) == b and num(r[column]) is not None]
                if len(ga) < 5 or len(gb) < 5:
                    continue
                sep = separation(ga, gb)
                print('%-18s %-9s %-10s %7s %9.4f' % (
                    font, '%d/%d' % (a, b), series,
                    '%.2f' % sep if sep is not None else 'n/a (sd=0)',
                    min(mean(ga), mean(gb)) / max(mean(ga), mean(gb))))

print()
print('--- continuity by series: largest adjacent-step jump on the L1-a sweeps ---')
print('Same rows as L1-a above, read three times. A single-path series that still steps is a')
print('series whose discontinuity is not the branch.')
print('%-26s %-6s %-9s %-7s %10s %10s %10s' % (
    'sweep', 'script', 'held', 'n', 'current%', 'boxOnly%', 'pitchOnly%'))
for sweep, filename, axis, fields in (
    ('width across 2H', 'out-width-boundary.tsv', 'width', ('script', 'height', 'glyphs')),
    ('height across W/2', 'out-height-boundary.tsv', 'height', ('script', 'width', 'glyphs')),
    ('n stepped by one', 'out-glyphs.tsv', 'glyphs', ('script', 'width', 'height')),
):
    rows = read_tsv(os.path.join(L1A_DIR, filename))
    groups = defaultdict(list)
    for r in rows:
        groups[tuple(r[f] for f in fields)].append(r)
    for key, items in sorted(groups.items()):
        items.sort(key=lambda r: float(r[axis]))
        worst = {}
        for column in ('glyphHeight', 'boxEstimate', 'pitchCandidate'):
            top = 0.0
            for x, y in zip(items, items[1:]):
                ex, ey = num(x[column]), num(y[column])
                if ex is None or ey is None or ex == 0:
                    continue
                top = max(top, abs(ey - ex) / ex)
            worst[column] = top * 100
        print('%-26s %-6s %-9s %-7s %10.2f %10.2f %10.2f' % (
            sweep, key[0], key[1], key[2],
            worst['glyphHeight'], worst['boxEstimate'], worst['pitchCandidate']))

print()
print('--- continuity (b), the discrete axis on its own: the n=3->4 step, per series ---')
print('Reported apart from the max because a glyph count is not a continuous quantity, and the')
print('one step the estimate changes behaviour across is the one worth naming.')
print('%-6s %-9s %-7s %10s %10s %10s   %s' % (
    'script', 'W', 'H', 'current%', 'boxOnly%', 'pitchOnly%', 'max over n>=4 (current)'))
rows = read_tsv(os.path.join(L1A_DIR, 'out-glyphs.tsv'))
byn = defaultdict(dict)
for r in rows:
    byn[(r['script'], r['width'], r['height'])][int(r['glyphs'])] = r
for key, table in sorted(byn.items()):
    step = {}
    for column in ('glyphHeight', 'boxEstimate', 'pitchCandidate'):
        a, b = num(table[3][column]), num(table[4][column])
        step[column] = abs(b - a) / a * 100 if a else float('nan')
    tail = 0.0
    for n in range(5, max(table) + 1):
        a, b = num(table[n - 1]['glyphHeight']), num(table[n]['glyphHeight'])
        if a:
            tail = max(tail, abs(b - a) / a)
    print('%-6s %-9s %-7s %10.2f %10.2f %10.2f   %.2f%%' % (
        key[0], key[1], key[2],
        step['glyphHeight'], step['boxEstimate'], step['pitchCandidate'], tail * 100))

# ---------------------------------------------------------------- L2: through OCR

ocr_path = os.path.join(SAMPLES_DIR, 'ocr.tsv')
if not os.path.exists(ocr_path):
    print('\n(no ocr.tsv yet — run --glyph-samples-ocr)')
    sys.exit(0)

ocr = read_tsv(ocr_path)

h2('L2  through OCR — detection boxes, and what detection did to the set')
print('Detection and recognition are reported apart: known_* is the OCR box with the text that')
print('was drawn, rec_* the same box with the text that came back. The gap between L1 and L2 is')
print('not "detection error" and is not any method\'s ceiling — box, text and script all moved.')

print()
print('%-6s %-6s %6s %8s %7s %6s %7s %9s' % (
    'size', 'script', 'rows', 'matched', 'missed', 'split', 'merged', 'textExact'))
for size in sorted({int(r['sizePx']) for r in ocr}):
    for script in sorted({r['knownScript'] for r in ocr}):
        rows = [r for r in ocr if int(r['sizePx']) == size and r['knownScript'] == script]
        if not rows:
            continue
        m = [r for r in rows if r['status'] == 'matched']
        exact = [r for r in m if r['textExact'] == 'True']
        print('%-6d %-6s %6d %8d %7d %6d %7d %8s' % (
            size, script, len(rows), len(m),
            sum(1 for r in rows if r['status'] == 'missed'),
            sum(1 for r in rows if r['status'] == 'split'),
            sum(1 for r in rows if r['status'] == 'merged'),
            '%.0f%%' % (100 * len(exact) / len(m)) if m else '-'))

matched = [r for r in ocr if r['status'] == 'matched']

print()
print('cross-check: the estimate this run computed on the recognised text against the one the')
print('pipeline itself stored on the block (they are the same call, so any difference is a bug')
print('in this tooling, not a finding):')
mismatch = [r for r in matched
            if (r['productionEst'] == 'null') != (r['rec_est'] == 'null')
            or (r['productionEst'] != 'null' and r['rec_est'] != 'null'
                and abs(float(r['productionEst']) - float(r['rec_est'])) > 1e-9)]
print('  matched rows %d, disagreements %d' % (len(matched), len(mismatch)))

print()
print('no estimate after OCR, by known script (coverage on the real path):')
for script in sorted({r['knownScript'] for r in matched}):
    rows = [r for r in matched if r['knownScript'] == script]
    print('  %-8s %5d matched, rec_est null %4d, script changed by recognition %4d' % (
        script, len(rows),
        sum(1 for r in rows if r['rec_est'] == 'null'),
        sum(1 for r in rows if r['recScript'] != r['knownScript'])))

for col, label in (('known_est', 'OCR box + known text'), ('rec_est', 'OCR box + recognised text')):
    usable = [r for r in matched if num(r[col]) is not None]
    print()
    print('--- same-size consistency on the OCR box, %s ---' % label)
    print('%-18s %-5s %-6s %5s %8s %8s   %s' % (
        'font', 'size', 'script', 'rows', 'CV_all', 'CV_n>=4', 'paths'))
    by = defaultdict(list)
    for r in usable:
        by[(r['font'], int(r['sizePx']), r['knownScript'])].append(r)
    for key, rows in sorted(by.items()):
        vals = [num(r[col]) for r in rows]
        long_ = [num(r[col]) for r in rows if int(r['glyphs']) >= 4]
        paths = defaultdict(int)
        for r in rows:
            paths[r[col.replace('_est', '_src')]] += 1
        c, cl = cv(vals), cv(long_) if long_ else None
        print('%-18s %-5d %-6s %5d %8s %8s   %s' % (
            key[0], key[1], key[2], len(rows),
            '%.4f' % c if c is not None else 'n/a',
            '%.4f' % cl if cl is not None else 'n/a',
            ' '.join('%s=%d' % kv for kv in sorted(paths.items()))))

    print()
    print('--- adjacent-size separation on the OCR box, %s ---' % label)
    sizes = sorted({int(r['sizePx']) for r in usable})
    print('%-18s %-6s %-9s %7s %8s %8s %8s' % (
        'font', 'script', 'sizes', 'sep', 'meanA', 'meanB', 'ratio'))
    for font in sorted({r['font'] for r in usable}):
        for script in sorted({r['knownScript'] for r in usable if r['font'] == font}):
            for a, b in zip(sizes, sizes[1:]):
                ga = [num(r[col]) for r in usable
                      if r['font'] == font and r['knownScript'] == script and int(r['sizePx']) == a]
                gb = [num(r[col]) for r in usable
                      if r['font'] == font and r['knownScript'] == script and int(r['sizePx']) == b]
                if len(ga) < 2 or len(gb) < 2:
                    continue
                s = separation(ga, gb)
                print('%-18s %-6s %-9s %7s %8.3f %8.3f %8.3f' % (
                    font, script, '%d/%d' % (a, b),
                    '%.2f' % s if s is not None else 'n/a (sd=0)',
                    mean(ga), mean(gb), min(mean(ga), mean(gb)) / max(mean(ga), mean(gb))))

    print()
    print('--- pair ratios against the size gates on the OCR box, %s ---' % label)
    print('%-18s %-6s %-13s %7s %9s %9s %9s' % (
        'font', 'script', 'pairs of', 'n', 'median', 'p05', '<0.88'))
    for font in sorted({r['font'] for r in usable}):
        for script in sorted({r['knownScript'] for r in usable if r['font'] == font}):
            for a in sizes:
                vals = [num(r[col]) for r in usable
                        if r['font'] == font and r['knownScript'] == script and int(r['sizePx']) == a]
                ratios = sorted(min(x, y) / max(x, y)
                                for i, x in enumerate(vals) for y in vals[i + 1:] if max(x, y) > 0)
                if not ratios:
                    continue
                print('%-18s %-6s %-13s %7d %9.4f %9.4f %8.1f%%' % (
                    font, script, 'same %dpx' % a, len(ratios),
                    ratios[len(ratios) // 2], ratios[max(0, int(len(ratios) * 0.05))],
                    100 * sum(1 for v in ratios if v < GENERAL_GATE) / len(ratios)))
            for a, b in zip(sizes, sizes[1:]):
                va = [num(r[col]) for r in usable
                      if r['font'] == font and r['knownScript'] == script and int(r['sizePx']) == a]
                vb = [num(r[col]) for r in usable
                      if r['font'] == font and r['knownScript'] == script and int(r['sizePx']) == b]
                if not va or not vb:
                    continue
                ratios = sorted(min(x, y) / max(x, y) for x in va for y in vb if max(x, y) > 0)
                print('%-18s %-6s %-13s %7d %9.4f %9.4f %8.1f%%   >=0.88: %.1f%%' % (
                    font, script, '%d vs %d' % (a, b), len(ratios),
                    ratios[len(ratios) // 2], ratios[max(0, int(len(ratios) * 0.05))],
                    100 * sum(1 for v in ratios if v < GENERAL_GATE) / len(ratios),
                    100 * sum(1 for v in ratios if v >= GENERAL_GATE) / len(ratios)))


print()
print('--- the two single-path series on the OCR box (recognised text), by size ---')
print('The same three columns as L1-b, on the boxes the detector actually drew. This is the')
print('closest the comparison gets to the real path; it is still not a corpus.')
print('%-18s %-6s %-5s %-10s %5s %8s %9s %9s' % (
    'font', 'script', 'size', 'series', 'rows', 'CV', 'medRatio', '<0.88'))
for font in sorted({r['font'] for r in matched}):
    for script in sorted({r['knownScript'] for r in matched if r['font'] == font}):
        for size in sorted({int(r['sizePx']) for r in matched}):
            rows = [r for r in matched if r['font'] == font
                    and r['knownScript'] == script and int(r['sizePx']) == size]
            if len(rows) < 5:
                continue
            for series, column in (('current', 'rec_est'),
                                   ('boxOnly', 'rec_boxEst'),
                                   ('pitchOnly', 'rec_pitchCand')):
                vals = [num(r[column]) for r in rows if num(r[column]) is not None]
                if len(vals) < 2:
                    continue
                ratios = sorted(min(x, y) / max(x, y)
                                for i, x in enumerate(vals) for y in vals[i + 1:] if max(x, y) > 0)
                # A script with no estimate carries a zero box estimate in its trace, so its
                # single-path columns are all zero and there is no ratio to take. Reported as
                # absent rather than as a row of zeroes, which would read as an answer.
                if not ratios:
                    print('%-18s %-6s %-5d %-10s %5d %8s %9s %9s' % (
                        font, script, size, series, len(vals), 'n/a', 'n/a', 'n/a'))
                    continue
                c = cv(vals)
                print('%-18s %-6s %-5d %-10s %5d %8s %9.4f %8.1f%%' % (
                    font, script, size, series, len(vals),
                    '%.4f' % c if c is not None else 'n/a',
                    ratios[len(ratios) // 2],
                    100 * sum(1 for v in ratios if v < GENERAL_GATE) / len(ratios)))

print()
print('--- adjacent-size separation on the OCR box by series (recognised text) ---')
print('%-18s %-6s %-9s %-10s %7s %9s' % ('font', 'script', 'sizes', 'series', 'sep', 'meanRatio'))
ocr_sizes = sorted({int(r['sizePx']) for r in matched})
for font in sorted({r['font'] for r in matched}):
    for script in sorted({r['knownScript'] for r in matched if r['font'] == font}):
        for a, b in zip(ocr_sizes, ocr_sizes[1:]):
            for series, column in (('current', 'rec_est'),
                                   ('boxOnly', 'rec_boxEst'),
                                   ('pitchOnly', 'rec_pitchCand')):
                ga = [num(r[column]) for r in matched if r['font'] == font
                      and r['knownScript'] == script and int(r['sizePx']) == a
                      and num(r[column]) is not None]
                gb = [num(r[column]) for r in matched if r['font'] == font
                      and r['knownScript'] == script and int(r['sizePx']) == b
                      and num(r[column]) is not None]
                # Both means zero is the no-estimate case again: nothing to separate, and a
                # ratio of 0/0 is not a separation of any size.
                if len(ga) < 5 or len(gb) < 5 or max(mean(ga), mean(gb)) == 0:
                    continue
                sep = separation(ga, gb)
                print('%-18s %-6s %-9s %-10s %7s %9.4f' % (
                    font, script, '%d/%d' % (a, b), series,
                    '%.2f' % sep if sep is not None else 'n/a (sd=0)',
                    min(mean(ga), mean(gb)) / max(mean(ga), mean(gb))))

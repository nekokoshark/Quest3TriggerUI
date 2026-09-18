#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Build compact pinyin dictionaries for the VR IME engine.

Sources:
  pinyin_simp.dict.yaml   - word table (Rime pinyin_simp), word<TAB>syl syl<TAB>weight
  phrase_pinyin.txt       - phrase table (mozillazg phrase-pinyin-data),
                            word: syl syl ...   (tone-marked syllables)
  terra_pinyin.dict.yaml  - char table (Rime Terra), char<TAB>sylN

Output:
  pinyin_dict.txt      letters<TAB>word word ...   (exact keys, freq-ordered)
  pinyin_initials.txt  initials<TAB>word word ...  (简拼 shorthand)
  pinyin_fuzzy.txt     normletters<TAB>word ...    (模糊音 normalized keys)
  pinyin_syllables.txt syllable per line           (segmentation set)
"""
import re, os, unicodedata

SRC_DIR = os.path.dirname(os.path.abspath(__file__))

TONE_MAP = {
    'ā':'a','á':'a','ǎ':'a','à':'a','ē':'e','é':'e','ě':'e','è':'e',
    'ī':'i','í':'i','ǐ':'i','ì':'i','ō':'o','ó':'o','ǒ':'o','ò':'o',
    'ū':'u','ú':'u','ǔ':'u','ù':'u','ǖ':'v','ǘ':'v','ǚ':'v','ǜ':'v',
    'ü':'v','ê':'e','ń':'n','ň':'n','ǹ':'n','ḿ':'m',
}

def strip_tone(s):
    return ''.join(TONE_MAP.get(c, c) for c in s)

def iter_simp(path):
    for line in open(path, encoding='utf-8'):
        if line.startswith('#') or '\t' not in line:
            continue
        cols = line.rstrip('\n').split('\t')
        word, syls = cols[0], cols[1].strip()
        weight = 0.0
        if len(cols) > 2:
            m = re.match(r'([\d.]+)', cols[2])
            if m: weight = float(m.group(1))
        if not word or not re.match(r'^[a-z ]+$', syls):
            continue
        syl_list = syls.split(' ')
        yield word, syl_list, weight

def iter_phrase(path):
    for line in open(path, encoding='utf-8'):
        if line.startswith('#') or ':' not in line:
            continue
        word, syls = line.rstrip('\n').split(':', 1)
        syl_list = [strip_tone(s) for s in syls.strip().split(' ')]
        if not word or not all(re.match(r'^[a-z]+$', s) for s in syl_list):
            continue
        yield word, syl_list, 0.0

def iter_terra(path):
    for line in open(path, encoding='utf-8'):
        if line.startswith('#') or '\t' not in line:
            continue
        cols = line.rstrip('\n').split('\t')
        word, syl = cols[0], re.sub(r'\d+$', '', cols[1].strip())
        if not word or not re.match(r'^[a-z]+$', syl):
            continue
        yield word, [syl], 0.0

# Fuzzy normalization — mirrors PinyinEngine.NormSyl exactly.
def fuzzy_syl(s):
    for a, b in (('zh', 'z'), ('ch', 'c'), ('sh', 's')):
        if s.startswith(a):
            s = b + s[len(a):]
            break
    else:
        if s.startswith('n') and len(s) > 1:
            s = 'l' + s[1:]
        elif s.startswith('f') and len(s) > 1:
            s = 'h' + s[1:]
    for a, b in (('iang', 'ian'), ('uang', 'uan'),
                 ('ang', 'an'), ('eng', 'en'), ('ing', 'in')):
        if s.endswith(a):
            s = s[:-len(a)] + b
            break
    return s

full = {}      # exact letters -> {word: weight}
initials = {}  # initials   -> {word: weight}
fuzzy = {}     # normalized letters -> {word: weight}
syllables = set()
CAP = 48       # per-key word cap — deeper entries are unreachable in VR

def add(table, key, word, weight):
    bucket = table.setdefault(key, {})
    if word not in bucket or bucket[word] < weight:
        bucket[word] = weight

# Sources stack by curation quality. Weight scales differ wildly across
# sources (simp ~1e6, ice ~1e2), so each source is normalized to [0,1]
# then multiplied by its priority; unweighted sources get a flat
# half-priority score and keep their file order via stable sort.
SOURCES = [
    ('ice_base.dict.yaml',   iter_simp,   1.0),
    ('ice_ext.dict.yaml',    iter_simp,   1.0),
    ('ice_8105.dict.yaml',   iter_simp,   1.0),
    ('pinyin_simp.dict.yaml', iter_simp,  0.9),
    ('phrase_pinyin.txt',    iter_phrase, 0.2),
    ('terra_pinyin.dict.yaml', iter_terra, 0.1),
]

for path, src, prio in SOURCES:
    tmp_full, tmp_init, tmp_fuzz = {}, {}, {}
    maxw = 0.0
    for word, syl_list, weight in src(os.path.join(SRC_DIR, path)):
        syllables.update(syl_list)
        letters = ''.join(syl_list)
        inits = ''.join(s[0] for s in syl_list)
        fletters = ''.join(fuzzy_syl(s) for s in syl_list)
        if weight > maxw:
            maxw = weight
        add(tmp_full, letters, word, weight)
        if len(word) > 1 and inits != letters:
            add(tmp_init, inits, word, weight)
        if fletters != letters:
            add(tmp_fuzz, fletters, word, weight)
    scale = prio / maxw if maxw > 0 else 0.0
    # Unweighted sources get a small flat score — must stay below real
    # normalized word weights (~0.003 for mid-freq words) or fallback chars
    # would crowd out common words in capped buckets.
    flat = prio * 0.01
    for tmp, table in ((tmp_full, full), (tmp_init, initials),
                       (tmp_fuzz, fuzzy)):
        for key, bucket in tmp.items():
            for word, w in bucket.items():
                add(table, key, word, w * scale if maxw > 0 else flat)
    print(path, 'merged, maxw=%g' % maxw)

def dump(table, path):
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        for key, bucket in table.items():
            ordered = sorted(bucket.items(), key=lambda kv: -kv[1])[:CAP]
            f.write(key + '\t' + ' '.join(w for w, _ in ordered) + '\n')
    print(path, len(table), 'keys')

dump(full, os.path.join(SRC_DIR, 'pinyin_dict.txt'))
dump(initials, os.path.join(SRC_DIR, 'pinyin_initials.txt'))
dump(fuzzy, os.path.join(SRC_DIR, 'pinyin_fuzzy.txt'))
with open(os.path.join(SRC_DIR, 'pinyin_syllables.txt'), 'w',
          encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(sorted(syllables)))
print('syllables:', len(syllables))

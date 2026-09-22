"""Optional independent MuPDF preservation check; all source files are read-only.
Usage: python verify_embedded_orientation_preservation.py <INS> <harness-evidence-dir>
"""
import base64
import hashlib
import json
from pathlib import Path
import sys
import pymupdf


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def shape(widget):
    return (widget.field_name, widget.field_type, widget.field_flags, tuple(widget.rect),
            widget.text_font, widget.text_fontsize, widget.text_color, widget.border_style,
            widget.border_width, widget.border_color, widget.fill_color)


def main():
    source, evidence = map(Path, sys.argv[1:])
    before = digest(source)
    data = json.loads(source.read_text())
    results = {}
    for family in ('generic', 'official'):
        original_path = evidence / f'{family}-embedded-original.pdf'
        working_path = evidence / f'{family}-embedded-{"prefilled" if family == "generic" else "working"}.pdf'
        hashes = {str(p): digest(p) for p in (original_path, working_path)}
        assert any(base64.b64decode(a.get('FileData') or '') == original_path.read_bytes()
                   for a in data['Attachments']), 'evidence must match current source attachment'
        with pymupdf.open(original_path) as original, pymupdf.open(working_path) as working:
            assert len(original) == len(working)
            nonblank, changed, widgets = 0, 0, 0
            for old_page, new_page in zip(original, working):
                assert old_page.rect == new_page.rect and old_page.rotation == new_page.rotation
                assert old_page.read_contents() == new_page.read_contents(), 'page content stream changed'
                old_widgets, new_widgets = list(old_page.widgets() or []), list(new_page.widgets() or [])
                assert [shape(w) for w in old_widgets] == [shape(w) for w in new_widgets]
                widgets += len(old_widgets)
                for old, new in zip(old_widgets, new_widgets):
                    value = str(old.field_value or '')
                    if value.strip() or old.field_type != pymupdf.PDF_WIDGET_TYPE_TEXT:
                        assert old.field_value == new.field_value, old.field_name
                        nonblank += bool(value.strip())
                    if old.field_value != new.field_value:
                        assert not value.strip() and old.field_type == pymupdf.PDF_WIDGET_TYPE_TEXT
                        changed += 1
            results[family] = {'pages': len(original), 'widgets': widgets,
                               'nonblank_values_preserved': nonblank, 'blank_text_values_filled': changed,
                               'layout_fonts_flags_and_page_streams_preserved': True,
                               'original_sha256': hashes[str(original_path)],
                               'working_sha256': hashes[str(working_path)]}
        assert all(digest(Path(p)) == h for p, h in hashes.items())
    assert results['generic']['blank_text_values_filled'] > 0
    assert results['official']['nonblank_values_preserved'] > 0
    assert digest(source) == before
    results['source_sha256'] = before
    results['sources_unchanged'] = True
    results['windows_editor_tested'] = False
    (evidence / 'embedded-preservation.json').write_text(json.dumps(results, indent=2) + '\n')
    print(json.dumps(results, indent=2))


if __name__ == '__main__':
    main()

"""Independent optional MuPDF check. Requires pymupdf; never edits input files.
Usage: python verify_orientation_pdf_render.py <template> <prefilled> <INS> <evidence-dir>
"""
import hashlib
import json
from pathlib import Path
import sys
import pymupdf


def main():
    template, working, ins, output = map(Path, sys.argv[1:])
    output.mkdir(parents=True, exist_ok=True)
    source_hashes = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in (template, working, ins)}
    original = pymupdf.open(template)
    filled = pymupdf.open(working)
    assert len(original) == len(filled)
    original_widgets = [w for p in original for w in p.widgets() or []]
    filled_widgets = [w for p in filled for w in p.widgets() or []]
    def shape(w):
        return (w.field_name, w.field_type, w.field_flags, tuple(w.rect), w.text_font,
                w.text_fontsize, w.text_color, w.border_style, w.border_width)
    assert [shape(w) for w in original_widgets] == [shape(w) for w in filled_widgets]
    data = json.loads(ins.read_text())
    items = {i['Name']: i.get('Value') for s in data['Sections'] for i in s['Items']}
    # These assertions target the real Beaumont case, without hardcoding personal data.
    expected = {"Address1_HOI": data['Address'], "Customer Name_HOI": items['Customer Name'],
                "Customer Phone_HOI": items['Customer Phone'], "Subdivision_HOI": data['Project'],
                "InspectionLot_HOI": data['Lot'], "InspectionBlock_HOI": data['Block']}
    for name in ('Microwave Serial Number', 'Stove/Oven Serial Number', 'Dishwasher Serial Number'):
        expected[name if name.startswith('Stove/') else name + '_HOI'] = items[name]
    actual = {w.field_name: w.field_value for w in filled_widgets}
    text = '\n'.join(p.get_text() for p in filled)
    for name, value in expected.items():
        assert actual[name] == value.strip(), name
        assert value.strip() in text, 'rendered text: ' + name
    for index, page in enumerate(filled):
        page.get_pixmap(matrix=pymupdf.Matrix(1.5, 1.5)).save(output / f'beaumont-page{index + 1}.png')
    # Prove the output is still editable by changing a field and reopening a separate output.
    first_page = filled[0]
    buyer = next(w for w in first_page.widgets() if w.field_name == 'Customer Name_HOI')
    buyer.field_value = 'Harness editable value'
    buyer.update()
    edited = output / 'editable-proof.pdf'
    filled.save(edited)
    with pymupdf.open(edited) as reopened:
        assert next(w for w in reopened[0].widgets() if w.field_name == 'Customer Name_HOI').field_value == 'Harness editable value'
        assert len([w for p in reopened for w in p.widgets() or []]) == len(filled_widgets)
    assert all(hashlib.sha256(Path(p).read_bytes()).hexdigest() == digest for p, digest in source_hashes.items())
    result = {'pages': len(filled), 'widgets': len(filled_widgets), 'visible_value_checks': len(expected),
              'layout_font_flags_preserved': True, 'editable_roundtrip': True, 'sources_unchanged': True,
              'source_sha256': source_hashes, 'renderer': pymupdf.VersionBind,
              'windows_editor_tested': False}
    (output / 'render-verification.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()

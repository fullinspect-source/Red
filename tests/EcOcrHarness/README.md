# EC OCR regression harness

Run `python3 tests/EcOcrHarness/run.py /path/to/red-ec-fix/evidence`.
Requires .NET 10 and actual OCR evidence. Extracts and compiles production `EcOcrQuality` and `IsPdfSoftMaskCandidate`, not copies of their implementation. No csproj exclusions needed: generated C# lives in a temp directory.

Exact-report reproduction used PyMuPDF image streams and Tesseract CLI. Each of eight pages has a color stream plus its full-page soft mask (not two text tiles). Page 1 is rotated in PDF metadata. Applying the old max(R,G,B)>3 transform and choosing longest text gives page 1 mask text of 1,365 characters, including House Tightness and Ventilation but missing Conditioned Floor Area and the address. Thus the old two-label document gate accepts incomplete OCR and never renders the composed page. Normal mask OCR recovers Conditioned Floor Area; rendered 220dpi evidence recovers both it and the address. Stream sizes and exact OCR outputs are retained under external evidence/streams, not committed here.

Windows System.Drawing/Tesseract/PDFium runtime cannot execute on macOS. This harness verifies the actual platform-independent production selection helpers against real OCR text; it does not claim to execute the Windows native OCR pipeline. OCR scheduling has a 180-second cooperative budget plus the currently executing native call; it is not a hard native-call cancellation guarantee.

# PDF cleanup and scavenging production-linked probe

Run from the repository root:

```sh
DOTNET_ROLL_FORWARD=Major dotnet run --project tests/PdfCleanupHarness
```

The normal `tests/run_pdf_attachment_verification.py` runner includes this harness.
All fixture paths live under a unique `work/pdf-cleanup-*` directory. No real INS,
PDF templates, provider files, or production AppData are read or written. Production
sources are linked directly; PDF data is generated locally with PDFsharp.

Coverage:
- Same-length save between preliminary CanLeave and Cleanup is preserved and can
  subsequently be captured by the same monitor.
- Existing exclusive writer blocks cleanup. An actual second dotnet process tries
  to save at the exact final-comparison boundary and must fail while cleanup holds
  its exclusive lease. Cleanup keeps that handle until retirement.
- Pending adds, injected retirement failure, repeated cleanup, preservation of
  unexpected editor files, root and identity preservation.
- Transactional attachment deletion holds the same exclusive working lease across
  save and retirement. Failed commits release the lease and retain the session.
- A Windows-only child-process atomic replacement test runs at the final comparison
  boundary. Non-Windows runs print an explicit skip, never a simulated Windows pass.
- Scavenging accepts only strict 64-hex identity / 32-hex session directories at
  the authorized root, with an exact owned marker and exactly the known files.
  All timestamps must be strictly older than 30 days. Missing/unknown ownership,
  unknown files/subdirectories, fresh files, and the exact retention boundary stay.
- Symlink/reparse root, ancestor, identity, session, PDF and marker are not followed.
- An actual child process creates a real monitor. Even artificially aged files are
  protected by its lifetime lock. After the child exits without cleanup, the old
  owned session becomes eligible. Coordination uses stdin/stdout, not timing sleeps.

Implementation notes and limits:
- Windows production cleanup opens with READ | WRITE | DELETE and no sharing,
  compares using that handle, then sets file-delete disposition on that same handle.
  This avoids File.Delete's inability to delete an open FileShare.None file and
  avoids dropping the lease to reopen by path.
- POSIX execution checks .NET's cooperating cross-process locks, but POSIX does not
  enforce Windows mandatory sharing against arbitrary native writes or renames.
  Native Windows kernel disposition, editor Save/Save As and WPF interaction still
  require a Windows smoke test. Running symlink tests on Windows requires Developer
  Mode or the applicable symlink privilege.
- Windows opens reparse points themselves, verifies handle attributes, and pins
  every ancestor without delete-sharing before opening a working file. POSIX uses
  O_NOFOLLOW on the final component, with conservative ancestor checks; POSIX path
  checks are not a boundary against hostile concurrent ancestor replacement.
  No recursive deletion is used in production.
- Abandoned legacy sessions without the new ownership marker are deliberately
  retained. Unexpected editor-side files are also retained for manual recovery.
- Unsaved bytes held solely in an external editor's memory cannot be detected.
- Performance remains limited by synchronous large-PDF work on the WPF UI thread:
  attachment enumeration/refresh parses embedded packets, polling reads working
  bytes, and validation/base64/serialization/full INS saves can block input. This
  follow-up does not claim an asynchronous or responsiveness fix. Moving that work
  off-thread needs separate ownership, cancellation and stale-result guards.

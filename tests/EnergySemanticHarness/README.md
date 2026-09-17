# Energy semantic regression harness
Run from repository root:
```
python3 tests/EnergySemanticHarness/generate.py
DOTNET_ROLL_FORWARD=Major dotnet run --project tests/EnergySemanticHarness/EnergySemanticHarness.csproj
```
Links real semantic, target and equipment services and real inspection models. Generator extracts the exact production EnergyComplianceInfo and EC resolve/apply/lookup methods to avoid native PDF/OCR and Windows dependencies. It does not transcribe those methods. Normalization utility alone is stubbed.
Fixtures contain schema/lookup options only from all five INS files recursively in MyList (HET twice, AFI, IEF, archived HER), no inspection results, addresses or job IDs. Production .ins files are never modified.
271 assertions passed; full Windows/WPF integration remains parent verification. CPP prompt guards are additional hardening, not real-template certified because no CPP file exists in MyList.

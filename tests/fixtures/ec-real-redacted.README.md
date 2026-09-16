# Real EC parser regression fixture

`ec-real-redacted.txt` is the captured rendered OCR of an eight-page revision-2 EC report, not synthesized parser input. Source evidence is retained privately outside the repository. Address/contact/person/organization/community identifiers were replaced with `[REDACTED]`; job IDs were replaced with `JOB-REDACTED`. Energy, building, equipment values, OCR errors, layout, and revision suffixes are unchanged. Do not replace the OCR with cleaned-up text.

Run `python3 tests/ec_parser_regression.py` from the repository root (.NET 10 SDK required). The harness extracts the current production model, ParseText, and regex helpers into a temporary console project, exercising actual C# methods without loading WPF/PDF dependencies or changing application project files.

Verified source expectations: HERS 50; area 1,156; volume 9,248; bedrooms 3; 4.5 ACH50 -> 694 rounded CFM; ducts 46; returns 2; duct R6/R6; windows 0.34/0.22; walls R15; vented attic R38; tankless gas; pipes R3; cooling 17.2 SEER2, 24.4 kBtuh and nominal 2 tons; explicit cooling flow 720 CFM; ventilation 145 CFM, 6.9 hours/day, 38 W. Missing attic-wall, roof-deck and sloped values stay blank.

Ambiguous repeated capacity rows intentionally yield no capacity/tonnage estimate: unkeyed text cannot distinguish duplicate rendering from multiple equal systems. One or two explicit cooling-flow rows retain report order as unit slots; more than two rows remain unresolved. Parser status counts available parsed/calculated fields, not completeness or applicability. Existing IsLoaded application gating and Pass/Fail behavior are unchanged.

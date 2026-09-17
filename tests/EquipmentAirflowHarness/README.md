# Equipment airflow regression harness

Run from repository root:

```sh
DOTNET_ROLL_FORWARD=Major dotnet run --project tests/EquipmentAirflowHarness
```

The harness links the production EquipmentAirflowService and InspectionModels sources. Only EnergyComplianceInfo is stubbed; its fallback-source property must also exist in the production class.

`Fixtures/real-hef-redacted.json` preserves section names and model/serial item numbers and prompts from an actual HEF in Dropbox/Inspections/Review, read-only. MyList contained no HEF files at test time. All original field values, identities, job metadata, attachments and unrelated prompts were removed. Tests insert explicit synthetic approved model pairs, rather than claiming the actual job matched STRADA rules.

Coverage includes all five unchanged approved rules, latest same-job attempt selection, unsaved inspection authority, real HEF two-unit pairing, unit-2-only provenance, changed matched units, removal, repeated no-match, absent/out-of-range/ambiguous unit labels, serial/model prompts, conflicting duplicate models, cross-unit rejection, wrong equipment sections and section-level explicit unit resolution.

UI integration: call EquipmentAirflowService.GetSourceForUnit(info, unitNumber), not aggregate DesignAirflowSource, for unit-specific labels. Required production EnergyComplianceInfo property is documented in ../../../../red-mapping-review/AIRFLOW-API.md.

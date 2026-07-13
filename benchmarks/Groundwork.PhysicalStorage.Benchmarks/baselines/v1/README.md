# Physical-storage baseline registry v1

This registry is intentionally empty until a complete scheduled run is executed on controlled
infrastructure and approved. Do not add synthetic benchmark numbers or partial smoke results.

An entry may be added only when `reports/elsa-migration-decision.json` reports
`baselineEligibility.eligible: true`. Archive the complete immutable run directory, calculate a
stable machine/configuration fingerprint outside the harness, and reference both the archive and
decision report from `baseline-index.json`.

Changing the machine, CPU architecture, operating system, .NET runtime, GC mode, provider version,
provider topology, or material database configuration requires a new baseline entry. Never edit or
replace an archived baseline in place.

# BODY-PRESET corpus

These fixtures were generated with the real product sources from
`d43deab60dadee19e22e02496efbadb75d985b20`, archived without changing the checkout.
`Generate.cs` is the producer driver, compiled alongside those sources using the
same Unity 6000.3.10f1 Roslyn/netcorerun references as `run-standalone.py`.
It accepts one fresh, uniquely named output directory. It also loads and resaves
the legacy document and checks that only SAVE@1's documented epoch increment
changes its payload. It uses no mocks or external services.

- `baseline-presets.json`: seeds 1234, 4242 and 999, eight resident IDs and eight
  existing dimensions per seed. SHA-256:
  `10e15043f27fdddf7fc922d55a25814085734f4d4422cf812558dc3bed97d66a`.
- `legacy-v1-save.json`: actual SaveV1 output with no base-body key, duplicate
  names, money, needs, traits, relationships, family, memory, clock state, shared
  balance/payment ledger and food stock. SHA-256:
  `70eb5200e1d6269a1e94830aaaadaeab384909990a29f1db2b3f3777ec2a4fa4`.

The suite derives checksum-valid invalid saves from this real document by adding
`baseBody=-1`, `baseBody=2`, `build=1` or `height=1`. Each case has a fresh directory
and retains a valid backup. The legacy comparison checks every payload section;
it permits only the new appearance key and SAVE@1's epoch increment.

The test API contract is at the top of `BODY-PRESET.cs`. Base body is separate
from the existing eight-dimension API so existing suites remain unchanged.
`Male=0` preserves the legacy value, `Female=1`, and the persisted key is
`baseBody`. Newborn assignment and rendered mesh selection belong to BIRTH and
SLICE, respectively. Red-step evidence is in
`evidence/production/BODY-PRESET/red.log`; green acceptance belongs to the product
developer and later review/assurance.

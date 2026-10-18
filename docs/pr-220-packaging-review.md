# PR #220 packaging and CI review

Reviewed original PR range `a5dc9928..4009385` on `codex/pr220-packaging-review`, based on the initial fixes at `72e08ed`.

## Result

No additional actionable defect was substantiated in this review area. No production changes were made merely to replace existing defensive behavior.

## Coverage

- `.github/workflows/ci.yml`: sequential macOS packaging, independent failure handling, cold cross-publish cache and diagnostics upload.
- `.github/workflows/test.yml`, `tools/test-shards.py`, `tools/test-shards-tests.py`: collection-aware scheduling, deterministic decimal comparisons, shard selection and result coverage.
- `.github/workflows/arm-validation.yml`, `.github/workflows/focused-native.yml`, `tools/check-arm64-pe.ps1`: native architecture checks, filters/discovery, exit-code propagation, installer checks and evidence collection.
- `tools/package-mac.sh`, `tools/dmg-mount.py`, `tools/test-dmg-mount.py`: backing-image/device ownership, cleanup before removal, partial attachments, refusal on unverifiable ownership and exit traps.
- `global.json`, package changes/locks, `Passenger.Inspector.Wpf.csproj`, `Passenger.Lsp.csproj`, `RoslynServer.targets`: SDK pinning, publishing runtime coverage and forwarding the target Roslyn server identity to the portable extension.

## Validation

- Sharding CLI regression suite against the actual discovered suite: **4 passed**.
- Windows six-shard generation: **1,039 tests in 129 classes**, successful.
- Git Bash syntax-only check of `tools/package-mac.sh`: passed.
- PowerShell parser check of `tools/check-arm64-pe.ps1`: no errors.
- Full solution and focused application checks are recorded in the main review report.

These checks do not replace native macOS disk-image creation or native Linux/ARM installer execution. Those were not run on this Windows x64 host. The Unix executable-based disk-image tests remain platform-skipped here, as recorded in the main report.

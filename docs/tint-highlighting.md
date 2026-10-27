# Tint highlighting comparison

Passenger's non-C# highlighter uses Tint 0.1.2 / GregsEx 0.5.3 in place of TextMateSharp 2.0.4 / Onigwrap 1.0.11. C# continues to use Roslyn. The replacement retains Passenger's grammar assets, scope-to-colour mapping, normalization, YAML key policy and incremental line tables.

## Reference experiment

The [reference branch](https://github.com/sebastienros/passenger/tree/reference/tint-instrumentation) preserves the instrumentation, benchmark harness, fixtures, raw measurements and reproduction instructions. The [fixed experiment commit](https://github.com/sebastienros/passenger/tree/0f1ae0c6/tools/Passenger.HighlightingBenchmarks) remains available independently of that branch. None of its diagnostic hooks is included in the production replacement.

The measurements below were collected against upstream `97758c1ac1d198b02b7ac2282d3c1018f0023282`, before the replacement was rebased onto newer main. They are evidence from that experiment, **not a fresh measurement of the rebased branch**.

Apple M4 Pro, 48 GiB RAM, macOS 15.8 arm64; .NET 11 RC3, Release. Ten workloads repeat existing Passenger sources to about 10,000 lines. Total measurements use three fresh processes per engine, two warmup passes after first use and seven measured full passes. Edit measurements use five measured batches of 100 one-character replacements. Results are medians of process medians. Tokenizer measurements use a separate instrumented run on four workloads.

## Full-file time

| Workload | Total time: TMS → Tint (ratio) | Tokenizer time¹: TMS → Tint (ratio) |
|---|---:|---:|
| XML | 87.76 → 80.36 ms (**0.92**) | 75.09 → 55.51 ms (**0.74**) |
| CSS | 4,141.73 → 307.37 ms (**0.07**) | 4,019.21 → 297.41 ms (**0.07**) |
| JavaScript | 1,686.04 → 235.57 ms (**0.14**) | 1,657.43 → 230.40 ms (**0.14**) |
| Less | 170.30 → 34.76 ms (**0.20**) | 163.14 → 101.35 ms (**0.62**) |

**Legend:** TMS = TextMateSharp. Ratio = Tint ÷ TMS; **0.33** means 33% of TMS's time, or 67% less. Total time includes tokenization, line preparation, cache updates and background dispatch, excluding UI rendering. ¹Tokenizer time is measured through instrumentation in separate runs, with measurement overhead. Columns are not additive.

## Allocations per edit

| Workload | Total allocations/edit: TMS → Tint (ratio) | Tokenizer allocations/edit¹: TMS → Tint (ratio) |
|---|---:|---:|
| XML | 18,252 → 17,636 B (**0.97**) | 944 → 328 B (**0.35**) |
| CSS | 21,164 → 15,572 B (**0.74**) | 936 → 336 B (**0.36**) |
| JavaScript | 17,732 → 15,556 B (**0.88**) | 1,928 → 328 B (**0.17**) |
| Less | 17,620 → 16,716 B (**0.95**) | 976 → 72 B (**0.07**) |

**Legend:** TMS = TextMateSharp. Ratio = Tint ÷ TMS. Total allocations cover the complete edit/highlighting operation, excluding UI rendering. ¹Tokenizer allocations come from separate instrumented runs. Values represent allocated managed memory, not retained RAM. This table measures **one-character edits**, not full-file passes.

## Interpretation and limits

- All ten expanded workloads produced identical complete colour-span hashes across both engines and all three processes. Edit batches restored the original colour spans. This corpus comparison does not prove compatibility for every input.
- Full-pass managed allocations fell 76–97%, and process CPU time fell 11–93% across the ten workloads. These are highlighter measurements, not whole-IDE CPU/RAM claims.
- Working set was mixed: JavaScript fell from 115.0 to 89.4 MiB, while Markdown rose from 90.2 to 97.2 MiB and HTML from 87.2 to 93.0 MiB.
- Whole XML and Less edits were slightly slower in the experiment (22.1 → 25.2 µs and 13.2 → 16.5 µs). Lower tokenizer allocations do not eliminate table maintenance and dispatch costs.
- Default tiered compilation remained enabled. Short-run variability was material for XML, JSON, Markdown and Less; the instrumented tokenizer and total-time columns must not be subtracted to estimate overhead.
- The experiment used runtime-loaded grammars, without generated kernels or hot-pattern compilation. The preserved harness and raw results describe the exact methodology and limitations.

## Validation of the cleaned replacement

On upstream `31db588d` plus the replacement: locked restore, the full Release solution build (zero warnings/errors), all 16 colour/responsiveness tests, and the package-notices check passed. The added tests cover standalone Razor's HTML/C# grammar dependencies and independent JSON document states sharing one tokenizer. The complete cross-platform suite and language-server-backed Razor tests were not run for this cleaned branch.

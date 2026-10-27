# Tint highlighting comparison

Passenger's non-C# highlighter uses Tint 0.1.2 / GregsEx 0.5.3 in place of TextMateSharp 2.0.4 / Onigwrap 1.0.11. C# continues to use Roslyn. The replacement retains Passenger's grammar assets, scope-to-colour mapping, normalization, YAML key policy and incremental line tables.

## Current comparison and reference branches

Measured on 2026-10-27 UTC against the same upstream revision: TextMateSharp at `fa86d9d42dbb53a8067f06f212d80d09540a3fe4`, and Tint at PR commit `9c5b2d034f6385d5888bea59a0e9cd4d90ba1e47`, which merges that upstream. Both reuse a line buffer within each pass. Subsequent PR changes only update this report.

The [current instrumented reference branch](https://github.com/sebastienros/passenger/tree/reference/tint-instrumentation-current) preserves the harness, instrumentation, raw measurements, binary/fixture hashes and reproduction instructions ([fixed snapshot](https://github.com/sebastienros/passenger/tree/1499d3df/tools/Passenger.HighlightingBenchmarks)). The [historical reference branch](https://github.com/sebastienros/passenger/tree/reference/tint-instrumentation) and [fixed historical commit](https://github.com/sebastienros/passenger/tree/0f1ae0c6/tools/Passenger.HighlightingBenchmarks) retain the earlier results. No experimental instrumentation or benchmark packaging changes are included in the PR's production code.

Apple M4 Pro, 48 GiB RAM, macOS 15.8.1 arm64; .NET 11 RC3, Release. Ten workloads repeat the original, unchanged Passenger fixtures to about 10,000 lines. Each engine/workload uses three fresh processes, sequentially, reversing engine order in the second round. Each process runs a first-use pass, two warmups, seven measured full passes and five measured batches of 100 one-character edits after an edit warmup. Values are medians of process medians. Tokenizer columns use three separate instrumented processes per engine on the four listed workloads, with the same warmup/pass/edit counts.

Both benchmark checkouts omit copying the unused web-language-server bundle after file-access failures blocked that packaging step. The harness never starts these servers; highlighter code and tokenizer dependencies are unaffected. See the reference README for the exact build setting.

## Full-file time

| Workload | Total time: TMS → Tint (ratio) | Tokenizer time¹: TMS → Tint (ratio) |
|---|---:|---:|
| XML | 113.51 → 76.33 ms (**0.67**) | 76.55 → 65.11 ms (**0.85**) |
| CSS | 4,117.25 → 307.50 ms (**0.07**) | 4,111.73 → 300.20 ms (**0.07**) |
| JavaScript | 1,678.45 → 235.39 ms (**0.14**) | 1,668.65 → 230.66 ms (**0.14**) |
| Less | 169.40 → 49.27 ms (**0.29**) | 163.54 → 81.12 ms (**0.50**) |

**Legend:** TMS = TextMateSharp. Ratio = Tint ÷ TMS; **0.33** means 33% of TMS’s time, or 67% less. Total time includes tokenization, line preparation, cache updates and background dispatch, excluding UI rendering. ¹Tokenizer time comes from separate instrumented runs, with measurement overhead. Columns are not additive and must not be subtracted to estimate overhead.

## Allocations per edit

| Workload | Total allocations/edit: TMS → Tint (ratio) | Tokenizer allocations/edit¹: TMS → Tint (ratio) |
|---|---:|---:|
| XML | 18,252 → 17,636 B (**0.97**) | 944 → 328 B (**0.35**) |
| CSS | 21,164 → 15,572 B (**0.74**) | 936 → 336 B (**0.36**) |
| JavaScript | 17,730 → 15,556 B (**0.88**) | 1,928 → 328 B (**0.17**) |
| Less | 17,620 → 16,716 B (**0.95**) | 976 → 72 B (**0.07**) |

**Legend:** TMS = TextMateSharp. Ratio = Tint ÷ TMS. Total allocations cover the complete edit/highlighting operation, excluding UI rendering. ¹Tokenizer allocations come from separate instrumented runs. These are allocated managed bytes, not retained RAM. This table measures **one-character edits**, not full-file passes.

## Full-file CPU, allocations and working set

| Workload | Process CPU: TMS → Tint (ratio) | Allocated: TMS → Tint (ratio) | Working set: TMS → Tint (ratio) |
|---|---:|---:|---:|
| XML | 130.52 → 90.32 ms (**0.69**) | 32.85 → 4.83 MiB (**0.15**) | 79.94 → 73.66 MiB (**0.92**) |
| JSON | 74.13 → 67.46 ms (**0.91**) | 27.63 → 4.85 MiB (**0.18**) | 82.03 → 72.20 MiB (**0.88**) |
| YAML | 211.60 → 134.64 ms (**0.64**) | 24.37 → 2.77 MiB (**0.11**) | 83.31 → 73.33 MiB (**0.88**) |
| Markdown | 156.03 → 147.65 ms (**0.95**) | 16.33 → 2.77 MiB (**0.17**) | 91.34 → 93.64 MiB (**1.03**) |
| HTML | 214.85 → 53.91 ms (**0.25**) | 75.10 → 4.15 MiB (**0.06**) | 90.39 → 92.03 MiB (**1.02**) |
| CSS | 4,206.95 → 315.11 ms (**0.07**) | 76.01 → 4.44 MiB (**0.06**) | 96.22 → 87.30 MiB (**0.91**) |
| JavaScript | 1,714.76 → 240.77 ms (**0.14**) | 146.22 → 3.99 MiB (**0.03**) | 114.86 → 87.06 MiB (**0.76**) |
| Shell | 461.33 → 68.61 ms (**0.15**) | 68.37 → 4.77 MiB (**0.07**) | 84.06 → 87.59 MiB (**1.04**) |
| SCSS | 1,203.18 → 80.78 ms (**0.07**) | 32.86 → 2.10 MiB (**0.06**) | 97.80 → 84.16 MiB (**0.86**) |
| Less | 175.45 → 98.83 ms (**0.56**) | 43.57 → 2.40 MiB (**0.06**) | 83.22 → 84.17 MiB (**1.01**) |

**Legend:** CPU is process CPU consumed during a full pass, summed across threads. Allocated is managed allocation during that pass. Working set is sampled after forced GC and includes runtime, JIT, heaps and native libraries; it is not the highlighter’s retained memory. All ratios are Tint ÷ TMS. These measurements exclude IDE rendering and language-server activity.

## Interpretation and limits

- All 60 uninstrumented and 24 instrumented processes completed successfully. Input and complete colour-span hashes match across engines, rounds and instrumentation modes. Every edit batch restored the original colour spans. This corpus does not prove compatibility for every input.
- Full-pass managed allocations fell 82–97%, and process CPU fell 5–93% across the ten workloads. CSS and JavaScript full-pass medians are 13.39× and 7.13× faster; SCSS is 15.28× faster. These are highlighter measurements, not whole-IDE improvements.
- JSON's full-pass median increased from 61.38 to 64.87 ms (ratio 1.06). This small difference is within substantial process-to-process variation: TextMateSharp process medians ranged 50.79–84.69 ms and Tint 61.57–78.21 ms. Treat it as an observation, not a demonstrated steady-state regression.
- Working set decreased in six workloads and increased in Markdown, HTML, shell and Less. Allocation savings do not imply the same RAM savings.
- Whole XML and Less edits were slower: 20.3 → 24.7 µs and 13.8 → 16.4 µs. SCSS edit allocations increased from 16,084 to 16,836 bytes despite faster edits. One-line edits allocate a per-pass line buffer in both engines, so buffer reuse chiefly benefits passes covering multiple lines.
- Default tiered compilation remained enabled. Seven measured passes do not guarantee steady state. XML, JSON, Markdown and Less show material variation; [all three process medians](https://github.com/sebastienros/passenger/blob/reference/tint-instrumentation-current/tools/Passenger.HighlightingBenchmarks/variability.md) are retained. The tables report observations on one machine, not confidence intervals or statistical significance.
- Instrumented runs include clock/counter and event overhead. Their stage accounting reconciles exactly, but their tokenizer times must not be subtracted from uninstrumented total time. TextMateSharp's encoded API does not expose its early-stop flag; Tint reported no stopped lines in these runs.
- Grammars load at runtime, without generated kernels or hot-pattern compilation. UI rendering, language-server activity and concurrent-document throughput are outside this benchmark.

## Validation of the cleaned replacement

On upstream `31db588d` plus the replacement: locked restore, the full Release solution build (zero warnings/errors), all 16 colour/responsiveness tests, and the package-notices check passed. The added tests cover standalone Razor's HTML/C# grammar dependencies and independent JSON document states sharing one tokenizer. The complete cross-platform suite and language-server-backed Razor tests were not run for this cleaned branch.

After merging upstream `fa86d9d4` and retaining its line-buffer reuse, locked restore, the full Release solution build (zero warnings/errors), and all 16 colour/responsiveness tests passed again.

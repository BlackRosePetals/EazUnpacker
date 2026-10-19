# EazUnpacker

A command-line unpacker and deobfuscator for **Eazfuscator.NET managed assemblies**. Recover analyzable IL, generate readable identifiers, and optionally export a C# project.

[简体中文](README.md) · **English** · **v1.1.0** · [Changelog](CHANGELOG.md)

[Quick start](#quick-start) · [Features](#features) · [Options](#options-and-exit-codes) · [Logs](#logs-and-statistics) · [FAQ](#faq) · [Development](#development-and-tests)

> Recovery uses metadata and IL semantics, not sample names or bundled known-key tables. Results are protection-dependent; arbitrary assemblies are not guaranteed to be fully restored.

## Features

| Area | Capabilities |
| --- | --- |
| Strings and resources | Recover supported strings, resources and embedded assemblies; extract identity-checked Costura managed dependencies |
| Devirtualization | Discover VM formats and call sites; recover supported instructions, signatures and exception regions; retain originals on failure |
| Inlining and proxies | Recover multiple VM sites per host; support constrained `ref` write-back; bypass transparent proxy chains and inline bounded, pure integer leaves |
| Control flow | Fold proven integer/enum predicates, simplify switch dispatch and jump chains, propagate constants across unique predecessors, remove unreachable IL and unused local stores |
| Readable symbols | Generate role-, base-type- and entry-point-aware names, shorten parameters, emit name maps and Portable PDBs |
| Key reporting | Log verified keys with tokens and input namespace/type/method identities; retain structured records |
| Optional source export | Export a C# project and string index with ILSpy, then attempt a rebuild |
| Optional CLR validation | Prepare recovered hosts, inline adapters and access bridges; **disabled by default** |

## Quick start

### 1. Extract the complete release

Extract `EazUnpacker-v1.1.0-win.zip` and keep every subdirectory, including `worker\modern`. Run `Unpacker.exe`; copying the EXE alone is not sufficient.

- **Runtime**: Windows and .NET Framework 4.8. Modern .NET targets also need a suitable .NET Desktop Runtime. The modern worker targets .NET 8 and can roll forward to installed newer major versions.
- **Permissions**: use a short, writable directory; the tool creates `temp` beside its executable. Framework loaders or third-party tools can still encounter Windows long-path limits.
- **SDK**: not required for ordinary recovery. Rebuilding C# needs a .NET SDK and the target project's references. See [development](#development-and-tests) to build the tool itself.

### 2. Run a command

Open PowerShell in the extracted directory:

```powershell
# Default: recover IL; no CLR preparation check and no source export
.\Unpacker.exe unpack "C:\samples\input.dll" "C:\output\input_unpacked.dll"

# Explicitly enable CLR validation
.\Unpacker.exe unpack "C:\samples\input.dll" "C:\output\input_unpacked.dll" --validate-clr

# Export/rebuild C# and also validate recovered/rebuilt methods
.\Unpacker.exe unpack "C:\samples\input.exe" "C:\output\input_unpacked.exe" --export-source --validate-clr

# Static metadata analysis; stdout is JSON
.\Unpacker.exe analyzer "C:\samples\input.dll"
```

### 3. Inspect the result

The console reports the output file, diagnostic directory and final counts. Check the exit code first, then inspect `result.json` in that run's directory. A check that did not run has not passed.

Input and output must differ. Available recovered assemblies are published even when later stages are incomplete; reports retain the partial status. Deploy generated Framework EXE `.config` files alongside their EXEs. DLLs remain dependent on their host and dependencies.

Published symbols match the output name: `input_unpacked.dll` gets only `input_unpacked.pdb`, not an extra `input-recovered.pdb`. CodeView names are rebound accordingly, including long/Unicode filenames. Intermediate files stay under `temp`.

## Options and exit codes

| Option | Default | Effect |
| --- | --- | --- |
| `--export-source` | Off | Export a C# project and string index, then attempt compilation |
| `--validate-clr` | Off | Prepare recovered IL in the CLR; also validate rebuilt methods when exporting source |

Options may appear before or after paths. Unknown or duplicate options fail. Optional stages that were not requested do not count as recovery failures; **not checked does not mean passed**.

| Code | Meaning |
| --- | --- |
| `0` | Requested stages completed |
| `2` | Output is available, but recovery or a requested optional stage is incomplete |
| `1` | Invalid arguments, input errors or processing errors |

## Logs and statistics

The default UTF-8 log shows stages, progress, verified keys, bounded failure summaries and final counts. **Host methods** and **VM call sites** are counted separately; ordinary methods are not counted as restored VM methods.

| File | Contents |
| --- | --- |
| `progress.log` / `progress.jsonl` | Readable progress / structured events |
| `vm-summary.json` / `vm-methods.json` | VM totals / per-call-site results |
| `vm-keys.jsonl` / `vm-keys.json` | Immediate / final key records, including original input identities and sources |
| `vm-detail.log` | Low-level VM diagnostics, hidden from normal console output |
| `inline-expressions.json` | Integer-expression inline locations and evidence |
| `rename-map.json` / `control-flow-cleanup.json` | Symbol mappings / control-flow edits |
| `recovery-statistics.json` / `result.json` | Final statistics / stage status |
| `clr-validation.json` | Created only when requested and recovered VM methods need validation |

Original names mean names still present in this run's VM input, not recovered author symbols. A key passing its original verifier does not guarantee subsequent region decoding succeeds.

Only originally verified values are labeled `[密钥]`. The console shows the actual integer/string; JSON retains `Value`, `KeyType`, original method identities, and the exact `CipherKeyEncoding` / `CipherKeyBytesHex` used for decryption. Integers use four big-endian bytes; strings use UTF-16LE. Rejected candidates are not reported as correct keys.

Key recovery infers expressions and types first, restores other VM methods to expose candidates, then retries unresolved regions. String keys do not enter integer enumeration. Standard resource identifiers and COM outcomes remain subject to original verification; they are not sample-key profiles. Failed candidates are deduplicated for fixed verifier inputs within the run. Integer fallback has both count and time limits. Target verifiers with unknown thread safety are not invoked concurrently.

### Environment variables

| Variable | Purpose |
| --- | --- |
| `UNPACKER_DEPENDENCY_PATHS` | Additional absolute dependency directories, separated by `;` on Windows |
| `UNPACKER_VM_VERBOSE=1` | More mapping information |
| `UNPACKER_VM_DEBUG=1` | Per-method/instruction diagnostics |
| `UNPACKER_VM_KEY_SEARCH_LIMIT` | Bounded integer search count; default `65536`, `0` disables it, maximum ten million |
| `UNPACKER_VM_KEY_SEARCH_SECONDS` | Module-wide integer fallback time budget; default `20` seconds, at most `5` per verifier, `0` disables it, maximum `120` |
| `UNPACKER_VM_INT32_KEYS` | Comma-separated integer candidates; original verification remains mandatory |
| `UNPACKER_VM_STRING_KEYS` | Explicit string candidates as a JSON array; original verification remains mandatory |
| `UNPACKER_VM_USE_KEY_CACHE=1` | Permit explicitly supplied historical candidate cache |
| `UNPACKER_VM_KEY_CACHE_PATH` | Candidate cache path; no sample-key table is bundled or read by default |

## Temporary files and outputs

All temporary work lives in **`temp` beside `Unpacker.exe`**, not `%LOCALAPPDATA%\Unpacker\work`. This is the executable directory, not the shell's current working directory.

```text
Unpacker.exe
worker/
  Unpacker.Worker.exe
  modern/
temp/
  work/<output-path-hash>/
    latest.json
    run-<timestamp>-<unique-id>/
      result.json
      progress.log
      ...
  analysis-<unique-id>/      # removed after analysis
  tests-<unique-id>/         # removed after self-tests
```

The main program and packaged workers share this root. Child `TEMP` / `TMP` variables point here too. The installation directory must be writable; failures are reported instead of silently falling back elsewhere. Diagnostics, requested source and rebuilt outputs remain in their run directories. Clean `temp` only after processing has stopped. Old AppData caches are not automatically migrated or deleted.

## Validation boundaries

- Arbitrary runtime keys, VM variants and obfuscation patterns cannot be guaranteed recoverable. Constant branches across loops or complex joins may remain.
- Integer-leaf inlining preserves argument order and exact-once evaluation. Unproven exception, field, external-call, generic and prefixed cases are retained.
- Flow simplification requires proof; unknown joins, escaped references, exception regions and reachable side effects are preserved.
- Definitions and tokens are retained where possible. Names or appearance alone never justify removing reflection-visible members, verifiers or resources.
- `EncryptedRegions=0` does not prove the absence of encryption. Generated identifiers are not original author symbols.
- CLR validation is loading/JIT preparation, not business-equivalence testing. It may trigger initialization. Disabling it does not mean no target decoder code is ever executed by recovery.
- Historical local reports covered 3305 VM sites across 29 Aspose DLLs. VBA recovery covered 324 sites, with five original-equivalent initialization failures outside Office/VBE. Historical results are not automatically a rerun of every later build.

## FAQ

**Why are there empty methods or repeated blocks?**

The original program may contain empty event handlers, and VM decoding may produce a `ret`-only body. Similar blocks can belong to different branches or result from protector inlining. Automatic reverse inlining is not guaranteed, and matching appearances do not justify deleting side effects. The development section includes an original/recovered empty-body comparison script.

**Why does exit code `2` still produce a DLL/EXE?**

Available recovered output is retained. Inspect failed stages in `result.json` to distinguish incomplete VM recovery from requested CLR checks or source-build failures. File existence alone is not a success criterion.

**Why does exported C# sometimes fail to compile?**

Readable IL is not the original source structure. Source export is optional and can be limited by compiler representation, missing references and host requirements. An export failure does not remove the recovered assembly already published.

**Where are logs and sources, and may I remove them?**

The console prints the full `temp\work\...\run-...` path. Clean `temp` only after every processing job has stopped, and first save reports, key records and source exports you need.

## Examples

<details>
<summary>Entry-point cleanup: before / after</summary>

![Entry point before](src/assets/screenshots/entry-point-before.png)
![Entry point after](src/assets/screenshots/entry-point-after.png)

</details>

<details>
<summary>Strings and control flow: before / after</summary>

![Method before](src/assets/screenshots/method-before.png)
![Method after](src/assets/screenshots/method-after.png)

</details>

## Dependencies and licenses

The combined project is [GPL-3.0](LICENSE). Third-party components retain their own licenses. See [NOTICE](NOTICE) for source origins, pinned baselines and local modifications.

### Vendored source

| Component | Role | License |
| --- | --- | --- |
| [EazFixer](src/ThirdParty/EazFixer) | String, resource and embedded-assembly recovery | [MIT](src/ThirdParty/EazFixer/LICENSE) |
| [EazyDevirt](src/ThirdParty/EazyDevirt) | VM discovery, decoding and IL recovery | [GPL-3.0](src/ThirdParty/EazyDevirt/LICENSE) |
| [Echo](src/ThirdParty/Echo) | CIL analysis and emulation infrastructure | [LGPL-3.0](src/ThirdParty/Echo/LICENSE.md) |

### Main NuGet references

| Package | Pinned version | Role |
| --- | --- | --- |
| dnlib | 4.5.0 | Metadata, IL and PDB rewriting |
| AsmResolver.DotNet | 6.0.0-beta.4 | VM backend PE/metadata and CIL generation |
| Lib.Harmony | 2.4.2 | Runtime adaptation in isolated decoder workers |
| BouncyCastle.Cryptography | 2.5.1 | VM encrypted-stream support |
| ICSharpCode.Decompiler | 11.1.0.9782 | ILSpy decompilation and project export |
| Newtonsoft.Json | 13.0.3 | Reports and progress serialization |
| System.Memory | 4.6.3 (main project) | Framework compatibility |

Echo also keeps upstream references such as `System.Collections.Immutable` and `Nullable`. Project files, NuGet metadata and included license files are authoritative for the complete dependency graph and licenses. These are pinned versions, not a latest-version list.

## Development and tests

Run from the repository root. Building requires an SDK that supports `.slnx` and .NET Framework 4.8 development references. This v1.1.0 release was built with the .NET 10 SDK.

```powershell
dotnet build Unpacker.slnx -c Release
```

The entry point is `src\bin\Release\net48\Unpacker.exe`. The release script builds a committed snapshot, runs self-tests and excludes samples, caches and temporary work:

```powershell
pwsh -File .\scripts\Build-Release.ps1
```

The package includes matching source, licenses and a per-file hash manifest.

```powershell
# Framework / modern self-tests; no external samples required
.\src\bin\Release\net48\worker\Unpacker.Worker.exe --self-test
.\src\bin\Release\net48\worker\modern\Unpacker.ModernWorker.exe --self-test

# Validate the CLR option and program-local temporary directory with your sample
.\src\tests\ClrValidationOptions.ps1 -InputAssembly "C:\samples\input.dll"

# Compare empty bodies by preserved MethodDef tokens, separating original no-ops
.\src\tests\CompareEmptyMethods.ps1 -Original "C:\samples\input.dll" -Recovered "C:\samples\input_unpacked.dll" -Report "C:\samples\empty-methods.json"

# Strict matrices explicitly enable CLR validation and retain failures
.\src\tests\AsposeCompatibility.ps1 -SampleRoot "C:\samples\Aspose" -RequireComplete
.\src\tests\VersionCompatibility.ps1 -SampleRoot "C:\samples" -RequireComplete
```

Main sources: `src/`; workers: `src/WorkerHost/` and `src/ModernWorker/`; recovery components: `src/ThirdParty/`; regression scripts: `src/tests/`.

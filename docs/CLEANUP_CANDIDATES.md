# Project Cleanup Candidates

Scope: documentation and support scripts that affect release/testing knowledge. No files have been deleted.

## Deleted

| File | Recommendation | Why |
| --- | --- | --- |
| `tools/fixcrlf.ps1` | Deleted. | One-off CRLF repair script. It hardcoded this local path and wrote to `bin\Debug\diagnose.bat`, so it was not portable and had no value for customer release validation. |
| `tools/diagnose.ps1` | Deleted. | Older PowerShell diagnostic, not copied by `AIRenderer.csproj`, not referenced by the release guide, and stale versus the current `.bat` flow. It still expected net7 files unconditionally and did not reflect the current net48 fallback packaging. |

## Rewrite Or Merge Candidates

| File | Recommendation | Why |
| --- | --- | --- |
| `CLAUDE.md` | Keep, but refresh instead of deleting. | Useful developer handoff, but currently stale: it describes the fallback as root `TuoJieSidecar-net48.exe`, while the current preferred layout also uses `net48-sidecar\TuoJieSidecar.exe`. It also says CI publishes a standalone `.rhp`, which no longer matches the intended package-only release flow. |
| `README.md` | Keep, but refresh as the short customer/developer entry point. | Useful top-level overview. It should mention the current release test command and correct package expectations. |

## Keep

| File | Why |
| --- | --- |
| `tools/diagnose.bat` | Customer-facing diagnostic. It is copied into build output and release packages, checks package files/runtime/Rhino/Sidecar, and matches the current support workflow. |
| `tools/preflight-release.ps1` | Developer release gate. It builds both frameworks, runs diagnosis, checks large pipe requests, and simulates extracted packages. |
| `tools/preflight-release.bat` | Needed wrapper so the preflight can run without depending on PowerShell PATH or execution policy defaults. |
| `docs/RELEASE_TEST_GUIDE.md` | Current manual test checklist for Rhino/API/firewall/customer-machine validation. |

## Notes Before Deleting

- Do not delete `tools\diagnose.bat`; it is part of the packaging contract.

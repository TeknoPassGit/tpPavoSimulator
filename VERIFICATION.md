# PAVO-only Release verification — 2026-11-05

- No ZIP, source copy or backup created for this task.
- Ticket host/endpoints, model/state, UI counter and two ticket-specific HTTP tests removed. Obsolete generated verification/test artifacts and disk-served UI output cleaned. Existing active PAVO runtime retained all 11 transactions, including 7 captured payments. Empty legacy service field removed; startup compatibility discards that field without resetting PAVO records.
- Debug build succeeded: 0 warnings, 0 errors.
- Release build succeeded: 0 warnings, 0 errors.
- Exact requested win-x64 self-contained single-file publish succeeded. Panel, templates and Release symbols embedded; publish output contains only TpPavoSimulator.exe.
- Debug HTTP suite: 83 passed, 0 failed. Results: tests/artifacts/f75faae9a8ce4d8f80f6e2622f5b3efd/test-results.json.
- Final published EXE HTTP suite: 83 passed, 0 failed. Results: tests/artifacts/9461d15467b04480b0a1eaa5cdb973f3/test-results.json.
- EXE launched directly from a separate working directory, with isolated data. Real cash/card templates, items/amount/VAT, six observed errors, 72/73 correction/restart/idempotency, response trace and embedded panel/JS/CSS/CSP passed.
- 18081 stayed closed while the EXE ran. All test-owned processes stopped; no listener remained on 18080 or 18081.
- SuccessResponse.cs and CorrectionEngine.cs SHA-256 unchanged. No other project, real device or production API used.
- EXE: Release\TpPavoSimulator.exe; 96,405,691 bytes.
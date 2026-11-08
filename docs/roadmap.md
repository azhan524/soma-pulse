# Open-source Roadmap

生命度与最终验收标准见 [硅基生命里程碑-v2.md](硅基生命里程碑-v2.md)。

## Exit criteria

The first public release is ready only when all of these are true:

- [x] `brain / body / face` code is physically separated and buildable (v0).
- [x] .NET 10 build passes locally.
- [ ] .NET 10 build passes in CI.
- [ ] Legacy C# 5 build still passes.
- [x] Full test suite passes: stream 345 / rules 293 / settings 111 / geometry 22 (one environment flake observed once).
- [ ] No secrets, personal paths, account IDs, or runtime data in the repo.
- [ ] LICENSE, README, SECURITY, CONTRIBUTING, NOTICE are present.
- [ ] Audit broker install/uninstall is documented and tested.
- [ ] Threat model is reviewed.
- [ ] At least one clean-machine install test.

## Milestones

### M1 — Repository hygiene

- [x] Target naming and three-module layout.
- [x] Engineering document.
- [x] LICENSE / README / SECURITY / CONTRIBUTING / NOTICE.
- [x] `.gitignore`.
- [ ] Secret scan and history rewrite if needed.
- [ ] Remove personal absolute paths from docs.
- [ ] Initialize Git history and tags.

### M2 — Build reproducibility

- [ ] Restore .NET 10 NuGet access (CefSharp).
- [ ] CI: legacy build + full tests.
- [ ] CI: .NET 10 build.
- [ ] Publish checksums and release notes.

### M3 — Module migration

- [x] Extract `ahz-scribe-body` tool/audit/shell code (v0).
- [x] Extract `ahz-scribe-brain` model/memory/pulse code (v0).
- [x] Extract `ahz-scribe-face` UI/CLI (v0).
- [x] Process boundary between brain and body (named pipe + BrainHost).
- [x] Contracts for `ActionRequest` / `ActionResult`.

### M4 — Security hardening

- [x] payloads user-read-only; only broker writes.\n- [ ] Single append-only payload stream.
- [ ] Decide whether to prevent false appends or accept/review them.
- [ ] Optional separate low-privilege broker account.
- [ ] Reproducible build notes.
- [ ] External review / pentest.

### M5 — Release

- [ ] Version 0.1.0 tag.
- [ ] Release binaries + source.
- [ ] Security advisory process.
- [ ] Announce with limitations, not marketing claims.

## Non-goals

- cross-platform in the first release;
- bundling QQ/NapCat credentials;
- a hosted SaaS;
- claiming “unhackable” or “fully sandboxed”.

## License decision

Current draft: AGPL-3.0.

If the goal is maximum adoption or commercial-friendly integration, change to Apache-2.0 before the first public release. Do not mix licenses without a deliberate decision.

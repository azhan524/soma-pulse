# Contributing

## Before you start

This repository contains the modular SomaPulse release. AhzScribe is the Agent
inside the project; `ahz-scribe-*` is a compatibility identifier for the
already-validated binaries and is intentionally not renamed yet.

Please open an issue before large refactors, especially anything touching:

- permission / approval logic;
- audit broker or ACLs;
- shell, file, network and desktop tools;
- prompt construction and memory injection;
- licensing.

## Development rules

- Windows-first; .NET 10 is the current build target.
- Keep the face / brain / body process boundaries.
- Keep dangerous tools approval-gated and audit-logged.
- Do not commit `config.json`, `sessions.json`, API keys, QQ credentials, audit keys, or runtime data.
- Do not weaken read-only/workspace-write guards for convenience.
- Add or update the fixed smoke tests under the corresponding module.
- Keep new public names lowercase kebab-case; retain `AhzScribe.*` assembly names until a breaking release.

## Local checks

```powershell
dotnet build contracts/AhzScribe.Contracts.csproj
dotnet build ahz-scribe-brain/src/AhzScribe.Brain.csproj
dotnet build ahz-scribe-body/src/AhzScribe.Body.csproj
dotnet build ahz-scribe-brain-host/src/AhzScribe.BrainHost.csproj
dotnet build ahz-scribe-face/src/AhzScribe.Face.csproj
```

Expected baseline: no failures. Test counts change over time; use the script output, not a hard-coded number.

## Pull requests

- One focused change per PR.
- Explain the user-visible behavior and the security impact.
- Include test evidence.
- For UI changes, include a screenshot and note DPI/scale.
- For audit changes, state whether HMAC/broker behavior changed.

## License

By contributing, you agree that your contribution is licensed under AGPL-3.0 unless explicitly stated otherwise.

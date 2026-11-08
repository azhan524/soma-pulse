# Security Policy

## Supported versions

SomaPulse v3.0.0 / Functional Silicon Life Candidate v1.0.
The current `main` branch and the latest v3 release tag are supported.

## Reporting a vulnerability

Do **not** open a public issue for:

- arbitrary code execution;
- sandbox / permission bypass;
- audit bypass or forgery;
- credential leakage;
- prompt injection leading to tool execution;
- path traversal or workspace escape.

Instead, open a private security advisory on the repository, or contact the maintainer through the profile contact listed on the repository.

Please include:

- affected version/commit;
- minimal reproduction;
- impact;
- whether the issue requires `完全访问` permission;
- suggested fix, if known.

## Threat model summary

SomaPulse's AhzScribe agent is a local desktop agent with optional shell, file, network and UI Automation access. The primary trust boundary is **the operating-system account**, not a JavaScript sandbox.

Read [docs/threat-model.md](docs/threat-model.md) for assets, boundaries, threats and residual risks.

## Security defaults

- start in `只可查看` or `工作区内修改`;
- `完全访问` requires explicit user choice;
- dangerous tools require approval;
- read-only / plan modes block desktop input and writes;
- audit main log is NTFS append-only and HMAC-signed by a LocalSystem broker;
- API keys and broker keys are never committed.

## Out of scope

- users granting `完全访问` to untrusted prompts or untrusted models;
- malware already running as the same Windows user;
- physical access;
- QQ / NapCat account compromise;
- third-party services (model providers, QQ) and their policies.

# Contributing

Thanks for considering a contribution to NextDNS Manager.

## Before opening an issue

- Search existing issues and discussions first.
- Use Discussions for questions and feature ideas; use an issue for a reproducible bug.
- Include the app version, Windows version, expected behavior, observed behavior, and concise reproduction steps.
- Never attach API keys, profile IDs, private domains, DNS query history, IP addresses, or device names. Review and sanitize diagnostic logs and screenshots before sharing.

## Code changes

1. Fork the repository and create a focused branch.
2. Explain the problem and the smallest useful change in your pull request.
3. Build with .NET 10 on Windows and run the relevant probes described in [build and test instructions](docs/COMPILACION_Y_PRUEBAS.md).
4. Do not run probes that alter DNS on a daily-use machine. Do not add real API keys, profiles, logs, or personal screenshots to commits or tests.
5. Keep the existing minimal interface, dark/light themes, keyboard accessibility, and privacy-safe diagnostics consistent.

Please do not submit release installers or change release tags from a feature pull request. Releases are maintained separately by the project owner.

# Security Policy

## Reporting a vulnerability

Please **do not** open a public issue for security problems.

Report them privately through GitHub Security Advisories. Open this repository's **Security** tab and choose
**Report a vulnerability**. Include:

- the affected version or commit;
- steps to reproduce, or a proof of concept;
- the impact you expect (for example, which local data or capability is exposed).

You will get an acknowledgement as soon as possible. A fix will be coordinated with you before details are
published.

## Scope

Areas of particular interest:

- the update mechanism and signature verification;
- the elevated FPS helper (scheduled task);
- reading the Windows notification database, clipboard history and the keyboard hook.

## Supported versions

Only the latest release receives security fixes.

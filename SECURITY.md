# Security policy

## Supported versions

The project is under active development; only the `main` branch is considered.

## Reporting a vulnerability

**Do not open a public issue.** Use GitHub's private reporting:
https://github.com/phucho0237/Luna/security/advisories/new

Include a description, steps to reproduce and the impact. **Do not attach real data** (cookies, tokens, uids, message content). The goal is to respond within 7 days, but this is a personal project, so there is no commitment.

## Scope

In scope: session storage, cookie handling, protocol encryption and decryption in this repository, data leaks through logs, `ToString` or diagnostic tools, and the supply chain (workflows, dependencies).

Out of scope: vulnerabilities in the messaging service itself (report them to its operator through their official channels), and attacks that require code execution under the user's own Windows account (DPAPI does not protect against this, as stated in `README.md`).

## If you leaked data by mistake

Sign out that device in the official mobile app, change your password if needed, then tell the maintainer so the Git history can be cleaned up.

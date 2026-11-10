# Security policy

Atrium stores people's private files, so security reports are handled
before anything else.

## Reporting a vulnerability

Do not open a public issue. Use GitHub's private reporting instead: on the
repository page open **Security**, then **Report a vulnerability**.

Please include what you found, how to reproduce it, and which version or
commit you tested. You will get an answer within seven days.

## Supported versions

Atrium is in early development and has no release yet. Until the first
release, only the `master` branch is supported.

## Scope

In scope: the server, the web, mobile and TV clients, the Docker image and
the default configuration in this repository.

Out of scope: problems that need an attacker to already have access to the
server's disk, database or config directory, and misconfiguration of a
reverse proxy or tunnel outside what the documentation recommends.

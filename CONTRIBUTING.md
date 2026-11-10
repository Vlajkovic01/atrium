# Contributing

Atrium is in early development. The design is in [docs/](docs/); read
[DESIGN.md](docs/DESIGN.md) and [DECISIONS.md](docs/DECISIONS.md) before
proposing a change.

## Outside contributions are not accepted yet

Issues and discussion are welcome. Pull requests from outside the project
cannot be merged for now.

The reason is licensing. Atrium is AGPL-3.0, and the iOS and Apple TV apps
can be published in the App Store only while the project owner holds the
copyright to all client code (see [LICENSES.md](docs/LICENSES.md), flag 3).
Accepting code from others needs a contributor license agreement, which
does not exist yet. This file will be updated when it does.

## How changes are made

- `master` is always releasable. It changes only through pull requests
  that pass CI.
- Work happens on short-lived branches named after the phase and topic,
  for example `phase-1/auth`.
- Commits are small and their messages explain why.
- Tests come with the code. A failing test is fixed or the change is
  withdrawn; tests are never weakened, skipped or deleted to make
  progress.
- Every new dependency is recorded with its license in
  [docs/LICENSES.md](docs/LICENSES.md) before it is added.

## Running the tests

See the [README](README.md).

## Security

Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

# Intelligence

## What this repo is

Spy On Wheels, a top-down driving game, in development: source code, tests, the stages it plays, and the design
documents it is built against. `Stages/` holds the worlds, authored in the studio's own world editor; `docs/` will hold
the game brief and technical profile; `design/` the architecture guidelines and the system specifications the
implementation honors.

## What powers it

This game is developed by one architect using AI as execution bandwidth, governed by a private cognition layer: a
versioned body of accumulated judgment (studio principles, cross-game knowledge, and game-specific causal models) that
AI sessions load before touching this code. That layer, the world editor, and the process that produces games from
them are not part of this repository. The editor's shared modules arrive here only as versioned packages. What you see
here is their output.

## The working contract

AI sessions in this repository work constraint-first: `docs/` and `design/` are the binding specification, read
before implementing. Insights discovered during implementation flow back to the private layer through a deliberate
admission process; they are not filed here.

## Authorship

AI co-authorship is visible throughout this repository's history by design. The game is built by a human empowered by
AI; that story is told deliberately, not discovered.

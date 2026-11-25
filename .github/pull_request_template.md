<!-- Read CONTRIBUTING.md first. A pull request with an empty section is not reviewed. -->

## Agreed

<!-- Link the issue (Closes #123), or quote the Discord agreement with its date. A bug fix links its issue. -->

## What it does

<!-- What the player or developer sees change. Is it on by default? -->

## Proof

<!-- What you ran and what it showed. -->

- [ ] `./build.sh` and `./run-demo.sh` pass
- [ ] `nix develop .#win -c ./build-win.sh` passes
- [ ] Screenshots or a clip, Linux and Windows, for a change the player sees or hears
- [ ] `tools/live/run-live.sh` PASS, for a change only the game can show

## Performance

<!-- Before and after, same machine and area (`/alloy bench` for the renderer), with the hardware.
     Write "no impact" and why when the change cannot touch frame time, loading or memory. -->

## Understanding

- [ ] I understand every line of this change and can explain it on request

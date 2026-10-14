# Refilling the binary cache

Every CI job starts with an empty Nix store. The `ci` dev shell needs the OpenAL Soft that `flake.nix`
builds (`.#openal`), and CI pulls it from the binary cache named in the `cachix-action` step of
`.github/workflows/ci.yml`. When the cache doesn't have it, the job compiles OpenAL Soft from source,
which roughly doubles the job's time.

CI never pushes to the cache. A maintainer with write access to it pushes by hand, from their own
machine. Never give the token to a workflow, because a workflow that holds it would hand it to every
fork pull request.

## When to refill it

The cache misses when any of these change, because each one changes the OpenAL Soft output path:

- `flake.lock`
- `tools/deps.json` (the `openal-src` pin)
- `tools/build-openal.sh`
- the `openal` derivation in `flake.nix`

Refill it from the pull request's branch before you push that branch, and its CI runs from the cache.

## Check the cache

```sh
path=$(nix eval --raw .#openal.outPath)
hash=$(basename "$path" | cut -d- -f1)
curl -s -o /dev/null -w '%{http_code}\n' "<cache URL>/$hash.narinfo"
```

`<cache URL>` is `https://<cache>.cachix.org`, where `<cache>` is the `name` in that step. 200 means the cache has
the path. 404 means you need to push it.

## Push and pin

You need a Cachix token with write access to the cache. Load it into `CACHIX_AUTH_TOKEN` without
typing it on the command line, where it would land in your shell history:

```sh
read -rs CACHIX_AUTH_TOKEN && export CACHIX_AUTH_TOKEN
```

Then build, push and pin:

```sh
nix build .#openal --no-link
path=$(nix eval --raw .#openal.outPath)
nix shell --inputs-from . nixpkgs#cachix -c cachix push <cache> "$path"
nix shell --inputs-from . nixpkgs#cachix -c cachix pin <cache> cx-openal-soft "$path" --keep-revisions 1
```

Push only `.#openal`. Never push `.#winsdk`: it is Microsoft's SDK, and its licence forbids
redistributing it.

`<cache>` is the cache's name: the first part of the cache URL's host. The pin stops the cache's
garbage collection from evicting the path. Always pin under `cx-openal-soft`, so the new path replaces
the old one instead of piling up beside it.

Run the check again. It must print 200.

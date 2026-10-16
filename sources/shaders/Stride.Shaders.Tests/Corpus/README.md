# Shader corpus snapshot

`ShaderCorpusTests` compiles every known shader permutation with the current SDSL compiler and checks that the
output did not change. A compiler PR either shows "unchanged" or lists exactly which permutations changed, and how.

## Files

- `corpus.json`: the permutations, sorted by id. Each entry is the mixin tree given to the shader mixer (before the
  platform macros), its effect name, graphics profile, and where it was seen (a permutation seen in several sources
  is stored once). Written by `Stride.Shaders.CorpusCapture`. Over 512 KB, so it is listed in
  `.github/repo-file-guard/oversized-blob-allow.txt`; as text it compresses well, and git stores small changes as deltas.
- `snapshot.txt`: one line per permutation and platform (`Direct3D11/<id>`, `Vulkan/<id>`), with a hash of each part
  of its output:
  - `strict`: SPIR-V from the mixer, normalized (debug info and names removed, IDs renumbered by first use, types and
    constants named after their content),
  - `legal`: the same after `SpirvTools.LegalizeForHlsl` (inlining, dead code removal, SSA). When only `strict` changed,
    the code is the same and only its order moved,
  - `reflection`: constant buffers, resource bindings, input attributes,
  - `explain`: the mixer's linking decisions (`ShaderMixer.Explanation`): mixin order per node, members placed in
    each node, override chain of each method,
  - `error`: instead of the four above, for a permutation that does not compile.

RenderTests shaders (`sources/shaders/assets/SDSL/RenderTests`) are added as extra entries.

Every entry is compiled for Direct3D11 and for Vulkan: the shaders have `#if` on the graphics API, and the mixer binds
resources per register bank for Direct3D11 but with one unified scheme for Vulkan (also used by Direct3D12, Android,
iOS). Every run writes the readable files (`.spvasm` with names, `.legal.spvasm`, `.reflection.txt`, `.explain.txt`,
`.error.txt`) to `ShaderCorpus/<platform>/` next to the test assembly.

Locally, both platforms are checked. `STRIDE_SHADER_CORPUS_PLATFORMS=Vulkan` (comma-separated list) checks only some
of them, which is faster while iterating. CI checks one per lane: the Windows Simple lane Direct3D11, the Linux one
Vulkan.

## Tests

| Test | Runs | Does |
|---|---|---|
| `CompilerOutputUnchanged` | always (CI) | compares with `snapshot.txt` |
| `CorpusEntriesRoundTrip` | always | the permutation files read back with the same hashes |
| `SaveBaseline` | explicit | saves a full run to `ShaderCorpus.baseline/` next to the test assembly |
| `MatchesBaseline` | explicit | compares with that baseline and writes `ShaderCorpus/diff.txt` (git diff of every changed file, names-only changes included) |
| `UpdateSnapshot` | explicit | rewrites `snapshot.txt` |

Explicit tests run when selected in the IDE, or from the command line with `-- xUnit.Explicit=on`.

## Workflow for a compiler change

```
dotnet test sources/shaders/Stride.Shaders.Tests --filter "FullyQualifiedName~ShaderCorpusTests.SaveBaseline" -- xUnit.Explicit=on
# ... change the compiler, rebuild ...
dotnet test sources/shaders/Stride.Shaders.Tests --filter "FullyQualifiedName~ShaderCorpusTests.MatchesBaseline" -- xUnit.Explicit=on
```

Read `ShaderCorpus/report.txt` and `ShaderCorpus/diff.txt`. If the changes are expected, run `UpdateSnapshot` and
commit `snapshot.txt`; say in the PR which permutations changed and why.

Legalization keeps functions and the order of declarations, so `legal` also changes when a function only moved to another
mixin node, or when two copies of a function became one. Baseline runs (`SaveBaseline`, `MatchesBaseline`) therefore also
write `.optimized.spvasm`: the legalized code after `spirv-opt -O --unify-const --canonicalize-ids`. When `legal` changed,
the report says "same optimized code" if these match (declarations compared as a set), or "optimized code differs too".
They are not in the snapshot hashes.

`STRIDE_SHADER_CORPUS_FILTER=<text>` limits a run to the ids containing that text. `UpdateSnapshot` refuses to run
with it or with `STRIDE_SHADER_CORPUS_PLATFORMS`, so the snapshot always covers everything.

A permutation that failed in the snapshot and still fails is "stale" (old effect log data, e.g. a shader that gained a
generic parameter since): reported, not a failure. A permutation that compiled before and fails now is a failure.

## Refreshing the corpus

The corpus is rebuilt from empty, out of what the current code really compiles, by the **Refresh Shader Corpus**
workflow (`.github/workflows/refresh-shader-corpus.yml`, manual dispatch). It runs with capture on:

- the samples (`test-enduser.yml`, Windows Direct3D11 leg), the editor (`test-windows-editor.yml`, Direct3D11 leg) and
  the game test suites (`test-windows-game.yml`, Direct3D11 leg, all of `build/Stride.Tests.Game.Desktop.slnf`), each
  with its `capture-shader-corpus` input. `Stride.Engine.Tests` has `RenderFeatureTests` for the features no sample
  uses (MSAA, ambient occlusion, depth of field, local reflections, temporal anti-aliasing, cubemap backgrounds),

adds the two `Stride.Graphics` effect logs (hand-kept specs of the bytecode embedded in `SpriteBatch`, `UIBatch`,
`PrimitiveQuad` and the text renderer, which runtime never compiles), writes `corpus.json`, updates `snapshot.txt`, and
uploads both as the `shader-corpus-refresh` artifact (or opens a PR with `open-pr`). The run summary lists the
permutations per source. A refresh is expected after rendering or material changes; a compiler PR should not need one.

Capture: with `STRIDE_SHADER_CORPUS_CAPTURE=<folder>` set (and optionally `STRIDE_SHADER_CORPUS_TAG=<source name>`,
the test suite for a game test, the entry assembly name otherwise, e.g. the game or `Stride.GameStudio`), every
effect compile writes its mixin tree there (`ShaderCorpusCapture`). The replay of an effect log by an asset build is
not recorded: effect logs are only a source when given to the tool. Captures are taken on one platform: the tree is recorded before the platform macros, and
the test compiles it for every platform.

The tool builds `corpus.json` from empty out of the sources it is given, for a local run:

```
dotnet run --project sources/tools/Stride.Shaders.CorpusCapture -- --logs sources/engine/Stride.Graphics --capture <folder> [--capture <folder>...]
```

`--logs` takes effect log files or folders, `--capture` capture folders (searched recursively). Then run
`UpdateSnapshot`.

The other effect logs (samples, templates, editor package) are not a source: they record what games compiled at some
point, with the parameter values of that time, not what the current code builds. Features no capture run reaches are
covered by adding a scenario (a fixture or an engine test), not a log.

## External shader libraries

`ExternalShaderTests` (explicit) compiles the shaders of libraries outside this repository from local clones, to check a
compiler change against real user code. Nothing of those libraries is committed: the tests read the clones from a local path.
First written for vvvv's libraries (VL.Fuse, VL.StandardLibs, VL.Addons), which are the default set.

```
set STRIDE_EXTERNAL_SHADERS=C:\path\to\clones
set STRIDE_EXTERNAL_SHADERS_OUT=C:\path\to\output
dotnet test sources/shaders/Stride.Shaders.Tests --filter "FullyQualifiedName~ExternalShaderTests" -- xUnit.Explicit=on
```

- `CompileExternalShaders`: each class compiled alone, then mixed alone at the root through the Direct3D 11 chain (validate,
  legalize, SPIRV-Cross, fxc). Writes `results.tsv` (one line per step: result, error codes, warning codes, messages).
- `CompileExternalTrees`: mixin trees shaped like the ones VL.Stride builds from C# (generic values as strings, compositions,
  compose arrays). Writes `trees.tsv` and each tree's reflection, to compare two branches.
- `STRIDE_EXTERNAL_SHADERS_SET=Name=relative/path;...` replaces the libraries; `STRIDE_EXTERNAL_SHADERS_UPGRADE=1` runs the shader
  source upgrade on them first (in memory; changed files and notes go to the output folder); `STRIDE_EXTERNAL_SHADERS_ONLY` /
  `_SKIP` take class names; `STRIDE_EXTERNAL_SHADERS_DUMP=1` writes the mixed SPIR-V.
- `progress.txt` gets each step before it runs: if a native crash ends the test process, its last line names the shader.

Clones are untrusted data: read them, don't build them or run their scripts. Check a library's license before copying any of its
code into a test; write minimal repros instead.

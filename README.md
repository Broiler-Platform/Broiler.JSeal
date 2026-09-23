# Broiler.JSeal

**JSEAL** — the JavaScript Engine Abstraction Layer for the Broiler platform, targeting .NET 10.

JSEAL provides engine-neutral contracts that HTML/DOM hosts and browsers bind against. An engine
**provider** implements those contracts over a specific JavaScript engine; which provider serves a
document or execution realm is a runtime registration rather than a compile-time dependency.

## Projects

| Project | Description |
|---|---|
| `Broiler.JSeal` | **The contracts.** Zero `ProjectReference` and zero `PackageReference`. Defines `IJsRealm`, `JsValue`, `JsCall`, `JsCapabilities`, and `JsEngineRegistry`. |
| `Broiler.JSeal.BroilerJs` | The JSEAL provider over [Broiler.JS](https://github.com/Broiler-Platform/Broiler.JS) (`Broiler.JavaScript`). |
| `Broiler.JSeal.Vm` | The JSEAL provider over the [Broiler.VM](https://github.com/Broiler-Platform/Broiler.VM) JavaScript profile's in-realm host surface. |
| `Broiler.JSeal.Tests` | The conformance and registry test suite. Data-driven theories verifying provider behavior across all registered engines. |

## Layering

```
                    ┌─────────────────────────────────────────┐
                    │  Host (e.g. Broiler.HtmlBridge / Core)  │
                    └───────────────────┬─────────────────────┘
                                        │  binds against
                    ┌───────────────────▼─────────────────────┐
                    │  Broiler.JSeal                 ◄─ JSEAL │   ZERO ProjectReferences
                    │    JsValue, JsCall, IJsRealm            │   ZERO PackageReferences
                    │    JsCapabilities, JsEngineRegistry     │
                    └───────────────────┬─────────────────────┘
                                        │  implemented by
              ┌─────────────────────────┴─────────────────────────┐
              │                                                   │
  ┌───────────▼──────────────┐                    ┌───────────────▼──────────────┐
  │ Broiler.JSeal.BroilerJs  │                    │ Broiler.JSeal.Vm             │
  │   over Broiler.JS        │                    │   over Broiler.VM profile    │
  │   broiler-js             │                    │   broiler-vm                 │
  └──────────────────────────┘                    └──────────────────────────────┘
```

**`Broiler.JSeal` declares no `ProjectReference` or `PackageReference`.** With those dependencies,
the compiler cannot resolve either engine's types in the contracts project. There is no dedicated
CI check preventing a future reference from being added; the current boundary is described in the
[architecture guide](docs/jseal.md#projects-and-dependency-boundaries).

## The Value Model

`JsValue` is a `readonly struct` (24 bytes on measured x64 builds) consisting of a kind tag (`JsValueKind`), a `double` for
numeric representation, and an `object?` reference for engine-managed objects or strings.

- **Missing vs Undefined**: `JsValue.Missing` has kind 0, allowing hosts to distinguish absent
  arguments from explicit `undefined`.
- **Reference Equality**: Handle equality (`==`, `===`) maps directly to engine identity for objects
  via `JsValue.ObjectIdentity`.
- **String Coercion Safety**: `JsValue.ToString()` renders a debug tag (e.g. `[object]`) and never
  enters the JavaScript engine or executes guest code. Guest conversions go through
  `IJsValues.ToJsString`.

## Continuous Integration and Publishing

Pushes to main and pull requests build all libraries on Linux and Windows under both `Release` and
`Release-VM` configurations, run the conformance test suite against all registered providers, and
run isolated package consumers. The Windows job also packs and verifies the NuGet packages.

Publishing supports GitHub Packages and NuGet.org with automated preview versioning.

## Build and Test

```bash
# Build all libraries; Release tests reference the JS provider
dotnet build Broiler.JSeal.slnx -c Release

# Run tests under Release
dotnet test Broiler.JSeal.slnx -c Release

# Build all libraries; Release-VM tests reference both providers
dotnet build Broiler.JSeal.slnx -c Release-VM

# Run tests under Release-VM (exercises both engines in the conformance suite)
dotnet test Broiler.JSeal.slnx -c Release-VM

# Pack and test each provider as a standalone NuGet consumer (PowerShell 7+)
pwsh -NoProfile -File eng/test-package-consumer.ps1
```

The [package-consumer check](eng/package-consumer/README.md) restores from an isolated local feed
into fresh caches and runs outside the source tree, for each provider alone and both together, and
gates engine package versions (Node.js 24 required). CI includes it on Windows and Linux. With
`-CandidateFeed <dir> -CandidateVersion Broiler.VM=<version>` it evaluates a candidate upstream set
without changing the pins or publishing anything.

## Documentation

- [JSEAL Specification](docs/jseal.md) — Detailed architecture, realm contracts, capabilities, and provider guide
- [Roadmap](docs/roadmap.md) — Release milestones and status
- [Human Review Record](HUMAN_REVIEW.md) — Review scope; human approval remains pending

Validate local documentation links and project examples with `python diagnostics/J14/check_docs.py`.
This optional check uses Python 3.9+ and ripgrep, falling back to `git ls-files` without it; it does not run behavioral tests or fetch URLs.

## License

Broiler.JSeal is licensed under the [Apache License 2.0](LICENSE).

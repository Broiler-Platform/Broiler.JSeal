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
  │   reference engine       │                    │   conditional / -VM builds   │
  └──────────────────────────┘                    └──────────────────────────────┘
```

**`Broiler.JSeal` has no `ProjectReference` and no `PackageReference`.** Engine neutrality is asserted
by the compiler: an assembly that references nothing cannot name a `Broiler.JavaScript` type, a
`Broiler.VM` type, or anything either drags in.

## The Value Model

`JsValue` is a 24-byte `readonly struct` consisting of a kind tag (`JsValueKind`), a `double` for
numeric representation, and an `object?` reference for engine-managed objects or strings.

- **Missing vs Undefined**: `JsValue.Missing` has kind 0, allowing hosts to distinguish absent
  arguments from explicit `undefined`.
- **Reference Equality**: Handle equality (`==`, `===`) maps directly to engine identity for objects
  via `JsValue.ObjectIdentity`.
- **String Coercion Safety**: `JsValue.ToString()` renders a debug tag (e.g. `[object]`) and never
  enters the JavaScript engine or executes guest code. Guest conversions go through
  `IJsValues.ToJsString`.

## Continuous Integration and Publishing

Every push and pull request builds all libraries on Linux and Windows under both `Release` and
`Release-VM` configurations, runs the conformance test suite against all registered providers, and
packs NuGet packages to ensure packaging validity.

Publishing supports GitHub Packages and NuGet.org with automated preview versioning.

## Build and Test

```bash
# Build Release (Broiler.JS provider + contracts)
dotnet build Broiler.JSeal.slnx -c Release

# Run tests under Release
dotnet test Broiler.JSeal.slnx -c Release

# Build Release-VM (includes Broiler.VM provider)
dotnet build Broiler.JSeal.slnx -c Release-VM

# Run tests under Release-VM (exercises both engines in the conformance suite)
dotnet test Broiler.JSeal.slnx -c Release-VM
```

## Documentation

- [JSEAL Specification](docs/jseal.md) — Detailed architecture, realm contracts, capabilities, and provider guide
- [Roadmap](docs/roadmap.md) — Release milestones and status
- [Human Review Record](HUMAN_REVIEW.md) — Preview review scope and security attestation

## License

Broiler.JSeal is licensed under the [Apache License 2.0](LICENSE).

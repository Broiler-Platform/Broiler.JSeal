# Broiler.JSeal

**JSEAL** — the JavaScript Engine Abstraction Layer of the Broiler platform. It gives HTML/DOM hosts
engine-neutral contracts to bind against: an engine **provider** implements them over a specific
JavaScript engine, and which provider serves a realm is chosen at run time rather than at compile
time.

> **Preview.** These packages are an early preview. The public API can change between previews,
> and no human review has taken place yet. Neither engine is a standalone sandbox: configure the
> realm's capabilities and bounds before running untrusted script.

## Packages

All packages are versioned in lockstep and target `net10.0`.

| Package | What it is |
|---|---|
| `Broiler.JSeal` | The contracts: `IJsRealm`, `JsValue`, `JsCall`, `JsCapabilities` and `JsEngineRegistry`. It references no engine and no other package. |
| `Broiler.JSeal.BroilerJs` | The provider over [Broiler.JS](https://github.com/Broiler-Platform/Broiler.JS) (`Broiler.JavaScript.*`), registered as `broiler-js`. |
| `Broiler.JSeal.Vm` | The provider over the [Broiler.VM](https://github.com/Broiler-Platform/Broiler.VM) JavaScript profile, registered as `broiler-vm`. |

A host references `Broiler.JSeal` for the contracts, plus the provider or providers it wants to
offer. Both providers can be loaded in one process.

## Getting started

```shell
dotnet add package Broiler.JSeal.BroilerJs --prerelease
```

```csharp
using Broiler.JSeal;
using Broiler.JSeal.BroilerJs;

BroilerJsEngineProvider.Register(); // or VmEngineProvider.Register() from Broiler.JSeal.Vm

using var realm = JsEngineRegistry.Find("broiler-js")!.CreateRealm(JsRealmOptions.Default);
var answer = realm.EvaluateHostScript("6 * 7", "example:answer");
Console.WriteLine(answer.AsNumber); // 42
```

What a realm can do is declared by `realm.Capabilities`; a host checks a capability before relying
on the member it guards.

## Links

- [Source and documentation](https://github.com/Broiler-Platform/Broiler.JSeal)
- [JSEAL specification](https://github.com/Broiler-Platform/Broiler.JSeal/blob/main/docs/jseal.md): realm contracts, capabilities and the provider guide
- [Roadmap](https://github.com/Broiler-Platform/Broiler.JSeal/blob/main/docs/roadmap.md)
- [Issues](https://github.com/Broiler-Platform/Broiler.JSeal/issues)

Licensed under Apache-2.0.

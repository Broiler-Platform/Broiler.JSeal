# Human review summary: Broiler.JSeal preview

> **Status: PENDING.** No human reviewer has attested to the code in this repository.
> Until a reviewer is named below with a reviewed commit, evidence and a decision, this
> component must not be described as human-approved.

This component was split out from
[Broiler.HtmlBridge](https://github.com/Broiler-Platform/Broiler.HtmlBridge) on 2026-09-19.
The extraction established JSEAL as an independent, standalone component in the Broiler
platform ecosystem.

## Review Scope

| Review | Scope | Status |
|---|---|---|
| [ ] | `Broiler.JSeal` — engine-neutral contracts and value model | **PENDING** |
| [ ] | `Broiler.JSeal.BroilerJs` — Broiler.JS provider | **PENDING** |
| [ ] | `Broiler.JSeal.Vm` — Broiler.VM provider | **PENDING** |
| [ ] | `Broiler.JSeal.Tests` — conformance and registry test suite | **PENDING** |

Reviewer: _not yet assigned_  
Reviewed commit: _none_  
Evidence: _none_  
Decision: **PENDING**  

## Architectural Guarantees to Verify

1. **Strict Neutrality Invariant**: `Broiler.JSeal.csproj` must contain zero `ProjectReference`
   and zero `PackageReference`.
2. **Handle Safety**: `JsValue` struct identity comparisons (`CA2013`, `CS8073`) are promoted to
   errors to prevent silent fall-through on absent values.
3. **Engine Sandboxing**: Neither underlying engine is a standalone sandbox; host applications
   must configure appropriate capability tables and bounds before executing untrusted script.

## Dependencies

| Component | How it is reached | Status |
|---|---|---|
| Broiler.JavaScript.* | NuGet packages (Broiler.JSeal.BroilerJs) | Preview packages |
| Broiler.VM.* | NuGet packages (Broiler.JSeal.Vm) | Preview packages |

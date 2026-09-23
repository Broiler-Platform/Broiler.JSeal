# J15/J16 nullable diagnostics and public API comparison

`Directory.Build.props` used to suppress the nullable family (CS8600, CS8601, CS8602, CS8603,
CS8604, CS8618, CS8619, CS8620, CS8622, CS8625, CS8631, CS8632, CS8765, CS8767) for every
project. That suppression is gone. The contracts inherit no suppression at all and promote
`nullable` warnings to errors; providers and tests report them as ordinary warnings.

## What surfaced

With the suppressions lifted, each project was built with `-p:NoWarn=` in `Release-VM`:

| Project | Nullable diagnostics | Classification and resolution |
| --- | ---: | --- |
| `Broiler.JSeal` | 0 | None. `IJsRealmAdoption.TryAdopt` gained `[NotNullWhen(true)]`, which its documentation already promised. |
| `Broiler.JSeal.BroilerJs` | 1 (CS8767) | Appeared once the contract gained the attribute; the implementation now carries it too. |
| `Broiler.JSeal.Vm` | 2 (CS8604) | Engine annotation gap: `JsHostValue.AsString()` is null exactly when `Kind` is not String, which the package does not express. A pattern match replaces the separate `Kind` test; no suppression. |
| `Broiler.JSeal.Tests` | 1 (CS8603) | A probe returned `AsString` directly; it now reports a non-string outcome by kind. |

No observable null behavior changed. Two assertions pin existing behavior: an unsupported
adoption answers false with a null realm, and a false `TryGetArrayBufferBytes` returns null.
The latter corrects the parameter documentation, which had described an empty array.

The `J00` and `J12` diagnostic projects also build with zero warnings. Only CA2255, for the
providers' registration module initializers, still fires among the remaining inherited
suppressions. CS9113, CA1416 and SYSLIB0013 matched nothing and are left for a separate change.

## Public API comparison

[`PublicApi`](PublicApi/Program.cs) loads an assembly by path and prints each public or protected
member on one line. The output includes `?` annotations and `System.Diagnostics.CodeAnalysis` flow
attributes. The retained [before](api-before.txt) and [after](api-after.txt) dumps are from Release
builds of `Broiler.JSeal` at the start and end of this change. They were generated on .NET 10.0.12.

```powershell
dotnet run -c Release --project diagnostics/J15/PublicApi -- <path>/Broiler.JSeal.dll <output>.txt
```

The contracts' only difference is:

```diff
-    public TryAdopt(System.Object engineRealm, Broiler.JSeal.JsRealmOptions options, out Broiler.JSeal.IJsRealm? realm) : System.Boolean
+    public TryAdopt(System.Object engineRealm, Broiler.JSeal.JsRealmOptions options, [NotNullWhen(True)] out Broiler.JSeal.IJsRealm? realm) : System.Boolean
```

The same line changed on `BroilerJsEngineProvider`. The public surface of `Broiler.JSeal.Vm` is
identical. The attribute is source- and binary-compatible. Callers may drop a `!` after a true
answer, and an external implementer that omits it now receives CS8767.

## B06: the one contract addition since

The same tool compared a Release build of `Broiler.JSeal` from the merged tree before B06's JSeal
half with one after it; the dumps are retained as [before](api-before-b06.txt) and
[after](api-after-b06.txt). The contracts' only difference is:

```diff
+    public IsStrictlyEqual(Broiler.JSeal.JsValue left, Broiler.JSeal.JsValue right) : System.Boolean
```

on `IJsValues`. It is a default interface member: existing callers and out-of-tree providers compile
unchanged, and a provider that does not implement it gets a body that answers `==` wherever `==` is
`===` and refuses two distinct BigInt handles with `NotSupportedException`. Both registered providers
implement it. No member of `JsValue` changed; its BigInt equality stays by reference, now documented
as the contract ([the guide](../../docs/jseal.md)).

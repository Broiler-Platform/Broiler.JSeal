using Broiler.JSeal;

namespace Broiler.JSeal.Tests;

/// <summary>
/// Source identity: label selection, attribution of escaped throws, syntax-error lines under
/// forced strictness, and the provider-specific stack details that are not guaranteed.
/// </summary>
public partial class JsealConformanceTests
{
    private delegate JsValue SourceMember(IJsRealm realm, string source, string label);

    private static readonly (string Name, SourceMember Evaluate)[] SourceMembers =
    [
        ("host", (realm, source, label) => realm.EvaluateHostScript(source, label)),
        ("classic", (realm, source, label) => realm.EvaluateClassicScript(source, label)),
        ("dynamic", (realm, source, label) => realm.EvaluateDynamicSource(source, label)),
    ];

    [Fact]
    public void SourceLabelSelectionPrefersTheLabelThenTheDocumentUrl()
    {
        var document = new JsRealmOptions { DocumentUrl = "https://example.test/page.html" };

        Assert.Equal("app.js", document.SourceLabelFor("app.js"));
        Assert.Equal(" app.js ", document.SourceLabelFor(" app.js "));
        Assert.Equal("https://example.test/page.html", document.SourceLabelFor(null));
        Assert.Equal("https://example.test/page.html", document.SourceLabelFor(""));
        Assert.Equal("https://example.test/page.html", document.SourceLabelFor(" \t"));
        Assert.Equal(JsRealmOptions.AnonymousSourceLabel, JsRealmOptions.Default.SourceLabelFor(" "));
        Assert.Equal(
            JsRealmOptions.AnonymousSourceLabel,
            new JsRealmOptions { DocumentUrl = " " }.SourceLabelFor(null));
    }

    /// <summary>
    /// The guaranteed metadata of a syntax error: the selected identity and the line in the text the
    /// host supplied, on every source member, with and without forced strictness.
    /// </summary>
    /// <remarks>
    /// Broiler.JS forces strictness with a directive on the first source line, so lines survive and
    /// only its first-line parser columns shift; it therefore reports no column. Broiler.VM compiles
    /// with a strictness flag and reports its front end's one-based UTF-16 column.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void ASyntaxErrorIsAttributedToItsEvaluationAndLine(string engine)
    {
        foreach (var strict in new[] { false, true })
        {
            using var realm = NewRealm(engine, new JsRealmOptions { ForceStrictMode = strict });

            foreach (var (member, evaluate) in SourceMembers)
            foreach (var (source, line, column) in new[] { ("1;\n\n  var x = ;", 3, 11), ("var x = ;", 1, 9) })
            {
                var label = $"test:syntax-{member}-{line}";
                var failure = Assert.Throws<JsEngineException>(() => evaluate(realm, source, label));
                var observed = (strict, member, failure.SourceLabel, failure.SourceLine, failure.SourceColumn);

                Assert.Equal(
                    (strict, member, label, (int?)line, engine == "broiler-vm" ? column : (int?)null),
                    observed);
                Assert.Equal("SyntaxError", realm.GetProperty(failure.Thrown, "name").AsString);
            }
        }
    }

    /// <summary>
    /// A reported line is the ECMAScript line of the failure in the supplied text, or absent; never
    /// a plausible wrong one.
    /// </summary>
    /// <remarks>
    /// ECMAScript counts CR, CRLF, LF, U+2028 and U+2029 as line terminators, including the one a line
    /// continuation ends with. The pinned Broiler.JS lexer counts only LF and CRLF, and leaves its
    /// compile frame at the start of an unterminated token, so it must report nothing for a lone CR,
    /// U+2028, U+2029 or an unterminated string or template. The pinned Broiler.VM front end loses the
    /// line of a continuation inside a template literal, so it must report nothing for that. A guest
    /// <c>Function</c> body compiles under the location <c>internal</c>, so a host label of that name
    /// must not adopt the body's line.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void ASyntaxErrorLineIsTheSpecificationLineOrAbsent(string engine)
    {
        (string Source, int Line, bool JsCountsIt, bool VmCountsIt)[] cases =
        [
            ("1;\n\n'abc", 3, false, true),
            ("1;\n\n `abc", 3, false, true),
            ("1;\r\r var x = ;", 3, false, true),
            ("1;\u2028\u2028 var x = ;", 3, false, true),
            ("1;\u2029\u2029 var x = ;", 3, false, true),
            ("1;\n/*\u2028*/\n var x = ;", 4, false, true),
            ("`a\rb`;\n var x = ;", 3, false, true),
            ("`a\\\nb`;\n var x = ;", 3, true, false),
            ("String.raw`a\\\nb`;\n var x = ;", 3, true, false),
            ("'a\\\n\\\nb';\n var x = ;", 4, true, true),
            ("1;\r\n\r\n var x = ;", 3, true, true),
            ("1;\n\n let a; let a;", 3, true, true),
        ];

        foreach (var strict in new[] { false, true })
        {
            using var realm = NewRealm(engine, new JsRealmOptions { ForceStrictMode = strict });

            foreach (var (source, line, jsCountsIt, vmCountsIt) in cases)
            {
                var failure = Assert.Throws<JsEngineException>(() => realm.EvaluateHostScript(source, "test:lines"));
                var counted = engine == "broiler-vm" ? vmCountsIt : jsCountsIt;
                var expected = counted ? line : (int?)null;

                Assert.Equal((strict, source, expected), (strict, source, failure.SourceLine));
                Assert.Equal((strict, source, engine == "broiler-vm" && counted),
                    (strict, source, failure.SourceColumn is not null));
                Assert.Equal("SyntaxError", realm.GetProperty(failure.Thrown, "name").AsString);
            }

            var body = Assert.Throws<JsEngineException>(
                () => realm.EvaluateHostScript("1;\nnew Function('a', '\\n\\n\\n var x = ;');", "internal"));
            Assert.Equal((strict, "internal", (int?)null), (strict, body.SourceLabel, body.SourceLine));
            Assert.Equal("SyntaxError", realm.GetProperty(body.Thrown, "name").AsString);
        }
    }

    /// <summary>
    /// A run-time throw is attributed to the evaluation it escaped from, with no invented line.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void AThrownErrorIsAttributedToTheEvaluationItEscaped(string engine)
    {
        using var realm = NewRealm(engine);
        realm.EvaluateClassicScript("function fail() { throw new TypeError('boom'); }", "test:defines-fail");

        foreach (var (member, evaluate) in SourceMembers)
        {
            var label = $"test:calls-fail-{member}";
            var failure = Assert.Throws<JsEngineException>(() => evaluate(realm, "1;\nfail();", label));

            Assert.Equal((member, label, (int?)null, (int?)null),
                (member, failure.SourceLabel, failure.SourceLine, failure.SourceColumn));
            Assert.Equal("TypeError", realm.GetProperty(failure.Thrown, "name").AsString);

            var primitive = Assert.Throws<JsEngineException>(() => evaluate(realm, "throw 7", label + "-7"));
            Assert.Equal(label + "-7", primitive.SourceLabel);
            Assert.True(primitive.Thrown == JsValue.Number(7d));
            Assert.Null(primitive.SourceLine);
        }

        // A syntax error in text the page evaluated is not a position in the host's text.
        var nested = Assert.Throws<JsEngineException>(
            () => realm.EvaluateHostScript("1;\n(0, eval)('1;\\n\\n  var x = ;');", "test:nested-eval"));
        Assert.Equal("test:nested-eval", nested.SourceLabel);
        Assert.Equal("SyntaxError", realm.GetProperty(nested.Thrown, "name").AsString);
        Assert.Null(nested.SourceLine);
        Assert.Null(nested.SourceColumn);

        // Failures that are not guest throws keep their own category and carry no identity.
        using var restricted = NewRealm(engine, new JsRealmOptions { AllowGuestEval = false });
        Assert.Throws<JsCapabilityUnavailableException>(
            () => restricted.EvaluateDynamicSource("1", "test:refused-before-evaluation"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void BlankLabelsFallBackToTheDocumentUrlThenAnonymous(string engine)
    {
        const string document = "https://example.test/page.html";

        using var withDocument = NewRealm(engine, new JsRealmOptions { DocumentUrl = document });
        using var withoutDocument = NewRealm(engine);

        foreach (var (member, evaluate) in SourceMembers)
        foreach (var blank in new[] { null!, "", "  " })
        {
            Assert.Equal((member, document), (member,
                Assert.Throws<JsEngineException>(() => evaluate(withDocument, "throw 1", blank)).SourceLabel));
            Assert.Equal((member, JsRealmOptions.AnonymousSourceLabel), (member,
                Assert.Throws<JsEngineException>(() => evaluate(withoutDocument, "var x = ;", blank)).SourceLabel));

            // An explicit label wins over the document URL.
            Assert.Equal((member, "test:explicit"), (member,
                Assert.Throws<JsEngineException>(() => evaluate(withDocument, "throw 1", "test:explicit")).SourceLabel));
        }
    }

    /// <summary>
    /// Neither a label nor a document URL is an authorization token: the member decides.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void LabelsAndDocumentUrlsDoNotGrantEvaluationPermissionOrStrictness(string engine)
    {
        const string document = "https://example.test/page.html";
        string[] labels = ["host", "test:host", "test:strict-host", "eval", "", document];

        using var restricted = NewRealm(engine, new JsRealmOptions { AllowGuestEval = false, DocumentUrl = document });
        Assert.False(restricted.Capabilities.HasFlag(JsCapabilities.GuestEval));
        InstallRefusedPage(restricted);

        foreach (var label in labels)
        {
            Assert.Throws<JsCapabilityUnavailableException>(() => restricted.EvaluateDynamicSource("42", label));
            Assert.Equal("SyntaxError", restricted.EvaluateHostScript("page()", label).AsString);
            Assert.Equal("SyntaxError", restricted.EvaluateClassicScript("page()", label).AsString);
        }

        const string plainCall = "(function () { return typeof this; })()";
        using var forced = NewRealm(engine, new JsRealmOptions { ForceStrictMode = true, DocumentUrl = document });

        foreach (var label in labels)
        {
            Assert.Equal((label, "undefined"), (label, forced.EvaluateHostScript(plainCall, label).AsString));
            Assert.Equal((label, "object"), (label, forced.EvaluateClassicScript(plainCall, label).AsString));
            Assert.Equal((label, "object"), (label, forced.EvaluateDynamicSource(plainCall, label).AsString));
        }
    }

    /// <summary>
    /// Stack text is provider-specific and outside the guarantee; this pins what each provider
    /// actually supplies so a change on either side is noticed rather than assumed.
    /// </summary>
    /// <remarks>
    /// Broiler.JS renders guest frames as <c>label:line,column</c> in an error's <c>stack</c>; lines
    /// survive forced strictness. Broiler.VM has no <c>stack</c> property and no script trace.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void StackTextIsProviderSpecificAndNotGuaranteed(string engine)
    {
        const string source = "1;\n\n  throw new Error('boom');";

        foreach (var strict in new[] { false, true })
        {
            using var realm = NewRealm(engine, new JsRealmOptions { ForceStrictMode = strict });
            var failure = Assert.Throws<JsEngineException>(() => realm.EvaluateHostScript(source, "test:stack"));
            var stack = realm.GetProperty(failure.Thrown, "stack");

            Assert.Equal("test:stack", failure.SourceLabel);

            if (engine == "broiler-vm")
            {
                Assert.True(stack.IsUndefined);
                Assert.Null(failure.ScriptStackTrace);
                continue;
            }

            Assert.Contains("test:stack:3,", stack.AsString);
            Assert.NotNull(failure.ScriptStackTrace);
        }
    }

    /// <summary>
    /// A direct <c>eval</c> in a function reads and writes the function's own bindings, or is
    /// refused explicitly; it is never answered from the wrong scope or as a misleading
    /// <c>SyntaxError</c>, and a realm without guest evaluation refuses it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The request a direct eval sends is not source text.</b> An engine that evaluates it
    /// against the caller's scope has to tell the provider which scope and which strictness, and a
    /// provider that decoded that request as a script would answer a <c>SyntaxError</c> for text the
    /// guest never wrote. This is the regression for that route.
    /// </para>
    /// <para>
    /// The pinned Broiler.VM package has no caller-scope evaluation: its executor refuses a direct
    /// eval in a function with an <c>EvalError</c> before any request is sent. That is a recorded
    /// gap (VM V14), asserted here as the refusal it is, so a change on either side is noticed.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.GuestEval | JsCapabilities.HostScriptSource)]
    public void ADirectEvalInAFunctionUsesTheFunctionsScopeOrIsRefused(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.GuestEval | JsCapabilities.HostScriptSource);

        const string reads = "(function () { var x = 7; return eval('x'); })()";
        const string writes = "(function () { var y = 1; eval('y = y + 41'); return (function () { return y; })(); })()";
        const string strict = "(function () { 'use strict'; eval('var leaked = 1'); return typeof leaked; })()";

        if (engine == "broiler-vm")
        {
            foreach (var source in new[] { reads, writes, strict })
            {
                var refused = Assert.Throws<JsEngineException>(() => realm.EvaluateHostScript(source, "test:direct-eval"));
                Assert.Equal((source, "EvalError"), (source, realm.GetProperty(refused.Thrown, "name").AsString));
            }
        }
        else
        {
            Assert.True(realm.EvaluateHostScript(reads, "test:direct-eval-reads") == JsValue.Number(7d));
            Assert.True(realm.EvaluateHostScript(writes, "test:direct-eval-writes") == JsValue.Number(42d));
            Assert.Equal("undefined", realm.EvaluateHostScript(strict, "test:direct-eval-strict").AsString);
        }

        // The eval'd text's own syntax error is still a SyntaxError of the eval'd text.
        var broken = Assert.Throws<JsEngineException>(
            () => realm.EvaluateHostScript("(function () { var z = 1; return eval('var = z;'); })()", "test:direct-eval-broken"));
        Assert.Equal(
            engine == "broiler-vm" ? "EvalError" : "SyntaxError",
            realm.GetProperty(broken.Thrown, "name").AsString);

        // Without GuestEval a page's direct eval is refused rather than evaluated, also when host
        // script calls the page's function.
        using var restricted = NewRealm(engine, new JsRealmOptions { AllowGuestEval = false });
        restricted.EvaluateClassicScript(
            "function direct() { var x = 7; try { return String(eval('x')); } catch (e) { return e.name; } }",
            "test:direct-eval-page");
        Assert.Contains(restricted.EvaluateHostScript("direct()", "test:direct-eval-denied").AsString, new[] { "EvalError", "SyntaxError" });
        Assert.Contains(
            restricted.Invoke(restricted.GetProperty(restricted.Global, "direct"), JsValue.Undefined).AsString,
            new[] { "EvalError", "SyntaxError" });
    }
}

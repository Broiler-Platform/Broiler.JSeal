using System.Numerics;
using System.Reflection;

using Broiler.JSeal;
using Broiler.JSeal.Providers;

namespace Broiler.JSeal.Tests;

/// <summary>
/// Capability coverage: a witness for every flag a provider declares, an explicit refusal for every
/// flag it does not, and the gaps no contract can reach yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provider ability and realm permission are different questions, and this file keeps them
/// apart.</b> <see cref="IJsEngineProvider.Capabilities"/> is what the engine can do; a realm's
/// <see cref="IJsRealm.Capabilities"/> is that ability narrowed, never widened. The only narrowing a
/// host can ask for is <see cref="JsRealmOptions.AllowGuestEval"/>; a provider may also withhold a
/// flag from a realm that turns out to lack what the flag needs (the VM provider drops BinaryData
/// and the three source flags when its bridge finds no binary intrinsics or no <c>eval</c>), which
/// <see cref="ADefaultRealmHasEveryCapabilityItsProviderDeclares"/> turns into a failure rather than
/// a quiet narrowing. Theory rows are chosen by provider ability. A witness then asserts that a realm
/// built with the permissions it asked for really has the capability, rather than returning early
/// when it does not, so a declaration that a realm cannot honour fails instead of passing vacuously.
/// </para>
/// <para>
/// <b>An undeclared capability is a separate row, not a skipped witness, and there are three kinds.</b>
/// <see cref="AnUndeclaredCapabilityIsAnExplicitRefusalNotAPass"/> runs a specified refusal of a
/// gated contract member; <see cref="AnUndeclaredCapabilityWithNoGatedMemberIsOnlyAbsent"/> asserts
/// absence where the contract specifies no refusal; <see cref="AnUndeclaredCapabilityWithNoContractIsARecordedGap"/>
/// names a capability no contract can reach yet. Each runs once per provider and capability the
/// provider does not declare, so a test report names every one by its kind. The witnesses never run
/// for such a provider, so none of these rows can be counted as a passing implementation.
/// </para>
/// </remarks>
public partial class JsealConformanceTests
{
    /// <summary>
    /// Every registered provider that declares all of <paramref name="required"/>, by name.
    /// </summary>
    /// <remarks>
    /// The theory data for a capability witness. A provider without the capability gets no row here;
    /// it gets a row in <see cref="UndeclaredCapabilities"/> instead.
    /// </remarks>
    public static IEnumerable<object[]> EnginesDeclaring(JsCapabilities required) =>
        JsEngineRegistry.All
            .Where(provider => (provider.Capabilities & required) == required)
            .Select(provider => new object[] { provider.Name });

    /// <summary>
    /// Every (provider, single capability) pair the provider does not declare and whose absence a
    /// contract member refuses, as specified in <see cref="CapabilityRefusals"/>.
    /// </summary>
    public static IEnumerable<object[]> UndeclaredCapabilities => Undeclared(CapabilityRefusals.ContainsKey);

    /// <summary>
    /// Every undeclared pair whose absence no contract member refuses: see <see cref="AbsenceOnly"/>.
    /// </summary>
    public static IEnumerable<object[]> UndeclaredCapabilitiesWithNoGatedMember => Undeclared(AbsenceOnly.ContainsKey);

    /// <summary>Every undeclared pair that is a recorded gap: see <see cref="CoverageGaps"/>.</summary>
    public static IEnumerable<object[]> UndeclaredGaps => Undeclared(CoverageGaps.ContainsKey);

    /// <summary>
    /// The engine name of the single row a kind of undeclared row has when no registered provider
    /// produces one: xUnit fails a theory with no data, and a report should say "none" rather than
    /// drop the theory.
    /// </summary>
    private const string NoProviderLacksOne = "(no registered provider lacks one)";

    private static IEnumerable<object[]> Undeclared(Func<JsCapabilities, bool> kind)
    {
        var rows = UndeclaredPairs(kind).Select(pair => new object[] { pair.Engine, pair.Capability }).ToArray();
        return rows.Length > 0 ? rows : [[NoProviderLacksOne, JsCapabilities.None]];
    }

    private static IEnumerable<(string Engine, JsCapabilities Capability)> UndeclaredPairs(Func<JsCapabilities, bool> kind) =>
        JsEngineRegistry.All.SelectMany(provider => SingleCapabilities()
            .Where(capability => !provider.Capabilities.HasFlag(capability) && kind(capability))
            .Select(capability => (provider.Name, capability)));

    /// <summary>
    /// True for the placeholder row, after asserting that it is one because the kind really is empty.
    /// </summary>
    private static bool IsPlaceholderRow(string engine, JsCapabilities capability, Func<JsCapabilities, bool> kind)
    {
        if (engine != NoProviderLacksOne)
            return false;

        Assert.Equal(JsCapabilities.None, capability);
        Assert.Empty(UndeclaredPairs(kind));
        return true;
    }

    /// <summary>
    /// Fails, rather than returning, when a realm lacks a capability its test row was chosen for.
    /// </summary>
    private static void AssertHas(IJsRealm realm, JsCapabilities required) =>
        Assert.True(
            (realm.Capabilities & required) == required,
            $"'{realm.EngineName}' declares {required}, but a realm built for this test lacks " +
            $"{required & ~realm.Capabilities}. A declared capability that a realm cannot honour is " +
            "an unsupported capability, and it must not pass as an implementation.");

    /// <summary>
    /// Which test witnesses each capability a provider may declare.
    /// </summary>
    /// <remarks>
    /// A capability is a claim a host is entitled to branch on without verifying it, so a suite that
    /// lets one go unexercised is letting a provider make a claim nothing checks. The meta-tests
    /// below read this table and resolve its names by reflection: a witness must exist, must be a
    /// theory, and must be driven by <see cref="EnginesDeclaring"/> for its own capability, so that it
    /// runs for every provider declaring the capability and for no other.
    /// </remarks>
    private static readonly IReadOnlyDictionary<JsCapabilities, string> CapabilityWitnesses =
        new Dictionary<JsCapabilities, string>
        {
            [JsCapabilities.HostScriptSource] = nameof(EvaluatingHostScriptAnswersTheValueOfTheLastExpression),
            [JsCapabilities.GuestEval] = nameof(ARealmBuiltWithoutGuestEvalRefusesDynamicSourceAndStillRunsHostScript),
            [JsCapabilities.ClassicScriptSource] = nameof(AClassicScriptRunsInARealmThatForbidsGuestEvaluation),
            [JsCapabilities.Promises] = nameof(APromiseSettlesFromTheHostAndItsReactionRunsAtTheNextDrain),
            [JsCapabilities.ExoticObjects] = nameof(AnOrdinaryPropertyWinsOverTheExoticHandler),
            [JsCapabilities.GlobalIsVariableScope] = nameof(ATopLevelDeclarationBecomesAPropertyOfTheGlobal),
            // Covers both halves of what the capability claims: the second realm on the second
            // thread, and values moved between the two by structured clone (IJsClone).
            [JsCapabilities.WorkerRealms] = nameof(ASecondRealmRunsOnASecondThread),
            [JsCapabilities.StructuredClone] = nameof(ACloneIsACopyRatherThanTheSameObject),
            [JsCapabilities.ReentrantHostCalls] = nameof(AHostFunctionMayCallBackIntoScriptWhileTheEngineIsInsideIt),
            [JsCapabilities.BinaryData] = nameof(AMintedArrayBufferIsTheRealmsOwnAndAViewOverItSeesTheBytes),
        };

    /// <summary>
    /// The capabilities no JSEAL contract can exercise yet, and why. A recorded gap, not an exemption.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="JsCapabilities.Modules"/> and <see cref="JsCapabilities.DynamicImport"/> describe
    /// binding an ES-module import end to end, and <see cref="IJsSource"/>, the only contract that
    /// takes source, has no module entry point: its three members run a script, which is a different
    /// thing from linking and evaluating a module graph. The optional <see cref="IJsModules"/>
    /// contract exists since I10 and its shared cases run through the providers' internal gate
    /// (<c>JsealConformanceTests.Modules.cs</c>); I13 turns them into these witnesses.
    /// </para>
    /// <para>
    /// <b>A provider that declares one of these fails the suite until then.</b> A declared capability
    /// with nothing to witness it is exactly the claim this table exists to catch, so the gap reason
    /// is reported in the failure instead of being accepted as coverage. A provider that does not
    /// declare them gets a row of <see cref="AnUndeclaredCapabilityWithNoContractIsARecordedGap"/>,
    /// never a refusal row: with no member to call there is nothing that could be refused.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<JsCapabilities, string> CoverageGaps =
        new Dictionary<JsCapabilities, string>
        {
            [JsCapabilities.Modules] =
                "IJsModules is reached only through the internal I10 gate; I13 publishes the flag with its witness",
            [JsCapabilities.DynamicImport] =
                "import() is routed through the module map by I12; I13 publishes the flag with its witness",
        };

    /// <summary>
    /// What a realm without each capability does when asked for it anyway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each entry calls a contract member that depends on the capability and expects
    /// <see cref="JsCapabilityUnavailableException"/> naming it, which is the contract's documented
    /// rule for an unsupported capability (<see cref="JsCapabilities"/>). Nothing here goes beyond
    /// that rule, and no entry needs another capability to make its call.
    /// </para>
    /// <para>
    /// <b>Most entries are specifications no registered provider exercises.</b> Both providers declare
    /// the Document bundle and GuestEval, so the only refusal a test run reaches today is WorkerRealms,
    /// from broiler-vm. The others are checked only for existence, by
    /// <see cref="EveryClaimOfCoverageNamesATheoryDrivenByItsOwnCapability"/>, and run only once a
    /// provider without the capability is registered; they are not evidence that any provider refuses
    /// correctly today.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<JsCapabilities, Action<IJsRealm>> CapabilityRefusals =
        new Dictionary<JsCapabilities, Action<IJsRealm>>
        {
            [JsCapabilities.HostScriptSource] = realm =>
                AssertRefused(realm, JsCapabilities.HostScriptSource, () => realm.EvaluateHostScript("1", "test:refused-host")),
            [JsCapabilities.ClassicScriptSource] = realm =>
                AssertRefused(realm, JsCapabilities.ClassicScriptSource, () => realm.EvaluateClassicScript("1", "test:refused-classic")),
            [JsCapabilities.GuestEval] = realm =>
                AssertRefused(realm, JsCapabilities.GuestEval, () => realm.EvaluateDynamicSource("1", "test:refused-dynamic")),
            // An engine that cannot hand a pending promise to the host has no deferred result for
            // fetch or whenDefined to return, and must say so rather than hand back something that
            // never settles.
            [JsCapabilities.Promises] = realm =>
                AssertRefused(realm, JsCapabilities.Promises, () => realm.NewPromise(out _, out _)),
            [JsCapabilities.ExoticObjects] = realm =>
                AssertRefused(realm, JsCapabilities.ExoticObjects, () => realm.NewExotic(new RecordingExotic())),
            [JsCapabilities.BinaryData] = realm =>
                AssertRefused(realm, JsCapabilities.BinaryData, () => realm.NewArrayBuffer([1])),
            [JsCapabilities.WorkerRealms] = RefusesWorkerRealms,
            [JsCapabilities.StructuredClone] = RefusesStructuredClone,
        };

    /// <summary>
    /// The capabilities that gate no contract member, so their absence has no specified refusal.
    /// </summary>
    /// <remarks>
    /// These describe how an engine runs, not an operation a host calls, and the contract states
    /// only what a provider that declares them does. A row for one asserts that the realm does not
    /// declare it and nothing more: inventing an observable absence (an exception type, a missing
    /// global property) would test behaviour no provider has promised. The bridge does depend on
    /// GlobalIsVariableScope - a nested browsing context recovers a frame's declarations by diffing the
    /// global's own names - which is why it is a declared flag, not why its absence has a shape.
    /// </remarks>
    private static readonly IReadOnlyDictionary<JsCapabilities, string> AbsenceOnly =
        new Dictionary<JsCapabilities, string>
        {
            [JsCapabilities.GlobalIsVariableScope] =
                "no contract member depends on it, and the contract does not say where an engine without it puts top-level declarations",
            [JsCapabilities.ReentrantHostCalls] =
                "no contract member depends on it, and the contract does not say how a re-entrant call fails on an engine without it",
        };

    private static void AssertRefused(IJsRealm realm, JsCapabilities capability, Action attempt)
    {
        var refusal = Assert.Throws<JsCapabilityUnavailableException>(attempt);
        Assert.Equal(capability, refusal.Missing);
        Assert.Equal(realm.EngineName, refusal.EngineName);
    }

    /// <summary>
    /// What a realm that does NOT declare WorkerRealms does when asked to clone anyway, and what a
    /// caller may conclude from the shape of the refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A refusal is <c>JsCapabilityUnavailableException</c>, and that type deliberately does not
    /// derive from <see cref="JsEngineException"/>. <c>JsErrors.cs</c> gives the reason: the second
    /// means the page's code went wrong, the first means the host's did, and "a host that branches
    /// on IJsRealm.Capabilities never sees it". The DOM bindings once called <c>Clone</c>,
    /// <c>Detach</c> and <c>Adopt</c> unguarded inside <c>catch (JsEngineException)</c> written to
    /// raise a <c>DataCloneError</c>; on a provider without the capability that catch could never
    /// fire. They branch now, and the last assertion pins the hierarchy that made the fix necessary.
    /// </para>
    /// <para>
    /// <b>Same-realm <see cref="IJsClone.Clone"/> is not gated on WorkerRealms since I18</b>; it is
    /// <see cref="JsCapabilities.StructuredClone"/>, refused by <see cref="RefusesStructuredClone"/>,
    /// the separate flag the J18 decision required once a provider had one half without the other.
    /// </para>
    /// </remarks>
    private static void RefusesWorkerRealms(IJsRealm realm)
    {
        var value = realm.NewObject();

        AssertRefused(realm, JsCapabilities.WorkerRealms, () => realm.Detach(value));
        AssertRefused(
            realm,
            JsCapabilities.WorkerRealms,
            () => realm.Adopt(JsProviderClone.Detached(realm.EngineName, null)));

        // Asked through reflection rather than as `refusal is JsEngineException`, which the compiler
        // would fold to a constant for two sealed unrelated types and which would then stop being a
        // question the moment someone changed the hierarchy - the exact change this is here to notice.
        Assert.False(
            typeof(JsEngineException).IsAssignableFrom(typeof(JsCapabilityUnavailableException)),
            "A capability refusal must not be catchable as JsEngineException: the bindings branch on "
            + "IJsRealm.Capabilities precisely because it is not, and a catch that absorbed it would "
            + "report a host bug to the page as though the page had caused it.");
    }

    /// <summary>
    /// What a realm that does NOT declare StructuredClone does when asked to clone anyway.
    /// </summary>
    private static void RefusesStructuredClone(IJsRealm realm)
    {
        var value = realm.NewObject();

        AssertRefused(realm, JsCapabilities.StructuredClone, () => realm.Clone(value));

        // ClassifyTransferable answers rather than refusing, because a host walking a transfer list
        // asks it about every entry before deciding to clone anything. A refusal there would refuse
        // the question rather than the operation.
        Assert.Equal(JsTransferKind.NotTransferable, realm.ClassifyTransferable(value));
    }

    /// <summary>
    /// A capability a provider does not declare is absent from its realms and refused explicitly.
    /// </summary>
    /// <remarks>
    /// One row per (provider, undeclared capability with a gated member), so a test report lists
    /// each unsupported capability by name. A pass here means "absent and refused as the contract
    /// requires", never "implemented": the capability's witness does not run for this provider at all.
    /// </remarks>
    [Theory]
    [MemberData(nameof(UndeclaredCapabilities))]
    public void AnUndeclaredCapabilityIsAnExplicitRefusalNotAPass(string engine, JsCapabilities capability)
    {
        if (IsPlaceholderRow(engine, capability, CapabilityRefusals.ContainsKey))
            return;

        using var realm = UndeclaredRealm(engine, capability);

        CapabilityRefusals[capability](realm);
    }

    /// <summary>
    /// A capability a provider does not declare and no contract member depends on is absent, which is
    /// all the contract lets a test say about it.
    /// </summary>
    /// <remarks>
    /// Named apart from the refusal rows so a report does not count an absence as a checked refusal.
    /// No registered provider produces a row today.
    /// </remarks>
    [Theory]
    [MemberData(nameof(UndeclaredCapabilitiesWithNoGatedMember))]
    public void AnUndeclaredCapabilityWithNoGatedMemberIsOnlyAbsent(string engine, JsCapabilities capability)
    {
        if (IsPlaceholderRow(engine, capability, AbsenceOnly.ContainsKey))
            return;

        using var realm = UndeclaredRealm(engine, capability);

        Assert.True(AbsenceOnly.ContainsKey(capability), $"{capability} has no recorded reason for an absence-only row.");
    }

    /// <summary>
    /// A capability no contract can reach yet: absent, and reported as a gap rather than a refusal.
    /// </summary>
    /// <remarks>
    /// <b>A pass here is not coverage of anything.</b> It asserts that the provider does not declare
    /// the capability and that the gap has a recorded reason; the capability itself is exercised by
    /// no test in this suite, for any provider. The theory is named for the gap so that a report lists
    /// these rows as recorded gaps, and <see cref="TheModulesWitnessIsNotExercisedUntilI13"/> and
    /// <see cref="TheDynamicImportWitnessIsNotExercisedUntilI13"/> are skipped so that the report's
    /// skip count shows the missing witnesses as well.
    /// </remarks>
    [Theory]
    [MemberData(nameof(UndeclaredGaps))]
    public void AnUndeclaredCapabilityWithNoContractIsARecordedGap(string engine, JsCapabilities capability)
    {
        if (IsPlaceholderRow(engine, capability, CoverageGaps.ContainsKey))
            return;

        using var realm = UndeclaredRealm(engine, capability);

        Assert.True(CoverageGaps.ContainsKey(capability), $"{capability} is not a recorded gap.");
    }

    /// <summary>The missing Modules witness, reported as not exercised. I13 replaces it.</summary>
    [Fact(Skip = "Not exercised: JSEAL has no module-graph contract; I09-I12 add it and I13 supplies the Modules witness.")]
    public void TheModulesWitnessIsNotExercisedUntilI13()
    {
    }

    /// <summary>The missing DynamicImport witness, reported as not exercised. I13 replaces it.</summary>
    [Fact(Skip = "Not exercised: JSEAL has no module-loader contract; I09-I12 add it and I13 supplies the DynamicImport witness.")]
    public void TheDynamicImportWitnessIsNotExercisedUntilI13()
    {
    }

    /// <summary>
    /// A default realm of a provider that does not declare <paramref name="capability"/>, after
    /// asserting that neither the provider nor the realm claims it.
    /// </summary>
    private static IJsRealm UndeclaredRealm(string engine, JsCapabilities capability)
    {
        var provider = Provider(engine);
        Assert.False(provider.Capabilities.HasFlag(capability));

        var realm = provider.CreateRealm(JsRealmOptions.Default);
        Assert.False(realm.Capabilities.HasFlag(capability), $"'{engine}' realm is wider than its provider: {capability}");
        return realm;
    }

    /// <summary>
    /// The one capability a witness may require besides its own: host script, through which most
    /// witnesses set up and read back what they check.
    /// </summary>
    private const JsCapabilities WitnessObserver = JsCapabilities.HostScriptSource;

    /// <summary>
    /// The capabilities a witness's rows are chosen by: its own, plus any it needs to observe it.
    /// </summary>
    /// <remarks>
    /// Read from the witness's <see cref="MemberDataAttribute"/> rather than restated, so the
    /// coverage checks below count exactly the rows xUnit will run.
    /// </remarks>
    private static JsCapabilities WitnessRequirement(JsCapabilities capability)
    {
        var data = typeof(JsealConformanceTests)
            .GetMethod(CapabilityWitnesses[capability], BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)?
            .GetCustomAttributes<MemberDataAttribute>(inherit: false)
            .SingleOrDefault(attribute => attribute.MemberName == nameof(EnginesDeclaring));

        // Not None, which EnginesDeclaring would answer with every provider: a witness with no
        // capability-driven rows witnesses nothing, and the check that asked has to fail.
        return data?.Parameters is [JsCapabilities required]
            ? required
            : throw new InvalidOperationException(
                $"{capability}'s witness '{CapabilityWitnesses[capability]}' takes no rows from {nameof(EnginesDeclaring)}.");
    }

    /// <summary>
    /// The declared capabilities with no witness, gaps included.
    /// </summary>
    internal static JsCapabilities[] UnwitnessedClaims(JsCapabilities declared) =>
        SingleCapabilities()
            .Where(capability => declared.HasFlag(capability))
            .Where(capability => !CapabilityWitnesses.ContainsKey(capability))
            .ToArray();

    [Theory]
    [MemberData(nameof(Engines))]
    public void EveryCapabilityAProviderDeclaresIsExercisedBySomeTest(string engine)
    {
        var unwitnessed = UnwitnessedClaims(Provider(engine).Capabilities);

        Assert.True(
            unwitnessed.Length == 0,
            $"'{engine}' declares {string.Join(", ", unwitnessed)}, which no test in this suite witnesses. " +
            string.Join(" ", unwitnessed.Select(capability => CoverageGaps.TryGetValue(capability, out var gap)
                ? $"{capability}: recorded gap - {gap}."
                : $"{capability}: add a witness and name it in {nameof(CapabilityWitnesses)}.")));
    }

    /// <summary>
    /// A recorded gap does not turn a declared claim into a witnessed one.
    /// </summary>
    /// <remarks>
    /// Asserted against a composed declaration rather than a registered provider, because the only
    /// provider that can declare Modules does so from a process-wide host callback, and setting it
    /// here would change every other test running in parallel.
    /// </remarks>
    [Fact]
    public void ADeclaredCapabilityWithOnlyARecordedGapIsAnUnwitnessedClaim()
    {
        Assert.Equal(
            new[] { JsCapabilities.Modules, JsCapabilities.DynamicImport },
            UnwitnessedClaims(JsCapabilities.Document | JsCapabilities.Modules | JsCapabilities.DynamicImport));

        Assert.Empty(UnwitnessedClaims(JsCapabilities.Document | JsCapabilities.GuestEval | JsCapabilities.WorkerRealms | JsCapabilities.StructuredClone));
    }

    [Fact]
    public void EveryClaimOfCoverageNamesATheoryDrivenByItsOwnCapability()
    {
        // The table is a claim about this class, so it is checked against this class rather than
        // believed. A witness renamed without updating the table, or one that ran for every provider
        // and returned early for those without the capability, would leave the capability reported
        // as covered by a test that did not exercise it.
        foreach (var (capability, testName) in CapabilityWitnesses)
        {
            var method = typeof(JsealConformanceTests).GetMethod(
                testName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.True(method is not null, $"{capability} names '{testName}', which is not a method of this class.");
            Assert.True(
                method!.GetCustomAttributes(typeof(TheoryAttribute), inherit: false).Length == 1,
                $"{capability} names '{testName}', which is not a [Theory] over the registered providers.");

            var data = method.GetCustomAttributes<MemberDataAttribute>(inherit: false).ToArray();
            Assert.True(
                data.Length == 1
                    && data[0].MemberName == nameof(EnginesDeclaring)
                    && data[0].Parameters is [JsCapabilities required]
                    && required.HasFlag(capability),
                $"{capability} names '{testName}', which must take its rows from " +
                $"{nameof(EnginesDeclaring)}({capability}) so that it runs for exactly the providers declaring it.");

            // The only other flag a witness may require is the observer every witness reads its result
            // through. Requiring anything else hides the capability from a provider that has it and lacks
            // the other flag: such a provider would get neither the witness nor a refusal row.
            Assert.True(
                (WitnessRequirement(capability) & ~capability & ~WitnessObserver) == JsCapabilities.None,
                $"{capability}'s witness '{testName}' also requires " +
                $"{WitnessRequirement(capability) & ~capability & ~WitnessObserver}; a provider declaring " +
                $"{capability} without it would be neither witnessed nor refused. Drive the witness by " +
                $"{capability} and run any half that needs another capability only where it is declared.");
        }

        // Every single capability is accounted for exactly once: witnessed, or a recorded gap.
        foreach (var capability in SingleCapabilities())
        {
            Assert.True(
                CapabilityWitnesses.ContainsKey(capability) ^ CoverageGaps.ContainsKey(capability),
                $"{capability} must be either witnessed or recorded as a gap, and not both.");
        }

        // An undeclared capability outside the gaps has a specified refusal, or a recorded reason why
        // the contract specifies none, and never both.
        foreach (var capability in SingleCapabilities().Where(capability => !CoverageGaps.ContainsKey(capability)))
        {
            Assert.True(
                CapabilityRefusals.ContainsKey(capability) ^ AbsenceOnly.ContainsKey(capability),
                $"{capability} must have either a refusal in {nameof(CapabilityRefusals)} or a reason in " +
                $"{nameof(AbsenceOnly)}, and not both.");
        }

        Assert.Empty(CapabilityRefusals.Keys.Where(CoverageGaps.ContainsKey));
        Assert.Empty(AbsenceOnly.Keys.Where(CoverageGaps.ContainsKey));
    }

    /// <summary>
    /// Every registered provider appears, for every capability, in exactly one of the witness rows,
    /// the refusal rows, the absence-only rows and the recorded-gap rows.
    /// </summary>
    /// <remarks>
    /// This is the property a test count cannot show. A provider dropped from a witness's data would
    /// leave the total lower but green; here it is named, with the capability it stopped exercising.
    /// </remarks>
    [Fact]
    public void EveryProviderIsWitnessedOrRefusedForEveryCapability()
    {
        var refusals = UndeclaredPairs(CapabilityRefusals.ContainsKey).ToHashSet();
        var absences = UndeclaredPairs(AbsenceOnly.ContainsKey).ToHashSet();
        var gaps = UndeclaredPairs(CoverageGaps.ContainsKey).ToHashSet();

        Assert.NotEmpty(JsEngineRegistry.All);

        // A recorded gap has nothing to refuse, so a refusal row for one would report "refused as
        // the contract requires" for a check that never ran.
        Assert.DoesNotContain(UndeclaredCapabilities, row => CoverageGaps.ContainsKey((JsCapabilities)row[1]));

        foreach (var provider in JsEngineRegistry.All)
        {
            foreach (var capability in SingleCapabilities())
            {
                var witnessed = CapabilityWitnesses.ContainsKey(capability)
                    && EnginesDeclaring(WitnessRequirement(capability)).Any(row => (string)row[0] == provider.Name);
                var refused = refusals.Contains((provider.Name, capability));
                var absent = absences.Contains((provider.Name, capability));
                var gap = gaps.Contains((provider.Name, capability));

                Assert.True(
                    new[] { witnessed, refused, absent, gap }.Count(kind => kind) == 1,
                    $"'{provider.Name}' / {capability}: witnessed={witnessed}, refused={refused}, " +
                    $"absent={absent}, gap={gap}. Each capability must be exercised by its witness or " +
                    "reported as exactly one kind of unsupported row.");
            }
        }
    }

    /// <summary>
    /// A realm built with default options has everything its provider declares.
    /// </summary>
    /// <remarks>
    /// <see cref="JsRealmOptions.Default"/> asks for nothing to be withheld, so the host narrows
    /// nothing here. A provider may still withhold a flag its realm turns out not to support (the VM
    /// provider checks its bridge for binary intrinsics and <c>eval</c>); that is a legitimate guard,
    /// but a default realm that actually hit it would mean the provider declares a capability its
    /// realms do not have, so it fails here instead of quietly narrowing every witness row.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void ADefaultRealmHasEveryCapabilityItsProviderDeclares(string engine)
    {
        var provider = Provider(engine);
        using var realm = provider.CreateRealm(JsRealmOptions.Default);

        Assert.Equal(provider.Capabilities, realm.Capabilities);
    }

    /// <summary>
    /// The numeric values are public contract; <c>Document</c> is a bundle with no witness of its own.
    /// </summary>
    /// <remarks>
    /// A renumbered flag would silently change what a persisted or cross-assembly value means, so
    /// each is pinned. <c>Document</c> is the baseline a document host needs, and declaring it
    /// establishes the seven flags it names, each through its own witness, and nothing else: not
    /// GuestEval, worker realms or modules, and not complete ECMAScript support.
    /// </remarks>
    [Fact]
    public void CapabilityValuesArePinnedAndDocumentIsOnlyItsBundle()
    {
        Assert.Equal(0u, (uint)JsCapabilities.None);
        Assert.Equal(1u << 0, (uint)JsCapabilities.HostScriptSource);
        Assert.Equal(1u << 1, (uint)JsCapabilities.GuestEval);
        Assert.Equal(1u << 2, (uint)JsCapabilities.Modules);
        Assert.Equal(1u << 3, (uint)JsCapabilities.DynamicImport);
        Assert.Equal(1u << 4, (uint)JsCapabilities.Promises);
        Assert.Equal(1u << 5, (uint)JsCapabilities.ExoticObjects);
        Assert.Equal(1u << 6, (uint)JsCapabilities.GlobalIsVariableScope);
        Assert.Equal(1u << 7, (uint)JsCapabilities.WorkerRealms);
        Assert.Equal(1u << 8, (uint)JsCapabilities.ReentrantHostCalls);
        Assert.Equal(1u << 9, (uint)JsCapabilities.BinaryData);
        Assert.Equal(1u << 10, (uint)JsCapabilities.ClassicScriptSource);
        Assert.Equal(1u << 11, (uint)JsCapabilities.StructuredClone);
        Assert.Equal(12, SingleCapabilities().Count());

        Assert.Equal(
            JsCapabilities.HostScriptSource | JsCapabilities.ClassicScriptSource | JsCapabilities.Promises |
            JsCapabilities.ExoticObjects | JsCapabilities.GlobalIsVariableScope |
            JsCapabilities.ReentrantHostCalls | JsCapabilities.BinaryData,
            JsCapabilities.Document);

        Assert.DoesNotContain(JsCapabilities.Document, SingleCapabilities());
        Assert.False(CapabilityWitnesses.ContainsKey(JsCapabilities.Document));
    }

#if BROILER_VM_JS
    /// <summary>VM configurations must discover and exercise both real providers, by name.</summary>
    /// <remarks>
    /// Jseal/VmProviderRegistration.cs registers both providers from a module initializer. This check
    /// detects a missing conditional reference or registration that would silently omit VM theories,
    /// and asserts identities rather than a count: the witness rows and the refusal rows each name
    /// both providers. Registry mutation tests use isolated state and do not change this registry.
    /// </remarks>
    [Fact]
    public void AVmBuildHasBothProvidersRegistered()
    {
        Assert.Equal(
            new[] { "broiler-js", "broiler-vm" },
            JsEngineRegistry.All.Select(provider => provider.Name).Order(StringComparer.Ordinal).ToArray());

        Assert.Contains(Engines, row => (string)row[0] == "broiler-js");
        Assert.Contains(Engines, row => (string)row[0] == "broiler-vm");

        // Both run the Document bundle's witnesses...
        foreach (var capability in SingleCapabilities().Where(capability => JsCapabilities.Document.HasFlag(capability)))
        {
            Assert.Contains(EnginesDeclaring(WitnessRequirement(capability)), row => (string)row[0] == "broiler-js");
            Assert.Contains(EnginesDeclaring(WitnessRequirement(capability)), row => (string)row[0] == "broiler-vm");
        }

        // ...and they part at worker realms: Broiler.JS witnesses it, Broiler.VM reports a refusal.
        var workerRows = EnginesDeclaring(WitnessRequirement(JsCapabilities.WorkerRealms)).ToArray();
        Assert.Contains(workerRows, row => (string)row[0] == "broiler-js");
        Assert.DoesNotContain(workerRows, row => (string)row[0] == "broiler-vm");
        Assert.Contains(
            UndeclaredCapabilities,
            row => (string)row[0] == "broiler-vm" && (JsCapabilities)row[1] == JsCapabilities.WorkerRealms);
    }
#else
    /// <summary>A Release build registers Broiler.JS alone, so no VM row can be mistaken for coverage.</summary>
    [Fact]
    public void AReleaseBuildHasOnlyTheBroilerJsProviderRegistered()
    {
        Assert.Equal(new[] { "broiler-js" }, JsEngineRegistry.All.Select(provider => provider.Name).ToArray());
    }
#endif

    /// <summary>Every capability that names one thing, so the composite <c>Document</c> is skipped.</summary>
    private static IEnumerable<JsCapabilities> SingleCapabilities() =>
        Enum.GetValues<JsCapabilities>().Where(value => BitOperations.PopCount((uint)value) == 1);
}

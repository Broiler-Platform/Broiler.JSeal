using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

using Broiler.JavaScript.Ast;
using Broiler.JavaScript.Ast.Expressions;
using Broiler.JavaScript.Ast.Misc;
using Broiler.JavaScript.Ast.Patterns;
using Broiler.JavaScript.Ast.Statements;
using Broiler.JavaScript.ExpressionCompiler.Core;
using Broiler.JavaScript.Parser;
using Broiler.JavaScript.Runtime;

namespace Broiler.JSeal.BroilerJs;

/// <summary>
/// What the module adapter needs to know about one module's text before the engine runs it: its
/// static requests, and whether it uses a feature the pinned engine would answer wrongly.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not linking.</b> The engine links nothing ahead of time: Broiler.JS 0.1.0-preview.1
/// compiles each <c>import</c> declaration into a call of the module's loader at the point the
/// declaration appears, and copies each imported name out of the exporter's exports object once, when
/// that call returns. What JSEAL reads here is what ECMAScript calls a module's requested modules,
/// which the contract needs before evaluation so that a missing module fails through
/// <see cref="IJsModuleMap.LoadAsync"/> rather than in the middle of an evaluation. Export names are
/// never resolved across modules, so a missing or ambiguous export is not detected (see
/// <c>docs/jseal.modules.md</c>).
/// </para>
/// <para>
/// <b>Refusals rather than plausible wrong answers.</b> Where the text uses something the pinned
/// engine gets observably wrong, and the text alone shows it, the adapter refuses the graph at link
/// time with the reason, instead of running it. Each refusal names the missing engine behavior and
/// is an upstream Broiler.JS follow-up, not something JSEAL fills in:
/// </para>
/// <list type="bullet">
/// <item>An exported <c>let</c>, <c>var</c>, function or class binding that the module ever assigns,
/// or that a direct <c>eval</c> could assign: imports are copies, not live bindings.</item>
/// <item>A static <c>import</c> or <c>export ... from</c> after any other statement: the engine runs
/// the dependency when the declaration is reached, not before the module body.</item>
/// <item>An <c>export { name }</c> list before <c>name</c>'s declaration (unless it is a function
/// declaration): the list copies the binding when it runs, so it would copy an uninitialized one.</item>
/// <item><c>import()</c>, which the pinned engine's loader cannot take from the map (I12 routes it for the VM provider only).</item>
/// <item>A reference to <c>module</c>, <c>exports</c>, <c>require</c>, <c>__dirname</c> or
/// <c>__fileame</c>, or any direct <c>eval</c>: the engine compiles module code as the body of a
/// CommonJS-style function with those parameters, so they would reach the engine's module object and
/// could replace or extend the namespace.</item>
/// <item><c>this</c> or <c>arguments</c> outside a non-arrow function or class body: they would be
/// that function's, where ECMAScript module code has <c>undefined</c> and no <c>arguments</c>.</item>
/// </list>
/// <para>
/// The map adds one more refusal across modules from <see cref="TopLevelVarNames"/> and
/// <see cref="FreeNames"/>: the engine binds a module's top-level <c>var</c> and function
/// declarations on the realm's global object (see <see cref="BroilerJsModuleMap"/>).
/// </para>
/// <para>
/// The checks are conservative and syntactic: an assignment to a same-named local in a nested
/// function also counts. A false refusal is explicit; a missed one would be a wrong answer.
/// </para>
/// </remarks>
internal sealed class BroilerJsModuleAnalysis
{
    /// <summary>
    /// The engine's internal parse-goal switches, reached by reflection because the pinned package
    /// keeps them internal. Without both, a standalone parse of module text would use the script
    /// goal and misread a top-level <c>await</c>. If either is missing the adapter refuses modules
    /// rather than guess.
    /// </summary>
    private static readonly MethodInfo? AllowTopLevelAwaitScope =
        typeof(CoreScript).GetMethod("AllowTopLevelAwaitScope", BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes);

    private static readonly MethodInfo? ModuleGoalScope =
        typeof(CoreScript).GetMethod("ModuleGoalScope", BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes);

    /// <summary>Whether this engine build exposes what module analysis needs.</summary>
    internal static bool IsAvailable =>
        AllowTopLevelAwaitScope?.ReturnType == typeof(IDisposable) && ModuleGoalScope?.ReturnType == typeof(IDisposable);

    /// <summary>Enters the parse state the engine's own module compilation uses.</summary>
    internal static IDisposable EnterTopLevelAwait() =>
        (IDisposable)AllowTopLevelAwaitScope!.Invoke(null, null)!;

    /// <summary>The parameters the engine compiles a module body with, which module code must not see.</summary>
    private static readonly string[] CommonJsParameters = ["module", "exports", "require", "__dirname", "__fileame"];

    private BroilerJsModuleAnalysis(
        List<StaticRequest> requests, string? refusal, bool hasTopLevelAwait,
        HashSet<string> topLevelVarNames, HashSet<string> freeNames)
    {
        Requests = requests;
        Refusal = refusal;
        HasTopLevelAwait = hasTopLevelAwait;
        TopLevelVarNames = topLevelVarNames;
        FreeNames = freeNames;
    }

    /// <summary>The module's static requests, in source order, one entry per specifier.</summary>
    internal IReadOnlyList<StaticRequest> Requests { get; }

    /// <summary>Why the pinned engine cannot run this module correctly, or null.</summary>
    internal string? Refusal { get; }

    /// <summary>Whether the module body awaits at top level.</summary>
    internal bool HasTopLevelAwait { get; }

    /// <summary>
    /// The module's var-scoped top-level names: every <c>var</c> outside a function, however deeply
    /// nested in blocks, loops or <c>try</c>, and every top-level function declaration. The pinned
    /// engine binds these on the realm's global object.
    /// </summary>
    internal IReadOnlySet<string> TopLevelVarNames { get; }

    /// <summary>
    /// Names the module reads or writes as identifiers and binds nowhere itself: no declaration,
    /// parameter, catch parameter or import of that name anywhere in the module. Only these can
    /// reach another module's top-level var through the global object.
    /// </summary>
    internal IReadOnlySet<string> FreeNames { get; }

    /// <summary>One static request: a specifier and its attributes.</summary>
    internal readonly record struct StaticRequest(string Specifier, IReadOnlyList<KeyValuePair<string, string>> Attributes);

    /// <summary>Parses <paramref name="text"/> with the module goal and analyzes it.</summary>
    /// <remarks>The engine has already compiled the text by the time this runs, so a parse failure here is not expected.</remarks>
    internal static BroilerJsModuleAnalysis Analyze(string text)
    {
        AstProgram program;
        using (EnterTopLevelAwait())
        using ((IDisposable)ModuleGoalScope!.Invoke(null, null)!)
        {
            StringSpan code = text;
            program = new FastParser(new FastTokenStream(in code, null)).ParseProgram();
        }

        var requests = new List<StaticRequest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var topLevelVarNames = new HashSet<string>(StringComparer.Ordinal);
        string? refusal = null;

        // name -> the top-level statement declaring it, and its kind. Imports count as const.
        var declaredAt = new Dictionary<string, (int Index, FastVariableKind Kind)>(StringComparer.Ordinal);
        var exportedBindings = new List<(string Name, FastVariableKind Kind)>();
        var exportLists = new List<(int Index, string Local)>();
        var otherStatementSeen = false;
        var index = 0;

        var statements = program.Statements.GetFastEnumerator();
        while (statements.MoveNext(out var statement))
        {
            switch (statement)
            {
                case AstImportStatement import:
                    AddRequest(import.Source.StringValue, import.Attributes);
                    if (import.Default is { } defaultBinding)
                        declaredAt[Text(defaultBinding.Name)] = (index, FastVariableKind.Const);
                    if (import.All is { } all)
                        declaredAt[Text(all.Name)] = (index, FastVariableKind.Const);
                    if (import.Members is { } members)
                    {
                        var member = members.GetFastEnumerator();
                        while (member.MoveNext(out var item))
                            declaredAt[Text(item.Item2)] = (index, FastVariableKind.Const);
                    }

                    break;

                case AstExportStatement export when export.Source is AstLiteral source:
                    AddRequest(source.StringValue, export.Attributes);
                    break;

                case AstExportStatement export when export.Members is { } exportList:
                    var local = exportList.GetFastEnumerator();
                    while (local.MoveNext(out var item))
                        exportLists.Add((index, Text(item.Item1)));

                    otherStatementSeen = true;
                    break;

                case AstExportStatement export:
                    if (export.Declaration is { } declaration)
                    {
                        foreach (var (name, kind) in Declared(declaration))
                        {
                            declaredAt[name] = (index, kind);
                            exportedBindings.Add((name, kind));
                        }
                    }

                    otherStatementSeen = true;
                    break;

                default:
                    foreach (var (name, kind) in Declared(statement))
                        declaredAt[name] = (index, kind);

                    otherStatementSeen = true;
                    break;
            }

            // A var nested in a block, loop, switch or try of this statement is a module-level binding too.
            foreach (var name in HoistedVars(statement))
            {
                declaredAt.TryAdd(name, (index, FastVariableKind.Var));
                topLevelVarNames.Add(name);
            }

            index++;
        }

        foreach (var (name, (_, kind)) in declaredAt)
        {
            if (kind is FastVariableKind.Var or FastVariableKind.Function)
                topLevelVarNames.Add(name);
        }

        var walk = new Walker();
        walk.Walk(program);

        foreach (var (at, name) in exportLists)
        {
            if (!declaredAt.TryGetValue(name, out var declared))
            {
                // Not a module-level binding JSEAL can see, so neither check below could run on it.
                refusal ??= $"'export {{ {name} }}' names no module-level declaration JSEAL's module analysis can see, so it cannot check that Broiler.JS would export a live binding";
                continue;
            }

            if (declared.Kind != FastVariableKind.Function && declared.Index > at)
                refusal ??= $"'export {{ {name} }}' precedes the declaration of '{name}', and Broiler.JS copies an exported binding when the export list runs";

            exportedBindings.Add((name, declared.Kind));
        }

        foreach (var (name, kind) in exportedBindings)
        {
            if (kind == FastVariableKind.Const)
                continue;

            if (walk.Assigned.Contains(name) || walk.DeclarationCount(name) > 1)
                refusal ??= $"the exported binding '{name}' is reassigned, and Broiler.JS imports are copies rather than live bindings";
            else if (walk.HasDirectEval)
                refusal ??= $"a direct eval could reassign the exported binding '{name}', and Broiler.JS imports are copies rather than live bindings";
        }

        if (walk.HasImportCall)
            refusal ??= "import() is not routed through the JSEAL module contract on the pinned Broiler.JS engine, whose loader answers only a module's static requests";

        foreach (var parameter in CommonJsParameters)
        {
            if (walk.References.Contains(parameter))
                refusal ??= $"the module names '{parameter}', and Broiler.JS passes module code the CommonJS parameter of that name, which ECMAScript module code does not have and through which it could replace the namespace";
        }

        if (walk.HasDirectEval)
            refusal ??= "a direct eval could reach the CommonJS parameters and module object Broiler.JS passes to module code";

        if (walk.OuterThisOrArguments is { } outer)
            refusal ??= $"the module reads '{outer}' outside a function, where Broiler.JS gives it the engine's module function's value rather than ECMAScript's undefined";

        var freeNames = new HashSet<string>(walk.References.Where(name => !walk.Bound.Contains(name) && !declaredAt.ContainsKey(name)), StringComparer.Ordinal);
        return new BroilerJsModuleAnalysis(requests, refusal, walk.HasTopLevelAwait, topLevelVarNames, freeNames);

        void AddRequest(string specifier, IFastEnumerable<(StringSpan key, AstLiteral value)>? attributes)
        {
            if (otherStatementSeen)
                refusal ??= $"the static import of '{specifier}' follows other statements, and Broiler.JS evaluates a dependency where its declaration appears rather than before the module body";

            var pairs = new List<KeyValuePair<string, string>>();
            if (attributes is not null)
            {
                var attribute = attributes.GetFastEnumerator();
                while (attribute.MoveNext(out var pair))
                    pairs.Add(new(Text(pair.key), pair.value.StringValue));
            }

            if (seen.Add(specifier))
                requests.Add(new StaticRequest(specifier, pairs));
            else if (pairs.Count > 0)
                requests.Add(new StaticRequest(specifier, pairs));
        }
    }

    private static string Text(StringSpan span) => span.Value ?? string.Empty;

    /// <summary>
    /// The names every <c>var</c> in <paramref name="node"/> binds, outside the functions and classes
    /// in it: the declarations that hoist to the enclosing (here, the module's) function scope.
    /// </summary>
    private static List<string> HoistedVars(AstNode node)
    {
        var names = new List<string>();
        var visited = new HashSet<AstNode>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        Visit(node);
        return names;

        void Visit(object? value)
        {
            switch (value)
            {
                case null or string or StringSpan or FastToken:
                    return;

                case AstFunctionExpression or AstClassExpression:
                    return;

                case AstNode child:
                    if (!visited.Add(child))
                        return;

                    if (child is AstVariableDeclaration { Kind: FastVariableKind.Var } declaration)
                    {
                        foreach (var (name, _) in Declared(declaration))
                            names.Add(name);
                    }

                    foreach (var field in Walker.FieldsOf(child.GetType()))
                        Visit(field.GetValue(child));
                    return;

                case IEnumerable items:
                    foreach (var item in items)
                        Visit(item);
                    return;

                default:
                    var type = value.GetType();
                    if (type.IsValueType && !type.IsPrimitive && !type.IsEnum)
                    {
                        foreach (var field in Walker.FieldsOf(type))
                            Visit(field.GetValue(value));
                    }

                    return;
            }
        }
    }

    /// <summary>The names a top-level declaration binds, with their kind (classes count as <c>let</c>).</summary>
    private static IEnumerable<(string Name, FastVariableKind Kind)> Declared(AstNode node)
    {
        switch (node)
        {
            case AstVariableDeclaration variables:
                var declarators = variables.Declarators.GetFastEnumerator();
                var names = new List<(string, FastVariableKind)>();
                while (declarators.MoveNext(out var declarator))
                {
                    foreach (var name in BoundNames(declarator.Identifier))
                        names.Add((name, variables.Kind));
                }

                return names;

            case AstExpressionStatement { Expression: var expression }:
                return Declared(expression);

            case AstFunctionExpression { Id: { } id }:
                return [(Text(id.Name), FastVariableKind.Function)];

            case AstClassExpression { Identifier: { } className }:
                return [(Text(className.Name), FastVariableKind.Let)];

            default:
                return [];
        }
    }

    /// <summary>
    /// Every identifier a binding or assignment target could write: identifiers and pattern leaves.
    /// Default values and computed keys are reads; a member expression writes a property, not a binding.
    /// </summary>
    private static IEnumerable<string> BoundNames(AstNode? target)
    {
        switch (target)
        {
            case null:
                yield break;

            case AstIdentifier identifier:
                yield return Text(identifier.Name);
                break;

            case AstArrayPattern array:
                var elements = array.Elements.GetFastEnumerator();
                while (elements.MoveNext(out var element))
                {
                    foreach (var name in BoundNames(element))
                        yield return name;
                }

                break;

            case AstArrayExpression arrayLiteral:
                var items = arrayLiteral.Elements.GetFastEnumerator();
                while (items.MoveNext(out var item))
                {
                    foreach (var name in BoundNames(item))
                        yield return name;
                }

                break;

            case AstObjectPattern obj:
                var properties = obj.Properties.GetFastEnumerator();
                while (properties.MoveNext(out var property))
                {
                    foreach (var name in BoundNames(property.Value ?? property.Key))
                        yield return name;
                }

                break;

            case AstBinaryExpression { Operator: TokenTypes.Assign } withDefault:
                foreach (var name in BoundNames(withDefault.Left))
                    yield return name;
                break;

            case AstSpreadElement spread:
                foreach (var name in BoundNames(spread.Argument))
                    yield return name;
                break;

            case AstMemberExpression:
                break;

            default:
                // An unrecognized target shape: count every identifier in it, which can only refuse more.
                var collector = new Walker();
                collector.Walk(target);
                foreach (var name in collector.Identifiers)
                    yield return name;
                break;
        }
    }

    /// <summary>
    /// The whole-tree facts: assigned names, declaration counts, referenced and bound names, import(),
    /// eval, <c>this</c> and <c>arguments</c> outside functions, and top-level await.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The walk visits every public field of every node by reflection rather than through the
    /// engine's <c>AstReduce</c>, which does not descend into variable initializers, object literal
    /// members, parameter defaults or switch cases. A check that could miss a node would turn a
    /// refusal back into a wrong answer, so completeness is taken from the node types themselves.
    /// </para>
    /// <para>
    /// The only children it skips are names that are never references: a non-computed member
    /// property, a non-computed property key that is not shorthand, a label, and <c>import.meta</c>
    /// or <c>new.target</c>. Binding positions count as references too, which can only refuse more.
    /// </para>
    /// </remarks>
    private sealed class Walker
    {
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> ChildFields = new();

        private readonly HashSet<AstNode> _visited = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int> _declarations = new(StringComparer.Ordinal);
        private int _functionDepth;

        // Non-arrow functions and class bodies: where this and arguments are no longer the module's.
        private int _thisDepth;

        internal HashSet<string> Assigned { get; } = new(StringComparer.Ordinal);
        internal List<string> Identifiers { get; } = [];
        internal HashSet<string> References { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Bound { get; } = new(StringComparer.Ordinal);
        internal bool HasImportCall { get; private set; }
        internal bool HasDirectEval { get; private set; }
        internal bool HasTopLevelAwait { get; private set; }
        internal string? OuterThisOrArguments { get; private set; }

        internal int DeclarationCount(string name) => _declarations.GetValueOrDefault(name);

        internal void Walk(object? value)
        {
            switch (value)
            {
                case null or string or StringSpan or FastToken:
                    return;

                case AstNode node:
                    if (!_visited.Add(node))
                        return;

                    Inspect(node);
                    if (WalkNamePositions(node))
                        return;

                    var function = node is AstFunctionExpression;
                    var ownThis = node is AstFunctionExpression { IsArrowFunction: false };
                    if (function)
                        _functionDepth++;
                    if (ownThis)
                        _thisDepth++;

                    try
                    {
                        foreach (var field in FieldsOf(node.GetType()))
                            Walk(field.GetValue(node));
                    }
                    finally
                    {
                        if (function)
                            _functionDepth--;
                        if (ownThis)
                            _thisDepth--;
                    }

                    return;

                case ObjectProperty property:
                    // A pattern property: the key is a reference only when computed or shorthand.
                    if (property.Computed || property.Value is null)
                        Walk(property.Key);
                    Walk(property.Value);
                    Walk(property.Init);
                    return;

                case IEnumerable items:
                    foreach (var item in items)
                        Walk(item);
                    return;

                default:
                    // Declarators, object properties, switch cases and tuples carry nodes in fields.
                    var type = value.GetType();
                    if (type.IsValueType && !type.IsPrimitive && !type.IsEnum)
                    {
                        foreach (var field in FieldsOf(type))
                            Walk(field.GetValue(value));
                    }

                    return;
            }
        }

        /// <summary>
        /// Walks the nodes whose children include names that are not references, or whose body has
        /// its own <c>this</c>. Returns false for every other node, which is walked field by field.
        /// </summary>
        private bool WalkNamePositions(AstNode node)
        {
            switch (node)
            {
                case AstMemberExpression { Computed: false } member:
                    Walk(member.Object);
                    return true;

                case AstMeta or AstBreakStatement or AstContinueStatement:
                    return true;

                case AstObjectLiteral literal:
                    var properties = literal.Properties.GetFastEnumerator();
                    while (properties.MoveNext(out var property))
                        WalkProperty(property, shorthandIsReference: true);
                    return true;

                case AstClassExpression classNode:
                    Walk(classNode.Identifier);
                    Walk(classNode.Base);
                    var members = classNode.Members.GetFastEnumerator();
                    while (members.MoveNext(out var member))
                    {
                        // A computed key is evaluated in the enclosing scope; the rest has the instance's this.
                        if (member is AstClassProperty { Computed: true } computed)
                            Walk(computed.Key);

                        _thisDepth++;
                        try
                        {
                            if (member is AstClassProperty classProperty)
                                Walk(classProperty.Init);
                            else
                                Walk(member);
                        }
                        finally
                        {
                            _thisDepth--;
                        }
                    }

                    return true;

                default:
                    return false;
            }
        }

        private void WalkProperty(object? property, bool shorthandIsReference)
        {
            if (property is not AstClassProperty classProperty || !_visited.Add(classProperty))
            {
                Walk(property);
                return;
            }

            if (classProperty.Computed || (shorthandIsReference && classProperty.Init is null))
                Walk(classProperty.Key);
            Walk(classProperty.Init);
        }

        internal static FieldInfo[] FieldsOf(Type type) =>
            ChildFields.GetOrAdd(type, static type => type
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(field => !(field.FieldType.IsPrimitive || field.FieldType.IsEnum
                    || field.FieldType == typeof(string) || field.FieldType == typeof(StringSpan)
                    || field.FieldType == typeof(FastToken)))
                .ToArray());

        /// <summary>The names a node binds anywhere in the module, beyond the declarations <see cref="Declare"/> counts.</summary>
        private void CollectBindings(AstNode node)
        {
            switch (node)
            {
                case AstFunctionExpression function:
                    if (function.Id is { } functionId)
                        Bound.Add(Text(functionId.Name));
                    var parameters = function.Params.GetFastEnumerator();
                    while (parameters.MoveNext(out var parameter))
                        Bound.UnionWith(BoundNames(parameter.Identifier));
                    break;

                case AstClassExpression { Identifier: { } className }:
                    Bound.Add(Text(className.Name));
                    break;

                case AstTryStatement { CatchParam: { } catchParameter }:
                    Bound.UnionWith(BoundNames(catchParameter));
                    break;

                case AstImportStatement import:
                    if (import.Default is { } importedDefault)
                        Bound.Add(Text(importedDefault.Name));
                    if (import.All is { } importedAll)
                        Bound.Add(Text(importedAll.Name));
                    if (import.Members is { } importedMembers)
                    {
                        var importedMember = importedMembers.GetFastEnumerator();
                        while (importedMember.MoveNext(out var item))
                            Bound.Add(Text(item.Item2));
                    }

                    break;
            }
        }

        private void Declare(string name)
        {
            _declarations[name] = _declarations.GetValueOrDefault(name) + 1;
            Bound.Add(name);
        }

        private void Inspect(AstNode node)
        {
            CollectBindings(node);

            switch (node)
            {
                case AstIdentifier identifier:
                    var referenced = Text(identifier.Name);
                    Identifiers.Add(referenced);
                    References.Add(referenced);
                    if (_thisDepth == 0 && referenced is "this" or "arguments")
                        OuterThisOrArguments ??= referenced;
                    break;


                case AstBinaryExpression binary when TokenTypesExtensions.IsAssignmentOperator(binary.Operator):
                    Assigned.UnionWith(BoundNames(binary.Left));
                    break;

                case AstUnaryExpression { Operator: UnaryOperator.Increment or UnaryOperator.Decrement } update:
                    Assigned.UnionWith(BoundNames(update.Argument));
                    break;

                case AstForInStatement { Init: AstExpression target }:
                    Assigned.UnionWith(BoundNames(target));
                    break;

                case AstForOfStatement forOf:
                    if (forOf.Init is AstExpression ofTarget)
                        Assigned.UnionWith(BoundNames(ofTarget));
                    if (forOf.IsAwait && _functionDepth == 0)
                        HasTopLevelAwait = true;
                    break;

                case AstVariableDeclaration:
                    foreach (var (name, _) in Declared(node))
                        Declare(name);
                    break;

                // A function or class declaration, and an exported one, which the parser does not
                // mark as a statement. Methods and function expressions are not declarations here.
                case AstFunctionExpression { IsStatement: true, Id: { } id }:
                    Declare(Text(id.Name));
                    break;

                case AstClassExpression { IsDeclaration: true, Identifier: { } className }:
                    Declare(Text(className.Name));
                    break;

                case AstExportStatement { Declaration: AstFunctionExpression { IsStatement: false, Id: { } exported } }:
                    Declare(Text(exported.Name));
                    break;

                case AstExportStatement { Declaration: AstClassExpression { IsDeclaration: false, Identifier: { } exportedClass } }:
                    Declare(Text(exportedClass.Name));
                    break;

                case AstAwaitExpression when _functionDepth == 0:
                    HasTopLevelAwait = true;
                    break;

                case AstCallExpression { Callee: AstIdentifier { Name: var callee } } when Text(callee) == "eval":
                    HasDirectEval = true;
                    break;

                case AstImportCall:
                    HasImportCall = true;
                    break;
            }
        }
    }
}
